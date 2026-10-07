using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>Declares the fields of a table or of a nested object, in the order in which they are written.</summary>
internal sealed class SchemaBuilder<T>
    where T : class
{
    private readonly string name;
    private readonly bool isSummary;
    private readonly Func<T> create;
    private readonly List<FieldAccessor<T>> fields = new List<FieldAccessor<T>>();
    private Func<T, IReadOnlyDictionary<string, JsonElement>?>? getExtensions;
    private Action<T, IReadOnlyDictionary<string, JsonElement>?>? setExtensions;

    public SchemaBuilder(string name, bool isSummary, Func<T> create)
    {
        this.name = name;
        this.isSummary = isSummary;
        this.create = create;
    }

    public SchemaBuilder<T> Id(Func<T, long> get, Action<T, long> set)
    {
        fields.Add(new IdAccessor<T>(get, set));
        return this;
    }

    public SchemaBuilder<T> Integer(string field, Func<T, long?> get, Action<T, long?> set, bool required = false)
    {
        fields.Add(new IntegerAccessor<T>(new FieldInfo(field, FieldKind.Integer, required, null, null), get, set));
        return this;
    }

    public SchemaBuilder<T> Reference(string field, string target, Func<T, long?> get, Action<T, long?> set, bool required = false)
    {
        fields.Add(new IntegerAccessor<T>(new FieldInfo(field, FieldKind.Reference, required, target, null), get, set));
        return this;
    }

    public SchemaBuilder<T> String(string field, Func<T, string?> get, Action<T, string?> set, bool required = false)
    {
        fields.Add(new StringAccessor<T>(new FieldInfo(field, FieldKind.String, required, null, null), get, set));
        return this;
    }

    public SchemaBuilder<T> Boolean(string field, Func<T, bool?> get, Action<T, bool?> set)
    {
        fields.Add(new BooleanAccessor<T>(new FieldInfo(field, FieldKind.Boolean, false, null, null), get, set));
        return this;
    }

    public SchemaBuilder<T> Timestamp(string field, Func<T, DateTimeOffset?> get, Action<T, DateTimeOffset?> set)
    {
        fields.Add(new TimestampAccessor<T>(new FieldInfo(field, FieldKind.Timestamp, false, null, null), get, set));
        return this;
    }

    public SchemaBuilder<T> StringList(string field, Func<T, IReadOnlyList<string>?> get, Action<T, IReadOnlyList<string>?> set, bool required = false)
    {
        fields.Add(new StringListAccessor<T>(new FieldInfo(field, FieldKind.StringList, required, null, null), get, set));
        return this;
    }

    public SchemaBuilder<T> ReferenceList(string field, string target, Func<T, IReadOnlyList<long>?> get, Action<T, IReadOnlyList<long>?> set)
    {
        fields.Add(new ReferenceListAccessor<T>(new FieldInfo(field, FieldKind.ReferenceList, false, target, null), get, set));
        return this;
    }

    public SchemaBuilder<T> Map(string field, Func<T, IReadOnlyDictionary<string, string>?> get, Action<T, IReadOnlyDictionary<string, string>?> set)
    {
        fields.Add(new MapAccessor<T>(new FieldInfo(field, FieldKind.Map, false, null, null), get, set));
        return this;
    }

    public SchemaBuilder<T> Object<TValue>(string field, Schema<TValue> schema, Func<T, TValue?> get, Action<T, TValue?> set)
        where TValue : class
    {
        fields.Add(new ObjectAccessor<T, TValue>(field, schema, get, set));
        return this;
    }

    /// <summary>Gives a nested object a place for the members that this version does not know.</summary>
    public SchemaBuilder<T> Extensions(Func<T, IReadOnlyDictionary<string, JsonElement>?> get, Action<T, IReadOnlyDictionary<string, JsonElement>?> set)
    {
        getExtensions = get;
        setExtensions = set;
        return this;
    }

    public Schema<T> Build() => new Schema<T>(name, isSummary, create, fields.ToArray(), getExtensions, setExtensions);
}
