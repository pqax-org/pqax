using System;

namespace Pqax;

/// <summary>A row of <c>columnBindings</c>: links one column of a semantic model to a column of the footprint.</summary>
public sealed class ColumnBindingRow : IEquatable<ColumnBindingRow>
{
    /// <summary>The <c>id</c> of the table binding. Required.</summary>
    public long? TableBinding { get; set; }

    /// <summary>The name of the column in the model. Required.</summary>
    public string? ModelColumn { get; set; }

    /// <summary>The <c>id</c> of the column of the footprint. Required.</summary>
    public long? Column { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(ColumnBindingRow? other) => Schemas.ColumnBindings.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ColumnBindingRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.ColumnBindings.Hash(this);
}
