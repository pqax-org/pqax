namespace Pqax.Collector;

/// <summary>How many pages of a kind a column chunk has with an encoding, from the <c>encoding_stats</c> of the footer.</summary>
public sealed class ParquetPageEncodingStats
{
    internal ParquetPageEncodingStats(string pageType, string encoding, int count)
    {
        PageType = pageType;
        Encoding = encoding;
        Count = count;
    }

    /// <summary><c>DATA_PAGE</c>, <c>DATA_PAGE_V2</c>, <c>DICTIONARY_PAGE</c>, or <c>INDEX_PAGE</c>.</summary>
    public string PageType { get; }

    /// <summary>The encoding of those pages.</summary>
    public string Encoding { get; }

    /// <summary>The number of pages.</summary>
    public int Count { get; }

    /// <summary>Whether the pages are data pages.</summary>
    public bool IsDataPage => PageType == "DATA_PAGE" || PageType == "DATA_PAGE_V2";

    /// <summary>Whether the pages use the dictionary.</summary>
    public bool IsDictionaryEncoded => Encoding == "PLAIN_DICTIONARY" || Encoding == "RLE_DICTIONARY";

    /// <inheritdoc/>
    public override string ToString() => PageType + ":" + Encoding + ":" + Count;
}
