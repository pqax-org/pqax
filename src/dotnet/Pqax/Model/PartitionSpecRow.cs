using System;

namespace Pqax;

/// <summary>A row of <c>partitionSpecs</c>: how the rows of a table are assigned to partitions.</summary>
public sealed class PartitionSpecRow : IEquatable<PartitionSpecRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The <c>id</c> of the table of the spec. Required.</summary>
    public long? Table { get; set; }

    /// <summary>The identifier that Iceberg assigns to the spec.</summary>
    public long? SpecId { get; set; }

    /// <summary>Whether new files use this spec.</summary>
    public bool? IsDefault { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(PartitionSpecRow? other) => Schemas.PartitionSpecs.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PartitionSpecRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.PartitionSpecs.Hash(this);
}
