using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A leaf column of the schema of a footer: a column that row groups store as a column chunk.</summary>
public sealed class ParquetLeafColumn
{
    internal ParquetLeafColumn(int ordinal, IReadOnlyList<string> path, ParquetSchemaElement element, int? topLevelFieldId)
    {
        Ordinal = ordinal;
        Path = path;
        PhysicalType = element.Type ?? string.Empty;
        TypeLength = element.TypeLength;
        Repetition = element.Repetition;
        LogicalType = element.LogicalType;
        ConvertedType = element.ConvertedType;
        Scale = element.Scale;
        Precision = element.Precision;
        FieldId = element.FieldId;
        TopLevelFieldId = topLevelFieldId;
    }

    /// <summary>The position among the leaf columns, from 0, which is also the position of its column chunk in a row group.</summary>
    public int Ordinal { get; }

    /// <summary>The names from the root of the schema to the leaf, the root excluded.</summary>
    public IReadOnlyList<string> Path { get; }

    /// <summary>The physical type, such as <c>INT64</c>.</summary>
    public string PhysicalType { get; }

    /// <summary>The length of a <c>FIXED_LEN_BYTE_ARRAY</c> value.</summary>
    public int? TypeLength { get; }

    /// <summary><c>REQUIRED</c>, <c>OPTIONAL</c>, or <c>REPEATED</c>.</summary>
    public string? Repetition { get; }

    /// <summary>The logical type as text, when the file has one.</summary>
    public string? LogicalType { get; }

    /// <summary>The converted type, when the file has one.</summary>
    public string? ConvertedType { get; }

    /// <summary>The scale of a decimal, for the converted type.</summary>
    public int? Scale { get; }

    /// <summary>The precision of a decimal, for the converted type.</summary>
    public int? Precision { get; }

    /// <summary>The field identifier of the leaf.</summary>
    public int? FieldId { get; }

    /// <summary>The field identifier of the top-level field that holds the leaf, which a table format maps to its column.</summary>
    public int? TopLevelFieldId { get; }

    /// <summary>
    /// The annotation that a footprint records: the logical type when the file has one, else the converted type, with
    /// precision and scale for a decimal. Null when the leaf has neither.
    /// </summary>
    public string? TypeAnnotation
    {
        get
        {
            if (LogicalType != null)
            {
                return LogicalType;
            }

            if (ConvertedType == "DECIMAL" && Precision != null && Scale != null)
            {
                return "DECIMAL(" + Precision.Value + "," + Scale.Value + ")";
            }

            return ConvertedType;
        }
    }

    /// <inheritdoc/>
    public override string ToString() => string.Join(".", Path) + " " + PhysicalType;
}
