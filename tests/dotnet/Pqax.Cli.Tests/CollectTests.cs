using System.Text.Json.Nodes;

namespace Pqax.Cli.Tests;

/// <summary>
/// <c>pqax collect</c> on the synthetic lakehouse of the kit, in a process of its own: exit code 0 and a file that
/// <c>pqax check</c> accepts, 2 for a missing folder or a wrong command line.
/// </summary>
public class CollectTests
{
    private static string Lakehouse => Path.Combine(CliKit.Root, "conformance", "fixtures", "lakehouse");

    /// <summary>The tables of the probe dump of the fixture: item, kind, namespace, and name.</summary>
    private static IReadOnlyList<JsonObject> DumpTables =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(CliKit.Root, "conformance", "fixtures", "probe-dump", "index.json")))!["tables"]!
            .AsArray().Select(table => table!.AsObject()).ToList();

    private sealed class Output : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pqax-cli-collect-" + Guid.NewGuid().ToString("N") + ".pqax");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    private static (int Exit, string Output, string Error) Collect(Output output, params string[] options) =>
        CliKit.Run(["collect", "--local", Lakehouse, "--output", output.Path, .. options]);

    private static void AssertRowsOf(string written, string sample)
    {
        using PqaxDocument actual = PqaxDocument.Open(written);
        using PqaxDocument expected = PqaxDocument.Open(Path.Combine(CliKit.Samples, sample));
        var tables = expected.Manifest.Parts.SelectMany(part => part.Tables.Keys).Union(actual.Manifest.Parts.SelectMany(part => part.Tables.Keys));
        foreach (string table in tables)
        {
            Assert.True(expected.RowCount(table) == actual.RowCount(table), $"{table}: {actual.RowCount(table)} rows, expected {expected.RowCount(table)}");
        }
    }

    [Fact]
    public void Collect_writes_a_file_that_check_accepts()
    {
        using var output = new Output();
        (int exit, string text, string error) = Collect(output);
        Assert.True(exit == 0, text + error);
        Assert.True(File.Exists(output.Path));
        string last = text.TrimEnd().Split('\n')[^1];
        Assert.Contains("valid", last, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid", last, StringComparison.Ordinal);
        (int check, string checkText, string checkError) = CliKit.Run("check", output.Path);
        Assert.True(check == 0, checkText + checkError);
        AssertRowsOf(output.Path, "sample.pqax");
        using PqaxDocument doc = PqaxDocument.Open(output.Path);
        Assert.Equal(2, doc.Manifest.Collection.Level);
        Assert.False(string.IsNullOrEmpty(doc.Manifest.Collection.CollectorApp));
        Assert.Empty(doc.Summary.Issues);
    }

    [Fact]
    public void Collect_prints_counts_and_no_name_of_the_data()
    {
        using var output = new Output();
        (int exit, string text, string error) = Collect(output);
        Assert.Equal(0, exit);
        foreach (JsonObject table in DumpTables)
        {
            foreach (string name in new[] { (string)table["item"]!, (string)table["name"]! })
            {
                Assert.True(!text.Contains(name, StringComparison.Ordinal) && !error.Contains(name, StringComparison.Ordinal), $"the output names {name}:\n{text}");
            }
        }
    }

    [Theory]
    [InlineData("summary", "summary-only.pqax")]
    [InlineData("files", "files.pqax")]
    [InlineData("rowGroups", "row-groups.pqax")]
    [InlineData("columnChunks", "sample.pqax")]
    public void The_depth_option_gives_the_rows_of_that_depth(string depth, string sample)
    {
        using var output = new Output();
        (int exit, string text, string error) = Collect(output, "--depth", depth);
        Assert.True(exit == 0, text + error);
        AssertRowsOf(output.Path, sample);
        Assert.Equal(0, CliKit.Run("check", output.Path).ExitCode);
    }

    [Fact]
    public void Level_1_reads_no_footer()
    {
        using var output = new Output();
        (int exit, string text, string error) = Collect(output, "--level", "1");
        Assert.True(exit == 0, text + error);
        using PqaxDocument doc = PqaxDocument.Open(output.Path);
        Assert.Equal(1, doc.Manifest.Collection.Level);
        Assert.All(doc.Summary.Tables, table => Assert.Equal(0, table.FooterCount));
        Assert.Empty(doc.ReadRowGroups());
    }

    [Fact]
    public void Max_footers_limits_the_footers_of_each_table()
    {
        using var output = new Output();
        (int exit, string text, string error) = Collect(output, "--max-footers", "1");
        Assert.True(exit == 0, text + error);
        using PqaxDocument doc = PqaxDocument.Open(output.Path);
        Assert.All(doc.Summary.Tables, table => Assert.Equal(1, table.FooterCount));
    }

    [Fact]
    public void Item_collects_the_tables_of_one_item()
    {
        JsonObject table = DumpTables[^1];
        string item = (string)table["item"]! + "." + (string)table["itemKind"]!;
        using var output = new Output();
        (int exit, string text, string error) = Collect(output, "--item", item);
        Assert.True(exit == 0, text + error);
        using PqaxDocument doc = PqaxDocument.Open(output.Path);
        TableRow collected = Assert.Single(doc.Summary.Tables);
        Assert.Equal((string)table["name"]!, collected.Name);
    }

    [Fact]
    public void A_missing_folder_exits_with_2_and_writes_nothing()
    {
        using var output = new Output();
        (int exit, string text, string error) = CliKit.Run("collect", "--local", Path.Combine(Lakehouse, "no-such-folder"), "--output", output.Path);
        Assert.True(exit == 2, $"exit code {exit}: {text}{error}");
        Assert.False(File.Exists(output.Path));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    public static TheoryData<string> WrongOptions => new() { "no output", "no source", "depth deep", "level 3", "max-footers -1", "parallelism 0", "unknown option" };

    [Theory]
    [MemberData(nameof(WrongOptions))]
    public void A_wrong_command_line_exits_with_2(string wrong)
    {
        using var output = new Output();
        string[] arguments = wrong switch
        {
            "no output" => ["collect", "--local", Lakehouse],
            "no source" => ["collect", "--output", output.Path],
            "depth deep" => ["collect", "--local", Lakehouse, "--output", output.Path, "--depth", "deep"],
            "level 3" => ["collect", "--local", Lakehouse, "--output", output.Path, "--level", "3"],
            "max-footers -1" => ["collect", "--local", Lakehouse, "--output", output.Path, "--max-footers", "-1"],
            "parallelism 0" => ["collect", "--local", Lakehouse, "--output", output.Path, "--parallelism", "0"],
            _ => ["collect", "--local", Lakehouse, "--output", output.Path, "--no-such-option"],
        };
        (int exit, string text, string error) = CliKit.Run(arguments);
        Assert.True(exit == 2, $"exit code {exit}: {text}{error}");
        Assert.False(File.Exists(output.Path));
    }

    [Fact]
    public void Collect_help_prints_the_usage()
    {
        (int exit, string text, _) = CliKit.Run("collect", "--help");
        Assert.Equal(0, exit);
        Assert.Contains("--local", text, StringComparison.Ordinal);
        Assert.Contains("--output", text, StringComparison.Ordinal);
    }
}
