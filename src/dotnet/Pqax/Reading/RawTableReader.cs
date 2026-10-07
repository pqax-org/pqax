using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>Reads every field of a table as JSON values, from all its parts, including the fields that this version does not know.</summary>
internal sealed class RawTableReader : ITableReader
{
    private readonly string name;
    private readonly ExtraFieldsBuilder fields = new ExtraFieldsBuilder();
    private int rowCount;
    private int partStart = -1;
    private int partRows;

    public RawTableReader(string name)
    {
        this.name = name;
    }

    public void BeginPart()
    {
        rowCount += partRows;
        partRows = 0;
        partStart = rowCount;
    }

    /// <summary>Reads the table object at the current token. The table has, in this part, the rows of its longest array.</summary>
    public void ReadTable(ref Utf8JsonReader reader)
    {
        if (partStart < 0)
        {
            BeginPart();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            JsonRead.SkipValue(ref reader);
            return;
        }

        // A table that appears twice in the same part keeps its last occurrence.
        fields.RemoveFrom(partStart);
        partRows = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string field = reader.GetString()!;
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                JsonRead.SkipValue(ref reader);
                continue;
            }

            IReadOnlyList<JsonElement?> values = ExtraFieldsBuilder.ReadValues(ref reader);
            fields.Add(field, partStart, values);
            partRows = Math.Max(partRows, values.Count);
        }
    }

    public RawTable Build()
    {
        int total = rowCount + partRows;
        return new RawTable(name, total, fields.Build(total));
    }
}
