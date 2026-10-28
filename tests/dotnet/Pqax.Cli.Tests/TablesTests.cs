using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pqax.Cli.Tests;

/// <summary>
/// <c>pqax tables</c>: the JSON against <c>conformance/expected/measures</c>, the sort and its ties, --top, the text table
/// with its aligned columns and the numbers written as the views write them, --wide, and the exit codes.
/// </summary>
public class TablesTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly string[] Columns = ["Table", "Rows", "Files", "Live", "Non-live", "Unreferenced", "Deleted", "Stale", "Waste", "Waste share"];

    private static readonly string[] WideColumns = ["Under 8 MiB", "Under 128 MiB", "Metadata", "Footers"];

    public static TheoryData<string> Samples => CliKit.Data(CliKit.ExpectedNames());

    public static TheoryData<string> Sorts => CliKit.Data(["waste", "total", "rows", "files", "name"]);

    private static string SamplePath(string name) => Path.Combine(CliKit.Samples, (string)CliKit.Expected(name)["file"]!);

    private static JsonObject ExpectedMeasures(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(CliKit.ExpectedFolder, "measures", name + ".json")))!.AsObject();

    private static (int Exit, string Output, string Error) Tables(params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = Program.Run(["tables", .. arguments], output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static JsonObject Json(params string[] arguments)
    {
        (int exit, string output, string error) = Tables([.. arguments, "--json"]);
        Assert.True(exit == 0, $"exit code {exit}: {output}{error}");
        return JsonNode.Parse(output)!.AsObject();
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    // ---- the numbers as the views write them, with the locale en-US ----

    /// <summary>Rounds as Intl.NumberFormat does: the shortest decimal of the double, then half away from zero.</summary>
    private static string Fixed(double value, int digits)
    {
        decimal exact = decimal.Parse(value.ToString("R", Invariant), NumberStyles.Float, Invariant);
        return Math.Round(exact, digits, MidpointRounding.AwayFromZero).ToString("N" + digits, Invariant);
    }

    public static string Bytes(double? value)
    {
        if (value is not double number)
        {
            return "n/a";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double scaled = Math.Abs(number);
        int unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        int digits = unit == 0 ? 0 : scaled < 10 ? 2 : scaled < 100 ? 1 : 0;
        return (number < 0 ? "-" : "") + Fixed(scaled, digits) + " " + units[unit];
    }

    public static string Count(double? value) =>
        value is double number ? Math.Round(number, MidpointRounding.AwayFromZero).ToString("N0", Invariant) : "n/a";

    public static string Share(double? value) =>
        value is not double number ? "n/a" : number > 0 && number < 0.0005 ? "<0.1%" : Fixed(number * 100, 1) + "%";

    private static double? D(JsonNode? node) => node is null ? null : node.GetValue<double>();

    /// <summary>
    /// The cell of a share of small files: a lower bound from the quartiles with ">=" when it is strictly between 0 and
    /// 1; an exact share from the files, or a bound of 0 or 1, which is exact, without.
    /// </summary>
    private static string SmallShare(JsonNode files)
    {
        if (files["shareAtLeast"] is null)
        {
            return "n/a";
        }

        double share = D(files["shareAtLeast"])!.Value;
        return (files["count"] is null && share > 0 && share < 1 ? ">=" : "") + Share(share);
    }

    /// <summary>The cells of a row of the text table, from a table of the JSON.</summary>
    private static string[] Cells(JsonObject table, bool wide)
    {
        JsonNode stack = table["stack"]!;
        var cells = new List<string>
        {
            (string)table["label"]!,
            Count(D(table["rowCount"])),
            Count(D(table["fileCount"])),
            Bytes(D(stack["live"])),
            Bytes(D(stack["nonLive"])),
            Bytes(D(stack["unreferenced"])),
            Bytes(D(stack["deleted"])),
            Bytes(D(stack["stale"])),
            Bytes(D(stack["waste"])),
            Share(D(stack["wasteShare"])),
        };
        if (wide)
        {
            cells.Add(SmallShare(table["tinyFiles"]!));
            cells.Add(SmallShare(table["smallFiles"]!));
            cells.Add(Bytes(D(stack["metadata"])));
            cells.Add(Bytes(D(stack["footers"])));
        }

        return [.. cells];
    }

    /// <summary>
    /// Checks the text table against the JSON of the same run: the first line, the header, and each row with its columns
    /// aligned: the label left-aligned, every number right-aligned, two spaces between the columns.
    /// </summary>
    private static void AssertTextTable(string path, string text, JsonObject json, bool wide, string sort, int? top)
    {
        string[] lines = Lines(text);
        string first = $"{Path.GetFileName(path)}: {(int)json["tableCount"]!} tables, by {sort}" + (top is int n ? $", the first {n}" : "");
        Assert.Equal(first, lines[0]);
        string[] header = [.. Columns, .. wide ? WideColumns : []];
        JsonArray tables = json["tables"]!.AsArray();
        Assert.Equal(2 + tables.Count, lines.Length);
        Assert.Equal(header, Regex.Split(lines[1].Trim(), " {2,}"));

        string[][] rows = tables.Select(table => Cells(table!.AsObject(), wide)).ToArray();
        int[] widths = header.Select((name, i) => Math.Max(name.Length, rows.Length == 0 ? 0 : rows.Max(row => row[i].Length))).ToArray();
        var starts = new int[header.Length];
        for (int i = 1; i < header.Length; i++)
        {
            starts[i] = starts[i - 1] + widths[i - 1] + 2;
        }

        for (int r = 0; r < rows.Length; r++)
        {
            string expected = string.Join("  ", rows[r].Select((cell, i) => i == 0 ? cell.PadRight(widths[i]) : cell.PadLeft(widths[i])));
            Assert.Equal(expected.TrimEnd(), lines[2 + r].TrimEnd());
        }

        // Each name of the header lies inside its column.
        int position = 0;
        for (int i = 0; i < header.Length; i++)
        {
            int at = lines[1].IndexOf(header[i], position, StringComparison.Ordinal);
            Assert.True(at >= starts[i] && at + header[i].Length <= starts[i] + widths[i], $"{header[i]} is outside its column in:\n{text}");
            position = at + header[i].Length;
        }
    }

    // ---- the sort of the interface, on the expected tables ----

    private static List<JsonObject> Sorted(IEnumerable<JsonObject> tables, string sort)
    {
        var list = tables.Select((table, row) => (Table: table, Row: row)).ToList();
        double Total((JsonObject Table, int Row) t) => (double)t.Table["stack"]!["total"]!;
        string Label((JsonObject Table, int Row) t) => (string)t.Table["label"]!;
        int Ties((JsonObject Table, int Row) a, (JsonObject Table, int Row) b) =>
            Total(b).CompareTo(Total(a)) is int c and not 0 ? c
            : string.CompareOrdinal(Label(a), Label(b)) is int l and not 0 ? l
            : a.Row.CompareTo(b.Row);
        int Nullable(double? a, double? b) => (a, b) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => b!.Value.CompareTo(a!.Value),
        };
        Comparison<(JsonObject Table, int Row)> compare = sort switch
        {
            "waste" => (a, b) => ((double)b.Table["stack"]!["waste"]!).CompareTo((double)a.Table["stack"]!["waste"]!) is int c and not 0 ? c : Ties(a, b),
            "total" => Ties,
            "rows" => (a, b) => Nullable(D(a.Table["rowCount"]), D(b.Table["rowCount"])) is int c and not 0 ? c : Ties(a, b),
            "files" => (a, b) => Nullable(D(a.Table["fileCount"]), D(b.Table["fileCount"])) is int c and not 0 ? c : Ties(a, b),
            _ => (a, b) => string.CompareOrdinal(Label(a), Label(b)) is int c and not 0 ? c : a.Row.CompareTo(b.Row),
        };
        list.Sort(compare);
        return list.Select(t => t.Table).ToList();
    }

    // The keys of a table that pqax tables --json prints: those of the expected measures less stack.shares, files,
    // partitions, and the keys of maintenance after the first three, which the kit gained with the other measures of
    // section 10 and the command does not print.
    private static readonly string[] CliTableKeys = ["id", "name", "label", "rowCount", "fileCount", "fileSize", "stack", "smallFiles", "tinyFiles", "measures", "maintenance"];

    private static readonly string[] CliStackKeys = ["live", "nonLive", "unreferenced", "deleted", "stale", "metadata", "footers", "deletedSource", "listed", "data", "total", "waste", "overhead", "wasteShare", "overheadShare"];

    private static readonly string[] CliMaintenanceKeys = ["lastCommitAt", "commitsPerDay", "daysSinceLastCommit"];

    private static JsonObject Only(JsonNode source, string[] keys) => new(keys.Select(key => KeyValuePair.Create(key, source[key]?.DeepClone())));

    /// <summary>An expected table restricted to the keys that the command prints.</summary>
    private static JsonObject CliShape(JsonObject expected)
    {
        JsonObject table = Only(expected, CliTableKeys);
        table["stack"] = Only(expected["stack"]!, CliStackKeys);
        table["maintenance"] = Only(expected["maintenance"]!, CliMaintenanceKeys);
        return table;
    }

    /// <summary>The expected tables of a sample, in the order of a sort, restricted to the keys that the command prints.</summary>
    private static JsonArray CliTables(JsonObject expected, string sort) =>
        new(Sorted(expected["tables"]!.AsArray().Select(t => t!.AsObject()), sort).Select(t => (JsonNode?)CliShape(t)).ToArray());

    /// <summary>The differences between two tables of the JSON: numbers as doubles, exactly, lastCommitAt as an instant.</summary>
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

        if (expected is JsonObject expectedObject)
        {
            JsonObject actualObject = actual.AsObject();
            foreach (string key in expectedObject.Select(pair => pair.Key).Union(actualObject.Select(pair => pair.Key)))
            {
                if (!expectedObject.ContainsKey(key) || !actualObject.ContainsKey(key))
                {
                    differences.Add($"{path}.{key}: present in only one of the two");
                    continue;
                }

                Compare(actualObject[key], expectedObject[key], path + "." + key, differences);
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

        if (path.EndsWith(".lastCommitAt", StringComparison.Ordinal) && expected.GetValueKind() == JsonValueKind.String && actual.GetValueKind() == JsonValueKind.String)
        {
            if (DateTimeOffset.Parse((string)actual!, Invariant) != DateTimeOffset.Parse((string)expected!, Invariant))
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

    // ---- the kit ----

    [Theory]
    [MemberData(nameof(Samples))]
    public void The_json_has_the_header_and_the_tables_of_the_expected_measures(string name)
    {
        string path = SamplePath(name);
        JsonObject json = Json(path);
        JsonObject expected = ExpectedMeasures(name);
        JsonObject sample = CliKit.Expected(name);

        Assert.Equal(Path.GetFileName(path), (string)json["file"]!);
        Assert.Equal((string?)sample["embeddedPart"], (string?)json["embeddedPart"]);
        Assert.Equal((string)sample["format"]!, (string)json["format"]!);
        Assert.Equal((string)sample["formatVersion"]!, (string)json["formatVersion"]!);
        DateTimeOffset? completedAt;
        using (PqaxDocument doc = sample["embeddedPart"] is null ? PqaxDocument.Open(path) : PqaxDocument.OpenEmbedded(path))
        {
            Assert.Equal("pqax-conformance-kit", doc.Manifest.Collection.CollectorApp);
            Assert.Equal("pqax-conformance-kit " + doc.Manifest.Collection.CollectorAppVersion, (string?)json["collector"]);
            completedAt = doc.Manifest.Collection.CompletedAt;
        }

        Assert.Null(json["library"]);
        Assert.Equal(completedAt, DateTimeOffset.Parse((string)json["collectedAt"]!, Invariant));
        Assert.Equal(2, (int)json["level"]!);
        Assert.Equal(134217728L, (long)json["smallFileSize"]!);
        Assert.Equal(8388608L, (long)json["tinyFileSize"]!);
        Assert.Equal("waste", (string)json["sort"]!);
        Assert.Null(json["top"]);
        Assert.True(json.ContainsKey("top"));
        Assert.Equal(expected["tables"]!.AsArray().Count, (int)json["tableCount"]!);
        Assert.Equal((bool)expected["filesRead"]!, (bool)json["filesRead"]!);
        Assert.Equal(expected["warnings"]!.AsArray().Count, json["warnings"]!.AsArray().Count);

        var differences = new List<string>();
        Compare(json["tables"], CliTables(expected, "waste"), "tables", differences);
        Assert.True(differences.Count == 0, string.Join("\n", differences));

        // The command prints only the keys it printed before the other measures of section 10.
        Assert.False(json.ContainsKey("columns"));
        Assert.False(json.ContainsKey("collection"));
        Assert.All(json["tables"]!.AsArray(), table =>
        {
            Assert.Equal(CliTableKeys, table!.AsObject().Select(pair => pair.Key));
            Assert.Equal(CliStackKeys, table["stack"]!.AsObject().Select(pair => pair.Key));
            Assert.Equal(CliMaintenanceKeys, table["maintenance"]!.AsObject().Select(pair => pair.Key));
        });
    }

    [Fact]
    public void The_json_prints_only_the_keys_it_printed_before_on_a_footprint_with_every_measure()
    {
        var builder = new FootprintBuilder(new CollectionInfo { CollectorApp = "pqax-tests", CollectorAppVersion = "0.0", CompletedAt = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero), Level = 2 });
        long table = builder.AddTable(new TableRow { Name = "t", Format = "Delta", DetailDepth = "Files", FileCount = 2, FileSize = 300, PartitionCount = 1, VOrderedFileCount = 1 });
        builder.AddColumn(new ColumnRow { Table = table, Name = "c", DataType = "string", ValueCount = 10, NullCount = 1 });
        builder.AddOperation(new OperationRow { Table = table, Name = "VACUUM END", CommitCount = 1, LastAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) });
        builder.AddPartition(new PartitionRow { Table = table, LastModifiedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) });
        builder.AddFile(new FileRow { Table = table, FileSize = 100, IsVOrdered = true });
        builder.AddFile(new FileRow { Table = table, FileSize = 200, IsVOrdered = false });
        string path = Path.Combine(Path.GetTempPath(), "pqax-keys-" + Guid.NewGuid().ToString("N") + ".pqax");
        try
        {
            builder.Write(path);
            JsonObject json = Json(path);
            Assert.Equal(
                ["file", "embeddedPart", "format", "formatVersion", "collector", "library", "collectedAt", "level", "smallFileSize", "tinyFileSize", "sort", "top", "tableCount", "filesRead", "warnings", "tables"],
                json.Select(pair => pair.Key));
            JsonObject printed = Assert.Single(json["tables"]!.AsArray())!.AsObject();
            Assert.Equal(CliTableKeys, printed.Select(pair => pair.Key));
            Assert.Equal(CliStackKeys, printed["stack"]!.AsObject().Select(pair => pair.Key));
            Assert.Equal(CliMaintenanceKeys, printed["maintenance"]!.AsObject().Select(pair => pair.Key));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(Sorts))]
    public void Each_sort_orders_the_tables_of_a_sample(string sort)
    {
        JsonObject json = Json(SamplePath("sample"), "--sort", sort);
        Assert.Equal(sort, (string)json["sort"]!);
        string[] labels = json["tables"]!.AsArray().Select(table => (string)table!["label"]!).ToArray();
        string[] expected = sort == "name" ? ["dbo.orders", "sales"] : ["sales", "dbo.orders"];
        Assert.Equal(expected, labels);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void The_text_table_has_aligned_columns_and_the_numbers_of_the_json(string name)
    {
        string path = SamplePath(name);
        foreach (bool wide in new[] { false, true })
        {
            string[] options = wide ? ["--wide"] : [];
            (int exit, string text, string error) = Tables([path, .. options]);
            Assert.True(exit == 0, $"exit code {exit}: {text}{error}");
            AssertTextTable(path, text, Json([path, .. options]), wide, "waste", null);
        }
    }

    [Fact]
    public void The_text_table_of_a_sample_has_the_numbers_of_the_views()
    {
        (int exit, string text, string error) = Tables(SamplePath("sample"));
        Assert.True(exit == 0, text + error);
        string[] lines = Lines(text);
        Assert.Equal("sample.pqax: 2 tables, by waste", lines[0]);
        // Written by the views (formatCount, formatBytes, formatShare) from the expected measures.
        Assert.Equal(["sales", "17,000", "3", "151 KB", "737 B", "0 B", "239 B", "0 B", "976 B", "0.6%"], Regex.Split(lines[2].Trim(), " {2,}"));
        Assert.Equal(["dbo.orders", "4,100", "2", "37.7 KB", "0 B", "0 B", "0 B", "0 B", "0 B", "0.0%"], Regex.Split(lines[3].Trim(), " {2,}"));
    }

    [Fact]
    public void Wide_writes_an_exact_share_from_the_files_and_from_a_maximum_below_the_threshold()
    {
        (int exit, string text, _) = Tables(SamplePath("sample"), "--wide");
        Assert.Equal(0, exit);
        string[] row = Regex.Split(Lines(text)[2].Trim(), " {2,}");
        Assert.Equal(["100.0%", "100.0%", "12.2 KB", "3.76 KB"], row[^4..]);

        (exit, text, _) = Tables(SamplePath("summary-only"), "--wide");
        Assert.Equal(0, exit);
        row = Regex.Split(Lines(text)[2].Trim(), " {2,}");
        // Both bounds come from the maximum, below both thresholds: 1, which is exact, without ">=".
        Assert.Equal(["100.0%", "100.0%", "12.2 KB", "3.76 KB"], row[^4..]);
    }

    [Fact]
    public void A_format_0_1_sample_has_no_unreferenced_files()
    {
        (int exit, string text, _) = Tables(SamplePath("format-0.1"));
        Assert.Equal(0, exit);
        string[] row = Regex.Split(Lines(text)[2].Trim(), " {2,}");
        Assert.Equal("sales", row[0]);
        Assert.Equal("n/a", row[5]);
        Assert.Equal("12.1 KB", row[4]);
    }

    [Fact]
    public void A_vpax_gives_the_tables_of_its_embedded_footprint()
    {
        JsonObject json = Json(Path.Combine(CliKit.Samples, "embedded-deflated.vpax"));
        Assert.Equal("Footprint.pqax", (string?)json["embeddedPart"]);
        Assert.Equal("embedded-deflated.vpax", (string)json["file"]!);
        JsonObject sample = Json(SamplePath("sample"));
        var differences = new List<string>();
        Compare(json["tables"], sample["tables"], "tables", differences);
        Assert.Empty(differences);

        JsonObject named = Json(Path.Combine(CliKit.Samples, "embedded-stored.vpax"), "--part", "Footprint.pqax");
        Assert.Equal(2, (int)named["tableCount"]!);
    }

    [Fact]
    public void Top_keeps_the_first_tables_and_the_count_of_all()
    {
        string path = SamplePath("sample");
        JsonObject json = Json(path, "--top", "1");
        Assert.Equal(1, (int)json["top"]!);
        Assert.Equal(2, (int)json["tableCount"]!);
        Assert.Equal("sales", (string)Assert.Single(json["tables"]!.AsArray())!["label"]!);

        (int exit, string text, _) = Tables(path, "--top", "1", "--sort", "name");
        Assert.Equal(0, exit);
        AssertTextTable(path, text, Json(path, "--top", "1", "--sort", "name"), false, "name", 1);
        Assert.StartsWith("dbo.orders ", Lines(text)[2], StringComparison.Ordinal);

        Assert.Equal(2, Json(path, "--top", "5")["tables"]!.AsArray().Count);
    }

    [Fact]
    public void The_output_does_not_depend_on_the_culture()
    {
        string path = Path.Combine(CliKit.Samples, "sample.pqax");
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo ui = CultureInfo.CurrentUICulture;
        string[] Outputs() =>
        [
            Tables(path).Output, Tables(path, "--wide").Output, Tables(path, "--json").Output,
        ];
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
    public void The_command_runs_in_a_process_of_its_own()
    {
        (int exit, string text, string error) = CliKit.Run("tables", SamplePath("sample"), "--json");
        Assert.True(exit == 0, text + error);
        Assert.Equal(2, (int)JsonNode.Parse(text)!["tableCount"]!);
        (exit, text, error) = CliKit.Run("tables", SamplePath("sample"));
        Assert.True(exit == 0, text + error);
        Assert.StartsWith("sample.pqax: 2 tables, by waste", text, StringComparison.Ordinal);
    }

    // ---- exit codes ----

    public static TheoryData<string[]> WrongCommandLines => new()
    {
        new string[0],
        new[] { "--json" },
        new[] { "{sample}", "--sort", "size" },
        new[] { "{sample}", "--sort" },
        new[] { "{sample}", "--top", "0" },
        new[] { "{sample}", "--top", "-1" },
        new[] { "{sample}", "--top", "x" },
        new[] { "{sample}", "--top", "1.5" },
        new[] { "{sample}", "--top" },
        new[] { "{sample}", "--no-such-option" },
        new[] { "{sample}", "{sample}" },
    };

    [Theory]
    [MemberData(nameof(WrongCommandLines))]
    public void A_wrong_command_line_exits_with_2(string[] arguments)
    {
        string[] resolved = arguments.Select(argument => argument == "{sample}" ? SamplePath("sample") : argument).ToArray();
        (int exit, string text, string error) = Tables(resolved);
        Assert.True(exit == 2, $"exit code {exit}: {text}{error}");
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_file_that_does_not_exist_exits_with_2()
    {
        (int exit, _, string error) = Tables(Path.Combine(CliKit.Samples, "no-such-file.pqax"));
        Assert.Equal(2, exit);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("manifest.versionUnknown.pqax")]
    [InlineData("manifest.format.pqax")]
    [InlineData("container.manifestMissing.pqax")]
    public void A_file_that_the_reader_refuses_exits_with_1(string file)
    {
        Assert.Throws<PqaxFormatException>(() => PqaxDocument.Open(Path.Combine(CliKit.Invalid, file)).Dispose());
        (int exit, string text, string error) = Tables(Path.Combine(CliKit.Invalid, file));
        Assert.True(exit == 1, $"exit code {exit}: {text}{error}");
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ---- synthetic footprints ----

    /// <summary>A footprint of format 0.1 written in a temporary file: the manifest, summary.json, and the tables given as rows.</summary>
    private sealed class Footprint : IDisposable
    {
        public Footprint(params JsonObject[] tables)
        {
            var summary = new JsonObject { ["tables"] = new JsonObject() };
            foreach (string key in tables.SelectMany(row => row.Select(pair => pair.Key)).Distinct())
            {
                summary["tables"]![key] = new JsonArray(tables.Select(row => row.TryGetPropertyValue(key, out JsonNode? value) ? value?.DeepClone() : null).ToArray());
            }

            var manifest = new JsonObject
            {
                ["format"] = "pqax",
                ["formatVersion"] = "0.1",
                ["collection"] = new JsonObject { ["collectorApp"] = "pqax-tests", ["collectorAppVersion"] = "0.0", ["completedAt"] = "2026-10-11T00:00:00.000Z", ["level"] = 2 },
                ["parts"] = new JsonArray(new JsonObject { ["name"] = "summary.json", ["tables"] = new JsonObject { ["tables"] = tables.Length } }),
            };
            using ZipArchive zip = ZipFile.Open(Path, ZipArchiveMode.Create);
            foreach ((string name, JsonNode content) in new[] { ("manifest.json", (JsonNode)manifest), ("summary.json", summary) })
            {
                using Stream stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content.ToJsonString()));
            }
        }

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pqax-tables-" + Guid.NewGuid().ToString("N") + ".pqax");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    private static JsonObject Row(long id, string name, long fileSize, long nonLiveFileSize, long? rowCount, long? fileCount) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["fileSize"] = fileSize,
        ["nonLiveFileSize"] = nonLiveFileSize,
        ["metadataSize"] = 0,
        ["rowCount"] = rowCount,
        ["deletedRowCount"] = 0,
        ["fileCount"] = fileCount,
    };

    /// <summary>
    /// Six tables, in this order of the file: waste and total are the non-live files and the files, so that the ties of
    /// each sort show. Row 1 and row 4 have the same label, waste, and total; Zeta sorts first in ordinal order.
    /// </summary>
    private static Footprint Ties() => new(
        Row(1, "beta", 1000, 100, 10, 1),
        Row(2, "alpha", 1000, 100, null, 5),
        Row(3, "gamma", 2000, 100, 10, null),
        Row(4, "delta", 500, 300, 99, 2),
        Row(5, "alpha", 1000, 100, 10, 1),
        Row(6, "Zeta", 10, 0, 1, 1));

    [Theory]
    [InlineData("waste", new long[] { 4, 3, 2, 5, 1, 6 })]
    [InlineData("total", new long[] { 3, 2, 5, 1, 4, 6 })]
    [InlineData("rows", new long[] { 4, 3, 5, 1, 6, 2 })]
    [InlineData("files", new long[] { 2, 4, 5, 1, 6, 3 })]
    [InlineData("name", new long[] { 6, 2, 5, 1, 4, 3 })]
    public void The_ties_go_by_total_then_label_then_the_order_of_the_file(string sort, long[] ids)
    {
        using Footprint footprint = Ties();
        string[] arguments = sort == "waste" ? [footprint.Path] : [footprint.Path, "--sort", sort];
        JsonObject json = Json(arguments);
        Assert.Equal(ids, json["tables"]!.AsArray().Select(table => (long)table!["id"]!));
        Assert.Equal(6, (int)json["tableCount"]!);

        (int exit, string text, string error) = Tables(arguments);
        Assert.True(exit == 0, text + error);
        AssertTextTable(footprint.Path, text, json, false, sort, null);
        AssertTextTable(footprint.Path, Tables([.. arguments, "--wide"]).Output, Json([.. arguments, "--wide"]), true, sort, null);
    }

    [Fact]
    public void Top_after_the_sort_with_ties()
    {
        using Footprint footprint = Ties();
        JsonObject json = Json(footprint.Path, "--top", "3", "--sort", "files");
        Assert.Equal([2L, 4L, 5L], json["tables"]!.AsArray().Select(table => (long)table!["id"]!));
        Assert.Equal(6, (int)json["tableCount"]!);
        Assert.Equal(3, (int)json["top"]!);
        (int exit, string text, _) = Tables(footprint.Path, "--top", "3", "--sort", "files");
        Assert.Equal(0, exit);
        AssertTextTable(footprint.Path, text, json, false, "files", 3);
    }

    [Fact]
    public void The_numbers_are_rounded_as_the_views_round_them()
    {
        const long GiB = 1024L * 1024 * 1024;
        using var footprint = new Footprint(
            Row(1, "ties", 1152, 102912, 1234567, 1),
            Row(2, "sixteenth", 15, 1, 10, 1),
            Row(3, "quarter", 12544, 0, 10, 1),
            Row(4, "tiny", 1000000, 1, 10, 1),
            Row(5, "big", 22544384, 1536, 10, 1),
            Row(6, "huge", 5 * GiB + GiB / 2, 0, 10, 1),
            Row(7, "thousand", 1000, 0, 10, 1));
        (int exit, string text, string error) = Tables(footprint.Path, "--sort", "name");
        Assert.True(exit == 0, text + error);
        Dictionary<string, string[]> rows = Lines(text).Skip(2).Select(line => Regex.Split(line.Trim(), " {2,}")).ToDictionary(cells => cells[0]);
        // 1152 B is 1.125 KB and 102912 B 100.5 KB: Intl.NumberFormat rounds half away from zero.
        Assert.Equal(["ties", "1,234,567", "1", "1.13 KB", "101 KB", "n/a", "0 B", "n/a", "101 KB", "98.9%"], rows["ties"]);
        // The waste share is 1/16, 6.25%.
        Assert.Equal("6.3%", rows["sixteenth"][9]);
        Assert.Equal("15 B", rows["sixteenth"][3]);
        // 12544 B is 12.25 KB.
        Assert.Equal("12.3 KB", rows["quarter"][3]);
        Assert.Equal("0.0%", rows["quarter"][9]);
        Assert.Equal("<0.1%", rows["tiny"][9]);
        Assert.Equal("21.5 MB", rows["big"][3]);
        Assert.Equal("1.50 KB", rows["big"][4]);
        Assert.Equal("5.50 GB", rows["huge"][3]);
        Assert.Equal("1,000 B", rows["thousand"][3]);
        AssertTextTable(footprint.Path, text, Json(footprint.Path, "--sort", "name"), false, "name", null);
    }

    [Fact]
    public void A_tie_of_the_shortest_decimal_rounds_up_although_the_double_is_below_it()
    {
        // The texts are those of formatBytes and formatShare of the views, run in node with the locale en.
        JsonObject bytes = Row(1, "shortbytes", 1312, 0, 5, 1);
        bytes["deletedRowCount"] = 1;
        using var footprint = new Footprint(
            bytes,
            Row(2, "shortshare", 1997, 3, 10, 1),
            Row(3, "shortshare2", 1977, 23, 10, 1));
        (int exit, string text, string error) = Tables(footprint.Path, "--sort", "name");
        Assert.True(exit == 0, text + error);
        Dictionary<string, string[]> rows = Lines(text).Skip(2).Select(line => Regex.Split(line.Trim(), " {2,}")).ToDictionary(cells => cells[0]);
        // The deleted rows are 1312 * 1 / 5 = 262.4 B, the live bytes 1049.6 B: 1.025 KB as the shortest decimal,
        // 1.02499999999999991... as the double.
        Assert.Equal("1.03 KB", rows["shortbytes"][3]);
        Assert.Equal("262 B", rows["shortbytes"][6]);
        Assert.Equal("262 B", rows["shortbytes"][8]);
        Assert.Equal("20.0%", rows["shortbytes"][9]);
        // 3 / 2000 * 100 is 0.15 as the shortest decimal, 0.14999999999999999... as the double.
        Assert.Equal("0.2%", rows["shortshare"][9]);
        // 23 / 2000 * 100 is 1.15 as the shortest decimal, 1.14999999999999991... as the double.
        Assert.Equal("1.2%", rows["shortshare2"][9]);
        AssertTextTable(footprint.Path, text, Json(footprint.Path, "--sort", "name"), false, "name", null);
    }

    [Fact]
    public void Wide_writes_a_lower_bound_from_the_quartiles_with_its_sign()
    {
        const long MiB = 1024 * 1024;
        JsonObject quartiles = Row(1, "quartiles", 400 * MiB, 0, 10, 4);
        quartiles["fileSizes"] = new JsonObject { ["count"] = 4, ["sum"] = 400 * MiB, ["minimum"] = 1 * MiB, ["firstQuartile"] = 4 * MiB, ["median"] = 9 * MiB, ["thirdQuartile"] = 100 * MiB, ["maximum"] = 200 * MiB };
        using var footprint = new Footprint(quartiles, Row(2, "none", 10, 0, 1, 1));
        (int exit, string text, string error) = Tables(footprint.Path, "--wide", "--sort", "name");
        Assert.True(exit == 0, text + error);
        Dictionary<string, string[]> rows = Lines(text).Skip(2).Select(line => Regex.Split(line.Trim(), " {2,}")).ToDictionary(cells => cells[0]);
        Assert.Equal([">=25.0%", ">=75.0%", "0 B", "n/a"], rows["quartiles"][^4..]);
        Assert.Equal(["n/a", "n/a", "0 B", "n/a"], rows["none"][^4..]);
        AssertTextTable(footprint.Path, text, Json(footprint.Path, "--wide", "--sort", "name"), true, "name", null);
    }

    [Fact]
    public void A_files_table_that_cannot_be_read_is_a_warning_and_the_summary_is_used()
    {
        string path = Path.Combine(Path.GetTempPath(), "pqax-tables-broken-" + Guid.NewGuid().ToString("N") + ".pqax");
        try
        {
            using (ZipArchive source = ZipFile.OpenRead(SamplePath("sample")))
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

            (int exit, string text, string error) = Tables(path, "--json");
            Assert.True(exit == 0, $"exit code {exit}: {text}{error}");
            Assert.False(string.IsNullOrWhiteSpace(error));
            JsonObject json = JsonNode.Parse(text)!.AsObject();
            Assert.False((bool)json["filesRead"]!);
            Assert.NotEmpty(json["warnings"]!.AsArray());
            // Without the files, the measures are those of the summary alone.
            var differences = new List<string>();
            Compare(json["tables"], CliTables(ExpectedMeasures("summary-only"), "waste"), "tables", differences);
            Assert.True(differences.Count == 0, string.Join("\n", differences));

            (exit, text, error) = Tables(path);
            Assert.Equal(0, exit);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(4, Lines(text).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
