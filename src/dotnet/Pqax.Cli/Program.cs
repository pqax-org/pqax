using System.Reflection;

namespace Pqax.Cli;

/// <summary>
/// The <c>pqax</c> command line. It is a thin layer over the <c>Pqax</c> library: <c>pqax check</c> runs the validator,
/// and <c>pqax info</c> opens the file with the reader.
/// </summary>
public static class Program
{
    /// <summary>The exit code of a command that succeeded, and of <c>check</c> on a valid file.</summary>
    public const int ExitOk = 0;

    /// <summary>The exit code of <c>check</c> on an invalid file, and of <c>info</c> on a file that the reader refuses.</summary>
    public const int ExitInvalid = 1;

    /// <summary>The exit code of a wrong command line, and of a file that cannot be read.</summary>
    public const int ExitError = 2;

    /// <summary>Runs the command line on the console.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code.</returns>
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>Runs the command line and writes to the given writers, so that a test or a host can capture the output.</summary>
    /// <param name="args">The arguments, such as <c>check sample.pqax</c>.</param>
    /// <param name="output">Where the result goes.</param>
    /// <param name="error">Where usage and errors go.</param>
    /// <returns>The exit code: 0, 1, or 2.</returns>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length == 0)
        {
            Usage(error);
            return ExitError;
        }

        string command = args[0];
        switch (command)
        {
            case "-h":
            case "--help":
            case "help":
                Usage(output);
                return ExitOk;
            case "--version":
                output.WriteLine($"pqax {Version()}");
                return ExitOk;
            case "check":
            case "info":
                break;
            case "collect":
                return CollectCommand.Run(args, output, error);
            default:
                error.WriteLine($"pqax: unknown command '{command}'.");
                Usage(error);
                return ExitError;
        }

        if (!CommandLine.TryParse(args, 1, out CommandLine? line, out string? problem))
        {
            error.WriteLine($"pqax {command}: {problem}");
            Usage(error);
            return ExitError;
        }

        try
        {
            return command == "check" ? CheckCommand.Run(line!, output) : InfoCommand.Run(line!, output, error);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
        {
            error.WriteLine($"pqax {command}: cannot read {line!.File}: {e.Message}");
            return ExitError;
        }
    }

    internal static string Version()
    {
        Assembly assembly = typeof(Program).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString() ?? "0";
    }

    private static void Usage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  pqax check <file> [--part <name>]   Validate a PQAX file, or the footprint inside a VPAX file.");
        writer.WriteLine("  pqax info <file> [--part <name>]    Show the format, the collection, the parts, and the rows of each table.");
        writer.WriteLine("  pqax collect --local <folder> --output <file>     Collect a footprint from a folder in the OneLake layout.");
        writer.WriteLine("  pqax collect --workspace <id> --item <name> --output <file>   Collect from OneLake, with a device-code sign-in. See pqax collect --help.");
        writer.WriteLine("  pqax --version                      Show the version.");
        writer.WriteLine();
        writer.WriteLine("A file with the extension .vpax, or any file with --part, is read as a VPAX file that holds the");
        writer.WriteLine("footprint as a part, Footprint.pqax unless --part names another one.");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 valid or done, 1 invalid, 2 wrong command line or file that cannot be read.");
    }
}
