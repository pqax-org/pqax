using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pqax.Cli.Tests;

/// <summary>Where the kit is, and how to run the command line in a process of its own.</summary>
public static class CliKit
{
    public static string Root { get; } = FindRoot();

    public static string Samples => Path.Combine(Root, "conformance", "samples");

    public static string Invalid => Path.Combine(Samples, "invalid");

    public static string ExpectedFolder => Path.Combine(Root, "conformance", "expected");

    private static string FindRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            if (Directory.Exists(Path.Combine(folder.FullName, "conformance")) && File.Exists(Path.Combine(folder.FullName, "Pqax.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException($"No folder with conformance/ and Pqax.slnx above {AppContext.BaseDirectory}");
    }

    public static IEnumerable<string> ExpectedNames() =>
        Directory.GetFiles(ExpectedFolder, "*.json").Select(path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.Ordinal);

    public static JsonObject Expected(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ExpectedFolder, name + ".json")))!.AsObject();

    public static JsonObject InvalidCases() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Invalid, "expected.json")))!.AsObject();

    public static TheoryData<string> Data(IEnumerable<string> values)
    {
        var data = new TheoryData<string>();
        foreach (string value in values)
        {
            data.Add(value);
        }

        return data;
    }

    /// <summary>The assembly of the command line, which the build copies next to the tests with its runtime configuration.</summary>
    public static string CliAssembly
    {
        get
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Pqax.Cli.dll");
            Assert.True(File.Exists(path), $"{path} is missing");
            Assert.True(File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")), "the runtime configuration of the command line is missing");
            return path;
        }
    }

    /// <summary>Runs <c>dotnet Pqax.Cli.dll</c> with the arguments, from the root of the repository.</summary>
    public static (int ExitCode, string Output, string Error) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(CliAssembly);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"pqax {string.Join(' ', arguments)} did not end");
        }

        return (process.ExitCode, output.Result, error.Result);
    }

    /// <summary>A pattern that finds a code as a word of its own, and not inside a file name such as json.bom.pqax.</summary>
    public static Regex Code(string code) => new($@"(?<![\w.]){Regex.Escape(code)}(?![\w.])");
}
