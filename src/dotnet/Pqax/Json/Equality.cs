using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>Value equality and hashing for the lists, maps, and extensions of rows.</summary>
internal static class Equality
{
    public static int Combine(int hash, int value) => unchecked((hash * 31) + value);

    public static bool Lists<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b, IEqualityComparer<T> comparer)
    {
        if (a == null || b == null)
        {
            return a == null && b == null;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!comparer.Equals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static int ListHash<T>(IReadOnlyList<T>? list, IEqualityComparer<T> comparer)
    {
        if (list == null)
        {
            return 0;
        }

        int hash = list.Count;
        for (int i = 0; i < list.Count; i++)
        {
            T item = list[i];
            hash = Combine(hash, item == null ? 0 : comparer.GetHashCode(item));
        }

        return hash;
    }

    public static bool Maps(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (a == null || b == null)
        {
            return a == null && b == null;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> pair in a)
        {
            if (!b.TryGetValue(pair.Key, out string? other) || !string.Equals(pair.Value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A hash that does not depend on the order of the members.</summary>
    public static int MapHash(IReadOnlyDictionary<string, string>? map)
    {
        if (map == null)
        {
            return 0;
        }

        int hash = map.Count;
        foreach (KeyValuePair<string, string> pair in map)
        {
            hash ^= Combine(StringComparer.Ordinal.GetHashCode(pair.Key), pair.Value == null ? 0 : StringComparer.Ordinal.GetHashCode(pair.Value));
        }

        return hash;
    }

    /// <summary>Extensions are equal when they have the same members with equal JSON values. Null equals empty.</summary>
    public static bool Extensions(IReadOnlyDictionary<string, JsonElement>? a, IReadOnlyDictionary<string, JsonElement>? b)
    {
        int countA = a?.Count ?? 0;
        int countB = b?.Count ?? 0;
        if (countA != countB)
        {
            return false;
        }

        if (countA == 0)
        {
            return true;
        }

        foreach (KeyValuePair<string, JsonElement> pair in a!)
        {
            if (!b!.TryGetValue(pair.Key, out JsonElement other) || !JsonEquality.DeepEquals(pair.Value, other))
            {
                return false;
            }
        }

        return true;
    }
}
