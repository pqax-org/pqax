namespace Pqax.Collector;

/// <summary>A <c>remove</c> action: a data file that leaves the table. The log keeps it as a tombstone for a while.</summary>
public sealed class RemoveAction : DeltaAction
{
    /// <summary>The path of the file as the log has it.</summary>
    public string? Path { get; set; }

    /// <summary>The size of the file in bytes, when the action records it.</summary>
    public long? Size { get; set; }

    /// <summary>When the file was removed, in milliseconds since 1970.</summary>
    public long? DeletionTimestamp { get; set; }

    /// <summary>The deletion vector of the file that is removed, which is part of its identity.</summary>
    public DeletionVectorDescriptor? DeletionVector { get; set; }
}
