using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>
/// The shape of a set of values, such as the sizes of the files of a table: count, sum, extremes, and quartiles.
/// It is the <c>Distribution</c> object of the object model.
/// </summary>
public sealed class Distribution : IEquatable<Distribution>
{
    /// <summary>The number of values.</summary>
    public long? Count { get; set; }

    /// <summary>The sum of the values.</summary>
    public long? Sum { get; set; }

    /// <summary>The smallest value.</summary>
    public long? Minimum { get; set; }

    /// <summary>The first quartile.</summary>
    public long? FirstQuartile { get; set; }

    /// <summary>The median.</summary>
    public long? Median { get; set; }

    /// <summary>The third quartile.</summary>
    public long? ThirdQuartile { get; set; }

    /// <summary>The largest value.</summary>
    public long? Maximum { get; set; }

    /// <summary>The members of the object that this version of the format does not know, kept so that a rewrite does not lose them. Null when there are none.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Whether the other distribution has the same values.</summary>
    /// <param name="other">The distribution to compare with.</param>
    /// <returns>True when every value is equal.</returns>
    public bool Equals(Distribution? other) => Schemas.DistributionSchema.ValuesEqual(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Distribution);

    /// <inheritdoc/>
    public override int GetHashCode() => Schemas.DistributionSchema.Hash(this);
}
