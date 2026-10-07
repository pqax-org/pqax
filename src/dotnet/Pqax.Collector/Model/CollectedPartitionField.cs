namespace Pqax.Collector;

/// <summary>A field of the partition spec of a table.</summary>
internal sealed class CollectedPartitionField
{
    public CollectedPartitionField(string name, int? sourceColumn)
    {
        Name = name;
        SourceColumn = sourceColumn;
    }

    public string Name { get; }

    /// <summary>The position of the source column in the columns of the table.</summary>
    public int? SourceColumn { get; }
}
