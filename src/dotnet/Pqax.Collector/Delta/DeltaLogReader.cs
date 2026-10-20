using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// Reads the Delta log of one table: one listing of <c>_delta_log</c>, <c>_last_checkpoint</c>, the checkpoint through
/// the <see cref="ICheckpointReader"/>, the commits after it, and older commits for the history up to a limit. It
/// replays the actions into the state of the last version and drops every data value on the way.
/// </summary>
internal sealed class DeltaLogReader
{
    /// <summary>The largest checkpoint that the collector reads, all parts together.</summary>
    public const long MaxCheckpointBytes = 512L * 1024 * 1024;

    private const string NoReader = "The table has a checkpoint in Parquet, and the collection has no checkpoint reader.";

    private static readonly Regex CommitFile = new Regex(@"^(\d{20})\.json$", RegexOptions.CultureInvariant);
    private static readonly Regex ClassicCheckpoint = new Regex(@"^(\d{20})\.checkpoint\.parquet$", RegexOptions.CultureInvariant);
    private static readonly Regex MultipartCheckpoint = new Regex(@"^(\d{20})\.checkpoint\.(\d{10})\.(\d{10})\.parquet$", RegexOptions.CultureInvariant);
    private static readonly Regex V2Checkpoint = new Regex(@"^(\d{20})\.checkpoint\.([0-9a-fA-F-]{36})\.(json|parquet)$", RegexOptions.CultureInvariant);

    private readonly IStorage storage;
    private readonly ICheckpointReader? checkpoints;
    private readonly int maxCommits;
    private readonly bool includeParameters;

    public DeltaLogReader(IStorage storage, ICheckpointReader? checkpoints, int maxCommits, bool includeParameters)
    {
        this.storage = storage;
        this.checkpoints = checkpoints;
        this.maxCommits = maxCommits;
        this.includeParameters = includeParameters;
    }

    public async Task<DeltaLog> ReadAsync(string tablePath, IssueList issues, CancellationToken cancellation)
    {
        string logPath = tablePath.TrimEnd('/') + "/_delta_log";
        var log = new DeltaLog();
        IReadOnlyList<StorageEntry> entries = await storage.ListAsync(logPath, cancellation).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            issues.Add(IssueCodes.DeltaLogMissing, "The table folder has no Delta log, or the log is empty.");
            return log;
        }

        log.Found = true;
        var listing = new LogListing();
        long? logSize = 0;
        foreach (StorageEntry entry in entries)
        {
            if (entry.IsDirectory)
            {
                if (entry.Name == "_sidecars")
                {
                    listing.HasSidecarFolder = true;
                }

                continue;
            }

            log.MetadataFileCount++;
            logSize = entry.Size == null || logSize == null ? null : logSize + entry.Size.Value;
            listing.Classify(entry);
        }

        if (listing.HasSidecarFolder)
        {
            foreach (StorageEntry sidecar in await storage.ListAsync(logPath + "/_sidecars", cancellation).ConfigureAwait(false))
            {
                if (!sidecar.IsDirectory)
                {
                    log.MetadataFileCount++;
                    logSize = sidecar.Size == null || logSize == null ? null : logSize + sidecar.Size.Value;
                }
            }
        }

        log.MetadataSize = logSize;
        log.CommitCount = listing.Commits.Count;

        long? hinted = await ReadLastCheckpointAsync(listing.LastCheckpoint, cancellation).ConfigureAwait(false);
        Checkpoint? checkpoint = listing.Newest();
        if (hinted != null && (checkpoint == null || hinted.Value > checkpoint.Version))
        {
            issues.Add(IssueCodes.DeltaCheckpointMissing, "_last_checkpoint names version " + Text(hinted.Value) + ", whose checkpoint the log does not have.");
        }

        var state = new DeltaState();
        bool complete = true;
        long replayFrom = 0;
        if (checkpoint != null)
        {
            log.CheckpointVersion = checkpoint.Version;
            log.CommitsAfterCheckpoint = listing.Commits.Keys.Count(v => v > checkpoint.Version);
            string? failure = await LoadCheckpointAsync(logPath, checkpoint, state, cancellation).ConfigureAwait(false);
            replayFrom = checkpoint.Version + 1;
            if (failure != null)
            {
                if (HasAllCommits(listing, checkpoint.Version))
                {
                    // Every commit up to the checkpoint is still there: the JSON commits give the same state. A checkpoint
                    // that failed is still worth an issue; one that was skipped for want of a reader is not.
                    if (failure != NoReader)
                    {
                        issues.Add(IssueCodes.DeltaCheckpointNotRead, failure + " The state comes from the JSON commits, which the log still has from version 0.");
                    }

                    state = new DeltaState();
                    replayFrom = 0;
                }
                else
                {
                    issues.Add(IssueCodes.DeltaCheckpointNotRead, failure + " The files of the table are unknown; the summary holds what the JSON commits say.");
                    complete = false;
                }
            }
        }
        else if (listing.Commits.Count == 0)
        {
            issues.Add(IssueCodes.DeltaLogIncomplete, "The log has no commit and no checkpoint.");
            complete = false;
        }
        else if (listing.Commits.Keys.First() != 0)
        {
            issues.Add(IssueCodes.DeltaLogIncomplete, "The log has no checkpoint, and its oldest commit is version " + Text(listing.Commits.Keys.First()) + ".");
            complete = false;
        }

        // Replay the commits that the state needs, in order: every one is read, whatever the limit. They are the commits
        // after the checkpoint, or all of them when the state comes from the JSON commits alone.
        var history = new List<CommitRecord>();
        int filesRead = 0;
        long expected = replayFrom;
        foreach (KeyValuePair<long, StorageEntry> commit in listing.Commits)
        {
            if (commit.Key < replayFrom)
            {
                continue;
            }

            if (commit.Key != expected && complete)
            {
                issues.Add(IssueCodes.DeltaLogIncomplete, "Commits " + Text(expected) + " to " + Text(commit.Key - 1) + " are missing from the log.");
                complete = false;
            }

            expected = commit.Key + 1;
            filesRead++;
            List<DeltaAction>? actions = await ReadCommitAsync(commit.Value, issues, cancellation).ConfigureAwait(false);
            if (actions == null)
            {
                complete = false;
                continue;
            }

            CommitInfoAction? info = null;
            foreach (DeltaAction action in actions)
            {
                if (action is CommitInfoAction commitInfo)
                {
                    info ??= commitInfo;
                }
                else
                {
                    state.Apply(action);
                }
            }

            history.Add(new CommitRecord(commit.Key, info, commit.Value.LastModified));
        }

        // Then older commits for the history only, the most recent first, until the commit files read reach the limit or
        // the log has no older one. A file whose read fails counts too, since it cost a request; it has an issue instead
        // of a place in the history.
        if (filesRead < maxCommits)
        {
            foreach (KeyValuePair<long, StorageEntry> commit in listing.Commits.Reverse())
            {
                if (filesRead >= maxCommits)
                {
                    break;
                }

                if (commit.Key >= replayFrom)
                {
                    continue;
                }

                filesRead++;
                List<DeltaAction>? actions = await ReadCommitAsync(commit.Value, issues, cancellation).ConfigureAwait(false);
                if (actions != null)
                {
                    history.Add(new CommitRecord(commit.Key, actions.OfType<CommitInfoAction>().FirstOrDefault(), commit.Value.LastModified));
                }
            }
        }

        // Every commit read stays in the history, also when the state needed more commits than the limit.
        history.Sort((a, b) => a.Version.CompareTo(b.Version));
        log.History.AddRange(history);

        state.Finish(log);
        if (state.StatsErrors > 0)
        {
            issues.Add(IssueCodes.DeltaStatsNotParsed, "The statistics of " + Text(state.StatsErrors) + " files are not valid JSON; their rows come from the footers when those are read.");
        }

        long? latest = listing.Commits.Count > 0 ? listing.Commits.Keys.Last() : (long?)null;
        log.Version = latest == null ? checkpoint?.Version : checkpoint == null ? latest : Math.Max(latest.Value, checkpoint.Version);
        if (log.Version != null)
        {
            CommitRecord? last = history.LastOrDefault(c => c.Version == log.Version.Value);
            log.SnapshotTimestamp = last?.Timestamp
                ?? (listing.Commits.TryGetValue(log.Version.Value, out StorageEntry? file) ? file.LastModified : null);
        }

        log.FilesComplete = complete;
        return log;
    }

    private static bool HasAllCommits(LogListing listing, long upTo)
    {
        if (listing.Commits.Count <= upTo)
        {
            return false;
        }

        for (long version = 0; version <= upTo; version++)
        {
            if (!listing.Commits.ContainsKey(version))
            {
                return false;
            }
        }

        return true;
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Describe(Exception e) => e.GetType().Name + ": " + e.Message;

    private async Task<long?> ReadLastCheckpointAsync(StorageEntry? entry, CancellationToken cancellation)
    {
        if (entry == null)
        {
            return null;
        }

        try
        {
            byte[] content = entry.Size != null
                ? await storage.ReadAllAsync(entry.Path, entry.Size.Value, cancellation).ConfigureAwait(false)
                : await storage.ReadAllAsync(entry.Path, cancellation).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out JsonElement version)
                && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt64(out long value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // The file is only a hint: the listing has the checkpoints.
        }
        catch (StorageException)
        {
        }

        return null;
    }

    /// <summary>Reads the checkpoint into the state.</summary>
    /// <returns>Null when it was read, otherwise why not.</returns>
    private async Task<string?> LoadCheckpointAsync(string logPath, Checkpoint checkpoint, DeltaState state, CancellationToken cancellation)
    {
        long size = 0;
        foreach (StorageEntry part in checkpoint.Parts)
        {
            size += part.Size ?? 0;
        }

        if (size > MaxCheckpointBytes)
        {
            return "The checkpoint takes " + Text(size) + " bytes, more than the limit of " + Text(MaxCheckpointBytes) + ".";
        }

        bool json = checkpoint.Parts.Count == 1 && checkpoint.Parts[0].Name.EndsWith(".json", StringComparison.Ordinal);
        if (!json && checkpoints == null)
        {
            return NoReader;
        }

        try
        {
            foreach (StorageEntry part in checkpoint.Parts)
            {
                IReadOnlyList<DeltaAction> actions;
                if (json)
                {
                    byte[] content = part.Size != null
                        ? await storage.ReadAllAsync(part.Path, part.Size.Value, cancellation).ConfigureAwait(false)
                        : await storage.ReadAllAsync(part.Path, cancellation).ConfigureAwait(false);
                    actions = DeltaJsonParser.Parse(content, false);
                }
                else
                {
                    actions = await checkpoints!.ReadAsync(storage, part.Path, cancellation).ConfigureAwait(false);
                }

                foreach (DeltaAction action in actions)
                {
                    state.Apply(action);
                }
            }

            // A v2 checkpoint can keep its file actions in sidecar files, which are always Parquet.
            foreach (SidecarAction sidecar in state.Sidecars)
            {
                if (checkpoints == null)
                {
                    return "The v2 checkpoint keeps its files in sidecar files, and the collection has no checkpoint reader.";
                }

                string name = Uri.UnescapeDataString(sidecar.Path!);
                int slash = name.LastIndexOf('/');
                string path = logPath + "/_sidecars/" + (slash >= 0 ? name.Substring(slash + 1) : name);
                foreach (DeltaAction action in await checkpoints.ReadAsync(storage, path, cancellation).ConfigureAwait(false))
                {
                    state.Apply(action);
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            string reason = "The checkpoint could not be read: " + Describe(e);
            return reason.EndsWith(".", StringComparison.Ordinal) ? reason : reason + ".";
        }
    }

    private async Task<List<DeltaAction>?> ReadCommitAsync(StorageEntry entry, IssueList issues, CancellationToken cancellation)
    {
        try
        {
            byte[] content = entry.Size != null
                ? await storage.ReadAllAsync(entry.Path, entry.Size.Value, cancellation).ConfigureAwait(false)
                : await storage.ReadAllAsync(entry.Path, cancellation).ConfigureAwait(false);
            return DeltaJsonParser.Parse(content, includeParameters);
        }
        catch (JsonException e)
        {
            issues.Add(IssueCodes.DeltaCommitNotRead, "A commit is not valid JSON: " + e.Message, "_delta_log/" + entry.Name);
            return null;
        }
        catch (StorageException e)
        {
            issues.Add(e.IsAccessDenied ? IssueCodes.AccessDenied : IssueCodes.DeltaCommitNotRead, "A commit could not be read: " + e.Message, "_delta_log/" + entry.Name);
            return null;
        }
    }

    /// <summary>A checkpoint of the log: its version and its files.</summary>
    private sealed class Checkpoint
    {
        public Checkpoint(long version, List<StorageEntry> parts)
        {
            Version = version;
            Parts = parts;
        }

        public long Version { get; }

        public List<StorageEntry> Parts { get; }
    }

    /// <summary>The files of <c>_delta_log</c> by kind.</summary>
    private sealed class LogListing
    {
        public SortedDictionary<long, StorageEntry> Commits { get; } = new SortedDictionary<long, StorageEntry>();

        public StorageEntry? LastCheckpoint { get; private set; }

        public bool HasSidecarFolder { get; set; }

        private Dictionary<long, StorageEntry> Classic { get; } = new Dictionary<long, StorageEntry>();

        private Dictionary<long, StorageEntry> Uuid { get; } = new Dictionary<long, StorageEntry>();

        private Dictionary<string, SortedDictionary<int, StorageEntry>> Multipart { get; } = new Dictionary<string, SortedDictionary<int, StorageEntry>>(StringComparer.Ordinal);

        public void Classify(StorageEntry entry)
        {
            string name = entry.Name;
            Match match;
            if ((match = CommitFile.Match(name)).Success)
            {
                Commits[Parse(match.Groups[1].Value)] = entry;
            }
            else if (name == "_last_checkpoint")
            {
                LastCheckpoint = entry;
            }
            else if ((match = ClassicCheckpoint.Match(name)).Success)
            {
                Classic[Parse(match.Groups[1].Value)] = entry;
            }
            else if ((match = MultipartCheckpoint.Match(name)).Success)
            {
                string key = match.Groups[1].Value + "/" + match.Groups[3].Value;
                if (!Multipart.TryGetValue(key, out SortedDictionary<int, StorageEntry>? parts))
                {
                    Multipart[key] = parts = new SortedDictionary<int, StorageEntry>();
                }

                parts[int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)] = entry;
            }
            else if ((match = V2Checkpoint.Match(name)).Success)
            {
                long version = Parse(match.Groups[1].Value);
                if (!Uuid.TryGetValue(version, out StorageEntry? existing) || string.CompareOrdinal(name, existing.Name) < 0)
                {
                    Uuid[version] = entry;
                }
            }
        }

        /// <summary>The newest complete checkpoint: a multi-part checkpoint counts when all its parts are there.</summary>
        public Checkpoint? Newest()
        {
            Checkpoint? best = null;
            foreach (KeyValuePair<long, StorageEntry> classic in Classic)
            {
                best = Better(best, new Checkpoint(classic.Key, new List<StorageEntry> { classic.Value }));
            }

            foreach (KeyValuePair<long, StorageEntry> uuid in Uuid)
            {
                best = Better(best, new Checkpoint(uuid.Key, new List<StorageEntry> { uuid.Value }));
            }

            foreach (KeyValuePair<string, SortedDictionary<int, StorageEntry>> multipart in Multipart)
            {
                int slash = multipart.Key.IndexOf('/');
                long version = Parse(multipart.Key.Substring(0, slash));
                int count = int.Parse(multipart.Key.Substring(slash + 1), CultureInfo.InvariantCulture);
                bool whole = multipart.Value.Count == count && multipart.Value.Keys.First() == 1 && multipart.Value.Keys.Last() == count;
                if (whole)
                {
                    best = Better(best, new Checkpoint(version, multipart.Value.Values.ToList()));
                }
            }

            return best;
        }

        private static Checkpoint Better(Checkpoint? best, Checkpoint candidate) =>
            best == null || candidate.Version > best.Version ? candidate : best;

        private static long Parse(string digits) => long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
    }
}
