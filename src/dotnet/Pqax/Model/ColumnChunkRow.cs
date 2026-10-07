using System;
using System.Collections.Generic;

namespace Pqax;

/// <summary>A row of <c>columnChunks</c>: the values of one physical column in one row group.</summary>
public sealed class ColumnChunkRow : IEquatable<ColumnChunkRow>
{
    /// <summary>The <c>id</c> of the row group of the chunk. Required.</summary>
    public long? RowGroup { get; set; }

    /// <summary>The <c>id</c> of the physical column that the chunk stores. Required.</summary>
    public long? PhysicalColumn { get; set; }

    /// <summary>The codec, such as <c>SNAPPY</c> or <c>ZSTD</c>.</summary>
    public string? Codec { get; set; }

    /// <summary>The encodings used in the chunk.</summary>
    public IReadOnlyList<string>? Encodings { get; set; }

    /// <summary>The values, including nulls.</summary>
    public long? ValueCount { get; set; }

    /// <summary>The null values.</summary>
    public long? NullCount { get; set; }

    /// <summary>The size of all pages, with their headers and the dictionary page. Required.</summary>
    public long? CompressedSize { get; set; }

    /// <summary>The same size before compression. Required.</summary>
    public long? UncompressedSize { get; set; }

    /// <summary>Whether the chunk has a dictionary page.</summary>
    public bool? HasDictionaryPage { get; set; }

    /// <summary>The compressed size of the dictionary page.</summary>
    public long? DictionaryPageSize { get; set; }

    /// <summary>The data pages of the chunk.</summary>
    public long? DataPageCount { get; set; }

    /// <summary>The data pages that use the dictionary.</summary>
    public long? DictionaryEncodedPageCount { get; set; }

    /// <summary>The bytes of the variable-length values before encoding.</summary>
    public long? UnencodedByteArrayDataBytes { get; set; }

    /// <summary>The size of the bloom filter.</summary>
    public long? BloomFilterSize { get; set; }

    /// <summary>The lower bound of the values, present only when the collection includes data values.</summary>
    public string? MinValue { get; set; }

    /// <summary>The upper bound of the values, present only when the collection includes data values.</summary>
    public string? MaxValue { get; set; }

    /// <summary>The distinct values in the chunk, not counting nulls.</summary>
    public long? DistinctCount { get; set; }

    /// <summary>The entries in the dictionary.</summary>
    public long? DictionaryEntryCount { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(ColumnChunkRow? other) => Schemas.ColumnChunks.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ColumnChunkRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.ColumnChunks.Hash(this);
}
