namespace Pqax.Tests;

/// <summary>
/// The derived measures of section 10 of <c>docs/object-model.md</c> that <see cref="StorageMeasures"/> offers, on
/// footprints built by hand: the storage stack when a component is absent (10.2), and the pace of the commits (10.4).
/// The expected values come from the text of section 10, not from the code. A measure that needs an absent field is
/// null, never 0: the principle "Unknown is not zero" of section 2 and rule 2 of 10.1. The assertions compare with
/// <c>double?</c>, so that they hold whether a sum of the stack is a <c>double</c> or a <c>double?</c>.
/// </summary>
public class Section10MeasuresTests
{
    private static readonly double? Null = null;

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static StorageStack Stack(double? fileSize = null, double? deleted = null, double? footerSize = null, double? staleColumnSize = null,
        double? nonLiveFileSize = null, double? unreferencedFileSize = null, double? metadataSize = null) =>
        StorageMeasures.StackOf(new StackInput
        {
            FileSize = fileSize,
            Deleted = deleted,
            FooterSize = footerSize,
            StaleColumnSize = staleColumnSize,
            NonLiveFileSize = nonLiveFileSize,
            UnreferencedFileSize = unreferencedFileSize,
            MetadataSize = metadataSize,
        });

    /// <summary>A footprint written by <see cref="FootprintBuilder"/> with one source and the tables given, opened by the reader.</summary>
    private static PqaxDocument Footprint(CollectionInfo collection, params TableRow[] tables)
    {
        var builder = new FootprintBuilder(collection);
        builder.AddSource(new SourceRow { Kind = "Storage folder" });
        foreach (TableRow table in tables)
        {
            table.Source = 1;
            table.Id = builder.AddTable(table);
        }

        using var stream = new MemoryStream();
        builder.Write(stream);
        return PqaxDocument.Open(new MemoryStream(stream.ToArray()));
    }

    private static CollectionInfo Collected => new() { CollectorApp = "pqax-tests", StartedAt = Utc(2026, 10, 1), CompletedAt = Utc(2026, 10, 11, 12), Level = 2 };

    // ---- 10.2 the storage stack ----

    [Fact]
    public void Rule_10_2_Data_is_null_when_Live_is_null_and_so_are_the_shares_while_the_sums_add_what_is_present()
    {
        // FileSize is absent, so Live is null. NonLive 7, Unreferenced absent, Deleted 10, Stale 2: Waste 19.
        // Metadata 3, Footers 5: Overhead 8. Total: the seven components, the absent ones as 0, 27.
        StorageStack stack = Stack(null, 10, 5, 2, 7, null, 3);
        Assert.Null(stack.Live);
        Assert.Equal(Null, stack.Data);
        Assert.Equal((double?)19, stack.Waste);
        Assert.Equal((double?)8, stack.Overhead);
        Assert.Equal((double?)27, stack.Total);
        Assert.Null(stack.WasteShare);
        Assert.Null(stack.OverheadShare);
    }

    [Fact]
    public void Rule_10_2_Waste_Overhead_and_Total_are_null_when_all_their_components_are_absent()
    {
        StorageStack stack = Stack();
        Assert.All(StorageMeasures.StackComponents, key => Assert.Null(stack.Component(key)));
        Assert.Equal(Null, stack.Waste);
        Assert.Equal(Null, stack.Overhead);
        Assert.Equal(Null, stack.Total);
        Assert.Equal(Null, stack.Data);
        Assert.Null(stack.WasteShare);
        Assert.Null(stack.OverheadShare);
    }

    [Fact]
    public void Rule_10_2_Total_with_only_MetadataSize_is_MetadataSize_and_Waste_is_null()
    {
        StorageStack stack = Stack(metadataSize: 30);
        Assert.Equal((double?)30, stack.Total);
        Assert.Equal((double?)30, stack.Overhead);
        Assert.Equal(Null, stack.Waste);
        Assert.Equal(Null, stack.Data);
    }

    [Fact]
    public void Rule_10_2_Waste_with_only_StaleColumnSize_is_StaleColumnSize_and_Overhead_is_null()
    {
        StorageStack stack = Stack(staleColumnSize: 20);
        Assert.Equal((double?)20, stack.Waste);
        Assert.Equal(Null, stack.Overhead);
        Assert.Equal((double?)20, stack.Total);
        Assert.Equal(Null, stack.Data);
    }

    [Fact]
    public void Rule_10_2_Waste_and_Overhead_sum_the_present_components_an_absent_one_counting_as_0()
    {
        // NonLive 100, Unreferenced absent (no listing), Deleted absent, Stale 20: Waste 120. Live 980, Data 1100.
        StorageStack stack = Stack(1000, staleColumnSize: 20, nonLiveFileSize: 100);
        Assert.Equal((double?)120, stack.Waste);
        Assert.Equal((double?)1100, stack.Data);
        Assert.Equal((double?)1100, stack.Total);
        Assert.Equal(Null, stack.Overhead);

        Assert.Equal((double?)40, Stack(1000, footerSize: 40).Overhead);
        Assert.Equal((double?)25, Stack(1000, metadataSize: 25).Overhead);
        // A present zero is a value: the overhead is 0, not null.
        Assert.Equal((double?)0, Stack(1000, metadataSize: 0).Overhead);
    }

    [Fact]
    public void Rule_10_2_WasteShare_and_OverheadShare_are_null_when_Waste_or_Overhead_is_null_even_when_Data_is_known()
    {
        // Only FileSize: Live 1000, Waste and Overhead null. Data = Live + Waste is null only when Live is null.
        StorageStack stack = Stack(1000);
        Assert.Equal(1000.0, stack.Live);
        Assert.Equal(Null, stack.Waste);
        Assert.Equal(Null, stack.Overhead);
        Assert.Equal((double?)1000, stack.Total);
        Assert.Equal((double?)1000, stack.Data);
        // 10.1 rule 2: a measure is null when a field that it needs is absent.
        Assert.Null(stack.WasteShare);
        Assert.Null(stack.OverheadShare);
    }

    [Fact]
    public void Rule_10_2_the_shares_are_null_when_Data_is_0()
    {
        StorageStack stack = Stack(0, 0, 0, 0, 0, 0, 30);
        Assert.Equal((double?)0, stack.Data);
        Assert.Equal((double?)30, stack.Total);
        Assert.Null(stack.WasteShare);
        Assert.Null(stack.OverheadShare);
    }

    [Fact]
    public void Rule_10_2_a_table_without_FileSize_has_a_null_Data_and_null_shares()
    {
        using PqaxDocument doc = Footprint(Collected,
            new TableRow { Name = "no_file_size", Format = "Delta", DetailDepth = "Summary", RowCount = 100, DeletedRowCount = 0, FooterSize = 5, StaleColumnSize = 2, NonLiveFileSize = 7, MetadataSize = 3 },
            new TableRow { Name = "bare", Format = "Delta", DetailDepth = "Summary" });
        IReadOnlyList<TableStorage> tables = StorageMeasures.ForTables(doc.Summary, null, doc.Manifest.Collection);

        StorageStack stack = tables[0].Stack;
        // Deleted is 0 since DeletedRowCount is 0, whatever the other fields.
        Assert.Equal(0.0, stack.Deleted);
        Assert.Null(stack.Live);
        Assert.Equal(Null, stack.Data);
        Assert.Equal((double?)(7 + 0 + 2), stack.Waste);
        Assert.Equal((double?)(3 + 5), stack.Overhead);
        Assert.Equal((double?)(9 + 8), stack.Total);
        Assert.Null(stack.WasteShare);
        Assert.Null(stack.OverheadShare);

        StorageStack bare = tables[1].Stack;
        Assert.Equal(Null, bare.Data);
        Assert.Equal(Null, bare.Waste);
        Assert.Equal(Null, bare.Overhead);
        Assert.Equal(Null, bare.Total);
        Assert.Null(bare.WasteShare);
    }

    // ---- 10.4 the history of a table ----

    [Fact]
    public void Rule_10_4_CommitsPerDay_is_CommitsRead_over_the_days_of_the_span_at_least_one_and_never_CommitCount()
    {
        using PqaxDocument doc = Footprint(Collected,
            new TableRow { Name = "ten_days", CommitsRead = 20, CommitCount = 100, OldestCommitAt = Utc(2026, 9, 1), SnapshotTimestamp = Utc(2026, 9, 11) },
            new TableRow { Name = "count_only", CommitCount = 100, OldestCommitAt = Utc(2026, 9, 1), SnapshotTimestamp = Utc(2026, 9, 11) },
            new TableRow { Name = "six_hours", CommitsRead = 5, CommitCount = 100, OldestCommitAt = Utc(2026, 9, 1), SnapshotTimestamp = Utc(2026, 9, 1, 6) },
            new TableRow { Name = "a_day_and_a_half", CommitsRead = 6, OldestCommitAt = Utc(2026, 9, 1), SnapshotTimestamp = Utc(2026, 9, 2, 12) },
            new TableRow { Name = "no_oldest", CommitsRead = 20, SnapshotTimestamp = Utc(2026, 9, 11) });
        IReadOnlyList<TableStorage> tables = StorageMeasures.ForTables(doc.Summary, null, doc.Manifest.Collection);

        // 20 over 10 days.
        Assert.Equal(2.0, tables[0].Maintenance.CommitsPerDay);
        // No CommitsRead: null, although CommitCount is present.
        Assert.Null(tables[1].Maintenance.CommitsPerDay);
        // A span shorter than a day counts as one day: 5 commits in 6 hours give 5.
        Assert.Equal(5.0, tables[2].Maintenance.CommitsPerDay);
        // The days as a fraction: 6 commits over 1.5 days.
        Assert.Equal(4.0, tables[3].Maintenance.CommitsPerDay);
        // No OldestCommitAt: null.
        Assert.Null(tables[4].Maintenance.CommitsPerDay);
    }

    [Fact]
    public void Rule_10_4_DaysSinceLastCommit_counts_to_CompletedAt_else_to_StartedAt_when_CompletedAt_is_absent()
    {
        var table = new TableRow { Name = "t", SnapshotTimestamp = Utc(2026, 10, 1, 12) };
        using (PqaxDocument completed = Footprint(new CollectionInfo { StartedAt = Utc(2026, 10, 4), CompletedAt = Utc(2026, 10, 11, 12), Level = 1 }, table))
        {
            // floor(10 days) to CompletedAt.
            Assert.Equal(10L, Assert.Single(StorageMeasures.ForTables(completed.Summary, null, completed.Manifest.Collection)).Maintenance.DaysSinceLastCommit);
        }

        var started = new CollectionInfo { StartedAt = Utc(2026, 10, 4), CompletedAt = null, Level = 1 };
        using (PqaxDocument doc = Footprint(started, new TableRow { Name = "t", SnapshotTimestamp = Utc(2026, 10, 1, 12) }))
        {
            // floor(2.5 days) to StartedAt.
            Assert.Equal(2L, Assert.Single(StorageMeasures.ForTables(doc.Summary, null, doc.Manifest.Collection)).Maintenance.DaysSinceLastCommit);
            Assert.Equal(2L, Assert.Single(StorageMeasures.ForTables(doc.Summary, null, started)).Maintenance.DaysSinceLastCommit);
            // Neither: null.
            Assert.Null(Assert.Single(StorageMeasures.ForTables(doc.Summary, null, new CollectionInfo { Level = 1 })).Maintenance.DaysSinceLastCommit);
            Assert.Null(Assert.Single(StorageMeasures.ForTables(doc.Summary, null, null)).Maintenance.DaysSinceLastCommit);
            // max(0, ...): a collection that ends before the last commit gives 0.
            Assert.Equal(0L, Assert.Single(StorageMeasures.ForTables(doc.Summary, null, new CollectionInfo { CompletedAt = Utc(2026, 9, 1) })).Maintenance.DaysSinceLastCommit);
        }
    }
}
