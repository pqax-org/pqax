namespace Pqax.Collector;

/// <summary>A <c>domainMetadata</c> action: the configuration of a metadata domain, such as <c>delta.clustering</c>.</summary>
public sealed class DomainMetadataAction : DeltaAction
{
    /// <summary>The name of the domain.</summary>
    public string? Domain { get; set; }

    /// <summary>The configuration of the domain, as text that is usually JSON.</summary>
    public string? Configuration { get; set; }

    /// <summary>Whether the action removes the domain.</summary>
    public bool Removed { get; set; }
}
