namespace Pqax.Cli;

/// <summary>The arguments of <c>check</c> and <c>info</c>: a file and an optional part name, parsed by hand.</summary>
public sealed class CommandLine
{
    private CommandLine(string file, string? part)
    {
        File = file;
        Part = part;
    }

    /// <summary>The path of the file.</summary>
    public string File { get; }

    /// <summary>The name of the part of a VPAX file that holds the footprint, when <c>--part</c> gives one.</summary>
    public string? Part { get; }

    /// <summary>Whether the file is read as a VPAX file: its extension is <c>.vpax</c>, or <c>--part</c> is given.</summary>
    public bool IsEmbedded => Part != null || string.Equals(Path.GetExtension(File), ".vpax", StringComparison.OrdinalIgnoreCase);

    /// <summary>The part that holds the footprint in a VPAX file.</summary>
    public string PartName => Part ?? PqaxDocument.FootprintPartName;

    /// <summary>Parses the arguments after the command.</summary>
    /// <param name="args">All the arguments.</param>
    /// <param name="start">The position of the first argument after the command.</param>
    /// <param name="line">The parsed arguments.</param>
    /// <param name="problem">What is wrong, when the arguments cannot be parsed.</param>
    /// <returns>True when the arguments are right.</returns>
    public static bool TryParse(string[] args, int start, out CommandLine? line, out string? problem)
    {
        line = null;
        problem = null;
        string? file = null;
        string? part = null;
        for (int i = start; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--part")
            {
                if (i + 1 >= args.Length || args[i + 1].Length == 0)
                {
                    problem = "--part needs the name of a part.";
                    return false;
                }

                part = args[++i];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                problem = $"unknown option '{arg}'.";
                return false;
            }
            else if (file == null)
            {
                file = arg;
            }
            else
            {
                problem = $"one file at a time; '{arg}' is one too many.";
                return false;
            }
        }

        if (string.IsNullOrEmpty(file))
        {
            problem = "the file is missing.";
            return false;
        }

        line = new CommandLine(file, part);
        return true;
    }
}
