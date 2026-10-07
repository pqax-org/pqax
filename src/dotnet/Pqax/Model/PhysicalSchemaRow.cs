using System;

namespace Pqax;

/// <summary>A row of <c>physicalSchemas</c>: one distinct Parquet schema found among the files of a table.</summary>
public sealed class PhysicalSchemaRow : IEquatable<PhysicalSchemaRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The <c>id</c> of the table of the schema. Required.</summary>
    public long? Table { get; set; }

    /// <summary>The files that use the schema.</summary>
    public long? FileCount { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(PhysicalSchemaRow? other) => Schemas.PhysicalSchemas.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PhysicalSchemaRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.PhysicalSchemas.Hash(this);
}
