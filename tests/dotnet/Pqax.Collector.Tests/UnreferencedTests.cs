using System.Text.Json.Nodes;

namespace Pqax.Collector.Tests;

/// <summary>
/// The fields of format 0.2 that the collector computes (object model 4.2 and 6.4, contracts 4.6): the unreferenced
/// files of a table from the listing of its folder, the removal times of its non-live files, and the last modification
/// of each partition. On the fixture, against the probe dump and the facts of conformance/README.md, and on Delta logs
/// and folders that the tests write.
/// </summary>
public class UnreferencedTests
{
    private const string TablePath = "Synth.Lakehouse/Tables/events";

    /// <summary>The removal time of the tombstone of the lakehouse table of the fixture, 1700000050000.</summary>
    private static readonly DateTimeOffset FixtureRemovedAt = new(2023, 11, 14, 22, 14, 10, TimeSpan.Zero);

    private static DumpTable Sales => Fixture.CheckpointTable;

    private static DumpTable Orders => Fixture.JsonOnlyTable;

    private static List<IssueRow> IssuesOf(PqaxDocument doc, TableRow table) => doc.Summary.Issues.Where(issue => issue.Table == table.Id).ToList();

    private static DateTimeOffset Time(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

    // ---- the fixture ----

    [Fact]
    public void The_dump_has_the_values_that_the_kit_readme_states()
    {
        Assert.Equal(1, Dump.Long(Sales.Dump["unreferencedFileCount"]));
        Assert.Equal(737, Dump.Long(Sales.Dump["unreferencedFileSize"]));
        Assert.Equal(FixtureRemovedAt, Dump.Time(Sales.Delta["removedOldestTimestamp"]));
        Assert.Equal(FixtureRemovedAt, Dump.Time(Sales.Delta["removedNewestTimestamp"]));
        Assert.Equal(0, Dump.Long(Orders.Dump["unreferencedFileCount"]));
        Assert.Equal(0, Dump.Long(Orders.Dump["unreferencedFileSize"]));
        Assert.Null(Orders.Delta["removedOldestTimestamp"]);
        Assert.Null(Orders.Delta["removedNewestTimestamp"]);
    }

    public static TheoryData<int, DetailDepth, bool> LevelsDepthsAndStorages
    {
        get
        {
            var data = new TheoryData<int, DetailDepth, bool>();
            foreach (int level in new[] { 1, 2 })
            {
                foreach (DetailDepth depth in new[] { DetailDepth.Summary, DetailDepth.Files, DetailDepth.RowGroups, DetailDepth.ColumnChunks })
                {
                    data.Add(level, depth, false);
                    data.Add(level, depth, true);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(LevelsDepthsAndStorages))]
    public async Task The_fixture_has_its_unreferenced_files_removal_times_and_partition_times_at_every_level_and_depth(int level, DetailDepth depth, bool folderByFolder)
    {
        // A TestStorage is a plain IStorage, which the collector lists one folder at a time.
        IStorage storage = folderByFolder ? new TestStorage(new LocalStorage(Fixture.Folder)) : new LocalStorage(Fixture.Folder);
        Collected result = await Fixture.CollectAsync(new CollectorOptions { Level = level, Depth = depth }, storage);
        using PqaxDocument doc = Fixture.Open(result.Data);

        TableRow sales = Fixture.TableOf(doc, Sales);
        Assert.Equal(1, sales.UnreferencedFileCount);
        Assert.Equal(737, sales.UnreferencedFileSize);
        Assert.Equal(FixtureRemovedAt, sales.NonLiveOldestRemovedAt);
        Assert.Equal(FixtureRemovedAt, sales.NonLiveNewestRemovedAt);
        Assert.Equal(Dump.Long(Sales.Delta["removedFileCount"]), sales.NonLiveFileCount);

        TableRow orders = Fixture.TableOf(doc, Orders);
        Assert.Equal(0, orders.UnreferencedFileCount);
        Assert.Equal(0, orders.UnreferencedFileSize);
        Assert.Null(orders.NonLiveOldestRemovedAt);
        Assert.Null(orders.NonLiveNewestRemovedAt);

        foreach (DumpTable dump in Fixture.Tables)
        {
            TableRow table = Fixture.TableOf(doc, dump);
            Assert.Equal(Dump.Long(dump.Dump["unreferencedFileCount"]), table.UnreferencedFileCount);
            Assert.Equal(Dump.Long(dump.Dump["unreferencedFileSize"]), table.UnreferencedFileSize);
            Assert.Equal(dump.Delta["removedOldestTimestamp"] is null ? null : Dump.Time(dump.Delta["removedOldestTimestamp"]), table.NonLiveOldestRemovedAt);
            Assert.Equal(dump.Delta["removedNewestTimestamp"] is null ? null : Dump.Time(dump.Delta["removedNewestTimestamp"]), table.NonLiveNewestRemovedAt);
            Assert.Empty(IssuesOf(doc, table));
        }

        if (depth != DetailDepth.Summary)
        {
            List<PartitionRow> partitions = doc.ReadPartitions().Where(partition => partition.Table == sales.Id).ToList();
            Assert.Equal(
                [new DateTimeOffset(2023, 11, 14, 22, 15, 0, TimeSpan.Zero), new DateTimeOffset(2023, 11, 14, 22, 15, 10, TimeSpan.Zero)],
                partitions.Select(partition => partition.LastModifiedAt!.Value).Order());
            List<FileRow> files = doc.ReadFiles().Where(file => file.Table == sales.Id).ToList();
            foreach (PartitionRow partition in partitions)
            {
                Assert.Equal(files.Where(file => file.Partition == partition.Id).Max(file => file.ModifiedAt), partition.LastModifiedAt);
            }

            // The same from the dump: the latest modificationTime of the files of each partition number.
            var latest = Sales.Files.GroupBy(file => Dump.Long(file["partition"]!["Year"]))
                .Select(group => Dump.Time(group.Max(file => Dump.Long(file["modificationTime"])))).Order();
            Assert.Equal(latest, partitions.Select(partition => partition.LastModifiedAt!.Value).Order());
        }

        Fixture.AssertValid(result.Data);
    }

    [Fact]
    public async Task With_partition_values_the_partitions_of_the_fixture_have_their_last_modification()
    {
        Collected result = await Fixture.CollectAsync(new CollectorOptions { IncludePartitionValues = true });
        using PqaxDocument doc = Fixture.Open(result.Data);
        TableRow sales = Fixture.TableOf(doc, Sales);
        List<PartitionRow> partitions = doc.ReadPartitions().Where(partition => partition.Table == sales.Id).ToList();
        PartitionRow nullYear = Assert.Single(partitions, partition => partition.Values is [null]);
        PartitionRow year2024 = Assert.Single(partitions, partition => partition.Values is ["2024"]);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 22, 15, 0, TimeSpan.Zero), nullYear.LastModifiedAt);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 22, 15, 10, TimeSpan.Zero), year2024.LastModifiedAt);
    }

    [Fact]
    public async Task Without_a_checkpoint_reader_the_checkpointed_table_has_no_unreferenced_fields_and_no_removal_times()
    {
        Collected result = await Fixture.CollectAsync(withoutCheckpointReader: true);
        using PqaxDocument doc = Fixture.Open(result.Data);
        TableRow sales = Fixture.TableOf(doc, Sales);
        Assert.Null(sales.FileCount);
        Assert.Null(sales.UnreferencedFileCount);
        Assert.Null(sales.UnreferencedFileSize);
        Assert.Null(sales.NonLiveOldestRemovedAt);
        Assert.Null(sales.NonLiveNewestRemovedAt);
        TableRow orders = Fixture.TableOf(doc, Orders);
        Assert.Equal(0, orders.UnreferencedFileCount);
        Assert.Equal(0, orders.UnreferencedFileSize);
        Fixture.AssertValid(result.Data);
    }

    // ---- tables that the tests write ----

    /// <summary>Writes a file of a size under the folder of the table. Its bytes are zeros: level 1 reads no footer.</summary>
    private static void Data(TempLake lake, string path, int size) => lake.Write(TablePath + "/" + path, new byte[size]);

    /// <summary>The actions of version 0: the protocol, the metadata, the commit, and the actions given.</summary>
    private static JsonNode[] First(params JsonNode[] actions) => [LogLines.Protocol(), LogLines.Metadata(), LogLines.CommitInfo(0), .. actions];

    /// <summary>The actions of a later version: its commit and the actions given.</summary>
    private static JsonNode[] Later(long version, params JsonNode[] actions) => [LogLines.CommitInfo(version), .. actions];

    /// <summary>A collection of the one table of a lake at level 1: its footprint, its row, and its issues.</summary>
    private sealed record Run(PqaxDocument Document, TableRow Table, Collected Result) : IDisposable
    {
        public List<IssueRow> Issues => IssuesOf(Document, Table);

        public void Dispose() => Document.Dispose();
    }

    private static async Task<Run> CollectAsync(IStorage storage, CollectorOptions? options = null)
    {
        Collected result = await Fixture.CollectAsync(options ?? new CollectorOptions { Level = 1 }, storage, new StateCheckpointReader());
        Fixture.AssertValid(result.Data);
        PqaxDocument doc = Fixture.Open(result.Data);
        return new Run(doc, Assert.Single(doc.Summary.Tables), result);
    }

    private static (long?, long?, DateTimeOffset?, DateTimeOffset?, long?) Fields(TableRow table) =>
        (table.UnreferencedFileCount, table.UnreferencedFileSize, table.NonLiveOldestRemovedAt, table.NonLiveNewestRemovedAt, table.NonLiveFileCount);

    /// <summary>
    /// Collects the lake through LocalStorage, which lists a folder at any depth in one request, and through a plain
    /// IStorage over it, which the collector lists one folder at a time, asserts that both give the same fields, and
    /// returns the first.
    /// </summary>
    private static async Task<Run> CollectAsync(TempLake lake)
    {
        Run run = await CollectAsync(new LocalStorage(lake.Root));
        using Run walked = await CollectAsync(new TestStorage(new LocalStorage(lake.Root)));
        Assert.Equal(Fields(run.Table), Fields(walked.Table));
        Assert.Equal(run.Issues.Select(issue => issue.Code), walked.Issues.Select(issue => issue.Code));
        return run;
    }

    [Fact]
    public async Task A_table_whose_folder_has_only_its_live_files_has_no_unreferenced_file()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1), LogLines.Add("p=1/b.parquet", 20, 2)));
        Data(lake, "a.parquet", 10);
        Data(lake, "p=1/b.parquet", 20);
        using Run run = await CollectAsync(lake);
        Assert.Equal(2, run.Table.FileCount);
        Assert.Equal(0, run.Table.UnreferencedFileCount);
        Assert.Equal(0, run.Table.UnreferencedFileSize);
        Assert.Equal(0, run.Table.NonLiveFileCount);
        Assert.Null(run.Table.NonLiveOldestRemovedAt);
        Assert.Null(run.Table.NonLiveNewestRemovedAt);
        Assert.Empty(run.Issues);
    }

    [Fact]
    public async Task An_unreferenced_file_in_a_partition_folder_and_one_in_the_root_are_counted()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1), LogLines.Add("p=1/b.parquet", 20, 2)));
        Data(lake, "a.parquet", 10);
        Data(lake, "p=1/b.parquet", 20);
        Data(lake, "p=1/stray.parquet", 11);
        Data(lake, "stray.parquet", 7);
        Data(lake, "p=2/deeper/folder/stray.parquet", 5);
        using Run run = await CollectAsync(lake);
        Assert.Equal(2, run.Table.FileCount);
        Assert.Equal(3, run.Table.UnreferencedFileCount);
        Assert.Equal(23, run.Table.UnreferencedFileSize);
        Assert.Empty(run.Issues);
    }

    [Fact]
    public async Task A_removed_file_still_in_storage_is_unreferenced_and_a_removed_file_deleted_is_not()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1), LogLines.Add("b.parquet", 20, 2), LogLines.Add("c.parquet", 30, 3)));
        lake.Commit(TablePath, 1, Later(1, LogLines.Remove("b.parquet", 20, LogLines.Epoch + 61_000), LogLines.Remove("c.parquet", 30, LogLines.Epoch + 61_000)));
        Data(lake, "a.parquet", 10);
        Data(lake, "b.parquet", 20);
        using Run run = await CollectAsync(lake);
        Assert.Equal(1, run.Table.FileCount);
        Assert.Equal(2, run.Table.NonLiveFileCount);
        Assert.Equal(50, run.Table.NonLiveFileSize);
        Assert.Equal(1, run.Table.UnreferencedFileCount);
        Assert.Equal(20, run.Table.UnreferencedFileSize);
        Assert.Equal(Time(LogLines.Epoch + 61_000), run.Table.NonLiveOldestRemovedAt);
        Assert.Equal(Time(LogLines.Epoch + 61_000), run.Table.NonLiveNewestRemovedAt);
        Assert.Empty(run.Issues);
    }

    [Fact]
    public async Task Change_data_files_and_files_that_are_not_parquet_are_not_counted()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1)));
        Data(lake, "a.parquet", 10);
        Data(lake, "_change_data/cdc-00000.snappy.parquet", 13);
        Data(lake, "_change_data/p=1/cdc-00001.snappy.parquet", 14);
        Data(lake, "_delta_log/stray.parquet", 15);
        Data(lake, "deletion_vector_0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b.bin", 16);
        Data(lake, ".a.parquet.crc", 17);
        Data(lake, "a.parquet.tmp", 18);
        Data(lake, "p=1/notes.txt", 19);
        Data(lake, "parquet", 20);
        using Run run = await CollectAsync(lake);
        Assert.Equal(0, run.Table.UnreferencedFileCount);
        Assert.Equal(0, run.Table.UnreferencedFileSize);
        Assert.Empty(run.Issues);
    }

    [Fact]
    public async Task Only_the_log_and_change_data_folders_directly_under_the_table_folder_are_left_out()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1)));
        Data(lake, "a.parquet", 10);
        Data(lake, "p=1/_change_data/x.parquet", 11);
        Data(lake, "nested/_delta_log/y.parquet", 12);
        using Run run = await CollectAsync(lake);
        Assert.Equal(2, run.Table.UnreferencedFileCount);
        Assert.Equal(23, run.Table.UnreferencedFileSize);
        Assert.Empty(run.Issues);
    }

    [Fact]
    public async Task The_recursive_listing_of_onelake_gives_the_same_counts_as_the_other_storages()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1), LogLines.Add("dir%20one/b.parquet", 20, 2)));
        lake.Commit(TablePath, 1, Later(1, LogLines.Remove("a.parquet", 10, LogLines.Epoch + 61_000)));
        Data(lake, "a.parquet", 10);
        Data(lake, "dir one/b.parquet", 20);
        Data(lake, "dir one/stray.PARQUET", 3);
        Data(lake, "p=1/_change_data/x.parquet", 11);
        Data(lake, "_change_data/cdc.parquet", 13);
        Data(lake, "_delta_log/stray.parquet", 15);
        Data(lake, "notes.txt", 17);
        var handler = new FakeOneLake(lake.Root, "Test Workspace") { PageSize = 2 };
        using var storage = new OneLakeStorage("Test Workspace", _ => Task.FromResult("test-token-not-a-secret"),
            new OneLakeStorageOptions { Endpoint = new Uri("https://onelake.test"), HttpClient = new HttpClient(handler), MaxRetries = 0 });
        using Run remote = await CollectAsync(storage);
        using Run local = await CollectAsync(lake);
        Assert.Equal(Fields(local.Table), Fields(remote.Table));
        Assert.Equal(3, remote.Table.UnreferencedFileCount);
        Assert.Equal(24, remote.Table.UnreferencedFileSize);
        Assert.Equal(Time(LogLines.Epoch + 61_000), remote.Table.NonLiveOldestRemovedAt);
        Assert.Empty(remote.Issues);
        Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("recursive=true", StringComparison.Ordinal)
            && call.PathAndQuery.Contains("continuation=", StringComparison.Ordinal));
        Assert.Equal(handler.Calls.Count, storage.RequestCount);
    }

    [Fact]
    public async Task An_extension_in_another_case_counts()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1)));
        Data(lake, "a.parquet", 10);
        Data(lake, "UPPER.PARQUET", 5);
        Data(lake, "p=1/Mixed.Parquet", 6);
        using Run run = await CollectAsync(lake);
        Assert.Equal(2, run.Table.UnreferencedFileCount);
        Assert.Equal(11, run.Table.UnreferencedFileSize);
        Assert.Empty(run.Issues);
    }

    [Fact]
    public async Task A_path_that_the_log_percent_encodes_matches_its_listing_entry()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("dir%20one/part%20a.parquet", 10, 1), LogLines.Add("p=x%25y/b.parquet", 20, 2)));
        Data(lake, "dir one/part a.parquet", 10);
        Data(lake, "p=x%y/b.parquet", 20);
        Data(lake, "dir one/stray b.parquet", 7);
        using Run run = await CollectAsync(lake);
        Assert.Equal(2, run.Table.FileCount);
        Assert.Equal(1, run.Table.UnreferencedFileCount);
        Assert.Equal(7, run.Table.UnreferencedFileSize);
        Assert.Empty(run.Issues);
    }

    [Fact]
    public async Task The_comparison_of_paths_is_case_sensitive()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("Upper.parquet", 10, 1)));
        Data(lake, "upper.parquet", 10);
        using Run run = await CollectAsync(lake);
        Assert.Equal(1, run.Table.UnreferencedFileCount);
        Assert.Equal(10, run.Table.UnreferencedFileSize);
    }

    [Fact]
    public async Task Remove_actions_without_a_timestamp_give_no_removal_time_and_are_still_counted()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1), LogLines.Add("b.parquet", 20, 2), LogLines.Add("c.parquet", 30, 3)));
        lake.Commit(TablePath, 1, Later(1, LogLines.Remove("b.parquet", 20, null), LogLines.Remove("c.parquet", 30, null)));
        Data(lake, "a.parquet", 10);
        using Run run = await CollectAsync(lake);
        Assert.Equal(2, run.Table.NonLiveFileCount);
        Assert.Equal(50, run.Table.NonLiveFileSize);
        Assert.Null(run.Table.NonLiveOldestRemovedAt);
        Assert.Null(run.Table.NonLiveNewestRemovedAt);
        Assert.Equal(0, run.Table.UnreferencedFileCount);
        Assert.Empty(run.Issues);
    }

    [Fact]
    public async Task Removals_at_distinct_times_give_distinct_oldest_and_newest()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1), LogLines.Add("b.parquet", 20, 2), LogLines.Add("c.parquet", 30, 3),
            LogLines.Add("d.parquet", 40, 4)));

        // The later commit holds the earlier removal time, and one remove has no time.
        lake.Commit(TablePath, 1, Later(1, LogLines.Remove("b.parquet", 20, LogLines.Epoch + 90_000)));
        lake.Commit(TablePath, 2, Later(2, LogLines.Remove("c.parquet", 30, LogLines.Epoch + 30_000), LogLines.Remove("d.parquet", 40, null)));
        Data(lake, "a.parquet", 10);
        using Run run = await CollectAsync(lake);
        Assert.Equal(3, run.Table.NonLiveFileCount);
        Assert.Equal(Time(LogLines.Epoch + 30_000), run.Table.NonLiveOldestRemovedAt);
        Assert.Equal(Time(LogLines.Epoch + 90_000), run.Table.NonLiveNewestRemovedAt);
        Assert.Empty(run.Issues);
    }

    [Fact]
    public async Task A_path_removed_twice_whose_later_remove_has_no_timestamp_keeps_the_earlier_one()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1), LogLines.Add("b.parquet", 20, 2)));
        lake.Commit(TablePath, 1, Later(1, LogLines.Remove("b.parquet", 20, LogLines.Epoch + 61_000)));
        lake.Commit(TablePath, 2, Later(2, LogLines.Remove("b.parquet", 20, null)));
        Data(lake, "a.parquet", 10);
        using Run run = await CollectAsync(lake);
        Assert.Equal(1, run.Table.NonLiveFileCount);
        Assert.Equal(Time(LogLines.Epoch + 61_000), run.Table.NonLiveOldestRemovedAt);
        Assert.Equal(Time(LogLines.Epoch + 61_000), run.Table.NonLiveNewestRemovedAt);
    }

    [Fact]
    public async Task A_path_removed_twice_with_two_timestamps_takes_the_later_remove()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1), LogLines.Add("b.parquet", 20, 2)));
        lake.Commit(TablePath, 1, Later(1, LogLines.Remove("b.parquet", 20, LogLines.Epoch + 61_000)));
        lake.Commit(TablePath, 2, Later(2, LogLines.Remove("b.parquet", 20, LogLines.Epoch + 121_000)));
        Data(lake, "a.parquet", 10);
        using Run run = await CollectAsync(lake);
        Assert.Equal(1, run.Table.NonLiveFileCount);
        Assert.Equal(Time(LogLines.Epoch + 121_000), run.Table.NonLiveOldestRemovedAt);
        Assert.Equal(Time(LogLines.Epoch + 121_000), run.Table.NonLiveNewestRemovedAt);
    }

    [Fact]
    public async Task A_removed_file_that_is_added_back_is_not_a_tombstone()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1)));
        lake.Commit(TablePath, 1, Later(1, LogLines.Remove("a.parquet", 10, LogLines.Epoch + 61_000)));
        lake.Commit(TablePath, 2, Later(2, LogLines.Add("a.parquet", 10, 1)));
        Data(lake, "a.parquet", 10);
        using Run run = await CollectAsync(lake);
        Assert.Equal(1, run.Table.FileCount);
        Assert.Equal(0, run.Table.NonLiveFileCount);
        Assert.Null(run.Table.NonLiveOldestRemovedAt);
        Assert.Null(run.Table.NonLiveNewestRemovedAt);
        Assert.Equal(0, run.Table.UnreferencedFileCount);
        Assert.Equal(0, run.Table.UnreferencedFileSize);
        Assert.Empty(run.Issues);
    }

    [Fact]
    public async Task A_listing_entry_without_a_size_counts_in_the_count_and_not_in_the_size()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1)));
        Data(lake, "a.parquet", 10);
        Data(lake, "stray-sized.parquet", 11);
        Data(lake, "p=1/stray-unsized.parquet", 7);
        var storage = new SizelessStorage(new LocalStorage(lake.Root), path => path.EndsWith("/a.parquet", StringComparison.Ordinal) || path.Contains("unsized", StringComparison.Ordinal));
        using Run run = await CollectAsync(storage);
        Assert.Equal(1, run.Table.FileCount);
        Assert.Equal(10, run.Table.FileSize);
        Assert.Equal(2, run.Table.UnreferencedFileCount);
        Assert.Equal(11, run.Table.UnreferencedFileSize);
        Assert.Empty(run.Issues);
    }

    public static TheoryData<int?, string> ListingFailures => new()
    {
        { 403, IssueCodes.AccessDenied },
        { 500, IssueCodes.DataFilesNotListed },
        { 503, IssueCodes.DataFilesNotListed },
        { null, IssueCodes.DataFilesNotListed },
    };

    [Theory]
    [MemberData(nameof(ListingFailures))]
    public async Task A_listing_that_fails_gives_an_issue_and_no_unreferenced_fields(int? status, string code)
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("a.parquet", 10, 1), LogLines.Add("p=1/b.parquet", 20, 2)));
        lake.Commit(TablePath, 1, Later(1, LogLines.Remove("a.parquet", 10, LogLines.Epoch + 61_000)));
        Data(lake, "a.parquet", 10);
        Data(lake, "p=1/b.parquet", 20);
        Data(lake, "p=1/stray.parquet", 7);
        var storage = new TestStorage(new LocalStorage(lake.Root))
        {
            Fail = (kind, path) => kind == "list" && path.Trim('/') == TablePath + "/p=1" ? new StorageException("A listing failed.", status) : null,
        };
        using Run run = await CollectAsync(storage);
        IssueRow issue = Assert.Single(run.Issues);
        Assert.Equal(code, issue.Code);
        Assert.Null(run.Table.UnreferencedFileCount);
        Assert.Null(run.Table.UnreferencedFileSize);

        // The rest of the table does not depend on the listing.
        Assert.Equal("Files", run.Table.DetailDepth);
        Assert.Equal(1, run.Table.FileCount);
        Assert.Equal(20, run.Table.FileSize);
        Assert.Equal(1, run.Table.NonLiveFileCount);
        Assert.Equal(Time(LogLines.Epoch + 61_000), run.Table.NonLiveOldestRemovedAt);
        Assert.Equal(Time(LogLines.Epoch + 61_000), run.Table.NonLiveNewestRemovedAt);
    }

    [Fact]
    public async Task A_log_that_cannot_be_read_whole_leaves_out_the_unreferenced_fields_and_the_removal_times()
    {
        // The log keeps no commit before its checkpoint, so without a checkpoint reader the live files are unknown.
        using var lake = new TempLake();
        lake.Checkpoint(TablePath, 2);
        lake.Commit(TablePath, 3, Later(3, LogLines.Add("a.parquet", 10, 1), LogLines.Remove("b.parquet", 20, LogLines.Epoch + 61_000)));
        Data(lake, "a.parquet", 10);
        Data(lake, "stray.parquet", 7);
        Collected result = await Fixture.CollectAsync(new CollectorOptions { Level = 1 }, new LocalStorage(lake.Root), withoutCheckpointReader: true);
        Fixture.AssertValid(result.Data);
        using PqaxDocument doc = Fixture.Open(result.Data);
        TableRow table = Assert.Single(doc.Summary.Tables);
        Assert.Contains(IssuesOf(doc, table), issue => issue.Code == IssueCodes.DeltaCheckpointNotRead);
        Assert.Null(table.FileCount);
        Assert.Null(table.UnreferencedFileCount);
        Assert.Null(table.UnreferencedFileSize);
        Assert.Null(table.NonLiveOldestRemovedAt);
        Assert.Null(table.NonLiveNewestRemovedAt);
    }

    [Fact]
    public async Task The_requests_of_the_listing_are_counted()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0, First(LogLines.Add("p=1/a.parquet", 10, 1), LogLines.Add("p=2/b.parquet", 20, 2)));
        Data(lake, "p=1/a.parquet", 10);
        Data(lake, "p=2/b.parquet", 20);
        Data(lake, "p=2/deeper/stray.parquet", 7);
        Data(lake, "_change_data/cdc.parquet", 8);

        // Folder by folder: one listing for each folder of the table outside the log and the change data.
        var walked = new TestStorage(new LocalStorage(lake.Root));
        IReadOnlyList<TableLocation> tables = await Fixture.DiscoverAsync(walked);
        int discovery = walked.Requests.Count;
        FootprintBuilder builder = await new PqaxCollector(walked, new CollectorOptions { Level = 1 }, new StateCheckpointReader()).CollectAsync(tables, null, CancellationToken.None);
        List<StorageRequest> requests = walked.Requests.Skip(discovery).ToList();
        Assert.Equal(requests.Count, builder.Collection.RequestCount);
        foreach (string folder in new[] { "", "/p=1", "/p=2", "/p=2/deeper" })
        {
            Assert.Contains(requests, request => request.Kind == "list" && request.Path.Trim('/') == TablePath + folder);
        }

        // Through LocalStorage, the folder is listed at any depth in one request, in place of the listing of the table
        // folder and of each folder below it.
        int below = requests.Count(request => request.Kind == "list" && request.Path.Trim('/').StartsWith(TablePath + "/", StringComparison.Ordinal)
            && !request.Path.Trim('/').StartsWith(TablePath + "/_delta_log", StringComparison.Ordinal));
        Assert.True(below >= 3, $"{below} listings below the table folder");
        var local = new LocalStorage(lake.Root);
        IReadOnlyList<TableLocation> again = await Fixture.DiscoverAsync(local);
        long before = local.RequestCount;
        FootprintBuilder recursive = await new PqaxCollector(local, new CollectorOptions { Level = 1 }, new StateCheckpointReader()).CollectAsync(again, null, CancellationToken.None);
        Assert.Equal(local.RequestCount - before, recursive.Collection.RequestCount);
        Assert.Equal(builder.Collection.RequestCount - below, recursive.Collection.RequestCount);

        using var stream = new MemoryStream();
        builder.Write(stream);
        using PqaxDocument doc = Fixture.Open(stream.ToArray());
        Assert.Equal(1, doc.Summary.Tables[0].UnreferencedFileCount);
        Assert.Equal(7, doc.Summary.Tables[0].UnreferencedFileSize);
    }

    [Fact]
    public async Task A_partition_has_the_latest_modification_of_its_files_and_none_when_a_file_has_none()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0,
            LogLines.Protocol(), LogLines.PartitionedMetadata(), LogLines.CommitInfo(0),
            LogLines.Add("p=1/a.parquet", 10, 1, LogLines.Epoch + 5_000, "1"),
            LogLines.Add("p=1/b.parquet", 20, 2, LogLines.Epoch + 9_000, "1"),
            LogLines.Add("p=1/c.parquet", 30, 3, LogLines.Epoch + 7_000, "1"),
            LogLines.Add("p=2/d.parquet", 40, 4, LogLines.Epoch + 8_000, "2"),
            LogLines.Add("p=2/e.parquet", 50, 5, null, "2"));
        using Run run = await CollectAsync(new LocalStorage(lake.Root), new CollectorOptions { Level = 1, IncludePartitionValues = true });
        List<PartitionRow> partitions = run.Document.ReadPartitions().ToList();
        Assert.Equal(2, partitions.Count);
        PartitionRow one = Assert.Single(partitions, partition => partition.Values is ["1"]);
        PartitionRow two = Assert.Single(partitions, partition => partition.Values is ["2"]);
        Assert.Equal(Time(LogLines.Epoch + 9_000), one.LastModifiedAt);
        Assert.Equal(3, one.FileCount);
        Assert.Null(two.LastModifiedAt);
        Assert.Equal(2, two.FileCount);
        Assert.Null(run.Table.LastModifiedAt);
    }

    [Fact]
    public async Task A_table_whose_files_all_have_a_modification_time_has_the_latest()
    {
        using var lake = new TempLake();
        lake.Commit(TablePath, 0,
            LogLines.Protocol(), LogLines.PartitionedMetadata(), LogLines.CommitInfo(0),
            LogLines.Add("p=1/a.parquet", 10, 1, LogLines.Epoch + 5_000, "1"),
            LogLines.Add("p=2/b.parquet", 20, 2, LogLines.Epoch + 9_000, "2"));
        using Run run = await CollectAsync(new LocalStorage(lake.Root), new CollectorOptions { Level = 1, IncludePartitionValues = true });
        Assert.Equal(Time(LogLines.Epoch + 9_000), run.Table.LastModifiedAt);
        Assert.Equal(Time(LogLines.Epoch + 5_000), Assert.Single(run.Document.ReadPartitions(), partition => partition.Values is ["1"]).LastModifiedAt);
        Assert.Equal(Time(LogLines.Epoch + 9_000), Assert.Single(run.Document.ReadPartitions(), partition => partition.Values is ["2"]).LastModifiedAt);
    }
}
