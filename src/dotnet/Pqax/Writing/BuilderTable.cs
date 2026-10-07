using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>The rows of one table of the builder, with the fields that the model does not have when they come from a file.</summary>
internal sealed class BuilderTable<T> : ITableWriter
    where T : class
{
    private readonly Schema<T> schema;
    private readonly List<T> rows = new List<T>();
    private readonly OrderedMap<List<JsonElement?>> extras = new OrderedMap<List<JsonElement?>>();
    private long lastId;

    public BuilderTable(Schema<T> schema)
    {
        this.schema = schema;
    }

    public string Name => schema.Name;

    public bool IsSummary => schema.Info.IsSummary;

    public int Count => rows.Count;

    /// <summary>
    /// Adds a row and returns its id, or for a table without ids, the position of the row from 0. A row that brings an
    /// id keeps it. A row whose id is 0 gets the next free id, one more than the largest id so far, so that it never
    /// takes an id that a row already has. <see cref="CheckIds"/> refuses a repeated id or one below 0 when the file is written.
    /// </summary>
    public long Add(T row)
    {
        if (row == null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        IdAccessor<T>? id = schema.Id;
        if (id == null)
        {
            rows.Add(row);
            return rows.Count - 1;
        }

        long value = id.Get(row);
        if (value == 0)
        {
            value = checked(lastId + 1);
            id.Set(row, value);
        }

        lastId = Math.Max(lastId, value);
        rows.Add(row);
        return value;
    }

    public void CheckIds()
    {
        IdAccessor<T>? id = schema.Id;
        if (id == null)
        {
            return;
        }

        // Ids that only increase, as the builder assigns them, are distinct without a set.
        bool increasing = true;
        long previous = 0;
        foreach (T row in rows)
        {
            long value = id.Get(row);
            if (value <= 0)
            {
                throw new InvalidOperationException($"The table {Name} has a row with the id {value}. An id is a positive integer.");
            }

            increasing &= value > previous;
            previous = value;
        }

        if (increasing)
        {
            return;
        }

        var seen = new HashSet<long>();
        foreach (T row in rows)
        {
            long value = id.Get(row);
            if (!seen.Add(value))
            {
                throw new InvalidOperationException($"The table {Name} has the id {value} in more than one row. Each row of a table has its own id.");
            }
        }
    }

    /// <summary>Copies the rows of a table of a document, with their ids and their extra fields.</summary>
    public void CopyFrom(PqaxTable<T> table)
    {
        int start = rows.Count;
        IdAccessor<T>? id = schema.Id;
        foreach (T row in table.Rows)
        {
            T copy = schema.Clone(row);
            rows.Add(copy);
            if (id != null)
            {
                lastId = Math.Max(lastId, id.Get(copy));
            }
        }

        foreach (KeyValuePair<string, IReadOnlyList<JsonElement?>> field in table.ExtraFields)
        {
            if (!extras.TryGetValue(field.Key, out List<JsonElement?>? values))
            {
                values = new List<JsonElement?>();
                extras.Set(field.Key, values);
            }

            while (values.Count < start)
            {
                values.Add(null);
            }

            values.AddRange(field.Value);
        }
    }

    public void Write(Utf8JsonWriter writer, int start, int count)
    {
        int end = start + count;
        writer.WriteStartObject(schema.Name);
        foreach (FieldAccessor<T> field in schema.Fields)
        {
            bool any = false;
            for (int i = start; i < end && !any; i++)
            {
                any = field.HasValue(rows[i]);
            }

            if (!any)
            {
                continue;
            }

            writer.WriteStartArray(field.Name);
            for (int i = start; i < end; i++)
            {
                field.Write(writer, rows[i]);
                JsonFlush.IfNeeded(writer);
            }

            writer.WriteEndArray();
        }

        foreach (KeyValuePair<string, List<JsonElement?>> field in extras)
        {
            if (schema.Find(field.Key) == null)
            {
                JsonFlush.WriteExtraField(writer, field.Key, field.Value, start, count);
            }
        }

        writer.WriteEndObject();
    }
}
