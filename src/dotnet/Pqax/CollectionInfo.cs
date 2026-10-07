using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>
/// How and when a footprint was collected: the <c>Collection</c> object of the object model, written as the
/// <c>collection</c> member of the manifest. <see cref="Level"/> is required.
/// </summary>
public sealed class CollectionInfo : IEquatable<CollectionInfo>
{
    /// <summary>The application that ran the collection.</summary>
    public string? CollectorApp { get; set; }

    /// <summary>The version of the application that ran the collection.</summary>
    public string? CollectorAppVersion { get; set; }

    /// <summary>The library that implemented the collection.</summary>
    public string? CollectorLib { get; set; }

    /// <summary>The version of the library that implemented the collection.</summary>
    public string? CollectorLibVersion { get; set; }

    /// <summary>When the collection started.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the collection completed.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The highest collection level requested, from 1 to 4. Required.</summary>
    public long? Level { get; set; }

    /// <summary>Whether minimum and maximum values were collected.</summary>
    public bool? IncludesDataValues { get; set; }

    /// <summary>Whether partition values were collected.</summary>
    public bool? IncludesPartitionValues { get; set; }

    /// <summary>The storage requests made.</summary>
    public long? RequestCount { get; set; }

    /// <summary>The bytes read from storage.</summary>
    public long? BytesRead { get; set; }

    /// <summary>The members of the object that this version of the format does not know, kept so that a rewrite does not lose them. Null when there are none.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Whether the other collection has the same values.</summary>
    /// <param name="other">The collection to compare with.</param>
    /// <returns>True when every value is equal.</returns>
    public bool Equals(CollectionInfo? other) => Schemas.CollectionSchema.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as CollectionInfo);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.CollectionSchema.Hash(this);
}
