using System.Collections.Generic;

namespace Pqax;

/// <summary>A rule of the format that a file breaks: the code of the rule, a message, and where it applies.</summary>
public sealed class ValidationIssue
{
    /// <summary>Creates an issue.</summary>
    /// <param name="code">The code of the rule, such as <c>part.rowCount</c>.</param>
    /// <param name="message">What is wrong.</param>
    /// <param name="part">The part concerned, when the issue is in one part.</param>
    /// <param name="table">The table concerned, when the issue is about one table.</param>
    /// <param name="field">The field concerned, when the issue is about one field.</param>
    public ValidationIssue(string code, string message, string? part = null, string? table = null, string? field = null)
    {
        Code = code;
        Message = message;
        Part = part;
        Table = table;
        Field = field;
    }

    /// <summary>The code of the rule, as the conformance kit lists it, such as <c>table.reference</c>.</summary>
    public string Code { get; }

    /// <summary>What is wrong, in plain text. It holds numbers and names of the format, never a name that comes from the data.</summary>
    public string Message { get; }

    /// <summary>The part or entry concerned, or null.</summary>
    public string? Part { get; }

    /// <summary>The table concerned, or null.</summary>
    public string? Table { get; }

    /// <summary>The field concerned, or null. A member of the manifest is named here too, such as <c>collection.level</c>.</summary>
    public string? Field { get; }

    /// <summary>The code, the message, and where the issue applies, on one line.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        var where = new List<string>();
        if (Part != null)
        {
            where.Add("part " + Part);
        }

        if (Table != null)
        {
            where.Add("table " + Table);
        }

        if (Field != null)
        {
            where.Add("field " + Field);
        }

        return where.Count == 0 ? $"{Code}: {Message}" : $"{Code}: {Message} ({string.Join(", ", where)})";
    }
}
