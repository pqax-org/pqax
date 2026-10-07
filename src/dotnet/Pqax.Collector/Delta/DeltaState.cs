using System;
using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>
/// The state of a Delta table while its log is replayed: the live files by their key of the Delta protocol, the path
/// and the deletion vector, the tombstones, the metadata, the protocol, and the domains. The latest action wins.
/// </summary>
internal sealed class DeltaState
{
    private readonly Dictionary<string, LiveFile> live = new Dictionary<string, LiveFile>(StringComparer.Ordinal);
    private readonly Dictionary<string, long?> removed = new Dictionary<string, long?>(StringComparer.Ordinal);

    public MetadataAction? Metadata { get; private set; }

    public ProtocolAction? Protocol { get; private set; }

    public Dictionary<string, string> Domains { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The sidecar files that a v2 checkpoint names.</summary>
    public List<SidecarAction> Sidecars { get; } = new List<SidecarAction>();

    /// <summary>The files whose statistics are not valid JSON.</summary>
    public int StatsErrors { get; private set; }

    public void Apply(DeltaAction action)
    {
        switch (action)
        {
            case AddAction add:
                ApplyAdd(add);
                break;
            case RemoveAction remove:
                ApplyRemove(remove);
                break;
            case MetadataAction metadata:
                Metadata = metadata;
                break;
            case ProtocolAction protocol:
                Protocol = protocol;
                break;
            case DomainMetadataAction domain when !string.IsNullOrEmpty(domain.Domain):
                if (domain.Removed)
                {
                    Domains.Remove(domain.Domain!);
                }
                else
                {
                    Domains[domain.Domain!] = domain.Configuration ?? string.Empty;
                }

                break;
            case SidecarAction sidecar when !string.IsNullOrEmpty(sidecar.Path):
                Sidecars.Add(sidecar);
                break;
        }
    }

    /// <summary>Writes the files and the tombstones into the log, the files sorted by modification time and path.</summary>
    public void Finish(DeltaLog log)
    {
        var files = new List<LiveFile>(live.Values);
        files.Sort((a, b) =>
        {
            int byTime = (a.ModificationTime ?? long.MinValue).CompareTo(b.ModificationTime ?? long.MinValue);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.RawPath, b.RawPath);
        });
        log.Files.AddRange(files);

        var livePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (LiveFile file in files)
        {
            livePaths.Add(file.Path);
        }

        long count = 0;
        long size = 0;
        bool sizeKnown = true;
        foreach (KeyValuePair<string, long?> tombstone in removed)
        {
            // A file removed with one deletion vector and added back with another is still live.
            if (livePaths.Contains(tombstone.Key))
            {
                continue;
            }

            count++;
            if (tombstone.Value == null)
            {
                sizeKnown = false;
            }
            else
            {
                size += tombstone.Value.Value;
            }
        }

        log.NonLiveFileCount = count;
        log.NonLiveFileSize = sizeKnown ? size : (long?)null;
        log.Metadata = Metadata;
        log.Protocol = Protocol;
        foreach (KeyValuePair<string, string> domain in Domains)
        {
            log.Domains[domain.Key] = domain.Value;
        }
    }

    private static string Decode(string path)
    {
        try
        {
            return Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return path;
        }
    }

    private static string Key(string path, DeletionVectorDescriptor? vector) =>
        vector == null ? path + "\u0000" : path + "\u0000\u0001" + vector.UniqueId;

    private void ApplyAdd(AddAction add)
    {
        if (string.IsNullOrEmpty(add.Path))
        {
            return;
        }

        string path = Decode(add.Path!);
        DeletionVectorDescriptor? vector = add.DeletionVector;
        var file = new LiveFile(add.Path!, path)
        {
            Size = add.Size,
            ModificationTime = add.ModificationTime,
            PartitionValues = add.PartitionValues,
            VOrderTag = VOrder(add.Tags),
            DeletedRowCount = vector == null ? 0 : vector.Cardinality,
            DeletionVectorSize = vector?.SizeInBytes,
            ClusteringProvider = add.ClusteringProvider,
            NumRecords = add.NumRecords,
            NullCounts = Counts(add.NullCounts),
        };
        if (add.Stats != null && add.Stats.Length > 0)
        {
            if (DeltaStatsParser.TryParse(add.Stats, out long? rows, out KeyValuePair<string, long>[]? nulls))
            {
                file.NumRecords = rows ?? file.NumRecords;
                file.NullCounts = nulls ?? file.NullCounts;
            }
            else
            {
                StatsErrors++;
            }
        }

        live[Key(path, vector)] = file;
    }

    private void ApplyRemove(RemoveAction remove)
    {
        if (string.IsNullOrEmpty(remove.Path))
        {
            return;
        }

        string path = Decode(remove.Path!);
        string key = Key(path, remove.DeletionVector);
        long? size = remove.Size;
        if (live.TryGetValue(key, out LiveFile? file))
        {
            live.Remove(key);
            size ??= file.Size;
        }

        if (removed.TryGetValue(path, out long? known) && size == null)
        {
            size = known;
        }

        removed[path] = size;
    }

    private static bool? VOrder(IReadOnlyDictionary<string, string?>? tags)
    {
        if (tags == null)
        {
            return null;
        }

        foreach (KeyValuePair<string, string?> tag in tags)
        {
            if (string.Equals(tag.Key, "VORDER", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(tag.Value, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(tag.Value, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return null;
    }

    private static KeyValuePair<string, long>[]? Counts(IReadOnlyDictionary<string, long>? counts)
    {
        if (counts == null)
        {
            return null;
        }

        var array = new KeyValuePair<string, long>[counts.Count];
        int i = 0;
        foreach (KeyValuePair<string, long> count in counts)
        {
            array[i++] = count;
        }

        return array;
    }
}
