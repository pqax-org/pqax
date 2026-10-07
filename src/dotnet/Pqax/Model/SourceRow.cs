using System;
using System.Collections.Generic;

namespace Pqax;

/// <summary>A row of <c>sources</c>: a place that holds tables, such as a lakehouse, a warehouse, a catalog, or a storage folder.</summary>
public sealed class SourceRow : IEquatable<SourceRow>
{
    /// <summary>The identifier of the row in its table. Zero when the file gives none.</summary>
    public long Id { get; set; }

    /// <summary>The kind of source, for example <c>OneLake lakehouse</c>, <c>OneLake warehouse</c>, <c>Unity Catalog</c>, <c>Iceberg catalog</c>, <c>Storage folder</c>. Required.</summary>
    public string? Kind { get; set; }

    /// <summary>The name of the lakehouse, warehouse, or catalog.</summary>
    public string? Name { get; set; }

    /// <summary>The base location of the source.</summary>
    public string? Location { get; set; }

    /// <summary>Identifiers that the platform assigns, such as a workspace id and an item id.</summary>
    public IReadOnlyDictionary<string, string>? Identifiers { get; set; }

    /// <summary>Whether the other row has the same values in every field.</summary>
    /// <param name="other">The row to compare with.</param>
    /// <returns>True when every field is equal.</returns>
    public bool Equals(SourceRow? other) => Schemas.Sources.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SourceRow);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.Sources.Hash(this);
}
