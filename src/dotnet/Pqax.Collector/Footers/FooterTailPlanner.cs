using System;

namespace Pqax.Collector;

/// <summary>
/// Chooses how many bytes to read from the end of each data file of a table. The first file is read with the initial
/// tail. The footers of one table have about the same size, because they describe the same columns, so the next files
/// are read with the largest footer seen so far times a margin, never below a floor and never above the initial tail.
/// A footer longer than the tail still arrives, with a second request, and raises the tail for the files that follow.
/// </summary>
public sealed class FooterTailPlanner
{
    private readonly object gate = new object();
    private readonly int initial;
    private readonly int floor;
    private readonly double margin;
    private int current;
    private int largest;

    /// <summary>Creates a planner.</summary>
    /// <param name="initialTailBytes">The bytes read from the end of the first file, and the most read from any file in one request.</param>
    /// <param name="floorBytes">The fewest bytes read from the end of a file, at least 12. A floor above the initial tail counts as the initial tail.</param>
    /// <param name="margin">The factor applied to the largest footer seen, at least 1.</param>
    public FooterTailPlanner(int initialTailBytes, int floorBytes, double margin)
    {
        if (initialTailBytes < 12)
        {
            throw new ArgumentOutOfRangeException(nameof(initialTailBytes), initialTailBytes, "The tail must hold at least the 12 bytes around a footer.");
        }

        if (floorBytes < 12)
        {
            throw new ArgumentOutOfRangeException(nameof(floorBytes), floorBytes, "The floor must hold at least the 12 bytes around a footer.");
        }

        if (margin < 1.0 || double.IsNaN(margin) || double.IsInfinity(margin))
        {
            throw new ArgumentOutOfRangeException(nameof(margin), margin, "The margin must be at least 1.");
        }

        initial = initialTailBytes;
        floor = Math.Min(floorBytes, initialTailBytes);
        this.margin = margin;
        current = initialTailBytes;
    }

    /// <summary>The bytes to read from the end of the next file.</summary>
    public int Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>The longest footer observed, with the 8 bytes that follow it, or 0 before the first observation.</summary>
    public int LargestFooterBytes
    {
        get
        {
            lock (gate)
            {
                return largest;
            }
        }
    }

    /// <summary>Records the length of a footer that was read, and adjusts the tail for the files that follow.</summary>
    /// <param name="footerLength">The length of the footer, without the 8 bytes that follow it.</param>
    public void Observe(int footerLength)
    {
        if (footerLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(footerLength), footerLength, "A footer length is not negative.");
        }

        lock (gate)
        {
            int needed = footerLength + 8;
            if (needed > largest)
            {
                largest = needed;
            }

            double planned = Math.Ceiling(largest * margin);
            current = (int)Math.Min(initial, Math.Max(floor, planned));
        }
    }
}
