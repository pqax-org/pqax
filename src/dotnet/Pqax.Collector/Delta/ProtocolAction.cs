using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A <c>protocol</c> action: the versions and the features that a reader and a writer need.</summary>
public sealed class ProtocolAction : DeltaAction
{
    /// <summary>The minimum reader version.</summary>
    public long? MinReaderVersion { get; set; }

    /// <summary>The minimum writer version.</summary>
    public long? MinWriterVersion { get; set; }

    /// <summary>The reader features, from reader version 3.</summary>
    public IReadOnlyList<string>? ReaderFeatures { get; set; }

    /// <summary>The writer features, from writer version 7.</summary>
    public IReadOnlyList<string>? WriterFeatures { get; set; }
}
