using System;
using System.Text.Json;

namespace Pqax;

/// <summary>A string field.</summary>
internal sealed class StringAccessor<T> : FieldAccessor<T>
    where T : class
{
    private readonly Func<T, string?> get;
    private readonly Action<T, string?> set;

    public StringAccessor(FieldInfo info, Func<T, string?> get, Action<T, string?> set)
        : base(info)
    {
        this.get = get;
        this.set = set;
    }

    public string? Get(T target) => get(target);

    public override void Read(T target, ref Utf8JsonReader reader, ReadContext context) => set(target, context.ReadString(ref reader));

    public override bool HasValue(T target) => get(target) != null;

    public override void Write(Utf8JsonWriter writer, T target)
    {
        string? value = get(target);
        if (value != null)
        {
            writer.WriteStringValue(value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    public override bool ValueEquals(T a, T b) => string.Equals(get(a), get(b), StringComparison.Ordinal);

    public override int ValueHash(T target) => get(target) is string value ? StringComparer.Ordinal.GetHashCode(value) : 0;

    public override void Copy(T source, T target) => set(target, get(source));
}
