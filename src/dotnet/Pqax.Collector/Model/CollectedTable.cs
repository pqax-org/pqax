using System;
using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>
/// Everything collected about one table, with its totals computed: the input of the assembler, which turns it into
/// rows of the footprint. References between objects are positions in the lists of the table.
/// </summary>
internal sealed class CollectedTable
{
    public CollectedTable(TableLocation location, int source)
    {
        Location = location;
        Source = source;
    }

    public TableLocation Location { get; }

    /// <summary>The position of the source of the table.</summary>
    public int Source { get; }

    public string? TableId { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public IReadOnlyDictionary<string, string>? Properties { get; set; }

    public string? SnapshotId { get; set; }

    public DateTimeOffset? SnapshotTimestamp { get; set; }

    public CollectedDelta? Delta { get; set; }

    /// <summary>The positions of the clustering columns. Null when the table is not clustered.</summary>
    public List<int>? ClusteringColumns { get; set; }

    public long? CollectedLevel { get; set; }

    public DetailDepth Depth { get; set; }

    public long? FileCount { get; set; }

    public long? FileSize { get; set; }

    public long? RowCount { get; set; }

    public long? DeletedRowCount { get; set; }

    public DateTimeOffset? LastModifiedAt { get; set; }

    public long? VOrderedFileCount { get; set; }

    public long? PartitionCount { get; set; }

    public long? CommitCount { get; set; }

    public long? CommitsRead { get; set; }

    public DateTimeOffset? OldestCommitAt { get; set; }

    public long? NonLiveFileCount { get; set; }

    public long? NonLiveFileSize { get; set; }

    public long? MetadataFileCount { get; set; }

    public long? MetadataSize { get; set; }

    public long? FooterCount { get; set; }

    public long? RowGroupCount { get; set; }

    public long? CompressedSize { get; set; }

    public long? UncompressedSize { get; set; }

    public long? FooterSize { get; set; }

    public long? StaleColumnSize { get; set; }

    public string? ColumnTotalsSource { get; set; }

    public CollectedDistribution? FileSizes { get; set; }

    public CollectedDistribution? RowGroupRowCounts { get; set; }

    public CollectedDistribution? PartitionSizes { get; set; }

    public List<CollectedColumn> Columns { get; } = new List<CollectedColumn>();

    /// <summary>The fields of the partition spec. Null when the table is not partitioned.</summary>
    public List<CollectedPartitionField>? PartitionFields { get; set; }

    public List<CollectedPartition> Partitions { get; } = new List<CollectedPartition>();

    public List<CollectedPhysicalSchema> PhysicalSchemas { get; } = new List<CollectedPhysicalSchema>();

    public List<CollectedWriter> Writers { get; } = new List<CollectedWriter>();

    public List<CollectedOperation> Operations { get; } = new List<CollectedOperation>();

    /// <summary>The data files, kept from the <c>Files</c> depth.</summary>
    public List<CollectedFile> Files { get; } = new List<CollectedFile>();

    /// <summary>The commits, kept from the <c>Files</c> depth.</summary>
    public List<CollectedCommit> Commits { get; } = new List<CollectedCommit>();

    public List<CollectedIssue> Issues { get; } = new List<CollectedIssue>();

    /// <summary>The footers read, for the progress report.</summary>
    public int FootersRead { get; set; }
}
