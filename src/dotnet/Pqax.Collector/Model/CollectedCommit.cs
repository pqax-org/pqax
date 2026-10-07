using System;
using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A commit kept in the history of a table.</summary>
internal sealed class CollectedCommit
{
    public CollectedCommit(string commitId)
    {
        CommitId = commitId;
    }

    public string CommitId { get; }

    public DateTimeOffset? Timestamp { get; set; }

    public string? Operation { get; set; }

    public string? Engine { get; set; }

    public bool? IsBlindAppend { get; set; }

    public IReadOnlyDictionary<string, string>? Metrics { get; set; }

    public IReadOnlyDictionary<string, string>? Parameters { get; set; }
}
