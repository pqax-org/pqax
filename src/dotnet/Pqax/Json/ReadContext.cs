using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>
/// Shares equal strings and equal lists of strings while a table is read. Values such as codecs, encodings, and
/// types repeat on most rows, so sharing them keeps one copy in memory instead of one for each row.
/// </summary>
internal sealed class ReadContext
{
    private const int MaxPooledLength = 64;
    private const int Slots = 4096;

    private readonly byte[]?[] stringKeys = new byte[]?[Slots];
    private readonly string?[] stringValues = new string?[Slots];
    private readonly string?[]?[] lists = new string?[]?[Slots];
    private readonly List<string?> scratch = new List<string?>();

    /// <summary>A string, or null when the value is not a string.</summary>
    public string? ReadString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            JsonRead.SkipValue(ref reader);
            return null;
        }

        return Pool(ref reader);
    }

    /// <summary>A list of strings. An element that is not a string is null.</summary>
    public IReadOnlyList<string?>? ReadStringList(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            JsonRead.SkipValue(ref reader);
            return null;
        }

        scratch.Clear();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            scratch.Add(ReadString(ref reader));
        }

        return PoolList();
    }

    /// <summary>A map of string to string. A member whose value is not a string is left out.</summary>
    public IReadOnlyDictionary<string, string>? ReadMap(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            JsonRead.SkipValue(ref reader);
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string key = Pool(ref reader);
            reader.Read();
            if (reader.TokenType == JsonTokenType.String)
            {
                map[key] = Pool(ref reader);
            }
            else
            {
                JsonRead.SkipValue(ref reader);
            }
        }

        return map;
    }

    /// <summary>The string of the current string or property name token, shared with an equal one read before.</summary>
    private string Pool(ref Utf8JsonReader reader)
    {
        ReadOnlySpan<byte> span = reader.ValueSpan;
        if (reader.ValueIsEscaped || reader.HasValueSequence || span.Length > MaxPooledLength)
        {
            return reader.GetString()!;
        }

        uint hash = 2166136261;
        for (int i = 0; i < span.Length; i++)
        {
            hash = (hash ^ span[i]) * 16777619;
        }

        int slot = (int)(hash & (Slots - 1));
        byte[]? key = stringKeys[slot];
        if (key != null && span.SequenceEqual(key))
        {
            return stringValues[slot]!;
        }

        string value = reader.GetString()!;
        stringKeys[slot] = span.ToArray();
        stringValues[slot] = value;
        return value;
    }

    /// <summary>The list in <see cref="scratch"/>, shared with an equal list read before.</summary>
    private IReadOnlyList<string?> PoolList()
    {
        int hash = 17;
        foreach (string? item in scratch)
        {
            hash = Equality.Combine(hash, item == null ? 0 : StringComparer.Ordinal.GetHashCode(item));
        }

        int slot = hash & (Slots - 1);
        string?[]? cached = lists[slot];
        if (cached != null && cached.Length == scratch.Count)
        {
            bool same = true;
            for (int i = 0; i < cached.Length && same; i++)
            {
                same = string.Equals(cached[i], scratch[i], StringComparison.Ordinal);
            }

            if (same)
            {
                return cached;
            }
        }

        string?[] list = scratch.ToArray();
        lists[slot] = list;
        return list;
    }
}
