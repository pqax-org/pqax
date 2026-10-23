using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pqax.Cli.Tests;

/// <summary>
/// <c>pqax tables</c> on a table that lacks FileSize and every field of the storage stack: section 10.2 of
/// <c>docs/object-model.md</c> makes its Waste, Overhead, Data, Total, and WasteShare null, so the JSON writes
/// <c>null</c> and the text table <c>n/a</c>, and 10.2 orders the tables by Waste with the null one after the others.
/// </summary>
public class Section10TablesTests
{
    private static (int Exit, string Output, string Error) Tables(params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = Program.Run(["tables", .. arguments], output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    private static string[] Split(string line) => Regex.Split(line.Trim(), " {2,}");

    /// <summary>A footprint of format 0.2 written in a temporary file: the manifest, summary.json, and the tables given as rows.</summary>
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
                ["formatVersion"] = "0.2",
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

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pqax-section10-" + Guid.NewGuid().ToString("N") + ".pqax");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    /// <summary>
    /// "bare" has no FileSize and no field of the stack; "full" has a Waste of 100 (NonLiveFileSize, no listing), a
    /// Total of 1100, and a WasteShare of 100 / 1100.
    /// </summary>
    private static Footprint BareAndFull() => new(
        new JsonObject { ["id"] = 1, ["name"] = "bare" },
        new JsonObject { ["id"] = 2, ["name"] = "full", ["fileSize"] = 1000, ["nonLiveFileSize"] = 100, ["metadataSize"] = 0, ["rowCount"] = 10, ["deletedRowCount"] = 0, ["fileCount"] = 1 });

    [Fact]
    public void Rule_10_2_the_json_writes_null_for_the_Waste_and_the_WasteShare_of_a_table_without_them()
    {
        using Footprint footprint = BareAndFull();
        (int exit, string output, string error) = Tables(footprint.Path, "--json");
        Assert.True(exit == 0, output + error);
        JsonArray tables = JsonNode.Parse(output)!["tables"]!.AsArray();

        // 10.2: the tables go by Waste, and a null Waste after the others.
        Assert.Equal(["full", "bare"], tables.Select(table => (string)table!["name"]!));
        JsonObject bare = tables[1]!["stack"]!.AsObject();
        foreach (string key in new[] { "live", "nonLive", "unreferenced", "deleted", "stale", "metadata", "footers", "data", "total", "waste", "overhead", "wasteShare", "overheadShare" })
        {
            Assert.True(bare.ContainsKey(key), key);
            Assert.True(bare[key] is null, $"{key} is {bare[key]?.ToJsonString()}");
        }

        JsonObject full = tables[0]!["stack"]!.AsObject();
        Assert.Equal(100.0, full["waste"]!.GetValue<double>());
        Assert.Equal(1100.0, full["total"]!.GetValue<double>());
        Assert.Equal(0.0, full["overhead"]!.GetValue<double>());
        Assert.Equal(100.0 / 1100, full["wasteShare"]!.GetValue<double>());
    }

    [Fact]
    public void Rule_10_2_the_text_table_writes_n_a_for_the_Waste_and_the_WasteShare_of_a_table_without_them()
    {
        using Footprint footprint = BareAndFull();
        (int exit, string output, string error) = Tables(footprint.Path, "--wide");
        Assert.True(exit == 0, output + error);
        string[] lines = Lines(output);
        Assert.Equal(4, lines.Length);
        string[] header = Split(lines[1]);
        Dictionary<string, string[]> rows = lines.Skip(2).Select(Split).ToDictionary(cells => cells[0]);
        Assert.Equal(["full", "bare"], lines.Skip(2).Select(line => Split(line)[0]));
        string[] bare = rows["bare"];
        Assert.Equal(header.Length, bare.Length);
        Assert.Equal("n/a", bare[Array.IndexOf(header, "Live")]);
        Assert.Equal("n/a", bare[Array.IndexOf(header, "Waste")]);
        Assert.Equal("n/a", bare[Array.IndexOf(header, "Waste share")]);
        Assert.Equal("n/a", bare[Array.IndexOf(header, "Metadata")]);
        Assert.Equal("n/a", bare[Array.IndexOf(header, "Footers")]);
        Assert.Equal("100 B", rows["full"][Array.IndexOf(header, "Waste")]);
        Assert.Equal("9.1%", rows["full"][Array.IndexOf(header, "Waste share")]);
    }
}
