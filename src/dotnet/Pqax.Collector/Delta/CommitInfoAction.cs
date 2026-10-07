using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>
/// A <c>commitInfo</c> action: what a commit did. The collector never reads the user names, notebook identifiers, or
/// cluster identifiers that it can carry, and it reads the parameters of the operation only when the collection
/// includes data values, because they can hold predicates.
/// </summary>
public sealed class CommitInfoAction : DeltaAction
{
    /// <summary>When the commit was made, in milliseconds since 1970.</summary>
    public long? Timestamp { get; set; }

    /// <summary>The commit time that the in-commit timestamps feature records, in milliseconds since 1970.</summary>
    public long? InCommitTimestamp { get; set; }

    /// <summary>The kind of operation, such as <c>WRITE</c> or <c>OPTIMIZE</c>.</summary>
    public string? Operation { get; set; }

    /// <summary>The engine that committed.</summary>
    public string? EngineInfo { get; set; }

    /// <summary>Whether the commit only added rows.</summary>
    public bool? IsBlindAppend { get; set; }

    /// <summary>The counters that the engine recorded. Null when the commit has none.</summary>
    public IReadOnlyDictionary<string, string>? OperationMetrics { get; set; }

    /// <summary>The parameters of the operation. Set only when the collection includes data values.</summary>
    public IReadOnlyDictionary<string, string>? OperationParameters { get; set; }
}
