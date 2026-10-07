namespace Pqax;

/// <summary>The rows of a table that one part holds.</summary>
internal sealed class PartSlice
{
    public PartSlice(ITableWriter table, int start, int count)
    {
        Table = table;
        Start = start;
        Count = count;
    }

    public ITableWriter Table { get; }

    public int Start { get; }

    public int Count { get; }
}
