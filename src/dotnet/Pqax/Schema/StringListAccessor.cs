using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>
/// A list of strings. An element is null when the file has null there, which the format allows only in
/// <c>partitions.values</c>; the property of a row says which lists can hold one.
/// </summary>
internal sealed class StringListAccessor<T> : FieldAccessor<T>
    where T : class
{
    private readonly Func<T, IReadOnlyList<string?>?> get;
    private readonly Action<T, IReadOnlyList<string?>?> set;

    public StringListAccessor(FieldInfo info, Func<T, IReadOnlyList<string?>?> get, Action<T, IReadOnlyList<string?>?> set)
        : base(info)
    {
        this.get = get;
        this.set = set;
    }

    public override void Read(T target, ref Utf8JsonReader reader, ReadContext context) => set(target, context.ReadStringList(ref reader));

    public override bool HasValue(T target) => get(target) != null;

    public override void Write(Utf8JsonWriter writer, T target)
    {
        IReadOnlyList<string?>? value = get(target);
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        for (int i = 0; i < value.Count; i++)
        {
            string? item = value[i];
            if (item == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(item);
            }
        }

        writer.WriteEndArray();
    }

    public override bool ValueEquals(T a, T b) => Equality.Lists(get(a), get(b), StringComparer.Ordinal);

    public override int ValueHash(T target) => Equality.ListHash(get(target), StringComparer.Ordinal);

    public override void Copy(T source, T target) => set(target, get(source));
}
