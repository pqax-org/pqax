using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>What only a Delta table has: its protocol, its features, and its checkpoint. It is the <c>delta</c> field of a table.</summary>
public sealed class DeltaDetails : IEquatable<DeltaDetails>
{
    /// <summary>The minimum reader version of the protocol.</summary>
    public long? MinReaderVersion { get; set; }

    /// <summary>The minimum writer version of the protocol.</summary>
    public long? MinWriterVersion { get; set; }

    /// <summary>The reader features of the table.</summary>
    public IReadOnlyList<string>? ReaderFeatures { get; set; }

    /// <summary>The writer features of the table.</summary>
    public IReadOnlyList<string>? WriterFeatures { get; set; }

    /// <summary>The version of the last checkpoint.</summary>
    public long? CheckpointVersion { get; set; }

    /// <summary>The commit files newer than the last checkpoint.</summary>
    public long? CommitsAfterCheckpoint { get; set; }

    /// <summary>The names of the metadata domains in use.</summary>
    public IReadOnlyList<string>? Domains { get; set; }

    /// <summary>The members of the object that this version of the format does not know, kept so that a rewrite does not lose them. Null when there are none.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Whether the other details have the same values.</summary>
    /// <param name="other">The details to compare with.</param>
    /// <returns>True when every value is equal.</returns>
    public bool Equals(DeltaDetails? other) => Schemas.DeltaSchema.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as DeltaDetails);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.DeltaSchema.Hash(this);
}
