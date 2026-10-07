using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>A list of references. An element that is not an integer is left out when the list is read.</summary>
internal sealed class ReferenceListAccessor<T> : FieldAccessor<T>
    where T : class
{
    private readonly Func<T, IReadOnlyList<long>?> get;
    private readonly Action<T, IReadOnlyList<long>?> set;

    public ReferenceListAccessor(FieldInfo info, Func<T, IReadOnlyList<long>?> get, Action<T, IReadOnlyList<long>?> set)
        : base(info)
    {
        this.get = get;
        this.set = set;
    }

    public override void Read(T target, ref Utf8JsonReader reader, ReadContext context) => set(target, JsonRead.IntegerList(ref reader));

    public override bool HasValue(T target) => get(target) != null;

    public override void Write(Utf8JsonWriter writer, T target)
    {
        IReadOnlyList<long>? value = get(target);
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        for (int i = 0; i < value.Count; i++)
        {
            writer.WriteNumberValue(value[i]);
        }

        writer.WriteEndArray();
    }

    public override bool ValueEquals(T a, T b) => Equality.Lists(get(a), get(b), EqualityComparer<long>.Default);

    public override int ValueHash(T target) => Equality.ListHash(get(target), EqualityComparer<long>.Default);

    public override void Copy(T source, T target) => set(target, get(source));
}
