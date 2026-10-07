namespace Pqax.Collector;

/// <summary>One combination of writing engine and V-Order found in the files of a table.</summary>
internal sealed class CollectedWriter
{
    public CollectedWriter(int firstFile, string? createdBy, bool? isVOrdered, string? vOrderLevel)
    {
        FirstFile = firstFile;
        CreatedBy = createdBy;
        IsVOrdered = isVOrdered;
        VOrderLevel = vOrderLevel;
    }

    /// <summary>The position of the first file written this way, which orders the writers.</summary>
    public int FirstFile { get; set; }

    public string? CreatedBy { get; }

    public bool? IsVOrdered { get; }

    public string? VOrderLevel { get; }

    public long FileCount { get; set; }

    public Total FileSize { get; } = Total.Zero();
}
