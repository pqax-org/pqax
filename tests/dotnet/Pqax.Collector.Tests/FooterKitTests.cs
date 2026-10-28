using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pqax.Collector.Tests;

/// <summary>
/// The kit of footers of other writers, as conformance/footers.md and its README describe it: conformance/samples/footers holds
/// Parquet files and expected.json, one member per file, with writer, what, statedSize (null for the size of the file),
/// includeDataValues, and either error (code, message) or footer (numRows, rowGroups, createdBy, leaves, columns). A
/// member with error is read with <see cref="FooterReader"/> and its stated size, and must give that code and message;
/// a member with footer must give its rows, its row groups, its writer, and its leaves, and a folder of Parquet files
/// that holds a copy of the file alone must give its columns, with the ordinals of the leaves whose physical column
/// refers to each. test_collect_footer_kit.py reads the same members.
/// </summary>
public class FooterKitTests
{
    private const string NoKit = "(no kit)";

    private static string KitFolder => Path.Combine(Fixture.Root, "conformance", "samples", "footers");

    private static string ExpectedPath => Path.Combine(KitFolder, "expected.json");

    /// <summary>expected.json, or null and the reason why it cannot be read.</summary>
    private static JsonObject? LoadExpected(out string problem)
    {
        problem = "";
        if (!Directory.Exists(KitFolder))
        {
            problem = $"The folder of the kit of footers, {KitFolder}, does not exist.";
            return null;
        }

        if (!File.Exists(ExpectedPath))
        {
            problem = $"The kit of footers has no expected.json: {ExpectedPath}.";
            return null;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(ExpectedPath)) is JsonObject expected)
            {
                return expected;
            }

            problem = "expected.json of the kit of footers is not a JSON object with one member per file.";
        }
        catch (JsonException e)
        {
            problem = "expected.json of the kit of footers is not valid JSON: " + e.Message;
        }

        return null;
    }

    /// <summary>The members of expected.json, or one row that fails the test with the reason when the kit is missing: never no row, which would skip.</summary>
    public static TheoryData<string> Members
    {
        get
        {
            var data = new TheoryData<string>();
            JsonObject? expected = LoadExpected(out string problem);
            if (expected == null)
            {
                data.Add(NoKit + " " + problem);
                return data;
            }

            foreach (KeyValuePair<string, JsonNode?> member in expected)
            {
                if (member.Value is JsonObject)
                {
                    data.Add(member.Key);
                }
            }

            if (data.Count == 0)
            {
                data.Add(NoKit + " expected.json of the kit of footers has no member.");
            }

            return data;
        }
    }

    /// <summary>The file of a member: its name, or its name with .parquet.</summary>
    private static string? FileOf(string member)
    {
        foreach (string name in new[] { member, member + ".parquet" })
        {
            if (name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(KitFolder, name)))
            {
                return name;
            }
        }

        return null;
    }

    [Fact]
    public void The_kit_has_an_expected_json_with_one_member_for_each_parquet_file()
    {
        JsonObject? expected = LoadExpected(out string problem);
        Assert.True(expected != null, problem);
        var members = expected!.Where(member => member.Value is JsonObject).Select(member => member.Key).ToList();
        Assert.NotEmpty(members);
        Assert.All(members, member => Assert.True(FileOf(member) != null, $"The member {member} of expected.json has no file in {KitFolder}."));
        var files = Directory.EnumerateFiles(KitFolder, "*.parquet").Select(Path.GetFileName).ToList();
        Assert.All(files, file => Assert.True(members.Any(member => FileOf(member) == file), $"{file} has no member in expected.json."));
        Assert.All(members, member =>
        {
            // Either error or footer: the other is absent or null.
            JsonObject entry = expected[member]!.AsObject();
            Assert.True((entry["error"] is JsonObject) != (entry["footer"] is JsonObject), $"{member}: a member has either error or footer.");
        });
    }

    [Theory]
    [MemberData(nameof(Members))]
    public async Task Every_member_of_the_kit_reads_as_expected_json_says(string member)
    {
        Assert.False(member.StartsWith(NoKit, StringComparison.Ordinal), member);
        JsonObject entry = LoadExpected(out _)![member]!.AsObject();
        string? file = FileOf(member);
        Assert.True(file != null, $"The member {member} of expected.json has no file in {KitFolder}.");
        long size = entry["statedSize"] is JsonNode stated ? stated.GetValue<long>() : new FileInfo(Path.Combine(KitFolder, file!)).Length;
        bool includeDataValues = entry["includeDataValues"]?.GetValue<bool>() ?? false;
        var reader = new FooterReader(new LocalStorage(KitFolder), includeDataValues: includeDataValues);
        string what = $"{member} ({entry["writer"]}: {entry["what"]})";

        if (entry["error"] is JsonObject error)
        {
            Exception? thrown = await Record.ExceptionAsync(() => reader.ReadAsync(file!, size, CancellationToken.None));
            var refused = Assert.IsType<ParquetFooterException>(thrown);
            Assert.True(
                ((string?)error["code"], (string?)error["message"]) == (refused.Code, refused.Message),
                $"{what}:\n  expected {error["code"]}: {error["message"]}\n  actual   {refused.Code}: {refused.Message}");
            return;
        }

        JsonObject expected = Assert.IsType<JsonObject>(entry["footer"]);
        ParquetFooter footer;
        try
        {
            footer = await reader.ReadAsync(file!, size, CancellationToken.None);
        }
        catch (ParquetFooterException refused)
        {
            Assert.Fail($"{what}: {refused.Code}: {refused.Message}");
            throw;
        }

        AssertSame(what, "numRows", expected["numRows"], JsonValue.Create(footer.NumRows));
        JsonNode? rowGroups = expected["rowGroups"];
        AssertSame(what, "rowGroups", rowGroups is JsonArray groups ? JsonValue.Create(groups.Count) : rowGroups, JsonValue.Create(footer.RowGroups.Count));
        AssertSame(what, "createdBy", expected["createdBy"], footer.CreatedBy == null ? null : JsonValue.Create(footer.CreatedBy));
        var leaves = new JsonArray();
        foreach (ParquetLeafColumn leaf in footer.Leaves)
        {
            leaves.Add(new JsonObject
            {
                ["path"] = new JsonArray(leaf.Path.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
                ["physicalType"] = leaf.PhysicalType,
                ["typeAnnotation"] = leaf.TypeAnnotation,
                ["repetition"] = leaf.Repetition,
            });
        }

        AssertSame(what, "leaves", Only(expected["leaves"], "path", "physicalType", "typeAnnotation", "repetition"), leaves);

        // The columns of a folder of Parquet files that holds a copy of the file alone.
        using var lake = new TempLake();
        lake.Write("data/kit/part-00000.parquet", File.ReadAllBytes(Path.Combine(KitFolder, file!)));
        using PqaxDocument doc = await FolderFootprint.CollectAsync(lake, "data/kit", new CollectorOptions { IncludeDataValues = includeDataValues });
        TableRow table = Assert.Single(doc.Summary.Tables);
        List<PhysicalColumnRow> physical = FolderFootprint.PhysicalColumnsOf(doc, table);
        var columns = new JsonArray();
        foreach (ColumnRow column in FolderFootprint.ColumnsOf(doc, table))
        {
            columns.Add(new JsonObject
            {
                ["name"] = column.Name,
                ["dataType"] = column.DataType,
                ["isNullable"] = column.IsNullable,
                ["leaves"] = new JsonArray(physical.Where(p => p.Column == column.Id).Select(p => p.Ordinal!.Value).Order().Select(ordinal => (JsonNode?)JsonValue.Create(ordinal)).ToArray()),
            });
        }

        AssertSame(what, "columns", Sorted(Only(expected["columns"], "name", "dataType", "isNullable", "leaves")), columns);
    }

    /// <summary>The members of a list of objects that the kit compares, so that a member of the kit for people does not count.</summary>
    private static JsonNode? Only(JsonNode? list, params string[] members)
    {
        if (list is not JsonArray array)
        {
            return list?.DeepClone();
        }

        var result = new JsonArray();
        foreach (JsonNode? item in array)
        {
            if (item is JsonObject obj)
            {
                var kept = new JsonObject();
                foreach (string member in members)
                {
                    kept[member] = obj[member]?.DeepClone();
                }

                result.Add(kept);
            }
            else
            {
                result.Add(item?.DeepClone());
            }
        }

        return result;
    }

    /// <summary>The ordinals of the leaves of each column, in ascending order.</summary>
    private static JsonNode? Sorted(JsonNode? columns)
    {
        if (columns is JsonArray array)
        {
            foreach (JsonNode? column in array)
            {
                if (column is JsonObject obj && obj["leaves"] is JsonArray leaves)
                {
                    obj["leaves"] = new JsonArray(leaves.Select(leaf => leaf?.GetValue<long>()).Order().Select(leaf => (JsonNode?)JsonValue.Create(leaf)).ToArray());
                }
            }
        }

        return columns;
    }

    private static void AssertSame(string what, string member, JsonNode? expected, JsonNode? actual)
    {
        string want = Fixture.Canonical(expected);
        string got = Fixture.Canonical(actual);
        Assert.True(want == got, $"{what}, {member}:\n  expected {want}\n  actual   {got}");
    }
}
