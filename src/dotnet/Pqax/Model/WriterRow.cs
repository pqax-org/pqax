using System;

namespace Pqax;

/// <summary>A row of <c>writers</c>: one combination of writing engine and optimization found in the files of a table.</summary>
public sealed class WriterRow : IEquatable<WriterRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The <c>id</c> of the table of the writer. Required.</summary>
    public long? Table { get; set; }

    /// <summary>The writer, as the footer names it.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>True when the footer metadata or a file tag marks the files as V-Ordered.</summary>
    public bool? IsVOrdered { get; set; }

    /// <summary>The V-Order level that the footer records.</summary>
    public string? VOrderLevel { get; set; }

    /// <summary>The files written this way.</summary>
    public long? FileCount { get; set; }

    /// <summary>The size of the files written this way.</summary>
    public long? FileSize { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(WriterRow? other) => Schemas.Writers.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as WriterRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.Writers.Hash(this);
}
