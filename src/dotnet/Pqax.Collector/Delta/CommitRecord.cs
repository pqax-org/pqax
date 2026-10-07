using System;

namespace Pqax.Collector;

/// <summary>A commit that the collector read for the history of a table.</summary>
internal sealed class CommitRecord
{
    public CommitRecord(long version, CommitInfoAction? info, DateTimeOffset? fileTime)
    {
        Version = version;
        Info = info;
        long? milliseconds = info?.InCommitTimestamp ?? info?.Timestamp;
        Timestamp = milliseconds != null ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value) : fileTime;
    }

    public long Version { get; }

    /// <summary>The <c>commitInfo</c> of the commit. Null when the commit has none.</summary>
    public CommitInfoAction? Info { get; }

    /// <summary>The in-commit timestamp, else the timestamp of the commit information, else the modification time of the commit file.</summary>
    public DateTimeOffset? Timestamp { get; }
}
