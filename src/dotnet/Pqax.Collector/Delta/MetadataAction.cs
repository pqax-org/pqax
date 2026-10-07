using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A <c>metaData</c> action: the schema, the partitioning, and the properties of the table.</summary>
public sealed class MetadataAction : DeltaAction
{
    /// <summary>The identifier of the table, which stays the same across versions.</summary>
    public string? Id { get; set; }

    /// <summary>The name that the log gives to the table.</summary>
    public string? Name { get; set; }

    /// <summary>The description of the table.</summary>
    public string? Description { get; set; }

    /// <summary>The format of the data files, <c>parquet</c>.</summary>
    public string? Provider { get; set; }

    /// <summary>The schema of the table as JSON text.</summary>
    public string? SchemaString { get; set; }

    /// <summary>The partition columns, in order.</summary>
    public IReadOnlyList<string>? PartitionColumns { get; set; }

    /// <summary>The table properties.</summary>
    public IReadOnlyDictionary<string, string>? Configuration { get; set; }

    /// <summary>When the table was created, in milliseconds since 1970.</summary>
    public long? CreatedTime { get; set; }
}
