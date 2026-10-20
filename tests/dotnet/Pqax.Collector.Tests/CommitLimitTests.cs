using System.Globalization;

namespace Pqax.Collector.Tests;

/// <summary>
/// MaxCommitsPerTable on Delta logs that the tests write (README of the collector, Commits): every commit that the state
/// needs is read whatever the limit, those after the checkpoint or all of them without one; then older ones, the most
/// recent first, until the commit files requested reach the limit or the log ends. A failed read counts toward the
/// limit, gets an issue, and has no row.
/// </summary>
public class CommitLimitTests
{
    private const string TablePath = "Synth.Lakehouse/Tables/events";

    private const string LogPath = TablePath + "/_delta_log";

    /// <summary>A table whose log keeps the commits from oldest to latest, with a checkpoint at a version or none.</summary>
    private static TempLake Lake(long latest, long? checkpoint, long oldest = 0)
    {
        var lake = new TempLake();
        for (long version = oldest; version <= latest; version++)
        {
            if (version == 0)
            {
                lake.Commit(TablePath, 0, LogLines.Protocol(), LogLines.Metadata(), LogLines.CommitInfo(0));
            }
            else
            {
                lake.Commit(TablePath, version, LogLines.CommitInfo(version));
            }
        }

        if (checkpoint != null)
        {
            lake.Checkpoint(TablePath, checkpoint.Value);
        }

        return lake;
    }

    /// <summary>A collection of the table: its footprint, its row, the versions of the commit files requested, and the checkpoint reader.</summary>
    private sealed record Run(PqaxDocument Document, TableRow Table, IReadOnlyList<long> Requested, StateCheckpointReader Checkpoints) : IDisposable
    {
        public List<CommitRow> Commits => Document.ReadCommits().Where(commit => commit.Table == Table.Id).ToList();

        public void Dispose() => Document.Dispose();
    }

    private static async Task<Run> CollectAsync(TempLake lake, int limit, Func<string, string, Exception?>? fail = null, bool withoutCheckpointReader = false)
    {
        var storage = new TestStorage(new LocalStorage(lake.Root)) { Fail = fail };
        var checkpoints = new StateCheckpointReader();
        Collected result = await Fixture.CollectAsync(new CollectorOptions { MaxCommitsPerTable = limit }, storage, checkpoints, withoutCheckpointReader);
        Fixture.AssertValid(result.Data);
        PqaxDocument doc = Fixture.Open(result.Data);
        List<long> requested = storage.Requests.Where(request => request.Kind == "read" && IsCommit(request.Path)).Select(request => VersionOf(request.Path)).ToList();
        return new Run(doc, Assert.Single(doc.Summary.Tables), requested, checkpoints);
    }

    private static bool IsCommit(string path)
    {
        if (!path.StartsWith(LogPath + "/", StringComparison.Ordinal))
        {
            return false;
        }

        string name = path[(LogPath.Length + 1)..];
        return name.Length == 25 && name.EndsWith(".json", StringComparison.Ordinal) && name[..20].All(char.IsAsciiDigit);
    }

    private static long VersionOf(string path) => long.Parse(path[(LogPath.Length + 1)..(LogPath.Length + 21)], CultureInfo.InvariantCulture);

    private static List<long> Versions(long first, long last) => Enumerable.Range((int)first, (int)(last - first + 1)).Select(version => (long)version).ToList();

    /// <summary>
    /// Asserts that the collection requested each of these commit files once, and that every one read without a failure
    /// is in commitsRead, in the commits, in the operations, and in oldestCommitAt.
    /// </summary>
    private static void AssertRead(Run run, IReadOnlyList<long> requested, IReadOnlyList<long>? failed = null)
    {
        Assert.Equal(requested, run.Requested.Order());
        List<long> read = requested.Except(failed ?? []).ToList();
        Assert.Equal(read.Count, run.Table.CommitsRead);

        List<CommitRow> commits = run.Commits;
        Assert.Equal(read, commits.Select(commit => long.Parse(commit.CommitId!, CultureInfo.InvariantCulture)).Order());
        Assert.All(commits, commit => Assert.Equal(LogLines.TimeOf(long.Parse(commit.CommitId!, CultureInfo.InvariantCulture)), commit.Timestamp));
        Assert.All(commits, commit => Assert.Equal("WRITE", commit.Operation));

        OperationRow operation = Assert.Single(run.Document.Summary.Operations, row => row.Table == run.Table.Id);
        Assert.Equal("WRITE", operation.Name);
        Assert.Equal(read.Count, operation.CommitCount);
        Assert.Equal(LogLines.TimeOf(read.Max()), operation.LastAt);
        Assert.Equal(LogLines.TimeOf(read.Min()), run.Table.OldestCommitAt);
    }

    /// <summary>Asserts that the state of the table is complete: the version, the checkpoint, and no issue.</summary>
    private static void AssertComplete(Run run, long latest, long? checkpoint, long kept)
    {
        Assert.Equal(latest.ToString(CultureInfo.InvariantCulture), run.Table.SnapshotId);
        Assert.Equal(LogLines.TimeOf(latest), run.Table.SnapshotTimestamp);
        Assert.Equal(kept, run.Table.CommitCount);
        Assert.Equal(0, run.Table.FileCount);
        Assert.NotNull(run.Table.Delta);
        Assert.Equal(checkpoint, run.Table.Delta.CheckpointVersion);
        Assert.Equal(checkpoint == null ? null : latest - checkpoint, run.Table.Delta.CommitsAfterCheckpoint);
        Assert.Equal(checkpoint == null ? [] : new[] { LogPath + "/" + checkpoint.Value.ToString("D20", CultureInfo.InvariantCulture) + ".checkpoint.parquet" }, run.Checkpoints.Paths);
        Assert.Empty(run.Document.Summary.Issues);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(0)]
    [InlineData(45)]
    public async Task A_limit_up_to_the_commits_after_the_checkpoint_reads_those_commits_and_no_older_one(int limit)
    {
        using TempLake lake = Lake(latest: 145, checkpoint: 100);
        using Run run = await CollectAsync(lake, limit);
        AssertRead(run, Versions(101, 145));
        Assert.Equal(45, run.Table.CommitsRead);
        AssertComplete(run, latest: 145, checkpoint: 100, kept: 146);
    }

    [Fact]
    public async Task A_limit_above_the_commits_after_the_checkpoint_also_reads_older_commits_the_most_recent_first()
    {
        using TempLake lake = Lake(latest: 145, checkpoint: 100);
        using Run run = await CollectAsync(lake, 50);
        AssertRead(run, Versions(96, 145));
        Assert.Equal(50, run.Table.CommitsRead);
        AssertComplete(run, latest: 145, checkpoint: 100, kept: 146);
    }

    [Fact]
    public async Task The_older_commits_stop_where_the_log_ends_before_the_limit()
    {
        using TempLake lake = Lake(latest: 145, checkpoint: 100, oldest: 90);
        using Run run = await CollectAsync(lake, 60);
        AssertRead(run, Versions(90, 145));
        Assert.Equal(56, run.Table.CommitsRead);
        AssertComplete(run, latest: 145, checkpoint: 100, kept: 56);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(50)]
    public async Task Without_a_checkpoint_every_commit_is_read_whatever_the_limit(int limit)
    {
        using TempLake lake = Lake(latest: 39, checkpoint: null);
        using Run run = await CollectAsync(lake, limit);
        AssertRead(run, Versions(0, 39));
        Assert.Equal(40, run.Table.CommitsRead);
        AssertComplete(run, latest: 39, checkpoint: null, kept: 40);
    }

    [Fact]
    public async Task A_checkpoint_that_is_not_read_with_every_commit_kept_makes_the_state_need_them_all()
    {
        using TempLake lake = Lake(latest: 145, checkpoint: 100);
        using Run run = await CollectAsync(lake, 30, withoutCheckpointReader: true);
        AssertRead(run, Versions(0, 145));
        Assert.Equal(146, run.Table.CommitsRead);
        Assert.Equal("145", run.Table.SnapshotId);
        Assert.Equal(0, run.Table.FileCount);
        Assert.Empty(run.Checkpoints.Paths);
        Assert.Empty(run.Document.Summary.Issues);
    }

    [Theory]
    [InlineData("denied", IssueCodes.AccessDenied)]
    [InlineData("not JSON", IssueCodes.DeltaCommitNotRead)]
    public async Task A_commit_whose_read_fails_counts_toward_the_limit_and_gets_an_issue_and_no_row(string failure, string code)
    {
        using TempLake lake = Lake(latest: 145, checkpoint: 100);
        string failed = LogPath + "/" + LogLines.CommitName(98);
        if (failure == "not JSON")
        {
            lake.Write(failed, "this line is not JSON\n");
        }

        using Run run = await CollectAsync(lake, 50, (kind, path) =>
            failure == "denied" && kind == "read" && path == failed ? new StorageException("Access was denied.", 403) : null);
        AssertRead(run, Versions(96, 145), failed: [98]);
        Assert.Equal(50, run.Requested.Count);
        Assert.Equal(49, run.Table.CommitsRead);
        IssueRow issue = Assert.Single(run.Document.Summary.Issues);
        Assert.Equal(code, issue.Code);
        Assert.Equal(run.Table.Id, issue.Table);
        Assert.Equal("_delta_log/" + LogLines.CommitName(98), issue.Path);
        Assert.Equal("145", run.Table.SnapshotId);
        Assert.Equal(0, run.Table.FileCount);
    }
}
