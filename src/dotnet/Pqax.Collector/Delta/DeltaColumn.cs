namespace Pqax.Collector;

/// <summary>A top-level column of the schema of a Delta table.</summary>
internal sealed class DeltaColumn
{
    public DeltaColumn(string name, string dataType)
    {
        Name = name;
        DataType = dataType;
    }

    /// <summary>The name of the column.</summary>
    public string Name { get; }

    /// <summary>The type, as <c>long</c>, <c>decimal(18,2)</c>, <c>array&lt;string&gt;</c>, or <c>struct&lt;a:int,b:string&gt;</c>.</summary>
    public string DataType { get; }

    /// <summary>Whether the column accepts nulls.</summary>
    public bool? IsNullable { get; set; }

    /// <summary>The <c>delta.columnMapping.id</c> of the column.</summary>
    public long? FieldId { get; set; }

    /// <summary>The <c>delta.columnMapping.physicalName</c> of the column, when it has one.</summary>
    public string? MappedName { get; set; }

    /// <summary>The comment on the column.</summary>
    public string? Description { get; set; }

    /// <summary>The name that the files use: the physical name with column mapping, the name otherwise.</summary>
    public string FileName => MappedName ?? Name;
}
