using System;

namespace Pqax;

/// <summary>A row of <c>operations</c>: one kind of operation in the history of a table, with how often and how recently it ran.</summary>
public sealed class OperationRow : IEquatable<OperationRow>
{
    /// <summary>The <c>id</c> of the table of the operation. Required.</summary>
    public long? Table { get; set; }

    /// <summary>The operation, for example <c>WRITE</c>, <c>MERGE</c>, <c>OPTIMIZE</c>, <c>append</c>. Required.</summary>
    public string? Name { get; set; }

    /// <summary>The commits with this operation.</summary>
    public long? CommitCount { get; set; }

    /// <summary>The time of the most recent one.</summary>
    public DateTimeOffset? LastAt { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(OperationRow? other) => Schemas.Operations.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as OperationRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.Operations.Hash(this);
}
