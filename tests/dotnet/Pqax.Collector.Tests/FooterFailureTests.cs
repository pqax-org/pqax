using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Pqax.Collector.Tests;

/// <summary>
/// A footer that cannot be read becomes an issue of its file, with the code of the failure, and the footer counts as not
/// read: the file keeps what the log says, and footerCount and the totals of level 2 leave it out (README of the
/// collector, Footer read failures). The failures come from the fake OneLake endpoint, a local file, a storage that
/// throws, bytes that are not a readable footer, and a log that gives no size or a path outside the table.
/// </summary>
public class FooterFailureTests
{
    private const string Workspace = "Test Workspace";
    private const string Token = "test-token-not-a-secret";
    private const string SynthTable = "Synth.Lakehouse/Tables/files";
    private static readonly Uri Endpoint = new("https://onelake.test");

    private static DumpTable Sales => Fixture.CheckpointTable;

    private static long SizeOf(string path) => new FileInfo(Fixture.LocalPath(path)).Length;

    private static List<string> DataFilesOf(DumpTable table) =>
        Fixture.DataFiles().Where(path => path.StartsWith(table.TablePath + "/", StringComparison.Ordinal)).ToList();

    /// <summary>The largest data file of the lakehouse table: the example of the README.</summary>
    private static string Largest => DataFilesOf(Sales).OrderByDescending(SizeOf).First();

    /// <summary>The data file of the lakehouse table that the VORDER tag of the log marks as V-Ordered.</summary>
    private static string TaggedVOrdered => DataFilesOf(Sales).Single(path => VOrderTag(Fixture.DumpFileOfSize(SizeOf(path)).File) == true);

    /// <summary>What the VORDER tag of a file of the dump says: true, false, or nothing when the file has no such tag.</summary>
    private static bool? VOrderTag(JsonObject file) => Dump.Text(file["tags"]?["VORDER"]) switch
    {
        string tag when tag.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
        string tag when tag.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        _ => null,
    };

    private static long ChunkSum(JsonObject file, string field) =>
        Dump.RowGroups(file).Sum(group => Enumerable.Range(0, Dump.ColumnCount(group)).Sum(i => Dump.At(group, field, i) ?? 0));

    /// <summary>
    /// Asserts a collection of the fixture in which the footer of one file of the lakehouse table was not read: one issue,
    /// with the code and the path of the file; the file with what the log says and nothing of its footer; footerCount and
    /// the totals of level 2 over the other files; the other files read; the warehouse table complete.
    /// </summary>
    private static void AssertFooterNotRead(byte[] data, string target, string code)
    {
        Fixture.AssertValid(data);
        using PqaxDocument doc = Fixture.Open(data);
        long size = SizeOf(target);
        (DumpTable dump, JsonObject failed) = Fixture.DumpFileOfSize(size);
        Assert.Equal(Sales.Id, dump.Id);
        TableRow table = Fixture.TableOf(doc, dump);
        List<FileRow> files = doc.ReadFiles().Where(row => row.Table == table.Id).ToList();
        FileRow file = Assert.Single(files, row => row.FileSize == size);

        IssueRow issue = Assert.Single(doc.Summary.Issues, row => row.Table == table.Id);
        Assert.Equal(code, issue.Code);
        Assert.Equal(file.Path, issue.Path);
        Assert.EndsWith(target[(target.LastIndexOf('/') + 1)..], issue.Path, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(issue.Message));
        Assert.DoesNotContain(Token, issue.Message, StringComparison.Ordinal);

        // footerCount and the totals of level 2 cover the other footers only.
        List<JsonObject> read = dump.Files.Where(other => Dump.Long(other["size"]) != size).ToList();
        Assert.Equal(dump.Files.Count - 1, read.Count);
        Assert.Equal(read.Count, table.FooterCount);
        Assert.Equal(read.Sum(other => Dump.RowGroups(other).Count), table.RowGroupCount);
        Assert.Equal(read.Sum(other => ChunkSum(other, "compressed")), table.CompressedSize);
        Assert.Equal(read.Sum(other => ChunkSum(other, "uncompressed")), table.UncompressedSize);
        Assert.Equal(read.Sum(other => Dump.Long(other["footer"]!["footerSize"])), table.FooterSize);
        Assert.NotNull(table.RowGroupRowCounts);
        Assert.Equal(read.Sum(other => Dump.RowGroups(other).Count), table.RowGroupRowCounts.Count);
        Assert.Equal(read.Count, doc.Summary.Writers.Where(writer => writer.Table == table.Id).Sum(writer => writer.FileCount));
        Assert.Equal(2, table.CollectedLevel);
        Assert.Equal("Column chunks", table.DetailDepth);
        Assert.Equal(dump.Files.Count, table.FileCount);
        Assert.Equal(dump.Files.Sum(other => Dump.Long(other["size"])), table.FileSize);
        bool?[] vordered = dump.Files.Select(other => Dump.Long(other["size"]) == size ? VOrderTag(other) : Dump.IsVOrdered(other)).ToArray();
        long? vorderedCount = vordered.All(value => value != null) ? vordered.Count(value => value == true) : null;
        Assert.Equal(vorderedCount, table.VOrderedFileCount);

        // The file keeps what the log says, and nothing of its footer.
        Assert.Equal(Dump.Time(failed["modificationTime"]), file.ModifiedAt);
        Assert.Equal(Dump.NullableLong(failed["numRecords"]), file.RowCount);
        Assert.Equal(Dump.NullableLong(failed["deletedRows"]) ?? 0, file.DeletedRowCount);
        Assert.Equal(Dump.NullableLong(failed["deletionVectorSize"]), file.DeletionVectorSize);
        Assert.Equal(VOrderTag(failed), file.IsVOrdered);
        Assert.Null(file.FooterSize);
        Assert.Null(file.RowGroupCount);
        Assert.Null(file.CompressedSize);
        Assert.Null(file.UncompressedSize);
        Assert.Null(file.PhysicalSchema);
        Assert.Null(file.Writer);
        Assert.DoesNotContain(doc.ReadRowGroups(), group => group.File == file.Id);

        // The other files are read, and so is the other table.
        Assert.All(files.Where(row => row.Id != file.Id), row => Assert.NotNull(row.FooterSize));
        CollectorTests.AssertComplete(doc, Fixture.JsonOnlyTable);
    }

    // ---- OneLake ----

    private static OneLakeStorage OneLake(FakeOneLake handler, int retries = 0, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler);
        if (timeout != null)
        {
            http.Timeout = timeout.Value;
        }

        return new OneLakeStorage(Workspace, _ => Task.FromResult(Token), new OneLakeStorageOptions { Endpoint = Endpoint, HttpClient = http, MaxRetries = retries });
    }

    /// <summary>Whether a request or a call of the fake endpoint reads a range of a file of the fixture.</summary>
    private static bool ReadsFile(string pathAndQuery, bool hasRange, string path) =>
        hasRange && Uri.UnescapeDataString(pathAndQuery) == "/" + Workspace + "/" + path;

    /// <summary>An answer of the fake endpoint that fails every read of one file and lets every other request through.</summary>
    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>> Failing(string target, string failure) =>
        (request, cancellation) => ReadsFile(request.RequestUri!.AbsolutePath, request.Headers.Range != null, target)
            ? Fail(request, target, failure, cancellation)
            : Task.FromResult<HttpResponseMessage?>(null);

    private static Task<HttpResponseMessage?> Fail(HttpRequestMessage request, string target, string failure, CancellationToken cancellation)
    {
        switch (failure)
        {
            case "timeout":
                return FakeOneLake.Hang(request, cancellation);
            case "broken connection":
                return Task.FromException<HttpResponseMessage?>(new HttpRequestException("The connection was reset."));
            case "broken body":
                return Answer(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new BrokenContent() });
            case "short":
            {
                // Half of the range asked for, as a file shorter than the log says gives.
                RangeItemHeaderValue range = request.Headers.Range!.Ranges.Single();
                byte[] file = File.ReadAllBytes(Fixture.LocalPath(target));
                int from = (int)range.From!.Value;
                int length = (int)(range.To!.Value - from + 1);
                return Answer(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(file, from, length / 2) });
            }

            case "503 after a retry":
                return Answer(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            default:
                return Answer(new HttpResponseMessage((HttpStatusCode)int.Parse(failure, CultureInfo.InvariantCulture)));
        }
    }

    private static Task<HttpResponseMessage?> Answer(HttpResponseMessage response) => Task.FromResult<HttpResponseMessage?>(response);

    public static TheoryData<string, string> HttpFailures => new()
    {
        { "401", IssueCodes.AccessDenied },
        { "403", IssueCodes.AccessDenied },
        { "404", IssueCodes.FooterNotRead },
        { "400", IssueCodes.FooterNotRead },
        { "500", IssueCodes.FooterNotRead },
        { "503 after a retry", IssueCodes.FooterNotRead },
        { "timeout", IssueCodes.FooterNotRead },
        { "broken connection", IssueCodes.FooterNotRead },
        { "broken body", IssueCodes.FooterNotRead },
        { "416", IssueCodes.FooterInvalid },
        { "short", IssueCodes.FooterInvalid },
    };

    [Theory]
    [MemberData(nameof(HttpFailures))]
    public async Task A_footer_read_that_fails_on_onelake_becomes_an_issue_with_the_code_of_the_failure(string failure, string code)
    {
        string target = Largest;
        bool retried = failure == "503 after a retry";
        var handler = new FakeOneLake(Fixture.Folder, Workspace) { Answer = Failing(target, failure) };
        using OneLakeStorage storage = OneLake(handler, retries: retried ? 1 : 0, timeout: failure == "timeout" ? TimeSpan.FromSeconds(2) : null);
        Collected result = await Fixture.CollectAsync(storage: storage);
        AssertFooterNotRead(result.Data, target, code);
        Assert.Equal(retried ? 2 : 1, handler.Calls.Count(call => ReadsFile(call.PathAndQuery, call.Range != null, target)));
    }

    [Fact]
    public async Task A_403_on_the_largest_file_of_the_lakehouse_table_takes_its_footer_out_of_the_totals()
    {
        var handler = new FakeOneLake(Fixture.Folder, Workspace) { Answer = Failing(Largest, "403") };
        using OneLakeStorage storage = OneLake(handler);
        Collected result = await Fixture.CollectAsync(storage: storage);
        AssertFooterNotRead(result.Data, Largest, IssueCodes.AccessDenied);
        using PqaxDocument doc = Fixture.Open(result.Data);
        TableRow table = Fixture.TableOf(doc, Sales);
        Assert.Equal(2, table.FooterCount);
        Assert.Equal(153328 - 80269, table.CompressedSize);
        Assert.Equal(10 - 5, table.RowGroupCount);
    }

    [Fact]
    public async Task A_file_whose_footer_is_not_read_keeps_the_v_order_of_its_log_tag()
    {
        string target = TaggedVOrdered;
        var handler = new FakeOneLake(Fixture.Folder, Workspace) { Answer = Failing(target, "403") };
        using OneLakeStorage storage = OneLake(handler);
        Collected result = await Fixture.CollectAsync(storage: storage);
        AssertFooterNotRead(result.Data, target, IssueCodes.AccessDenied);
        using PqaxDocument doc = Fixture.Open(result.Data);
        TableRow table = Fixture.TableOf(doc, Sales);
        FileRow file = Assert.Single(doc.ReadFiles(), row => row.Table == table.Id && row.FileSize == SizeOf(target));
        Assert.True(file.IsVOrdered);
        Assert.NotNull(file.Partition);
    }

    // ---- a local file, and a storage that throws ----

    [Fact]
    public async Task A_local_file_that_cannot_be_opened_becomes_footer_not_read()
    {
        using TempLake lake = TempLake.CopyOfFixture();
        string full = lake.FullPath(Largest);
        using (new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (!KeepsReadersOut(full))
            {
                // A platform whose file locks do not keep a reader out: there is no local error to cause.
                return;
            }

            Collected result = await Fixture.CollectAsync(storage: new LocalStorage(lake.Root));
            AssertFooterNotRead(result.Data, Largest, IssueCodes.FooterNotRead);
        }
    }

    private static bool KeepsReadersOut(string path)
    {
        try
        {
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    [Theory]
    [InlineData("another exception")]
    [InlineData("a cancellation that the caller did not ask for")]
    public async Task Any_other_exception_of_a_footer_read_becomes_footer_not_read(string failure)
    {
        string target = Largest;
        var storage = new TestStorage(new LocalStorage(Fixture.Folder))
        {
            Fail = (kind, path) => kind != "read" || path != target
                ? null
                : failure == "another exception"
                    ? (Exception)new InvalidOperationException("Something that no storage expects.")
                    : new TaskCanceledException("A request that the storage gave up."),
        };
        Collected result = await Fixture.CollectAsync(storage: storage);
        AssertFooterNotRead(result.Data, target, IssueCodes.FooterNotRead);
    }

    // ---- bytes ----

    [Theory]
    [InlineData("not Thrift", IssueCodes.FooterInvalid)]
    [InlineData("column chunks without metadata", IssueCodes.FooterColumnsEncrypted)]
    public async Task A_footer_that_the_parser_refuses_or_whose_column_chunks_have_no_metadata_gets_its_code(string fault, string code)
    {
        string target = Largest;
        byte[] original = File.ReadAllBytes(Fixture.LocalPath(target));
        byte[] changed;
        if (fault == "not Thrift")
        {
            changed = (byte[])original.Clone();
            int length = BinaryPrimitives.ReadInt32LittleEndian(changed.AsSpan(changed.Length - 8, 4));
            changed.AsSpan(changed.Length - 8 - length, length).Fill(0xFF);
        }
        else
        {
            byte[] footer = ParquetFiles.FooterWithoutChunkMetadata();
            Assert.False(ParquetFooter.Parse(footer).RowGroups.Single().Columns.Single().HasMetadata);
            changed = ParquetFiles.EndingWith(footer, original.Length);
        }

        var storage = new TestStorage(new LocalStorage(Fixture.Folder)) { Content = path => path == target ? changed : null };
        Collected result = await Fixture.CollectAsync(storage: storage);
        AssertFooterNotRead(result.Data, target, code);
    }

    // ---- what the log says ----

    /// <summary>
    /// A table with two files: <c>good.parquet</c>, a copy of a data file of the fixture whose footer is read, and the
    /// file of a test, with its add action.
    /// </summary>
    private static TempLake TableWith(JsonObject add)
    {
        string source = DataFilesOf(Fixture.JsonOnlyTable).OrderBy(SizeOf).First();
        byte[] good = File.ReadAllBytes(Fixture.LocalPath(source));
        long rows = Dump.Long(Fixture.DumpFileOfSize(good.Length).File["footer"]!["numRows"]);
        var lake = new TempLake();
        lake.Write(SynthTable + "/good.parquet", good);
        lake.Commit(SynthTable, 0, LogLines.Protocol(), LogLines.Metadata(), LogLines.Add("good.parquet", good.Length, rows), add, LogLines.CommitInfo(0));
        return lake;
    }

    /// <summary>
    /// Asserts the footprint of such a table: one issue, with the code and the path of the file of the test, whose row
    /// keeps what the log says and nothing of a footer, and footerCount and the totals of level 2 from good.parquet alone.
    /// </summary>
    private static void AssertNotReadInTable(Collected result, string path, string code)
    {
        Fixture.AssertValid(result.Data);
        using PqaxDocument doc = Fixture.Open(result.Data);
        TableRow table = Assert.Single(doc.Summary.Tables);
        IssueRow issue = Assert.Single(doc.Summary.Issues);
        Assert.Equal(code, issue.Code);
        Assert.Equal(path, issue.Path);
        List<FileRow> files = doc.ReadFiles().ToList();
        Assert.Equal(2, files.Count);
        FileRow file = Assert.Single(files, row => row.Path == path);
        Assert.Equal(10, file.RowCount);
        Assert.Null(file.FooterSize);
        Assert.Null(file.RowGroupCount);
        Assert.Null(file.CompressedSize);
        Assert.Null(file.UncompressedSize);
        FileRow good = Assert.Single(files, row => row.Path == "good.parquet");
        Assert.NotNull(good.FooterSize);
        Assert.Equal(1, table.FooterCount);
        Assert.Equal(good.FooterSize, table.FooterSize);
        Assert.Equal(good.RowGroupCount, table.RowGroupCount);
        Assert.Equal(good.CompressedSize, table.CompressedSize);
        Assert.Equal(good.UncompressedSize, table.UncompressedSize);
        Assert.Equal(2, table.CollectedLevel);
    }

    /// <summary>The files outside the Delta log that the collection read bytes of.</summary>
    private static List<string> DataReads(TestStorage storage) =>
        storage.Requests.Where(request => request.Kind == "read" && !request.Path.Contains("/_delta_log/", StringComparison.Ordinal))
            .Select(request => request.Path).ToList();

    [Fact]
    public async Task A_file_without_a_size_in_the_log_becomes_footer_not_read_with_no_request()
    {
        using TempLake lake = TableWith(LogLines.Add("unsized.parquet", null, 10));
        var storage = new TestStorage(new LocalStorage(lake.Root));
        Collected result = await Fixture.CollectAsync(storage: storage);
        AssertNotReadInTable(result, "unsized.parquet", IssueCodes.FooterNotRead);
        Assert.Equal([SynthTable + "/good.parquet"], DataReads(storage));
    }

    [Theory]
    [InlineData("abfss://Elsewhere@onelake.test/Other.Lakehouse/Files/outside.parquet")]
    [InlineData("/elsewhere/outside.parquet")]
    public async Task A_file_outside_the_table_becomes_file_outside_table_with_no_request(string path)
    {
        using TempLake lake = TableWith(LogLines.Add(path, 5000, 10));
        var storage = new TestStorage(new LocalStorage(lake.Root));
        Collected result = await Fixture.CollectAsync(storage: storage);
        AssertNotReadInTable(result, path, IssueCodes.FileOutsideTable);
        Assert.Equal([SynthTable + "/good.parquet"], DataReads(storage));
    }

    [Fact]
    public async Task A_footer_over_64_mb_becomes_footer_too_large_after_one_request()
    {
        const long size = 100L * 1024 * 1024;
        byte[] tail = ParquetFiles.Tail(FooterReader.MaxFooterLength + 1);
        using TempLake lake = TableWith(LogLines.Add("large.parquet", size, 10));
        var storage = new TestStorage(new LocalStorage(lake.Root))
        {
            Range = (path, offset, length) => path == SynthTable + "/large.parquet" ? VirtualFileStorage.Slice(size, tail, offset, length) : null,
        };
        Collected result = await Fixture.CollectAsync(storage: storage);
        AssertNotReadInTable(result, "large.parquet", IssueCodes.FooterTooLarge);
        StorageRequest read = Assert.Single(storage.Requests, request => request.Path == SynthTable + "/large.parquet");
        Assert.Equal(size, read.Offset + read.Length);
        Assert.True(read.Length <= FooterReader.DefaultTailBytes, $"{read.Length} bytes");
    }
}
