using System;
using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A table to collect: where it is in the storage, and the source and names that the footprint records.</summary>
public sealed class TableLocation
{
    private static readonly IReadOnlyList<string> NoNamespace = new string[0];

    /// <summary>Creates a location.</summary>
    /// <param name="sourceKind">The kind of the source, such as <see cref="SourceKinds.OneLakeLakehouse"/>.</param>
    /// <param name="sourceName">The name of the lakehouse, warehouse, or folder.</param>
    /// <param name="namespace">The schema or namespace path of the table, empty for none.</param>
    /// <param name="name">The table name.</param>
    /// <param name="path">The folder of the table in the storage, the one that holds <c>_delta_log</c>.</param>
    /// <param name="location">The root location to record for the table, such as its URL. Null to record none.</param>
    public TableLocation(string sourceKind, string sourceName, IReadOnlyList<string>? @namespace, string name, string path, string? location = null)
    {
        SourceKind = sourceKind ?? throw new ArgumentNullException(nameof(sourceKind));
        SourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
        Namespace = @namespace ?? NoNamespace;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Path = (path ?? throw new ArgumentNullException(nameof(path))).Trim('/');
        Location = location;
    }

    /// <summary>The kind of the source, such as <see cref="SourceKinds.OneLakeLakehouse"/>.</summary>
    public string SourceKind { get; }

    /// <summary>The name of the source: the lakehouse, warehouse, or folder.</summary>
    public string SourceName { get; }

    /// <summary>The schema or namespace path of the table. Empty for a table outside any schema.</summary>
    public IReadOnlyList<string> Namespace { get; }

    /// <summary>The table name.</summary>
    public string Name { get; }

    /// <summary>The folder of the table in the storage, relative to its root.</summary>
    public string Path { get; }

    /// <summary>The root location that the footprint records for the table. Null to record none.</summary>
    public string? Location { get; }

    /// <inheritdoc/>
    public override string ToString() => Path;
}
