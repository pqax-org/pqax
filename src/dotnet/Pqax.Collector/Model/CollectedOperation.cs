using System;

namespace Pqax.Collector;

/// <summary>One kind of operation in the history read, with how often and how recently it ran.</summary>
internal sealed class CollectedOperation
{
    public CollectedOperation(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public long CommitCount { get; set; }

    public DateTimeOffset? LastAt { get; set; }
}
