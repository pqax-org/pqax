using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>An <c>add</c> action: a data file that joins the table.</summary>
public sealed class AddAction : DeltaAction
{
    /// <summary>The path of the file as the log has it: relative to the table and URI-encoded, or absolute.</summary>
    public string? Path { get; set; }

    /// <summary>The partition values of the file, by partition column. A value is null when the partition value is null.</summary>
    public IReadOnlyDictionary<string, string?>? PartitionValues { get; set; }

    /// <summary>The size of the file in bytes.</summary>
    public long? Size { get; set; }

    /// <summary>When the file was written, in milliseconds since 1970.</summary>
    public long? ModificationTime { get; set; }

    /// <summary>
    /// The statistics of the file as JSON text. The collector reads <c>numRecords</c> and <c>nullCount</c> from it and
    /// drops the rest, which holds data values.
    /// </summary>
    public string? Stats { get; set; }

    /// <summary>The rows of the file, when a checkpoint gives the statistics as a struct instead of <see cref="Stats"/>.</summary>
    public long? NumRecords { get; set; }

    /// <summary>The null values of each top-level column, when a checkpoint gives the statistics as a struct instead of <see cref="Stats"/>.</summary>
    public IReadOnlyDictionary<string, long>? NullCounts { get; set; }

    /// <summary>The tags of the file, such as <c>VORDER</c>.</summary>
    public IReadOnlyDictionary<string, string?>? Tags { get; set; }

    /// <summary>The deletion vector of the file, when it has one.</summary>
    public DeletionVectorDescriptor? DeletionVector { get; set; }

    /// <summary>The clustering implementation that wrote the file.</summary>
    public string? ClusteringProvider { get; set; }
}
