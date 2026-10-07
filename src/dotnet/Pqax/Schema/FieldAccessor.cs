using System.Text.Json;

namespace Pqax;

/// <summary>Reads, writes, compares, and copies one field of a row or of a nested object, with no boxing.</summary>
/// <typeparam name="T">The row or nested object type.</typeparam>
internal abstract class FieldAccessor<T>
    where T : class
{
    protected FieldAccessor(FieldInfo info)
    {
        Info = info;
    }

    public FieldInfo Info { get; }

    public string Name => Info.Name;

    /// <summary>Reads the value at the current token and leaves the reader on the last token of the value.</summary>
    public abstract void Read(T target, ref Utf8JsonReader reader, ReadContext context);

    /// <summary>Whether the field has a value, which is anything except null.</summary>
    public abstract bool HasValue(T target);

    /// <summary>Writes the value, or null when there is none.</summary>
    public abstract void Write(Utf8JsonWriter writer, T target);

    public abstract bool ValueEquals(T a, T b);

    public abstract int ValueHash(T target);

    /// <summary>Copies the value, with a copy of a nested object.</summary>
    public abstract void Copy(T source, T target);
}
