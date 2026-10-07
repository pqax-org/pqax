using System.Text.Json;

namespace Pqax;

/// <summary>Keeps the buffer of a <see cref="Utf8JsonWriter"/> small while a large part is written.</summary>
internal static class JsonFlush
{
    private const int Threshold = 1 << 16;

    public static void IfNeeded(Utf8JsonWriter writer)
    {
        if (writer.BytesPending > Threshold)
        {
            writer.Flush();
        }
    }

    /// <summary>Writes an array of extra values from <paramref name="start"/>, when at least one of them has a value.</summary>
    public static void WriteExtraField(Utf8JsonWriter writer, string name, System.Collections.Generic.IReadOnlyList<JsonElement?> values, int start, int count)
    {
        int end = start + count;
        bool any = false;
        for (int i = start; i < end && !any; i++)
        {
            any = i < values.Count && values[i].HasValue;
        }

        if (!any)
        {
            return;
        }

        writer.WriteStartArray(name);
        for (int i = start; i < end; i++)
        {
            JsonElement? value = i < values.Count ? values[i] : null;
            if (value.HasValue)
            {
                value.Value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            IfNeeded(writer);
        }

        writer.WriteEndArray();
    }
}
