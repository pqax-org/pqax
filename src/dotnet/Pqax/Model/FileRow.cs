using System;

namespace Pqax;

/// <summary>A row of <c>files</c>: a data file that belongs to a table at the version described.</summary>
public sealed class FileRow : IEquatable<FileRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The <c>id</c> of the table of the file. Required.</summary>
    public long? Table { get; set; }

    /// <summary>The path, relative to the table location when the file is inside it, complete otherwise.</summary>
    public string? Path { get; set; }

    /// <summary><c>Parquet</c>, <c>ORC</c>, or <c>Avro</c>.</summary>
    public string? Format { get; set; }

    /// <summary>The size of the file.</summary>
    public long? FileSize { get; set; }

    /// <summary>When the file was written.</summary>
    public DateTimeOffset? ModifiedAt { get; set; }

    /// <summary>The rows stored in the file, including rows marked as deleted.</summary>
    public long? RowCount { get; set; }

    /// <summary>The rows marked as deleted. Zero when the table format says there are none, null when it cannot say.</summary>
    public long? DeletedRowCount { get; set; }

    /// <summary>The size of the deletion vector.</summary>
    public long? DeletionVectorSize { get; set; }

    /// <summary>The <c>id</c> of the partition of the file.</summary>
    public long? Partition { get; set; }

    /// <summary>The <c>id</c> of the sort order the file was written with.</summary>
    public long? SortOrder { get; set; }

    /// <summary>The clustering implementation that wrote the file.</summary>
    public string? ClusteringProvider { get; set; }

    /// <summary>Whether a file tag or the footer marks the file as V-Ordered.</summary>
    public bool? IsVOrdered { get; set; }

    /// <summary>The size of the footer. Present when the footer was read.</summary>
    public long? FooterSize { get; set; }

    /// <summary>The <c>id</c> of the physical schema of the file.</summary>
    public long? PhysicalSchema { get; set; }

    /// <summary>The <c>id</c> of the writer of the file.</summary>
    public long? Writer { get; set; }

    /// <summary>The row groups of the file.</summary>
    public long? RowGroupCount { get; set; }

    /// <summary>The sum of the compressed sizes of the column chunks of the file.</summary>
    public long? CompressedSize { get; set; }

    /// <summary>The sum of the uncompressed sizes of the column chunks of the file.</summary>
    public long? UncompressedSize { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(FileRow? other) => Schemas.Files.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as FileRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.Files.Hash(this);
}
