using System.Collections.Generic;

namespace Pqax;

/// <summary>The result of <see cref="PqaxValidator"/>: every issue found, and how many checks ran.</summary>
public sealed class ValidationResult
{
    internal ValidationResult(IReadOnlyList<ValidationIssue> issues, int checksRun)
    {
        Issues = issues;
        ChecksRun = checksRun;
    }

    /// <summary>True when the file breaks no rule.</summary>
    public bool IsValid => Issues.Count == 0;

    /// <summary>Every issue, in the order found: the container, the manifest, the parts in the order of the manifest, then the rules across tables.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>The number of checks that ran. A rule that could not be evaluated because an earlier rule failed did not run.</summary>
    public int ChecksRun { get; }

    /// <summary>A line that says whether the file is valid, with the counts.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => IsValid
        ? $"valid, {ChecksRun} checks"
        : $"invalid, {Issues.Count} issues in {ChecksRun} checks";
}
