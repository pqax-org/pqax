using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>A table that this version of the format does not define, copied from a file so that a rewrite keeps it.</summary>
internal sealed class UnknownTable : ITableWriter
{
    private readonly RawTable table;

    public UnknownTable(RawTable table, bool isSummary)
    {
        this.table = table;
        IsSummary = isSummary;
    }

    public string Name => table.Name;

    public bool IsSummary { get; }

    public int Count => table.Count;

    /// <summary>The ids of a table that this version does not know have no meaning that the builder could check.</summary>
    public void CheckIds()
    {
    }

    public void Write(Utf8JsonWriter writer, int start, int count)
    {
        writer.WriteStartObject(table.Name);
        foreach (KeyValuePair<string, IReadOnlyList<JsonElement?>> field in table.Fields)
        {
            JsonFlush.WriteExtraField(writer, field.Key, field.Value, start, count);
        }

        writer.WriteEndObject();
    }
}
