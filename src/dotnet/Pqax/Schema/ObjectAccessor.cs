using System;
using System.Text.Json;

namespace Pqax;

/// <summary>A nested object: a <see cref="Distribution"/>, <see cref="DeltaDetails"/>, or <see cref="IcebergDetails"/>.</summary>
internal sealed class ObjectAccessor<T, TValue> : FieldAccessor<T>
    where T : class
    where TValue : class
{
    private readonly Schema<TValue> schema;
    private readonly Func<T, TValue?> get;
    private readonly Action<T, TValue?> set;

    public ObjectAccessor(string name, Schema<TValue> schema, Func<T, TValue?> get, Action<T, TValue?> set)
        : base(new FieldInfo(name, FieldKind.Object, false, null, schema.Info.Fields))
    {
        this.schema = schema;
        this.get = get;
        this.set = set;
    }

    public override void Read(T target, ref Utf8JsonReader reader, ReadContext context) => set(target, schema.ReadObject(ref reader, context));

    public override bool HasValue(T target) => get(target) != null;

    public override void Write(Utf8JsonWriter writer, T target)
    {
        TValue? value = get(target);
        if (value == null)
        {
            writer.WriteNullValue();
        }
        else
        {
            schema.WriteObject(writer, value);
        }
    }

    public override bool ValueEquals(T a, T b)
    {
        TValue? x = get(a);
        TValue? y = get(b);
        if (x == null || y == null)
        {
            return x == null && y == null;
        }

        return schema.ValuesEqual(x, y);
    }

    public override int ValueHash(T target) => get(target) is TValue value ? schema.Hash(value) : 0;

    public override void Copy(T source, T target)
    {
        TValue? value = get(source);
        set(target, value == null ? null : schema.Clone(value));
    }

    public override long? IntegerOutOfRange(T target) => get(target) is TValue value ? schema.IntegerOutOfRange(value) : null;
}
