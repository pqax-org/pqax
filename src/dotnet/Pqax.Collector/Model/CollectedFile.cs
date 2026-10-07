using System;
using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A data file of a table, with what the log and its footer say.</summary>
internal sealed class CollectedFile
{
    public CollectedFile(string path)
    {
        Path = path;
    }

    /// <summary>The path written to the footprint: relative to the table, with partition values replaced unless the collection includes them.</summary>
    public string Path { get; }

    public long? FileSize { get; set; }

    public DateTimeOffset? ModifiedAt { get; set; }

    public long? RowCount { get; set; }

    public long? DeletedRowCount { get; set; }

    public long? DeletionVectorSize { get; set; }

    /// <summary>The position of the partition of the file.</summary>
    public int? Partition { get; set; }

    public string? ClusteringProvider { get; set; }

    public bool? IsVOrdered { get; set; }

    public long? FooterSize { get; set; }

    public CollectedPhysicalSchema? PhysicalSchema { get; set; }

    public CollectedWriter? Writer { get; set; }

    public long? RowGroupCount { get; set; }

    public long? CompressedSize { get; set; }

    public long? UncompressedSize { get; set; }

    /// <summary>The row groups, kept from the <c>Row groups</c> depth.</summary>
    public List<CollectedRowGroup>? RowGroups { get; set; }
}
