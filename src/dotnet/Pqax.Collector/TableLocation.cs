using System;
using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A table to collect: where it is in the storage, and the source and names that the footprint records.</summary>
/// <remarks>
/// The tables with the same source kind, source name, and source location share one source in the footprint, which
/// takes its identifiers from the first of them. <see cref="TableDiscovery"/> fills the locations and the identifiers
/// for <see cref="LocalStorage"/> and <see cref="OneLakeStorage"/>, as the README of the collector tells.
/// </remarks>
public sealed class TableLocation
{
    private static readonly IReadOnlyList<string> NoNamespace = new string[0];

    /// <summary>Creates the location of a Delta table.</summary>
    /// <param name="sourceKind">The kind of the source, such as <see cref="SourceKinds.OneLakeLakehouse"/>.</param>
    /// <param name="sourceName">The name of the lakehouse, warehouse, or folder.</param>
    /// <param name="namespace">The schema or namespace path of the table, empty for none.</param>
    /// <param name="name">The table name.</param>
    /// <param name="path">The folder of the table in the storage, the one that holds <c>_delta_log</c>.</param>
    /// <param name="location">The root location to record for the table, such as its URL. Null to record none.</param>
    /// <param name="sourceLocation">The base location to record for the source, such as the URL of the lakehouse. Null to record none.</param>
    /// <param name="sourceIdentifiers">The identifiers that the platform assigns to the source, such as <c>workspaceId</c> and <c>itemId</c>. Null to record none. The location keeps a copy.</param>
    public TableLocation(
        string sourceKind,
        string sourceName,
        IReadOnlyList<string>? @namespace,
        string name,
        string path,
        string? location = null,
        string? sourceLocation = null,
        IReadOnlyDictionary<string, string>? sourceIdentifiers = null)
        : this(sourceKind, sourceName, @namespace, name, path, location, sourceLocation, sourceIdentifiers, TableFormats.Delta)
    {
    }

    /// <summary>Creates the location of a table of a given format.</summary>
    /// <param name="sourceKind">The kind of the source, such as <see cref="SourceKinds.StorageFolder"/>.</param>
    /// <param name="sourceName">The name of the lakehouse, warehouse, or folder.</param>
    /// <param name="namespace">The schema or namespace path of the table, empty for none.</param>
    /// <param name="name">The table name.</param>
    /// <param name="path">The folder of the table in the storage: the one that holds <c>_delta_log</c>, or the Parquet files.</param>
    /// <param name="location">The root location to record for the table, such as its URL. Null to record none.</param>
    /// <param name="sourceLocation">The base location to record for the source, such as the URL of the folder. Null to record none.</param>
    /// <param name="sourceIdentifiers">The identifiers that the platform assigns to the source. Null to record none. The location keeps a copy.</param>
    /// <param name="format"><see cref="TableFormats.Delta"/> or <see cref="TableFormats.Parquet"/>.</param>
    /// <exception cref="ArgumentException">The format is not one that the collector reads.</exception>
    public TableLocation(
        string sourceKind,
        string sourceName,
        IReadOnlyList<string>? @namespace,
        string name,
        string path,
        string? location,
        string? sourceLocation,
        IReadOnlyDictionary<string, string>? sourceIdentifiers,
        string format)
    {
        if (format != TableFormats.Delta && format != TableFormats.Parquet)
        {
            throw new ArgumentException("The format is Delta or Parquet.", nameof(format));
        }

        Format = format;
        SourceKind = sourceKind ?? throw new ArgumentNullException(nameof(sourceKind));
        SourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
        Namespace = @namespace ?? NoNamespace;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Path = (path ?? throw new ArgumentNullException(nameof(path))).Trim('/');
        Location = location;
        SourceLocation = sourceLocation;
        if (sourceIdentifiers != null)
        {
            var identifiers = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> identifier in sourceIdentifiers)
            {
                identifiers[identifier.Key] = identifier.Value ?? throw new ArgumentException("An identifier has no value.", nameof(sourceIdentifiers));
            }

            SourceIdentifiers = identifiers;
        }
    }

    /// <summary>The format of the table: <see cref="TableFormats.Delta"/> or <see cref="TableFormats.Parquet"/>.</summary>
    public string Format { get; }

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

    /// <summary>The base location that the footprint records for the source of the table. Null to record none.</summary>
    public string? SourceLocation { get; }

    /// <summary>The identifiers that the footprint records for the source of the table, sorted by key. Null to record none.</summary>
    public IReadOnlyDictionary<string, string>? SourceIdentifiers { get; }

    /// <inheritdoc/>
    public override string ToString() => Path;
}
