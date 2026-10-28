using System.Text.Json.Nodes;

namespace Pqax.Tests;

/// <summary>
/// The measures of section 10 of <c>docs/object-model.md</c> that <see cref="StorageMeasures"/> gains so as to offer
/// all of them, as the storage views do: the share of each component of the stack (10.2), the file size figures and the
/// V-Order shares (10.3), the VACUUM and the OPTIMIZE (10.4), the partitions (10.5), the measures of a column (10.6), and
/// those of the collection (10.7). The footprints are those of <c>tests/typescript-views/section10.test.ts</c>. The
/// expected values come from the text of section 10, not from the code, with the arithmetic in the comments. A measure
/// that needs an absent field is null, never 0: the principle "Unknown is not zero" of section 2 and rule 2 of 10.1.
/// </summary>
public class Section10AllMeasuresTests
{
    private static readonly double? Null = null;

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private static CollectionInfo Collected => new() { CollectorApp = "pqax-tests", StartedAt = Utc(2026, 10, 1), CompletedAt = Utc(2026, 10, 11, 12), Level = 2 };

    private static StorageStack Stack(double? fileSize = null, double? deleted = null, double? footerSize = null, double? staleColumnSize = null,
        double? nonLiveFileSize = null, double? unreferencedFileSize = null, double? metadataSize = null) =>
        StorageMeasures.StackOf(new StackInput
        {
            FileSize = fileSize,
            Deleted = deleted,
            FooterSize = footerSize,
            StaleColumnSize = staleColumnSize,
            NonLiveFileSize = nonLiveFileSize,
            UnreferencedFileSize = unreferencedFileSize,
            MetadataSize = metadataSize,
        });

    /// <summary>
    /// A footprint written by <see cref="FootprintBuilder"/> with the rows that <paramref name="add"/> adds, opened by
    /// the reader. <paramref name="summary"/> changes summary.json after the write, for what the builder refuses, such
    /// as a required field without a value.
    /// </summary>
    private static PqaxDocument Build(Action<FootprintBuilder> add, CollectionInfo? collection = null, Action<JsonNode>? summary = null)
    {
        var builder = new FootprintBuilder(collection ?? Collected);
        add(builder);
        using var stream = new MemoryStream();
        builder.Write(stream);
        byte[] data = stream.ToArray();
        if (summary != null)
        {
            data = Kit.Rewrite(data, (name, content) =>
            {
                if (name != "summary.json")
                {
                    return content;
                }

                JsonNode json = Kit.Json(content);
                summary(json);
                return Kit.Bytes(json);
            });
        }

        return PqaxDocument.Open(new MemoryStream(data));
    }

    /// <summary>
    /// A footprint of summary.json only, written by hand, for what the builder refuses: a repeated id, an id of 0. The
    /// summary maps each table to its rows.
    /// </summary>
    private static PqaxDocument Written(params (string Table, JsonObject[] Rows)[] summary)
    {
        var tables = new JsonObject();
        var counts = new JsonObject();
        foreach ((string name, JsonObject[] rows) in summary)
        {
            tables[name] = Columnar(rows);
            counts[name] = rows.Length;
        }

        var manifest = new JsonObject
        {
            ["format"] = "pqax",
            ["formatVersion"] = "0.2",
            ["collection"] = new JsonObject
            {
                ["collectorApp"] = "pqax-tests",
                ["collectorAppVersion"] = "0.0",
                ["startedAt"] = "2026-10-01T00:00:00.000Z",
                ["completedAt"] = "2026-10-11T12:00:00.000Z",
                ["level"] = 2,
            },
            ["parts"] = new JsonArray(new JsonObject { ["name"] = "summary.json", ["tables"] = counts }),
        };
        byte[] zip = Kit.Zip([("manifest.json", Kit.Bytes(manifest)), ("summary.json", Kit.Bytes(tables))]);
        return PqaxDocument.Open(new MemoryStream(zip));
    }

    private static JsonObject Columnar(JsonObject[] rows)
    {
        var table = new JsonObject();
        foreach (string key in rows.SelectMany(row => row.Select(pair => pair.Key)).Distinct())
        {
            table[key] = new JsonArray(rows.Select(row => row.TryGetPropertyValue(key, out JsonNode? value) ? value?.DeepClone() : null).ToArray());
        }

        return table;
    }

    // ---- 10.2 the share of each component ----

    [Fact]
    public void Rule_10_2_each_of_the_seven_shares_is_the_component_over_Data()
    {
        // FileSize 1000, Deleted 0, FooterSize 40, StaleColumnSize 10, NonLiveFileSize 300, UnreferencedFileSize 200,
        // MetadataSize 50. Live = max(0, 1000 - 0 - 40 - 10) = 950. Listed: NonLive = min(300, 200) = 200, Unreferenced =
        // 200 - 200 = 0. Waste = 200 + 0 + 0 + 10 = 210. Data = 950 + 210 = 1160.
        StorageStack stack = Stack(1000, 0, 40, 10, 300, 200, 50);
        Assert.Equal((double?)1160, stack.Data);
        Assert.Equal(950.0 / 1160, stack.Share("live"));
        Assert.Equal(200.0 / 1160, stack.Share("nonLive"));
        Assert.Equal(0.0, stack.Share("unreferenced"));
        Assert.Equal(0.0, stack.Share("deleted"));
        Assert.Equal(10.0 / 1160, stack.Share("stale"));
        Assert.Equal(50.0 / 1160, stack.Share("metadata"));
        Assert.Equal(40.0 / 1160, stack.Share("footers"));
    }

    [Fact]
    public void Rule_10_2_the_share_of_an_absent_component_is_null()
    {
        // Live 1000, NonLive 100 without a listing, every other component absent. Data 1100.
        StorageStack stack = Stack(1000, nonLiveFileSize: 100);
        Assert.Equal(1000.0 / 1100, stack.Share("live"));
        Assert.Equal(100.0 / 1100, stack.Share("nonLive"));
        Assert.All(new[] { "unreferenced", "deleted", "stale", "metadata", "footers" }, key => Assert.Null(stack.Share(key)));
    }

    [Fact]
    public void Rule_10_2_the_shares_of_Metadata_and_Footers_can_exceed_1()
    {
        // FileSize 100, MetadataSize 300: Live 100, Waste null, Data 100. 300 / 100 = 3, not clipped.
        StorageStack stack = Stack(100, metadataSize: 300);
        Assert.Equal(3.0, stack.Share("metadata"));
        Assert.Equal(1.0, stack.Share("live"));
    }

    [Fact]
    public void Rule_10_2_every_share_is_null_when_Data_is_null_or_0()
    {
        // FileSize absent: Live and Data null. Data 0: a zero denominator. Nothing at all.
        foreach (StorageStack stack in new[] { Stack(null, 10, 5, 2, 7, null, 3), Stack(0, 0, 0, 0, 0, 0, 30), Stack() })
        {
            Assert.All(StorageMeasures.StackComponents, key => Assert.Null(stack.Share(key)));
        }
    }

    [Fact]
    public void Rule_10_2_ForTables_gives_the_shares_of_each_table()
    {
        using PqaxDocument doc = Build(b => b.AddTable(new TableRow
        {
            Id = 1, Name = "t", Format = "Delta", DetailDepth = "Summary", FileSize = 1000, RowCount = 100, DeletedRowCount = 0, FooterSize = 40,
            StaleColumnSize = 10, NonLiveFileSize = 300, UnreferencedFileSize = 200, MetadataSize = 50,
        }));
        StorageStack stack = Assert.Single(StorageMeasures.ForTables(doc.Summary, null, doc.Manifest.Collection)).Stack;
        Assert.Equal(950.0 / 1160, stack.Share("live"));
        Assert.Equal(40.0 / 1160, stack.Share("footers"));
    }

    // ---- 10.3 the files of a table ----

    /// <summary>
    /// Table 1 keeps five files with a size, one without, and one with a negative size, which is absent; its FileSizes
    /// and VOrderedFileCount differ from the files, so that the form from the files shows. Table 2 keeps four files, none
    /// with IsVOrdered. Tables 3 to 6 keep no file.
    /// </summary>
    private static PqaxDocument FileTables() => Build(b =>
    {
        b.AddTable(new TableRow
        {
            Id = 1, Name = "five_files", Format = "Delta", DetailDepth = "Files", FileCount = 5, FileSize = 150, VOrderedFileCount = 5,
            FileSizes = new Distribution { Count = 99, Sum = 9999, Minimum = 1, FirstQuartile = 2, Median = 3, ThirdQuartile = 4, Maximum = 5 },
        });
        b.AddTable(new TableRow { Id = 2, Name = "four_files", Format = "Delta", DetailDepth = "Files", FileCount = 4, FileSize = 1000, VOrderedFileCount = 3 });
        b.AddTable(new TableRow
        {
            Id = 3, Name = "totals", Format = "Delta", DetailDepth = "Summary", FileCount = 12, FileSize = 90, VOrderedFileCount = 15,
            FileSizes = new Distribution { Minimum = 5, FirstQuartile = 6, Median = 7, ThirdQuartile = 8, Maximum = 9 },
        });
        b.AddTable(new TableRow { Id = 4, Name = "own_count", Format = "Delta", DetailDepth = "Summary", FileCount = 99, FileSize = 999, FileSizes = new Distribution { Count = 3, Sum = 60, Median = 20 } });
        b.AddTable(new TableRow { Id = 5, Name = "zero", Format = "Delta", DetailDepth = "Summary", FileCount = 0, FileSize = 0, VOrderedFileCount = 0 });
        b.AddTable(new TableRow
        {
            Id = 6, Name = "negative", Format = "Delta", DetailDepth = "Summary", FileCount = -3, FileSize = -10, VOrderedFileCount = 1,
            FileSizes = new Distribution { Count = -1, Sum = -2, Median = -5 },
        });
    });

    private static readonly (long Table, long? Size, bool? VOrdered)[] FileRows =
    [
        (1, 50, true), (1, 10, false), (1, 40, true), (1, 20, null), (1, 30, false), (1, null, null), (1, -7, null),
        (2, 300, null), (2, 100, null), (2, 400, null), (2, 200, null),
    ];

    private static FileRow[] Files() =>
        FileRows.Select((f, i) => new FileRow { Id = i + 1, Table = f.Table, FileSize = f.Size, IsVOrdered = f.VOrdered }).ToArray();

    private static double?[] Figures(FileMeasures f) => [f.Count, f.Sum, f.Minimum, f.FirstQuartile, f.Median, f.ThirdQuartile, f.Maximum];

    [Fact]
    public void Rule_10_3_from_the_files_the_quartiles_are_taken_by_position()
    {
        using PqaxDocument doc = FileTables();
        IReadOnlyList<TableStorage> tables = StorageMeasures.ForTables(doc.Summary, Files(), doc.Manifest.Collection);
        // Table 1: the sizes 10, 20, 30, 40, 50 (the file without a size and the one of -7 are left out). n = 5: positions
        // floor(5/4) = 1, floor(5/2) = 2, floor(15/4) = 3. Count 5 and sum 150, not the 99 and 9999 of FileSizes.
        Assert.Equal(new double?[] { 5, 150, 10, 20, 30, 40, 50 }, Figures(tables[0].Files));
        // Table 2: 100, 200, 300, 400. n = 4: positions 1, 2, 3, so 200, 300, 400, not the interpolated 175, 250, 325.
        Assert.Equal(new double?[] { 4, 1000, 100, 200, 300, 400, 400 }, Figures(tables[1].Files));
    }

    [Fact]
    public void Rule_10_3_from_FileSizes_with_FileCount_and_FileSize_for_a_missing_Count_and_Sum()
    {
        using PqaxDocument doc = FileTables();
        IReadOnlyList<TableStorage> tables = StorageMeasures.ForTables(doc.Summary, Files(), doc.Manifest.Collection);
        // Table 3: FileSizes has no Count and no Sum: FileCount 12 and FileSize 90.
        Assert.Equal(new double?[] { 12, 90, 5, 6, 7, 8, 9 }, Figures(tables[2].Files));
        // Table 4: Count 3 and Sum 60 of FileSizes, not FileCount 99 and FileSize 999.
        Assert.Equal(new double?[] { 3, 60, null, null, 20, null, null }, Figures(tables[3].Files));
        // Table 5: no FileSizes; FileCount 0 and FileSize 0 are values, the shape is null.
        Assert.Equal(new double?[] { 0, 0, null, null, null, null, null }, Figures(tables[4].Files));
        // Table 6: every value negative, so absent.
        Assert.Equal(new double?[] { null, null, null, null, null, null, null }, Figures(tables[5].Files));
    }

    [Fact]
    public void Rule_10_3_VOrderShare_from_the_files_else_from_the_totals_and_VOrderBytesShare_from_the_files_only()
    {
        using PqaxDocument doc = FileTables();
        IReadOnlyList<TableStorage> tables = StorageMeasures.ForTables(doc.Summary, Files(), doc.Manifest.Collection);
        // Table 1: IsVOrdered on 4 files, true on 2: 2 / 4. The bytes: (50 + 40) / (50 + 10 + 40 + 30). Not 5 / 5.
        Assert.Equal(0.5, tables[0].Files.VOrderShare);
        Assert.Equal(90.0 / 130, tables[0].Files.VOrderBytesShare);
        // Table 2: no file has IsVOrdered: 3 / 4 from the totals; VOrderBytesShare null.
        Assert.Equal(0.75, tables[1].Files.VOrderShare);
        Assert.Null(tables[1].Files.VOrderBytesShare);
        // Table 3: min(1, 15 / 12) = 1.
        Assert.Equal(1.0, tables[2].Files.VOrderShare);
        Assert.Null(tables[2].Files.VOrderBytesShare);
        // Table 4: no VOrderedFileCount. Table 5: FileCount 0. Table 6: FileCount negative, so absent.
        Assert.All(tables.Skip(3), table =>
        {
            Assert.Null(table.Files.VOrderShare);
            Assert.Null(table.Files.VOrderBytesShare);
        });
    }

    [Fact]
    public void Rule_10_3_without_the_files_the_figures_and_the_share_come_from_the_totals()
    {
        using PqaxDocument doc = FileTables();
        TableStorage table = StorageMeasures.ForTables(doc.Summary, null, doc.Manifest.Collection)[0];
        // The FileSizes of table 1 now, min(1, 5 / 5) = 1, and no VOrderBytesShare.
        Assert.Equal(new double?[] { 99, 9999, 1, 2, 3, 4, 5 }, Figures(table.Files));
        Assert.Equal(1.0, table.Files.VOrderShare);
        Assert.Null(table.Files.VOrderBytesShare);
    }

    [Fact]
    public void Rule_10_3_a_file_with_IsVOrdered_and_no_FileSize_counts_in_the_share_and_adds_no_bytes()
    {
        using PqaxDocument doc = Build(b => b.AddTable(new TableRow { Id = 1, Name = "t", Format = "Delta", DetailDepth = "Files", FileCount = 3 }));
        FileRow[] files =
        [
            new() { Id = 1, Table = 1, FileSize = 100, IsVOrdered = true },
            new() { Id = 2, Table = 1, FileSize = null, IsVOrdered = true },
            new() { Id = 3, Table = 1, FileSize = 300, IsVOrdered = false },
        ];
        TableStorage table = Assert.Single(StorageMeasures.ForTables(doc.Summary, files, doc.Manifest.Collection));
        // IsVOrdered is present on the three files, true on two: 2 / 3. The bytes: 100 / (100 + 300).
        Assert.Equal(2.0 / 3, table.Files.VOrderShare);
        Assert.Equal(0.25, table.Files.VOrderBytesShare);
        Assert.Equal(2L, table.Files.Count);
        Assert.Equal(400.0, table.Files.Sum);
    }

    // ---- 10.4 VACUUM and OPTIMIZE ----

    [Fact]
    public void Rule_10_4_HasVacuum_and_HasOptimize_with_the_latest_LastAt_of_the_matching_operations()
    {
        using PqaxDocument doc = Build(b =>
        {
            foreach ((long id, string name) in new[] { (1L, "maintained"), (2L, "written"), (3L, "no_operation"), (4L, "unnamed_only"), (5L, "vacuum_untimed") })
            {
                b.AddTable(new TableRow { Id = id, Name = name, Format = "Delta", DetailDepth = "Summary" });
            }

            foreach ((long table, string? name, DateTimeOffset? lastAt) in new (long, string?, DateTimeOffset?)[]
            {
                (1, "WRITE", Utc(2026, 9, 3)),
                (1, "VACUUM START", Utc(2026, 9, 5)),
                (1, "vacuum end", Utc(2026, 9, 7)),
                (1, "VACUUM END", Utc(2026, 9, 6)),
                (1, "Optimize", Utc(2026, 9, 4)),
                (1, "OPTIMIZE", null),
                (2, "WRITE", Utc(2026, 9, 2)),
                (2, "MERGE", Utc(2026, 9, 3)),
                (4, null, Utc(2026, 9, 6)),
                (5, "VACUUM END", null),
            })
            {
                b.AddOperation(new OperationRow { Table = table, Name = name ?? "unnamed", CommitCount = 1, LastAt = lastAt });
            }
        },
        // The builder refuses an operation without a name, which is required, so the ninth loses it after the write.
        summary: json => json["operations"]!["name"]![8] = null);
        IReadOnlyList<TableStorage> tables = StorageMeasures.ForTables(doc.Summary, null, doc.Manifest.Collection);

        // The latest of 09-05, 09-07 ("vacuum end", lower case), and 09-06, whatever their order. OPTIMIZE without a
        // LastAt counts for HasOptimize and leaves LastOptimizeAt at the 09-04 of "Optimize".
        MaintenanceMeasures maintained = tables[0].Maintenance;
        Assert.True(maintained.HasVacuum);
        Assert.Equal(Utc(2026, 9, 7), maintained.LastVacuumAt);
        Assert.True(maintained.HasOptimize);
        Assert.Equal(Utc(2026, 9, 4), maintained.LastOptimizeAt);

        // Operations, none of them a VACUUM or an OPTIMIZE: false.
        MaintenanceMeasures written = tables[1].Maintenance;
        Assert.False(written.HasVacuum);
        Assert.Null(written.LastVacuumAt);
        Assert.False(written.HasOptimize);
        Assert.Null(written.LastOptimizeAt);

        // No Operation for the table, and an operation without a Name: null.
        Assert.All(new[] { tables[2], tables[3] }, table =>
        {
            Assert.Null(table.Maintenance.HasVacuum);
            Assert.Null(table.Maintenance.LastVacuumAt);
            Assert.Null(table.Maintenance.HasOptimize);
            Assert.Null(table.Maintenance.LastOptimizeAt);
        });

        // A VACUUM without a LastAt: true, and no time.
        MaintenanceMeasures untimed = tables[4].Maintenance;
        Assert.True(untimed.HasVacuum);
        Assert.Null(untimed.LastVacuumAt);
        Assert.False(untimed.HasOptimize);
        Assert.Null(untimed.LastOptimizeAt);
    }

    // ---- 10.5 the partitions ----

    private static PqaxDocument PartitionTables() => Build(b =>
    {
        b.AddTable(new TableRow { Id = 1, Name = "counted_and_kept", Format = "Delta", DetailDepth = "Summary", PartitionCount = 10 });
        b.AddTable(new TableRow { Id = 2, Name = "kept_only", Format = "Delta", DetailDepth = "Summary" });
        b.AddTable(new TableRow { Id = 3, Name = "counted_only", Format = "Delta", DetailDepth = "Summary", PartitionCount = 4 });
        b.AddTable(new TableRow { Id = 4, Name = "neither", Format = "Delta", DetailDepth = "Summary" });
        b.AddTable(new TableRow { Id = 5, Name = "negative_count", Format = "Delta", DetailDepth = "Summary", PartitionCount = -1 });
        b.AddTable(new TableRow { Id = 6, Name = "zero_count", Format = "Delta", DetailDepth = "Summary", PartitionCount = 0 });
    });

    /// <summary>The partitions kept. The row of table 99 belongs to no table of the file.</summary>
    private static PartitionRow[] Partitions() =>
        new (long Table, DateTimeOffset? Modified)[]
        {
            (1, Utc(2026, 9, 5)), (1, Utc(2026, 9, 1)), (1, Utc(2026, 9, 3)), (2, Utc(2026, 9, 2)), (2, null), (5, null), (99, Utc(2026, 1, 1)),
        }.Select((p, i) => new PartitionRow { Id = i + 1, Table = p.Table, LastModifiedAt = p.Modified }).ToArray();

    private static void AssertPartitions(PartitionMeasures actual, long? count, long kept, DateTimeOffset? oldest, DateTimeOffset? newest)
    {
        Assert.Equal(count, actual.Count);
        Assert.Equal(kept, actual.Kept);
        Assert.Equal(oldest, actual.OldestModifiedAt);
        Assert.Equal(newest, actual.NewestModifiedAt);
    }

    [Fact]
    public void Rule_10_5_Partitions_is_PartitionCount_else_the_partitions_kept_and_null_when_neither()
    {
        using PqaxDocument doc = PartitionTables();
        IReadOnlyList<TableStorage> tables = StorageMeasures.ForTables(doc.Summary, null, doc.Manifest.Collection, null, Partitions());
        // PartitionCount 10, three rows kept, from 09-01 to 09-05.
        AssertPartitions(tables[0].Partitions, 10, 3, Utc(2026, 9, 1), Utc(2026, 9, 5));
        // No PartitionCount: the two rows kept. One has no LastModifiedAt.
        AssertPartitions(tables[1].Partitions, 2, 2, Utc(2026, 9, 2), Utc(2026, 9, 2));
        AssertPartitions(tables[2].Partitions, 4, 0, null, null);
        // Neither: null, and kept 0.
        AssertPartitions(tables[3].Partitions, null, 0, null, null);
        // A negative PartitionCount is absent: the one row kept.
        AssertPartitions(tables[4].Partitions, 1, 1, null, null);
        // A PartitionCount of 0 is a value.
        AssertPartitions(tables[5].Partitions, 0, 0, null, null);
    }

    [Fact]
    public void Rule_10_5_without_the_partitions_table_kept_is_0()
    {
        using PqaxDocument doc = PartitionTables();
        IReadOnlyList<TableStorage> tables = StorageMeasures.ForTables(doc.Summary, null, doc.Manifest.Collection);
        Assert.Equal(new long?[] { 10, null, 4, null, null, 0 }, tables.Select(table => table.Partitions.Count));
        Assert.All(tables, table => AssertPartitions(table.Partitions, table.Partitions.Count, 0, null, null));
    }

    [Fact]
    public void Compute_reads_the_partitions_and_the_files_tables()
    {
        using PqaxDocument doc = Build(b =>
        {
            b.AddTable(new TableRow { Id = 1, Name = "partitioned", Format = "Delta", DetailDepth = "Files", FileCount = 2, FileSize = 300, VOrderedFileCount = 2 });
            b.AddTable(new TableRow { Id = 2, Name = "plain", Format = "Delta", DetailDepth = "Summary" });
            b.AddPartition(new PartitionRow { Table = 1, LastModifiedAt = Utc(2026, 9, 1) });
            b.AddPartition(new PartitionRow { Table = 1, LastModifiedAt = Utc(2026, 9, 8) });
            b.AddFile(new FileRow { Table = 1, FileSize = 100, IsVOrdered = true });
            b.AddFile(new FileRow { Table = 1, FileSize = 200, IsVOrdered = false });
        });
        Assert.Equal(2, doc.RowCount("partitions"));
        FootprintMeasures measures = StorageMeasures.Compute(doc);
        // No PartitionCount: the two partitions kept, from 09-01 to 09-08.
        AssertPartitions(measures.Tables[0].Partitions, 2, 2, Utc(2026, 9, 1), Utc(2026, 9, 8));
        AssertPartitions(measures.Tables[1].Partitions, null, 0, null, null);
        // The files: 100 and 200, n = 2: positions floor(2/4) = 0, floor(2/2) = 1, floor(6/4) = 1. One V-Ordered: 1 / 2 of
        // the files, 100 / 300 of the bytes, not 2 / 2 from the totals.
        Assert.Equal(new double?[] { 2, 300, 100, 100, 200, 200, 200 }, Figures(measures.Tables[0].Files));
        Assert.Equal(0.5, measures.Tables[0].Files.VOrderShare);
        Assert.Equal(100.0 / 300, measures.Tables[0].Files.VOrderBytesShare);
    }

    // ---- 10.6 the measures of a column ----

    [Fact]
    public void Rule_10_6_the_measures_of_each_column()
    {
        // Table 1 read 4 footers, table 2 has no FooterCount, table 3 read none. Column 9 repeats the id 1.
        using PqaxDocument doc = Written(
            ("tables",
            [
                new JsonObject { ["id"] = 1, ["name"] = "a", ["footerCount"] = 4 },
                new JsonObject { ["id"] = 2, ["name"] = "b" },
                new JsonObject { ["id"] = 3, ["name"] = "c", ["footerCount"] = 0 },
            ]),
            ("columns",
            [
                new JsonObject
                {
                    ["id"] = 1, ["table"] = 1, ["name"] = "full", ["valueCount"] = 100, ["nullCount"] = 25, ["compressedSize"] = 400, ["uncompressedSize"] = 1600,
                    ["chunkCount"] = 8, ["dictionaryChunkCount"] = 6, ["fallbackChunkCount"] = 3, ["unencodedByteArrayDataBytes"] = 600, ["fileCountWithoutColumn"] = 1,
                },
                new JsonObject { ["id"] = 2, ["table"] = 1, ["name"] = "more_nulls", ["valueCount"] = 10, ["nullCount"] = 15, ["fileCountWithoutColumn"] = 0 },
                new JsonObject
                {
                    ["id"] = 3, ["table"] = 1, ["name"] = "all_null", ["valueCount"] = 20, ["nullCount"] = 20, ["unencodedByteArrayDataBytes"] = 0, ["chunkCount"] = 0,
                    ["dictionaryChunkCount"] = 0, ["fallbackChunkCount"] = 0, ["compressedSize"] = 0, ["uncompressedSize"] = 10,
                },
                new JsonObject { ["id"] = 4, ["table"] = 2, ["name"] = "no_footer_count", ["valueCount"] = 0, ["nullCount"] = 0, ["fileCountWithoutColumn"] = 1 },
                new JsonObject { ["id"] = 5, ["table"] = 3, ["name"] = "zero_footers", ["fileCountWithoutColumn"] = 0 },
                new JsonObject { ["id"] = 6, ["table"] = 99, ["name"] = "orphan", ["valueCount"] = 10, ["nullCount"] = 1, ["fileCountWithoutColumn"] = 1 },
                new JsonObject
                {
                    ["id"] = 7, ["table"] = 1, ["name"] = "negative", ["valueCount"] = -10, ["nullCount"] = 2, ["compressedSize"] = -1, ["uncompressedSize"] = 5,
                    ["chunkCount"] = 4, ["dictionaryChunkCount"] = -2, ["fileCountWithoutColumn"] = -1,
                },
                new JsonObject { ["id"] = 8, ["table"] = 1 },
                new JsonObject { ["id"] = 1, ["table"] = 1 },
            ]));
        IReadOnlyList<ColumnStorage> columns = StorageMeasures.ForColumns(doc.Summary);
        Assert.Equal(9, columns.Count);
        Assert.Equal(Enumerable.Range(0, 9), columns.Select(column => column.Row));

        static double?[] Measures(ColumnStorage c) =>
            [c.NullShare, c.CompressionRatio, c.DictionaryShare, c.DictionaryFallbackShare, c.AverageValueLength, c.FilesWithoutColumnShare];

        void AssertColumn(int row, long? id, long? table, string? name, string label, double?[] measures)
        {
            ColumnStorage column = columns[row];
            Assert.Equal(id, column.Id);
            Assert.Equal(table, column.Table);
            Assert.Equal(name, column.Name);
            Assert.Equal(label, column.Label);
            Assert.Equal(measures, Measures(column));
        }

        // 25 / 100, 1600 / 400, 6 / 8, 3 / 6, 600 / (100 - 25), 1 / 4.
        AssertColumn(0, 1, 1, "full", "full", [0.25, 4, 0.75, 0.5, 8, 0.25]);
        // min(1, 15 / 10) = 1; 0 / 4 = 0.
        AssertColumn(1, 2, 1, "more_nulls", "more_nulls", [1, null, null, null, null, 0]);
        // 20 / 20 = 1. CompressedSize 0, ChunkCount 0, DictionaryChunkCount 0, ValueCount - NullCount 0: null.
        AssertColumn(2, 3, 1, "all_null", "all_null", [1, null, null, null, null, null]);
        // ValueCount 0: null. Its table has no FooterCount: null.
        AssertColumn(3, 4, 2, "no_footer_count", "no_footer_count", [null, null, null, null, null, null]);
        // FooterCount 0: null.
        AssertColumn(4, 5, 3, "zero_footers", "zero_footers", [null, null, null, null, null, null]);
        // A column of no table of the file: its table is null, 1 / 10, and no FooterCount to divide by.
        AssertColumn(5, 6, null, "orphan", "orphan", [0.1, null, null, null, null, null]);
        // Every negative value is absent.
        AssertColumn(6, 7, 1, "negative", "negative", [null, null, null, null, null, null]);
        // No field: a partition column that the files do not store. The label names the id, then the row.
        AssertColumn(7, 8, 1, null, "Column 8", [null, null, null, null, null, null]);
        AssertColumn(8, null, 1, null, "Column at row 9", [null, null, null, null, null, null]);
    }

    [Fact]
    public void Rule_10_6_a_summary_without_columns_has_none()
    {
        using PqaxDocument doc = Build(b => b.AddTable(new TableRow { Id = 1, Name = "t", Format = "Delta", DetailDepth = "Summary" }));
        Assert.Empty(StorageMeasures.ForColumns(doc.Summary));
        Assert.Empty(StorageMeasures.Compute(doc).Columns);
    }

    // ---- 10.7 the measures of the collection ----

    [Fact]
    public void Rule_10_7_the_totals_and_the_depths_of_the_collection()
    {
        // Sources 1, 2, 2 again, and 0, which is no id. t1: Live 1000 - 0 - 50 - 0 = 950, Waste 100, Overhead 30 + 50 = 80,
        // Total 1130. t2: Live 500, Total 500, Waste and Overhead null. t3: nothing, no DetailDepth. t4: FileCount and
        // FileSize negative so absent, Live null, Overhead 20, Total 20, Waste null.
        using PqaxDocument doc = Written(
            ("sources",
            [
                new JsonObject { ["id"] = 1, ["kind"] = "Lakehouse" }, new JsonObject { ["id"] = 2, ["kind"] = "Lakehouse" },
                new JsonObject { ["id"] = 2, ["kind"] = "Lakehouse" }, new JsonObject { ["id"] = 0, ["kind"] = "Lakehouse" },
            ]),
            ("tables",
            [
                new JsonObject
                {
                    ["id"] = 1, ["source"] = 1, ["name"] = "t1", ["detailDepth"] = "Files", ["fileCount"] = 3, ["fileSize"] = 1000, ["rowCount"] = 10,
                    ["deletedRowCount"] = 0, ["footerSize"] = 50, ["staleColumnSize"] = 0, ["nonLiveFileSize"] = 100, ["metadataSize"] = 30,
                },
                new JsonObject { ["id"] = 2, ["source"] = 2, ["name"] = "t2", ["detailDepth"] = "Summary", ["fileCount"] = 2, ["fileSize"] = 500 },
                new JsonObject { ["id"] = 3, ["source"] = 1, ["name"] = "t3" },
                new JsonObject
                {
                    ["id"] = 4, ["source"] = 2, ["name"] = "t4", ["detailDepth"] = "Summary", ["fileCount"] = -1, ["fileSize"] = -5, ["rowCount"] = 7,
                    ["metadataSize"] = 20,
                },
            ]),
            ("columns",
            [
                new JsonObject { ["id"] = 1, ["table"] = 1, ["name"] = "x" }, new JsonObject { ["id"] = 2, ["table"] = 2, ["name"] = "y" },
                new JsonObject { ["id"] = 3, ["table"] = 4, ["name"] = "z" },
            ]));
        IReadOnlyList<TableStorage> tables = StorageMeasures.ForTables(doc.Summary, null, doc.Manifest.Collection);
        CollectionMeasures collection = StorageMeasures.ForCollection(doc.Summary, tables);

        Assert.Equal(2L, collection.Sources);
        Assert.Equal(4L, collection.Tables);
        Assert.Equal(3L, collection.Columns);
        // 3 + 2; 1000 + 500; 10 + 7.
        Assert.Equal(5L, collection.Files);
        Assert.Equal(1500L, collection.FileSize);
        Assert.Equal(17L, collection.RowCount);
        // 1130 + 500 + 20; 100; 80 + 20.
        Assert.Equal((double?)1650, collection.Footprint);
        Assert.Equal((double?)100, collection.Waste);
        Assert.Equal((double?)100, collection.Overhead);
        Assert.Equal(new Dictionary<string, long> { ["Files"] = 1, ["Summary"] = 2, ["Unknown"] = 1 }, collection.Depths.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary());

        // Compute gives the same.
        CollectionMeasures computed = StorageMeasures.Compute(doc).Collection;
        Assert.Equal((collection.Sources, collection.Files, collection.Footprint), (computed.Sources, computed.Files, computed.Footprint));
    }

    [Fact]
    public void Rule_10_7_every_sum_is_null_when_no_table_has_the_value()
    {
        // A table without a detailDepth, which is required and which the builder refuses.
        using PqaxDocument doc = Written(("tables", [new JsonObject { ["id"] = 1, ["name"] = "bare", ["format"] = "Delta" }]));
        CollectionMeasures collection = StorageMeasures.ForCollection(doc.Summary, StorageMeasures.ForTables(doc.Summary, null, doc.Manifest.Collection));
        Assert.Equal(0L, collection.Sources);
        Assert.Equal(1L, collection.Tables);
        Assert.Equal(0L, collection.Columns);
        Assert.Null(collection.Files);
        Assert.Null(collection.FileSize);
        Assert.Null(collection.RowCount);
        Assert.Equal(Null, collection.Footprint);
        Assert.Equal(Null, collection.Waste);
        Assert.Equal(Null, collection.Overhead);
        Assert.Equal(new Dictionary<string, long> { ["Unknown"] = 1 }, collection.Depths.ToDictionary());

        using PqaxDocument empty = Build(_ => { });
        CollectionMeasures none = StorageMeasures.ForCollection(empty.Summary, []);
        Assert.Equal(0L, none.Tables);
        Assert.Null(none.Files);
        Assert.Equal(Null, none.Footprint);
        Assert.Empty(none.Depths);
    }

    [Fact]
    public void Compute_gives_the_columns_and_the_collection()
    {
        using PqaxDocument doc = Build(b =>
        {
            long source = b.AddSource(new SourceRow { Kind = "Lakehouse" });
            long table = b.AddTable(new TableRow { Source = source, Name = "t", Format = "Delta", DetailDepth = "Summary", FileCount = 2, FileSize = 500, FooterCount = 4 });
            b.AddColumn(new ColumnRow { Table = table, Name = "c", DataType = "string", ValueCount = 10, NullCount = 5, FileCountWithoutColumn = 2 });
        });
        FootprintMeasures measures = StorageMeasures.Compute(doc);
        ColumnStorage column = Assert.Single(measures.Columns);
        // 5 / 10 and 2 / 4.
        Assert.Equal("c", column.Label);
        Assert.Equal(measures.Tables[0].Id, column.Table);
        Assert.Equal(0.5, column.NullShare);
        Assert.Equal(0.5, column.FilesWithoutColumnShare);
        Assert.Equal(1L, measures.Collection.Sources);
        Assert.Equal(1L, measures.Collection.Columns);
        Assert.Equal(2L, measures.Collection.Files);
        Assert.Equal((double?)500, measures.Collection.Footprint);
        Assert.Null(measures.Collection.RowCount);
        Assert.Equal(Null, measures.Collection.Waste);
        Assert.Equal(new Dictionary<string, long> { ["Summary"] = 1 }, measures.Collection.Depths.ToDictionary());
    }
}
