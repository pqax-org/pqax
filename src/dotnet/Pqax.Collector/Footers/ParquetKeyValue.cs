using System;

namespace Pqax.Collector;

/// <summary>
/// A key of the key-value metadata of a footer. The value is kept only for the keys of an allow-list, because other
/// keys can hold data values: Fabric, for example, copies the statistics of the Delta log, with their bounds, into the
/// footer. The allow-list: keys that contain <c>vorder</c>, the key <c>org.apache.spark.version</c>, and keys that end
/// with <c>string_prefix_length</c>, with a value of at most 200 characters.
/// </summary>
public sealed class ParquetKeyValue
{
    /// <summary>The longest value that is kept for a key of the allow-list.</summary>
    public const int MaxKeptValueLength = 200;

    internal ParquetKeyValue(string key, int? valueLength, string? value)
    {
        Key = key;
        ValueLength = valueLength;
        Value = value;
    }

    /// <summary>The key.</summary>
    public string Key { get; }

    /// <summary>The length of the value in bytes. Null when the footer gives no value.</summary>
    public int? ValueLength { get; }

    /// <summary>The value, for a key of the allow-list. Null otherwise.</summary>
    public string? Value { get; }

    /// <summary>Whether the value of a key is kept.</summary>
    /// <param name="key">The key.</param>
    /// <returns>True for a key of the allow-list.</returns>
    public static bool IsKept(string key)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        return key.IndexOf("vorder", StringComparison.OrdinalIgnoreCase) >= 0
            || string.Equals(key, "org.apache.spark.version", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("string_prefix_length", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public override string ToString() => Value == null ? Key : Key + "=" + Value;
}
