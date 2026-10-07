using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>
/// Reads the rows of one table from its parts, in the order of the manifest, straight from the JSON tokens into row
/// objects. A field missing from a part is null in its rows, a member that is not an array is ignored, and a field
/// that the model does not have goes to the extra fields.
/// </summary>
internal sealed class TableReader<T> : ITableReader
    where T : class
{
    private readonly Schema<T> schema;
    private readonly ReadContext context;
    private readonly List<T> rows = new List<T>();
    private readonly ExtraFieldsBuilder extras = new ExtraFieldsBuilder();
    private int partStart = -1;

    public TableReader(Schema<T> schema, ReadContext context)
    {
        this.schema = schema;
        this.context = context;
    }

    /// <summary>Starts a new part. A table that appears twice in the same part keeps its last occurrence.</summary>
    public void BeginPart() => partStart = rows.Count;

    /// <summary>Reads the table object at the current token. The table has, in this part, the rows of its longest array.</summary>
    public void ReadTable(ref Utf8JsonReader reader)
    {
        if (partStart < 0)
        {
            partStart = rows.Count;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            JsonRead.SkipValue(ref reader);
            return;
        }

        if (rows.Count > partStart)
        {
            rows.RemoveRange(partStart, rows.Count - partStart);
            extras.RemoveFrom(partStart);
        }

        int start = partStart;
        int partRows = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                // A member that is not an array is not a field of this version.
                JsonRead.SkipValue(ref reader);
                continue;
            }

            FieldAccessor<T>? field = schema.Find(name);
            int count = 0;
            if (field != null)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    field.Read(RowAt(start + count), ref reader, context);
                    count++;
                }
            }
            else
            {
                IReadOnlyList<JsonElement?> values = ExtraFieldsBuilder.ReadValues(ref reader);
                count = values.Count;
                extras.Add(name, start, values);
            }

            partRows = Math.Max(partRows, count);
        }

        while (rows.Count < start + partRows)
        {
            rows.Add(schema.Create());
        }
    }

    public PqaxTable<T> Build()
    {
        IdAccessor<T>? id = schema.Id;
        return new PqaxTable<T>(schema.Name, rows.ToArray(), extras.Build(rows.Count), id == null ? null : id.Get);
    }

    private T RowAt(int index)
    {
        while (rows.Count <= index)
        {
            rows.Add(schema.Create());
        }

        return rows[index];
    }
}
