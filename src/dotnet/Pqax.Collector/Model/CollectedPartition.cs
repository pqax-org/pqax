using System;
using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A distinct combination of partition values that has at least one file, with the totals of its files.</summary>
internal sealed class CollectedPartition
{
    private DateTimeOffset? lastModified;
    private bool modifiedUnknown;

    public CollectedPartition(IReadOnlyList<string?> values)
    {
        Values = values;
    }

    /// <summary>The values, one for each field of the spec. Written only when the collection includes partition values.</summary>
    public IReadOnlyList<string?> Values { get; }

    public long FileCount { get; set; }

    public Total FileSize { get; } = Total.Zero();

    public Total RowCount { get; } = Total.Zero();

    public Total DeletedRowCount { get; } = Total.Zero();

    /// <summary>When the most recent file of the partition was written. Null when a file of the partition has no time.</summary>
    public DateTimeOffset? LastModifiedAt => modifiedUnknown ? null : lastModified;

    /// <summary>Adds the time of a file of the partition to <see cref="LastModifiedAt"/>.</summary>
    public void AddModifiedAt(DateTimeOffset? modifiedAt)
    {
        if (modifiedAt == null)
        {
            modifiedUnknown = true;
        }
        else if (lastModified == null || modifiedAt > lastModified)
        {
            lastModified = modifiedAt;
        }
    }
}
