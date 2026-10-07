using System;

namespace Pqax;

/// <summary>A row of <c>rowGroups</c>: a row group of a data file.</summary>
public sealed class RowGroupRow : IEquatable<RowGroupRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The <c>id</c> of the data file of the row group. Required.</summary>
    public long? File { get; set; }

    /// <summary>The position in the file, from 0. Required.</summary>
    public long? Ordinal { get; set; }

    /// <summary>The rows in the row group. Required.</summary>
    public long? RowCount { get; set; }

    /// <summary>The uncompressed size of the column data.</summary>
    public long? UncompressedSize { get; set; }

    /// <summary>The compressed size of the column data.</summary>
    public long? CompressedSize { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(RowGroupRow? other) => Schemas.RowGroups.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as RowGroupRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.RowGroups.Hash(this);
}
