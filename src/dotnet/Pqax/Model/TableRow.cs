using System;
using System.Collections.Generic;

namespace Pqax;

/// <summary>A row of <c>tables</c>: a table at one version, with its totals. The footprint describes the data files of that version only.</summary>
public sealed class TableRow : IEquatable<TableRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The <c>id</c> of the source that holds the table.</summary>
    public long? Source { get; set; }

    /// <summary>The schema or namespace path of the table.</summary>
    public IReadOnlyList<string>? Namespace { get; set; }

    /// <summary>The table name. Required.</summary>
    public string? Name { get; set; }

    /// <summary>The root location of the table.</summary>
    public string? Location { get; set; }

    /// <summary><c>Delta</c>, <c>Iceberg</c>, or <c>Parquet</c> for a folder of files with no table format. Required.</summary>
    public string? Format { get; set; }

    /// <summary>An identifier that stays the same across versions of the table.</summary>
    public string? TableId { get; set; }

    /// <summary>The description of the table.</summary>
    public string? Description { get; set; }

    /// <summary>When the table was created.</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>The table properties.</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; set; }

    /// <summary>The version of the table that the footprint describes.</summary>
    public string? SnapshotId { get; set; }

    /// <summary>When that version was created.</summary>
    public DateTimeOffset? SnapshotTimestamp { get; set; }

    /// <summary>The details of a Delta table.</summary>
    public DeltaDetails? Delta { get; set; }

    /// <summary>The details of an Iceberg table.</summary>
    public IcebergDetails? Iceberg { get; set; }

    /// <summary>The <c>id</c> of the columns of a Delta clustered table.</summary>
    public IReadOnlyList<long>? ClusteringColumns { get; set; }

    /// <summary>The collection level actually reached for this table.</summary>
    public long? CollectedLevel { get; set; }

    /// <summary><c>Summary</c>, <c>Files</c>, <c>Row groups</c>, or <c>Column chunks</c>: which detail the file keeps for the table. Required.</summary>
    public string? DetailDepth { get; set; }

    /// <summary>The data files of the version described.</summary>
    public long? FileCount { get; set; }

    /// <summary>The sum of the sizes of the data files.</summary>
    public long? FileSize { get; set; }

    /// <summary>The rows stored, including rows marked as deleted.</summary>
    public long? RowCount { get; set; }

    /// <summary>The rows marked as deleted. Null when the table format cannot say.</summary>
    public long? DeletedRowCount { get; set; }

    /// <summary>When the most recent data file was written.</summary>
    public DateTimeOffset? LastModifiedAt { get; set; }

    /// <summary>The files that a file tag or the footer marks as V-Ordered.</summary>
    public long? VOrderedFileCount { get; set; }

    /// <summary>The distinct partitions that hold data.</summary>
    public long? PartitionCount { get; set; }

    /// <summary>The delete files of an Iceberg table.</summary>
    public long? DeleteFileCount { get; set; }

    /// <summary>The size of the delete files of an Iceberg table.</summary>
    public long? DeleteFileSize { get; set; }

    /// <summary>The commits that the table format still keeps.</summary>
    public long? CommitCount { get; set; }

    /// <summary>The most recent commits that the collector read.</summary>
    public long? CommitsRead { get; set; }

    /// <summary>The time of the oldest of the commits read.</summary>
    public DateTimeOffset? OldestCommitAt { get; set; }

    /// <summary>Files that left the table and that the log still tracks, so they may still be stored.</summary>
    public long? NonLiveFileCount { get; set; }

    /// <summary>The size of the files that left the table and that the log still tracks.</summary>
    public long? NonLiveFileSize { get; set; }

    /// <summary>The earliest removal time of the non-live files. Null when none of their remove actions has a timestamp.</summary>
    public DateTimeOffset? NonLiveOldestRemovedAt { get; set; }

    /// <summary>The latest removal time of the non-live files.</summary>
    public DateTimeOffset? NonLiveNewestRemovedAt { get; set; }

    /// <summary>The data files found under the table location that the version described does not reference. They include the non-live files that are still stored.</summary>
    public long? UnreferencedFileCount { get; set; }

    /// <summary>The sum of the sizes of the unreferenced files, as the listing gives them. A file whose size the listing does not give counts in <see cref="UnreferencedFileCount"/> and not here.</summary>
    public long? UnreferencedFileSize { get; set; }

    /// <summary>The files of the table format itself: the Delta log, or the Iceberg metadata and manifests.</summary>
    public long? MetadataFileCount { get; set; }

    /// <summary>The size of the files of the table format itself.</summary>
    public long? MetadataSize { get; set; }

    /// <summary>The files whose footer was read.</summary>
    public long? FooterCount { get; set; }

    /// <summary>The row groups of the files whose footer was read.</summary>
    public long? RowGroupCount { get; set; }

    /// <summary>The sum of the compressed sizes of the column chunks.</summary>
    public long? CompressedSize { get; set; }

    /// <summary>The sum of the uncompressed sizes of the column chunks.</summary>
    public long? UncompressedSize { get; set; }

    /// <summary>The sum of the footer sizes.</summary>
    public long? FooterSize { get; set; }

    /// <summary>The compressed size of the physical columns that the table no longer declares.</summary>
    public long? StaleColumnSize { get; set; }

    /// <summary><c>Footers</c> or <c>Table format</c>: where the totals of the columns come from.</summary>
    public string? ColumnTotalsSource { get; set; }

    /// <summary>The sizes of the data files.</summary>
    public Distribution? FileSizes { get; set; }

    /// <summary>The rows of the row groups.</summary>
    public Distribution? RowGroupRowCounts { get; set; }

    /// <summary>The data size of the partitions.</summary>
    public Distribution? PartitionSizes { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(TableRow? other) => Schemas.Tables.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as TableRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.Tables.Hash(this);
}
