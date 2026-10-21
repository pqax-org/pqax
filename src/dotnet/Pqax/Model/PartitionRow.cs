using System;
using System.Collections.Generic;

namespace Pqax;

/// <summary>A row of <c>partitions</c>: one distinct combination of partition values that has at least one file.</summary>
public sealed class PartitionRow : IEquatable<PartitionRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The <c>id</c> of the table of the partition. Required.</summary>
    public long? Table { get; set; }

    /// <summary>The <c>id</c> of the spec that produced the values.</summary>
    public long? PartitionSpec { get; set; }

    /// <summary>One value for each field of the spec, present only when the collection includes partition values. An element is null for a null partition value.</summary>
    public IReadOnlyList<string>? Values { get; set; }

    /// <summary>The files of the partition.</summary>
    public long? FileCount { get; set; }

    /// <summary>The size of the files of the partition.</summary>
    public long? FileSize { get; set; }

    /// <summary>The rows of the files of the partition.</summary>
    public long? RowCount { get; set; }

    /// <summary>The rows marked as deleted in the files of the partition.</summary>
    public long? DeletedRowCount { get; set; }

    /// <summary>When the most recent data file of the partition was written.</summary>
    public DateTimeOffset? LastModifiedAt { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(PartitionRow? other) => Schemas.Partitions.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PartitionRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.Partitions.Hash(this);
}
