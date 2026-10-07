using System;

namespace Pqax;

/// <summary>A row of <c>tableBindings</c>: links one table of a semantic model to the table of the footprint that it reads.</summary>
public sealed class TableBindingRow : IEquatable<TableBindingRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The <c>id</c> of the model binding. Required.</summary>
    public long? ModelBinding { get; set; }

    /// <summary>The name of the table in the model. Required.</summary>
    public string? ModelTable { get; set; }

    /// <summary>The <c>id</c> of the table of the footprint that it reads. Required.</summary>
    public long? Table { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(TableBindingRow? other) => Schemas.TableBindings.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as TableBindingRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.TableBindings.Hash(this);
}
