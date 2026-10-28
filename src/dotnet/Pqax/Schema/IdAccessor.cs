using System;
using System.Text.Json;

namespace Pqax;

/// <summary>The <c>id</c> field, which is a <see cref="long"/> and is always written.</summary>
internal sealed class IdAccessor<T> : FieldAccessor<T>
    where T : class
{
    private readonly Func<T, long> get;
    private readonly Action<T, long> set;

    public IdAccessor(Func<T, long> get, Action<T, long> set)
        : base(new FieldInfo("id", FieldKind.Id, true, null, null))
    {
        this.get = get;
        this.set = set;
    }

    public long Get(T target) => get(target);

    public void Set(T target, long value) => set(target, value);

    public override void Read(T target, ref Utf8JsonReader reader, ReadContext context) => set(target, JsonRead.Integer(ref reader) ?? 0);

    public override bool HasValue(T target) => true;

    public override void Write(Utf8JsonWriter writer, T target) => writer.WriteNumberValue(get(target));

    public override bool ValueEquals(T a, T b) => get(a) == get(b);

    public override int ValueHash(T target) => get(target).GetHashCode();

    public override void Copy(T source, T target) => set(target, get(source));

    public override long? IntegerOutOfRange(T target) => IsOutOfRange(get(target)) ? get(target) : null;
}
