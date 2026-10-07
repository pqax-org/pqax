using System;
using System.IO.Compression;

namespace Pqax;

/// <summary>How a <see cref="FootprintBuilder"/> writes a file.</summary>
public sealed class FootprintBuilderOptions
{
    /// <summary>The largest number of rows that a detail part may hold, which the format fixes at 250,000.</summary>
    public const int MaxRowsPerPart = 250_000;

    private int rowsPerPart = MaxRowsPerPart;

    /// <summary>
    /// The rows of a detail table in one part, from 1 to 250,000. A table with more rows continues in more parts,
    /// named <c>table.1.json</c>, <c>table.2.json</c>, and so on. The default is 250,000.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1 or above 250,000.</exception>
    public int RowsPerPart
    {
        get => rowsPerPart;
        set
        {
            if (value < 1 || value > MaxRowsPerPart)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"A detail part holds from 1 to {MaxRowsPerPart} rows.");
            }

            rowsPerPart = value;
        }
    }

    /// <summary>The deflate level of every entry. The default is <see cref="CompressionLevel.Optimal"/>.</summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;
}
