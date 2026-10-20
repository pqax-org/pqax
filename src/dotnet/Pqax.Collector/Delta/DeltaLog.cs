using System;
using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>What the Delta log of a table says: the state of its last version, the history read, and the log itself.</summary>
internal sealed class DeltaLog
{
    /// <summary>Whether the table has a log at all.</summary>
    public bool Found { get; set; }

    /// <summary>
    /// Whether <see cref="Files"/> is the complete set of data files of the version. False when the checkpoint or a commit
    /// that the state needs was not read: the totals over the files are then unknown.
    /// </summary>
    public bool FilesComplete { get; set; }

    public long? Version { get; set; }

    public DateTimeOffset? SnapshotTimestamp { get; set; }

    public MetadataAction? Metadata { get; set; }

    public ProtocolAction? Protocol { get; set; }

    /// <summary>The active metadata domains, with their configuration, by name.</summary>
    public SortedDictionary<string, string> Domains { get; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

    public long? CheckpointVersion { get; set; }

    /// <summary>The commit files newer than the checkpoint.</summary>
    public long? CommitsAfterCheckpoint { get; set; }

    /// <summary>The commit files in the log.</summary>
    public long CommitCount { get; set; }

    /// <summary>The files of the log, in <c>_delta_log</c> and <c>_delta_log/_sidecars</c>.</summary>
    public long MetadataFileCount { get; set; }

    /// <summary>Their size. Null when the listing does not give a size.</summary>
    public long? MetadataSize { get; set; }

    /// <summary>The data files, sorted by modification time and path.</summary>
    public List<LiveFile> Files { get; } = new List<LiveFile>();

    /// <summary>Files that left the table and that the log still tracks.</summary>
    public long? NonLiveFileCount { get; set; }

    public long? NonLiveFileSize { get; set; }

    /// <summary>
    /// Every commit read, in ascending order of version: those that the state needs, and the older ones read for the
    /// history up to the limit. A commit whose read failed is not here.
    /// </summary>
    public List<CommitRecord> History { get; } = new List<CommitRecord>();
}
