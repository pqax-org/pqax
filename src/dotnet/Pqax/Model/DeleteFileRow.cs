using System;
using System.Collections.Generic;

namespace Pqax;

/// <summary>A row of <c>deleteFiles</c>: a file that marks rows of data files as deleted, as Iceberg tables have.</summary>
public sealed class DeleteFileRow : IEquatable<DeleteFileRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The <c>id</c> of the table of the delete file. Required.</summary>
    public long? Table { get; set; }

    /// <summary><c>Position deletes</c>, <c>Equality deletes</c>, or <c>Deletion vector</c>. Required.</summary>
    public string? Kind { get; set; }

    /// <summary>The location of the file.</summary>
    public string? Path { get; set; }

    /// <summary><c>Parquet</c>, <c>ORC</c>, <c>Avro</c>, or <c>Puffin</c>.</summary>
    public string? Format { get; set; }

    /// <summary>The size of the file, or of the deletion vector inside it.</summary>
    public long? FileSize { get; set; }

    /// <summary>The delete records, or the rows that a deletion vector removes.</summary>
    public long? RecordCount { get; set; }

    /// <summary>The <c>id</c> of the data file, when the delete file applies to one file only.</summary>
    public long? ReferencedFile { get; set; }

    /// <summary>The <c>id</c> of the columns that an equality delete compares.</summary>
    public IReadOnlyList<long>? EqualityColumns { get; set; }

    /// <summary>The <c>id</c> of the partition of the delete file.</summary>
    public long? Partition { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(DeleteFileRow? other) => Schemas.DeleteFiles.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as DeleteFileRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.DeleteFiles.Hash(this);
}
