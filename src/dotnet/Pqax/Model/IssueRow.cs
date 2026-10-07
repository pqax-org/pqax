using System;

namespace Pqax;

/// <summary>A row of <c>issues</c>: a problem met while collecting, for a table or for the whole footprint.</summary>
public sealed class IssueRow : IEquatable<IssueRow>
{
    /// <summary>The <c>id</c> of the table concerned. Null when the issue concerns no single table.</summary>
    public long? Table { get; set; }

    /// <summary>The file concerned, when the issue is about one file.</summary>
    public string? Path { get; set; }

    /// <summary>A stable code, for example <c>AccessDenied</c> or <c>FooterNotRead</c>. Required.</summary>
    public string? Code { get; set; }

    /// <summary>The detail.</summary>
    public string? Message { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(IssueRow? other) => Schemas.Issues.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as IssueRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.Issues.Hash(this);
}
