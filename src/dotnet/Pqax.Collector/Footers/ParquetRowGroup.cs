using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A row group of a Parquet file, as its footer describes it.</summary>
public sealed class ParquetRowGroup
{
    internal ParquetRowGroup(IReadOnlyList<ParquetColumnChunk> columns)
    {
        Columns = columns;
    }

    /// <summary>The rows.</summary>
    public long NumRows { get; internal set; }

    /// <summary>The size of the column data before compression, as the writer computed it.</summary>
    public long TotalByteSize { get; internal set; }

    /// <summary>The compressed size of the column data, when the footer gives it.</summary>
    public long? TotalCompressedSize { get; internal set; }

    /// <summary>The position of the first page of the row group, when the footer gives it.</summary>
    public long? FileOffset { get; internal set; }

    /// <summary>The position of the row group in the file, when the footer gives it.</summary>
    public int? Ordinal { get; internal set; }

    /// <summary>The columns that the rows are sorted by. Null when the footer gives none.</summary>
    public IReadOnlyList<ParquetSortingColumn>? SortingColumns { get; internal set; }

    /// <summary>The column chunks, one for each leaf column, in the order of the leaves.</summary>
    public IReadOnlyList<ParquetColumnChunk> Columns { get; }
}
