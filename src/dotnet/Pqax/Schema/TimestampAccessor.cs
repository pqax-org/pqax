using System;
using System.Text.Json;

namespace Pqax;

/// <summary>A timestamp field, written in UTC with exactly three decimals.</summary>
internal sealed class TimestampAccessor<T> : FieldAccessor<T>
    where T : class
{
    private readonly Func<T, DateTimeOffset?> get;
    private readonly Action<T, DateTimeOffset?> set;

    public TimestampAccessor(FieldInfo info, Func<T, DateTimeOffset?> get, Action<T, DateTimeOffset?> set)
        : base(info)
    {
        this.get = get;
        this.set = set;
    }

    public override void Read(T target, ref Utf8JsonReader reader, ReadContext context) => set(target, JsonRead.Timestamp(ref reader));

    public override bool HasValue(T target) => get(target).HasValue;

    public override void Write(Utf8JsonWriter writer, T target)
    {
        DateTimeOffset? value = get(target);
        if (value.HasValue)
        {
            writer.WriteStringValue(Timestamps.Format(value.Value));
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
