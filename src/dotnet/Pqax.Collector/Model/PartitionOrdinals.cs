using System;
using System.Collections.Generic;
using System.Globalization;

namespace Pqax.Collector;

/// <summary>
/// Replaces partition values with numbers, so that a footprint without partition values keeps the shape of the paths
/// without the values: <c>Year=2024/part-1.parquet</c> becomes <c>Year=#1/part-1.parquet</c>. Each column numbers its
/// values from 0 in the order in which they first appear; a null value is a value of its own.
/// </summary>
internal sealed class PartitionOrdinals
{
    private readonly Dictionary<string, Dictionary<string, int>> columns = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

    /// <summary>The number of a value of a column, given on first sight.</summary>
    public int Get(string column, string? value)
    {
        if (!columns.TryGetValue(column, out Dictionary<string, int>? values))
        {
            values = new Dictionary<string, int>(StringComparer.Ordinal);
            columns[column] = values;
        }

        string key = value == null ? "\u0000" : "\u0001" + value;
        if (!values.TryGetValue(key, out int ordinal))
        {
            ordinal = values.Count;
            values[key] = ordinal;
        }

        return ordinal;
    }

    /// <summary>The path with the value of every <c>column=value</c> folder replaced by its number.</summary>
    public string Sanitize(string path)
    {
        string[] segments = path.Split('/');
        bool changed = false;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string segment = segments[i];
            int equal = segment.IndexOf('=');
            if (equal <= 0)
            {
                continue;
            }

            string column = segment.Substring(0, equal);
            segments[i] = column + "=#" + Get(column, segment.Substring(equal + 1)).ToString(CultureInfo.InvariantCulture);
            changed = true;
        }

        return changed ? string.Join("/", segments) : path;
    }
}
