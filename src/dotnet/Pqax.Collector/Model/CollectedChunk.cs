using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A column chunk kept at the <c>Column chunks</c> depth.</summary>
internal sealed class CollectedChunk
{
    public CollectedChunk(int leafOrdinal, IReadOnlyList<string> encodings)
    {
        LeafOrdinal = leafOrdinal;
        Encodings = encodings;
    }

    /// <summary>The ordinal of the leaf in the physical schema of the file.</summary>
    public int LeafOrdinal { get; }

    public string? Codec { get; set; }

    /// <summary>The encodings, sorted.</summary>
    public IReadOnlyList<string> Encodings { get; }

    public long ValueCount { get; set; }

    public long? NullCount { get; set; }

    public long CompressedSize { get; set; }

    public long UncompressedSize { get; set; }

    public bool HasDictionaryPage { get; set; }

    public long? DictionaryPageSize { get; set; }

    public long? DataPageCount { get; set; }

    public long? DictionaryEncodedPageCount { get; set; }

    public long? UnencodedByteArrayDataBytes { get; set; }

    public long? BloomFilterSize { get; set; }

    public string? MinValue { get; set; }

    public string? MaxValue { get; set; }

    public long? DistinctCount { get; set; }
}
