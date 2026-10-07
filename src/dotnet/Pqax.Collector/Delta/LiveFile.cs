using System;
using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A data file of the version of the table that the log describes, with what the log says about it.</summary>
internal sealed class LiveFile
{
    public LiveFile(string rawPath, string path)
    {
        RawPath = rawPath;
        Path = path;
    }

    /// <summary>The path as the log has it, URI-encoded.</summary>
    public string RawPath { get; }

    /// <summary>The path decoded: relative to the table when the file is inside it, absolute otherwise.</summary>
    public string Path { get; }

    public long? Size { get; set; }

    /// <summary>When the file was written, in milliseconds since 1970.</summary>
    public long? ModificationTime { get; set; }

    /// <summary>The rows of the file, from the statistics of the log.</summary>
    public long? NumRecords { get; set; }

    /// <summary>The null values of the top-level columns, by the name that the files use, from the statistics of the log.</summary>
    public KeyValuePair<string, long>[]? NullCounts { get; set; }

    public IReadOnlyDictionary<string, string?>? PartitionValues { get; set; }

    /// <summary>What the <c>VORDER</c> tag of the file says, when it has one.</summary>
    public bool? VOrderTag { get; set; }

    /// <summary>The rows marked as deleted: the cardinality of the deletion vector, 0 without one.</summary>
    public long? DeletedRowCount { get; set; }

    public long? DeletionVectorSize { get; set; }

    public string? ClusteringProvider { get; set; }

    /// <summary>Whether the path is outside the table folder: absolute, with a scheme or a leading slash.</summary>
    public bool IsOutsideTable => Path.IndexOf("://", StringComparison.Ordinal) >= 0 || Path.StartsWith("/", StringComparison.Ordinal);
}
