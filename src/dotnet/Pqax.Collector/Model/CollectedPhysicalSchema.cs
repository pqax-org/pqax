using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A distinct Parquet schema found among the files of a table.</summary>
internal sealed class CollectedPhysicalSchema
{
    public CollectedPhysicalSchema(int firstFile, List<CollectedPhysicalColumn> columns)
    {
        FirstFile = firstFile;
        Columns = columns;
    }

    /// <summary>The position of the first file that uses the schema, which orders the schemas.</summary>
    public int FirstFile { get; set; }

    public long FileCount { get; set; }

    public List<CollectedPhysicalColumn> Columns { get; }
}
