using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>A top-level column of a table, with the totals of its column chunks.</summary>
internal sealed class CollectedColumn
{
    public CollectedColumn(string name, string dataType, string fileName)
    {
        Name = name;
        DataType = dataType;
        FileName = fileName;
    }

    public long? FieldId { get; set; }

    public string Name { get; }

    public string DataType { get; }

    public bool? IsNullable { get; set; }

    /// <summary>The physical name, when it differs from the name.</summary>
    public string? PhysicalName { get; set; }

    /// <summary>The name that the files and the statistics of the log use.</summary>
    public string FileName { get; }

    public bool IsPartitionColumn { get; set; }

    public string? Description { get; set; }

    public Total CompressedSize { get; } = new Total();

    public Total UncompressedSize { get; } = new Total();

    public Total ValueCount { get; } = new Total();

    public Total NullCount { get; } = new Total();

    public Total ChunkCount { get; } = new Total();

    public Total DictionaryChunkCount { get; } = new Total();

    public Total FallbackChunkCount { get; } = new Total();

    public Total DictionaryPageSize { get; } = new Total();

    public Total UnencodedByteArrayDataBytes { get; } = new Total();

    public SortedSet<string> Codecs { get; } = new SortedSet<string>(System.StringComparer.Ordinal);

    public SortedSet<string> Encodings { get; } = new SortedSet<string>(System.StringComparer.Ordinal);

    public long? FileCountWithoutColumn { get; set; }

    public long? LargestChunkDistinctCount { get; set; }

    /// <summary>The null count from the statistics of the log, used when no footer was read.</summary>
    public long? LogNullCount { get; set; }

    /// <summary>Whether a physical column of a file read maps to this column.</summary>
    public bool IsStored { get; set; }
}
