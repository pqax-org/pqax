using System.Collections.Generic;

namespace Pqax;

/// <summary>Counts the checks and records the issues of a validation.</summary>
internal sealed class IssueLog
{
    private readonly List<ValidationIssue> issues = new List<ValidationIssue>();

    public int Checks { get; private set; }

    public IReadOnlyList<ValidationIssue> Issues => issues;

    /// <summary>Counts a check, records an issue when the condition is false, and returns the condition.</summary>
    public bool Check(bool condition, string code, string message, string? part = null, string? table = null, string? field = null)
    {
        Checks++;
        if (!condition)
        {
            issues.Add(new ValidationIssue(code, message, part, table, field));
        }

        return condition;
    }

    /// <summary>Counts a check that failed and records its issue.</summary>
    public void Fail(string code, string message, string? part = null, string? table = null, string? field = null)
        => Check(false, code, message, part, table, field);

    /// <summary>Counts a check that passed.</summary>
    public void Pass() => Checks++;

    public ValidationResult Result() => new ValidationResult(issues.ToArray(), Checks);
}
