using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pqax.Cli.Tests;

/// <summary>
/// <c>pqax check</c> and <c>pqax info</c>, each in a process of its own: exit code 0 on a valid sample, 1 on an
/// invalid one with the codes in the output, 2 on a wrong command line or a file that cannot be read (plan 5).
/// </summary>
public class CheckTests
{
    public static TheoryData<string> ValidSamples => CliKit.Data(CliKit.ExpectedNames().Select(name => (string)CliKit.Expected(name)["file"]!));

    public static TheoryData<string> InvalidFiles => CliKit.Data(CliKit.InvalidCases().Select(pair => pair.Key));

    private static readonly IReadOnlyList<string> AllCodes = CliKit.InvalidCases()
        .SelectMany(pair => pair.Value!["codes"]!.AsArray().Select(code => (string)code!)).Distinct().ToList();

    [Theory]
    [MemberData(nameof(ValidSamples))]
    public void Check_exits_with_0_on_a_valid_sample(string file)
    {
        (int exit, string output, string error) = CliKit.Run("check", Path.Combine(CliKit.Samples, file));
        Assert.True(exit == 0, output + error);
        Assert.All(AllCodes, code => Assert.DoesNotMatch(new Regex($"^{Regex.Escape(code)}:", RegexOptions.Multiline), output));
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Check_exits_with_1_and_prints_the_codes_of_an_invalid_sample(string file)
    {
        (int exit, string output, string error) = CliKit.Run("check", Path.Combine(CliKit.Invalid, file));
        Assert.True(exit == 1, $"exit code {exit}: {output}{error}");
        string[] lines = output.Split('\n');
        foreach (JsonNode? code in CliKit.InvalidCases()[file]!["codes"]!.AsArray())
        {
            string text = (string)code!;
            Assert.True(lines.Any(line => CliKit.Code(text).IsMatch(line)), $"{text} is not in the output:\n{output}");
            Assert.Matches(new Regex($"^{Regex.Escape(text)}:", RegexOptions.Multiline), output);
        }
    }

    [Fact]
    public void Check_prints_one_line_for_each_issue()
    {
        (int exit, string output, _) = CliKit.Run("check", Path.Combine(CliKit.Invalid, "totals.fileSize.pqax"));
        Assert.Equal(1, exit);
        Assert.Single(output.Split('\n'), line => CliKit.Code("totals.fileSize").IsMatch(line));
    }

    [Fact]
    public void Check_of_a_package_without_the_footprint_fails()
    {
        string path = Path.Combine(Path.GetTempPath(), "pqax-cli-tests-" + Guid.NewGuid().ToString("N") + ".vpax");
        try
        {
            using (var source = System.IO.Compression.ZipFile.OpenRead(Path.Combine(CliKit.Samples, "embedded-stored.vpax")))
            using (var target = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var entry in source.Entries.Where(entry => entry.FullName != "Footprint.pqax"))
                {
                    using Stream from = entry.Open();
                    using Stream to = target.CreateEntry(entry.FullName).Open();
                    from.CopyTo(to);
                }
            }

            (int exit, string output, string error) = CliKit.Run("check", path);
            Assert.True(exit is 1 or 2, $"exit code {exit}");
            Assert.Matches(CliKit.Code("embedded.partMissing"), output + error);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class CommandLineTests
{
    public static TheoryData<string> InfoSamples => CliKit.Data(["sample", "multipart", "extensions", "summary-only", "embedded-deflated", "embedded-stored"]);

    [Theory]
    [InlineData]
    [InlineData("unknown", "file.pqax")]
    [InlineData("check")]
    [InlineData("info")]
    public void A_wrong_command_line_exits_with_2(params string[] arguments)
    {
        (int exit, _, string error) = CliKit.Run(arguments);
        Assert.Equal(2, exit);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("check")]
    [InlineData("info")]
    public void A_file_that_does_not_exist_exits_with_2(string command)
    {
        (int exit, _, string error) = CliKit.Run(command, Path.Combine(CliKit.Samples, "no-such-file.pqax"));
        Assert.Equal(2, exit);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [MemberData(nameof(InfoSamples))]
    public void Info_prints_the_parts_and_the_rows(string name)
    {
        JsonObject expected = CliKit.Expected(name);
        (int exit, string output, string error) = CliKit.Run("info", Path.Combine(CliKit.Samples, (string)expected["file"]!));
        Assert.True(exit == 0, output + error);
        foreach (JsonNode? part in expected["parts"]!.AsArray())
        {
            Assert.Matches(CliKit.Code((string)part!), output);
        }

        foreach ((string table, JsonNode? rows) in expected["rows"]!.AsObject())
        {
            var pattern = new Regex($@"(?<![\w.]){Regex.Escape(table)}\W+{(int)rows!}(?!\d)");
            Assert.True(pattern.IsMatch(output), $"{table} {(int)rows!} is not in the output:\n{output}");
        }

        if (expected["embeddedCompression"] is JsonNode compression)
        {
            Assert.Contains((string)compression!, output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("manifest.versionUnknown.pqax")]
    [InlineData("manifest.format.pqax")]
    [InlineData("container.manifestMissing.pqax")]
    public void Info_on_a_file_that_the_reader_refuses_exits_with_1(string file)
    {
        (int exit, string output, string error) = CliKit.Run("info", Path.Combine(CliKit.Invalid, file));
        Assert.True(exit == 1, $"exit code {exit}: {output}{error}");
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--help")]
    public void Version_and_help_exit_with_0(string option)
    {
        (int exit, string output, _) = CliKit.Run(option);
        Assert.Equal(0, exit);
        Assert.Contains("pqax", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_in_process_gives_the_exit_codes_of_the_process()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, Program.Run(["check", Path.Combine(CliKit.Samples, "sample.pqax")], output, error));
        Assert.Equal(1, Program.Run(["check", Path.Combine(CliKit.Invalid, "json.bom.pqax")], output, error));
        Assert.Matches(new Regex("^json.bom:", RegexOptions.Multiline), output.ToString());
        Assert.Equal(2, Program.Run([], output, error));
    }
}
