using System;
using System.Collections.Generic;

namespace Pqax;

/// <summary>A row of <c>columns</c>: a top-level column that a table declares, with its totals.</summary>
public sealed class ColumnRow : IEquatable<ColumnRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The <c>id</c> of the table of the column. Required.</summary>
    public long? Table { get; set; }

    /// <summary>The identifier that the table format assigns, which survives a rename.</summary>
    public long? FieldId { get; set; }

    /// <summary>The name of the column. Required.</summary>
    public string? Name { get; set; }

    /// <summary>The type as the table format declares it, for example <c>decimal(18,2)</c>. Required.</summary>
    public string? DataType { get; set; }

    /// <summary>Whether the column accepts nulls.</summary>
    public bool? IsNullable { get; set; }

    /// <summary>The name used inside the files when it differs from the name.</summary>
    public string? PhysicalName { get; set; }

    /// <summary>Whether a partition field uses the column.</summary>
    public bool? IsPartitionColumn { get; set; }

    /// <summary>The comment on the column.</summary>
    public string? Description { get; set; }

    /// <summary>The compressed size of the column.</summary>
    public long? CompressedSize { get; set; }

    /// <summary>The size of the column before compression.</summary>
    public long? UncompressedSize { get; set; }

    /// <summary>The values, including nulls.</summary>
    public long? ValueCount { get; set; }

    /// <summary>The null values.</summary>
    public long? NullCount { get; set; }

    /// <summary>The column chunks.</summary>
    public long? ChunkCount { get; set; }

    /// <summary>The column chunks with a dictionary page.</summary>
    public long? DictionaryChunkCount { get; set; }

    /// <summary>The column chunks that started with a dictionary and fell back to another encoding.</summary>
    public long? FallbackChunkCount { get; set; }

    /// <summary>The compressed size of the dictionary pages.</summary>
    public long? DictionaryPageSize { get; set; }

    /// <summary>The bytes of the variable-length values before encoding.</summary>
    public long? UnencodedByteArrayDataBytes { get; set; }

    /// <summary>The codecs that the column chunks use.</summary>
    public IReadOnlyList<string>? Codecs { get; set; }

    /// <summary>The encodings that the column chunks use.</summary>
    public IReadOnlyList<string>? Encodings { get; set; }

    /// <summary>The files that do not store the column.</summary>
    public long? FileCountWithoutColumn { get; set; }

    /// <summary>The largest distinct count of any column chunk.</summary>
    public long? LargestChunkDistinctCount { get; set; }

    /// <summary>The number of distinct values in the table.</summary>
    public long? DistinctCount { get; set; }

    /// <summary>Whether the distinct count is an estimate.</summary>
    public bool? DistinctCountIsEstimate { get; set; }

    /// <summary><c>Iceberg statistics</c>, <c>Scan</c>, or <c>Semantic model</c>: where the distinct count comes from.</summary>
    public string? DistinctCountSource { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(ColumnRow? other) => Schemas.Columns.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ColumnRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.Columns.Hash(this);
}
