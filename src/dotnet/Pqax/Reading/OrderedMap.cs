using System;
using System.Collections;
using System.Collections.Generic;

namespace Pqax;

/// <summary>A read-only map from names to values that enumerates in the order in which the names were added: the order of the file.</summary>
/// <typeparam name="TValue">The type of the values.</typeparam>
internal sealed class OrderedMap<TValue> : IReadOnlyDictionary<string, TValue>
{
    private readonly List<KeyValuePair<string, TValue>> items = new List<KeyValuePair<string, TValue>>();
    private readonly Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);

    public int Count => items.Count;

    public IEnumerable<string> Keys
    {
        get
        {
            foreach (KeyValuePair<string, TValue> item in items)
            {
                yield return item.Key;
            }
        }
    }

    public IEnumerable<TValue> Values
    {
        get
        {
            foreach (KeyValuePair<string, TValue> item in items)
            {
                yield return item.Value;
            }
        }
    }

    public TValue this[string key] => items[index[key]].Value;

    /// <summary>Adds a name, or replaces its value and keeps its place when the name is already there.</summary>
    public void Set(string key, TValue value)
    {
        if (index.TryGetValue(key, out int position))
        {
            items[position] = new KeyValuePair<string, TValue>(key, value);
        }
        else
        {
            index.Add(key, items.Count);
            items.Add(new KeyValuePair<string, TValue>(key, value));
        }
    }

    public void Add(string key, TValue value) => Set(key, value);

    public bool ContainsKey(string key) => index.ContainsKey(key);

    public bool TryGetValue(string key, out TValue value)
    {
        if (index.TryGetValue(key, out int position))
        {
            value = items[position].Value;
            return true;
        }

        value = default!;
        return false;
    }

    public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
