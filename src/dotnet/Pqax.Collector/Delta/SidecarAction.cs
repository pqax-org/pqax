namespace Pqax.Collector;

/// <summary>A <c>sidecar</c> action of a v2 checkpoint: a file in <c>_delta_log/_sidecars</c> that holds more file actions.</summary>
public sealed class SidecarAction : DeltaAction
{
    /// <summary>The path of the sidecar file, relative to <c>_delta_log/_sidecars</c>.</summary>
    public string? Path { get; set; }

    /// <summary>The size of the sidecar file in bytes.</summary>
    public long? SizeInBytes { get; set; }
}
