using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pqax.Tests;

/// <summary>
/// <see cref="StorageDiff"/>: the comparison of two footprints, section 13 of <c>docs/object-model.md</c> (Proposed on
/// 24 October 2026). The kit: <c>conformance/samples/diff-before.pqax</c> and <c>diff-after.pqax</c>, whose changes
/// SCENARIO in <c>conformance/make_diff.py</c> declares, against <c>conformance/expected/diff/diff-before.diff-after.json</c>,
/// value for value with no tolerance; each case of SCENARIO on its own. Then the rules, each on small footprints written
/// here with <see cref="FootprintBuilder"/>: the pairing of the tables and the columns in three passes, the recreated
/// tables, the files, partitions, and commits, the compared values, what makes a table changed, and the order of Rank.
/// </summary>
public class DiffTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly string[] Orders = ["total", "waste", "rows", "files", "name"];

    private const double Day = 86_400_000;

    // ---- the kit ----

    private static JsonObject ExpectedDiff() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Kit.ExpectedFolder, "diff", "diff-before.diff-after.json")))!.AsObject();

    private static FootprintDiff KitDiff()
    {
        using PqaxDocument older = PqaxDocument.Open(Path.Combine(Kit.Samples, "diff-before.pqax"));
        using PqaxDocument newer = PqaxDocument.Open(Path.Combine(Kit.Samples, "diff-after.pqax"));
        return StorageDiff.Compare(older, newer);
    }

    private static string KitUuid(int n) => $"6d1f0c2a-0000-4000-8000-{n:D12}";

    private static KeptTable KeptByName(FootprintDiff diff, string newerName) => Assert.Single(diff.Kept, k => k.Newer.Name == newerName);

    private static ValueChange Value(IReadOnlyList<ValueChange> values, string name) => Assert.Single(values, v => v.Name == name);

    /// <summary>A number of a compared value: a long or a double, whichever the library gives.</summary>
    private static double? N(object? value) => value switch
    {
        null => null,
        long l => l,
        int i => i,
        double d => d,
        _ => throw new Xunit.Sdk.XunitException($"{value} ({value.GetType().Name}) is not a number"),
    };

    private static (double? Older, double? Newer, double? Delta) Numbers(ValueChange value) => (N(value.Older), N(value.Newer), value.Delta);

    /// <summary>files or partitions as (added, removed, kept, changed, addedFileSize, removedFileSize).</summary>
    private static (long, long, long, long?, long?, long?)? Counts(DetailChanges? detail) =>
        detail is null ? null : (detail.Added, detail.Removed, detail.Kept, detail.Changed, detail.AddedFileSize, detail.RemovedFileSize);

    private static (string?, string?, string)[] ColumnPairs(KeptTable table) =>
        table.Columns.Kept.Select(c => (c.Older.Name, c.Newer.Name, c.MatchedBy)).ToArray();

    private static string Label(TableChange change) => change.Kind == "kept" ? change.Kept!.Newer.Label : change.Entry!.Table.Label;

    private static (string, string)[] Labels(IEnumerable<TableChange> changes) => changes.Select(c => (c.Kind, Label(c))).ToArray();

    // ---- the result as the JSON of the command, from the API ----

    private static JsonNode? Json(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        DateTimeOffset t => JsonValue.Create(t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Invariant)),
        IReadOnlyList<string> list => new JsonArray(list.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
        _ => throw new Xunit.Sdk.XunitException($"{value} ({value.GetType().Name}) is not a type of a compared value"),
    };

    private static JsonArray Items(IEnumerable<JsonNode?> items) => new(items.ToArray());

    private static JsonObject Changes(IReadOnlyList<ValueChange> values)
    {
        var result = new JsonObject();
        foreach (ValueChange value in values)
        {
            result[value.Name] = new JsonObject { ["older"] = Json(value.Older), ["newer"] = Json(value.Newer), ["delta"] = value.Delta };
        }

        return result;
    }

    private static JsonObject Side(TableSide side) => new()
    {
        ["id"] = side.Id,
        ["label"] = side.Label,
        ["name"] = side.Name,
        ["namespace"] = Json(side.Namespace),
        ["tableId"] = side.TableId,
        ["snapshotId"] = side.SnapshotId,
        ["detailDepth"] = side.DetailDepth,
        ["columnCount"] = side.ColumnCount,
        ["fileCount"] = side.FileCount,
        ["fileSize"] = side.FileSize,
        ["rowCount"] = side.RowCount,
        ["total"] = side.Total,
        ["waste"] = side.Waste,
    };

    private static JsonObject EntryJson(TableEntry entry)
    {
        JsonObject result = Side(entry.Table);
        result["recreated"] = entry.Recreated;
        return result;
    }

    private static JsonObject ColumnJson(ColumnSide side) => new()
    {
        ["id"] = side.Id,
        ["label"] = side.Label,
        ["name"] = side.Name,
        ["fieldId"] = side.FieldId,
        ["dataType"] = side.DataType,
        ["isNullable"] = side.IsNullable,
        ["compressedSize"] = side.CompressedSize,
    };

    private static JsonObject? Detail(DetailChanges? detail, bool commits) => detail is null ? null : commits
        ? new JsonObject { ["added"] = detail.Added, ["removed"] = detail.Removed, ["kept"] = detail.Kept }
        : new JsonObject
        {
            ["added"] = detail.Added,
            ["removed"] = detail.Removed,
            ["kept"] = detail.Kept,
            ["changed"] = detail.Changed,
            ["addedFileSize"] = detail.AddedFileSize,
            ["removedFileSize"] = detail.RemovedFileSize,
        };

    private static JsonObject KeptJson(KeptTable kept) => new()
    {
        ["status"] = kept.IsChanged ? "changed" : "unchanged",
        ["matchedBy"] = kept.MatchedBy,
        ["renamed"] = kept.Renamed,
        ["older"] = Side(kept.Older),
        ["newer"] = Side(kept.Newer),
        ["totals"] = Changes(kept.Totals),
        ["measures"] = Changes(kept.Measures),
        ["columns"] = new JsonObject
        {
            ["added"] = Items(kept.Columns.Added.Select(c => (JsonNode?)ColumnJson(c))),
            ["removed"] = Items(kept.Columns.Removed.Select(c => (JsonNode?)ColumnJson(c))),
            ["kept"] = Items(kept.Columns.Kept.Select(c => (JsonNode?)new JsonObject
            {
                ["status"] = c.IsChanged ? "changed" : "unchanged",
                ["matchedBy"] = c.MatchedBy,
                ["renamed"] = c.Renamed,
                ["older"] = ColumnJson(c.Older),
                ["newer"] = ColumnJson(c.Newer),
                ["totals"] = Changes(c.Totals),
                ["measures"] = Changes(c.Measures),
            })),
        },
        ["files"] = Detail(kept.Files, false),
        ["partitions"] = Detail(kept.Partitions, false),
        ["commits"] = Detail(kept.Commits, true),
    };

    private static JsonObject Header(FootprintMeasures measures) => new()
    {
        ["tableCount"] = measures.Tables.Count,
        ["filesRead"] = measures.FilesRead,
        ["warnings"] = Items(measures.Warnings.Select(w => (JsonNode?)JsonValue.Create(w))),
    };

    /// <summary>The result in the shape of the expected file, less the keys that only the command writes, the changes in the order of Rank.</summary>
    private static JsonNode AsJson(FootprintDiff diff)
    {
        IReadOnlyList<TableChange> ranked = StorageDiff.Rank(diff);
        var result = new JsonObject
        {
            ["older"] = Header(diff.Older),
            ["newer"] = Header(diff.Newer),
            ["smallFileSize"] = diff.SmallFileSize,
            ["tinyFileSize"] = diff.TinyFileSize,
            ["counts"] = new JsonObject { ["added"] = diff.Added.Count, ["removed"] = diff.Removed.Count, ["kept"] = diff.Kept.Count, ["changed"] = diff.ChangedCount },
            ["collection"] = Changes(diff.Collection),
            ["added"] = Items(ranked.Where(c => c.Kind == "added").Select(c => (JsonNode?)EntryJson(c.Entry!))),
            ["removed"] = Items(ranked.Where(c => c.Kind == "removed").Select(c => (JsonNode?)EntryJson(c.Entry!))),
            ["kept"] = Items(ranked.Where(c => c.Kind == "kept").Select(c => (JsonNode?)KeptJson(c.Kept!))),
        };
        // Through the text, so that every number reads back as a number of a parsed document.
        return JsonNode.Parse(result.ToJsonString())!;
    }

    /// <summary>The differences: objects key by key, arrays item by item, numbers exactly as doubles, the rest as JSON text.</summary>
    private static void Compare(JsonNode? actual, JsonNode? expected, string path, List<string> differences)
    {
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
            {
                differences.Add($"{path}: got {actual?.ToJsonString() ?? "null"}, expected {expected?.ToJsonString() ?? "null"}");
            }

            return;
        }

        switch (expected)
        {
            case JsonObject expectedObject when actual is JsonObject actualObject:
                foreach (string key in expectedObject.Select(p => p.Key).Union(actualObject.Select(p => p.Key)))
                {
                    if (!expectedObject.ContainsKey(key) || !actualObject.ContainsKey(key))
                    {
                        differences.Add($"{path}.{key}: present in only one of the two");
                    }
                    else
                    {
                        Compare(actualObject[key], expectedObject[key], path + "." + key, differences);
                    }
                }

                return;
            case JsonArray expectedArray when actual is JsonArray actualArray:
                if (expectedArray.Count != actualArray.Count)
                {
                    differences.Add($"{path}: {actualArray.Count} items, expected {expectedArray.Count}");
                    return;
                }

                for (int i = 0; i < expectedArray.Count; i++)
                {
                    Compare(actualArray[i], expectedArray[i], $"{path}[{i}]", differences);
                }

                return;
        }

        if (expected.GetValueKind() == JsonValueKind.Number && actual.GetValueKind() == JsonValueKind.Number)
        {
            if (actual.GetValue<double>() != expected.GetValue<double>())
            {
                differences.Add($"{path}: got {actual.ToJsonString()}, expected {expected.ToJsonString()}");
            }

            return;
        }

        if (actual.ToJsonString() != expected.ToJsonString())
        {
            differences.Add($"{path}: got {actual.ToJsonString()}, expected {expected.ToJsonString()}");
        }
    }

    [Fact]
    public void Orders_are_the_five_of_the_interface()
    {
        Assert.Equal(Orders, StorageDiff.Orders);
    }

    [Fact]
    public void The_kit_gives_the_expected_diff_less_the_header_of_the_command()
    {
        JsonObject expected = ExpectedDiff();
        expected.Remove("sort");
        expected.Remove("top");
        foreach (string side in new[] { "older", "newer" })
        {
            JsonNode header = expected[side]!;
            expected[side] = new JsonObject
            {
                ["tableCount"] = header["tableCount"]!.DeepClone(),
                ["filesRead"] = header["filesRead"]!.DeepClone(),
                ["warnings"] = header["warnings"]!.DeepClone(),
            };
        }

        var differences = new List<string>();
        Compare(AsJson(KitDiff()), expected, "$", differences);
        Assert.True(differences.Count == 0, string.Join("\n", differences.Take(40)));
    }

    [Fact]
    public void The_compared_values_are_in_the_order_of_the_interface()
    {
        JsonObject expected = ExpectedDiff();
        FootprintDiff diff = KitDiff();
        Assert.Equal(expected["collection"]!.AsObject().Select(p => p.Key), diff.Collection.Select(v => v.Name));
        foreach (KeptTable kept in diff.Kept)
        {
            JsonNode model = expected["kept"]!.AsArray().Single(k => (string)k!["newer"]!["name"]! == kept.Newer.Name)!;
            Assert.Equal(model["totals"]!.AsObject().Select(p => p.Key), kept.Totals.Select(v => v.Name));
            Assert.Equal(model["measures"]!.AsObject().Select(p => p.Key), kept.Measures.Select(v => v.Name));
            foreach (KeptColumn column in kept.Columns.Kept)
            {
                JsonNode columnModel = model["columns"]!["kept"]!.AsArray()[0]!;
                Assert.Equal(columnModel["totals"]!.AsObject().Select(p => p.Key), column.Totals.Select(v => v.Name));
                Assert.Equal(columnModel["measures"]!.AsObject().Select(p => p.Key), column.Measures.Select(v => v.Name));
            }
        }
    }

    [Fact]
    public void Compare_lists_the_changes_in_the_order_of_the_files()
    {
        FootprintDiff diff = KitDiff();
        Assert.Equal(["events", "products"], diff.Added.Select(e => e.Table.Name));
        Assert.Equal(["products", "legacy"], diff.Removed.Select(e => e.Table.Name));
        Assert.Equal(["sales", "clients", "orders", "staging", "audit"], diff.Kept.Select(k => k.Newer.Name));
        // Row: the position of the table in its document.
        Assert.Equal([0, 4], diff.Added.Select(e => e.Table.Row));
        Assert.Equal([3, 4], diff.Removed.Select(e => e.Table.Row));
        Assert.Equal([1, 2, 3, 5, 6], diff.Kept.Select(k => k.Newer.Row));
    }

    [Fact]
    public void The_header_and_the_counts_of_the_kit()
    {
        FootprintDiff diff = KitDiff();
        Assert.Equal(7, diff.Older.Tables.Count);
        Assert.Equal(7, diff.Newer.Tables.Count);
        Assert.True(diff.Older.FilesRead && diff.Newer.FilesRead);
        Assert.Empty(diff.Older.Warnings);
        Assert.Equal(134217728L, diff.SmallFileSize);
        Assert.Equal(8388608L, diff.TinyFileSize);
        Assert.Equal((2, 2, 5, 4), (diff.Added.Count, diff.Removed.Count, diff.Kept.Count, diff.ChangedCount));
    }

    // The cases of SCENARIO in conformance/make_diff.py, one by one.

    [Fact]
    public void Sales_is_kept_by_its_TableId_and_changed()
    {
        KeptTable sales = KeptByName(KitDiff(), "sales");
        Assert.Equal(("tableId", false, true), (sales.MatchedBy, sales.Renamed, sales.IsChanged));
        Assert.Equal(KitUuid(1), sales.Older.TableId);
        Assert.Equal(("4", "9"), (sales.Older.SnapshotId, sales.Newer.SnapshotId));
        Assert.Equal((4.0, 5.0, 1.0), Numbers(Value(sales.Totals, "fileCount")));
        Assert.Equal((124500.0, 170000.0, 45500.0), Numbers(Value(sales.Measures, "stack.total")));
        Assert.Equal((9000.0, 28000.0, 19000.0), Numbers(Value(sales.Measures, "stack.waste")));
    }

    [Fact]
    public void Sales_amt_is_renamed_amount_with_the_same_FieldId()
    {
        KeptTable sales = KeptByName(KitDiff(), "sales");
        Assert.Equal([("id", "id", "fieldId"), ("amt", "amount", "fieldId"), ("year", "year", "fieldId")], ColumnPairs(sales));
        KeptColumn amount = sales.Columns.Kept[1];
        Assert.True(amount.Renamed);
        Assert.True(amount.IsChanged);
        Assert.Equal((2L, 2L), (amount.Older.FieldId, amount.Newer.FieldId));
        Assert.Equal(("amt", "amount"), (amount.Older.Label, amount.Newer.Label));
        // year is a partition column that the files do not store: no totals, nothing changed.
        KeptColumn year = sales.Columns.Kept[2];
        Assert.False(year.IsChanged);
        Assert.Equal((null, null, null), Numbers(Value(year.Totals, "compressedSize")));
    }

    [Fact]
    public void Sales_discount_dropped_and_added_again_is_removed_and_added()
    {
        // discount has FieldId 3 before and 6 after: two columns, which neither the FieldId nor the name pairs.
        KeptTable sales = KeptByName(KitDiff(), "sales");
        Assert.Equal([("discount", 6L, "double"), ("region", 7L, "string")], sales.Columns.Added.Select(c => (c.Name, c.FieldId, c.DataType)));
        Assert.Equal([("discount", 3L, "double"), ("note", 4L, "string")], sales.Columns.Removed.Select(c => (c.Name, c.FieldId, c.DataType)));
    }

    [Fact]
    public void Sales_files_partitions_and_commits()
    {
        KeptTable sales = KeptByName(KitDiff(), "sales");
        Assert.Equal((2L, 1L, 3L, 1L, 40000L, 10000L), Counts(sales.Files));
        Assert.Equal((1L, 1L, 2L, 2L, 15000L, 10000L), Counts(sales.Partitions));
        Assert.Equal((5L, 2L, 3L, (long?)null, (long?)null, (long?)null), Counts(sales.Commits));
    }

    [Fact]
    public void Sales_timestamps_have_their_delta_in_milliseconds()
    {
        KeptTable sales = KeptByName(KitDiff(), "sales");
        ValueChange modified = Value(sales.Totals, "lastModifiedAt");
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero), Assert.IsType<DateTimeOffset>(modified.Older));
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero), Assert.IsType<DateTimeOffset>(modified.Newer));
        Assert.Equal(8 * Day, modified.Delta);
        Assert.Equal(4 * Day, Value(sales.Totals, "nonLiveNewestRemovedAt").Delta);
        Assert.Equal(8 * Day, Value(sales.Measures, "maintenance.lastCommitAt").Delta);
    }

    [Fact]
    public void Orders_shrunk_by_an_optimize_and_a_vacuum()
    {
        KeptTable orders = KeptByName(KitDiff(), "orders");
        Assert.Equal(("tableId", false, true), (orders.MatchedBy, orders.Renamed, orders.IsChanged));
        Assert.Equal((1L, 4L, 0L, 0L, 30000L, 32000L), Counts(orders.Files));
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), Counts(orders.Partitions));
        Assert.Equal((4L, 1L, 4L), (orders.Commits!.Added, orders.Commits.Removed, orders.Commits.Kept));
        ValueChange vacuum = Value(orders.Measures, "maintenance.hasVacuum");
        Assert.Equal(false, vacuum.Older);
        Assert.Equal(true, vacuum.Newer);
        Assert.Null(vacuum.Delta);
        Assert.True(vacuum.IsChanged);
        Assert.Equal((141000.0, 74500.0, -66500.0), Numbers(Value(orders.Measures, "stack.total")));
    }

    [Fact]
    public void Orders_has_no_FieldIds_and_price_changed_from_int_to_bigint()
    {
        KeptTable orders = KeptByName(KitDiff(), "orders");
        Assert.Equal([("order_id", "order_id", "name"), ("qty", "qty", "name"), ("price", "price", "name"), ("customer", "customer", "name")], ColumnPairs(orders));
        Assert.Empty(orders.Columns.Added);
        Assert.Empty(orders.Columns.Removed);
        KeptColumn price = orders.Columns.Kept[2];
        Assert.Equal(("int", "bigint"), (price.Older.DataType, price.Newer.DataType));
        Assert.True(price.IsChanged);
        Assert.False(price.Renamed);
    }

    [Fact]
    public void Customers_renamed_clients_with_the_same_TableId()
    {
        KeptTable clients = KeptByName(KitDiff(), "clients");
        Assert.Equal(("tableId", true, true), (clients.MatchedBy, clients.Renamed, clients.IsChanged));
        Assert.Equal(("customers", "customers"), (clients.Older.Name, clients.Older.Label));
        Assert.Equal(("clients", "clients"), (clients.Newer.Name, clients.Newer.Label));
        // The rename alone changes it.
        Assert.All(clients.Totals, v => Assert.False(v.IsChanged, v.Name));
        Assert.All(clients.Columns.Kept, c => Assert.False(c.IsChanged));
        Assert.Null(clients.Files);
        Assert.Equal((0L, 0L, 2L), (clients.Commits!.Added, clients.Commits.Removed, clients.Commits.Kept));
    }

    [Fact]
    public void Products_dropped_and_created_again_is_removed_and_added_both_recreated()
    {
        FootprintDiff diff = KitDiff();
        TableEntry removed = Assert.Single(diff.Removed, e => e.Table.Name == "products");
        TableEntry added = Assert.Single(diff.Added, e => e.Table.Name == "products");
        Assert.Equal((KitUuid(4), true), (removed.Table.TableId, removed.Recreated));
        Assert.Equal((KitUuid(5), true), (added.Table.TableId, added.Recreated));
        Assert.DoesNotContain(diff.Kept, k => k.Newer.Name == "products");
    }

    [Fact]
    public void Legacy_is_removed_and_events_added_neither_recreated()
    {
        FootprintDiff diff = KitDiff();
        TableEntry legacy = Assert.Single(diff.Removed, e => e.Table.Name == "legacy");
        TableEntry events = Assert.Single(diff.Added, e => e.Table.Name == "events");
        Assert.False(legacy.Recreated);
        Assert.False(events.Recreated);
        TableSide side = legacy.Table;
        Assert.Equal((5L, "legacy", (IReadOnlyList<string>?)null, KitUuid(6), "20", "Files"), (side.Id, side.Label, side.Namespace, side.TableId, side.SnapshotId, side.DetailDepth));
        Assert.Equal((2L, 2L, 136000L, 15000L, 181000.0, 0.0), (side.ColumnCount, side.FileCount, side.FileSize, side.RowCount, side.Total, side.Waste));
    }

    [Fact]
    public void Raw_staging_has_no_TableId_and_is_kept_by_its_name()
    {
        KeptTable staging = KeptByName(KitDiff(), "staging");
        Assert.Equal(("name", false, true), (staging.MatchedBy, staging.Renamed, staging.IsChanged));
        Assert.Null(staging.Older.TableId);
        Assert.Null(staging.Newer.TableId);
        Assert.Equal(["raw"], staging.Newer.Namespace!);
        Assert.Equal("raw.staging", staging.Newer.Label);
        Assert.Equal((1L, 0L, 2L, 0L, 5400L, 0L), Counts(staging.Files));
        Assert.Equal(["source_file"], staging.Columns.Added.Select(c => c.Name));
        Assert.Equal((2L, 3L), (staging.Older.ColumnCount, staging.Newer.ColumnCount));
    }

    [Fact]
    public void Audit_is_kept_unchanged_and_its_files_are_not_compared()
    {
        KeptTable audit = KeptByName(KitDiff(), "audit");
        Assert.Equal(("tableId", false, false), (audit.MatchedBy, audit.Renamed, audit.IsChanged));
        Assert.Equal(("Files", "Summary"), (audit.Older.DetailDepth, audit.Newer.DetailDepth));
        Assert.Null(audit.Files);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), Counts(audit.Partitions));
        Assert.Equal((0L, 0L, 4L), (audit.Commits!.Added, audit.Commits.Removed, audit.Commits.Kept));
    }

    [Fact]
    public void The_collection_of_the_kit()
    {
        FootprintDiff diff = KitDiff();
        Assert.Equal(["sources", "tables", "columns", "files", "fileSize", "rowCount", "footprint", "waste", "overhead", "depths.Files", "depths.Summary"], diff.Collection.Select(v => v.Name));
        Assert.Equal((649700.0, 392100.0, -257600.0), Numbers(Value(diff.Collection, "footprint")));
        Assert.Equal((5.0, 4.0, -1.0), Numbers(Value(diff.Collection, "depths.Files")));
        Assert.Equal((2.0, 3.0, 1.0), Numbers(Value(diff.Collection, "depths.Summary")));
    }

    [Fact]
    public void A_list_and_a_boolean_have_no_delta()
    {
        KeptTable sales = KeptByName(KitDiff(), "sales");
        ValueChange codecs = Value(sales.Columns.Kept[0].Totals, "codecs");
        Assert.Equal(["SNAPPY"], Assert.IsAssignableFrom<IReadOnlyList<string>>(codecs.Older));
        Assert.Equal(["SNAPPY"], Assert.IsAssignableFrom<IReadOnlyList<string>>(codecs.Newer));
        Assert.Null(codecs.Delta);
        Assert.False(codecs.IsChanged);
        ValueChange listed = Value(sales.Measures, "stack.listed");
        Assert.Equal((true, true, (double?)null), (listed.Older, listed.Newer, listed.Delta));
    }

    // ---- Rank on the kit ----

    public static TheoryData<string> OrderData => Kit.Data(Orders);

    private static readonly IReadOnlyDictionary<string, (string, string)[]> KitOrder = new Dictionary<string, (string, string)[]>
    {
        ["total"] = [("removed", "legacy"), ("removed", "products"), ("kept", "orders"), ("kept", "sales"), ("added", "events"), ("added", "products"), ("kept", "raw.staging"), ("kept", "audit"), ("kept", "clients")],
        ["waste"] = [("kept", "orders"), ("removed", "products"), ("kept", "sales"), ("kept", "audit"), ("kept", "clients"), ("added", "events"), ("removed", "legacy"), ("added", "products"), ("kept", "raw.staging")],
        ["rows"] = [("removed", "legacy"), ("removed", "products"), ("added", "events"), ("kept", "sales"), ("added", "products"), ("kept", "raw.staging"), ("kept", "audit"), ("kept", "clients"), ("kept", "orders")],
        ["files"] = [("kept", "orders"), ("removed", "products"), ("added", "events"), ("removed", "legacy"), ("added", "products"), ("kept", "raw.staging"), ("kept", "sales"), ("kept", "audit"), ("kept", "clients")],
        ["name"] = [("kept", "audit"), ("kept", "clients"), ("added", "events"), ("removed", "legacy"), ("kept", "orders"), ("added", "products"), ("removed", "products"), ("kept", "raw.staging"), ("kept", "sales")],
    };

    [Theory]
    [MemberData(nameof(OrderData))]
    public void Rank_orders_the_changes_of_the_kit(string order)
    {
        FootprintDiff diff = KitDiff();
        Assert.Equal(KitOrder[order], Labels(StorageDiff.Rank(diff, order)));
        foreach (int top in new[] { 1, 3, 9, 100 })
        {
            Assert.Equal(KitOrder[order].Take(top), Labels(StorageDiff.Rank(diff, order, top)));
        }
    }

    [Fact]
    public void Rank_by_total_is_the_default_and_gives_the_entry_or_the_kept_table_of_each_kind()
    {
        FootprintDiff diff = KitDiff();
        IReadOnlyList<TableChange> ranked = StorageDiff.Rank(diff);
        Assert.Equal(KitOrder["total"], Labels(ranked));
        Assert.All(ranked, change =>
        {
            Assert.Equal(change.Kind == "kept", change.Kept is not null);
            Assert.Equal(change.Kind != "kept", change.Entry is not null);
        });
    }

    [Fact]
    public void Rank_refuses_another_order()
    {
        FootprintDiff diff = KitDiff();
        Assert.ThrowsAny<ArgumentException>(() => StorageDiff.Rank(diff, "size"));
    }

    // ---- small footprints ----

    private static CollectionInfo Collected(DateTimeOffset? completed = null) => new()
    {
        CollectorApp = "pqax-tests",
        CollectorAppVersion = "0.0",
        StartedAt = completed ?? new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero),
        CompletedAt = completed ?? new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero),
        Level = 2,
    };

    private static DateTimeOffset Utc(int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new(2026, month, day, hour, minute, second, millisecond, TimeSpan.Zero);

    /// <summary>A table with constant totals unless given, in the source with id 1.</summary>
    private static TableRow T(string name, string? tableId = null, long? source = 1, string[]? ns = null, string? depth = "Summary",
        long? fileCount = 1, long? fileSize = 1000, long? rowCount = 10, string? snapshotId = null, DateTimeOffset? lastModifiedAt = null,
        DateTimeOffset? oldestCommitAt = null, long? deletedRowCount = null, long? unreferencedFileSize = null) => new()
    {
        Name = name,
        TableId = tableId,
        Source = source,
        Namespace = ns,
        Format = "Delta",
        DetailDepth = depth,
        FileCount = fileCount,
        FileSize = fileSize,
        RowCount = rowCount,
        SnapshotId = snapshotId,
        LastModifiedAt = lastModifiedAt,
        OldestCommitAt = oldestCommitAt,
        DeletedRowCount = deletedRowCount,
        UnreferencedFileSize = unreferencedFileSize,
    };

    private static SourceRow Lakehouse(long id = 0, string kind = "OneLake lakehouse", string name = "Lakehouse") => new() { Id = id, Kind = kind, Name = name };

    /// <summary>The bytes of a footprint with a source and these tables.</summary>
    private static byte[] Footprint(params TableRow[] tables) => Footprint(b =>
    {
        b.AddSource(Lakehouse());
        foreach (TableRow table in tables)
        {
            b.AddTable(table);
        }
    });

    private static byte[] Footprint(Action<FootprintBuilder> add, CollectionInfo? collection = null)
    {
        var builder = new FootprintBuilder(collection ?? Collected());
        add(builder);
        using var stream = new MemoryStream();
        builder.Write(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// A footprint with one value set to null after the builder wrote it: what the builder refuses, such as a required
    /// field without a value, and a reader still reads.
    /// </summary>
    private static byte[] Without(byte[] data, string part, string table, string field, int row) => Kit.Rewrite(data, (name, content) =>
    {
        if (name != part)
        {
            return content;
        }

        JsonNode json = Kit.Json(content);
        json[table]![field]![row] = null;
        return Kit.Bytes(json);
    });

    /// <summary>A footprint with a source and one table "t", TableId "1", with the rows that <paramref name="add"/> adds to it.</summary>
    private static byte[] OneTable(Action<FootprintBuilder, long> add, TableRow? table = null) => Footprint(b =>
    {
        b.AddSource(Lakehouse());
        long id = b.AddTable(table ?? T("t", "1"));
        add(b, id);
    });

    private static FootprintDiff Compare(byte[] older, byte[] newer, MeasureOptions? options = null)
    {
        using PqaxDocument a = PqaxDocument.Open(new MemoryStream(older));
        using PqaxDocument b = PqaxDocument.Open(new MemoryStream(newer));
        return StorageDiff.Compare(a, b, options);
    }

    private static (string?, string?, string)[] TablePairs(FootprintDiff diff) => diff.Kept.Select(k => (k.Older.Name, k.Newer.Name, k.MatchedBy)).ToArray();

    private static (string?, string?, bool)[] Entries(IEnumerable<TableEntry> entries) => entries.Select(e => (e.Table.Name, e.Table.TableId, e.Recreated)).ToArray();

    // ---- the identity of a table ----

    [Fact]
    public void The_TableId_wins_over_the_name()
    {
        FootprintDiff diff = Compare(Footprint(T("x", "1"), T("y", "2")), Footprint(T("x", "2"), T("y", "1")));
        Assert.Equal([("y", "x", "tableId"), ("x", "y", "tableId")], TablePairs(diff));
        Assert.All(diff.Kept, k => Assert.True(k.Renamed && k.IsChanged));
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Equal(2, diff.ChangedCount);
    }

    [Fact]
    public void Two_TableIds_that_differ_never_pair_even_with_the_same_name()
    {
        FootprintDiff diff = Compare(Footprint(T("t", "1")), Footprint(T("t", "2")));
        Assert.Empty(diff.Kept);
        Assert.Equal([("t", "1", true)], Entries(diff.Removed));
        Assert.Equal([("t", "2", true)], Entries(diff.Added));
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData("1", null)]
    [InlineData(null, null)]
    public void The_name_is_the_fallback_when_a_side_has_no_TableId(string? olderId, string? newerId)
    {
        FootprintDiff diff = Compare(Footprint(T("t", olderId)), Footprint(T("t", newerId)));
        Assert.Equal([("t", "t", "name")], TablePairs(diff));
        Assert.False(diff.Kept[0].Renamed);
    }

    [Fact]
    public void A_table_without_TableId_on_either_side_renamed_is_removed_and_added()
    {
        FootprintDiff diff = Compare(Footprint(T("before")), Footprint(T("after")));
        Assert.Empty(diff.Kept);
        Assert.Equal([("before", null, false)], Entries(diff.Removed));
        Assert.Equal([("after", null, false)], Entries(diff.Added));
    }

    public static TheoryData<string> FallbackCases => Kit.Data(["source name", "source kind", "no source", "namespace", "namespace null", "namespace split", "case"]);

    [Theory]
    [MemberData(nameof(FallbackCases))]
    public void The_fallback_needs_the_same_source_namespace_and_name(string change)
    {
        SourceRow source = change switch
        {
            "source name" => Lakehouse(name: "Other"),
            "source kind" => Lakehouse(kind: "ADLS Gen2 container"),
            _ => Lakehouse(),
        };
        (TableRow older, TableRow newer) = change switch
        {
            "no source" => (T("t"), T("t", source: null)),
            "namespace" => (T("t", ns: ["raw"]), T("t", ns: ["clean"])),
            "namespace null" => (T("t", ns: ["raw"]), T("t")),
            "namespace split" => (T("t", ns: ["a", "b"]), T("t", ns: ["a.b"])),
            "case" => (T("Sales"), T("sales")),
            _ => (T("t"), T("t")),
        };
        byte[] newerFile = Footprint(b =>
        {
            b.AddSource(source);
            b.AddTable(newer);
        });
        FootprintDiff diff = Compare(Footprint(older), newerFile);
        Assert.Empty(diff.Kept);
        // Not the same name key, so not recreated either.
        Assert.False(Assert.Single(diff.Removed).Recreated);
        Assert.False(Assert.Single(diff.Added).Recreated);
    }

    [Fact]
    public void The_fallback_compares_the_source_by_kind_and_name_not_by_its_id()
    {
        byte[] older = Footprint(b =>
        {
            b.AddSource(Lakehouse(id: 5));
            b.AddTable(T("t", source: 5));
        });
        byte[] newer = Footprint(b =>
        {
            b.AddSource(Lakehouse(id: 1, name: "Other"));
            b.AddSource(Lakehouse(id: 2));
            b.AddTable(T("t", source: 2));
        });
        Assert.Equal([("t", "t", "name")], TablePairs(Compare(older, newer)));
    }

    [Fact]
    public void Tables_without_a_source_pair_by_name()
    {
        byte[] file = Footprint(b => b.AddTable(T("t", source: null)));
        Assert.Equal([("t", "t", "name")], TablePairs(Compare(file, file)));
    }

    [Fact]
    public void A_new_namespace_with_the_same_TableId_is_renamed()
    {
        KeptTable kept = Assert.Single(Compare(Footprint(T("t", "1", ns: ["raw"])), Footprint(T("t", "1", ns: ["clean"]))).Kept);
        Assert.Equal(("tableId", true, true), (kept.MatchedBy, kept.Renamed, kept.IsChanged));
        Assert.Equal(("raw.t", "clean.t"), (kept.Older.Label, kept.Newer.Label));
    }

    [Fact]
    public void The_first_pass_pairs_the_same_TableId_and_the_same_name()
    {
        // Two tables with the same TableId on each side: the first pass pairs a with a and b with b, although the second
        // alone would pair the first older table with the first newer one.
        FootprintDiff diff = Compare(Footprint(T("a", "1"), T("b", "1")), Footprint(T("b", "1"), T("a", "1")));
        Assert.Equal([("b", "b", "tableId"), ("a", "a", "tableId")], TablePairs(diff));
        Assert.All(diff.Kept, k => Assert.False(k.Renamed));
    }

    [Fact]
    public void The_TableId_pass_comes_before_the_name_pass()
    {
        // The older t has no TableId and the name of the newer t; u has its TableId. The TableId pairs u with t first, and
        // the older t is removed.
        FootprintDiff diff = Compare(Footprint(T("t", fileSize: 100), T("u", "1")), Footprint(T("t", "1")));
        Assert.Equal([("u", "t", "tableId")], TablePairs(diff));
        Assert.Equal([("t", null, false)], Entries(diff.Removed));
        Assert.Empty(diff.Added);
    }

    [Fact]
    public void Each_older_table_takes_the_first_unpaired_newer_table_in_the_order_of_the_file()
    {
        FootprintDiff diff = Compare(Footprint(T("a", "1")), Footprint(T("b", "1"), T("c", "1")));
        Assert.Equal([("a", "b", "tableId")], TablePairs(diff));
        Assert.Equal([("c", "1", false)], Entries(diff.Added));

        diff = Compare(Footprint(T("a", "1"), T("b", "1")), Footprint(T("c", "1")));
        Assert.Equal([("a", "c", "tableId")], TablePairs(diff));
        Assert.Equal([("b", "1", false)], Entries(diff.Removed));

        diff = Compare(Footprint(T("t", fileSize: 100), T("t", fileSize: 200)), Footprint(T("t")));
        Assert.Equal(100L, Assert.Single(diff.Kept).Older.FileSize);
        Assert.Equal(200L, Assert.Single(diff.Removed).Table.FileSize);
    }

    [Fact]
    public void The_side_of_a_table()
    {
        byte[] older = OneTable((b, t) =>
        {
            b.AddColumn(new ColumnRow { Table = t, Name = "a", DataType = "string" });
            b.AddColumn(new ColumnRow { Table = t, Name = "b", DataType = "string" });
        }, T("t", "1", ns: ["raw"], snapshotId: "3", depth: "Files"));
        KeptTable kept = Assert.Single(Compare(older, Footprint(T("t", "1", ns: ["raw"], snapshotId: "3", depth: "Files"))).Kept);
        TableSide side = kept.Older;
        Assert.Equal((1L, 0, "raw.t", "t", "1", "3", "Files"), (side.Id, side.Row, side.Label, side.Name, side.TableId, side.SnapshotId, side.DetailDepth));
        Assert.Equal(["raw"], side.Namespace!);
        Assert.Equal((2L, 1L, 1000L, 10L, 1000.0, (double?)null), (side.ColumnCount, side.FileCount, side.FileSize, side.RowCount, side.Total, side.Waste));
        Assert.Equal(0L, kept.Newer.ColumnCount);
    }

    // ---- the identity of a column ----

    private static byte[] WithColumns(params ColumnRow[] columns) => OneTable((b, t) =>
    {
        foreach (ColumnRow column in columns)
        {
            b.AddColumn(new ColumnRow
            {
                Table = t,
                Name = column.Name,
                FieldId = column.FieldId,
                DataType = column.DataType,
                IsNullable = column.IsNullable,
                CompressedSize = column.CompressedSize,
                Codecs = column.Codecs,
                Encodings = column.Encodings,
            });
        }
    });

    private static ColumnRow C(string name, long? fieldId = null, string? dataType = "int") => new() { Name = name, FieldId = fieldId, DataType = dataType };

    [Fact]
    public void Columns_pair_by_FieldId_then_by_name()
    {
        FootprintDiff diff = Compare(
            WithColumns(C("a", 1), C("b", 2), C("c"), C("d", 4), C("e")),
            WithColumns(C("a", 2), C("b", 1), C("c", 9), C("d", 5), C("f")));
        KeptTable kept = Assert.Single(diff.Kept);
        // The FieldIds swap the names of a and b; c has a FieldId on one side only, so its name pairs it; d has two
        // FieldIds that differ, so it is removed and added.
        Assert.Equal([("b", "a", "fieldId"), ("a", "b", "fieldId"), ("c", "c", "name")], ColumnPairs(kept));
        Assert.Equal([true, true, false], kept.Columns.Kept.Select(c => c.Renamed));
        Assert.Equal([("d", 5L), ("f", (long?)null)], kept.Columns.Added.Select(c => (c.Name, c.FieldId)));
        Assert.Equal([("d", 4L), ("e", (long?)null)], kept.Columns.Removed.Select(c => (c.Name, c.FieldId)));
        Assert.True(kept.IsChanged);
    }

    [Fact]
    public void The_first_pass_of_the_columns_pairs_the_same_FieldId_and_the_same_name()
    {
        KeptTable kept = Assert.Single(Compare(WithColumns(C("a", 1), C("b", 1)), WithColumns(C("b", 1), C("a", 1))).Kept);
        Assert.Equal([("b", "b", "fieldId"), ("a", "a", "fieldId")], ColumnPairs(kept));
        Assert.False(kept.IsChanged);
    }

    [Fact]
    public void A_column_renamed_without_FieldId_is_removed_and_added()
    {
        KeptTable kept = Assert.Single(Compare(WithColumns(C("a")), WithColumns(C("b"))).Kept);
        Assert.Empty(kept.Columns.Kept);
        Assert.Equal(["b"], kept.Columns.Added.Select(c => c.Name));
        Assert.Equal(["a"], kept.Columns.Removed.Select(c => c.Name));
    }

    public static TheoryData<string, bool> ColumnChanges => new()
    {
        { "nothing", false }, { "data type", true }, { "nullable", true }, { "a total", true }, { "a list", true }, { "the order of a list", true },
    };

    [Theory]
    [MemberData(nameof(ColumnChanges))]
    public void What_changes_a_column(string change, bool changed)
    {
        ColumnRow Make(bool newer) => new()
        {
            Name = "c",
            DataType = newer && change == "data type" ? "bigint" : "int",
            IsNullable = !(newer && change == "nullable"),
            CompressedSize = newer && change == "a total" ? 101 : 100,
            Codecs = newer && change == "a list" ? ["ZSTD"] : ["SNAPPY"],
            Encodings = newer && change == "the order of a list" ? ["RLE", "PLAIN"] : ["PLAIN", "RLE"],
        };
        KeptTable kept = Assert.Single(Compare(WithColumns(Make(false)), WithColumns(Make(true))).Kept);
        KeptColumn column = Assert.Single(kept.Columns.Kept);
        Assert.Equal(changed, column.IsChanged);
        Assert.Equal(changed, kept.IsChanged);
        Assert.Equal((1L, 0, "c", "c", (long?)null, "int", (bool?)true, (long?)100), (column.Older.Id, column.Older.Row, column.Older.Label, column.Older.Name, column.Older.FieldId, column.Older.DataType, column.Older.IsNullable, column.Older.CompressedSize));
        Assert.Null(Value(column.Totals, "codecs").Delta);
        Assert.Null(Value(column.Totals, "encodings").Delta);
    }

    // ---- the compared values ----

    [Fact]
    public void The_delta_is_null_when_a_side_is_null()
    {
        KeptTable kept = Assert.Single(Compare(Footprint(T("t", "1")), Footprint(T("t", "1", deletedRowCount: 5))).Kept);
        ValueChange deleted = Value(kept.Totals, "deletedRowCount");
        Assert.Equal(((double?)null, (double?)5, (double?)null), Numbers(deleted));
        Assert.True(deleted.IsChanged);
        ValueChange nonLive = Value(kept.Totals, "nonLiveFileSize");
        Assert.Equal(((double?)null, (double?)null, (double?)null), Numbers(nonLive));
        Assert.False(nonLive.IsChanged);
        Assert.True(kept.IsChanged);
    }

    [Fact]
    public void The_same_values_on_both_sides_leave_a_table_unchanged()
    {
        FootprintDiff diff = Compare(Footprint(T("t", "1", snapshotId: "4")), Footprint(T("t", "1", snapshotId: "4")));
        Assert.False(Assert.Single(diff.Kept).IsChanged);
        Assert.Equal(0, diff.ChangedCount);
        Assert.All(diff.Kept[0].Totals, v => Assert.True(v.Delta is null or 0, v.Name));
    }

    [Fact]
    public void The_delta_of_a_timestamp_is_in_milliseconds()
    {
        KeptTable kept = Assert.Single(Compare(
            Footprint(T("t", "1", lastModifiedAt: Utc(10, 1), oldestCommitAt: Utc(9, 1))),
            Footprint(T("t", "1", lastModifiedAt: Utc(10, 2, 0, 0, 1, 500)))).Kept);
        ValueChange modified = Value(kept.Totals, "lastModifiedAt");
        Assert.Equal((object)Utc(10, 1), modified.Older);
        Assert.Equal((object)Utc(10, 2, 0, 0, 1, 500), modified.Newer);
        Assert.Equal(86_401_500.0, modified.Delta);
        ValueChange oldest = Value(kept.Totals, "oldestCommitAt");
        Assert.Null(oldest.Newer);
        Assert.Null(oldest.Delta);
    }

    [Fact]
    public void A_boolean_has_no_delta()
    {
        // Listed is whether UnreferencedFileSize is present.
        KeptTable kept = Assert.Single(Compare(Footprint(T("t", "1")), Footprint(T("t", "1", unreferencedFileSize: 0))).Kept);
        ValueChange listed = Value(kept.Measures, "stack.listed");
        Assert.Equal((false, true, (double?)null, true), (listed.Older, listed.Newer, listed.Delta, listed.IsChanged));
    }

    [Fact]
    public void A_SnapshotId_alone_changes_a_table()
    {
        Assert.True(Assert.Single(Compare(Footprint(T("t", "1", snapshotId: "1")), Footprint(T("t", "1", snapshotId: "2"))).Kept).IsChanged);
    }

    [Fact]
    public void A_change_of_the_measures_alone_leaves_a_table_unchanged()
    {
        // The same table collected ten days later: DaysSinceLastCommit changes with the time of the collection alone.
        TableRow table = T("t", "1", lastModifiedAt: Utc(10, 1));
        byte[] older = Footprint(b =>
        {
            b.AddSource(Lakehouse());
            b.AddTable(table);
        });
        byte[] newer = Footprint(
            b =>
            {
                b.AddSource(Lakehouse());
                b.AddTable(table);
            },
            Collected(Utc(10, 21)));
        FootprintDiff diff = Compare(older, newer);
        KeptTable kept = Assert.Single(diff.Kept);
        Assert.Equal((10.0, 20.0, 10.0), Numbers(Value(kept.Measures, "maintenance.daysSinceLastCommit")));
        Assert.True(Value(kept.Measures, "maintenance.daysSinceLastCommit").IsChanged);
        Assert.False(kept.IsChanged);
        Assert.Equal(0, diff.ChangedCount);
    }

    [Fact]
    public void The_depths_of_the_collection_count_0_where_a_footprint_has_none()
    {
        FootprintDiff diff = Compare(Footprint(T("t", "1")), Footprint(T("t", "1", depth: "Files")));
        Assert.Equal(["depths.Files", "depths.Summary"], diff.Collection.Select(v => v.Name).TakeLast(2));
        Assert.Equal((0.0, 1.0, 1.0), Numbers(Value(diff.Collection, "depths.Files")));
        Assert.Equal((1.0, 0.0, -1.0), Numbers(Value(diff.Collection, "depths.Summary")));
    }

    [Fact]
    public void The_thresholds_of_the_small_files()
    {
        byte[] file = OneTable(
            (b, t) =>
            {
                b.AddFile(new FileRow { Table = t, Path = "a.parquet", FileSize = 500 });
                b.AddFile(new FileRow { Table = t, Path = "b.parquet", FileSize = 2000 });
            },
            T("t", "1", depth: "Files", fileCount: 2, fileSize: 2500));
        FootprintDiff diff = Compare(file, file, new MeasureOptions { SmallFileSize = 1000, TinyFileSize = 600 });
        Assert.Equal((1000L, 600L), (diff.SmallFileSize, diff.TinyFileSize));
        Assert.Equal((1.0, 1.0, 0.0), Numbers(Value(diff.Kept[0].Measures, "smallFiles.count")));
        Assert.Equal((2.0, 2.0, 0.0), Numbers(Value(Compare(file, file).Kept[0].Measures, "smallFiles.count")));
    }

    // ---- files ----

    private static byte[] WithFiles(string? depth, params FileRow[] files) => OneTable(
        (b, t) =>
        {
            foreach (FileRow file in files)
            {
                b.AddFile(new FileRow
                {
                    Table = t,
                    Path = file.Path,
                    FileSize = file.FileSize,
                    RowCount = file.RowCount,
                    DeletedRowCount = file.DeletedRowCount,
                    DeletionVectorSize = file.DeletionVectorSize,
                    ModifiedAt = file.ModifiedAt,
                });
            }
        },
        T("t", "1", depth: depth));

    private static FileRow F(string? path, long? size = null) => new() { Path = path, FileSize = size };

    [Fact]
    public void The_files_pair_by_path()
    {
        FootprintDiff diff = Compare(
            WithFiles("Files", F("p1", 100), F("p2", 200), F("p3", 300)),
            WithFiles("Files", F("p2", 200), new FileRow { Path = "p3", FileSize = 300, DeletedRowCount = 5 }, F("p4", 400), F("p5")));
        KeptTable kept = Assert.Single(diff.Kept);
        // p4 and p5 added, the size of p5 unknown; p1 removed; p2 and p3 kept, p3 changed.
        Assert.Equal((2L, 1L, 2L, 1L, 400L, 100L), Counts(kept.Files));
        Assert.True(kept.IsChanged);
    }

    [Fact]
    public void The_size_of_the_files_added_is_null_when_none_has_a_size_and_0_when_none_was_added()
    {
        KeptTable kept = Assert.Single(Compare(WithFiles("Files", F("p1", 100)), WithFiles("Files", F("p1", 100), F("p2"))).Kept);
        Assert.Equal((1L, 0L, 1L, 0L, (long?)null, 0L), Counts(kept.Files));
    }

    public static TheoryData<string, bool> FileChanges => new()
    {
        { "size", true }, { "rows", true }, { "deleted rows", true }, { "deletion vector from null", true }, { "modified only", false }, { "nothing", false },
    };

    [Theory]
    [MemberData(nameof(FileChanges))]
    public void What_changes_a_file(string change, bool changed)
    {
        FileRow Make(bool newer) => new()
        {
            Path = "p1",
            FileSize = newer && change == "size" ? 101 : 100,
            RowCount = newer && change == "rows" ? 11 : 10,
            DeletedRowCount = newer && change == "deleted rows" ? 1 : 0,
            DeletionVectorSize = newer && change == "deletion vector from null" ? 40 : null,
            ModifiedAt = newer && change == "modified only" ? Utc(10, 2) : Utc(10, 1),
        };
        KeptTable kept = Assert.Single(Compare(WithFiles("Files", Make(false)), WithFiles("Files", Make(true))).Kept);
        Assert.Equal((0L, 0L, 1L, changed ? 1L : 0L, 0L, 0L), Counts(kept.Files));
        Assert.Equal(changed, kept.IsChanged);
    }

    [Theory]
    [InlineData("Files", "Summary")]
    [InlineData("Summary", "Files")]
    [InlineData("Files", null)]
    public void The_files_are_not_compared_when_a_side_does_not_keep_them(string? olderDepth, string? newerDepth)
    {
        FileRow[] Files(string? depth) => depth == "Summary" ? [] : [F("p1", 100)];

        // The builder refuses a table without a detailDepth, which is required, so that side loses it after the write.
        byte[] Side(string? depth) => depth == null
            ? Without(WithFiles("Files", Files(depth)), "summary.json", "tables", "detailDepth", 0)
            : WithFiles(depth, Files(depth));
        KeptTable kept = Assert.Single(Compare(Side(olderDepth), Side(newerDepth)).Kept);
        Assert.Null(kept.Files);
    }

    [Fact]
    public void The_files_are_not_compared_when_a_file_has_no_path()
    {
        KeptTable kept = Assert.Single(Compare(WithFiles("Files", F("p1", 100), F(null, 50)), WithFiles("Files", F("p1", 100))).Kept);
        Assert.Null(kept.Files);
    }

    [Fact]
    public void The_files_are_not_compared_when_the_files_table_cannot_be_read()
    {
        byte[] data = WithFiles("Files", F("p1", 100));
        byte[] broken = Kit.Rewrite(data, (name, content) => name == "files.json" ? Encoding.UTF8.GetBytes("{\"files\": {\"id\": [1, 2,") : content);
        FootprintDiff diff = Compare(data, broken);
        Assert.Null(Assert.Single(diff.Kept).Files);
        Assert.True(diff.Older.FilesRead);
        Assert.False(diff.Newer.FilesRead);
        Assert.NotEmpty(diff.Newer.Warnings);
    }

    // ---- partitions ----

    private static PartitionRow P(string?[]? values, long size, long fileCount = 1, long rowCount = 10, long deletedRowCount = 0, DateTimeOffset? modified = null) => new()
    {
        Values = values!,
        FileCount = fileCount,
        FileSize = size,
        RowCount = rowCount,
        DeletedRowCount = deletedRowCount,
        LastModifiedAt = modified,
    };

    private static void AddPartitions(FootprintBuilder b, long table, IEnumerable<PartitionRow> partitions)
    {
        foreach (PartitionRow p in partitions)
        {
            b.AddPartition(new PartitionRow
            {
                Table = table,
                Values = p.Values,
                FileCount = p.FileCount,
                FileSize = p.FileSize,
                RowCount = p.RowCount,
                DeletedRowCount = p.DeletedRowCount,
                LastModifiedAt = p.LastModifiedAt,
            });
        }
    }

    private static byte[] WithPartitions(params PartitionRow[] partitions) => OneTable((b, t) => AddPartitions(b, t, partitions));

    [Fact]
    public void The_partitions_pair_by_their_values_a_null_equal_to_a_null()
    {
        FootprintDiff diff = Compare(
            WithPartitions(P(["2024"], 100), P(["2025"], 200), P([null], 50), P(["a", null], 70)),
            WithPartitions(P(["2025"], 250), P([null], 50), P(["2026"], 300), P([null, "a"], 80)));
        KeptTable kept = Assert.Single(diff.Kept);
        // Kept: 2025 (changed) and [null]. Added: 2026 and [null, a]; removed: 2024 and [a, null].
        Assert.Equal((2L, 2L, 2L, 1L, 380L, 170L), Counts(kept.Partitions));
        Assert.True(kept.IsChanged);
    }

    public static TheoryData<string, bool> PartitionChanges => new()
    {
        { "files", true }, { "size", true }, { "rows", true }, { "deleted rows", true }, { "modified only", false },
    };

    [Theory]
    [MemberData(nameof(PartitionChanges))]
    public void What_changes_a_partition(string change, bool changed)
    {
        PartitionRow Make(bool newer) => P(
            ["2025"],
            newer && change == "size" ? 101 : 100,
            fileCount: newer && change == "files" ? 2 : 1,
            rowCount: newer && change == "rows" ? 11 : 10,
            deletedRowCount: newer && change == "deleted rows" ? 1 : 0,
            modified: newer && change == "modified only" ? Utc(10, 2) : Utc(10, 1));
        KeptTable kept = Assert.Single(Compare(WithPartitions(Make(false)), WithPartitions(Make(true))).Kept);
        Assert.Equal((0L, 0L, 1L, changed ? 1L : 0L, 0L, 0L), Counts(kept.Partitions));
    }

    [Fact]
    public void The_partitions_of_a_table_with_a_partition_without_values_are_not_compared()
    {
        byte[] Two(params PartitionRow[] ofU) => Footprint(b =>
        {
            b.AddSource(Lakehouse());
            long t = b.AddTable(T("t", "1"));
            long u = b.AddTable(T("u", "2"));
            AddPartitions(b, t, [P(["2025"], 100)]);
            AddPartitions(b, u, ofU);
        });
        FootprintDiff diff = Compare(Two(P(["x"], 100), P(null, 100)), Two(P(["x"], 100)));
        Assert.Null(Assert.Single(diff.Kept, k => k.Newer.Name == "u").Partitions);
        Assert.Equal((0L, 0L, 1L, 0L, 0L, 0L), Counts(Assert.Single(diff.Kept, k => k.Newer.Name == "t").Partitions));
    }

    [Fact]
    public void The_partitions_are_not_compared_when_a_footprint_keeps_none()
    {
        Assert.Null(Assert.Single(Compare(WithPartitions(P(["2025"], 100)), Footprint(T("t", "1"))).Kept).Partitions);
    }

    [Fact]
    public void A_table_without_partitions_compares_zero_partitions_when_both_footprints_keep_some()
    {
        byte[] file = Footprint(b =>
        {
            b.AddSource(Lakehouse());
            long t = b.AddTable(T("t", "1"));
            b.AddTable(T("u", "2"));
            AddPartitions(b, t, [P(["2025"], 100)]);
        });
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), Counts(Assert.Single(Compare(file, file).Kept, k => k.Newer.Name == "u").Partitions));
    }

    // ---- commits ----

    private static byte[] WithCommits(params string?[] ids) => OneTable((b, t) =>
    {
        for (int i = 0; i < ids.Length; i++)
        {
            b.AddCommit(new CommitRow { Table = t, CommitId = ids[i], Timestamp = Utc(10, i + 1) });
        }
    });

    [Fact]
    public void The_commits_pair_by_CommitId()
    {
        KeptTable kept = Assert.Single(Compare(WithCommits("1", "2", "3"), WithCommits("2", "3", "4", "5")).Kept);
        Assert.Equal((2L, 1L, 2L), (kept.Commits!.Added, kept.Commits.Removed, kept.Commits.Kept));
        Assert.Null(kept.Commits.Changed);
        Assert.Null(kept.Commits.AddedFileSize);
        Assert.Null(kept.Commits.RemovedFileSize);
        Assert.True(kept.IsChanged);
    }

    [Fact]
    public void The_same_commits_leave_a_table_unchanged()
    {
        KeptTable kept = Assert.Single(Compare(WithCommits("1", "2"), WithCommits("1", "2")).Kept);
        Assert.Equal((0L, 0L, 2L), (kept.Commits!.Added, kept.Commits.Removed, kept.Commits.Kept));
        Assert.False(kept.IsChanged);
    }

    [Fact]
    public void The_commits_are_not_compared_with_a_commit_without_id_or_a_footprint_without_commits()
    {
        // The builder refuses a commit without a commitId, which is required, so the second commit loses it after the write.
        byte[] withoutId = Without(WithCommits("1", "2"), "commits.json", "commits", "commitId", 1);
        Assert.Null(Assert.Single(Compare(withoutId, WithCommits("1")).Kept).Commits);
        Assert.Null(Assert.Single(Compare(WithCommits("1"), Footprint(T("t", "1"))).Kept).Commits);
    }

    // ---- Rank on small footprints ----

    [Fact]
    public void Rank_by_magnitude_then_label_then_kind_then_position_with_a_null_magnitude_last()
    {
        // The Total of each table is its FileSize: no other component is present.
        byte[] older = Footprint(T("m", "1", fileSize: 500), T("k", "9", fileSize: 900), T("n", "8", fileSize: null));
        byte[] newer = Footprint(T("z", "2", fileSize: null), T("x", "7", fileSize: 100), T("b", "3", fileSize: 500), T("m", "5", fileSize: 500),
            T("x", "6", fileSize: 100), T("a", "4", fileSize: 100), T("k", "9", fileSize: 600), T("n", "8", fileSize: 50));
        FootprintDiff diff = Compare(older, newer);
        (string, string, string?)[] Keys(IEnumerable<TableChange> changes) => changes
            .Select(c => (c.Kind, Label(c), c.Kind == "kept" ? c.Kept!.Newer.TableId : c.Entry!.Table.TableId)).ToArray();

        // m removed and m added: the same magnitude and label, added first. x twice: the order of the newer file. k kept
        // with a delta of -300, by its absolute value. n kept with a null delta and z added with a null total, last.
        Assert.Equal(
            [("added", "b", "3"), ("added", "m", "5"), ("removed", "m", "1"), ("kept", "k", "9"), ("added", "a", "4"), ("added", "x", "7"), ("added", "x", "6"), ("kept", "n", "8"), ("added", "z", "2")],
            Keys(StorageDiff.Rank(diff, "total")));
        Assert.Equal(
            [("added", "a", "4"), ("added", "b", "3"), ("kept", "k", "9"), ("added", "m", "5"), ("removed", "m", "1"), ("kept", "n", "8"), ("added", "x", "7"), ("added", "x", "6"), ("added", "z", "2")],
            Keys(StorageDiff.Rank(diff, "name")));
        Assert.Equal([("added", "b", "3"), ("added", "m", "5")], Keys(StorageDiff.Rank(diff, "total", 2)));
    }

    [Fact]
    public void Rank_orders_the_labels_by_UTF16_code_units()
    {
        // Ordinal: Z (U+005A) before a (U+0061); U+1F600, whose UTF-16 starts with the surrogate U+D83D, before U+FF41.
        FootprintDiff diff = Compare(Footprint(), Footprint(T("a", "1"), T("Z", "2"), T("\U0001F600", "3"), T("ａ", "4")));
        Assert.Equal(["Z", "a", "\U0001F600", "ａ"], StorageDiff.Rank(diff, "name").Select(Label));
    }

    [Fact]
    public void Rank_by_rows_and_files_uses_the_counts()
    {
        FootprintDiff diff = Compare(
            Footprint(T("a", "1", rowCount: 100, fileCount: 1), T("b", "2", rowCount: 10, fileCount: 9)),
            Footprint(T("a", "1", rowCount: 150, fileCount: 1), T("c", "3", rowCount: 60, fileCount: null)));
        Assert.Equal([("added", "c"), ("kept", "a"), ("removed", "b")], Labels(StorageDiff.Rank(diff, "rows")));
        Assert.Equal([("removed", "b"), ("kept", "a"), ("added", "c")], Labels(StorageDiff.Rank(diff, "files")));
    }
}
