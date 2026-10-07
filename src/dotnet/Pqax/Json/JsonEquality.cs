using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>Compares two JSON values by content: members in any order, strings after unescaping, numbers by value.</summary>
internal static class JsonEquality
{
    public static bool DeepEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                return ObjectsEqual(a, b);
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength())
                {
                    return false;
                }

                // The enumerators are structs that hold no resource: they are not in a using statement,
                // where MoveNext would run on a read-only copy.
                JsonElement.ArrayEnumerator x = a.EnumerateArray();
                JsonElement.ArrayEnumerator y = b.EnumerateArray();
                while (x.MoveNext() && y.MoveNext())
                {
                    if (!DeepEquals(x.Current, y.Current))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                if (a.GetRawText() == b.GetRawText())
                {
                    return true;
                }

                if (a.TryGetDecimal(out decimal m) && b.TryGetDecimal(out decimal n))
                {
                    return m == n;
                }

                return a.TryGetDouble(out double p) && b.TryGetDouble(out double q) && p.Equals(q);
            default:
                return true;
        }
    }

    private static bool ObjectsEqual(JsonElement a, JsonElement b)
    {
        Dictionary<string, JsonElement> x = Members(a);
        Dictionary<string, JsonElement> y = Members(b);
        if (x.Count != y.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, JsonElement> pair in x)
        {
            if (!y.TryGetValue(pair.Key, out JsonElement other) || !DeepEquals(pair.Value, other))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The members of an object. When a name repeats, the last value wins, as in most JSON parsers.</summary>
    private static Dictionary<string, JsonElement> Members(JsonElement element)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            members[property.Name] = property.Value;
        }

        return members;
    }
}
