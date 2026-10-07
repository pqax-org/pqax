using System;
using System.Collections.Generic;

namespace Pqax;

/// <summary>A row of <c>commits</c>: one change to a table, a Delta commit or an Iceberg snapshot.</summary>
public sealed class CommitRow : IEquatable<CommitRow>
{
    /// <summary>The <c>id</c> of the table of the commit. Required.</summary>
    public long? Table { get; set; }

    /// <summary>The identifier of the commit. Required.</summary>
    public string? CommitId { get; set; }

    /// <summary>The commit it follows.</summary>
    public string? ParentId { get; set; }

    /// <summary>When it was committed.</summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>The kind of operation.</summary>
    public string? Operation { get; set; }

    /// <summary>The engine that committed.</summary>
    public string? Engine { get; set; }

    /// <summary>Whether the commit only added rows.</summary>
    public bool? IsBlindAppend { get; set; }

    /// <summary>The counters that the engine recorded, such as files and rows added and removed.</summary>
    public IReadOnlyDictionary<string, string>? Metrics { get; set; }

    /// <summary>The parameters of the operation, present only when the collection includes data values.</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(CommitRow? other) => Schemas.Commits.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as CommitRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.Commits.Hash(this);
}
