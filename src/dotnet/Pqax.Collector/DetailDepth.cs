namespace Pqax.Collector;

/// <summary>How much detail a footprint keeps for a table, in the terms of section 4 of the object model.</summary>
public enum DetailDepth
{
    /// <summary>The table, its columns, partition specs, physical schemas, writers, operations, and issues.</summary>
    Summary = 0,

    /// <summary>Also one row for each data file, and the partitions and commits.</summary>
    Files = 1,

    /// <summary>Also one row for each row group of the files whose footer was read.</summary>
    RowGroups = 2,

    /// <summary>Also one row for each column chunk of those row groups.</summary>
    ColumnChunks = 3,
}
