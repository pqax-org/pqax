namespace Pqax.Collector;

/// <summary>A leaf column of a physical schema, with the totals of its column chunks.</summary>
internal sealed class CollectedPhysicalColumn
{
    public CollectedPhysicalColumn(ParquetLeafColumn leaf, int? column)
    {
        Leaf = leaf;
        Column = column;
    }

    public ParquetLeafColumn Leaf { get; }

    /// <summary>The position of the column of the table that the leaf belongs to. Null when the table no longer declares it.</summary>
    public int? Column { get; }

    public Total CompressedSize { get; } = Total.Zero();

    public Total UncompressedSize { get; } = Total.Zero();

    public long ChunkCount { get; set; }
}
