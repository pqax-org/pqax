using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Parquet;
using Parquet.Serialization;

namespace Pqax.Collector.Tests;

/// <summary>
/// The physical columns of nested columns, from the rules N1 to N6 of conformance/footers.md: one physical column
/// for each leaf, with every name of its path and the types of the leaf itself; its column, by field id or by name; the
/// totals of a column over all its leaves; and the types of the nested columns of a folder of Parquet files. The files
/// are written by Parquet.Net, whose own reading of the schema is the oracle of the paths and types, and the chunks of
/// the footer parsed with <see cref="ParquetFooter.Parse(byte[], bool)"/> are the oracle of the totals; and by the
/// synthetic footers of <see cref="Footers.SchemaFooter"/>, written as collect_kit.schema_footer writes them for
/// test_collect_nested.py, so that the two suites expect the same rows for the same bytes (N6).
/// </summary>
public class NestedColumnTests
{
    private static readonly DateTime Older = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Newer = Older.AddHours(1);

    public sealed class Point
    {
        public int X { get; set; }

        public string? Label { get; set; }
    }

    public sealed class Leaf3
    {
        public string? Text { get; set; }

        public List<long>? Numbers { get; set; }
    }

    public sealed class Inner2
    {
        public int? Value { get; set; }

        public Leaf3? Leaf { get; set; }
    }

    public sealed class Outer1
    {
        public string? Name { get; set; }

        public Inner2? Inner { get; set; }
    }

    /// <summary>The rows of the most recent file: lists, maps, structures three levels deep, a list of structures, a map of lists.</summary>
    public sealed class NewerRow
    {
        public int Id { get; set; }

        public List<int?>? Tags { get; set; }

        public Dictionary<string, long?>? Props { get; set; }

        public Outer1? Deep { get; set; }

        public List<Point?>? Points { get; set; }

        public Dictionary<string, List<int>?>? MapOfLists { get; set; }

        public string? Category { get; set; }
    }

    /// <summary>The rows of an older file: a column in another case, a structure, and a column that the newer file no longer has.</summary>
    public sealed class OlderRow
    {
        public int Id { get; set; }

        public List<int?>? TAGS { get; set; }

        public Outer1? Deep { get; set; }

        public Dictionary<string, string?>? Retired { get; set; }
    }

    /// <summary>Nulls at each level: a null value, an empty list or map, a null element, a null field of a structure.</summary>
    private static List<NewerRow> NewerRows() =>
    [
        new() { Id = 1, Tags = [1, null, 3], Props = new() { ["a"] = 1, ["b"] = null }, Deep = new() { Name = "x", Inner = new() { Value = 5, Leaf = new() { Text = "t", Numbers = [1, 2] } } }, Points = [new() { X = 1, Label = "p" }, null], MapOfLists = new() { ["k"] = [1, 2], ["n"] = null }, Category = "a" },
        new() { Id = 2 },
        new() { Id = 3, Tags = [], Props = new(), Deep = new() { Name = null, Inner = null }, Points = [], MapOfLists = new(), Category = "a" },
        new() { Id = 4, Tags = [null], Props = new() { ["c"] = null }, Deep = new() { Name = "y", Inner = new() { Value = null, Leaf = null } }, Points = [null], MapOfLists = new() { ["e"] = [] }, Category = "b" },
        new() { Id = 5, Tags = [7], Props = new() { ["d"] = 4 }, Deep = new() { Name = "z", Inner = new() { Value = 7, Leaf = new() { Text = null, Numbers = null } } }, Points = [new() { X = 2, Label = null }], MapOfLists = new() { ["f"] = [9] }, Category = "a" },
        new() { Id = 6, Tags = [8, 9], Deep = new() { Name = "w", Inner = new() { Value = 8, Leaf = new() { Text = "u", Numbers = [] } } }, Category = "a" },
    ];

    private static List<OlderRow> OlderRows() =>
    [
        new() { Id = 10, TAGS = [1], Deep = new() { Name = "o", Inner = new() { Value = 1, Leaf = new() { Text = "old", Numbers = [5] } } }, Retired = new() { ["r"] = "v" } },
        new() { Id = 11 },
        new() { Id = 12, TAGS = [null, 2], Deep = new() { Name = null }, Retired = new() { ["s"] = null } },
        new() { Id = 13, TAGS = [], Retired = new() },
    ];

    private const string Folder = "data/nested";
    private const string OlderFile = "part-0001.parquet";
    private const string NewerFile = "part-0002.parquet";

    private static async Task WriteAsync<T>(TempLake lake, string path, IEnumerable<T> rows, DateTime modified, ParquetSerializerOptions options)
    {
        string full = lake.FullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (FileStream stream = File.Create(full))
        {
            await ParquetSerializer.SerializeAsync(rows, stream, options);
        }

        File.SetLastWriteTimeUtc(full, modified);
    }

    /// <summary>A folder with the two files of two writers' settings: the newer one with Snappy, the older one with Gzip, both in row groups of a few rows.</summary>
    private static async Task<TempLake> TwoFilesAsync()
    {
        var lake = new TempLake();
        await WriteAsync(lake, Folder + "/" + OlderFile, OlderRows(), Older, new ParquetSerializerOptions { RowGroupSize = 2, CompressionMethod = CompressionMethod.Gzip });
        await WriteAsync(lake, Folder + "/" + NewerFile, NewerRows(), Newer, new ParquetSerializerOptions { RowGroupSize = 3 });
        return lake;
    }

    private static byte[] MetadataOf(string file)
    {
        byte[] bytes = File.ReadAllBytes(file);
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(bytes.Length - 8, 4));
        return bytes.AsSpan(bytes.Length - 8 - length, length).ToArray();
    }

    /// <summary>A leaf as Parquet.Net reads the schema of the file: the oracle of N1 and N2.</summary>
    private sealed record WriterLeaf(string Path, string Type, int? TypeLength, string? Repetition, int? FieldId);

    private static async Task<List<WriterLeaf>> ParquetNetLeavesAsync(string file)
    {
        using FileStream stream = File.OpenRead(file);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        List<Parquet.Meta.SchemaElement> schema = reader.Metadata!.Schema;
        var leaves = new List<WriterLeaf>();
        int index = 1;

        void Walk(int children, List<string> path)
        {
            for (int i = 0; i < children; i++)
            {
                Parquet.Meta.SchemaElement element = schema[index++];
                path.Add(element.Name);
                if (element.Type == null)
                {
                    Walk(element.NumChildren ?? 0, path);
                }
                else
                {
                    leaves.Add(new WriterLeaf(string.Join(".", path), element.Type.Value.ToString(), element.TypeLength, element.RepetitionType?.ToString(), element.FieldId));
                }

                path.RemoveAt(path.Count - 1);
            }
        }

        Walk(schema[0].NumChildren ?? 0, []);

        // Parquet.Net's own fields give the same paths.
        Assert.Equal(leaves.Select(leaf => leaf.Path), reader.Schema.GetDataFields().Select(field => string.Join(".", field.Path.ToList())));
        return leaves;
    }

    private static List<PhysicalColumnRow> PhysicalColumnsOfFile(PqaxDocument doc, TableRow table, string file)
    {
        FileRow row = Assert.Single(doc.ReadFiles(), f => f.Table == table.Id && f.Path == file);
        return FolderFootprint.PhysicalColumnsOf(doc, table).Where(column => column.PhysicalSchema == row.PhysicalSchema).OrderBy(column => column.Ordinal).ToList();
    }

    private static string PathOf(PhysicalColumnRow column) => string.Join(".", column.Path!);

    // ---- N1 and N2: one physical column for each leaf, with its path and its own types ----

    /// <summary>The leaves of the newer file as Parquet.Net writes them: path, type, repetition, annotation.</summary>
    private static readonly (string Path, string Type, string Repetition, string Annotation)[] NewerLeaves =
    [
        ("Id", "INT32", "REQUIRED", "INTEGER(32, SIGNED)"),
        ("Tags.list.element", "INT32", "OPTIONAL", "INTEGER(32, SIGNED)"),
        ("Props.key_value.key", "BYTE_ARRAY", "REQUIRED", "STRING"),
        ("Props.key_value.value", "INT64", "OPTIONAL", "INTEGER(64, SIGNED)"),
        ("Deep.Name", "BYTE_ARRAY", "OPTIONAL", "STRING"),
        ("Deep.Inner.Value", "INT32", "OPTIONAL", "INTEGER(32, SIGNED)"),
        ("Deep.Inner.Leaf.Text", "BYTE_ARRAY", "OPTIONAL", "STRING"),
        ("Deep.Inner.Leaf.Numbers.list.element", "INT64", "REQUIRED", "INTEGER(64, SIGNED)"),
        ("Points.list.element.X", "INT32", "REQUIRED", "INTEGER(32, SIGNED)"),
        ("Points.list.element.Label", "BYTE_ARRAY", "OPTIONAL", "STRING"),
        ("MapOfLists.key_value.key", "BYTE_ARRAY", "REQUIRED", "STRING"),
        ("MapOfLists.key_value.value.list.element", "INT32", "REQUIRED", "INTEGER(32, SIGNED)"),
        ("Category", "BYTE_ARRAY", "OPTIONAL", "STRING"),
    ];

    [Fact]
    public async Task Each_leaf_of_a_nested_schema_is_one_physical_column_with_every_name_of_its_path_in_the_order_of_the_leaves()
    {
        using TempLake lake = await TwoFilesAsync();
        using PqaxDocument doc = await FolderFootprint.CollectAsync(lake, Folder);
        TableRow table = Assert.Single(doc.Summary.Tables);
        Assert.Empty(FolderFootprint.IssuesOf(doc, table).Select(issue => issue.Code + ": " + issue.Message));
        Assert.Equal(2, FolderFootprint.PhysicalSchemasOf(doc, table).Count);
        foreach (string file in new[] { OlderFile, NewerFile })
        {
            List<WriterLeaf> expected = await ParquetNetLeavesAsync(lake.FullPath(Folder + "/" + file));
            List<PhysicalColumnRow> physical = PhysicalColumnsOfFile(doc, table, file);
            Assert.Equal(Enumerable.Range(0, expected.Count).Select(i => (long?)i), physical.Select(column => column.Ordinal));
            Assert.Equal(expected.Select(leaf => leaf.Path), physical.Select(PathOf));
        }

        Assert.Equal(NewerLeaves.Select(leaf => leaf.Path), PhysicalColumnsOfFile(doc, table, NewerFile).Select(PathOf));
    }

    [Fact]
    public async Task A_physical_column_has_the_types_of_its_leaf_and_not_those_of_the_groups_above_it()
    {
        using TempLake lake = await TwoFilesAsync();
        using PqaxDocument doc = await FolderFootprint.CollectAsync(lake, Folder);
        TableRow table = Assert.Single(doc.Summary.Tables);
        foreach (string file in new[] { OlderFile, NewerFile })
        {
            string full = lake.FullPath(Folder + "/" + file);
            List<WriterLeaf> expected = await ParquetNetLeavesAsync(full);
            ParquetFooter footer = ParquetFooter.Parse(MetadataOf(full));
            List<PhysicalColumnRow> physical = PhysicalColumnsOfFile(doc, table, file);
            Assert.Equal(
                expected.Select(leaf => (leaf.Path, leaf.Type, (long?)leaf.TypeLength, leaf.Repetition, (long?)leaf.FieldId)),
                physical.Select(column => (PathOf(column), column.PhysicalType!, column.TypeLength, column.Repetition, column.FieldId)));
            Assert.Equal(footer.Leaves.Select(leaf => leaf.TypeAnnotation), physical.Select(column => column.LogicalType));
        }

        // The annotation and the repetition of the leaf itself: a LIST or MAP group, a REPEATED group, and an OPTIONAL structure above.
        Assert.Equal(NewerLeaves, PhysicalColumnsOfFile(doc, table, NewerFile).Select(column => (PathOf(column), column.PhysicalType!, column.Repetition!, column.LogicalType!)));
    }

    [Fact]
    public async Task The_field_id_of_a_physical_column_is_that_of_its_leaf_and_not_of_its_top_level_field()
    {
        using var lake = new TempLake();
        Node[] fields =
        [
            Node.Group("tags", "OPTIONAL", null, "LIST", Node.Group("list", "REPEATED", null, null, Node.Leaf("element", "INT32", fieldId: 21)) with { FieldId = 20 }) with { FieldId = 2 },
            Node.Group("point", Node.Leaf("x", "DOUBLE", fieldId: 31), Node.Leaf("y", "DOUBLE")) with { FieldId = 3 },
            Node.Leaf("plain", "INT64", fieldId: 4),
        ];
        lake.Write("data/ids/part-0001.parquet", Footers.FileOf(Footers.SchemaFooter(fields)));
        using PqaxDocument doc = await FolderFootprint.CollectAsync(lake, "data/ids");
        TableRow table = Assert.Single(doc.Summary.Tables);
        List<PhysicalColumnRow> physical = FolderFootprint.PhysicalColumnsOf(doc, table).OrderBy(column => column.Ordinal).ToList();
        Assert.Equal(
            [("tags.list.element", (long?)21), ("point.x", 31), ("point.y", null), ("plain", 4)],
            physical.Select(column => (PathOf(column), column.FieldId)));

        // A folder of Parquet files has no field ids, so the leaves find their column by name.
        List<ColumnRow> columns = FolderFootprint.ColumnsOf(doc, table);
        Assert.All(columns, column => Assert.Null(column.FieldId));
        Assert.Equal(["tags", "point", "point", "plain"], physical.Select(column => columns.Single(c => c.Id == column.Column).Name));
    }

    // ---- N3: the column of a leaf ----

    [Fact]
    public async Task The_column_of_a_leaf_is_its_top_level_field_by_name_exactly_then_without_case_and_a_field_that_the_table_does_not_declare_is_stale()
    {
        using TempLake lake = await TwoFilesAsync();
        using PqaxDocument doc = await FolderFootprint.CollectAsync(lake, Folder);
        TableRow table = Assert.Single(doc.Summary.Tables);
        List<ColumnRow> columns = FolderFootprint.ColumnsOf(doc, table);
        Assert.Equal(["Id", "Tags", "Props", "Deep", "Points", "MapOfLists", "Category"], columns.Select(column => column.Name));
        string? NameOf(PhysicalColumnRow column) => column.Column == null ? null : columns.Single(c => c.Id == column.Column).Name;

        Assert.All(PhysicalColumnsOfFile(doc, table, NewerFile), column => Assert.Equal(column.Path![0], NameOf(column)));
        List<PhysicalColumnRow> older = PhysicalColumnsOfFile(doc, table, OlderFile);
        Assert.Equal(
            older.Select(column => column.Path![0] switch { "TAGS" => "Tags", "Retired" => null, string name => name }),
            older.Select(NameOf));

        // The leaves of Retired, which the table does not declare, are the stale size.
        ParquetFooter footer = ParquetFooter.Parse(MetadataOf(lake.FullPath(Folder + "/" + OlderFile)));
        long stale = footer.RowGroups.Sum(group => group.Columns.Where((chunk, c) => footer.Leaves[c].Path[0] == "Retired").Sum(chunk => chunk.TotalCompressedSize));
        Assert.True(stale > 0);
        Assert.Equal(stale, table.StaleColumnSize);
    }

    /// <summary>A folder of two crafted files whose leaves are top-level fields: the newer file gives the columns.</summary>
    private static async Task<PqaxDocument> TwoCraftedFilesAsync(TempLake lake, string folder, string[] newer, string[] older)
    {
        lake.Write(folder + "/" + NewerFile, Footers.FileOf(Footers.SchemaFooter(newer.Select(name => Node.Leaf(name, "INT64")).ToArray())));
        File.SetLastWriteTimeUtc(lake.FullPath(folder + "/" + NewerFile), Newer);
        lake.Write(folder + "/" + OlderFile, Footers.FileOf(Footers.SchemaFooter(older.Select(name => Node.Leaf(name, "INT64")).ToArray())));
        File.SetLastWriteTimeUtc(lake.FullPath(folder + "/" + OlderFile), Older);
        return await FolderFootprint.CollectAsync(lake, folder);
    }

    [Fact]
    public async Task A_leaf_that_matches_two_columns_without_case_and_none_exactly_is_stale_and_an_exact_match_wins()
    {
        using var lake = new TempLake();
        using PqaxDocument doc = await TwoCraftedFilesAsync(lake, "data/case", ["dup", "DUP", "Mixed", "MIXED", "x"], ["Dup", "MIXED", "X"]);
        TableRow table = Assert.Single(doc.Summary.Tables);
        List<ColumnRow> columns = FolderFootprint.ColumnsOf(doc, table);
        Assert.Equal(["dup", "DUP", "Mixed", "MIXED", "x"], columns.Select(column => column.Name));
        List<PhysicalColumnRow> older = PhysicalColumnsOfFile(doc, table, OlderFile);
        Assert.Equal([null, "MIXED", "x"], older.Select(column => column.Column == null ? null : columns.Single(c => c.Id == column.Column).Name));

        // The synthetic footers give a leaf of ordinal i the compressed size 50 + i: Dup is the first leaf.
        Assert.Equal(50, table.StaleColumnSize);
        Assert.Equal([1L, 1L, 1L, 0L, 0L], columns.Select(column => column.FileCountWithoutColumn!.Value));
    }

    [Fact]
    public async Task A_column_of_a_long_s_does_not_match_a_leaf_of_an_s_and_an_e_acute_matches_its_capital_as_ordinal_ignore_case_compares()
    {
        // P4 as amended: the same fold for column names and for the keys of the allow-list; a character outside ASCII
        // never folds into ASCII, and the others fold to their upper case.
        using var lake = new TempLake();
        using PqaxDocument doc = await TwoCraftedFilesAsync(lake, "data/fold", ["ſum", "Été", "both"], ["sum", "été", "BOTH"]);
        TableRow table = Assert.Single(doc.Summary.Tables);
        List<ColumnRow> columns = FolderFootprint.ColumnsOf(doc, table);
        Assert.Equal(["ſum", "Été", "both"], columns.Select(column => column.Name));
        List<PhysicalColumnRow> older = PhysicalColumnsOfFile(doc, table, OlderFile);
        Assert.Equal([null, "Été", "both"], older.Select(column => column.Column == null ? null : columns.Single(c => c.Id == column.Column).Name));
        Assert.Equal(50, table.StaleColumnSize);
        Assert.Equal([1L, 0L, 0L], columns.Select(column => column.FileCountWithoutColumn!.Value));
    }

    /// <summary>The schema of a Delta table under column mapping by id: amount, tags, and point, with their ids and physical names.</summary>
    private static string MappedSchema()
    {
        JsonObject Mapping(long id, string physical) => new() { ["delta.columnMapping.id"] = id, ["delta.columnMapping.physicalName"] = physical };
        return new JsonObject
        {
            ["type"] = "struct",
            ["fields"] = new JsonArray(
                new JsonObject { ["name"] = "amount", ["type"] = "long", ["nullable"] = true, ["metadata"] = Mapping(1, "col-amount") },
                new JsonObject
                {
                    ["name"] = "tags",
                    ["type"] = new JsonObject { ["type"] = "array", ["elementType"] = "integer", ["containsNull"] = true },
                    ["nullable"] = true,
                    ["metadata"] = Mapping(2, "col-tags"),
                },
                new JsonObject
                {
                    ["name"] = "point",
                    ["type"] = new JsonObject
                    {
                        ["type"] = "struct",
                        ["fields"] = new JsonArray(new JsonObject { ["name"] = "x", ["type"] = "double", ["nullable"] = true, ["metadata"] = Mapping(4, "col-x") }),
                    },
                    ["nullable"] = true,
                    ["metadata"] = Mapping(3, "col-point"),
                }),
        }.ToJsonString();
    }

    private static Node TagsList(string name, int? listFieldId = null, int? elementFieldId = null) =>
        Node.Group(name, "OPTIONAL", null, "LIST", Node.Group("list", "REPEATED", null, null, Node.Leaf("element", "INT32", fieldId: elementFieldId))) with { FieldId = listFieldId };

    [Fact]
    public async Task The_column_of_a_leaf_is_found_by_the_field_id_of_its_top_level_field_or_else_by_the_physical_name_of_a_mapped_column()
    {
        using var lake = new TempLake();
        const string table = "data/mapped";

        // With field ids: a name that is no physical name but the id of amount, and the physical name of amount with an
        // id that no column has.
        Node[] withIds =
        [
            Node.Leaf("other-name", "INT64", fieldId: 1),
            TagsList("col-tags", 2, 5),
            Node.Group("col-point", Node.Leaf("col-x", "DOUBLE", fieldId: 4)) with { FieldId = 3 },
            Node.Leaf("col-amount", "INT32", fieldId: 99),
        ];

        // Without field ids: the physical names, one in another case, and the logical name of a column, which the files do not use.
        Node[] withoutIds =
        [
            Node.Leaf("col-amount", "INT64"),
            TagsList("COL-TAGS"),
            Node.Leaf("amount", "INT64"),
            Node.Group("col-point", Node.Leaf("col-x", "DOUBLE")),
        ];
        byte[] first = Footers.FileOf(Footers.SchemaFooter(withIds, rows: 3));
        byte[] second = Footers.FileOf(Footers.SchemaFooter(withoutIds, rows: 4));
        lake.Write(table + "/with-ids.parquet", first);
        lake.Write(table + "/without-ids.parquet", second);
        JsonObject metadata = LogLines.Metadata();
        metadata["metaData"]!["schemaString"] = MappedSchema();
        metadata["metaData"]!["configuration"] = new JsonObject { ["delta.columnMapping.mode"] = "id", ["delta.columnMapping.maxColumnId"] = "5" };
        JsonObject protocol = new() { ["protocol"] = new JsonObject { ["minReaderVersion"] = 2, ["minWriterVersion"] = 5 } };
        lake.Commit(table, 0, protocol, metadata, LogLines.Add("with-ids.parquet", first.Length, 3), LogLines.Add("without-ids.parquet", second.Length, 4), LogLines.CommitInfo(0));

        var storage = new LocalStorage(lake.Root);
        TableLocation location = Assert.Single(await TableDiscovery.DiscoverFolderAsync(storage, table, CancellationToken.None));
        Assert.Equal(TableFormats.Delta, location.Format);
        using PqaxDocument doc = await FolderFootprint.CollectAsync(storage, location);
        TableRow row = Assert.Single(doc.Summary.Tables);
        Assert.Empty(FolderFootprint.IssuesOf(doc, row).Select(issue => issue.Code + ": " + issue.Message));
        List<ColumnRow> columns = FolderFootprint.ColumnsOf(doc, row);
        Assert.Equal([("amount", (long?)1, "col-amount"), ("tags", 2, "col-tags"), ("point", 3, "col-point")], columns.Select(column => (column.Name!, column.FieldId, column.PhysicalName)));
        string? NameOf(PhysicalColumnRow column) => column.Column == null ? null : columns.Single(c => c.Id == column.Column).Name;

        Assert.Equal(
            [("other-name", "amount"), ("col-tags.list.element", "tags"), ("col-point.col-x", "point"), ("col-amount", null)],
            PhysicalColumnsOfFile(doc, row, "with-ids.parquet").Select(column => (PathOf(column), NameOf(column))));
        Assert.Equal(
            [("col-amount", "amount"), ("COL-TAGS.list.element", "tags"), ("amount", null), ("col-point.col-x", "point")],
            PhysicalColumnsOfFile(doc, row, "without-ids.parquet").Select(column => (PathOf(column), NameOf(column))));
        Assert.Equal([(long?)1, 5, 4, 99], PhysicalColumnsOfFile(doc, row, "with-ids.parquet").Select(column => column.FieldId));

        // A leaf of ordinal i has the compressed size 50 + i: col-amount (3) of the first file and amount (2) of the second are stale.
        Assert.Equal(53 + 52, row.StaleColumnSize);
        Assert.Equal([(long?)(50 + 50), 51 + 51, 52 + 53], columns.Select(column => column.CompressedSize));
        Assert.Equal([(long?)(3 + 4), 3 + 4, 3 + 4], columns.Select(column => column.ValueCount));
        Assert.All(columns, column => Assert.Equal(0, column.FileCountWithoutColumn));
    }

    // ---- N4: the totals of a column, over all its leaves ----

    private static long? Fallback(ParquetColumnChunk chunk)
    {
        // As the README of the collector says: from the encoding statistics when the footer has them; without them,
        // false when the chunk lists only dictionary and level encodings, and unknown otherwise.
        if (!chunk.HasDictionaryPage)
        {
            return 0;
        }

        if (chunk.EncodingStats != null)
        {
            long data = chunk.EncodingStats.Where(stats => stats.IsDataPage).Sum(stats => (long)stats.Count);
            long dictionary = chunk.EncodingStats.Where(stats => stats.IsDataPage && stats.IsDictionaryEncoded).Sum(stats => (long)stats.Count);
            return dictionary < data ? 1 : 0;
        }

        return chunk.Encodings.All(encoding => encoding is "PLAIN_DICTIONARY" or "RLE_DICTIONARY" or "RLE" or "BIT_PACKED") ? 0 : null;
    }

    /// <summary>A sum that is unknown when a term is, and when there is no term.</summary>
    private static long? Sum(IEnumerable<long?> terms)
    {
        long sum = 0;
        bool any = false;
        foreach (long? term in terms)
        {
            if (term == null)
            {
                return null;
            }

            sum += term.Value;
            any = true;
        }

        return any ? sum : null;
    }

    /// <summary>What the totals of a column must be, from the chunks of its leaves.</summary>
    private static string ExpectedTotals(IReadOnlyList<(ParquetLeafColumn Leaf, ParquetColumnChunk Chunk)> chunks, long filesWithout)
    {
        List<ParquetColumnChunk> all = chunks.Select(pair => pair.Chunk).ToList();
        List<ParquetColumnChunk> byteArrays = chunks.Where(pair => pair.Leaf.PhysicalType == "BYTE_ARRAY").Select(pair => pair.Chunk).ToList();
        string[] codecs = all.Select(chunk => chunk.Codec).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray();
        string[] encodings = all.SelectMany(chunk => chunk.Encodings).Distinct().Order(StringComparer.Ordinal).ToArray();
        long[] distinct = all.Select(chunk => chunk.DistinctCount).OfType<long>().ToArray();
        return Totals(
            Sum(all.Select(chunk => (long?)chunk.TotalCompressedSize)),
            Sum(all.Select(chunk => (long?)chunk.TotalUncompressedSize)),
            all.Count,
            Sum(all.Select(chunk => (long?)chunk.NumValues)),
            Sum(all.Select(chunk => chunk.NullCount)),
            all.Count(chunk => chunk.HasDictionaryPage),
            Sum(all.Select(Fallback)),
            Sum(all.Select(chunk => chunk.HasDictionaryPage ? chunk.DictionaryPageSize : 0)),
            Sum(byteArrays.Select(chunk => chunk.UnencodedByteArrayDataBytes)),
            codecs.Length == 0 ? null : codecs,
            encodings.Length == 0 ? null : encodings,
            distinct.Length == 0 ? null : distinct.Max(),
            filesWithout);
    }

    private static string Totals(long? compressed, long? uncompressed, long? chunks, long? values, long? nulls, long? dictionaryChunks, long? fallbackChunks, long? dictionaryPageSize, long? unencoded, IReadOnlyList<string>? codecs, IReadOnlyList<string>? encodings, long? largestDistinct, long? filesWithout) =>
        $"compressed {compressed}, uncompressed {uncompressed}, chunks {chunks}, values {values}, nulls {nulls}, dictionary chunks {dictionaryChunks}, "
        + $"fallback chunks {fallbackChunks}, dictionary page size {dictionaryPageSize}, unencoded {unencoded}, codecs {(codecs == null ? "-" : string.Join(",", codecs))}, "
        + $"encodings {(encodings == null ? "-" : string.Join(",", encodings))}, largest distinct {largestDistinct}, files without {filesWithout}";

    private static string ActualTotals(ColumnRow column) => Totals(
        column.CompressedSize, column.UncompressedSize, column.ChunkCount, column.ValueCount, column.NullCount, column.DictionaryChunkCount,
        column.FallbackChunkCount, column.DictionaryPageSize, column.UnencodedByteArrayDataBytes, column.Codecs, column.Encodings,
        column.LargestChunkDistinctCount, column.FileCountWithoutColumn);

    [Fact]
    public async Task The_totals_of_a_nested_column_are_over_the_column_chunks_of_all_its_leaves_in_every_file()
    {
        using TempLake lake = await TwoFilesAsync();
        using PqaxDocument doc = await FolderFootprint.CollectAsync(lake, Folder);
        TableRow table = Assert.Single(doc.Summary.Tables);
        List<ColumnRow> columns = FolderFootprint.ColumnsOf(doc, table);

        // The column of each top-level field of the two files, as N3 finds it.
        var mapped = new Dictionary<string, string?> { ["TAGS"] = "Tags", ["Retired"] = null };
        var footers = new[] { OlderFile, NewerFile }.ToDictionary(file => file, file => ParquetFooter.Parse(MetadataOf(lake.FullPath(Folder + "/" + file))));
        var chunks = new List<(string? Column, string File, ParquetLeafColumn Leaf, ParquetColumnChunk Chunk)>();
        foreach ((string file, ParquetFooter footer) in footers)
        {
            foreach (ParquetRowGroup group in footer.RowGroups)
            {
                for (int c = 0; c < group.Columns.Count; c++)
                {
                    ParquetLeafColumn leaf = footer.Leaves[c];
                    string? column = mapped.TryGetValue(leaf.Path[0], out string? name) ? name : leaf.Path[0];
                    chunks.Add((column, file, leaf, group.Columns[c]));
                }
            }
        }

        Assert.True(footers[NewerFile].RowGroups.Count > 1 && footers[OlderFile].RowGroups.Count > 1, "each file has more than one row group");
        Assert.Contains(chunks, chunk => chunk.Chunk.HasDictionaryPage);
        foreach (ColumnRow column in columns)
        {
            var own = chunks.Where(chunk => chunk.Column == column.Name).Select(chunk => (chunk.Leaf, chunk.Chunk)).ToList();
            long without = footers.Count(pair => !chunks.Any(chunk => chunk.File == pair.Key && chunk.Column == column.Name));
            Assert.True(ExpectedTotals(own, without) == ActualTotals(column), $"{column.Name}:\n  expected {ExpectedTotals(own, without)}\n  actual   {ActualTotals(column)}");
        }

        Assert.Equal(1, columns.Single(column => column.Name == "Props").FileCountWithoutColumn);
        Assert.Equal(0, columns.Single(column => column.Name == "Tags").FileCountWithoutColumn);

        // The table: every chunk, and the chunks of the leaves without a column as the stale size.
        Assert.Equal(chunks.Sum(chunk => chunk.Chunk.TotalCompressedSize), table.CompressedSize);
        Assert.Equal(chunks.Where(chunk => chunk.Column == null).Sum(chunk => chunk.Chunk.TotalCompressedSize), table.StaleColumnSize);
        Assert.Equal(2, table.FooterCount);

        // A physical column: the chunks of its leaf in its file.
        foreach (string file in footers.Keys)
        {
            foreach (PhysicalColumnRow physical in PhysicalColumnsOfFile(doc, table, file))
            {
                var own = chunks.Where(chunk => chunk.File == file && chunk.Leaf.Ordinal == physical.Ordinal).ToList();
                Assert.Equal(
                    ((long?)own.Sum(chunk => chunk.Chunk.TotalCompressedSize), (long?)own.Sum(chunk => chunk.Chunk.TotalUncompressedSize), (long?)own.Count),
                    (physical.CompressedSize, physical.UncompressedSize, physical.ChunkCount));
            }
        }
    }

    [Fact]
    public async Task The_footer_parser_reads_the_chunks_of_parquet_net_as_parquet_net_reads_them()
    {
        using TempLake lake = await TwoFilesAsync();
        foreach (string file in new[] { OlderFile, NewerFile })
        {
            string full = lake.FullPath(Folder + "/" + file);
            ParquetFooter footer = ParquetFooter.Parse(MetadataOf(full));
            using FileStream stream = File.OpenRead(full);
            using ParquetReader reader = await ParquetReader.CreateAsync(stream);
            Parquet.Meta.FileMetaData metadata = reader.Metadata!;
            Assert.Equal((metadata.NumRows, metadata.RowGroups.Count, metadata.CreatedBy), (footer.NumRows, footer.RowGroups.Count, footer.CreatedBy));
            for (int g = 0; g < footer.RowGroups.Count; g++)
            {
                for (int c = 0; c < footer.RowGroups[g].Columns.Count; c++)
                {
                    Parquet.Meta.ColumnMetaData expected = metadata.RowGroups[g].Columns[c].MetaData!;
                    ParquetColumnChunk actual = footer.RowGroups[g].Columns[c];
                    Assert.Equal(
                        (expected.Codec.ToString(), expected.NumValues, expected.TotalCompressedSize, expected.TotalUncompressedSize, expected.DataPageOffset, expected.DictionaryPageOffset, expected.Statistics?.NullCount),
                        (actual.Codec!, actual.NumValues, actual.TotalCompressedSize, actual.TotalUncompressedSize, actual.DataPageOffset, actual.DictionaryPageOffset, actual.NullCount));
                    Assert.Equal(expected.Encodings.Select(encoding => encoding.ToString()), actual.Encodings);
                }
            }
        }
    }

    // ---- N5: the types of the nested columns of a folder ----

    [Fact]
    public async Task The_type_of_a_nested_column_of_a_folder_is_in_the_names_of_the_delta_schema_and_its_nullability_that_of_its_top_level_field()
    {
        using TempLake lake = await TwoFilesAsync();
        using PqaxDocument doc = await FolderFootprint.CollectAsync(lake, Folder);
        TableRow table = Assert.Single(doc.Summary.Tables);
        (string, string, bool)[] expected =
        [
            ("Id", "integer", false),
            ("Tags", "array<integer>", true),
            ("Props", "map<string,long>", true),
            ("Deep", "struct<Name:string,Inner:struct<Value:integer,Leaf:struct<Text:string,Numbers:array<long>>>>", true),
            ("Points", "array<struct<X:integer,Label:string>>", true),
            ("MapOfLists", "map<string,array<integer>>", true),
            ("Category", "string", true),
        ];
        Assert.Equal(expected, FolderFootprint.ColumnsOf(doc, table).Select(column => (column.Name!, column.DataType!, column.IsNullable!.Value)));
    }

    /// <summary>The layouts of lists and maps of the specification and of older writers: the field, its column, and its leaves (path, type, repetition, annotation).</summary>
    private static readonly (Node Field, string DataType, bool IsNullable, (string Path, string Type, string? Repetition, string? Annotation)[] Leaves)[] Layouts =
    [
        (Node.Group("tags", "OPTIONAL", null, "LIST", Node.Group("list", "REPEATED", null, null, Node.Leaf("element", "INT32", "REQUIRED"))),
            "array<integer>", true, [("tags.list.element", "INT32", "REQUIRED", null)]),
        (Node.Group("items", "OPTIONAL", null, "LIST", Node.Group("list", "REPEATED", null, null, Node.Leaf("item", "BYTE_ARRAY", logical: "STRING"))),
            "array<string>", true, [("items.list.item", "BYTE_ARRAY", "OPTIONAL", "STRING")]),
        (Node.Group("legacy", "OPTIONAL", "LIST", null, Node.Leaf("value", "INT64", "REPEATED")),
            "array<long>", true, [("legacy.value", "INT64", "REPEATED", null)]),
        (Node.Group("points", "OPTIONAL", "LIST", null, Node.Group("array", "REPEATED", null, null, Node.Leaf("x", "DOUBLE"))),
            "array<struct<x:double>>", true, [("points.array.x", "DOUBLE", "OPTIONAL", null)]),
        (Node.Group("pairs", "OPTIONAL", "LIST", null, Node.Group("pairs_tuple", "REPEATED", null, null, Node.Leaf("a", "INT32"))),
            "array<struct<a:integer>>", true, [("pairs.pairs_tuple.a", "INT32", "OPTIONAL", null)]),
        (Node.Group("hive", "OPTIONAL", "LIST", null, Node.Group("bag", "REPEATED", null, null, Node.Leaf("array_element", "INT32"))),
            "array<integer>", true, [("hive.bag.array_element", "INT32", "OPTIONAL", null)]),
        (Node.Group("spans", "OPTIONAL", null, "LIST", Node.Group("item", "REPEATED", null, null, Node.Leaf("a", "INT32"), Node.Leaf("b", "INT32"))),
            "array<struct<a:integer,b:integer>>", true, [("spans.item.a", "INT32", "OPTIONAL", null), ("spans.item.b", "INT32", "OPTIONAL", null)]),
        (Node.Group("props", "OPTIONAL", null, "MAP", Node.Group("key_value", "REPEATED", null, null, Node.Leaf("key", "BYTE_ARRAY", "REQUIRED", logical: "STRING"), Node.Leaf("value", "INT64"))),
            "map<string,long>", true, [("props.key_value.key", "BYTE_ARRAY", "REQUIRED", "STRING"), ("props.key_value.value", "INT64", "OPTIONAL", null)]),
        (Node.Group("old_map", "OPTIONAL", "MAP_KEY_VALUE", null, Node.Group("map", "REPEATED", null, null, Node.Leaf("key", "BYTE_ARRAY", "REQUIRED", converted: "UTF8"), Node.Leaf("value", "DOUBLE"))),
            "map<string,double>", true, [("old_map.map.key", "BYTE_ARRAY", "REQUIRED", "UTF8"), ("old_map.map.value", "DOUBLE", "OPTIONAL", null)]),
        (Node.Group("kv", "OPTIONAL", "MAP", null, Node.Group("map", "REPEATED", "MAP_KEY_VALUE", null, Node.Leaf("key", "INT32", "REQUIRED"), Node.Leaf("value", "BOOLEAN"))),
            "map<integer,boolean>", true, [("kv.map.key", "INT32", "REQUIRED", null), ("kv.map.value", "BOOLEAN", "OPTIONAL", null)]),
        (Node.Group("mls", "OPTIONAL", null, "MAP", Node.Group("key_value", "REPEATED", null, null, Node.Leaf("key", "INT32", "REQUIRED"),
                Node.Group("value", "OPTIONAL", null, "LIST", Node.Group("list", "REPEATED", null, null, Node.Group("element", Node.Leaf("p", "FLOAT"), Node.Leaf("q", "BYTE_ARRAY", logical: "STRING")))))),
            "map<integer,array<struct<p:float,q:string>>>", true,
            [("mls.key_value.key", "INT32", "REQUIRED", null), ("mls.key_value.value.list.element.p", "FLOAT", "OPTIONAL", null), ("mls.key_value.value.list.element.q", "BYTE_ARRAY", "OPTIONAL", "STRING")]),
        (Node.Group("deep", Node.Group("l2", Node.Group("l3", Node.Leaf("x", "INT32"), Node.Group("xs", "OPTIONAL", null, "LIST", Node.Group("list", "REPEATED", null, null, Node.Leaf("element", "INT64")))))),
            "struct<l2:struct<l3:struct<x:integer,xs:array<long>>>>", true, [("deep.l2.l3.x", "INT32", "OPTIONAL", null), ("deep.l2.l3.xs.list.element", "INT64", "OPTIONAL", null)]),
        (Node.Leaf("numbers", "INT32", "REPEATED"), "array<integer>", false, [("numbers", "INT32", "REPEATED", null)]),
        (Node.Group("records", "REPEATED", null, null, Node.Leaf("a", "INT32")), "array<struct<a:integer>>", false, [("records.a", "INT32", "OPTIONAL", null)]),
        (Node.Group("matrix", "OPTIONAL", null, "LIST", Node.Group("list", "REPEATED", null, null, Node.Group("element", "OPTIONAL", null, "LIST", Node.Group("list", "REPEATED", null, null, Node.Leaf("element", "DOUBLE"))))),
            "array<array<double>>", true, [("matrix.list.element.list.element", "DOUBLE", "OPTIONAL", null)]),
        (Node.Group("req", "REQUIRED", null, null, Node.Leaf("a", "INT32", "REQUIRED")), "struct<a:integer>", false, [("req.a", "INT32", "REQUIRED", null)]),
    ];

    private static async Task<(TempLake Lake, PqaxDocument Doc, TableRow Table)> LayoutsAsync()
    {
        var lake = new TempLake();
        lake.Write("data/layouts/part-0001.parquet", Footers.FileOf(Footers.SchemaFooter(Layouts.Select(layout => layout.Field).ToArray(), rows: 7)));
        PqaxDocument doc = await FolderFootprint.CollectAsync(lake, "data/layouts");
        return (lake, doc, Assert.Single(doc.Summary.Tables));
    }

    [Fact]
    public async Task The_paths_of_the_leaves_keep_the_names_of_the_levels_of_lists_and_maps_of_every_layout()
    {
        (TempLake lake, PqaxDocument doc, TableRow table) = await LayoutsAsync();
        using (lake)
        using (doc)
        {
            Assert.Empty(FolderFootprint.IssuesOf(doc, table).Select(issue => issue.Code + ": " + issue.Message));
            List<ColumnRow> columns = FolderFootprint.ColumnsOf(doc, table);
            List<PhysicalColumnRow> physical = FolderFootprint.PhysicalColumnsOf(doc, table).OrderBy(column => column.Ordinal).ToList();
            Assert.Equal(
                Layouts.SelectMany(layout => layout.Leaves.Select(leaf => (leaf.Path, leaf.Type, leaf.Repetition, leaf.Annotation, layout.Field.Name))),
                physical.Select(column => (PathOf(column), column.PhysicalType!, column.Repetition, column.LogicalType, columns.Single(c => c.Id == column.Column).Name!)));
            Assert.All(physical, column => Assert.Null(column.TypeLength));
            Assert.All(physical, column => Assert.Null(column.FieldId));
        }
    }

    [Fact]
    public async Task The_type_of_a_column_of_every_layout_of_lists_and_maps_is_the_one_that_spark_reads()
    {
        (TempLake lake, PqaxDocument doc, TableRow table) = await LayoutsAsync();
        using (lake)
        using (doc)
        {
            Assert.Equal(
                Layouts.Select(layout => (layout.Field.Name, layout.DataType, layout.IsNullable)),
                FolderFootprint.ColumnsOf(doc, table).Select(column => (column.Name!, column.DataType!, column.IsNullable!.Value)));
        }
    }

    [Fact]
    public async Task The_totals_of_a_column_of_every_layout_are_the_sums_over_its_leaves()
    {
        (TempLake lake, PqaxDocument doc, TableRow table) = await LayoutsAsync();
        using (lake)
        using (doc)
        {
            // The synthetic footers give the leaf of ordinal i the compressed size 50 + i, the uncompressed size 100 + i,
            // the rows of the file as its values, ZSTD, PLAIN, and no statistics, dictionary, or size statistics.
            int ordinal = 0;
            foreach ((Node field, _, _, var leaves) in Layouts)
            {
                ColumnRow column = FolderFootprint.ColumnsOf(doc, table).Single(c => c.Name == field.Name);
                long[] ordinals = Enumerable.Range(ordinal, leaves.Length).Select(i => (long)i).ToArray();
                ordinal += leaves.Length;
                string expected = Totals(
                    ordinals.Sum(i => 50 + i), ordinals.Sum(i => 100 + i), leaves.Length, 7L * leaves.Length, null, 0, 0, 0, null,
                    ["ZSTD"], ["PLAIN"], null, 0);
                Assert.True(expected == ActualTotals(column), $"{field.Name}:\n  expected {expected}\n  actual   {ActualTotals(column)}");
            }
        }
    }
}
