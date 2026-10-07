namespace Pqax.Collector;

/// <summary>A column that a row group is sorted by.</summary>
public sealed class ParquetSortingColumn
{
    internal ParquetSortingColumn(int columnIndex, bool descending, bool nullsFirst)
    {
        ColumnIndex = columnIndex;
        Descending = descending;
        NullsFirst = nullsFirst;
    }

    /// <summary>The ordinal of the leaf column.</summary>
    public int ColumnIndex { get; }

    /// <summary>Whether the order is descending.</summary>
    public bool Descending { get; }

    /// <summary>Whether nulls come first.</summary>
    public bool NullsFirst { get; }
}
