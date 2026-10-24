using System.Net;
using System.Text.Json.Nodes;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using Parquet.Serialization;
using Pqax.Collector.ParquetNet;

namespace Pqax.Collector.Tests;

/// <summary>
/// The collection of the tables of a storage folder (object model 6.4 and 6.5): a folder of Parquet files with no table
/// format, from one listing of its folder, its columns from the footer of its most recent file, its totals from the
/// listing and, at level 2, from the footers; a Delta table found at a depth, collected as any Delta table; the source of
/// the folder; and the failures of the listing and of the footer of the columns.
/// </summary>
public class ParquetTableTests
{
    private static JsonObject OlderDump => FolderLayout.DumpOf(FolderLayout.OlderFile);

    private static JsonObject NewerDump => FolderLayout.DumpOf(FolderLayout.NewerFile);

    /// <summary>The columns of the Parquet files of the fixture, in the order of their footer: name, Delta type, nullable.</summary>
    private static readonly (string Name, string DataType, bool IsNullable)[] FixtureColumns =
    [
        ("id", "long", true),
        ("category", "integer", true),
        ("name", "string", true),
        ("amount", "double", true),
    ];

    private static async Task<PqaxDocument> CollectAsync(IStorage storage, IReadOnlyList<TableLocation> tables, CollectorOptions? options = null)
    {
        FootprintBuilder builder = await new PqaxCollector(storage, options ?? new CollectorOptions(), new ParquetNetCheckpointReader()).CollectAsync(tables, null, CancellationToken.None);
        using var stream = new MemoryStream();
        builder.Write(stream);
        byte[] data = stream.ToArray();
        Fixture.AssertValid(data);
        return Fixture.Open(data);
    }

    private static TableRow TableAt(PqaxDocument doc, string name, params string[] ns) =>
        Assert.Single(doc.Summary.Tables, table => table.Name == name && (table.Namespace ?? []).SequenceEqual(ns));

    private static List<ColumnRow> ColumnsOf(PqaxDocument doc, TableRow table) => doc.Summary.Columns.Where(column => column.Table == table.Id).ToList();

    private static List<FileRow> FilesOf(PqaxDocument doc, TableRow table) => doc.ReadFiles().Where(file => file.Table == table.Id).ToList();

    private static List<IssueRow> IssuesOf(PqaxDocument doc, TableRow table) => doc.Summary.Issues.Where(issue => issue.Table == table.Id).ToList();

    private static long ChunkSum(JsonObject file, string field) =>
        Dump.RowGroups(file).Sum(group => Enumerable.Range(0, Dump.ColumnCount(group)).Sum(i => Dump.At(group, field, i) ?? 0));

    private static IStorage StorageOf(string kind, TempLake lake, FakeOneLake handler) =>
        kind == "adls" ? FolderLayout.Adls(handler) : new LocalStorage(lake.Root);

    private static string? LocationOf(string kind, IStorage storage, string path) => kind == "adls"
        ? FolderLayout.Container + (path.Length == 0 ? "" : "/" + path)
        : Path.Combine(((LocalStorage)storage).Root, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Asserts what a Parquet table never has: the fields of a table format, of its log, and of its history.</summary>
    private static void AssertNoTableFormat(PqaxDocument doc, TableRow table)
    {
        Assert.Equal(TableFormats.Parquet, table.Format);
        Assert.Null(table.TableId);
        Assert.Null(table.SnapshotId);
        Assert.Null(table.SnapshotTimestamp);
        Assert.Null(table.CreatedAt);
        Assert.Null(table.Description);
        Assert.Null(table.Properties);
        Assert.Null(table.Delta);
        Assert.Null(table.Iceberg);
        Assert.Null(table.CommitCount);
        Assert.Null(table.CommitsRead);
        Assert.Null(table.OldestCommitAt);
        Assert.Null(table.MetadataFileCount);
        Assert.Null(table.MetadataSize);
        Assert.Null(table.NonLiveFileCount);
        Assert.Null(table.NonLiveFileSize);
        Assert.Null(table.NonLiveOldestRemovedAt);
        Assert.Null(table.NonLiveNewestRemovedAt);
        Assert.DoesNotContain(doc.Summary.Operations, operation => operation.Table == table.Id);
        Assert.DoesNotContain(doc.ReadCommits(), commit => commit.Table == table.Id);
        Assert.DoesNotContain(doc.Summary.PartitionSpecs, spec => spec.Table == table.Id);
    }

    /// <summary>Asserts the totals that come from the listing of the folder of Parquet files of the layout.</summary>
    private static void AssertListingTotals(PqaxDocument doc, TableRow table)
    {
        long older = Dump.Long(OlderDump["size"]);
        long newer = Dump.Long(NewerDump["size"]);
        Assert.Equal(2, table.FileCount);
        Assert.Equal(older + newer, table.FileSize);
        Assert.Equal(new DateTimeOffset(FolderLayout.Newer), table.LastModifiedAt);
        Assert.Equal(0, table.UnreferencedFileCount);
        Assert.Equal(0, table.UnreferencedFileSize);
        Assert.NotNull(table.FileSizes);
        Assert.Equal(2, table.FileSizes.Count);
        Assert.Equal(older + newer, table.FileSizes.Sum);
        Assert.Equal(Math.Min(older, newer), table.FileSizes.Minimum);
        Assert.Equal(Math.Max(older, newer), table.FileSizes.Maximum);
    }

    private static void AssertFixtureColumns(PqaxDocument doc, TableRow table)
    {
        List<ColumnRow> columns = ColumnsOf(doc, table);
        Assert.Equal(FixtureColumns, columns.Select(column => (column.Name!, column.DataType!, column.IsNullable!.Value)));
        Assert.All(columns, column =>
        {
            Assert.Null(column.FieldId);
            Assert.Null(column.PhysicalName);
            Assert.Null(column.Description);
        });
    }

    // ---- a folder of Parquet files ----

    [Theory]
    [InlineData("adls")]
    [InlineData("local")]
    public async Task At_level_2_a_folder_of_parquet_files_is_a_table_of_its_files_with_the_totals_of_their_footers(string kind)
    {
        using TempLake lake = FolderLayout.Create();
        FakeOneLake handler = FolderLayout.Handler(lake);
        IStorage storage = StorageOf(kind, lake, handler);
        using IDisposable? owned = storage as IDisposable;
        TableLocation raw = Assert.Single(await TableDiscovery.DiscoverFolderAsync(storage, "data/raw", CancellationToken.None));
        while (handler.Calls.TryDequeue(out _))
        {
        }

        using PqaxDocument doc = await CollectAsync(storage, [raw]);
        TableRow table = Assert.Single(doc.Summary.Tables);
        Assert.Equal("raw", table.Name);
        Assert.Empty(table.Namespace ?? []);
        Assert.Equal(LocationOf(kind, storage, "data/raw"), table.Location);
        AssertNoTableFormat(doc, table);
        AssertListingTotals(doc, table);
        AssertFixtureColumns(doc, table);

        Assert.Equal(2, table.CollectedLevel);
        Assert.Equal("Column chunks", table.DetailDepth);
        Assert.Equal("Footers", table.ColumnTotalsSource);
        Assert.Equal(2, table.FooterCount);
        Assert.Equal(Dump.Long(OlderDump["footer"]!["numRows"]) + Dump.Long(NewerDump["footer"]!["numRows"]), table.RowCount);
        Assert.Equal(Dump.RowGroups(OlderDump).Count + Dump.RowGroups(NewerDump).Count, table.RowGroupCount);
        Assert.Equal(Dump.Long(OlderDump["footer"]!["footerSize"]) + Dump.Long(NewerDump["footer"]!["footerSize"]), table.FooterSize);
        Assert.Equal(ChunkSum(OlderDump, "compressed") + ChunkSum(NewerDump, "compressed"), table.CompressedSize);
        Assert.Equal(ChunkSum(OlderDump, "uncompressed") + ChunkSum(NewerDump, "uncompressed"), table.UncompressedSize);
        Assert.All(ColumnsOf(doc, table), column => Assert.Equal(table.RowGroupCount, column.ChunkCount));
        Assert.Empty(IssuesOf(doc, table));

        // The data files only, by their names, with the size and the time of the listing and what their footer says.
        List<FileRow> files = FilesOf(doc, table).OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
        Assert.Equal([FolderLayout.OlderFile, FolderLayout.NewerFile], files.Select(file => file.Path));
        foreach ((FileRow file, JsonObject dump, DateTime time) in new[] { (files[0], OlderDump, FolderLayout.Older), (files[1], NewerDump, FolderLayout.Newer) })
        {
            Assert.Equal(Dump.Long(dump["size"]), file.FileSize);
            Assert.Equal(new DateTimeOffset(time), file.ModifiedAt);
            Assert.Equal(Dump.Long(dump["footer"]!["footerSize"]), file.FooterSize);
            Assert.Equal(Dump.RowGroups(dump).Count, file.RowGroupCount);
            Assert.Equal(Dump.Long(dump["footer"]!["numRows"]), file.RowCount);
        }

        // The source is the folder that the discovery was given.
        SourceRow source = Assert.Single(doc.Summary.Sources);
        Assert.Equal(source.Id, table.Source);
        Assert.Equal(SourceKinds.StorageFolder, source.Kind);
        Assert.Equal("raw", source.Name);
        Assert.Equal(LocationOf(kind, storage, "data/raw"), source.Location);
        Assert.Null(source.Identifiers);

        if (kind == "adls")
        {
            List<HttpCall> calls = [.. handler.Calls];

            // One listing of the folder, not recursive, in pages; no request goes below it.
            List<HttpCall> listings = calls.Where(call => FolderLayout.ListedDirectory(call) != null).ToList();
            Assert.NotEmpty(listings);
            Assert.All(listings, call =>
            {
                Assert.Equal("data/raw", FolderLayout.ListedDirectory(call));
                Assert.Contains("recursive=false", call.PathAndQuery, StringComparison.Ordinal);
            });
            Assert.Single(listings, call => !call.PathAndQuery.Contains("continuation=", StringComparison.Ordinal));

            // The footers of the two data files, and each range once: the footer of the columns is not read twice.
            List<HttpCall> reads = calls.Where(call => FolderLayout.ReadPath(call) != null).ToList();
            Assert.Equal(["data/raw/" + FolderLayout.OlderFile, "data/raw/" + FolderLayout.NewerFile], reads.Select(FolderLayout.ReadPath).Distinct().Order(StringComparer.Ordinal));
            Assert.Equal(reads.Count, reads.Select(call => (FolderLayout.ReadPath(call), call.Range)).Distinct().Count());
        }
    }

    [Theory]
    [InlineData(DetailDepth.ColumnChunks, "Files")]
    [InlineData(DetailDepth.Files, "Files")]
    [InlineData(DetailDepth.Summary, "Summary")]
    public async Task At_level_1_only_the_footer_of_the_most_recent_file_is_read_for_the_columns(DetailDepth depth, string detail)
    {
        using TempLake lake = FolderLayout.Create();
        FakeOneLake handler = FolderLayout.Handler(lake);
        using OneLakeStorage storage = FolderLayout.Adls(handler);
        TableLocation raw = Assert.Single(await TableDiscovery.DiscoverFolderAsync(storage, "data/raw", CancellationToken.None));
        while (handler.Calls.TryDequeue(out _))
        {
        }

        using PqaxDocument doc = await CollectAsync(storage, [raw], new CollectorOptions { Level = 1, Depth = depth });
        TableRow table = Assert.Single(doc.Summary.Tables);
        AssertNoTableFormat(doc, table);
        AssertListingTotals(doc, table);
        AssertFixtureColumns(doc, table);
        Assert.Equal(1, table.CollectedLevel);
        Assert.Equal(detail, table.DetailDepth);
        Assert.Equal(0, table.FooterCount);
        Assert.Null(table.RowCount);
        Assert.Empty(doc.ReadRowGroups());
        Assert.Empty(doc.ReadColumnChunks());
        Assert.Empty(IssuesOf(doc, table));
        List<FileRow> files = FilesOf(doc, table);
        Assert.Equal(depth == DetailDepth.Summary ? 0 : 2, files.Count);
        Assert.All(files, file => Assert.Null(file.FooterSize));

        List<HttpCall> reads = handler.Calls.Where(call => FolderLayout.ReadPath(call) != null).ToList();
        Assert.NotEmpty(reads);
        Assert.All(reads, call => Assert.Equal("data/raw/" + FolderLayout.NewerFile, FolderLayout.ReadPath(call)));
    }

    [Fact]
    public async Task A_limit_of_footers_reads_that_many_at_level_2()
    {
        using TempLake lake = FolderLayout.Create();
        var storage = new LocalStorage(lake.Root);
        TableLocation raw = Assert.Single(await TableDiscovery.DiscoverFolderAsync(storage, "data/raw", CancellationToken.None));
        using PqaxDocument doc = await CollectAsync(storage, [raw], new CollectorOptions { MaxFootersPerTable = 1 });
        TableRow table = Assert.Single(doc.Summary.Tables);
        Assert.Equal(2, table.FileCount);
        Assert.Equal(2, table.CollectedLevel);
        AssertFixtureColumns(doc, table);

        // The limit holds: the footer of the columns takes the place of a file of the sample, and is not read twice.
        Assert.Equal(1, table.FooterCount);
        List<FileRow> read = FilesOf(doc, table).Where(file => file.FooterSize != null).ToList();
        Assert.Equal(table.FooterCount, read.Count);
        Assert.Contains(read, file => file.Path == FolderLayout.NewerFile);
    }

    // ---- the columns ----

    /// <summary>Writes a Parquet file of one row group with Parquet.Net, each column a field and its values, and sets its time.</summary>
    private static async Task WriteAsync(TempLake lake, string path, DateTime modified, params (DataField Field, Array Values)[] columns)
    {
        string full = lake.FullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Parquet.Net attaches the fields to the schema, which must exist before the columns.
        var schema = new ParquetSchema(columns.Select(column => column.Field).ToArray());
        using (FileStream stream = File.Create(full))
        {
            using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream);
            using ParquetRowGroupWriter group = writer.CreateRowGroup();
            foreach ((DataField field, Array values) in columns)
            {
                await group.WriteColumnAsync(new DataColumn(field, values));
            }
        }

        File.SetLastWriteTimeUtc(full, modified);
    }

    private static async Task<List<ColumnRow>> ColumnsOfFolderAsync(TempLake lake, string folder)
    {
        var storage = new LocalStorage(lake.Root);
        TableLocation location = Assert.Single(await TableDiscovery.DiscoverFolderAsync(storage, folder, CancellationToken.None));
        Assert.Equal(TableFormats.Parquet, location.Format);
        using PqaxDocument doc = await CollectAsync(storage, [location]);
        TableRow table = Assert.Single(doc.Summary.Tables);
        Assert.Empty(IssuesOf(doc, table));
        return ColumnsOf(doc, table);
    }

    [Fact]
    public async Task The_columns_are_those_of_the_most_recent_file_with_the_type_names_of_delta()
    {
        using var lake = new TempLake();

        // The most recent file comes first by name, so that its time, not its name, chooses it.
        await WriteAsync(lake, "data/typed/a-newest.parquet", FolderLayout.Newer,
            (new DataField<bool>("flag"), new[] { true }),
            (new DataField<int?>("count"), new int?[] { 1 }),
            (new DataField<long>("id"), new[] { 1L }),
            (new DataField<string>("name"), new[] { "synthetic" }),
            (new DataField<double?>("amount"), new double?[] { 1.5 }),
            (new DataField<float>("ratio"), new[] { 0.5f }),
            (new DataField<byte[]>("blob"), new[] { new byte[] { 1, 2 } }),
            (new DataField<short>("small"), new short[] { 1 }),
            (new DataField<sbyte>("tiny"), new sbyte[] { 1 }),
            (new DataField<DateTime>("at"), new[] { new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }),
            (new DataField<decimal>("price"), new[] { 1.25m }),
            (new DecimalDataField("cost", 10, 2), new[] { 3.5m }),
            (new DataField<DateOnly>("day"), new[] { new DateOnly(2026, 1, 1) }));
        await WriteAsync(lake, "data/typed/b-older.parquet", FolderLayout.Older,
            (new DataField<int>("legacy"), new[] { 1 }));

        List<ColumnRow> columns = await ColumnsOfFolderAsync(lake, "data/typed");

        // As Parquet.Net writes them: an int and a long as INTEGER(32) and INTEGER(64), a DateTime as INT96, a decimal as a
        // FIXED_LEN_BYTE_ARRAY decimal(38,18), and the DecimalDataField as an INT64 DECIMAL; a value type is REQUIRED.
        (string, string, bool)[] expected =
        [
            ("flag", "boolean", false),
            ("count", "integer", true),
            ("id", "long", false),
            ("name", "string", true),
            ("amount", "double", true),
            ("ratio", "float", false),
            ("blob", "binary", true),
            ("small", "short", false),
            ("tiny", "byte", false),
            ("at", "timestamp", false),
            ("price", "decimal(38,18)", false),
            ("cost", "decimal(10,2)", false),
            ("day", "date", false),
        ];
        Assert.Equal(expected, columns.Select(column => (column.Name!, column.DataType!, column.IsNullable!.Value)));
        Assert.All(columns, column =>
        {
            Assert.Null(column.FieldId);
            Assert.Null(column.PhysicalName);
            Assert.Null(column.Description);
        });
    }

    public sealed class Point
    {
        public int A { get; set; }

        public string? B { get; set; }
    }

    public sealed class Nested
    {
        public int Id { get; set; }

        public List<int>? Tags { get; set; }

        public Dictionary<string, long>? Props { get; set; }

        public Point? Where { get; set; }
    }

    [Fact]
    public async Task A_nested_column_is_one_column_with_the_complete_type()
    {
        using var lake = new TempLake();
        string full = lake.FullPath("data/nested/part-0001.parquet");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var rows = new[] { new Nested { Id = 1, Tags = [1, 2], Props = new Dictionary<string, long> { ["k"] = 1 }, Where = new Point { A = 1, B = "x" } } };
        using (FileStream stream = File.Create(full))
        {
            await ParquetSerializer.SerializeAsync(rows, stream);
        }

        List<ColumnRow> columns = await ColumnsOfFolderAsync(lake, "data/nested");

        // A list, a map, and a class, each OPTIONAL as Parquet.Net writes them, with REQUIRED elements.
        (string, string, bool)[] expected =
        [
            ("Id", "integer", false),
            ("Tags", "array<integer>", true),
            ("Props", "map<string,long>", true),
            ("Where", "struct<A:integer,B:string>", true),
        ];
        Assert.Equal(expected, columns.Select(column => (column.Name!, column.DataType!, column.IsNullable!.Value)));
    }

    [Fact]
    public async Task Between_files_of_the_same_time_the_last_name_gives_the_columns()
    {
        using var lake = new TempLake();
        await WriteAsync(lake, "data/tie/x.parquet", FolderLayout.Older, (new DataField<int?>("first"), new int?[] { 1 }));
        await WriteAsync(lake, "data/tie/y.parquet", FolderLayout.Older, (new DataField<string>("second"), new[] { "synthetic" }));
        List<ColumnRow> columns = await ColumnsOfFolderAsync(lake, "data/tie");
        Assert.Equal(["second"], columns.Select(column => column.Name));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_footer_of_the_columns_that_cannot_be_read_is_an_issue_and_the_table_has_no_columns(int level)
    {
        using TempLake lake = FolderLayout.Create();
        FolderLayout.CopyFile(lake, FolderLayout.OlderFile, "data/bad/good.parquet", FolderLayout.Older);
        lake.Write("data/bad/broken.parquet", FolderLayout.NotParquet);
        File.SetLastWriteTimeUtc(lake.FullPath("data/bad/broken.parquet"), FolderLayout.Newer);
        var storage = new LocalStorage(lake.Root);
        TableLocation bad = Assert.Single(await TableDiscovery.DiscoverFolderAsync(storage, "data/bad", CancellationToken.None));

        using PqaxDocument doc = await CollectAsync(storage, [bad], new CollectorOptions { Level = level });
        TableRow table = Assert.Single(doc.Summary.Tables);
        Assert.Equal(TableFormats.Parquet, table.Format);
        Assert.Empty(ColumnsOf(doc, table));
        IssueRow issue = Assert.Single(IssuesOf(doc, table));
        Assert.Equal(IssueCodes.FooterInvalid, issue.Code);
        Assert.NotNull(issue.Path);
        Assert.EndsWith("broken.parquet", issue.Path, StringComparison.Ordinal);
        Assert.Equal(2, table.FileCount);
        Assert.Equal(level, table.CollectedLevel);
        Assert.Equal(level == 2 ? 1 : 0, table.FooterCount);
    }

    public static TheoryData<int, string> ListingFailures => new()
    {
        { 403, IssueCodes.AccessDenied },
        { 401, IssueCodes.AccessDenied },
        { 500, IssueCodes.TableNotRead },
    };

    [Theory]
    [MemberData(nameof(ListingFailures))]
    public async Task A_listing_of_the_folder_that_fails_is_an_issue_at_the_summary_depth(int status, string code)
    {
        using TempLake lake = FolderLayout.Create();
        using OneLakeStorage discovery = FolderLayout.Adls(FolderLayout.Handler(lake));
        TableLocation raw = Assert.Single(await TableDiscovery.DiscoverFolderAsync(discovery, "data/raw", CancellationToken.None));

        var handler = new FakeOneLake(lake.Root, FolderLayout.Filesystem)
        {
            Override = request => request.RequestUri!.Query.Contains("resource=filesystem", StringComparison.Ordinal) ? new HttpResponseMessage((HttpStatusCode)status) : null,
        };
        using OneLakeStorage storage = FolderLayout.Adls(handler);
        using PqaxDocument doc = await CollectAsync(storage, [raw]);
        TableRow table = Assert.Single(doc.Summary.Tables);
        Assert.Equal(TableFormats.Parquet, table.Format);
        Assert.Equal("Summary", table.DetailDepth);
        Assert.Contains(IssuesOf(doc, table), issue => issue.Code == code);
        Assert.Empty(FilesOf(doc, table));
        Assert.DoesNotContain(handler.Calls, call => FolderLayout.ReadPath(call) != null);
        Assert.All(doc.Summary.Issues, issue => Assert.DoesNotContain(FolderLayout.Token, issue.Message ?? "", StringComparison.Ordinal));
    }

    // ---- the tables of a container ----

    [Fact]
    public async Task The_tables_of_a_folder_are_collected_with_their_format_and_one_source()
    {
        using TempLake lake = FolderLayout.Create();
        using OneLakeStorage storage = FolderLayout.Adls(FolderLayout.Handler(lake));
        IReadOnlyList<TableLocation> tables = await TableDiscovery.DiscoverFolderAsync(storage, "data", CancellationToken.None);
        using PqaxDocument doc = await CollectAsync(storage, tables);
        Assert.Empty(doc.Summary.Issues);

        SourceRow source = Assert.Single(doc.Summary.Sources);
        Assert.Equal(SourceKinds.StorageFolder, source.Kind);
        Assert.Equal("data", source.Name);
        Assert.Equal(FolderLayout.Container + "/data", source.Location);
        Assert.Null(source.Identifiers);

        Assert.Equal(FolderLayout.DataTables.Count, doc.Summary.Tables.Count);
        foreach ((string path, string[] ns, string format) in FolderLayout.DataTables)
        {
            TableRow table = TableAt(doc, path[(path.LastIndexOf('/') + 1)..], ns);
            Assert.Equal(source.Id, table.Source);
            Assert.Equal(format, table.Format);
            Assert.Equal(FolderLayout.Container + "/" + path, table.Location);
            Assert.Equal(2, table.CollectedLevel);
        }

        // The Delta table at depth 1 and its copy at depth 2 are collected as the fixture table.
        CollectorTests.AssertComplete(doc, Fixture.CheckpointTable);
        TableRow copy = TableAt(doc, "sales", "eu");
        TableRow sales = TableAt(doc, "sales");
        foreach (TableRow table in new[] { sales, copy })
        {
            Assert.Equal(TableFormats.Delta, table.Format);
            Assert.Equal(Fixture.CheckpointTable.Files.Count, table.FileCount);
            Assert.Equal(Fixture.CheckpointTable.Files.Sum(file => Dump.Long(file["size"])), table.FileSize);
            Assert.NotNull(table.TableId);
            Assert.NotNull(table.SnapshotId);
            Assert.NotNull(table.Delta);
            Assert.NotNull(table.CommitCount);
            Assert.NotNull(table.MetadataFileCount);
        }

        Assert.Equal(sales.TableId, copy.TableId);
        Assert.Equal(sales.SnapshotId, copy.SnapshotId);
        Assert.Equal(sales.RowCount, copy.RowCount);
        Assert.Equal(ColumnsOf(doc, sales).Select(column => (column.Name, column.DataType)), ColumnsOf(doc, copy).Select(column => (column.Name, column.DataType)));

        // The table at depth 4, with a log and no file.
        TableRow deep = TableAt(doc, "deep", "a", "b", "c");
        Assert.Equal(TableFormats.Delta, deep.Format);
        Assert.Equal(LogLines.TableId, deep.TableId);
        Assert.Equal(0, deep.FileCount);

        // The folders of Parquet files of the layout are copies of the fixture files.
        TableRow upper = TableAt(doc, "upper");
        Assert.Equal(1, upper.FileCount);
        Assert.Equal(Dump.Long(OlderDump["footer"]!["numRows"]), upper.RowCount);
        Assert.Equal(["DATA.PARQUET"], FilesOf(doc, upper).Select(file => file.Path));
        TableRow inner = TableAt(doc, "inner", "mixed");
        Assert.Equal(["y.parquet"], FilesOf(doc, inner).Select(file => file.Path));
        AssertListingTotals(doc, TableAt(doc, "raw"));
    }
}
