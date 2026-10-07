using System;
using System.Collections.Generic;

namespace Pqax;

/// <summary>A row of <c>physicalColumns</c>: a leaf column of a physical schema.</summary>
public sealed class PhysicalColumnRow : IEquatable<PhysicalColumnRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The <c>id</c> of the physical schema of the column. Required.</summary>
    public long? PhysicalSchema { get; set; }

    /// <summary>The position among the leaf columns, from 0, which is also the position of its column chunk in a row group. Required.</summary>
    public long? Ordinal { get; set; }

    /// <summary>The names from the root of the schema to the leaf. Required.</summary>
    public IReadOnlyList<string>? Path { get; set; }

    /// <summary>The Parquet physical type, such as <c>INT64</c> or <c>BYTE_ARRAY</c>. Required.</summary>
    public string? PhysicalType { get; set; }

    /// <summary>The length of a fixed-length value.</summary>
    public long? TypeLength { get; set; }

    /// <summary>The logical type annotation as text, for example <c>STRING</c> or <c>DECIMAL(18,2)</c>.</summary>
    public string? LogicalType { get; set; }

    /// <summary><c>REQUIRED</c>, <c>OPTIONAL</c>, or <c>REPEATED</c>.</summary>
    public string? Repetition { get; set; }

    /// <summary>The field identifier written in the file.</summary>
    public long? FieldId { get; set; }

    /// <summary>The <c>id</c> of the top-level column that the leaf belongs to. Null when the table no longer declares it.</summary>
    public long? Column { get; set; }

    /// <summary>The sum of the compressed sizes of its column chunks.</summary>
    public long? CompressedSize { get; set; }

    /// <summary>The sum of the uncompressed sizes of its column chunks.</summary>
    public long? UncompressedSize { get; set; }

    /// <summary>Its column chunks.</summary>
    public long? ChunkCount { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(PhysicalColumnRow? other) => Schemas.PhysicalColumns.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PhysicalColumnRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.PhysicalColumns.Hash(this);
}
