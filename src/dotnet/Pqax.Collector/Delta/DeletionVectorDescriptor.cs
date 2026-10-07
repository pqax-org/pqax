using System.Globalization;

namespace Pqax.Collector;

/// <summary>
/// The deletion vector of a data file: the rows of the file that are marked as deleted. The collector keeps the number
/// of rows and the size; the location serves only to identify the file in the log.
/// </summary>
public sealed class DeletionVectorDescriptor
{
    /// <summary><c>u</c> for a file next to the table, <c>p</c> for an absolute path, <c>i</c> for a vector inline in the log.</summary>
    public string? StorageType { get; set; }

    /// <summary>The location of the vector, or the vector itself when it is inline.</summary>
    public string? PathOrInlineDv { get; set; }

    /// <summary>The position of the vector in its file.</summary>
    public long? Offset { get; set; }

    /// <summary>The size of the vector in bytes.</summary>
    public long? SizeInBytes { get; set; }

    /// <summary>The rows that the vector marks as deleted.</summary>
    public long? Cardinality { get; set; }

    /// <summary>The unique identifier of the Delta protocol: storage type, location, and offset when there is one.</summary>
    internal string UniqueId => (StorageType ?? string.Empty) + (PathOrInlineDv ?? string.Empty)
        + (Offset == null ? string.Empty : "@" + Offset.Value.ToString(CultureInfo.InvariantCulture));
}
