using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>A map of string to string. A member whose value is not a string is left out when the map is read.</summary>
internal sealed class MapAccessor<T> : FieldAccessor<T>
    where T : class
{
    private readonly Func<T, IReadOnlyDictionary<string, string>?> get;
    private readonly Action<T, IReadOnlyDictionary<string, string>?> set;

    public MapAccessor(FieldInfo info, Func<T, IReadOnlyDictionary<string, string>?> get, Action<T, IReadOnlyDictionary<string, string>?> set)
        : base(info)
    {
        this.get = get;
        this.set = set;
    }

    public override void Read(T target, ref Utf8JsonReader reader, ReadContext context) => set(target, context.ReadMap(ref reader));

    public override bool HasValue(T target) => get(target) != null;

    public override void Write(Utf8JsonWriter writer, T target)
    {
        IReadOnlyDictionary<string, string>? value = get(target);
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (KeyValuePair<string, string> pair in value)
        {
            if (pair.Value == null)
            {
                writer.WriteNull(pair.Key);
            }
            else
            {
                writer.WriteString(pair.Key, pair.Value);
            }
        }

        writer.WriteEndObject();
    }

    public override bool ValueEquals(T a, T b) => Equality.Maps(get(a), get(b));

    public override int ValueHash(T target) => Equality.MapHash(get(target));

    public override void Copy(T source, T target) => set(target, get(source));
}
