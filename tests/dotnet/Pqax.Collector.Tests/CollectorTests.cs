using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using Pqax.Collector.ParquetNet;

namespace Pqax.Collector.Tests;

/// <summary>
/// What the options of the collector change, and how it turns a failure into an issue instead of an exception
/// (plan 4.6 and 5, and the README of the collector).
/// </summary>
public class CollectorTests
{
    private static DumpTable Checkpointed => Fixture.CheckpointTable;

    private static DumpTable JsonOnly => Fixture.JsonOnlyTable;

    private static bool IsDataFile(string path) =>
        path.EndsWith(".parquet", StringComparison.Ordinal) && !path.Contains("/_delta_log/", StringComparison.Ordinal);

    private static List<FileRow> FilesOf(PqaxDocument doc, TableRow table) => doc.ReadFiles().Where(file => file.Table == table.Id).ToList();

    private static List<IssueRow> IssuesOf(PqaxDocument doc, TableRow table) => doc.Summary.Issues.Where(issue => issue.Table == table.Id).ToList();

    /// <summary>Asserts that a table of the dump was collected completely, at the Column chunks depth.</summary>
    private static void AssertComplete(PqaxDocument doc, DumpTable dump)
    {
        TableRow table = Fixture.TableOf(doc, dump);
        Assert.Equal("Column chunks", table.DetailDepth);
        Assert.Equal(dump.Files.Count, table.FileCount);
        Assert.Equal(dump.Files.Sum(file => Dump.Long(file["size"])), table.FileSize);
        List<FileRow> files = FilesOf(doc, table);
        Assert.Equal(dump.Files.Select(file => Dump.Long(file["size"])).Order(), files.Select(file => file.FileSize!.Value).Order());
        var groups = doc.ReadRowGroups().Where(group => files.Any(file => file.Id == group.File)).ToList();
        Assert.Equal(dump.Files.Sum(file => Dump.RowGroups(file).Count), groups.Count);
        long chunkSize = doc.ReadColumnChunks().Where(chunk => groups.Any(group => group.Id == chunk.RowGroup)).Sum(chunk => chunk.CompressedSize ?? 0);
        long expected = dump.Files.SelectMany(Dump.RowGroups).Sum(group => Enumerable.Range(0, Dump.ColumnCount(group)).Sum(i => Dump.At(group, "compressed", i) ?? 0));
        Assert.Equal(expected, chunkSize);
        Assert.Empty(IssuesOf(doc, table));
    }

    // ---- checkpoints ----

    [Fact]
    public async Task Without_a_checkpoint_reader_the_checkpointed_table_gets_an_issue_and_the_other_is_complete()
    {
        Collected result = await Fixture.CollectAsync(withoutCheckpointReader: true);
        using PqaxDocument doc = Fixture.Open(result.Data);
        TableRow table = Fixture.TableOf(doc, Checkpointed);
        Assert.Equal("Summary", table.DetailDepth);
        Assert.Null(table.FileCount);
        Assert.Null(table.FileSize);
        Assert.Null(table.RowCount);
        Assert.Empty(FilesOf(doc, table));
        Assert.Contains(IssuesOf(doc, table), issue => issue.Code == IssueCodes.DeltaCheckpointNotRead);
        AssertComplete(doc, JsonOnly);
        Fixture.AssertValid(result.Data);
    }

    private sealed class FailingCheckpointReader : ICheckpointReader
    {
        public Task<IReadOnlyList<DeltaAction>> ReadAsync(IStorage storage, string path, CancellationToken cancellation) =>
            throw new InvalidDataException("A checkpoint that does not line up.");
    }

    [Fact]
    public async Task A_checkpoint_reader_that_fails_gives_an_issue_and_no_exception()
    {
        Collected result = await Fixture.CollectAsync(checkpoints: new FailingCheckpointReader());
        using PqaxDocument doc = Fixture.Open(result.Data);
        TableRow table = Fixture.TableOf(doc, Checkpointed);
        Assert.Contains(IssuesOf(doc, table), issue => issue.Code == IssueCodes.DeltaCheckpointNotRead);
        Assert.Null(table.FileCount);
        AssertComplete(doc, JsonOnly);
        Fixture.AssertValid(result.Data);
    }

    private sealed class RecordingCheckpointReader : ICheckpointReader
    {
        private readonly ParquetNetCheckpointReader inner = new();

        public List<string> Paths { get; } = [];

        public Task<IReadOnlyList<DeltaAction>> ReadAsync(IStorage storage, string path, CancellationToken cancellation)
        {
            lock (Paths)
            {
                Paths.Add(path);
            }

            return inner.ReadAsync(storage, path, cancellation);
        }
    }

    [Fact]
    public async Task The_checkpoint_reader_reads_the_checkpoint_of_the_checkpointed_table_only()
    {
        var reader = new RecordingCheckpointReader();
        await Fixture.CollectAsync(checkpoints: reader);
        long version = Dump.Long(Checkpointed.Delta["checkpointVersion"]);
        Assert.Equal(new[] { $"{Checkpointed.TablePath}/_delta_log/{version.ToString("D20", CultureInfo.InvariantCulture)}.checkpoint.parquet" }, reader.Paths);
    }

    // ---- footers and levels ----

    [Fact]
    public async Task A_limit_of_one_footer_reads_one_footer_for_each_table()
    {
        var storage = new TestStorage(new LocalStorage(Fixture.Folder));
        Collected result = await Fixture.CollectAsync(new CollectorOptions { MaxFootersPerTable = 1 }, storage);
        using PqaxDocument doc = Fixture.Open(result.Data);
        foreach (DumpTable dump in Fixture.Tables)
        {
            TableRow table = Fixture.TableOf(doc, dump);
            Assert.Equal(1, table.FooterCount);
            Assert.Equal(dump.Files.Count, table.FileCount);
            List<FileRow> files = FilesOf(doc, table);
            Assert.Equal(dump.Files.Count, files.Count);
            FileRow read = Assert.Single(files, file => file.FooterSize != null);
            Assert.Equal(1, storage.Requests.Count(request => request.Kind == "read" && IsDataFile(request.Path) && request.Path.StartsWith(dump.TablePath + "/", StringComparison.Ordinal)));
            List<RowGroupRow> groups = doc.ReadRowGroups().Where(group => files.Any(file => file.Id == group.File)).ToList();
            Assert.All(groups, group => Assert.Equal(read.Id, group.File));
            Assert.Equal(read.RowGroupCount, groups.Count);
            Assert.Equal(groups.Count, table.RowGroupCount);
            Assert.Equal(doc.ReadColumnChunks().Where(chunk => groups.Any(group => group.Id == chunk.RowGroup)).Sum(chunk => chunk.CompressedSize), table.CompressedSize);
            JsonObject dumpFile = dump.Files.Single(file => Dump.Long(file["size"]) == read.FileSize);
            Assert.Equal(Dump.Long(dumpFile["footer"]!["footerSize"]), table.FooterSize);
        }

        Fixture.AssertValid(result.Data);
    }

    [Fact]
    public async Task Level_1_reads_the_logs_and_no_footer()
    {
        var storage = new TestStorage(new LocalStorage(Fixture.Folder));
        Collected result = await Fixture.CollectAsync(new CollectorOptions { Level = 1 }, storage);
        Assert.DoesNotContain(storage.Requests, request => request.Kind == "read" && IsDataFile(request.Path));
        using PqaxDocument doc = Fixture.Open(result.Data);
        Assert.Equal(1, doc.Manifest.Collection.Level);
        Assert.Empty(doc.Summary.PhysicalSchemas);
        Assert.Empty(doc.Summary.PhysicalColumns);
        Assert.Empty(doc.Summary.Writers);
        Assert.Empty(doc.ReadRowGroups());
        Assert.Empty(doc.ReadColumnChunks());
        foreach (DumpTable dump in Fixture.Tables)
        {
            TableRow table = Fixture.TableOf(doc, dump);
            Assert.Equal("Files", table.DetailDepth);
            Assert.Equal(1, table.CollectedLevel);
            Assert.Equal(0, table.FooterCount);
            Assert.Equal(dump.Files.Count, table.FileCount);
            Assert.Equal(dump.Files.Sum(file => Dump.Long(file["size"])), table.FileSize);
            List<FileRow> files = FilesOf(doc, table);
            Assert.All(files, file => Assert.Null(file.FooterSize));
            foreach (JsonObject file in dump.Files)
            {
                Assert.Equal(Dump.NullableLong(file["numRecords"]), files.Single(row => row.FileSize == Dump.Long(file["size"])).RowCount);
            }

            bool everyFileHasStats = dump.Files.All(file => file["numRecords"] is not null);
            long? rows = everyFileHasStats ? dump.Files.Sum(file => Dump.Long(file["numRecords"])) : null;
            Assert.Equal(rows, table.RowCount);
            List<ColumnRow> columns = doc.Summary.Columns.Where(column => column.Table == table.Id).ToList();
            Assert.All(columns, column => Assert.True(column.NullCount is null or 0, $"{column.Name}: {column.NullCount}"));
            if (!everyFileHasStats)
            {
                Assert.All(columns, column => Assert.Null(column.NullCount));
            }
        }

        Assert.Null(Fixture.TableOf(doc, JsonOnly).RowCount);
        Fixture.AssertValid(result.Data);
    }

    [Fact]
    public async Task The_default_tail_reads_each_footer_in_one_request_and_a_short_tail_in_two()
    {
        var once = new TestStorage(new LocalStorage(Fixture.Folder));
        Collected first = await Fixture.CollectAsync(storage: once);
        var twice = new TestStorage(new LocalStorage(Fixture.Folder));
        Collected second = await Fixture.CollectAsync(new CollectorOptions { FooterTailBytes = 12 }, twice);
        IReadOnlyList<string> dataFiles = Fixture.DataFiles();
        foreach (string path in dataFiles)
        {
            Assert.Equal(1, once.Requests.Count(request => request.Kind == "read" && request.Path == path));
            Assert.Equal(2, twice.Requests.Count(request => request.Kind == "read" && request.Path == path));
        }

        Assert.Equal(dataFiles.Count, once.Requests.Count(request => request.Kind == "read" && IsDataFile(request.Path)));
        using PqaxDocument a = Fixture.Open(first.Data);
        using PqaxDocument b = Fixture.Open(second.Data);
        Assert.Empty(Fixture.RawDifferences(b, a));
    }

    /// <summary>
    /// For each table, in the order in which the collector read its files, asserts that the first request of a file asks
    /// for the tail that the rule gives after the footers read before it, and that a footer takes a second request only
    /// when it does not fit that tail. Needs Parallelism 1, so that the files of a table are read one after the other.
    /// Returns, for each table of the dump, the length of the first request of each file, in order, and the requests.
    /// </summary>
    private static Dictionary<string, List<(string Path, int First, int Requests)>> AssertTailsFollowTheRule(TestStorage storage, CollectorOptions options)
    {
        var result = new Dictionary<string, List<(string, int, int)>>();
        foreach (DumpTable dump in Fixture.Tables)
        {
            string prefix = dump.TablePath + "/";
            List<StorageRequest> reads = storage.Requests.Where(request => request.Kind == "read" && IsDataFile(request.Path) && request.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            List<string> order = reads.Select(request => request.Path).Distinct().ToList();
            Assert.Equal(dump.Files.Count, order.Count);
            var files = new List<(string, int, int)>();
            int largest = 0;
            foreach (string path in order)
            {
                long size = new FileInfo(Path.Combine(Fixture.Folder, path)).Length;
                int footer = (int)Dump.Long(Fixture.DumpFileOfSize(size).File["footer"]!["footerSize"]);
                int tail = FooterTailTests.Expected(options.FooterTailBytes, options.FooterTailFloorBytes, options.FooterTailMargin, largest);
                int first = (int)Math.Min(tail, size);
                List<StorageRequest> fileReads = reads.Where(request => request.Path == path).ToList();
                Assert.True(first == fileReads[0].Length, $"{path}: first request of {fileReads[0].Length} bytes, expected {first}");
                Assert.Equal(size - first, fileReads[0].Offset);
                int requests = footer + 8 <= first ? 1 : 2;
                Assert.True(requests == fileReads.Count, $"{path}: {fileReads.Count} requests, expected {requests}");
                files.Add((path, first, fileReads.Count));
                largest = Math.Max(largest, footer + 8);
            }

            result[dump.Id] = files;
        }

        return result;
    }

    [Fact]
    public async Task The_first_file_of_a_table_is_read_with_the_initial_tail_and_the_next_with_the_learned_tail()
    {
        var options = new CollectorOptions { Parallelism = 1 };
        var storage = new TestStorage(new LocalStorage(Fixture.Folder));
        Collected adaptive = await Fixture.CollectAsync(options, storage);
        Dictionary<string, List<(string Path, int First, int Requests)>> files = AssertTailsFollowTheRule(storage, options);
        Assert.All(files.Values.SelectMany(list => list), file => Assert.Equal(1, file.Requests));

        // The footers of the lakehouse table are about 1 KB: the learned tail is the floor, 8,192 bytes.
        List<(string Path, int First, int Requests)> checkpointed = files[Checkpointed.Id];
        long firstSize = new FileInfo(Path.Combine(Fixture.Folder, checkpointed[0].Path)).Length;
        Assert.Equal((int)Math.Min(32768, firstSize), checkpointed[0].First);
        Assert.All(checkpointed.Skip(1), file => Assert.Equal(8192, file.First));

        // The same footprint as the kit, with fewer bytes than a fixed tail of 32,768 bytes.
        using (PqaxDocument doc = Fixture.Open(adaptive.Data))
        using (PqaxDocument sample = Fixture.OpenSample("sample.pqax"))
        {
            Assert.Empty(Fixture.RawDifferences(doc, sample));
        }

        var fixedStorage = new TestStorage(new LocalStorage(Fixture.Folder));
        Collected fixedTail = await Fixture.CollectAsync(new CollectorOptions { Parallelism = 1, FooterTailFloorBytes = 32768 }, fixedStorage);
        Assert.All(fixedStorage.Requests.Where(request => request.Kind == "read" && IsDataFile(request.Path)),
            request => Assert.Equal(Math.Min(32768, new FileInfo(Path.Combine(Fixture.Folder, request.Path)).Length), request.Length));
        long Bytes(TestStorage s) => s.Requests.Where(request => request.Kind == "read" && IsDataFile(request.Path)).Sum(request => (long)request.Length);
        Assert.True(Bytes(storage) < Bytes(fixedStorage), $"{Bytes(storage)} bytes with the learned tail, {Bytes(fixedStorage)} with a fixed one");
        using PqaxDocument a = Fixture.Open(adaptive.Data);
        using PqaxDocument b = Fixture.Open(fixedTail.Data);
        Assert.True(a.Manifest.Collection.BytesRead < b.Manifest.Collection.BytesRead);
        Assert.Equal(a.Manifest.Collection.RequestCount, b.Manifest.Collection.RequestCount);
        Assert.Empty(Fixture.RawDifferences(a, b));
    }

    [Fact]
    public async Task With_a_tiny_floor_and_no_margin_a_later_larger_footer_costs_a_second_request_and_raises_the_tail()
    {
        var options = new CollectorOptions { Parallelism = 1, FooterTailFloorBytes = 12, FooterTailMargin = 1.0 };
        var storage = new TestStorage(new LocalStorage(Fixture.Folder));
        Collected result = await Fixture.CollectAsync(options, storage);
        Dictionary<string, List<(string Path, int First, int Requests)>> files = AssertTailsFollowTheRule(storage, options);
        List<(string Path, int First, int Requests)> all = files.Values.SelectMany(list => list).ToList();
        Assert.Contains(all, file => file.Requests == 2);
        foreach (List<(string Path, int First, int Requests)> table in files.Values)
        {
            for (int i = 1; i < table.Count; i++)
            {
                long size = new FileInfo(Path.Combine(Fixture.Folder, table[i].Path)).Length;
                Assert.True(table[i].First < size, $"{table[i].Path}: a later file is read with the learned tail, shorter than the file");
                if (table[i - 1].Requests == 2)
                {
                    Assert.True(table[i].First > table[i - 1].First, $"{table[i].Path}: the tail rises after a footer that did not fit");
                }
            }
        }

        Assert.Equal(all.Count + all.Count(file => file.Requests == 2), storage.Requests.Count(request => request.Kind == "read" && IsDataFile(request.Path)));
        using PqaxDocument doc = Fixture.Open(result.Data);
        using PqaxDocument sample = Fixture.OpenSample("sample.pqax");
        Assert.Empty(Fixture.RawDifferences(doc, sample));
        Fixture.AssertValid(result.Data);
    }

    [Fact]
    public async Task The_collection_counts_every_request_of_the_storage()
    {
        var storage = new TestStorage(new LocalStorage(Fixture.Folder));
        IReadOnlyList<TableLocation> tables = await Fixture.DiscoverAsync(storage);
        int discovery = storage.Requests.Count;
        FootprintBuilder builder = await new PqaxCollector(storage, new CollectorOptions(), new ParquetNetCheckpointReader()).CollectAsync(tables, null, CancellationToken.None);
        Assert.Equal(storage.Requests.Count - discovery, builder.Collection.RequestCount);
        Assert.True(builder.Collection.BytesRead > 0);
        Assert.Equal(2, builder.Collection.Level);
    }

    // ---- values that are kept only when asked ----

    [Fact]
    public async Task Partition_values_are_kept_only_when_asked()
    {
        Collected with = await Fixture.CollectAsync(new CollectorOptions { IncludePartitionValues = true });
        Collected without = await Fixture.CollectAsync();
        string prefix = Checkpointed.TablePath + "/";
        List<string> partitioned = Fixture.DataFiles().Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && path[prefix.Length..].Contains('/')).ToList();
        Assert.NotEmpty(partitioned);

        using (PqaxDocument doc = Fixture.Open(with.Data))
        {
            Assert.True(doc.Manifest.Collection.IncludesPartitionValues);
            TableRow table = Fixture.TableOf(doc, Checkpointed);
            List<FileRow> files = FilesOf(doc, table);
            foreach (string path in Fixture.DataFiles().Where(path => path.StartsWith(prefix, StringComparison.Ordinal)))
            {
                string relative = path[prefix.Length..];
                FileRow file = files.Single(row => row.Path == relative);
                PartitionRow? partition = doc.ReadPartitions().FindById(file.Partition);
                Assert.NotNull(partition);
                Assert.NotNull(partition.Values);
                string? expected = relative.Contains('/') ? relative[(relative.IndexOf('=') + 1)..relative.IndexOf('/')] : null;
                Assert.Equal(new[] { expected }, partition.Values!.ToArray());
            }

            Fixture.AssertValid(with.Data);
        }

        using (PqaxDocument doc = Fixture.Open(without.Data))
        {
            Assert.False(doc.Manifest.Collection.IncludesPartitionValues);
            Assert.All(doc.ReadPartitions(), partition => Assert.Null(partition.Values));
            List<string?> paths = FilesOf(doc, Fixture.TableOf(doc, Checkpointed)).Select(file => file.Path).ToList();
            foreach (string path in partitioned)
            {
                Assert.DoesNotContain(path[prefix.Length..], paths);
                Assert.DoesNotContain(path[prefix.Length..path.LastIndexOf('/')], Fixture.AllText(without.Data));
            }
        }
    }

    [Fact]
    public async Task Data_values_are_kept_only_when_asked()
    {
        Collected with = await Fixture.CollectAsync(new CollectorOptions { IncludeDataValues = true });
        Collected without = await Fixture.CollectAsync();
        using (PqaxDocument doc = Fixture.Open(with.Data))
        {
            Assert.True(doc.Manifest.Collection.IncludesDataValues);
            Assert.All(doc.ReadColumnChunks(), chunk =>
            {
                Assert.NotNull(chunk.MinValue);
                Assert.NotNull(chunk.MaxValue);
            });
            foreach (DumpTable dump in Fixture.Tables)
            {
                TableRow table = Fixture.TableOf(doc, dump);
                foreach (JsonObject commit in dump.Commits)
                {
                    CommitRow row = doc.ReadCommits().Single(c => c.Table == table.Id && c.CommitId == Dump.Long(commit["version"]).ToString(CultureInfo.InvariantCulture));
                    IReadOnlyList<string> keys = Dump.Strings(commit["parameterKeys"]);
                    Assert.Equal(keys.Order(StringComparer.Ordinal), (row.Parameters?.Keys ?? []).Order(StringComparer.Ordinal));
                }
            }

            Fixture.AssertValid(with.Data);
        }

        using (PqaxDocument doc = Fixture.Open(without.Data))
        {
            Assert.False(doc.Manifest.Collection.IncludesDataValues);
            Assert.All(doc.ReadColumnChunks(), chunk =>
            {
                Assert.Null(chunk.MinValue);
                Assert.Null(chunk.MaxValue);
            });
            Assert.All(doc.ReadCommits(), commit => Assert.Null(commit.Parameters));
        }
    }

    /// <summary>The values of commitInfo members that name a person, in the logs of the fixture.</summary>
    private static List<string> UserNames()
    {
        var names = new List<string>();
        foreach (string path in Directory.EnumerateFiles(Fixture.Folder, "*.json", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadLines(path).Where(line => line.Trim().Length > 0))
            {
                if (JsonNode.Parse(line)?["commitInfo"]?["userName"] is JsonNode name)
                {
                    names.Add((string)name!);
                }
            }
        }

        return names;
    }

    [Fact]
    public async Task A_user_name_of_a_commit_is_never_written()
    {
        List<string> names = UserNames();
        Assert.NotEmpty(names);
        foreach (Collected result in new[] { await Fixture.CollectAsync(), await Fixture.CollectAsync(new CollectorOptions { IncludeDataValues = true, IncludePartitionValues = true }) })
        {
            string text = Fixture.AllText(result.Data);
            Assert.All(names, name => Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("\"userName\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"userId\"", text, StringComparison.Ordinal);
        }
    }

    // ---- progress, parallelism, cancellation ----

    [Fact]
    public async Task Progress_is_reported_once_for_each_table()
    {
        var progress = new ListProgress();
        Collected result = await Fixture.CollectAsync(progress: progress);
        Assert.Equal(result.Tables.Count, progress.Reports.Count);
        Assert.Equal(Enumerable.Range(1, result.Tables.Count), progress.Reports.Select(report => report.TablesCompleted));
        Assert.All(progress.Reports, report => Assert.Equal(result.Tables.Count, report.TableCount));
        Assert.Equal(result.Tables.Select(table => table.Path).Order(), progress.Reports.Select(report => report.Table.Path).Order());
        Assert.Equal(Fixture.Tables.Sum(table => table.Files.Count), progress.Reports.Sum(report => report.FootersRead));
        Assert.All(progress.Reports, report => Assert.Equal(0, report.IssueCount));
        Assert.True(progress.Reports.Zip(progress.Reports.Skip(1)).All(pair => pair.First.RequestCount <= pair.Second.RequestCount));
        Assert.True(progress.Reports[^1].RequestCount <= result.StorageRequests);
        Assert.True(progress.Reports[^1].BytesRead > 0);
    }

    [Fact]
    public async Task One_request_at_a_time_gives_the_same_footprint()
    {
        Collected serial = await Fixture.CollectAsync(new CollectorOptions { Parallelism = 1 });
        Collected parallel = await Fixture.CollectAsync(new CollectorOptions { Parallelism = 64 });
        using PqaxDocument a = Fixture.Open(serial.Data);
        using PqaxDocument b = Fixture.Open(parallel.Data);
        Assert.Empty(Fixture.RawDifferences(a, b));
        Assert.Equal(a.Manifest.Collection.RequestCount, b.Manifest.Collection.RequestCount);
    }

    [Fact]
    public async Task A_small_commit_limit_keeps_the_state_complete()
    {
        Collected result = await Fixture.CollectAsync(new CollectorOptions { MaxCommitsPerTable = 1 });
        using PqaxDocument doc = Fixture.Open(result.Data);
        foreach (DumpTable dump in Fixture.Tables)
        {
            TableRow table = Fixture.TableOf(doc, dump);
            Assert.Equal(dump.Files.Count, table.FileCount);
            Assert.True(doc.ReadCommits().Count(commit => commit.Table == table.Id) <= dump.Commits.Count);
        }

        Fixture.AssertValid(result.Data);
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_collection_before_it_starts()
    {
        var storage = new TestStorage(new LocalStorage(Fixture.Folder));
        IReadOnlyList<TableLocation> tables = await Fixture.DiscoverAsync(storage);
        int discovery = storage.Requests.Count;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var collector = new PqaxCollector(storage, new CollectorOptions(), new ParquetNetCheckpointReader());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(tables, null, cancel.Token));
        Assert.True(storage.Requests.Count - discovery <= 2, $"{storage.Requests.Count - discovery} requests after the cancellation");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public async Task Cancelling_during_the_collection_stops_it(int at)
    {
        IReadOnlyList<TableLocation> tables = await Fixture.DiscoverAsync(new LocalStorage(Fixture.Folder));
        using var cancel = new CancellationTokenSource();
        var storage = new TestStorage(new LocalStorage(Fixture.Folder)) { CancelSource = cancel, CancelAt = at };
        var collector = new PqaxCollector(storage, new CollectorOptions { Parallelism = 1 }, new ParquetNetCheckpointReader());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(tables, null, cancel.Token));
        Assert.True(storage.Requests.Count <= at + 1, $"{storage.Requests.Count} requests, cancelled at {at}");
    }

    // ---- options ----

    public static TheoryData<string> BadOptions => new()
    {
        "Level=0", "Level=3", "Depth=7", "MaxFootersPerTable=-1", "MaxCommitsPerTable=-1", "Parallelism=0", "FooterTailBytes=11",
        "FooterTailMargin=0.99", "FooterTailMargin=0", "FooterTailMargin=NaN", "FooterTailMargin=Infinity", "FooterTailFloorBytes=11",
        "FooterTailFloorBytes=0",
    };

    public static TheoryData<string> GoodOptions => new()
    {
        "FooterTailMargin=1", "FooterTailMargin=3.5", "FooterTailFloorBytes=12", "FooterTailFloorBytes=32768",
        "FooterTailFloorBytes=40000", "FooterTailBytes=12", "FooterTailBytes=4096",
    };

    private static CollectorOptions Options(string setting)
    {
        string[] pair = setting.Split('=');
        var options = new CollectorOptions();
        if (pair[0] == "FooterTailMargin")
        {
            options.FooterTailMargin = double.Parse(pair[1], CultureInfo.InvariantCulture);
            return options;
        }

        int value = int.Parse(pair[1], CultureInfo.InvariantCulture);
        switch (pair[0])
        {
            case "Level": options.Level = value; break;
            case "Depth": options.Depth = (DetailDepth)value; break;
            case "MaxFootersPerTable": options.MaxFootersPerTable = value; break;
            case "MaxCommitsPerTable": options.MaxCommitsPerTable = value; break;
            case "Parallelism": options.Parallelism = value; break;
            case "FooterTailBytes": options.FooterTailBytes = value; break;
            case "FooterTailFloorBytes": options.FooterTailFloorBytes = value; break;
            default: throw new ArgumentException(setting);
        }

        return options;
    }

    [Theory]
    [MemberData(nameof(GoodOptions))]
    public void An_option_in_range_is_accepted(string setting)
    {
        Assert.Null(Record.Exception(() => Options(setting).Validate()));
        Assert.Null(Record.Exception(() => new PqaxCollector(new LocalStorage(Fixture.Folder), Options(setting))));
    }

    [Fact]
    public async Task A_floor_above_the_tail_reads_with_the_tail()
    {
        var options = new CollectorOptions { Parallelism = 1, FooterTailBytes = 4096, FooterTailFloorBytes = 8192 };
        var storage = new TestStorage(new LocalStorage(Fixture.Folder));
        Collected result = await Fixture.CollectAsync(options, storage);
        AssertTailsFollowTheRule(storage, options);
        Assert.All(storage.Requests.Where(request => request.Kind == "read" && IsDataFile(request.Path)),
            request => Assert.True(request.Length <= 4096, $"{request.Path}: {request.Length} bytes"));
        using PqaxDocument doc = Fixture.Open(result.Data);
        using PqaxDocument sample = Fixture.OpenSample("sample.pqax");
        Assert.Empty(Fixture.RawDifferences(doc, sample));
    }

    [Theory]
    [MemberData(nameof(BadOptions))]
    public void An_option_out_of_range_is_refused(string setting)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(setting).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PqaxCollector(new LocalStorage(Fixture.Folder), Options(setting)));
    }

    [Fact]
    public void The_defaults_are_those_of_the_plan()
    {
        var options = new CollectorOptions();
        Assert.Equal(2, options.Level);
        Assert.Equal(DetailDepth.ColumnChunks, options.Depth);
        Assert.Equal(0, options.MaxFootersPerTable);
        Assert.Equal(30, options.MaxCommitsPerTable);
        Assert.Equal(16, options.Parallelism);
        Assert.False(options.IncludeDataValues);
        Assert.False(options.IncludePartitionValues);
        Assert.Equal(32768, options.FooterTailBytes);
        Assert.Equal(1.25, options.FooterTailMargin);
        Assert.Equal(8192, options.FooterTailFloorBytes);
        options.Validate();
        new CollectorOptions { Level = 1, Parallelism = 1, FooterTailBytes = 12, MaxCommitsPerTable = 0 }.Validate();
        new CollectorOptions { FooterTailBytes = 12, FooterTailFloorBytes = 12, FooterTailMargin = 1 }.Validate();
    }

    // ---- failures that become issues ----

    [Fact]
    public async Task A_table_without_a_log_gets_an_issue()
    {
        var storage = new LocalStorage(Fixture.Folder);
        List<TableLocation> tables = [.. await Fixture.DiscoverAsync(storage)];
        TableLocation known = tables.First();
        tables.Add(new TableLocation(known.SourceKind, known.SourceName, [], "no_such_table", Checkpointed.ItemFolder + "/Tables/no_such_table"));
        FootprintBuilder builder = await new PqaxCollector(storage, new CollectorOptions(), new ParquetNetCheckpointReader()).CollectAsync(tables, null, CancellationToken.None);
        using var stream = new MemoryStream();
        builder.Write(stream);
        byte[] data = stream.ToArray();
        using PqaxDocument doc = Fixture.Open(data);
        TableRow missing = doc.Summary.Tables.Single(table => table.Name == "no_such_table");
        Assert.Contains(IssuesOf(doc, missing), issue => issue.Code == IssueCodes.DeltaLogMissing);
        Assert.Equal("Summary", missing.DetailDepth);
        foreach (DumpTable dump in Fixture.Tables)
        {
            AssertComplete(doc, dump);
        }

        Fixture.AssertValid(data);
    }

    public static TheoryData<string> FooterFaults => new() { "encrypted", "length", "short", "missing", "denied" };

    [Theory]
    [MemberData(nameof(FooterFaults))]
    public async Task A_footer_that_cannot_be_read_becomes_an_issue_of_its_file(string fault)
    {
        string prefix = Checkpointed.TablePath + "/";
        string target = Fixture.DataFiles().Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(path => new FileInfo(Path.Combine(Fixture.Folder, path)).Length).First();
        byte[] original = File.ReadAllBytes(Path.Combine(Fixture.Folder, target));
        byte[] changed = (byte[])original.Clone();
        switch (fault)
        {
            case "encrypted":
                changed[^1] = (byte)'E';
                break;
            case "length":
                BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(changed.Length - 8, 4), changed.Length);
                break;
            case "short":
                changed = original.AsSpan(0, original.Length / 2).ToArray();
                break;
        }

        var storage = new TestStorage(new LocalStorage(Fixture.Folder))
        {
            Content = path => path == target && fault is "encrypted" or "length" or "short" ? changed : null,
            Fail = (kind, path) => kind == "read" && path == target && fault is "missing" or "denied"
                ? new StorageException("A request failed.", fault == "missing" ? 404 : 403)
                : null,
        };
        Collected result = await Fixture.CollectAsync(storage: storage);
        using PqaxDocument doc = Fixture.Open(result.Data);
        TableRow table = Fixture.TableOf(doc, Checkpointed);
        string[] expected = fault switch
        {
            "encrypted" => [IssueCodes.FooterEncrypted],
            "length" or "short" => [IssueCodes.FooterInvalid],
            "missing" => [IssueCodes.FooterNotRead],
            _ => [IssueCodes.AccessDenied, IssueCodes.FooterNotRead],
        };
        IssueRow issue = Assert.Single(IssuesOf(doc, table));
        Assert.Contains(issue.Code, expected);
        Assert.False(string.IsNullOrEmpty(issue.Path));
        Assert.Equal(Checkpointed.Files.Count - 1, table.FooterCount);
        Assert.Equal(Checkpointed.Files.Count, table.FileCount);
        Assert.Equal(original.Length, FilesOf(doc, table).Single(file => file.FooterSize == null).FileSize);
        AssertComplete(doc, JsonOnly);
        Fixture.AssertValid(result.Data);
    }

    [Fact]
    public async Task Access_denied_to_a_log_becomes_an_issue_of_its_table()
    {
        string log = Checkpointed.TablePath + "/_delta_log";
        var storage = new TestStorage(new LocalStorage(Fixture.Folder))
        {
            Fail = (kind, path) => kind == "list" && path.Trim('/') == log ? new StorageException("Access was denied.", 403) : null,
        };
        Collected result = await Fixture.CollectAsync(storage: storage);
        using PqaxDocument doc = Fixture.Open(result.Data);
        TableRow table = Fixture.TableOf(doc, Checkpointed);
        Assert.Contains(IssuesOf(doc, table), issue => issue.Code == IssueCodes.AccessDenied);
        Assert.Equal("Summary", table.DetailDepth);
        Assert.Empty(FilesOf(doc, table));
        AssertComplete(doc, JsonOnly);
        Fixture.AssertValid(result.Data);
    }
}
