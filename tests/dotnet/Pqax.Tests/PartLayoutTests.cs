using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pqax.Tests;

/// <summary>
/// The parts of a rewrite: <see cref="FootprintBuilder.From"/> and Write keep the names of the parts of the source,
/// their order, their rows, and the members of their descriptors, and lay out the other tables as the builder does;
/// <see cref="PartInfo.Extensions"/> holds those members (the contract of the hardening before 1.0, section 1).
/// </summary>
public class PartLayoutTests
{
    private static readonly string[] DescriptorMembers = ["name", "tables"];

    /// <summary>The 21 tables of the format, each with its rows as the reader returns them.</summary>
    private static readonly (string Name, Func<PqaxDocument, IEnumerable<object>> Rows)[] FormatTables =
    [
        ("sources", doc => doc.Summary.Sources.Rows), ("tables", doc => doc.Summary.Tables.Rows),
        ("columns", doc => doc.Summary.Columns.Rows), ("partitionSpecs", doc => doc.Summary.PartitionSpecs.Rows),
        ("partitionFields", doc => doc.Summary.PartitionFields.Rows), ("sortOrders", doc => doc.Summary.SortOrders.Rows),
        ("sortFields", doc => doc.Summary.SortFields.Rows), ("physicalSchemas", doc => doc.Summary.PhysicalSchemas.Rows),
        ("physicalColumns", doc => doc.Summary.PhysicalColumns.Rows), ("writers", doc => doc.Summary.Writers.Rows),
        ("operations", doc => doc.Summary.Operations.Rows), ("issues", doc => doc.Summary.Issues.Rows),
        ("modelBindings", doc => doc.Summary.ModelBindings.Rows), ("tableBindings", doc => doc.Summary.TableBindings.Rows),
        ("columnBindings", doc => doc.Summary.ColumnBindings.Rows), ("partitions", doc => doc.ReadPartitions().Rows),
        ("files", doc => doc.ReadFiles().Rows), ("rowGroups", doc => doc.ReadRowGroups().Rows),
        ("columnChunks", doc => doc.ReadColumnChunks().Rows), ("deleteFiles", doc => doc.ReadDeleteFiles().Rows),
        ("commits", doc => doc.ReadCommits().Rows),
    ];

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

    /// <summary>A rewrite through From. The rows per part are set, and change runs, after From and before the write.</summary>
    private static byte[] Rewrite(byte[] source, int? rowsPerPart = null, Action<FootprintBuilder>? change = null)
    {
        using PqaxDocument original = Open(source);
        FootprintBuilder builder = FootprintBuilder.From(original);
        if (rowsPerPart is int rows)
        {
            builder.Options.RowsPerPart = rows;
        }

        change?.Invoke(builder);
        return Write(builder);
    }

    /// <summary>The part descriptors as manifest.json holds them.</summary>
    private static List<JsonObject> Parts(byte[] data) =>
        Kit.Json(Kit.Entries(data).Single(entry => entry.Name == "manifest.json").Content)["parts"]!.AsArray()
            .Select(part => part!.AsObject()).ToList();

    private static List<string> Names(IEnumerable<JsonObject> parts) => parts.Select(part => (string)part["name"]!).ToList();

    private static Dictionary<string, JsonObject> ByName(IEnumerable<JsonObject> parts) =>
        parts.ToDictionary(part => (string)part["name"]!, StringComparer.Ordinal);

    /// <summary>A descriptor as JSON text with sorted members, to compare two descriptors whatever their order.</summary>
    private static string Describe(JsonObject part) => Kit.Canonical(part);

    /// <summary>A part as the reader gives it: its name, its tables, and the members of its descriptor, sorted.</summary>
    private static string Describe(PartInfo part) =>
        part.Name + " " + string.Join(",", part.Tables.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")) +
        " " + string.Join(",", part.Extensions.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={Kit.Canonical(pair.Value)}"));

    /// <summary>For each table, the sum of the rows that the descriptors state.</summary>
    private static Dictionary<string, int> StatedRows(IEnumerable<JsonObject> parts)
    {
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonObject part in parts)
        {
            foreach ((string table, JsonNode? count) in part["tables"]!.AsObject())
            {
                rows[table] = rows.GetValueOrDefault(table) + count!.GetValue<int>();
            }
        }

        return rows;
    }

    private static void AssertExtraFieldsEqual(IReadOnlyDictionary<string, IReadOnlyList<JsonElement?>> expected,
        IReadOnlyDictionary<string, IReadOnlyList<JsonElement?>> actual)
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
        foreach ((string name, Func<PqaxDocument, IEnumerable<object>> rows) in FormatTables)
        {
            Assert.True(rows(expected).SequenceEqual(rows(actual)), $"{name} differs");
            AssertExtraFieldsEqual(Kit.Typed(expected, name).Extra, Kit.Typed(actual, name).Extra);
        }

        foreach (string table in Kit.StatedTables(expected.Manifest).Where(name => !Kit.FormatTables.Contains(name)))
        {
            RawTable before = expected.ReadRaw(table);
            RawTable after = actual.ReadRaw(table);
            Assert.Equal(before.Count, after.Count);
            AssertExtraFieldsEqual(before.Fields, after.Fields);
        }
    }

    private static void AssertSameTables(byte[] expected, byte[] actual)
    {
        using PqaxDocument before = Open(expected);
        using PqaxDocument after = Open(actual);
        AssertSameTables(before, after);
    }

    /// <summary>A copy of a PQAX file whose manifest goes through change.</summary>
    private static byte[] ChangeManifest(byte[] zip, Action<JsonNode> change) =>
        Kit.Rewrite(zip, (name, content) =>
        {
            if (name != "manifest.json")
            {
                return content;
            }

            JsonNode manifest = Kit.Json(content);
            change(manifest);
            return Kit.Bytes(manifest);
        });

    /// <summary>A copy of a PQAX file with a part renamed, in the archive and in the manifest.</summary>
    private static byte[] RenamePart(byte[] zip, string from, string to)
    {
        byte[] renamed = ChangeManifest(zip, manifest =>
        {
            foreach (JsonNode? part in manifest["parts"]!.AsArray())
            {
                if ((string)part!["name"]! == from)
                {
                    part["name"] = to;
                }
            }
        });
        return Kit.Zip(Kit.Entries(renamed).Select(entry => (entry.Name == from ? to : entry.Name, entry.Content)));
    }

    /// <summary>Adds a table at the Files depth with two files whose totals agree, so that the file stays valid.</summary>
    private static void AddTwoFiles(FootprintBuilder builder)
    {
        Assert.Equal(3, builder.AddTable(new TableRow { Source = 1, Name = "added", Format = "Parquet", DetailDepth = "Files", FileCount = 2, FileSize = 30 }));
        Assert.Equal(6, builder.AddFile(new FileRow { Table = 3, FileSize = 10 }));
        Assert.Equal(7, builder.AddFile(new FileRow { Table = 3, FileSize = 20 }));
    }

    // ---- what the reader returns ----

    [Fact]
    public void PartInfo_extensions_hold_the_members_of_a_producer_of_extensions_pqax()
    {
        using PqaxDocument doc = PqaxDocument.Open(Path.Combine(Kit.Samples, "extensions.pqax"));
        Dictionary<string, PartInfo> parts = doc.Manifest.Parts.ToDictionary(part => part.Name, StringComparer.Ordinal);
        Assert.Equal(["acme_checksum"], parts["summary.json"].Extensions.Keys);
        Assert.Equal("c3a1", parts["summary.json"].Extensions["acme_checksum"].GetString());
        Assert.Equal(["acme_checksum"], parts["files.json"].Extensions.Keys);
        Assert.Equal("5f0c", parts["files.json"].Extensions["acme_checksum"].GetString());
        Assert.Equal(["acme_version"], parts["acme_extra.json"].Extensions.Keys);
        Assert.Equal(2, parts["acme_extra.json"].Extensions["acme_version"].GetInt32());
        Assert.Equal(3, parts["acme_extra.json"].Tables["acme_details"]);
        foreach (string name in new[] { "partitions.json", "rowGroups.json", "columnChunks.json", "commits.json" })
        {
            Assert.NotNull(parts[name].Extensions);
            Assert.Empty(parts[name].Extensions);
        }
    }

    [Fact]
    public void PartInfo_extensions_keep_the_order_of_the_file_and_leave_out_name_and_tables()
    {
        byte[] data = ChangeManifest(Kit.SampleBytes("sample.pqax"), manifest =>
        {
            JsonArray parts = manifest["parts"]!.AsArray();
            int index = parts.Select(part => (string)part!["name"]!).ToList().IndexOf("files.json");
            parts[index] = new JsonObject
            {
                ["zeta"] = 1,
                ["name"] = "files.json",
                ["alpha"] = new JsonObject { ["x"] = new JsonArray(1, 2) },
                ["tables"] = new JsonObject { ["files"] = 5 },
                ["mid"] = "text",
            };
        });
        using PqaxDocument doc = Open(data);
        PartInfo files = doc.Manifest.Parts.Single(part => part.Name == "files.json");
        Assert.Equal(["zeta", "alpha", "mid"], files.Extensions.Keys);
        Assert.Equal(1, files.Extensions["zeta"].GetInt32());
        Assert.Equal("{\"x\":[1,2]}", Kit.Canonical(files.Extensions["alpha"]));
        Assert.Equal("text", files.Extensions["mid"].GetString());
        Assert.Equal(5, files.Tables["files"]);
        Assert.All(doc.Manifest.Parts.Where(part => part.Name != "files.json"), part => Assert.Empty(part.Extensions));
        Assert.Equal(5, doc.ReadFiles().Count);
    }

    // ---- a rewrite keeps the parts ----

    [Theory]
    [InlineData("extensions.pqax")]
    [InlineData("multipart.pqax")]
    public void A_rewrite_has_exactly_the_parts_of_the_source_and_so_has_a_second_one(string file)
    {
        byte[] source = Kit.SampleBytes(file);
        byte[] first = Rewrite(source);
        byte[] second = Rewrite(first);
        List<string> expected = Parts(source).Select(Describe).ToList();
        foreach (byte[] copy in new[] { first, second })
        {
            // The names, in order, the rows stated for each table, and every member of each descriptor.
            Assert.Equal(expected, Parts(copy).Select(Describe));
            using PqaxDocument original = Open(source);
            using PqaxDocument doc = Open(copy);
            Assert.Equal(original.Manifest.Parts.Select(Describe), doc.Manifest.Parts.Select(Describe));
            AssertSameTables(original, doc);
            AssertValid(copy);
        }
    }

    [Fact]
    public void Multipart_keeps_its_parts_of_files_and_column_chunks()
    {
        byte[] data = Rewrite(Kit.SampleBytes("multipart.pqax"));
        using PqaxDocument doc = Open(data);
        Assert.Equal(
            ["summary.json", "partitions.json", "files.1.json", "files.2.json", "rowGroups.json", "columnChunks.1.json", "columnChunks.2.json", "columnChunks.3.json", "commits.json"],
            doc.Manifest.Parts.Select(part => part.Name));
        Assert.Equal([2, 3, 2, 13, 20, 20, 12, 6], doc.Manifest.Parts.Skip(1).Select(part => part.Tables.Values.Single()));
        Assert.Equal([40L, null, null, null, null], doc.ReadFiles().Select(file => file.DeletionVectorSize));
    }

    [Fact]
    public void Rows_added_after_From_go_to_a_new_part_after_the_kept_one()
    {
        byte[] source = Kit.SampleBytes("sample.pqax");
        byte[] data = Rewrite(source, change: AddTwoFiles);
        List<JsonObject> before = Parts(source);
        List<JsonObject> after = Parts(data);
        List<string> written = Names(after);
        Assert.Equal("summary.json", written[0]);
        // Every part of the source is kept, in the order of the source, and the added rows are in files.2.json.
        Assert.Equal(Names(before), written.Where(name => name != "files.2.json"));
        Assert.True(written.IndexOf("files.2.json") > written.IndexOf("files.json"), string.Join(", ", written));
        Dictionary<string, JsonObject> parts = ByName(after);
        foreach (JsonObject part in before.Skip(1))
        {
            Assert.Equal(Describe(part), Describe(parts[(string)part["name"]!]));
        }

        Assert.Equal("{\"name\":\"files.2.json\",\"tables\":{\"files\":2}}", Describe(parts["files.2.json"]));
        using PqaxDocument original = Open(source);
        using PqaxDocument copy = Open(data);
        PqaxTable<FileRow> files = copy.ReadFiles();
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L, 7L], files.Select(file => file.Id));
        Assert.Equal(original.ReadFiles().Rows, files.Rows.Take(5));
        Assert.Equal([3L, 3L], files.Rows.Skip(5).Select(file => file.Table));
        Assert.Equal([10L, 20L], files.Rows.Skip(5).Select(file => file.FileSize));
        AssertValid(data);
    }

    [Fact]
    public void A_new_part_whose_name_is_taken_without_case_gets_a_suffix()
    {
        byte[] source = RenamePart(Kit.SampleBytes("sample.pqax"), "commits.json", "FILES.2.json");
        AssertValid(source);
        byte[] data = Rewrite(source, change: AddTwoFiles);
        List<string> written = Names(Parts(data));
        Assert.Equal(Names(Parts(source)), written.Where(name => name != "files.2_2.json"));
        Assert.True(written.IndexOf("files.2_2.json") > written.IndexOf("files.json"), string.Join(", ", written));
        Dictionary<string, JsonObject> parts = ByName(Parts(data));
        Assert.Equal("{\"name\":\"FILES.2.json\",\"tables\":{\"commits\":6}}", Describe(parts["FILES.2.json"]));
        Assert.Equal("{\"name\":\"files.2_2.json\",\"tables\":{\"files\":2}}", Describe(parts["files.2_2.json"]));
        Assert.Equal(written.Count, written.Select(name => name.ToLowerInvariant()).Distinct().Count());
        AssertValid(data);
    }

    [Fact]
    public void A_part_listed_twice_is_kept_once_with_its_first_descriptor()
    {
        byte[] source = ChangeManifest(Kit.SampleBytes("sample.pqax"), manifest =>
        {
            JsonArray parts = manifest["parts"]!.AsArray();
            parts.Single(part => (string)part!["name"]! == "commits.json")!["acme_note"] = "first";
            parts.Add(new JsonObject { ["name"] = "commits.json", ["tables"] = new JsonObject { ["commits"] = 6 }, ["acme_note"] = "second" });
        });
        byte[] data = Rewrite(source);
        List<JsonObject> parts = Parts(data);
        Assert.Equal(Names(Parts(Kit.SampleBytes("sample.pqax"))), Names(parts));
        Assert.Equal("{\"acme_note\":\"first\",\"name\":\"commits.json\",\"tables\":{\"commits\":6}}", Describe(ByName(parts)["commits.json"]));
        AssertSameTables(source, data);
        AssertValid(data);
    }

    // ---- the tables that keep no part ----

    [Fact]
    public void A_smaller_part_size_keeps_every_part_that_fits_and_lays_out_the_others_after_them()
    {
        byte[] source = Kit.SampleBytes("sample.pqax");
        byte[] data = Rewrite(source, rowsPerPart: 20);
        List<JsonObject> parts = Parts(data);
        Assert.Equal(
            ["summary.json", "partitions.json", "files.json", "rowGroups.json", "commits.json", "columnChunks.1.json", "columnChunks.2.json", "columnChunks.3.json"],
            Names(parts));
        Assert.Equal([2, 5, 13, 6, 20, 20, 12], parts.Skip(1).Select(part => part["tables"]!.AsObject().Single().Value!.GetValue<int>()));
        Assert.All(parts, part => Assert.Equal(DescriptorMembers, part.Select(pair => pair.Key).Order(StringComparer.Ordinal)));
        Assert.Equal(StatedRows(Parts(source)).OrderBy(pair => pair.Key), StatedRows(parts).OrderBy(pair => pair.Key));
        AssertSameTables(source, data);
        AssertValid(data);
    }

    [Fact]
    public void A_table_that_keeps_no_part_drops_the_members_of_its_descriptors()
    {
        byte[] source = Kit.SampleBytes("extensions.pqax");
        byte[] data = Rewrite(source, rowsPerPart: 4);
        Dictionary<string, JsonObject> before = ByName(Parts(source));
        List<JsonObject> after = Parts(data);

        // summary.json keeps its members, whatever the size of the parts.
        Assert.Equal(Describe(before["summary.json"]), Describe(after[0]));

        // The two parts that hold at most 4 rows are kept, in the order of the source, right after summary.json.
        Assert.Equal([Describe(before["partitions.json"]), Describe(before["acme_extra.json"])], after.Skip(1).Take(2).Select(Describe));

        // The other tables are laid out by the writer, with no member in their descriptors.
        Dictionary<string, JsonObject> written = ByName(after);
        Assert.DoesNotContain("files.json", written.Keys);
        Assert.Equal("{\"name\":\"files.1.json\",\"tables\":{\"files\":4}}", Describe(written["files.1.json"]));
        Assert.Equal("{\"name\":\"files.2.json\",\"tables\":{\"files\":1}}", Describe(written["files.2.json"]));
        foreach (JsonObject part in after.Skip(3))
        {
            string name = (string)part["name"]!;
            Assert.Equal(DescriptorMembers, part.Select(pair => pair.Key).Order(StringComparer.Ordinal));
            KeyValuePair<string, JsonNode?> table = Assert.Single(part["tables"]!.AsObject());
            Assert.True(table.Value!.GetValue<int>() <= 4, name);
            Assert.Matches("^" + Regex.Escape(table.Key) + @"\.[0-9]+\.json$", name);
        }

        foreach (string table in new[] { "files", "rowGroups", "columnChunks", "commits" })
        {
            List<string> listing = Names(after.Where(part => part["tables"]!.AsObject().ContainsKey(table)));
            Assert.Equal(Enumerable.Range(1, listing.Count).Select(k => $"{table}.{k}.json"), listing);
        }

        Assert.Equal(StatedRows(before.Values).OrderBy(pair => pair.Key), StatedRows(after).OrderBy(pair => pair.Key));
        AssertSameTables(source, data);
        AssertValid(data);
    }

    /// <summary>
    /// For each invalid sample: the parts that a rewrite keeps, in order after summary.json, and the parts that the writer
    /// lays out after them, in any order.
    /// </summary>
    public static TheoryData<string, string[], string[]> Layouts => new()
    {
        // The rows stated for commits, 7, are not the rows of the table, 6.
        { "part.rowCount.manifest.pqax", ["partitions.json", "files.json", "rowGroups.json", "columnChunks.json"], ["commits.json"] },
        // files.json lists two tables.
        { "part.singleTable.pqax", ["partitions.json", "columnChunks.json", "commits.json"], ["files.json", "rowGroups.json"] },
        // commits is in summary.json, which a detail table never keeps.
        { "part.kind.detail-in-summary.pqax", ["partitions.json", "files.json", "rowGroups.json", "columnChunks.json"], ["commits.json"] },
        // operations is a summary table, which never keeps a detail part.
        { "part.kind.summary-in-detail.pqax", ["partitions.json", "files.json", "rowGroups.json", "columnChunks.json", "commits.json"], [] },
        // The part of commits is named "commits 1.json", which is not a valid name.
        { "container.partName.pqax", ["partitions.json", "files.json", "rowGroups.json", "columnChunks.json"], ["commits.json"] },
        // files.json is listed before summary.json, which a rewrite writes first.
        { "manifest.firstPart.pqax", ["files.json", "partitions.json", "rowGroups.json", "columnChunks.json", "commits.json"], [] },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void A_part_that_cannot_be_kept_is_laid_out_by_the_writer(string file, string[] kept, string[] laidOut)
    {
        byte[] source = Kit.InvalidBytes(file);
        byte[] data = Rewrite(source);
        List<JsonObject> parts = Parts(data);
        List<string> written = Names(parts);
        Assert.Equal("summary.json", written[0]);
        Assert.Equal(kept, written.Skip(1).Take(kept.Length));
        Assert.Equal(laidOut.Order(StringComparer.Ordinal), written.Skip(1 + kept.Length).Order(StringComparer.Ordinal));
        using PqaxDocument original = Open(source);
        foreach (JsonObject part in parts.Skip(1))
        {
            string name = (string)part["name"]!;
            KeyValuePair<string, JsonNode?> table = Assert.Single(part["tables"]!.AsObject());
            Assert.DoesNotContain(table.Key, Kit.SummaryTables);
            if (laidOut.Contains(name))
            {
                Assert.Equal($"{table.Key}.json", name);
            }
        }

        // The rows that the rewrite states are the rows of each table as the reader returns them, whatever the source stated.
        foreach ((string table, int rows) in StatedRows(parts))
        {
            int read = Kit.FormatTables.Contains(table) ? Kit.Typed(original, table).Count : original.ReadRaw(table).Count;
            Assert.True(read == rows, $"{table}: {rows} rows stated, {read} rows read");
        }

        AssertSameTables(source, data);
        AssertValid(data);
    }

    // ---- a builder built from nothing ----

    [Fact]
    public void A_builder_built_from_nothing_writes_no_member_in_the_descriptors()
    {
        var builder = new FootprintBuilder(new CollectionInfo { CollectorApp = "pqax-tests", Level = 1 });
        builder.Options.RowsPerPart = 2;
        builder.AddSource(new SourceRow { Kind = "Storage folder" });
        builder.AddTable(new TableRow { Source = 1, Name = "a", Format = "Parquet", DetailDepth = "Files", FileCount = 3, FileSize = 6 });
        for (int size = 1; size <= 3; size++)
        {
            builder.AddFile(new FileRow { Table = 1, FileSize = size });
        }

        byte[] data = Write(builder);
        List<JsonObject> parts = Parts(data);
        Assert.Equal(["summary.json", "files.1.json", "files.2.json"], Names(parts));
        Assert.All(parts, part => Assert.Equal(DescriptorMembers, part.Select(pair => pair.Key).Order(StringComparer.Ordinal)));
        using PqaxDocument doc = Open(data);
        Assert.All(doc.Manifest.Parts, part =>
        {
            Assert.NotNull(part.Extensions);
            Assert.Empty(part.Extensions);
        });
        AssertValid(data);

        byte[] empty = Write(new FootprintBuilder(new CollectionInfo { Level = 1 }));
        Assert.Equal("[{\"name\":\"summary.json\",\"tables\":{}}]", Kit.Canonical(new JsonArray(Parts(empty).Select(part => (JsonNode?)part.DeepClone()).ToArray())));
        AssertValid(empty);
    }
}
