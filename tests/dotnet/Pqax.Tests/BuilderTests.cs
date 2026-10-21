using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pqax.Tests;

/// <summary>
/// The writer, FootprintBuilder: round trips of the samples through From, a footprint built from nothing, the layout
/// of the parts, and a table of 600,000 rows in parts of 250,000 (plan 4.3 and 5).
/// </summary>
public class BuilderTests
{
    private static readonly Regex PartName = new("^[A-Za-z0-9._-]+$");

    public static TheoryData<string> Samples => Kit.Data(Kit.ExpectedNames());

    private static byte[] Write(FootprintBuilder builder)
    {
        using var stream = new MemoryStream();
        builder.Write(stream);
        return stream.ToArray();
    }

    private static PqaxDocument Open(byte[] data) => PqaxDocument.Open(new MemoryStream(data));

    private static void AssertValid(byte[] data)
    {
        using var stream = new MemoryStream(data);
        ValidationResult result = PqaxValidator.Validate(stream);
        Assert.Empty(result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
        Assert.True(result.IsValid);
    }

    private static void AssertExtraFieldsEqual(IReadOnlyDictionary<string, IReadOnlyList<JsonElement?>> expected,
        IReadOnlyDictionary<string, IReadOnlyList<JsonElement?>> actual, string table)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach ((string field, IReadOnlyList<JsonElement?> values) in expected)
        {
            Assert.Equal(values.Select(Kit.Canonical), actual[field].Select(Kit.Canonical));
        }
    }

    /// <summary>Every table of two documents is equal, row by row, with the fields that this version does not know.</summary>
    private static void AssertSameTables(PqaxDocument expected, PqaxDocument actual)
    {
        Assert.Equal(expected.Summary.Sources.Rows, actual.Summary.Sources.Rows);
        Assert.Equal(expected.Summary.Tables.Rows, actual.Summary.Tables.Rows);
        Assert.Equal(expected.Summary.Columns.Rows, actual.Summary.Columns.Rows);
        Assert.Equal(expected.Summary.PartitionSpecs.Rows, actual.Summary.PartitionSpecs.Rows);
        Assert.Equal(expected.Summary.PartitionFields.Rows, actual.Summary.PartitionFields.Rows);
        Assert.Equal(expected.Summary.SortOrders.Rows, actual.Summary.SortOrders.Rows);
        Assert.Equal(expected.Summary.SortFields.Rows, actual.Summary.SortFields.Rows);
        Assert.Equal(expected.Summary.PhysicalSchemas.Rows, actual.Summary.PhysicalSchemas.Rows);
        Assert.Equal(expected.Summary.PhysicalColumns.Rows, actual.Summary.PhysicalColumns.Rows);
        Assert.Equal(expected.Summary.Writers.Rows, actual.Summary.Writers.Rows);
        Assert.Equal(expected.Summary.Operations.Rows, actual.Summary.Operations.Rows);
        Assert.Equal(expected.Summary.Issues.Rows, actual.Summary.Issues.Rows);
        Assert.Equal(expected.Summary.ModelBindings.Rows, actual.Summary.ModelBindings.Rows);
        Assert.Equal(expected.Summary.TableBindings.Rows, actual.Summary.TableBindings.Rows);
        Assert.Equal(expected.Summary.ColumnBindings.Rows, actual.Summary.ColumnBindings.Rows);
        Assert.Equal(expected.ReadPartitions().Rows, actual.ReadPartitions().Rows);
        Assert.Equal(expected.ReadFiles().Rows, actual.ReadFiles().Rows);
        Assert.Equal(expected.ReadRowGroups().Rows, actual.ReadRowGroups().Rows);
        Assert.Equal(expected.ReadColumnChunks().Rows, actual.ReadColumnChunks().Rows);
        Assert.Equal(expected.ReadDeleteFiles().Rows, actual.ReadDeleteFiles().Rows);
        Assert.Equal(expected.ReadCommits().Rows, actual.ReadCommits().Rows);
        foreach (string table in Kit.FormatTables)
        {
            AssertExtraFieldsEqual(Kit.Typed(expected, table).Extra, Kit.Typed(actual, table).Extra, table);
        }

        foreach (string table in Kit.StatedTables(expected.Manifest).Where(name => !Kit.FormatTables.Contains(name)))
        {
            RawTable before = expected.ReadRaw(table);
            RawTable after = actual.ReadRaw(table);
            Assert.Equal(before.Count, after.Count);
            AssertExtraFieldsEqual(before.Fields, after.Fields, table);
        }
    }

    // ---- round trips ----

    [Theory]
    [MemberData(nameof(Samples))]
    public void From_writes_a_file_with_the_same_tables_that_is_valid(string name)
    {
        JsonObject expected = Kit.Expected(name);
        using PqaxDocument original = Kit.OpenSample(expected);
        byte[] data = Write(FootprintBuilder.From(original));
        using PqaxDocument copy = Open(data);
        Assert.Equal("pqax", copy.Manifest.Format);
        Assert.Equal("0.2", copy.Manifest.FormatVersion);
        Assert.Equal("summary.json", copy.Manifest.Parts[0].Name);
        Assert.Equal(original.Manifest.Collection, copy.Manifest.Collection);
        Assert.Equal(original.Manifest.ObfuscatorDictionaryId, copy.Manifest.ObfuscatorDictionaryId);
        Assert.Equal(original.Manifest.Extensions.Keys.Order(StringComparer.Ordinal), copy.Manifest.Extensions.Keys.Order(StringComparer.Ordinal));
        foreach ((string member, JsonElement value) in original.Manifest.Extensions)
        {
            Assert.Equal(Kit.Canonical(value), Kit.Canonical(copy.Manifest.Extensions[member]));
        }

        foreach (string table in Kit.StatedTables(original.Manifest))
        {
            Assert.True(original.RowCount(table) == copy.RowCount(table), table);
        }

        Assert.Equal(Kit.StatedTables(original.Manifest).Order(StringComparer.Ordinal), Kit.StatedTables(copy.Manifest).Order(StringComparer.Ordinal));
        AssertSameTables(original, copy);
        Assert.Empty(Kit.Differences(Kit.Shape(copy), expected, Kit.ShapeKeys.Where(key => key is not "parts" and not "formatVersion")));
        AssertValid(data);
        Assert.Empty(SchemaCheck.Errors(data));
    }

    [Fact]
    public void Unknown_fields_survive_two_round_trips()
    {
        using PqaxDocument original = PqaxDocument.Open(Path.Combine(Kit.Samples, "extensions.pqax"));
        using PqaxDocument first = Open(Write(FootprintBuilder.From(original)));
        using PqaxDocument second = Open(Write(FootprintBuilder.From(first)));
        foreach (PqaxDocument doc in new[] { first, second })
        {
            Assert.Equal("[7,9]", Kit.Canonical(new JsonArray(doc.Summary.Tables.ExtraFields["acme_score"]
                .Select(value => JsonNode.Parse(value!.Value.GetRawText())).ToArray())));
            Assert.All(doc.ReadFiles().ExtraFields["acme_tier"], value => Assert.Equal("hot", value!.Value.GetString()));
            Assert.Equal(7, doc.Manifest.Extensions["acme_build"].GetProperty("number").GetInt32());
            Assert.Equal(83049, doc.Summary.Tables[0].FileSizes!.Extensions!["ninetiethPercentile"].GetInt64());
            Assert.Equal(2, doc.RowCount("acme_reviews"));
            Assert.Equal(["a", "b", "c"], doc.ReadRaw("acme_details").Fields["label"].Select(value => value!.Value.GetString()));
            Assert.All(doc.ReadColumnChunks(), chunk => Assert.Null(chunk.Encodings));
            AssertSameTables(original, doc);
        }
    }

    [Fact]
    public void A_smaller_part_size_splits_the_detail_tables()
    {
        using PqaxDocument original = PqaxDocument.Open(Path.Combine(Kit.Samples, "sample.pqax"));
        FootprintBuilder builder = FootprintBuilder.From(original);
        builder.Options.RowsPerPart = 20;
        byte[] data = Write(builder);
        using PqaxDocument copy = Open(data);
        Assert.Equal([20, 20, 12], copy.Manifest.Parts.Where(part => part.Tables.ContainsKey("columnChunks")).Select(part => part.Tables["columnChunks"]));
        Assert.All(copy.Manifest.Parts.Skip(1), part =>
        {
            Assert.Single(part.Tables);
            Assert.True(part.Tables.Values.Sum() <= 20);
        });
        AssertSameTables(original, copy);
        AssertValid(data);
    }

    [Fact]
    public void The_same_footprint_gives_the_same_bytes()
    {
        using PqaxDocument original = PqaxDocument.Open(Path.Combine(Kit.Samples, "sample.pqax"));
        FootprintBuilder builder = FootprintBuilder.From(original);
        byte[] first = Write(builder);
        byte[] second = Write(builder);
        byte[] third = Write(FootprintBuilder.From(original));
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        using var file = new TemporaryFile();
        builder.Write(file.Path);
        Assert.Equal(first, File.ReadAllBytes(file.Path));
    }

    [Fact]
    public void The_written_json_is_compact_utf8_with_no_byte_order_mark()
    {
        using PqaxDocument original = PqaxDocument.Open(Path.Combine(Kit.Samples, "extensions.pqax"));
        byte[] data = Write(FootprintBuilder.From(original));
        var names = new List<string>();
        foreach ((string name, long offset, int method, long _) in Kit.CentralDirectory(data).Entries)
        {
            names.Add(name);
            Assert.True(method is 0 or 8, $"{name} has method {method}");
            Assert.Matches(PartName, name);
            Assert.Equal(0, BitConverter.ToUInt16(data, (int)offset + 6) & 1);
        }

        Assert.Contains("manifest.json", names);
        Assert.Contains("summary.json", names);
        Assert.Equal(names.Count, names.Select(name => name.ToLowerInvariant()).Distinct().Count());
        foreach ((string name, byte[] content) in Kit.Entries(data))
        {
            Assert.False(content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF, $"{name} has a byte order mark");
            string text = new UTF8Encoding(false, true).GetString(content);
            JsonNode node = JsonNode.Parse(text)!;
            Assert.IsType<JsonObject>(node);
            Assert.True(text == Compact(node), $"{name} has whitespace between tokens");
        }
    }

    private static string Compact(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // ---- a footprint built from nothing ----

    private sealed class Everything
    {
        public required FootprintBuilder Builder { get; init; }
        public List<SourceRow> Sources { get; } = [];
        public List<TableRow> Tables { get; } = [];
        public List<ColumnRow> Columns { get; } = [];
        public List<PartitionSpecRow> PartitionSpecs { get; } = [];
        public List<PartitionFieldRow> PartitionFields { get; } = [];
        public List<SortOrderRow> SortOrders { get; } = [];
        public List<SortFieldRow> SortFields { get; } = [];
        public List<PhysicalSchemaRow> PhysicalSchemas { get; } = [];
        public List<PhysicalColumnRow> PhysicalColumns { get; } = [];
        public List<WriterRow> Writers { get; } = [];
        public List<OperationRow> Operations { get; } = [];
        public List<IssueRow> Issues { get; } = [];
        public List<ModelBindingRow> ModelBindings { get; } = [];
        public List<TableBindingRow> TableBindings { get; } = [];
        public List<ColumnBindingRow> ColumnBindings { get; } = [];
        public List<PartitionRow> Partitions { get; } = [];
        public List<FileRow> Files { get; } = [];
        public List<RowGroupRow> RowGroups { get; } = [];
        public List<ColumnChunkRow> ColumnChunks { get; } = [];
        public List<DeleteFileRow> DeleteFiles { get; } = [];
        public List<CommitRow> Commits { get; } = [];
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second, int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, TimeSpan.Zero);

    /// <summary>A small footprint with rows in all 21 tables, totals that agree with the detail, and every kind of value.</summary>
    private static Everything BuildEverything()
    {
        var collection = new CollectionInfo
        {
            CollectorApp = "pqax-tests", CollectorAppVersion = "0.1", CollectorLib = "Pqax", CollectorLibVersion = "0.1",
            StartedAt = Utc(2026, 10, 7, 8, 15, 30, 123), CompletedAt = Utc(2026, 10, 7, 8, 16, 0, 123), Level = 2,
            IncludesDataValues = true, IncludesPartitionValues = true, RequestCount = 12, BytesRead = 34567,
        };
        var all = new Everything { Builder = new FootprintBuilder(collection) };
        FootprintBuilder builder = all.Builder;

        var source = new SourceRow { Kind = "Storage folder", Name = "folder one", Location = "abfss://example/one",
            Identifiers = new Dictionary<string, string> { ["workspaceId"] = "w-1", ["itemId"] = "i-1" } };
        Assert.Equal(1, builder.AddSource(source));
        all.Sources.Add(source);

        long[,] chunkSize = new long[4, 3];
        for (int group = 1; group <= 3; group++)
        {
            for (int physical = 1; physical <= 2; physical++)
            {
                chunkSize[group, physical] = (100 * group) + (10 * physical);
            }
        }

        long ColumnSum(int physical) => chunkSize[1, physical] + chunkSize[2, physical] + chunkSize[3, physical];
        long GroupSum(int group) => chunkSize[group, 1] + chunkSize[group, 2];
        long totalChunks = ColumnSum(1) + ColumnSum(2);
        var distribution = new Distribution { Count = 2, Sum = 3000, Minimum = 1000, FirstQuartile = 1000, Median = 2000, ThirdQuartile = 2000, Maximum = 2000 };

        var tableOne = new TableRow
        {
            Source = 1, Namespace = ["dbo"], Name = "trips", Location = "Tables/trips", Format = "Iceberg", TableId = "t-1",
            Description = "the trips", CreatedAt = Utc(2025, 1, 2, 3, 4, 5), Properties = new Dictionary<string, string> { ["write.format.default"] = "parquet" },
            SnapshotId = "42", SnapshotTimestamp = Utc(2026, 1, 2, 3, 4, 5, 6),
            Iceberg = new IcebergDetails { FormatVersion = 2, CurrentSchemaId = 0, SchemaCount = 1, SnapshotCount = 3, RefCount = 1, ManifestCount = 2, ManifestSize = 9000, StatisticsFileCount = 0 },
            CollectedLevel = 2, DetailDepth = "Column chunks", FileCount = 2, FileSize = 3000, RowCount = 150, DeletedRowCount = 5,
            LastModifiedAt = Utc(2026, 1, 2, 3, 0, 0), VOrderedFileCount = 0, PartitionCount = 1, DeleteFileCount = 1, DeleteFileSize = 100,
            CommitCount = 2, CommitsRead = 2, OldestCommitAt = Utc(2026, 1, 1, 0, 0, 0), NonLiveFileCount = 2, NonLiveFileSize = 700,
            NonLiveOldestRemovedAt = Utc(2025, 12, 1, 0, 0, 0, 250), NonLiveNewestRemovedAt = Utc(2025, 12, 31, 23, 59, 59),
            UnreferencedFileCount = 3, UnreferencedFileSize = 900, MetadataFileCount = 4, MetadataSize = 4096, FooterCount = 2, RowGroupCount = 3, CompressedSize = totalChunks,
            UncompressedSize = 2 * totalChunks, FooterSize = 500, StaleColumnSize = 0, ColumnTotalsSource = "Footers", FileSizes = distribution,
            RowGroupRowCounts = new Distribution { Count = 3, Sum = 150, Minimum = 40, FirstQuartile = 40, Median = 50, ThirdQuartile = 60, Maximum = 60 },
            PartitionSizes = distribution,
        };
        Assert.Equal(1, builder.AddTable(tableOne));
        tableOne.Id = 1;
        all.Tables.Add(tableOne);
        var tableTwo = new TableRow
        {
            Source = 1, Namespace = [], Name = "keys", Format = "Delta",
            Delta = new DeltaDetails { MinReaderVersion = 1, MinWriterVersion = 2, ReaderFeatures = [], WriterFeatures = [], CheckpointVersion = 3, CommitsAfterCheckpoint = 1, Domains = [] },
            Properties = new Dictionary<string, string>(), CollectedLevel = 1, DetailDepth = "Summary", FileCount = 10, FileSize = 99999,
            RowCount = 1000, ClusteringColumns = [3],
        };
        Assert.Equal(2, builder.AddTable(tableTwo));
        tableTwo.Id = 2;
        all.Tables.Add(tableTwo);

        ColumnRow[] columns =
        [
            new() { Table = 1, FieldId = 1, Name = "id", DataType = "long", IsNullable = false, IsPartitionColumn = false, CompressedSize = ColumnSum(1),
                UncompressedSize = 2 * ColumnSum(1), ValueCount = 150, NullCount = 0, ChunkCount = 3, Codecs = ["ZSTD"], Encodings = ["PLAIN"] },
            new() { Table = 1, FieldId = 2, Name = "amount", DataType = "decimal(18,2)", IsNullable = true, CompressedSize = ColumnSum(2), ChunkCount = 3,
                DictionaryChunkCount = 3, FallbackChunkCount = 0, DictionaryPageSize = 30, LargestChunkDistinctCount = 9, DistinctCount = 12,
                DistinctCountIsEstimate = true, DistinctCountSource = "Scan", UnencodedByteArrayDataBytes = 77, FileCountWithoutColumn = 0 },
            new() { Table = 2, Name = "key", DataType = "string", PhysicalName = "col-1", Description = "the key" },
        ];
        for (int i = 0; i < columns.Length; i++)
        {
            Assert.Equal(i + 1, builder.AddColumn(columns[i]));
            columns[i].Id = i + 1;
            all.Columns.Add(columns[i]);
        }

        var spec = new PartitionSpecRow { Table = 1, SpecId = 0, IsDefault = true };
        Assert.Equal(1, builder.AddPartitionSpec(spec));
        spec.Id = 1;
        all.PartitionSpecs.Add(spec);
        var partitionField = new PartitionFieldRow { PartitionSpec = 1, Position = 0, Name = "id_bucket", SourceColumns = [1], Transform = "bucket[4]" };
        Assert.Equal(0, builder.AddPartitionField(partitionField));
        all.PartitionFields.Add(partitionField);
        var order = new SortOrderRow { Table = 1, OrderId = 1, IsDefault = true };
        Assert.Equal(1, builder.AddSortOrder(order));
        order.Id = 1;
        all.SortOrders.Add(order);
        var sortField = new SortFieldRow { SortOrder = 1, Position = 0, SourceColumn = 2, Transform = "identity", Direction = "asc", NullOrder = "nulls-first" };
        Assert.Equal(0, builder.AddSortField(sortField));
        all.SortFields.Add(sortField);
        var schema = new PhysicalSchemaRow { Table = 1, FileCount = 2 };
        Assert.Equal(1, builder.AddPhysicalSchema(schema));
        schema.Id = 1;
        all.PhysicalSchemas.Add(schema);
        PhysicalColumnRow[] physicalColumns =
        [
            new() { PhysicalSchema = 1, Ordinal = 0, Path = ["id"], PhysicalType = "INT64", Repetition = "OPTIONAL", Column = 1, CompressedSize = ColumnSum(1), ChunkCount = 3 },
            new() { PhysicalSchema = 1, Ordinal = 1, Path = ["amount"], PhysicalType = "FIXED_LEN_BYTE_ARRAY", TypeLength = 9, LogicalType = "DECIMAL(18,2)",
                Repetition = "OPTIONAL", FieldId = 2, Column = 2, CompressedSize = ColumnSum(2), UncompressedSize = 2 * ColumnSum(2), ChunkCount = 3 },
        ];
        for (int i = 0; i < physicalColumns.Length; i++)
        {
            Assert.Equal(i + 1, builder.AddPhysicalColumn(physicalColumns[i]));
            physicalColumns[i].Id = i + 1;
            all.PhysicalColumns.Add(physicalColumns[i]);
        }

        var writer = new WriterRow { Table = 1, CreatedBy = "writer-x 1.0", IsVOrdered = false, VOrderLevel = "none", FileCount = 2, FileSize = 3000 };
        Assert.Equal(1, builder.AddWriter(writer));
        writer.Id = 1;
        all.Writers.Add(writer);
        var operation = new OperationRow { Table = 1, Name = "append", CommitCount = 2, LastAt = Utc(2026, 1, 2, 3, 4, 5) };
        Assert.Equal(0, builder.AddOperation(operation));
        all.Operations.Add(operation);
        var issueOne = new IssueRow { Table = 1, Path = "data/f2.parquet", Code = "FooterNotRead", Message = "an example" };
        var issueTwo = new IssueRow { Code = "AccessDenied", Message = "no single table" };
        Assert.Equal(0, builder.AddIssue(issueOne));
        Assert.Equal(1, builder.AddIssue(issueTwo));
        all.Issues.AddRange([issueOne, issueTwo]);
        var model = new ModelBindingRow { ModelName = "Model", ServerName = "server" };
        Assert.Equal(1, builder.AddModelBinding(model));
        model.Id = 1;
        all.ModelBindings.Add(model);
        var tableBinding = new TableBindingRow { ModelBinding = 1, ModelTable = "Trips", Table = 1 };
        Assert.Equal(1, builder.AddTableBinding(tableBinding));
        tableBinding.Id = 1;
        all.TableBindings.Add(tableBinding);
        var bindingOne = new ColumnBindingRow { TableBinding = 1, ModelColumn = "Id", Column = 1 };
        var bindingTwo = new ColumnBindingRow { TableBinding = 1, ModelColumn = "Amount", Column = 2 };
        Assert.Equal(0, builder.AddColumnBinding(bindingOne));
        Assert.Equal(1, builder.AddColumnBinding(bindingTwo));
        all.ColumnBindings.AddRange([bindingOne, bindingTwo]);

        // LastModifiedAt is the latest ModifiedAt of the two files of the partition, below.
        var partition = new PartitionRow { Table = 1, PartitionSpec = 1, Values = ["3"], FileCount = 2, FileSize = 3000, RowCount = 150, DeletedRowCount = 5,
            LastModifiedAt = Utc(2026, 1, 2, 3, 0, 0, 999) };
        Assert.Equal(1, builder.AddPartition(partition));
        partition.Id = 1;
        all.Partitions.Add(partition);
        FileRow[] files =
        [
            new() { Table = 1, Path = "data/f1.parquet", Format = "Parquet", FileSize = 2000, ModifiedAt = Utc(2026, 1, 2, 3, 0, 0, 999), RowCount = 100,
                DeletedRowCount = 5, DeletionVectorSize = 40, Partition = 1, SortOrder = 1, ClusteringProvider = "liquid", IsVOrdered = false, FooterSize = 300,
                PhysicalSchema = 1, Writer = 1, RowGroupCount = 2, CompressedSize = GroupSum(1) + GroupSum(2), UncompressedSize = 2 * (GroupSum(1) + GroupSum(2)) },
            new() { Table = 1, Path = "data/f2.parquet", Format = "Parquet", FileSize = 1000, ModifiedAt = new DateTimeOffset(2026, 1, 2, 4, 0, 0, TimeSpan.FromHours(2)),
                RowCount = 50, DeletedRowCount = 0, Partition = 1, SortOrder = 1, IsVOrdered = false, FooterSize = 200, PhysicalSchema = 1, Writer = 1,
                RowGroupCount = 1, CompressedSize = GroupSum(3), UncompressedSize = 2 * GroupSum(3) },
        ];
        for (int i = 0; i < files.Length; i++)
        {
            Assert.Equal(i + 1, builder.AddFile(files[i]));
            files[i].Id = i + 1;
            all.Files.Add(files[i]);
        }

        (long File, long Ordinal, long Rows)[] groups = [(1, 0, 60), (1, 1, 40), (2, 0, 50)];
        for (int group = 1; group <= 3; group++)
        {
            var row = new RowGroupRow { File = groups[group - 1].File, Ordinal = groups[group - 1].Ordinal, RowCount = groups[group - 1].Rows,
                UncompressedSize = 2 * GroupSum(group), CompressedSize = GroupSum(group) };
            Assert.Equal(group, builder.AddRowGroup(row));
            row.Id = group;
            all.RowGroups.Add(row);
        }

        int position = 0;
        for (int group = 1; group <= 3; group++)
        {
            for (int physical = 1; physical <= 2; physical++)
            {
                long size = chunkSize[group, physical];
                var chunk = new ColumnChunkRow
                {
                    RowGroup = group, PhysicalColumn = physical, Codec = "ZSTD", Encodings = physical == 1 ? ["PLAIN"] : ["RLE_DICTIONARY", "PLAIN"],
                    ValueCount = 50, NullCount = 0, CompressedSize = size, UncompressedSize = 2 * size, HasDictionaryPage = physical == 2,
                    DictionaryPageSize = physical == 2 ? 10 : null, DataPageCount = 1, DictionaryEncodedPageCount = physical == 2 ? 1 : 0,
                    MinValue = "1", MaxValue = "99", DistinctCount = physical == 2 ? 3 : null,
                };
                Assert.Equal(position++, builder.AddColumnChunk(chunk));
                all.ColumnChunks.Add(chunk);
            }
        }

        var deleteFile = new DeleteFileRow { Table = 1, Kind = "Position deletes", Path = "data/d1.parquet", Format = "Parquet", FileSize = 100,
            RecordCount = 5, ReferencedFile = 1, EqualityColumns = [1], Partition = 1 };
        Assert.Equal(1, builder.AddDeleteFile(deleteFile));
        deleteFile.Id = 1;
        all.DeleteFiles.Add(deleteFile);
        var commitOne = new CommitRow { Table = 1, CommitId = "41", ParentId = "40", Timestamp = Utc(2026, 1, 1, 0, 0, 0), Operation = "append",
            Engine = "engine 1", IsBlindAppend = true, Metrics = new Dictionary<string, string> { ["added-files"] = "1" },
            Parameters = new Dictionary<string, string> { ["mode"] = "append" } };
        var commitTwo = new CommitRow { Table = 1, CommitId = "42", ParentId = "41", Timestamp = Utc(2026, 1, 2, 3, 4, 5), Operation = "append",
            Metrics = new Dictionary<string, string>() };
        Assert.Equal(0, builder.AddCommit(commitOne));
        Assert.Equal(1, builder.AddCommit(commitTwo));
        all.Commits.AddRange([commitOne, commitTwo]);
        return all;
    }

    [Fact]
    public void A_footprint_with_every_table_is_written_read_back_equal_and_valid()
    {
        Everything all = BuildEverything();
        byte[] data = Write(all.Builder);
        using PqaxDocument doc = Open(data);
        Assert.Equal(all.Builder.Collection, doc.Manifest.Collection);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 8, 15, 30, 123, TimeSpan.Zero), doc.Manifest.Collection.StartedAt);
        Assert.Equal(all.Sources, doc.Summary.Sources.Rows);
        Assert.Equal(all.Tables, doc.Summary.Tables.Rows);
        Assert.Equal(all.Columns, doc.Summary.Columns.Rows);
        Assert.Equal(all.PartitionSpecs, doc.Summary.PartitionSpecs.Rows);
        Assert.Equal(all.PartitionFields, doc.Summary.PartitionFields.Rows);
        Assert.Equal(all.SortOrders, doc.Summary.SortOrders.Rows);
        Assert.Equal(all.SortFields, doc.Summary.SortFields.Rows);
        Assert.Equal(all.PhysicalSchemas, doc.Summary.PhysicalSchemas.Rows);
        Assert.Equal(all.PhysicalColumns, doc.Summary.PhysicalColumns.Rows);
        Assert.Equal(all.Writers, doc.Summary.Writers.Rows);
        Assert.Equal(all.Operations, doc.Summary.Operations.Rows);
        Assert.Equal(all.Issues, doc.Summary.Issues.Rows);
        Assert.Equal(all.ModelBindings, doc.Summary.ModelBindings.Rows);
        Assert.Equal(all.TableBindings, doc.Summary.TableBindings.Rows);
        Assert.Equal(all.ColumnBindings, doc.Summary.ColumnBindings.Rows);
        Assert.Equal(all.Partitions, doc.ReadPartitions().Rows);
        Assert.Equal(all.Files, doc.ReadFiles().Rows);
        Assert.Equal(all.RowGroups, doc.ReadRowGroups().Rows);
        Assert.Equal(all.ColumnChunks, doc.ReadColumnChunks().Rows);
        Assert.Equal(all.DeleteFiles, doc.ReadDeleteFiles().Rows);
        Assert.Equal(all.Commits, doc.ReadCommits().Rows);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 2, 0, 0, TimeSpan.Zero), doc.ReadFiles()[1].ModifiedAt);
        Assert.Equal(TimeSpan.Zero, doc.ReadFiles()[1].ModifiedAt!.Value.Offset);
        Assert.NotNull(doc.ReadCommits()[1].Metrics);
        Assert.Empty(doc.ReadCommits()[1].Metrics!);
        Assert.Empty(doc.Summary.Tables[1].Delta!.ReaderFeatures!);
        AssertValid(data);
        Assert.Empty(SchemaCheck.Errors(data));

        JsonNode files = Kit.Json(Kit.Entries(data).Single(entry => entry.Name == "files.json").Content);
        Assert.Equal("2026-01-02T03:00:00.999Z", (string?)files["files"]!["modifiedAt"]![0]);
        Assert.Equal("2026-01-02T02:00:00.000Z", (string?)files["files"]!["modifiedAt"]![1]);
    }

    // ---- the fields of format 0.2 ----

    [Fact]
    public void A_footprint_with_the_fields_of_format_0_2_writes_0_2_and_reads_them_back_equal()
    {
        var builder = new FootprintBuilder(new CollectionInfo { CollectorApp = "pqax-tests", Level = 1 });
        builder.AddSource(new SourceRow { Kind = "Storage folder" });
        var first = new TableRow
        {
            Source = 1, Name = "a", Format = "Delta", DetailDepth = "Files", FileCount = 2, FileSize = 30, NonLiveFileCount = 2, NonLiveFileSize = 50,
            NonLiveOldestRemovedAt = Utc(2025, 3, 1, 8, 0, 0, 5), NonLiveNewestRemovedAt = new DateTimeOffset(2025, 3, 2, 10, 0, 0, TimeSpan.FromHours(2)),
            UnreferencedFileCount = 4, UnreferencedFileSize = 0,
        };
        var second = new TableRow { Source = 1, Name = "b", Format = "Delta", DetailDepth = "Summary", UnreferencedFileCount = 0, UnreferencedFileSize = 0 };
        first.Id = builder.AddTable(first);
        second.Id = builder.AddTable(second);
        var partition = new PartitionRow { Table = first.Id, FileCount = 2, FileSize = 30, LastModifiedAt = Utc(2025, 3, 3, 0, 0, 0) };
        partition.Id = builder.AddPartition(partition);
        builder.AddFile(new FileRow { Table = first.Id, FileSize = 10, Partition = partition.Id, ModifiedAt = Utc(2025, 3, 2, 0, 0, 0) });
        builder.AddFile(new FileRow { Table = first.Id, FileSize = 20, Partition = partition.Id, ModifiedAt = Utc(2025, 3, 3, 0, 0, 0) });
        byte[] data = Write(builder);
        using PqaxDocument doc = Open(data);
        Assert.Equal("0.2", doc.Manifest.FormatVersion);
        Assert.Equal([first, second], doc.Summary.Tables.Rows);
        Assert.Equal([partition], doc.ReadPartitions().Rows);
        Assert.Equal(new DateTimeOffset(2025, 3, 2, 8, 0, 0, TimeSpan.Zero), doc.Summary.Tables[0].NonLiveNewestRemovedAt);
        Assert.Empty(doc.Summary.Tables.ExtraFields);
        Assert.Empty(doc.ReadPartitions().ExtraFields);

        JsonNode summary = Kit.Json(Kit.Entries(data).Single(entry => entry.Name == "summary.json").Content);
        Assert.Equal("[4,0]", Kit.Canonical(summary["tables"]!["unreferencedFileCount"]));
        Assert.Equal("[0,0]", Kit.Canonical(summary["tables"]!["unreferencedFileSize"]));
        Assert.Equal("[\"2025-03-01T08:00:00.005Z\",null]", Kit.Canonical(summary["tables"]!["nonLiveOldestRemovedAt"]));
        Assert.Equal("[\"2025-03-02T08:00:00.000Z\",null]", Kit.Canonical(summary["tables"]!["nonLiveNewestRemovedAt"]));
        JsonNode partitions = Kit.Json(Kit.Entries(data).Single(entry => entry.Name.StartsWith("partitions", StringComparison.Ordinal)).Content);
        Assert.Equal("[\"2025-03-03T00:00:00.000Z\"]", Kit.Canonical(partitions["partitions"]!["lastModifiedAt"]));
        AssertValid(data);
        Assert.Empty(SchemaCheck.Errors(data));

        byte[] again = Write(FootprintBuilder.From(doc));
        using PqaxDocument copy = Open(again);
        Assert.Equal("0.2", copy.Manifest.FormatVersion);
        AssertSameTables(doc, copy);
        AssertValid(again);
    }

    [Fact]
    public void A_rewrite_of_a_file_of_format_0_1_writes_0_2_and_keeps_the_tables()
    {
        using PqaxDocument original = PqaxDocument.Open(Path.Combine(Kit.Samples, "format-0.1.pqax"));
        Assert.Equal("0.1", original.Manifest.FormatVersion);
        byte[] data = Write(FootprintBuilder.From(original));
        using PqaxDocument copy = Open(data);
        Assert.Equal("0.2", copy.Manifest.FormatVersion);
        Assert.Equal(original.Manifest.Collection, copy.Manifest.Collection);
        AssertSameTables(original, copy);
        Assert.All(copy.Summary.Tables, table =>
        {
            Assert.Null(table.UnreferencedFileCount);
            Assert.Null(table.UnreferencedFileSize);
            Assert.Null(table.NonLiveOldestRemovedAt);
            Assert.Null(table.NonLiveNewestRemovedAt);
        });
        Assert.All(copy.ReadPartitions(), partition => Assert.Null(partition.LastModifiedAt));
        Assert.Empty(Kit.Differences(Kit.Shape(copy), Kit.Expected("format-0.1"), Kit.ShapeKeys.Where(key => key is not "parts" and not "formatVersion")));
        AssertValid(data);
        Assert.Empty(SchemaCheck.Errors(data));
    }

    [Fact]
    public void An_empty_footprint_has_an_empty_summary_and_is_valid()
    {
        byte[] data = Write(new FootprintBuilder(new CollectionInfo { Level = 1 }));
        Dictionary<string, byte[]> entries = Kit.Entries(data).ToDictionary(entry => entry.Name, entry => entry.Content);
        Assert.Equal("{}", Encoding.UTF8.GetString(entries["summary.json"]));
        JsonNode manifest = Kit.Json(entries["manifest.json"]);
        Assert.Equal("[\"summary.json\"]", Kit.Canonical(new JsonArray(manifest["parts"]!.AsArray().Select(part => (JsonNode?)JsonValue.Create((string)part!["name"]!)).ToArray())));
        using PqaxDocument doc = Open(data);
        Assert.Empty(doc.Summary.Tables);
        Assert.Empty(doc.ReadFiles());
        AssertValid(data);
    }

    [Fact]
    public void A_field_or_a_table_without_values_is_left_out()
    {
        var builder = new FootprintBuilder(new CollectionInfo { Level = 1 });
        builder.Options.RowsPerPart = 2;
        builder.AddSource(new SourceRow { Kind = "Storage folder" });
        builder.AddTable(new TableRow { Source = 1, Name = "a", Format = "Parquet", DetailDepth = "Files", FileCount = 3, FileSize = 6 });
        builder.AddFile(new FileRow { Table = 1, FileSize = 1, DeletionVectorSize = 8 });
        builder.AddFile(new FileRow { Table = 1, FileSize = 2 });
        builder.AddFile(new FileRow { Table = 1, FileSize = 3 });
        byte[] data = Write(builder);
        Dictionary<string, byte[]> entries = Kit.Entries(data).ToDictionary(entry => entry.Name, entry => entry.Content);
        JsonObject summary = Kit.Json(entries["summary.json"]).AsObject();
        Assert.Equal(["sources", "tables"], summary.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        JsonNode manifest = Kit.Json(entries["manifest.json"]);
        List<string> parts = manifest["parts"]!.AsArray().Select(part => (string)part!["name"]!).Skip(1).ToList();
        Assert.Equal(2, parts.Count);
        List<JsonObject> pieces = parts.Select(part => Kit.Json(entries[part])["files"]!.AsObject()).ToList();
        Assert.Equal([2, 1], pieces.Select(piece => piece["id"]!.AsArray().Count));
        Assert.All(pieces, piece => Assert.False(piece.ContainsKey("path")));
        Assert.True(pieces[0].ContainsKey("deletionVectorSize"));
        Assert.False(pieces[1].ContainsKey("deletionVectorSize"));
        using PqaxDocument doc = Open(data);
        Assert.Equal([8L, null, null], doc.ReadFiles().Select(file => file.DeletionVectorSize));
        AssertValid(data);
    }

    [Fact]
    public void Ids_and_references_survive_from_and_write()
    {
        byte[] renumbered = Kit.Rewrite(Kit.SampleBytes("summary-only.pqax"), (name, content) =>
        {
            if (name != "summary.json")
            {
                return content;
            }

            JsonNode json = Kit.Json(content);
            json["sources"]!["id"] = new JsonArray(10, 20);
            json["tables"]!["source"] = new JsonArray(10, 20);
            return Kit.Bytes(json);
        });
        AssertValid(renumbered);
        using PqaxDocument original = Open(renumbered);
        byte[] data = Write(FootprintBuilder.From(original));
        using PqaxDocument copy = Open(data);
        Assert.Equal([10L, 20L], copy.Summary.Sources.Select(source => source.Id));
        Assert.Equal([10L, 20L], copy.Summary.Tables.Select(table => table.Source));
        AssertValid(data);
    }

    [Fact]
    public void A_part_size_above_250000_is_refused()
    {
        var builder = new FootprintBuilder(new CollectionInfo { Level = 1 });
        builder.AddSource(new SourceRow { Kind = "Storage folder" });
        Exception? error = Record.Exception(() =>
        {
            builder.Options.RowsPerPart = FootprintBuilderOptions.MaxRowsPerPart + 1;
            Write(builder);
        });
        Assert.NotNull(error);
        Assert.Equal(250_000, FootprintBuilderOptions.MaxRowsPerPart);
        Assert.Equal(250_000, new FootprintBuilder(new CollectionInfo { Level = 1 }).Options.RowsPerPart);
    }

    // ---- the large test ----

    [Fact]
    public void A_table_of_600000_column_chunks_is_split_in_parts_of_250000_and_read_back_whole()
    {
        const int fileCount = 100, groupsPerFile = 100, physicalCount = 60;
        const int groupCount = fileCount * groupsPerFile, chunkCount = groupCount * physicalCount;
        Assert.Equal(600_000, chunkCount);
        static long Size(int index) => 1 + ((index * 7919L) % 1000);
        long fileSize = Enumerable.Range(0, fileCount).Sum(index => 1000L + index);
        long totalSize = Enumerable.Range(0, chunkCount).Sum(Size);
        var builder = new FootprintBuilder(new CollectionInfo { CollectorApp = "pqax-tests", Level = 2 });
        builder.AddSource(new SourceRow { Kind = "Storage folder" });
        long table = builder.AddTable(new TableRow { Source = 1, Name = "large", Format = "Parquet", DetailDepth = "Column chunks",
            FileCount = fileCount, FileSize = fileSize, RowGroupCount = groupCount, CompressedSize = totalSize });
        Assert.Equal(1, table);
        builder.AddPhysicalSchema(new PhysicalSchemaRow { Table = table, FileCount = fileCount });
        for (int i = 1; i <= physicalCount; i++)
        {
            builder.AddColumn(new ColumnRow { Table = table, Name = $"c{i}", DataType = "long" });
            builder.AddPhysicalColumn(new PhysicalColumnRow { PhysicalSchema = 1, Ordinal = i - 1, Path = [$"c{i}"], PhysicalType = "INT64", Column = i });
        }

        for (int i = 0; i < fileCount; i++)
        {
            builder.AddFile(new FileRow { Table = table, FileSize = 1000 + i });
        }

        for (int i = 0; i < groupCount; i++)
        {
            builder.AddRowGroup(new RowGroupRow { File = 1 + (i / groupsPerFile), Ordinal = i % groupsPerFile, RowCount = 1000 });
        }

        for (int i = 0; i < chunkCount; i++)
        {
            builder.AddColumnChunk(new ColumnChunkRow { RowGroup = 1 + (i / physicalCount), PhysicalColumn = 1 + (i % physicalCount), CompressedSize = Size(i), UncompressedSize = 2 * Size(i) });
        }

        byte[] data = Write(builder);
        using PqaxDocument doc = Open(data);
        Assert.Equal([250_000, 250_000, 100_000], doc.Manifest.Parts.Where(part => part.Tables.ContainsKey("columnChunks")).Select(part => part.Tables["columnChunks"]));
        Assert.All(doc.Manifest.Parts.Skip(1), part => Assert.True(part.Tables.Values.Sum() <= 250_000));
        Assert.Equal(chunkCount, doc.RowCount("columnChunks"));
        PqaxTable<ColumnChunkRow> chunks = doc.ReadColumnChunks();
        Assert.Equal(chunkCount, chunks.Count);
        int mismatches = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            ColumnChunkRow chunk = chunks[i];
            if (chunk.CompressedSize != 1 + ((i * 7919L) % 1000) || chunk.RowGroup != 1 + (i / physicalCount) || chunk.PhysicalColumn != 1 + (i % physicalCount))
            {
                mismatches++;
            }
        }

        Assert.Equal(0, mismatches);
        AssertValid(data);
    }
}
