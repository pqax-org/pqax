namespace Pqax.Collector;

/// <summary>A <c>checkpointMetadata</c> action of a v2 checkpoint.</summary>
public sealed class CheckpointMetadataAction : DeltaAction
{
    /// <summary>The version of the table that the checkpoint describes.</summary>
    public long? Version { get; set; }
}
