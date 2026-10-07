using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>What only an Iceberg table has: its format version, schemas, snapshots, and manifests. It is the <c>iceberg</c> field of a table.</summary>
public sealed class IcebergDetails : IEquatable<IcebergDetails>
{
    /// <summary>The version of the table format.</summary>
    public long? FormatVersion { get; set; }

    /// <summary>The schema that the columns describe.</summary>
    public long? CurrentSchemaId { get; set; }

    /// <summary>The schemas that the metadata still lists.</summary>
    public long? SchemaCount { get; set; }

    /// <summary>The snapshots that the metadata still lists.</summary>
    public long? SnapshotCount { get; set; }

    /// <summary>The branches and tags.</summary>
    public long? RefCount { get; set; }

    /// <summary>The manifests of the current snapshot.</summary>
    public long? ManifestCount { get; set; }

    /// <summary>The size of the manifests of the current snapshot.</summary>
    public long? ManifestSize { get; set; }

    /// <summary>The statistics files attached to the table.</summary>
    public long? StatisticsFileCount { get; set; }

    /// <summary>The members of the object that this version of the format does not know, kept so that a rewrite does not lose them. Null when there are none.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Whether the other details have the same values.</summary>
    /// <param name="other">The details to compare with.</param>
    /// <returns>True when every value is equal.</returns>
    public bool Equals(IcebergDetails? other) => Schemas.IcebergSchema.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as IcebergDetails);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.IcebergSchema.Hash(this);
}
