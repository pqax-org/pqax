using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Pqax.Tests;

/// <summary>
/// The checks of <see cref="FootprintBuilder.Write(Stream)"/> and <see cref="FootprintBuilder.Write(string)"/>: a
/// footprint whose integers, required fields, ids, or collection level break a rule of the format is refused with
/// <see cref="PqaxWriteException"/> and the code of the rule, before anything is written (the contract of the
/// hardening before 1.0, section 2).
/// </summary>
public class WriterCheckTests
{
    /// <summary>2^53 - 1, the largest integer of the format in absolute value.</summary>
    private const long Limit = 9007199254740991;

    private static byte[] Write(FootprintBuilder builder)
    {
        using var stream = new MemoryStream();
        builder.Write(stream);
        return stream.ToArray();
    }

    private static PqaxDocument Open(byte[] data) => PqaxDocument.Open(new MemoryStream(data));

    /// <summary>
    /// A small valid footprint: a source, a table at the Files depth, and its file. The actions change the collection and
    /// the rows before they are added, and add more rows after.
    /// </summary>
    private static FootprintBuilder Valid(Action<CollectionInfo>? collection = null, Action<TableRow>? table = null,
        Action<FileRow>? file = null, Action<FootprintBuilder>? more = null)
    {
        var info = new CollectionInfo { CollectorApp = "pqax-tests", Level = 1 };
        collection?.Invoke(info);
        var builder = new FootprintBuilder(info);
        builder.AddSource(new SourceRow { Kind = "Storage folder" });
        var tableRow = new TableRow { Source = 1, Name = "a", Format = "Parquet", DetailDepth = "Files", FileCount = 1, FileSize = 10 };
        table?.Invoke(tableRow);
        builder.AddTable(tableRow);
        var fileRow = new FileRow { Table = 1, FileSize = 10 };
        file?.Invoke(fileRow);
        builder.AddFile(fileRow);
        more?.Invoke(builder);
        return builder;
    }

    /// <summary>
    /// The exception of a refused write, the same from both Write methods, after checking that nothing was written: the
    /// stream is empty, no file is created, and a file that is there is left as it was.
    /// </summary>
    private static PqaxWriteException Refused(FootprintBuilder builder)
    {
        using var stream = new MemoryStream();
        PqaxWriteException toStream = Assert.Throws<PqaxWriteException>(() => builder.Write(stream));
        Assert.Equal(0, stream.Length);

        using var file = new TemporaryFile();
        PqaxWriteException toPath = Assert.Throws<PqaxWriteException>(() => builder.Write(file.Path));
        Assert.False(File.Exists(file.Path), "a refused write created the file");

        using TemporaryFile existing = Kit.Temporary(Encoding.UTF8.GetBytes("left as it was"));
        PqaxWriteException toExisting = Assert.Throws<PqaxWriteException>(() => builder.Write(existing.Path));
        Assert.Equal("left as it was", File.ReadAllText(existing.Path));

        foreach (PqaxWriteException error in new[] { toPath, toExisting })
        {
            Assert.Equal((toStream.Code, toStream.Table, toStream.Field), (error.Code, error.Table, error.Field));
        }

        Assert.IsAssignableFrom<InvalidOperationException>(toStream);
        Assert.False(string.IsNullOrEmpty(toStream.Message));
        return toStream;
    }

    private static void AssertRefused(FootprintBuilder builder, string code, string? table, string field)
    {
        PqaxWriteException error = Refused(builder);
        Assert.Equal((code, table, field), (error.Code, error.Table, error.Field));
    }

    // ---- the exception ----

    [Fact]
    public void PqaxWriteException_is_a_public_sealed_InvalidOperationException_with_a_code_a_table_and_a_field()
    {
        Type type = typeof(PqaxWriteException);
        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        Assert.Equal(typeof(InvalidOperationException), type.BaseType);
        var context = new NullabilityInfoContext();
        Assert.Equal(NullabilityState.NotNull, context.Create(type.GetProperty(nameof(PqaxWriteException.Code))!).ReadState);
        Assert.Equal(NullabilityState.Nullable, context.Create(type.GetProperty(nameof(PqaxWriteException.Table))!).ReadState);
        Assert.Equal(NullabilityState.Nullable, context.Create(type.GetProperty(nameof(PqaxWriteException.Field))!).ReadState);
        Assert.All(new[] { "Code", "Table", "Field" }, name => Assert.Equal(typeof(string), type.GetProperty(name)!.PropertyType));
    }

    [Fact]
    public void A_valid_footprint_is_written_and_a_caller_of_InvalidOperationException_still_catches_a_refusal()
    {
        byte[] data = Write(Valid());
        using (var stream = new MemoryStream(data))
        {
            Assert.True(PqaxValidator.Validate(stream).IsValid);
        }

        FootprintBuilder repeated = Valid(more: builder =>
        {
            builder.AddSource(new SourceRow { Id = 5, Kind = "Storage folder" });
            builder.AddSource(new SourceRow { Id = 5, Kind = "Storage folder" });
        });
        Assert.IsType<PqaxWriteException>(Assert.ThrowsAny<InvalidOperationException>(() => Write(repeated)));
    }

    // ---- integers ----

    /// <summary>A kind of integer of the format: a footprint with a value there, where the error points, and the value read back.</summary>
    private sealed record Integer(string? Table, string Field, bool Signed, Func<long, FootprintBuilder> Make, Func<PqaxDocument, long?> Read);

    private static readonly Dictionary<string, Integer> Integers = new(StringComparer.Ordinal)
    {
        ["an integer field"] = new("tables", "rowCount", true, value => Valid(table: row => row.RowCount = value),
            doc => doc.Summary.Tables[0].RowCount),
        ["an integer field of a detail table"] = new("files", "rowCount", true, value => Valid(file: row => row.RowCount = value),
            doc => doc.ReadFiles()[0].RowCount),
        // A negative id also breaks table.idUnique, so an id is tested with positive values only.
        ["an id"] = new("sources", "id", false, value => Valid(more: builder => builder.AddSource(new SourceRow { Id = value, Kind = "Storage folder" })),
            doc => doc.Summary.Sources[1].Id),
        ["a reference"] = new("tables", "source", true, value => Valid(table: row => row.Source = value),
            doc => doc.Summary.Tables[0].Source),
        ["an element of a list of references"] = new("tables", "clusteringColumns", true, value => Valid(table: row => row.ClusteringColumns = [1, value]),
            doc => doc.Summary.Tables[0].ClusteringColumns![1]),
        ["a member of a Distribution"] = new("tables", "fileSizes", true, value => Valid(table: row => row.FileSizes = new Distribution { Count = 1, Maximum = value }),
            doc => doc.Summary.Tables[0].FileSizes!.Maximum),
        ["a member of DeltaDetails"] = new("tables", "delta", true, value => Valid(table: row => row.Delta = new DeltaDetails { MinReaderVersion = 1, CheckpointVersion = value }),
            doc => doc.Summary.Tables[0].Delta!.CheckpointVersion),
        ["a member of IcebergDetails"] = new("tables", "iceberg", true, value => Valid(table: row => row.Iceberg = new IcebergDetails { FormatVersion = 2, SnapshotCount = value }),
            doc => doc.Summary.Tables[0].Iceberg!.SnapshotCount),
        ["a position"] = new("partitionFields", "position", true, value => Valid(more: builder =>
            {
                builder.AddPartitionSpec(new PartitionSpecRow { Table = 1 });
                builder.AddPartitionField(new PartitionFieldRow { PartitionSpec = 1, Position = value });
            }),
            doc => doc.Summary.PartitionFields[0].Position),
        ["collection.level"] = new(null, "collection.level", true, value => Valid(collection: info => info.Level = value),
            doc => doc.Manifest.Collection.Level),
        ["collection.requestCount"] = new(null, "collection.requestCount", true, value => Valid(collection: info => info.RequestCount = value),
            doc => doc.Manifest.Collection.RequestCount),
        ["collection.bytesRead"] = new(null, "collection.bytesRead", true, value => Valid(collection: info => info.BytesRead = value),
            doc => doc.Manifest.Collection.BytesRead),
    };

    private static TheoryData<string, long> Cases(params long[] values)
    {
        var data = new TheoryData<string, long>();
        foreach ((string kind, Integer integer) in Integers)
        {
            foreach (long value in values.Where(value => value > 0 || integer.Signed))
            {
                data.Add(kind, value);
            }
        }

        return data;
    }

    public static TheoryData<string, long> AtTheLimit => Cases(Limit, -Limit);

    public static TheoryData<string, long> BeyondTheLimit => Cases(Limit + 1, -(Limit + 1), long.MaxValue, long.MinValue);

    [Theory]
    [MemberData(nameof(AtTheLimit))]
    public void An_integer_of_2_53_minus_1_in_absolute_value_is_written(string kind, long value)
    {
        byte[] data = Write(Integers[kind].Make(value));
        using PqaxDocument doc = Open(data);
        Assert.Equal(value, Integers[kind].Read(doc));
    }

    [Theory]
    [MemberData(nameof(BeyondTheLimit))]
    public void An_integer_beyond_2_53_minus_1_is_refused_with_json_integerRange(string kind, long value)
    {
        Integer integer = Integers[kind];
        AssertRefused(integer.Make(value), "json.integerRange", integer.Table, integer.Field);
    }

    [Fact]
    public void The_numbers_of_a_producer_are_not_checked()
    {
        const long huge = 1L << 60;
        byte[] data = Kit.Rewrite(Kit.SampleBytes("extensions.pqax"), (name, content) =>
        {
            if (name is not ("manifest.json" or "summary.json" or "acme_extra.json"))
            {
                return content;
            }

            JsonNode json = Kit.Json(content);
            switch (name)
            {
                case "manifest.json":
                    json["acme_build"]!["number"] = huge;
                    json["collection"]!["acme_requests"] = -huge;
                    break;
                case "summary.json":
                    json["tables"]!["acme_score"] = new JsonArray(huge, -huge);
                    json["tables"]!["fileSizes"]![0]!["ninetiethPercentile"] = huge;
                    json["acme_reviews"]!["score"] = new JsonArray(huge, 3);
                    break;
                default:
                    json["acme_details"]!["size"] = new JsonArray(huge, huge, -huge);
                    break;
            }

            return Kit.Bytes(json);
        });
        using PqaxDocument original = Open(data);
        byte[] written = Write(FootprintBuilder.From(original));
        using PqaxDocument doc = Open(written);
        Assert.Equal(huge, doc.Manifest.Extensions["acme_build"].GetProperty("number").GetInt64());
        Assert.Equal(-huge, doc.Manifest.Collection.Extensions!["acme_requests"].GetInt64());
        Assert.Equal([huge, -huge], doc.Summary.Tables.ExtraFields["acme_score"].Select(value => value!.Value.GetInt64()));
        Assert.Equal(huge, doc.Summary.Tables[0].FileSizes!.Extensions!["ninetiethPercentile"].GetInt64());
        Assert.Equal([huge, 3L], doc.ReadRaw("acme_reviews").Fields["score"].Select(value => value!.Value.GetInt64()));
        Assert.Equal([huge, huge, -huge], doc.ReadRaw("acme_details").Fields["size"].Select(value => value!.Value.GetInt64()));
    }

    // ---- required fields ----

    /// <summary>Footprints that miss one required field of rules.md 4.4, with the table and the field.</summary>
    private static readonly Dictionary<string, (Func<FootprintBuilder> Make, string Table, string Field)> Required = new(StringComparer.Ordinal)
    {
        ["tables.format"] = (() => Valid(table: row => row.Format = null), "tables", "format"),
        ["tables.name, in the second row"] = (() => Valid(more: builder => builder.AddTable(new TableRow { Source = 1, Format = "Parquet", DetailDepth = "Summary" })),
            "tables", "name"),
        ["columns.table, the reference to the parent"] = (() => Valid(more: builder => builder.AddColumn(new ColumnRow { Name = "c", DataType = "long" })),
            "columns", "table"),
        ["partitionFields.position"] = (() => Valid(more: builder =>
            {
                builder.AddPartitionSpec(new PartitionSpecRow { Table = 1 });
                builder.AddPartitionField(new PartitionFieldRow { PartitionSpec = 1, Name = "p" });
            }), "partitionFields", "position"),
        ["sortFields.direction"] = (() => Valid(more: builder =>
            {
                builder.AddColumn(new ColumnRow { Table = 1, Name = "c", DataType = "long" });
                builder.AddSortOrder(new SortOrderRow { Table = 1 });
                builder.AddSortField(new SortFieldRow { SortOrder = 1, Position = 0, SourceColumn = 1 });
            }), "sortFields", "direction"),
        ["issues.code"] = (() => Valid(more: builder => builder.AddIssue(new IssueRow { Message = "no code" })), "issues", "code"),
        ["files.table, in the second row of a detail table"] = (() => Valid(more: builder => builder.AddFile(new FileRow { FileSize = 5 })),
            "files", "table"),
        ["columnChunks.uncompressedSize"] = (() => Valid(more: builder =>
            {
                builder.AddPhysicalSchema(new PhysicalSchemaRow { Table = 1 });
                builder.AddPhysicalColumn(new PhysicalColumnRow { PhysicalSchema = 1, Ordinal = 0, Path = ["c"], PhysicalType = "INT64" });
                builder.AddRowGroup(new RowGroupRow { File = 1, Ordinal = 0, RowCount = 1 });
                builder.AddColumnChunk(new ColumnChunkRow { RowGroup = 1, PhysicalColumn = 1, CompressedSize = 1 });
            }), "columnChunks", "uncompressedSize"),
        ["deleteFiles.kind"] = (() => Valid(more: builder => builder.AddDeleteFile(new DeleteFileRow { Table = 1 })), "deleteFiles", "kind"),
        ["commits.commitId"] = (() => Valid(more: builder => builder.AddCommit(new CommitRow { Table = 1 })), "commits", "commitId"),
    };

    public static TheoryData<string> RequiredCases => Kit.Data(Required.Keys);

    [Theory]
    [MemberData(nameof(RequiredCases))]
    public void A_required_field_without_a_value_is_refused_with_table_required(string name)
    {
        (Func<FootprintBuilder> make, string table, string field) = Required[name];
        AssertRefused(make(), "table.required", table, field);
    }

    [Fact]
    public void A_collection_without_level_is_refused_with_manifest_required()
    {
        AssertRefused(Valid(collection: info => info.Level = null), "manifest.required", null, "collection.level");
        AssertRefused(new FootprintBuilder(new CollectionInfo()), "manifest.required", null, "collection.level");
    }

    // ---- ids ----

    [Fact]
    public void A_repeated_id_is_refused_with_table_idUnique()
    {
        AssertRefused(Valid(more: builder =>
        {
            builder.AddSource(new SourceRow { Id = 5, Kind = "Storage folder" });
            builder.AddSource(new SourceRow { Id = 5, Kind = "Storage folder" });
        }), "table.idUnique", "sources", "id");
        AssertRefused(Valid(table: row => row.FileCount = 2, more: builder => builder.AddFile(new FileRow { Id = 1, Table = 1, FileSize = 10 })),
            "table.idUnique", "files", "id");
    }

    [Fact]
    public void A_negative_id_is_refused_with_table_idUnique()
    {
        AssertRefused(Valid(more: builder => builder.AddSource(new SourceRow { Id = -3, Kind = "Storage folder" })), "table.idUnique", "sources", "id");
    }

    // ---- rewrites of the invalid samples ----

    /// <summary>
    /// The invalid samples whose rule a writer checks: From keeps what the reader returns, and the write is refused with
    /// the code that the kit gives to the sample, where the change is.
    /// </summary>
    public static TheoryData<string, string, string?, string> Rewrites => new()
    {
        { "json.integerRange.pqax", "json.integerRange", "rowGroups", "uncompressedSize" },
        { "manifest.required.level.pqax", "manifest.required", null, "collection.level" },
        { "table.idUnique.repeated.pqax", "table.idUnique", "sources", "id" },
        { "table.idUnique.nonPositive.pqax", "table.idUnique", "sources", "id" },
        { "table.required.missing.pqax", "table.required", "columns", "dataType" },
        { "table.required.null.pqax", "table.required", "columns", "name" },
        { "table.required.operationName.pqax", "table.required", "operations", "name" },
        { "table.required.position.pqax", "table.required", "partitionFields", "position" },
    };

    [Theory]
    [MemberData(nameof(Rewrites))]
    public void The_rewrite_of_an_invalid_sample_is_refused_with_its_code(string file, string code, string? table, string field)
    {
        Assert.Contains(code, Kit.InvalidCodes(file));
        using PqaxDocument doc = PqaxDocument.Open(Path.Combine(Kit.Invalid, file));
        AssertRefused(FootprintBuilder.From(doc), code, table, field);
    }
}
