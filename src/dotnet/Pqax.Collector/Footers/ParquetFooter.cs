using System;
using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>
/// The footer of a Parquet file: its <c>FileMetaData</c>, decoded from the Thrift compact protocol by the parser of
/// this library. It keeps what a footprint uses and drops the rest without allocating for it: the bounds of the
/// statistics unless asked, the values of the footer keys outside the allow-list of <see cref="ParquetKeyValue"/>,
/// the paths of the column chunks, the level histograms, and the encryption details.
/// </summary>
public sealed class ParquetFooter
{
    internal ParquetFooter(
        int version,
        long numRows,
        string? createdBy,
        IReadOnlyList<ParquetKeyValue> keyValueMetadata,
        IReadOnlyList<ParquetSchemaElement> schema,
        IReadOnlyList<ParquetLeafColumn> leaves,
        IReadOnlyList<ParquetRowGroup> rowGroups,
        bool hasEncryptionAlgorithm)
    {
        Version = version;
        NumRows = numRows;
        CreatedBy = createdBy;
        KeyValueMetadata = keyValueMetadata;
        Schema = schema;
        Leaves = leaves;
        RowGroups = rowGroups;
        HasEncryptionAlgorithm = hasEncryptionAlgorithm;
        foreach (ParquetRowGroup group in rowGroups)
        {
            foreach (ParquetColumnChunk chunk in group.Columns)
            {
                if (chunk.IsEncrypted)
                {
                    HasEncryptedColumns = true;
                }
            }
        }
    }

    /// <summary>The length of the encoded <c>FileMetaData</c> in bytes, as the four bytes before the closing magic state it. Zero when the footer was parsed from bytes alone.</summary>
    public int FooterLength { get; internal set; }

    /// <summary>The storage requests that reading the footer took: 1 in the common case, 2 for a footer longer than the first read. Zero when the footer was parsed from bytes alone.</summary>
    public int RequestCount { get; internal set; }

    /// <summary>The version of the Parquet format that the writer states.</summary>
    public int Version { get; }

    /// <summary>The rows of the file.</summary>
    public long NumRows { get; }

    /// <summary>The writer, as the footer names it.</summary>
    public string? CreatedBy { get; }

    /// <summary>The keys of the key-value metadata, with a value only for the keys of the allow-list.</summary>
    public IReadOnlyList<ParquetKeyValue> KeyValueMetadata { get; }

    /// <summary>The elements of the schema, in the depth-first order of the file, the root first.</summary>
    public IReadOnlyList<ParquetSchemaElement> Schema { get; }

    /// <summary>The leaf columns, in the order in which row groups store their column chunks.</summary>
    public IReadOnlyList<ParquetLeafColumn> Leaves { get; }

    /// <summary>The row groups.</summary>
    public IReadOnlyList<ParquetRowGroup> RowGroups { get; }

    /// <summary>Whether the footer states an encryption algorithm: a file with encrypted columns and a footer in plain text.</summary>
    public bool HasEncryptionAlgorithm { get; }

    /// <summary>Whether a column chunk is encrypted, so that its metadata may be missing.</summary>
    public bool HasEncryptedColumns { get; }

    /// <summary>Parses an encoded <c>FileMetaData</c>.</summary>
    /// <param name="metadata">The bytes of the <c>FileMetaData</c>, without the length and the magic that follow it in a file.</param>
    /// <param name="includeDataValues">Whether to keep the bounds of the statistics, as text.</param>
    /// <returns>The footer.</returns>
    /// <exception cref="ParquetFooterException">The bytes are not a valid footer.</exception>
    public static ParquetFooter Parse(byte[] metadata, bool includeDataValues = false)
    {
        if (metadata == null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        return FooterParser.Parse(metadata, 0, metadata.Length, includeDataValues);
    }

    /// <summary>Parses an encoded <c>FileMetaData</c> inside a buffer.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">The position of the first byte of the <c>FileMetaData</c>.</param>
    /// <param name="count">Its length.</param>
    /// <param name="includeDataValues">Whether to keep the bounds of the statistics, as text.</param>
    /// <returns>The footer.</returns>
    /// <exception cref="ParquetFooterException">The bytes are not a valid footer.</exception>
    public static ParquetFooter Parse(byte[] buffer, int offset, int count, bool includeDataValues = false)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (offset < 0 || count < 0 || offset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "The range is outside the buffer.");
        }

        return FooterParser.Parse(buffer, offset, count, includeDataValues);
    }
}
