using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>What only a Delta table has: its protocol, its features, and its checkpoint.</summary>
internal sealed class CollectedDelta
{
    public long? MinReaderVersion { get; set; }

    public long? MinWriterVersion { get; set; }

    public IReadOnlyList<string>? ReaderFeatures { get; set; }

    public IReadOnlyList<string>? WriterFeatures { get; set; }

    public long? CheckpointVersion { get; set; }

    public long? CommitsAfterCheckpoint { get; set; }

    public IReadOnlyList<string>? Domains { get; set; }
}
