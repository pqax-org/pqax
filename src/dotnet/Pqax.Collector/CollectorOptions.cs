using System;

namespace Pqax.Collector;

/// <summary>What a <see cref="PqaxCollector"/> reads and what it keeps.</summary>
public sealed class CollectorOptions
{
    /// <summary>
    /// The collection level: 1 reads the Delta logs only, 2 also reads the Parquet footers. The default is 2. Levels 3
    /// and 4 are not supported in this version.
    /// </summary>
    public int Level { get; set; } = 2;

    /// <summary>
    /// The detail kept for every table. The default is <see cref="DetailDepth.ColumnChunks"/>. A table keeps less when
    /// the detail cannot be complete: <see cref="DetailDepth.Summary"/> when its files are unknown, and at most
    /// <see cref="DetailDepth.Files"/> when no footer was read.
    /// </summary>
    public DetailDepth Depth { get; set; } = DetailDepth.ColumnChunks;

    /// <summary>
    /// The footers read for each table at level 2, chosen evenly over the files sorted by modification time. 0, the
    /// default, reads every footer. With a limit, the totals of level 2 cover the files read, as <c>footerCount</c> says.
    /// </summary>
    public int MaxFootersPerTable { get; set; }

    /// <summary>
    /// The commit files read for each table, which give its operations and its commits. Each one costs a request. The
    /// collector reads every commit that the state of the table needs: those after the checkpoint, or all of them when
    /// the state comes from the JSON commits alone. Then it reads older ones, the most recent first, until the commit
    /// files read reach the limit or the log has no older one. So a table reads the greater of the limit and the commits
    /// that the state needs, within the commits that the log keeps. 0 reads only the commits that the state needs. The
    /// default is 30.
    /// </summary>
    /// <remarks>
    /// <c>commitsRead</c> is the number of commit files read. The <c>commits</c> of the table, kept from the
    /// <see cref="DetailDepth.Files"/> depth, hold every one of them, and the operations and <c>oldestCommitAt</c> cover
    /// them all. A file whose read fails counts toward the limit, since it cost a request, and gets an issue instead of a
    /// place in <c>commitsRead</c> and in the commits. For example, with a limit of 30, a checkpoint at version 100, and version 145 the latest, the collector
    /// reads the 45 commits 101 to 145 and no older one; with a limit of 50 it also reads 100 to 96.
    /// </remarks>
    public int MaxCommitsPerTable { get; set; } = 30;

    /// <summary>The storage requests in flight at the same time, for the whole collection. The default is 16.</summary>
    public int Parallelism { get; set; } = 16;

    /// <summary>
    /// Whether to keep data values: the bounds of the column chunks, the parameters of the operations, and the
    /// constraints of the table properties. The default is false.
    /// </summary>
    public bool IncludeDataValues { get; set; }

    /// <summary>
    /// Whether to keep partition values: the values of each partition, and the file paths as the log has them. Without
    /// them, a value in a path becomes a number, as in <c>Year=#1</c>. The default is false.
    /// </summary>
    public bool IncludePartitionValues { get; set; }

    /// <summary>
    /// The bytes read from the end of the first data file of a table, and the most read from any file in one request.
    /// The next files of the table are read with a tail learned from the footers seen so far: see
    /// <see cref="FooterTailMargin"/> and <see cref="FooterTailFloorBytes"/>. The default is 32,768.
    /// </summary>
    public int FooterTailBytes { get; set; } = FooterReader.DefaultTailBytes;

    /// <summary>
    /// The factor applied to the largest footer of a table seen so far, to choose the tail of its next files. 1 reads
    /// exactly the largest footer, and a larger footer then costs a second request. The default is 1.25.
    /// </summary>
    public double FooterTailMargin { get; set; } = 1.25;

    /// <summary>The fewest bytes read from the end of a data file, whatever the footers seen, capped by <see cref="FooterTailBytes"/>. The default is 8,192.</summary>
    public int FooterTailFloorBytes { get; set; } = 8192;

    /// <summary>Checks the values and throws when one is out of range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate()
    {
        if (Level < 1 || Level > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(Level), Level, "The collector supports levels 1 and 2.");
        }

        if (Depth < DetailDepth.Summary || Depth > DetailDepth.ColumnChunks)
        {
            throw new ArgumentOutOfRangeException(nameof(Depth), Depth, "Unknown depth.");
        }

        if (MaxFootersPerTable < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFootersPerTable), MaxFootersPerTable, "The limit is negative.");
        }

        if (MaxCommitsPerTable < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCommitsPerTable), MaxCommitsPerTable, "The limit is negative.");
        }

        if (Parallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Parallelism), Parallelism, "At least one request must be allowed.");
        }

        if (FooterTailBytes < 12 || FooterTailBytes > FooterReader.MaxFooterLength)
        {
            throw new ArgumentOutOfRangeException(nameof(FooterTailBytes), FooterTailBytes, "The tail must hold at least 12 bytes and at most the largest footer.");
        }

        if (FooterTailMargin < 1.0 || double.IsNaN(FooterTailMargin) || double.IsInfinity(FooterTailMargin))
        {
            throw new ArgumentOutOfRangeException(nameof(FooterTailMargin), FooterTailMargin, "The margin must be at least 1.");
        }

        if (FooterTailFloorBytes < 12)
        {
            throw new ArgumentOutOfRangeException(nameof(FooterTailFloorBytes), FooterTailFloorBytes, "The floor must hold at least the 12 bytes around a footer.");
        }
    }
}
