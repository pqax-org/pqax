namespace Pqax.Collector.Tests;

/// <summary>The fixture collected with the default options, shared by the tests of a class.</summary>
public sealed class DefaultCollection : IAsyncLifetime
{
    public Collected Result { get; private set; } = null!;

    public PqaxDocument Document { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Result = await Fixture.CollectAsync();
        Document = Fixture.Open(Result.Data);
    }

    public Task DisposeAsync()
    {
        Document?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// The fixture, collected at level 2 through LocalStorage, against the samples of the kit that were built from the
/// probe dump of the same fixture: every table and field equal, at every depth (plan 5).
/// </summary>
public class KitTests(DefaultCollection collection) : IClassFixture<DefaultCollection>
{
    public static TheoryData<DetailDepth, string> Depths => new()
    {
        { DetailDepth.Summary, "summary-only.pqax" },
        { DetailDepth.Files, "files.pqax" },
        { DetailDepth.RowGroups, "row-groups.pqax" },
        { DetailDepth.ColumnChunks, "sample.pqax" },
    };

    [Theory]
    [MemberData(nameof(Depths))]
    public async Task Each_depth_gives_the_tables_of_the_kit_sample_of_that_depth(DetailDepth depth, string sample)
    {
        Collected result = await Fixture.CollectAsync(new CollectorOptions { Depth = depth });
        using PqaxDocument actual = Fixture.Open(result.Data);
        using PqaxDocument expected = Fixture.OpenSample(sample);
        Assert.Equal(expected.Manifest.Parts.Select(part => part.Name), actual.Manifest.Parts.Select(part => part.Name));
        foreach (string table in Fixture.StatedTables(expected))
        {
            Assert.True(expected.RowCount(table) == actual.RowCount(table), $"{table}: {actual.RowCount(table)} rows, expected {expected.RowCount(table)}");
        }

        // The samples have no location; a collection from LocalStorage records the full paths of the folders.
        Assert.Empty(Fixture.RawDifferences(actual, expected, Fixture.IsLocation));
        Fixture.AssertLocalLocations(actual);
        Fixture.AssertValid(result.Data);
    }

    [Fact]
    public void The_comparison_of_two_files_sees_a_difference()
    {
        using PqaxDocument files = Fixture.OpenSample("files.pqax");
        List<string> differences = Fixture.RawDifferences(collection.Document, files);
        Assert.Contains(differences, difference => difference.StartsWith("rowGroups:", StringComparison.Ordinal));
        Assert.Contains(differences, difference => difference.StartsWith("columnChunks:", StringComparison.Ordinal));
        Assert.Contains(differences, difference => difference.StartsWith("tables.detailDepth:", StringComparison.Ordinal));
        Assert.Empty(Fixture.RawDifferences(collection.Document, collection.Document));
    }

    [Fact]
    public void The_default_collection_is_valid_and_has_the_rows_of_sample_pqax()
    {
        Fixture.AssertValid(collection.Result.Data);
        using PqaxDocument expected = Fixture.OpenSample("sample.pqax");
        Assert.Equal(Fixture.StatedTables(expected), Fixture.StatedTables(collection.Document));
        foreach (string table in Fixture.StatedTables(expected))
        {
            Assert.Equal(expected.RowCount(table), collection.Document.RowCount(table));
        }

        Assert.Equal(["Column chunks", "Column chunks"], collection.Document.Summary.Tables.Select(table => table.DetailDepth));
    }

    [Fact]
    public void The_collection_states_the_library_the_level_the_times_and_the_requests()
    {
        CollectionInfo info = collection.Document.Manifest.Collection;
        Assert.Equal(2, info.Level);
        Assert.False(info.IncludesDataValues);
        Assert.False(info.IncludesPartitionValues);
        Assert.Equal("Pqax.Collector", info.CollectorLib);
        Assert.False(string.IsNullOrEmpty(info.CollectorLibVersion));
        Assert.NotNull(info.StartedAt);
        Assert.NotNull(info.CompletedAt);
        Assert.True(info.StartedAt <= info.CompletedAt);
        Assert.Equal(TimeSpan.Zero, info.StartedAt!.Value.Offset);
        Assert.Equal(collection.Result.StorageRequests, info.RequestCount);
        Assert.Equal(collection.Result.StorageBytes, info.BytesRead);
        Assert.True(info.RequestCount > 0);
        Assert.Equal("pqax", collection.Document.Manifest.Format);
        Assert.Equal("0.1", collection.Document.Manifest.FormatVersion);
    }

    [Fact]
    public void Sources_are_the_items_of_the_fixture()
    {
        foreach (DumpTable dump in Fixture.Tables)
        {
            TableRow table = Fixture.TableOf(collection.Document, dump);
            SourceRow? source = collection.Document.Summary.Sources.FindById(table.Source);
            Assert.NotNull(source);
            Assert.Equal(dump.Item, source.Name);
            Assert.Equal(dump.ItemKind == "Warehouse" ? SourceKinds.OneLakeWarehouse : SourceKinds.OneLakeLakehouse, source.Kind);
        }

        Assert.Equal(Fixture.Tables.Select(table => table.Item).Distinct().Count(), collection.Document.Summary.Sources.Count);
    }
}
