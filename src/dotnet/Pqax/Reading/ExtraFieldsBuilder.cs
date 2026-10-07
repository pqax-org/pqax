using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>
/// Collects the fields of a table that are not in the model, part by part, and lays them out with one value for each
/// row of the whole table. A row of a part that does not have the field gets null.
/// </summary>
internal sealed class ExtraFieldsBuilder
{
    private readonly List<string> names = new List<string>();
    private readonly Dictionary<string, List<KeyValuePair<int, IReadOnlyList<JsonElement?>>>> segments =
        new Dictionary<string, List<KeyValuePair<int, IReadOnlyList<JsonElement?>>>>(StringComparer.Ordinal);

    public bool IsEmpty => names.Count == 0;

    /// <summary>Adds the values that a part has for a field, from row <paramref name="start"/> of the table.</summary>
    public void Add(string name, int start, IReadOnlyList<JsonElement?> values)
    {
        if (!segments.TryGetValue(name, out List<KeyValuePair<int, IReadOnlyList<JsonElement?>>>? list))
        {
            list = new List<KeyValuePair<int, IReadOnlyList<JsonElement?>>>();
            segments.Add(name, list);
            names.Add(name);
        }

        // Each segment is the first row of the part in the table, and the values of the part.
        list.Add(new KeyValuePair<int, IReadOnlyList<JsonElement?>>(start, values));
    }

    /// <summary>Forgets the values from row <paramref name="start"/> on, when a part repeats a table and the last one wins.</summary>
    public void RemoveFrom(int start)
    {
        foreach (List<KeyValuePair<int, IReadOnlyList<JsonElement?>>> list in segments.Values)
        {
            list.RemoveAll(segment => segment.Key >= start);
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<JsonElement?>> Build(int rowCount)
    {
        var fields = new OrderedMap<IReadOnlyList<JsonElement?>>();
        foreach (string name in names)
        {
            List<KeyValuePair<int, IReadOnlyList<JsonElement?>>> list = segments[name];
            if (list.Count == 0)
            {
                continue;
            }

            var values = new JsonElement?[rowCount];
            foreach (KeyValuePair<int, IReadOnlyList<JsonElement?>> segment in list)
            {
                int count = Math.Min(segment.Value.Count, rowCount - segment.Key);
                for (int i = 0; i < count; i++)
                {
                    values[segment.Key + i] = segment.Value[i];
                }
            }

            fields.Add(name, values);
        }

        return fields;
    }

    /// <summary>
    /// Reads an array of a field that is not in the model. JSON null becomes null. The values stay valid after the part
    /// is gone, because the array is parsed into a document of its own.
    /// </summary>
    public static IReadOnlyList<JsonElement?> ReadValues(ref Utf8JsonReader reader)
    {
        JsonElement array = JsonElement.ParseValue(ref reader);
        var values = new JsonElement?[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement element in array.EnumerateArray())
        {
            values[i++] = element.ValueKind == JsonValueKind.Null ? null : element;
        }

        return values;
    }
}
