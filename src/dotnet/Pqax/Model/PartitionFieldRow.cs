using System;
using System.Collections.Generic;

namespace Pqax;

/// <summary>A row of <c>partitionFields</c>: one field of a partition spec, in order.</summary>
public sealed class PartitionFieldRow : IEquatable<PartitionFieldRow>
{
    /// <summary>The <c>id</c> of the partition spec of the field. Required.</summary>
    public long? PartitionSpec { get; set; }

    /// <summary>The position of the field in its spec, from 0. Required.</summary>
    public long? Position { get; set; }

    /// <summary>The name of the partition field.</summary>
    public string? Name { get; set; }

    /// <summary>The <c>id</c> of the columns that the value comes from. Iceberg allows more than one.</summary>
    public IReadOnlyList<long>? SourceColumns { get; set; }

    /// <summary><c>identity</c> for Delta. For Iceberg also <c>bucket[N]</c>, <c>truncate[W]</c>, <c>year</c>, <c>month</c>, <c>day</c>, <c>hour</c>, <c>void</c>.</summary>
    public string? Transform { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(PartitionFieldRow? other) => Schemas.PartitionFields.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PartitionFieldRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.PartitionFields.Hash(this);
}
