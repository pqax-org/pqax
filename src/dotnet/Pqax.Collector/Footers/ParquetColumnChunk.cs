using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>
/// A column chunk of a row group, as its footer describes it: the <c>ColumnChunk</c> and its <c>ColumnMetaData</c>.
/// The bounds of the statistics are kept only when the footer is parsed with data values; otherwise the parser skips
/// them without reading them, as it skips <c>path_in_schema</c>, which the schema already gives.
/// </summary>
public sealed class ParquetColumnChunk
{
    private static readonly IReadOnlyList<string> NoEncodings = new string[0];

    internal ParquetColumnChunk()
    {
        Encodings = NoEncodings;
    }

    /// <summary>Whether the chunk has its <c>ColumnMetaData</c>. A chunk of an encrypted column may have none, and then its sizes are unknown.</summary>
    public bool HasMetadata { get; internal set; }

    /// <summary>Whether the column is encrypted: the chunk has crypto metadata or encrypted column metadata.</summary>
    public bool IsEncrypted { get; internal set; }

    /// <summary>The file that holds the chunk when it is not the file of the footer. Almost never set.</summary>
    public string? FilePath { get; internal set; }

    /// <summary>The physical type that the metadata states.</summary>
    public string? PhysicalType { get; internal set; }

    /// <summary>The codec, such as <c>SNAPPY</c> or <c>ZSTD</c>.</summary>
    public string? Codec { get; internal set; }

    /// <summary>The encodings that the chunk uses, in the order of the footer.</summary>
    public IReadOnlyList<string> Encodings { get; internal set; }

    /// <summary>The values, including nulls.</summary>
    public long NumValues { get; internal set; }

    /// <summary>The size of all the pages before compression, with their headers.</summary>
    public long TotalUncompressedSize { get; internal set; }

    /// <summary>The size of all the pages, with their headers and the dictionary page.</summary>
    public long TotalCompressedSize { get; internal set; }

    /// <summary>The position of the first data page.</summary>
    public long DataPageOffset { get; internal set; }

    /// <summary>The position of the index page, when there is one.</summary>
    public long? IndexPageOffset { get; internal set; }

    /// <summary>The position of the dictionary page, when there is one.</summary>
    public long? DictionaryPageOffset { get; internal set; }

    /// <summary>Whether the metadata has statistics.</summary>
    public bool HasStatistics { get; internal set; }

    /// <summary>The null values, from the statistics.</summary>
    public long? NullCount { get; internal set; }

    /// <summary>The distinct values, not counting nulls, from the statistics.</summary>
    public long? DistinctCount { get; internal set; }

    /// <summary>Whether the statistics have bounds. The bounds themselves are data values.</summary>
    public bool HasMinMax { get; internal set; }

    /// <summary>The lower bound as text. Set only when the footer is parsed with data values.</summary>
    public string? MinValue { get; internal set; }

    /// <summary>The upper bound as text. Set only when the footer is parsed with data values.</summary>
    public string? MaxValue { get; internal set; }

    /// <summary>The pages by kind and encoding. Null when the footer has no encoding statistics.</summary>
    public IReadOnlyList<ParquetPageEncodingStats>? EncodingStats { get; internal set; }

    /// <summary>The position of the bloom filter.</summary>
    public long? BloomFilterOffset { get; internal set; }

    /// <summary>The size of the bloom filter.</summary>
    public int? BloomFilterLength { get; internal set; }

    /// <summary>The bytes of the variable-length values before encoding, from the size statistics.</summary>
    public long? UnencodedByteArrayDataBytes { get; internal set; }

    /// <summary>The position of the offset index.</summary>
    public long? OffsetIndexOffset { get; internal set; }

    /// <summary>The size of the offset index.</summary>
    public int? OffsetIndexLength { get; internal set; }

    /// <summary>The position of the column index.</summary>
    public long? ColumnIndexOffset { get; internal set; }

    /// <summary>The size of the column index.</summary>
    public int? ColumnIndexLength { get; internal set; }

    /// <summary>
    /// Whether the chunk has a dictionary page: the footer gives its position, the encoding statistics count a
    /// dictionary page, or the encodings include a dictionary encoding, which some older writers list without the
    /// position.
    /// </summary>
    public bool HasDictionaryPage
    {
        get
        {
            if (DictionaryPageOffset is long offset && offset > 0)
            {
                return true;
            }

            if (EncodingStats != null)
            {
                foreach (ParquetPageEncodingStats stats in EncodingStats)
                {
                    if (stats.PageType == "DICTIONARY_PAGE" && stats.Count > 0)
                    {
                        return true;
                    }
                }
            }

            foreach (string encoding in Encodings)
            {
                if (encoding == "PLAIN_DICTIONARY" || encoding == "RLE_DICTIONARY")
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The compressed size of the dictionary page, with its header: the position of the first data page minus the
    /// position of the dictionary page. Null when the footer does not give both, or when they are not in that order.
    /// </summary>
    public long? DictionaryPageSize =>
        DictionaryPageOffset is long dictionary && dictionary > 0 && dictionary < DataPageOffset ? DataPageOffset - dictionary : (long?)null;
}
