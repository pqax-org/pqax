using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>The fields of a table row or of a nested object, with the operations that the reader, the writer, and the equality of rows share.</summary>
/// <typeparam name="T">The row or nested object type.</typeparam>
internal sealed class Schema<T>
    where T : class
{
    private readonly Func<T> create;
    private readonly Dictionary<string, FieldAccessor<T>> byName;
    private readonly Func<T, IReadOnlyDictionary<string, JsonElement>?>? getExtensions;
    private readonly Action<T, IReadOnlyDictionary<string, JsonElement>?>? setExtensions;

    public Schema(
        string name,
        bool isSummary,
        Func<T> create,
        IReadOnlyList<FieldAccessor<T>> fields,
        Func<T, IReadOnlyDictionary<string, JsonElement>?>? getExtensions,
        Action<T, IReadOnlyDictionary<string, JsonElement>?>? setExtensions)
    {
        this.create = create;
        this.getExtensions = getExtensions;
        this.setExtensions = setExtensions;
        Name = name;
        Fields = fields;
        byName = new Dictionary<string, FieldAccessor<T>>(StringComparer.Ordinal);
        var infos = new List<FieldInfo>(fields.Count);
        foreach (FieldAccessor<T> field in fields)
        {
            byName.Add(field.Name, field);
            infos.Add(field.Info);
            if (field is IdAccessor<T> id)
            {
                Id = id;
            }
        }

        Info = new TableInfo(name, isSummary, infos);
    }

    /// <summary>The name of the table in the file, or of the nested object.</summary>
    public string Name { get; }

    public TableInfo Info { get; }

    public IReadOnlyList<FieldAccessor<T>> Fields { get; }

    /// <summary>The <c>id</c> field, or null for a table without ids.</summary>
    public IdAccessor<T>? Id { get; }

    public T Create() => create();

    public FieldAccessor<T>? Find(string name) => byName.TryGetValue(name, out FieldAccessor<T>? field) ? field : null;

    /// <summary>A copy of the value: nested objects are copied, lists and maps are shared because they are read only.</summary>
    public T Clone(T source)
    {
        T copy = create();
        foreach (FieldAccessor<T> field in Fields)
        {
            field.Copy(source, copy);
        }

        if (getExtensions != null && setExtensions != null)
        {
            setExtensions(copy, getExtensions(source));
        }

        return copy;
    }

    public bool ValuesEqual(T a, T? b)
    {
        if (b == null)
        {
            return false;
        }

        if (ReferenceEquals(a, b))
        {
            return true;
        }

        foreach (FieldAccessor<T> field in Fields)
        {
            if (!field.ValueEquals(a, b))
            {
                return false;
            }
        }

        return getExtensions == null || Equality.Extensions(getExtensions(a), getExtensions(b));
    }

    public int Hash(T value)
    {
        int hash = 17;
        foreach (FieldAccessor<T> field in Fields)
        {
            hash = Equality.Combine(hash, field.ValueHash(value));
        }

        return hash;
    }

    /// <summary>Reads a nested object. Members that the schema does not know go to its extensions.</summary>
    public T? ReadObject(ref Utf8JsonReader reader, ReadContext context)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            JsonRead.SkipValue(ref reader);
            return null;
        }

        T value = create();
        Dictionary<string, JsonElement>? extensions = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            FieldAccessor<T>? field = Find(name);
            if (field != null)
            {
                field.Read(value, ref reader, context);
            }
            else if (setExtensions != null)
            {
                extensions ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                extensions[name] = JsonElement.ParseValue(ref reader);
            }
            else
            {
                reader.Skip();
            }
        }

        if (extensions != null)
        {
            setExtensions!(value, extensions);
        }

        return value;
    }

    /// <summary>Writes a nested object: the fields that have a value, then the extensions.</summary>
    public void WriteObject(Utf8JsonWriter writer, T value)
    {
        writer.WriteStartObject();
        foreach (FieldAccessor<T> field in Fields)
        {
            if (field.HasValue(value))
            {
                writer.WritePropertyName(field.Name);
                field.Write(writer, value);
            }
        }

        IReadOnlyDictionary<string, JsonElement>? extensions = getExtensions?.Invoke(value);
        if (extensions != null)
        {
            foreach (KeyValuePair<string, JsonElement> pair in extensions)
            {
                if (!byName.ContainsKey(pair.Key))
                {
                    writer.WritePropertyName(pair.Key);
                    pair.Value.WriteTo(writer);
                }
            }
        }

        writer.WriteEndObject();
    }
}
