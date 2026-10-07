namespace Pqax.Collector;

/// <summary>An element of the schema of a footer, in the depth-first order of the file. The first element is the root.</summary>
public sealed class ParquetSchemaElement
{
    internal ParquetSchemaElement()
    {
        Name = string.Empty;
    }

    /// <summary>The name of the field.</summary>
    public string Name { get; internal set; }

    /// <summary>The physical type of a leaf, such as <c>INT64</c> or <c>BYTE_ARRAY</c>. Null for a group.</summary>
    public string? Type { get; internal set; }

    /// <summary>The length of a <c>FIXED_LEN_BYTE_ARRAY</c> value.</summary>
    public int? TypeLength { get; internal set; }

    /// <summary><c>REQUIRED</c>, <c>OPTIONAL</c>, or <c>REPEATED</c>. Null for the root of most files.</summary>
    public string? Repetition { get; internal set; }

    /// <summary>The children of a group. Null for a leaf.</summary>
    public int? NumChildren { get; internal set; }

    /// <summary>The converted type, such as <c>UTF8</c> or <c>INT_64</c>.</summary>
    public string? ConvertedType { get; internal set; }

    /// <summary>The scale of a decimal, for the converted type.</summary>
    public int? Scale { get; internal set; }

    /// <summary>The precision of a decimal, for the converted type.</summary>
    public int? Precision { get; internal set; }

    /// <summary>The field identifier written in the file.</summary>
    public int? FieldId { get; internal set; }

    /// <summary>The logical type as text, such as <c>STRING</c>, <c>DECIMAL(18,2)</c>, or <c>TIMESTAMP(MICROS, UTC)</c>.</summary>
    public string? LogicalType { get; internal set; }

    /// <inheritdoc/>
    public override string ToString() => Type == null ? Name + " (group)" : Name + " " + Type;
}
