using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>
/// Reads one value of the model type at the current token of a <see cref="Utf8JsonReader"/>.
/// A value that does not have the type of the model is read as null, and the reader is left on its last token.
/// </summary>
internal static class JsonRead
{
    /// <summary>The largest integer that the format allows, 2^53 - 1.</summary>
    public const long MaxSafeInteger = 9007199254740991L;

    /// <summary>Skips an object or an array. A primitive value needs no skipping.</summary>
    public static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

    /// <summary>An integer. A number with a fraction of zero, such as <c>1.0</c>, is read as an integer.</summary>
    public static long? Integer(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            SkipValue(ref reader);
            return null;
        }

        if (reader.TryGetInt64(out long value))
        {
            return value;
        }

        if (reader.TryGetDouble(out double number) && number == Math.Floor(number) && number >= -9.2e18 && number <= 9.2e18)
        {
            return (long)number;
        }

        return null;
    }

    public static bool? Boolean(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            default:
                SkipValue(ref reader);
                return null;
        }
    }

    /// <summary>A timestamp. The form of the format is read fast, and other ISO 8601 forms are accepted.</summary>
    public static DateTimeOffset? Timestamp(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            SkipValue(ref reader);
            return null;
        }

        return Timestamps.Parse(reader.GetString()!);
    }

    /// <summary>A list of integers. An element that is not an integer is left out.</summary>
    public static IReadOnlyList<long>? IntegerList(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            SkipValue(ref reader);
            return null;
        }

        var values = new List<long>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            long? value = Integer(ref reader);
            if (value.HasValue)
            {
                values.Add(value.Value);
            }
        }

        return values.ToArray();
    }

    /// <summary>The bytes of a part without the byte order mark that a reader skips.</summary>
    public static ReadOnlyMemory<byte> WithoutBom(byte[] bytes)
    {
        bool bom = HasBom(bytes);
        return new ReadOnlyMemory<byte>(bytes, bom ? 3 : 0, bom ? bytes.Length - 3 : bytes.Length);
    }

    public static bool HasBom(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
