using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A row group kept from the <c>Row groups</c> depth.</summary>
internal sealed class CollectedRowGroup
{
    public long Ordinal { get; set; }

    public long RowCount { get; set; }

    public long UncompressedSize { get; set; }

    public long CompressedSize { get; set; }

    /// <summary>The column chunks, kept at the <c>Column chunks</c> depth only.</summary>
    public List<CollectedChunk>? Chunks { get; set; }
}
