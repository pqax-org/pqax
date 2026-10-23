using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pqax.Cli.Tests;

/// <summary>
/// <c>pqax diff</c>: the JSON of the kit against <c>conformance/expected/diff/diff-before.diff-after.json</c>, value for
/// value, key for key, and number for number as Python writes them; the text report of the kit line by line, the same
/// lines as <c>tests/python/test_cli_diff.py</c>; --sort, --top, a .vpax that embeds a footprint, the warnings, and the
/// exit codes. That the C# and the Python outputs are the same bytes is left to <c>conformance/cross_check.py</c>.
/// </summary>
public class DiffCommandTests
{
    private static string Before => Path.Combine(CliKit.Samples, "diff-before.pqax");

    private static string After => Path.Combine(CliKit.Samples, "diff-after.pqax");

    private static JsonObject ExpectedDiff() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(CliKit.ExpectedFolder, "diff", "diff-before.diff-after.json")))!.AsObject();

    private static (int Exit, string Output, string Error) Diff(params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = Program.Run(["diff", .. arguments], output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static JsonObject Json(params string[] arguments)
    {
        (int exit, string output, string error) = Diff([.. arguments, "--json"]);
        Assert.True(exit == 0, $"exit code {exit}: {output}{error}");
        return JsonNode.Parse(output)!.AsObject();
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    /// <summary>
    /// The differences between two parsed documents: objects with the same keys in the same order, arrays item by item,
    /// numbers with the same text, as Python writes them (an integer without a decimal point, any other number as the
    /// shortest repr), and the rest as JSON text.
    /// </summary>
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
                string[] expectedKeys = expectedObject.Select(p => p.Key).ToArray();
                string[] actualKeys = actualObject.Select(p => p.Key).ToArray();
                if (!expectedKeys.SequenceEqual(actualKeys))
                {
                    differences.Add($"{path}: keys {string.Join(", ", actualKeys)}, expected {string.Join(", ", expectedKeys)}");
                    return;
                }

                foreach (string key in expectedKeys)
                {
                    Compare(actualObject[key], expectedObject[key], path + "." + key, differences);
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

        if (expected.GetValueKind() == JsonValueKind.Number && actual.GetValueKind() == JsonValueKind.Number &&
            actual.GetValue<double>() != expected.GetValue<double>())
        {
            differences.Add($"{path}: got {actual.ToJsonString()}, expected {expected.ToJsonString()}");
        }
        else if (actual.ToJsonString() != expected.ToJsonString())
        {
            differences.Add($"{path}: got {actual.ToJsonString()}, expected {expected.ToJsonString()} (the same value, written differently)");
        }
    }

    private static void AssertSame(JsonNode actual, JsonNode expected)
    {
        var differences = new List<string>();
        Compare(actual, expected, "$", differences);
        Assert.True(differences.Count == 0, string.Join("\n", differences.Take(40)));
    }

    /// <summary>Rows of cells in columns two spaces apart, the first <paramref name="left"/> left-aligned and the others right-aligned, without trailing spaces.</summary>
    private static IEnumerable<string> Layout(IReadOnlyList<string[]> rows, int left)
    {
        int[] widths = Enumerable.Range(0, rows[0].Length).Select(i => rows.Max(row => row[i].Length)).ToArray();
        return rows.Select(row => string.Join("  ", row.Select((cell, i) => i < left ? cell.PadRight(widths[i]) : cell.PadLeft(widths[i]))).TrimEnd());
    }

    // ---- the text report of the kit, from the interface of 24 October 2026 ----

    /// <summary>The interface writes "&lt;n&gt; table(s)": "table" for one, "tables" otherwise, as pqax tables writes its first line.</summary>
    private const string TablesWord = "tables";

    private static readonly string[] Header = ["Change", "Table", "Total before", "Total after", "Total change", "Waste change", "Files change", "Rows change", "Files added", "Files removed"];

    private static readonly string[] ColumnsHeader = ["Table", "Column", "Change", "Detail"];

    /// <summary>One row per change of the kit, by kind and label, the numbers written as pqax tables writes bytes.</summary>
    private static readonly IReadOnlyDictionary<(string, string), string[]> Rows = new Dictionary<(string, string), string[]>
    {
        [("removed", "legacy")] = ["removed", "legacy", "177 KB", "-", "-177 KB", "0 B", "-2", "-15,000", "-", "-"],
        [("removed", "products")] = ["replaced", "products", "108 KB", "-", "-108 KB", "-29.3 KB", "-3", "-6,000", "-", "-"],
        [("kept", "orders")] = ["changed", "orders", "138 KB", "72.8 KB", "-64.9 KB", "-66.4 KB", "-3", "0", "1", "4"],
        [("kept", "sales")] = ["changed", "sales", "122 KB", "166 KB", "+44.4 KB", "+18.6 KB", "+1", "+3,000", "2", "1"],
        [("added", "events")] = ["added", "events", "-", "27.3 KB", "+27.3 KB", "0 B", "+2", "+5,000", "-", "-"],
        [("added", "products")] = ["recreated", "products", "-", "21.5 KB", "+21.5 KB", "0 B", "+1", "+2,000", "-", "-"],
        [("kept", "raw.staging")] = ["changed", "raw.staging", "9.96 KB", "15.2 KB", "+5.27 KB", "0 B", "+1", "+540", "1", "0"],
        [("kept", "audit")] = ["unchanged", "audit", "17.6 KB", "17.6 KB", "0 B", "0 B", "0", "0", "n/a", "n/a"],
        [("kept", "clients")] = ["renamed", "clients (was customers)", "62.5 KB", "62.5 KB", "0 B", "0 B", "0", "0", "n/a", "n/a"],
    };

    /// <summary>The columns of each kept table that changed: added, then removed, then the kept ones that changed.</summary>
    private static readonly IReadOnlyDictionary<string, string[][]> ColumnRows = new Dictionary<string, string[][]>
    {
        ["orders"] =
        [
            ["orders", "order_id", "changed", "size -1,000 B"],
            ["orders", "qty", "changed", "size -500 B"],
            ["orders", "price", "changed", "type int to bigint; size +1,000 B"],
            ["orders", "customer", "changed", "size -1,000 B"],
        ],
        ["sales"] =
        [
            ["sales", "discount", "added", "double"],
            ["sales", "region", "added", "string"],
            ["sales", "discount", "removed", "double"],
            ["sales", "note", "removed", "string"],
            ["sales", "id", "changed", "size +8.30 KB"],
            ["sales", "amount", "renamed", "was amt; size +6.84 KB"],
        ],
        ["raw.staging"] =
        [
            ["raw.staging", "source_file", "added", "string"],
            ["raw.staging", "batch_id", "changed", "size +1.17 KB"],
            ["raw.staging", "payload", "changed", "size +2.25 KB"],
        ],
    };

    private static readonly IReadOnlyDictionary<string, (string Kind, string Label)[]> Order = new Dictionary<string, (string, string)[]>
    {
        ["total"] = [("removed", "legacy"), ("removed", "products"), ("kept", "orders"), ("kept", "sales"), ("added", "events"), ("added", "products"), ("kept", "raw.staging"), ("kept", "audit"), ("kept", "clients")],
        ["waste"] = [("kept", "orders"), ("removed", "products"), ("kept", "sales"), ("kept", "audit"), ("kept", "clients"), ("added", "events"), ("removed", "legacy"), ("added", "products"), ("kept", "raw.staging")],
        ["rows"] = [("removed", "legacy"), ("removed", "products"), ("added", "events"), ("kept", "sales"), ("added", "products"), ("kept", "raw.staging"), ("kept", "audit"), ("kept", "clients"), ("kept", "orders")],
        ["files"] = [("kept", "orders"), ("removed", "products"), ("added", "events"), ("removed", "legacy"), ("added", "products"), ("kept", "raw.staging"), ("kept", "sales"), ("kept", "audit"), ("kept", "clients")],
        ["name"] = [("kept", "audit"), ("kept", "clients"), ("added", "events"), ("removed", "legacy"), ("kept", "orders"), ("added", "products"), ("removed", "products"), ("kept", "raw.staging"), ("kept", "sales")],
    };

    private static (string Kind, string Label)[] Shown(string sort, int? top) => top is int n ? Order[sort].Take(n).ToArray() : Order[sort];

    private static string[] ExpectedReport(string sort = "total", int? top = null)
    {
        (string Kind, string Label)[] shown = Shown(sort, top);
        var text = new List<string>
        {
            $"Older: diff-before.pqax, 7 {TablesWord}, collected 2026-10-01T06:00:00.000Z",
            $"Newer: diff-after.pqax, 7 {TablesWord}, collected 2026-10-08T06:00:00.000Z",
            $"Tables: 2 added, 2 removed, 5 kept, 4 changed; by {sort}" + (top is int n ? $", the first {n}" : ""),
            "Footprint: 634 KB to 383 KB, -252 KB; waste: 136 KB to 58.6 KB, -77.1 KB",
            "",
        };
        text.AddRange(Layout([Header, .. shown.Select(change => Rows[change])], 2));
        string[][] columns = shown.Where(change => change.Kind == "kept")
            .SelectMany(change => ColumnRows.TryGetValue(change.Label, out string[][]? rows) ? rows : []).ToArray();
        if (columns.Length > 0)
        {
            text.Add("");
            text.AddRange(Layout([ColumnsHeader, .. columns], 4));
        }

        return [.. text];
    }

    /// <summary>The labels of each list of the JSON, in its order.</summary>
    private static (string[] Added, string[] Removed, string[] Kept) Labels(JsonObject json) =>
        (json["added"]!.AsArray().Select(e => (string)e!["label"]!).ToArray(),
         json["removed"]!.AsArray().Select(e => (string)e!["label"]!).ToArray(),
         json["kept"]!.AsArray().Select(e => (string)e!["newer"]!["label"]!).ToArray());

    private static (string[] Added, string[] Removed, string[] Kept) ExpectedLabels(string sort, int? top = null)
    {
        (string Kind, string Label)[] shown = Shown(sort, top);
        string[] Of(string kind) => shown.Where(c => c.Kind == kind).Select(c => c.Label).ToArray();
        return (Of("added"), Of("removed"), Of("kept"));
    }

    private static void AssertLabels((string[] Added, string[] Removed, string[] Kept) expected, (string[] Added, string[] Removed, string[] Kept) actual)
    {
        Assert.Equal(expected.Added, actual.Added);
        Assert.Equal(expected.Removed, actual.Removed);
        Assert.Equal(expected.Kept, actual.Kept);
    }

    private static void AssertCounts(JsonObject json, int added, int removed, int kept, int changed) =>
        Assert.Equal((added, removed, kept, changed), ((int)json["counts"]!["added"]!, (int)json["counts"]!["removed"]!, (int)json["counts"]!["kept"]!, (int)json["counts"]!["changed"]!));

    // ---- the kit ----

    [Fact]
    public void The_json_of_the_kit_is_the_expected_diff()
    {
        AssertSame(Json(Before, After), ExpectedDiff());
    }

    [Fact]
    public void The_json_is_indented_as_python_writes_it()
    {
        (int exit, string output, string error) = Diff(Before, After, "--json");
        Assert.True(exit == 0, output + error);
        string text = output.Replace("\r\n", "\n");
        Assert.StartsWith("{\n  \"older\": {\n    \"file\": \"diff-before.pqax\",\n", text, StringComparison.Ordinal);
        Assert.Contains("\"smallFileSize\": 134217728,", text, StringComparison.Ordinal);
        Assert.Contains("\"delta\": -257600", text, StringComparison.Ordinal);
        Assert.Contains("\"warnings\": []", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".0,", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".0\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_report_of_the_kit()
    {
        (int exit, string output, string error) = Diff(Before, After);
        Assert.True(exit == 0, output + error);
        Assert.Equal(ExpectedReport(), Lines(output));
    }

    [Fact]
    public void The_rows_of_the_text_report_of_the_kit()
    {
        (int exit, string output, _) = Diff(Before, After);
        Assert.Equal(0, exit);
        string[] text = Lines(output);
        string[] Words(int line) => text[line].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"Older: diff-before.pqax, 7 {TablesWord}, collected 2026-10-01T06:00:00.000Z", text[0]);
        Assert.Equal("Tables: 2 added, 2 removed, 5 kept, 4 changed; by total", text[2]);
        Assert.Equal("Footprint: 634 KB to 383 KB, -252 KB; waste: 136 KB to 58.6 KB, -77.1 KB", text[3]);
        Assert.Equal("", text[4]);
        Assert.Equal(["removed", "legacy", "177", "KB", "-", "-177", "KB", "0", "B", "-2", "-15,000", "-", "-"], Words(6));
        Assert.Equal(["replaced", "products"], Words(7)[..2]);
        Assert.Equal(["changed", "sales", "122", "KB", "166", "KB", "+44.4", "KB", "+18.6", "KB", "+1", "+3,000", "2", "1"], Words(9));
        Assert.Equal(["recreated", "products"], Words(11)[..2]);
        Assert.Equal(["n/a", "n/a"], Words(13)[^2..]);
        Assert.StartsWith("renamed    clients (was customers)  ", text[14], StringComparison.Ordinal);
        Assert.Equal("", text[15]);
        Assert.Equal(["Table", "Column", "Change", "Detail"], Words(16));
        Assert.Equal(["orders", "price", "changed", "type", "int", "to", "bigint;", "size", "+1,000", "B"], Words(19));
        Assert.Equal(["sales", "amount", "renamed", "was", "amt;", "size", "+6.84", "KB"], Words(26));
        Assert.Equal(30, text.Length);
    }

    public static TheoryData<string> Sorts => CliKit.Data(["total", "waste", "rows", "files", "name"]);

    [Theory]
    [MemberData(nameof(Sorts))]
    public void Each_sort(string sort)
    {
        JsonObject json = Json(Before, After, "--sort", sort);
        Assert.Equal(sort, (string)json["sort"]!);
        Assert.Null(json["top"]);
        Assert.True(json.ContainsKey("top"));
        AssertLabels(ExpectedLabels(sort), Labels(json));
        AssertCounts(json, 2, 2, 5, 4);
        (int exit, string output, string error) = Diff(Before, After, "--sort", sort);
        Assert.True(exit == 0, output + error);
        Assert.Equal(ExpectedReport(sort), Lines(output));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(100)]
    public void Top_keeps_the_first_changes_and_the_counts_of_all(int top)
    {
        string n = top.ToString(CultureInfo.InvariantCulture);
        JsonObject json = Json(Before, After, "--top", n);
        Assert.Equal(top, (int)json["top"]!);
        AssertCounts(json, 2, 2, 5, 4);
        AssertLabels(ExpectedLabels("total", top), Labels(json));
        (int exit, string output, string error) = Diff(Before, After, "--top", n);
        Assert.True(exit == 0, output + error);
        Assert.Equal(ExpectedReport("total", top), Lines(output));
    }

    [Fact]
    public void Top_2_shows_no_kept_table_and_no_columns_table()
    {
        (int exit, string output, _) = Diff(Before, After, "--top", "2");
        Assert.Equal(0, exit);
        Assert.Equal(8, Lines(output).Length);
        Assert.DoesNotContain("Column", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Top_with_another_sort()
    {
        AssertLabels((["events"], [], ["audit", "clients"]), Labels(Json(Before, After, "--top", "3", "--sort", "name")));
    }

    [Fact]
    public void A_vpax_gives_its_embedded_footprint_and_the_same_footprint_has_no_change()
    {
        string vpax = Path.Combine(CliKit.Samples, "embedded-stored.vpax");
        string sample = Path.Combine(CliKit.Samples, "sample.pqax");
        JsonObject json = Json(vpax, sample);
        Assert.Equal(("embedded-stored.vpax", "Footprint.pqax"), ((string)json["older"]!["file"]!, (string?)json["older"]!["embeddedPart"]));
        Assert.Equal(("sample.pqax", (string?)null), ((string)json["newer"]!["file"]!, (string?)json["newer"]!["embeddedPart"]));
        AssertCounts(json, 0, 0, 2, 0);
        Assert.All(json["kept"]!.AsArray(), kept =>
        {
            Assert.Equal("unchanged", (string)kept!["status"]!);
            Assert.False((bool)kept["renamed"]!);
        });
        (int exit, string output, string error) = Diff(vpax, sample);
        Assert.True(exit == 0, output + error);
        Assert.Equal("Tables: 0 added, 0 removed, 2 kept, 0 changed; by total", Lines(output)[2]);
        Assert.DoesNotContain("Column", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Part_applies_to_both_files()
    {
        JsonObject json = Json(Path.Combine(CliKit.Samples, "embedded-stored.vpax"), Path.Combine(CliKit.Samples, "embedded-deflated.vpax"), "--part", "Footprint.pqax");
        Assert.Equal("Footprint.pqax", (string?)json["older"]!["embeddedPart"]);
        Assert.Equal("Footprint.pqax", (string?)json["newer"]!["embeddedPart"]);
        AssertCounts(json, 0, 0, 2, 0);
    }

    [Fact]
    public void A_footprint_against_itself()
    {
        AssertCounts(Json(After, After), 0, 0, 7, 0);
    }

    [Fact]
    public void DiffCommand_runs_with_diff_first_in_the_arguments()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = DiffCommand.Run(["diff", Before, After, "--json"], output, error);
        Assert.True(exit == 0, output.ToString() + error);
        AssertSame(JsonNode.Parse(output.ToString())!, ExpectedDiff());
    }

    [Fact]
    public void The_output_does_not_depend_on_the_culture()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo ui = CultureInfo.CurrentUICulture;
        string[] Outputs() => [Diff(Before, After).Output, Diff(Before, After, "--json").Output];
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            string[] english = Outputs();
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(english, Outputs());
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(english, Outputs());
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }
    }

    [Fact]
    public void The_warnings_of_the_measures_go_to_the_error_output()
    {
        string path = Path.Combine(Path.GetTempPath(), "pqax-diff-" + Guid.NewGuid().ToString("N"), "broken.pqax");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (ZipArchive source = ZipFile.OpenRead(After))
            using (ZipArchive target = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (ZipArchiveEntry entry in source.Entries)
                {
                    using Stream to = target.CreateEntry(entry.FullName).Open();
                    if (entry.FullName == "files.json")
                    {
                        to.Write(Encoding.UTF8.GetBytes("{\"files\": {\"id\": [1, 2,"));
                    }
                    else
                    {
                        using Stream from = entry.Open();
                        from.CopyTo(to);
                    }
                }
            }

            (int exit, string output, string error) = Diff(Before, path, "--json");
            Assert.True(exit == 0, $"exit code {exit}: {output}{error}");
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.All(Lines(error), line => Assert.StartsWith("pqax diff: broken.pqax: ", line, StringComparison.Ordinal));
            JsonObject json = JsonNode.Parse(output)!.AsObject();
            Assert.False((bool)json["newer"]!["filesRead"]!);
            Assert.True((bool)json["older"]!["filesRead"]!);
            Assert.NotEmpty(json["newer"]!["warnings"]!.AsArray());
            Assert.All(json["kept"]!.AsArray(), kept => Assert.Null(kept!["files"]));

            (exit, _, error) = Diff(Before, path);
            Assert.Equal(0, exit);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void The_command_runs_in_a_process_of_its_own()
    {
        (int exit, string output, string error) = CliKit.Run("diff", Before, After, "--json");
        Assert.True(exit == 0, output + error);
        AssertSame(JsonNode.Parse(output)!, ExpectedDiff());
        (exit, output, error) = CliKit.Run("diff", Before, After);
        Assert.True(exit == 0, output + error);
        Assert.Equal(ExpectedReport(), Lines(output));
    }

    // ---- exit codes ----

    public static TheoryData<string[]> WrongCommandLines => new()
    {
        new string[0],
        new[] { "{before}" },
        new[] { "{before}", "{after}", "{after}" },
        new[] { "--json" },
        new[] { "{before}", "{after}", "--sort", "size" },
        new[] { "{before}", "{after}", "--sort" },
        new[] { "{before}", "{after}", "--top", "0" },
        new[] { "{before}", "{after}", "--top", "-1" },
        new[] { "{before}", "{after}", "--top", "x" },
        new[] { "{before}", "{after}", "--top", "1.5" },
        new[] { "{before}", "{after}", "--top" },
        new[] { "{before}", "{after}", "--no-such-option" },
    };

    [Theory]
    [MemberData(nameof(WrongCommandLines))]
    public void A_wrong_command_line_exits_with_2_with_the_problem_and_the_usage(string[] arguments)
    {
        string[] resolved = arguments.Select(a => a == "{before}" ? Before : a == "{after}" ? After : a).ToArray();
        (int exit, string output, string error) = Diff(resolved);
        Assert.True(exit == 2, $"exit code {exit}: {output}{error}");
        Assert.Contains("pqax diff: ", error, StringComparison.Ordinal);
        Assert.Contains("usage", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("older")]
    [InlineData("newer")]
    public void A_file_that_does_not_exist_exits_with_2(string missing)
    {
        string absent = Path.Combine(CliKit.Samples, "no-such-file.pqax");
        (int exit, _, string error) = missing == "older" ? Diff(absent, After) : Diff(Before, absent);
        Assert.Equal(2, exit);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("manifest.format.pqax", "older")]
    [InlineData("manifest.format.pqax", "newer")]
    [InlineData("manifest.versionUnknown.pqax", "older")]
    [InlineData("manifest.versionUnknown.pqax", "newer")]
    [InlineData("container.manifestMissing.pqax", "older")]
    [InlineData("container.manifestMissing.pqax", "newer")]
    public void A_file_that_the_reader_refuses_exits_with_1(string file, string side)
    {
        string invalid = Path.Combine(CliKit.Invalid, file);
        Assert.Throws<PqaxFormatException>(() => PqaxDocument.Open(invalid).Dispose());
        (int exit, string output, string error) = side == "older" ? Diff(invalid, After) : Diff(Before, invalid);
        Assert.True(exit == 1, $"exit code {exit}: {output}{error}");
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
