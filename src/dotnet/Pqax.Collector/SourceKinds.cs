namespace Pqax.Collector;

/// <summary>The kinds of source that <see cref="TableDiscovery"/> assigns, as the object model spells them.</summary>
public static class SourceKinds
{
    /// <summary>A Fabric lakehouse.</summary>
    public const string OneLakeLakehouse = "OneLake lakehouse";

    /// <summary>A Fabric warehouse.</summary>
    public const string OneLakeWarehouse = "OneLake warehouse";

    /// <summary>A Fabric mirrored database.</summary>
    public const string OneLakeMirroredDatabase = "OneLake mirrored database";

    /// <summary>A Fabric SQL database.</summary>
    public const string OneLakeSqlDatabase = "OneLake SQL database";

    /// <summary>Another OneLake item with a <c>Tables</c> folder, or an item addressed by its id.</summary>
    public const string OneLakeItem = "OneLake item";

    /// <summary>A storage folder that holds tables.</summary>
    public const string StorageFolder = "Storage folder";
}
