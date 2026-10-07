using System;

namespace Pqax;

/// <summary>A row of <c>sortOrders</c>: a sort order that a table declares.</summary>
public sealed class SortOrderRow : IEquatable<SortOrderRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The <c>id</c> of the table of the sort order. Required.</summary>
    public long? Table { get; set; }

    /// <summary>The identifier that Iceberg assigns to the sort order.</summary>
    public long? OrderId { get; set; }

    /// <summary>Whether new files use this order.</summary>
    public bool? IsDefault { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(SortOrderRow? other) => Schemas.SortOrders.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SortOrderRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.SortOrders.Hash(this);
}
