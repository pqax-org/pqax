using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Pqax.Collector;

/// <summary>
/// Reads the <c>schemaString</c> of a Delta table: its top-level columns, with the type written as Spark writes a type
/// in DDL, such as <c>decimal(18,2)</c>, <c>array&lt;string&gt;</c>, <c>map&lt;string,int&gt;</c>, or <c>struct&lt;a:int,b:string&gt;</c>.
/// </summary>
internal static class DeltaSchema
{
    /// <summary>Parses the schema.</summary>
    /// <exception cref="JsonException">The text is not a valid schema.</exception>
    public static List<DeltaColumn> Parse(string schemaString)
    {
        using JsonDocument document = JsonDocument.Parse(schemaString);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("fields", out JsonElement fields) || fields.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The schema is not a struct with fields.");
        }

        var columns = new List<DeltaColumn>();
        foreach (JsonElement field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("A field of the schema has no name.");
            }

            string type = field.TryGetProperty("type", out JsonElement typeElement) ? TypeText(typeElement) : "unknown";
            var column = new DeltaColumn(name.GetString() ?? string.Empty, type);
            if (field.TryGetProperty("nullable", out JsonElement nullable) && (nullable.ValueKind == JsonValueKind.True || nullable.ValueKind == JsonValueKind.False))
            {
                column.IsNullable = nullable.GetBoolean();
            }

            if (field.TryGetProperty("metadata", out JsonElement metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                if (metadata.TryGetProperty("delta.columnMapping.id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long fieldId))
                {
                    column.FieldId = fieldId;
                }

                if (metadata.TryGetProperty("delta.columnMapping.physicalName", out JsonElement physical) && physical.ValueKind == JsonValueKind.String)
                {
                    column.MappedName = physical.GetString();
                }

                if (metadata.TryGetProperty("comment", out JsonElement comment) && comment.ValueKind == JsonValueKind.String)
                {
                    column.Description = comment.GetString();
                }
            }

            columns.Add(column);
        }

        return columns;
    }

    private static string TypeText(JsonElement type)
    {
        var text = new StringBuilder();
        AppendType(text, type, 0);
        return text.ToString();
    }

    private static void AppendType(StringBuilder text, JsonElement type, int depth)
    {
        if (depth > 64)
        {
            throw new JsonException("The schema is nested too deep.");
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            text.Append(type.GetString());
            return;
        }

        if (type.ValueKind != JsonValueKind.Object || !type.TryGetProperty("type", out JsonElement kind) || kind.ValueKind != JsonValueKind.String)
        {
            text.Append(type.GetRawText());
            return;
        }

        switch (kind.GetString())
        {
            case "struct" when type.TryGetProperty("fields", out JsonElement fields) && fields.ValueKind == JsonValueKind.Array:
                text.Append("struct<");
                int count = 0;
                foreach (JsonElement field in fields.EnumerateArray())
                {
                    if (count++ > 0)
                    {
                        text.Append(',');
                    }

                    text.Append(field.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : "?");
                    text.Append(':');
                    if (field.TryGetProperty("type", out JsonElement fieldType))
                    {
                        AppendType(text, fieldType, depth + 1);
                    }
                }

                text.Append('>');
                return;
            case "array" when type.TryGetProperty("elementType", out JsonElement element):
                text.Append("array<");
                AppendType(text, element, depth + 1);
                text.Append('>');
                return;
            case "map" when type.TryGetProperty("keyType", out JsonElement key) && type.TryGetProperty("valueType", out JsonElement value):
                text.Append("map<");
                AppendType(text, key, depth + 1);
                text.Append(',');
                AppendType(text, value, depth + 1);
                text.Append('>');
                return;
            default:
                // A user-defined type or a kind this version does not know: the JSON as it is.
                text.Append(type.GetRawText());
                return;
        }
    }
}
