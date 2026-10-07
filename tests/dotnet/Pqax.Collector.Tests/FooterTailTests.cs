namespace Pqax.Collector.Tests;

/// <summary>
/// The adaptive footer tail: FooterTailPlanner, and FooterReader with a tail chosen by the caller. The first file of a
/// table is read with the initial tail; the next ones with the largest footer seen, plus its 8 bytes, times the margin,
/// never below the floor and never above the initial tail. A floor above the initial tail is capped at it.
/// </summary>
public class FooterTailTests
{
    /// <summary>The tail that the planner must give after footers whose largest needs this many bytes, from the rule above.</summary>
    internal static int Expected(int initial, int floor, double margin, int largestNeeded) =>
        largestNeeded == 0 ? initial : (int)Math.Min(initial, Math.Max(Math.Min(floor, initial), Math.Ceiling(largestNeeded * margin)));

    [Fact]
    public void The_planner_starts_at_the_initial_tail()
    {
        var planner = new FooterTailPlanner(32768, 8192, 1.25);
        Assert.Equal(32768, planner.Current);
        Assert.Equal(0, planner.LargestFooterBytes);
    }

    [Fact]
    public void After_the_first_footer_the_tail_shrinks_to_the_floor_or_to_the_footer_with_its_margin()
    {
        var withFloor = new FooterTailPlanner(32768, 8192, 1.25);
        withFloor.Observe(1139);
        Assert.Equal(8192, withFloor.Current);
        Assert.Equal(1147, withFloor.LargestFooterBytes);

        var tinyFloor = new FooterTailPlanner(32768, 12, 1.25);
        tinyFloor.Observe(1139);
        Assert.Equal(1434, tinyFloor.Current);

        var exact = new FooterTailPlanner(32768, 12, 1.0);
        exact.Observe(1139);
        Assert.Equal(1147, exact.Current);
    }

    [Fact]
    public void A_larger_footer_raises_the_tail_and_a_smaller_one_does_not_lower_it()
    {
        var planner = new FooterTailPlanner(32768, 12, 1.25);
        planner.Observe(1139);
        Assert.Equal(1434, planner.Current);
        planner.Observe(1813);
        Assert.Equal(2277, planner.Current);
        Assert.Equal(1821, planner.LargestFooterBytes);
        planner.Observe(449);
        Assert.Equal(2277, planner.Current);
        Assert.Equal(1821, planner.LargestFooterBytes);
    }

    [Fact]
    public void The_tail_never_goes_above_the_initial_tail()
    {
        var planner = new FooterTailPlanner(32768, 12, 1.25);
        planner.Observe(40000);
        Assert.Equal(32768, planner.Current);
        Assert.Equal(40008, planner.LargestFooterBytes);

        var doubled = new FooterTailPlanner(4096, 12, 2.0);
        doubled.Observe(3000);
        Assert.Equal(4096, doubled.Current);
    }

    [Fact]
    public void The_tail_never_goes_below_the_floor()
    {
        var planner = new FooterTailPlanner(32768, 8192, 1.25);
        planner.Observe(0);
        Assert.Equal(8192, planner.Current);
        planner.Observe(10);
        Assert.Equal(8192, planner.Current);
    }

    [Fact]
    public void A_floor_above_the_initial_tail_is_capped_at_it()
    {
        var planner = new FooterTailPlanner(4096, 8192, 1.25);
        Assert.Equal(4096, planner.Current);
        planner.Observe(100);
        Assert.Equal(4096, planner.Current);

        var smallest = new FooterTailPlanner(12, 8192, 1.25);
        Assert.Equal(12, smallest.Current);
        smallest.Observe(1000);
        Assert.Equal(12, smallest.Current);
    }

    [Theory]
    [InlineData(32768, 8192, 1.25, 1)]
    [InlineData(32768, 12, 1.0, 2)]
    [InlineData(1000, 12, 1.5, 3)]
    [InlineData(65536, 65536, 1.0, 4)]
    [InlineData(4096, 8192, 3.0, 5)]
    public void Any_sequence_of_footers_follows_the_rule(int initial, int floor, double margin, int seed)
    {
        var random = new Random(seed);
        var planner = new FooterTailPlanner(initial, floor, margin);
        int largest = 0;
        for (int i = 0; i < 500; i++)
        {
            int footer = random.Next(4) == 0 ? random.Next(200_000) : random.Next(5_000);
            planner.Observe(footer);
            largest = Math.Max(largest, footer + 8);
            Assert.Equal(largest, planner.LargestFooterBytes);
            Assert.Equal(Expected(initial, floor, margin, largest), planner.Current);
            Assert.InRange(planner.Current, Math.Min(floor, initial), initial);
        }
    }

    [Fact]
    public void Observations_from_many_threads_keep_the_largest_footer()
    {
        var planner = new FooterTailPlanner(32768, 12, 1.25);
        Parallel.For(0, 2000, i => planner.Observe(i % 1000));
        Assert.Equal(999 + 8, planner.LargestFooterBytes);
        Assert.Equal(Expected(32768, 12, 1.25, 1007), planner.Current);
    }

    [Theory]
    [InlineData(11, 8192, 1.25, "initialTailBytes")]
    [InlineData(32768, 11, 1.25, "floorBytes")]
    [InlineData(32768, 8192, 0.99, "margin")]
    [InlineData(32768, 8192, double.NaN, "margin")]
    [InlineData(32768, 8192, double.PositiveInfinity, "margin")]
    [InlineData(32768, 8192, double.NegativeInfinity, "margin")]
    public void Arguments_out_of_range_are_refused(int initial, int floor, double margin, string parameter)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new FooterTailPlanner(initial, floor, margin));
        Assert.Equal(parameter, error.ParamName);
    }

    [Fact]
    public void A_negative_footer_length_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FooterTailPlanner(32768, 8192, 1.25).Observe(-1));
    }

    [Fact]
    public void The_smallest_arguments_and_a_floor_above_the_tail_are_accepted()
    {
        Assert.Null(Record.Exception(() => new FooterTailPlanner(12, 12, 1.0)));
        Assert.Null(Record.Exception(() => new FooterTailPlanner(12, int.MaxValue, 1.0)));
        Assert.Null(Record.Exception(() => new FooterTailPlanner(32768, 40000, 1.25)));
    }

    // ---- the reader with a tail chosen by the caller ----

    private static (string Path, long Size, int Footer) DataFile(string path)
    {
        long size = new FileInfo(Path.Combine(Fixture.Folder, path)).Length;
        return (path, size, (int)Dump.Long(Fixture.DumpFileOfSize(size).File["footer"]!["footerSize"]));
    }

    [Fact]
    public async Task The_reader_takes_the_tail_of_each_call()
    {
        (string path, long size, int footer) = DataFile(Fixture.DataFiles()[0]);
        var storage = new TestStorage(new LocalStorage(Fixture.Folder));
        var reader = new FooterReader(storage);
        ParquetFooter shortTail = await reader.ReadAsync(path, size, 12, CancellationToken.None);
        Assert.Equal(2, shortTail.RequestCount);
        Assert.Equal(12, storage.Requests[0].Length);
        ParquetFooter exact = await reader.ReadAsync(path, size, footer + 8, CancellationToken.None);
        Assert.Equal(1, exact.RequestCount);
        Assert.Equal(footer + 8, storage.Requests[^1].Length);
        ParquetFooter byDefault = await reader.ReadAsync(path, size, CancellationToken.None);
        Assert.Equal(1, byDefault.RequestCount);
        Assert.Equal(Math.Min(size, reader.TailBytes), storage.Requests[^1].Length);
        foreach (ParquetFooter parsed in new[] { shortTail, exact })
        {
            Assert.Equal(footer, parsed.FooterLength);
            Assert.Equal(byDefault.NumRows, parsed.NumRows);
            Assert.Equal(byDefault.RowGroups.Select(group => group.TotalCompressedSize), parsed.RowGroups.Select(group => group.TotalCompressedSize));
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(path, size, 11, CancellationToken.None));
    }

    [Fact]
    public async Task A_later_larger_footer_costs_a_second_request_and_raises_the_tail()
    {
        var files = Fixture.DataFiles().Select(DataFile).OrderBy(file => file.Footer).ToList();
        Assert.Equal(files.Count, files.Select(file => file.Footer).Distinct().Count());
        var planner = new FooterTailPlanner(FooterReader.DefaultTailBytes, 12, 1.0);
        var reader = new FooterReader(new LocalStorage(Fixture.Folder));
        for (int i = 0; i < files.Count; i++)
        {
            (string path, long size, int footer) = files[i];
            int tail = planner.Current;
            ParquetFooter parsed = await reader.ReadAsync(path, size, tail, CancellationToken.None);
            int expected = footer + 8 <= Math.Min(tail, size) ? 1 : 2;
            Assert.True(expected == parsed.RequestCount, $"{path}: tail {tail}, footer {footer}, {parsed.RequestCount} requests");
            if (i > 0)
            {
                Assert.True(size > tail, $"{path} is shorter than the tail, so the case does not test a second request");
                Assert.Equal(2, parsed.RequestCount);
            }

            planner.Observe(parsed.FooterLength);
            Assert.Equal(footer + 8, planner.Current);
        }
    }
}
