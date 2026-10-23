namespace Pqax.Collector;

/// <summary>The table formats that the collector reads, as the object model spells them.</summary>
public static class TableFormats
{
    /// <summary>A Delta table: a folder with <c>_delta_log</c>.</summary>
    public const string Delta = "Delta";

    /// <summary>A folder of Parquet files with no table format.</summary>
    public const string Parquet = "Parquet";
}
