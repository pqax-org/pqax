using System;

namespace Pqax;

/// <summary>A row of <c>sortFields</c>: one field of a sort order, in order.</summary>
public sealed class SortFieldRow : IEquatable<SortFieldRow>
{
    /// <summary>The <c>id</c> of the sort order of the field. Required.</summary>
    public long? SortOrder { get; set; }

    /// <summary>The position of the field in its sort order, from 0. Required.</summary>
    public long? Position { get; set; }

    /// <summary>The <c>id</c> of the column that the order uses. Required.</summary>
    public long? SourceColumn { get; set; }

    /// <summary><c>identity</c>, or a transform as in a partition field.</summary>
    public string? Transform { get; set; }

    /// <summary><c>asc</c> or <c>desc</c>. Required.</summary>
    public string? Direction { get; set; }

    /// <summary><c>nulls-first</c> or <c>nulls-last</c>.</summary>
    public string? NullOrder { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(SortFieldRow? other) => Schemas.SortFields.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SortFieldRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.SortFields.Hash(this);
}
