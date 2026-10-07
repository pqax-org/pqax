using System;
using System.Text.Json;

namespace Pqax;

/// <summary>An integer field or a reference, as a <see cref="Nullable{Int64}"/>.</summary>
internal sealed class IntegerAccessor<T> : FieldAccessor<T>
    where T : class
{
    private readonly Func<T, long?> get;
    private readonly Action<T, long?> set;

    public IntegerAccessor(FieldInfo info, Func<T, long?> get, Action<T, long?> set)
        : base(info)
    {
        this.get = get;
        this.set = set;
    }

    public long? Get(T target) => get(target);

    public override void Read(T target, ref Utf8JsonReader reader, ReadContext context) => set(target, JsonRead.Integer(ref reader));

    public override bool HasValue(T target) => get(target).HasValue;

    public override void Write(Utf8JsonWriter writer, T target)
    {
        long? value = get(target);
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    public override bool ValueEquals(T a, T b) => get(a) == get(b);

    public override int ValueHash(T target) => get(target)?.GetHashCode() ?? 0;

    public override void Copy(T source, T target) => set(target, get(source));
}
