using System;

namespace Pqax;

/// <summary>A row of <c>modelBindings</c>: the semantic model whose tables read the tables of the footprint.</summary>
public sealed class ModelBindingRow : IEquatable<ModelBindingRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The semantic model. Required.</summary>
    public string? ModelName { get; set; }

    /// <summary>The server or workspace of the model, as VPAX records it.</summary>
    public string? ServerName { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(ModelBindingRow? other) => Schemas.ModelBindings.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ModelBindingRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.ModelBindings.Hash(this);
}
