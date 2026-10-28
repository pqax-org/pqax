using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Pqax.Collector;

/// <summary>A top-level field of the schema of a footer, with its type in the names of the Delta schema.</summary>
internal sealed class ParquetField
{
    public ParquetField(string name, string dataType, bool isNullable)
    {
        Name = name;
        DataType = dataType;
        IsNullable = isNullable;
    }

    public string Name { get; }

    /// <summary>The type as a Delta schema names it, such as <c>long</c>, <c>decimal(18,2)</c>, or <c>array&lt;string&gt;</c>.</summary>
    public string DataType { get; }

    public bool IsNullable { get; }
}

/// <summary>
/// The columns of a folder of Parquet files, from the schema of a footer: the top-level fields, with the types that
/// Spark gives them when it reads the file, in the names of the Delta schema, so that they compare with those of a
/// Delta table.
/// </summary>
internal static class ParquetTypes
{
    public static List<ParquetField> TopLevelFields(ParquetFooter footer)
    {
        var fields = new List<ParquetField>();
        IReadOnlyList<ParquetSchemaElement> schema = footer.Schema;
        if (schema.Count == 0)
        {
            return fields;
        }

        int count = schema[0].NumChildren ?? 0;
        int index = 1;
        for (int i = 0; i < count && index < schema.Count; i++)
        {
            ParquetSchemaElement element = schema[index];
            var text = new StringBuilder();
            index = AppendField(schema, index, text, 0);
            fields.Add(new ParquetField(element.Name, text.ToString(), element.Repetition == null || element.Repetition == "OPTIONAL"));
        }

        return fields;
    }

    /// <summary>Appends the type of the element at an index, repeated or not, and returns the index after its subtree.</summary>
    private static int AppendField(IReadOnlyList<ParquetSchemaElement> schema, int index, StringBuilder text, int depth)
    {
        ParquetSchemaElement element = schema[index];
        if (element.Repetition == "REPEATED")
        {
            // A repeated field outside a list annotation is a list of its values.
            text.Append("array<");
            int next = AppendType(schema, index, text, depth);
            text.Append('>');
            return next;
        }

        return AppendType(schema, index, text, depth);
    }

    /// <summary>Appends the type of the element at an index, ignoring its repetition, and returns the index after its subtree.</summary>
    private static int AppendType(IReadOnlyList<ParquetSchemaElement> schema, int index, StringBuilder text, int depth)
    {
        // The parser refuses a schema of more than 256 levels, so a footer that it returns never reaches this.
        if (depth > 256)
        {
            throw new ParquetFooterException(IssueCodes.FooterInvalid, "The schema is nested too deep.");
        }

        ParquetSchemaElement element = schema[index];
        if (element.NumChildren == null)
        {
            text.Append(Primitive(element));
            return index + 1;
        }

        int children = element.NumChildren.Value;
        int first = index + 1;
        string? annotation = element.LogicalType ?? element.ConvertedType;
        if ((annotation == "LIST") && children == 1 && first < schema.Count && schema[first].Repetition == "REPEATED")
        {
            ParquetSchemaElement repeated = schema[first];
            text.Append("array<");
            int next;
            if (repeated.NumChildren == 1 && repeated.Name != "array" && repeated.Name != element.Name + "_tuple")
            {
                // The three levels of the specification: the repeated group holds the element.
                next = AppendType(schema, first + 1, text, depth + 1);
            }
            else
            {
                // The two levels of older writers: the repeated field is the element.
                next = AppendType(schema, first, text, depth + 1);
            }

            text.Append('>');
            return next;
        }

        if ((annotation == "MAP" || annotation == "MAP_KEY_VALUE") && children == 1 && first < schema.Count
            && schema[first].Repetition == "REPEATED" && schema[first].NumChildren == 2)
        {
            text.Append("map<");
            int next = AppendType(schema, first + 1, text, depth + 1);
            text.Append(',');
            next = next < schema.Count ? AppendType(schema, next, text, depth + 1) : next;
            text.Append('>');
            return next;
        }

        if (annotation != null && annotation.StartsWith("VARIANT", StringComparison.Ordinal))
        {
            text.Append("variant");
            return Skip(schema, index);
        }

        text.Append("struct<");
        int position = first;
        for (int c = 0; c < children && position < schema.Count; c++)
        {
            if (c > 0)
            {
                text.Append(',');
            }

            text.Append(schema[position].Name).Append(':');
            position = AppendField(schema, position, text, depth + 1);
        }

        text.Append('>');
        return position;
    }

    /// <summary>The index after the subtree of the element at an index.</summary>
    private static int Skip(IReadOnlyList<ParquetSchemaElement> schema, int index)
    {
        int pending = 1;
        int position = index;
        while (pending > 0 && position < schema.Count)
        {
            pending += (schema[position].NumChildren ?? 0) - 1;
            position++;
        }

        return position;
    }

    private static string Primitive(ParquetSchemaElement element)
    {
        string? logical = element.LogicalType;
        string? converted = element.ConvertedType;
        string? decimalType = Decimal(element);
        switch (element.Type)
        {
            case "BOOLEAN":
                return "boolean";
            case "INT32":
                if (decimalType != null)
                {
                    return decimalType;
                }

                if (logical == "DATE" || converted == "DATE")
                {
                    return "date";
                }

                return Integer(logical, converted) switch
                {
                    (8, true) => "byte",
                    (16, true) => "short",
                    (8, false) => "short",
                    (16, false) => "integer",
                    (32, false) => "long",
                    _ => "integer",
                };
            case "INT64":
                if (decimalType != null)
                {
                    return decimalType;
                }

                if (logical != null && logical.StartsWith("TIMESTAMP(", StringComparison.Ordinal))
                {
                    return logical.EndsWith(", LOCAL)", StringComparison.Ordinal) ? "timestamp_ntz" : "timestamp";
                }

                if (converted == "TIMESTAMP_MILLIS" || converted == "TIMESTAMP_MICROS")
                {
                    return "timestamp";
                }

                return Integer(logical, converted) is (64, false) ? "decimal(20,0)" : "long";
            case "INT96":
                return "timestamp";
            case "FLOAT":
                return "float";
            case "DOUBLE":
                return "double";
            case "BYTE_ARRAY":
                if (decimalType != null)
                {
                    return decimalType;
                }

                return logical == "STRING" || logical == "ENUM" || logical == "JSON" || converted == "UTF8" || converted == "ENUM" || converted == "JSON"
                    ? "string"
                    : "binary";
            case "FIXED_LEN_BYTE_ARRAY":
                return decimalType ?? (logical == "FLOAT16" ? "float" : "binary");
            default:
                return "binary";
        }
    }

    /// <summary><c>decimal(p,s)</c> from the logical type or the converted type, or null.</summary>
    private static string? Decimal(ParquetSchemaElement element)
    {
        string? logical = element.LogicalType;
        if (logical != null && logical.StartsWith("DECIMAL(", StringComparison.Ordinal))
        {
            return "decimal" + logical.Substring("DECIMAL".Length).Replace(" ", string.Empty);
        }

        if (element.ConvertedType == "DECIMAL" && element.Precision != null)
        {
            return "decimal(" + element.Precision.Value.ToString(CultureInfo.InvariantCulture) + ","
                + (element.Scale ?? 0).ToString(CultureInfo.InvariantCulture) + ")";
        }

        return null;
    }

    /// <summary>The bit width and the sign of an integer annotation, or null.</summary>
    private static (int Width, bool Signed)? Integer(string? logical, string? converted)
    {
        if (logical != null && logical.StartsWith("INTEGER(", StringComparison.Ordinal))
        {
            // INTEGER(<width>, SIGNED) or INTEGER(<width>, UNSIGNED), as the footer parser writes it.
            int comma = logical.IndexOf(',');
            if (comma > 8 && int.TryParse(logical.Substring(8, comma - 8), NumberStyles.None, CultureInfo.InvariantCulture, out int width))
            {
                return (width, logical.IndexOf("UNSIGNED", StringComparison.Ordinal) < 0);
            }

            return null;
        }

        return converted switch
        {
            "INT_8" => (8, true),
            "INT_16" => (16, true),
            "INT_32" => (32, true),
            "INT_64" => (64, true),
            "UINT_8" => (8, false),
            "UINT_16" => (16, false),
            "UINT_32" => (32, false),
            "UINT_64" => (64, false),
            _ => null,
        };
    }
}
