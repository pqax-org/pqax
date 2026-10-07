namespace Pqax.Cli;

/// <summary><c>pqax check</c>: validates a file and prints one line for each issue, with its code.</summary>
public static class CheckCommand
{
    /// <summary>Validates the file and writes the issues and a last line with the result.</summary>
    /// <param name="line">The arguments.</param>
    /// <param name="output">Where the result goes.</param>
    /// <returns>0 when the file is valid, 1 when it is not.</returns>
    public static int Run(CommandLine line, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);
        ValidationResult result = line.IsEmbedded
            ? PqaxValidator.ValidateEmbedded(line.File, line.PartName)
            : PqaxValidator.Validate(line.File);
        foreach (ValidationIssue issue in result.Issues)
        {
            output.WriteLine(issue.ToString());
        }

        string what = line.IsEmbedded ? $"{Path.GetFileName(line.File)} ({line.PartName})" : Path.GetFileName(line.File);
        output.WriteLine(result.IsValid
            ? $"{what}: valid, {result.ChecksRun} checks"
            : $"{what}: invalid, {result.Issues.Count} {(result.Issues.Count == 1 ? "issue" : "issues")} in {result.ChecksRun} checks");
        return result.IsValid ? Program.ExitOk : Program.ExitInvalid;
    }
}
