using System;
using System.Text.Json;

namespace Pqax;

/// <summary>A boolean field.</summary>
internal sealed class BooleanAccessor<T> : FieldAccessor<T>
    where T : class
{
    private readonly Func<T, bool?> get;
    private readonly Action<T, bool?> set;

    public BooleanAccessor(FieldInfo info, Func<T, bool?> get, Action<T, bool?> set)
        : base(info)
    {
        this.get = get;
        this.set = set;
    }

    public override void Read(T target, ref Utf8JsonReader reader, ReadContext context) => set(target, JsonRead.Boolean(ref reader));

    public override bool HasValue(T target) => get(target).HasValue;

    public override void Write(Utf8JsonWriter writer, T target)
    {
        bool? value = get(target);
        if (value.HasValue)
        {
            writer.WriteBooleanValue(value.Value);
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
