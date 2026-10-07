using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A distinct combination of partition values that has at least one file, with the totals of its files.</summary>
internal sealed class CollectedPartition
{
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
}
