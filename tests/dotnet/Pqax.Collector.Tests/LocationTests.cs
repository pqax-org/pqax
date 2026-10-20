using Pqax.Collector.ParquetNet;

namespace Pqax.Collector.Tests;

/// <summary>
/// The locations and the identifiers that the discovery records for a source and its tables (README of the collector,
/// Locations): ABFSS URIs on OneLake, by names or by ids, with the host of the endpoint and nothing escaped; full paths
/// on disk; nothing through a storage that wraps another. And the sources that tables share: same kind, name, and
/// location.
/// </summary>
public class LocationTests(DefaultCollection collection) : IClassFixture<DefaultCollection>
{
    private const string Token = "test-token-not-a-secret";
    private const string WorkspaceName = "My Workspace";
    private const string ItemName = "Sales.Lakehouse";
    private const string WorkspaceId = "aaaaaaaa-bbbb-4ccc-8ddd-000000000001";
    private const string ItemId = "aaaaaaaa-bbbb-4ccc-8ddd-000000000002";
    private static readonly Uri TestEndpoint = new("https://onelake.test");

    /// <summary>A workspace with one item and two tables, one of them under a schema, each with a log of one commit.</summary>
    private static TempLake Workspace(string item)
    {
        var lake = new TempLake();
        foreach (string table in new[] { item + "/Tables/plain", item + "/Tables/schema1/nested" })
        {
            lake.Commit(table, 0, LogLines.Protocol(), LogLines.Metadata(), LogLines.CommitInfo(0));
        }

        return lake;
    }

    /// <summary>A OneLake storage that sends its requests to the fake endpoint, whatever its endpoint: nothing leaves the process.</summary>
    private static OneLakeStorage OneLake(FakeOneLake handler, string workspace, Uri? endpoint = null)
    {
        var options = new OneLakeStorageOptions { HttpClient = new HttpClient(handler), MaxRetries = 0 };
        if (endpoint != null)
        {
            options.Endpoint = endpoint;
        }

        return new OneLakeStorage(workspace, _ => Task.FromResult(Token), options);
    }

    private static async Task<PqaxDocument> CollectAsync(IStorage storage, IReadOnlyList<TableLocation> tables)
    {
        FootprintBuilder builder = await new PqaxCollector(storage, new CollectorOptions(), new ParquetNetCheckpointReader()).CollectAsync(tables, null, CancellationToken.None);
        using var stream = new MemoryStream();
        builder.Write(stream);
        byte[] data = stream.ToArray();
        Fixture.AssertValid(data);
        return Fixture.Open(data);
    }

    private static TableRow TableNamed(PqaxDocument doc, string name) => Assert.Single(doc.Summary.Tables, table => table.Name == name);

    // ---- OneLake ----

    public static TheoryData<string?, string> Endpoints => new()
    {
        { null, "onelake.dfs.fabric.microsoft.com" },
        { "https://onelake.test", "onelake.test" },
        { "https://onelake.test:8443", "onelake.test:8443" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task On_onelake_by_names_the_locations_are_abfss_uris_with_the_host_of_the_endpoint_and_no_identifiers(string? endpoint, string host)
    {
        using TempLake lake = Workspace(ItemName);
        var handler = new FakeOneLake(lake.Root, WorkspaceName);
        using OneLakeStorage storage = OneLake(handler, WorkspaceName, endpoint == null ? null : new Uri(endpoint));
        IReadOnlyList<TableLocation> tables = await TableDiscovery.DiscoverAsync(storage, ItemName, CancellationToken.None);

        string source = "abfss://My Workspace@" + host + "/Sales.Lakehouse";
        Assert.Equal(2, tables.Count);
        Assert.All(tables, table =>
        {
            Assert.Equal(SourceKinds.OneLakeLakehouse, table.SourceKind);
            Assert.Equal("Sales", table.SourceName);
            Assert.Equal(source, table.SourceLocation);
            Assert.Null(table.SourceIdentifiers);
        });
        Assert.Equal(source + "/Tables/plain", Assert.Single(tables, table => table.Name == "plain").Location);
        Assert.Equal(source + "/Tables/schema1/nested", Assert.Single(tables, table => table.Name == "nested").Location);

        // The requests escape the workspace; the locations keep it as the caller gave it.
        Assert.All(handler.Calls, call => Assert.StartsWith("/My%20Workspace", call.PathAndQuery, StringComparison.Ordinal));

        using PqaxDocument doc = await CollectAsync(storage, tables);
        SourceRow row = Assert.Single(doc.Summary.Sources);
        Assert.Equal(SourceKinds.OneLakeLakehouse, row.Kind);
        Assert.Equal("Sales", row.Name);
        Assert.Equal(source, row.Location);
        Assert.Null(row.Identifiers);
        Assert.Equal(source + "/Tables/plain", TableNamed(doc, "plain").Location);
        Assert.Equal(source + "/Tables/schema1/nested", TableNamed(doc, "nested").Location);
        Assert.Empty(doc.Summary.Issues);
    }

    [Fact]
    public async Task On_onelake_by_ids_the_locations_use_the_ids_and_the_identifiers_hold_them()
    {
        using TempLake lake = Workspace(ItemId);
        using OneLakeStorage storage = OneLake(new FakeOneLake(lake.Root, WorkspaceId), WorkspaceId, TestEndpoint);
        IReadOnlyList<TableLocation> tables = await TableDiscovery.DiscoverAsync(storage, ItemId, CancellationToken.None);

        string source = "abfss://" + WorkspaceId + "@onelake.test/" + ItemId;
        var identifiers = new Dictionary<string, string> { ["itemId"] = ItemId, ["workspaceId"] = WorkspaceId };
        Assert.Equal(2, tables.Count);
        Assert.All(tables, table =>
        {
            Assert.Equal(SourceKinds.OneLakeItem, table.SourceKind);
            Assert.Equal(ItemId, table.SourceName);
            Assert.Equal(source, table.SourceLocation);
            Assert.NotNull(table.SourceIdentifiers);
            Assert.Equal(identifiers.OrderBy(pair => pair.Key, StringComparer.Ordinal), table.SourceIdentifiers.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        });
        Assert.Equal(source + "/Tables/plain", Assert.Single(tables, table => table.Name == "plain").Location);
        Assert.Equal(source + "/Tables/schema1/nested", Assert.Single(tables, table => table.Name == "nested").Location);

        using PqaxDocument doc = await CollectAsync(storage, tables);
        SourceRow row = Assert.Single(doc.Summary.Sources);
        Assert.Equal(SourceKinds.OneLakeItem, row.Kind);
        Assert.Equal(ItemId, row.Name);
        Assert.Equal(source, row.Location);
        Assert.NotNull(row.Identifiers);
        Assert.Equal(identifiers.OrderBy(pair => pair.Key, StringComparer.Ordinal), row.Identifiers.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(source + "/Tables/plain", TableNamed(doc, "plain").Location);
        Assert.Equal(source + "/Tables/schema1/nested", TableNamed(doc, "nested").Location);
    }

    public static TheoryData<string, string, string?, string?> Identifiers => new()
    {
        { WorkspaceId, ItemId, WorkspaceId, ItemId },
        { WorkspaceName, ItemId, null, ItemId },
        { WorkspaceId, ItemName, WorkspaceId, null },
        { WorkspaceName, ItemName, null, null },
        { WorkspaceId.Replace("-", "", StringComparison.Ordinal), ItemId.Replace("-", "", StringComparison.Ordinal), null, null },
        { "{" + WorkspaceId + "}", "(" + ItemId + ")", null, null },
    };

    [Theory]
    [MemberData(nameof(Identifiers))]
    public async Task An_identifier_is_recorded_only_for_a_workspace_or_an_item_given_as_a_guid_with_hyphens(string workspace, string item, string? workspaceId, string? itemId)
    {
        using TempLake lake = Workspace(item);
        using OneLakeStorage storage = OneLake(new FakeOneLake(lake.Root, workspace), workspace, TestEndpoint);
        IReadOnlyList<TableLocation> tables = await TableDiscovery.DiscoverAsync(storage, item, CancellationToken.None);
        Assert.Equal(2, tables.Count);

        var expected = new Dictionary<string, string>();
        if (workspaceId != null)
        {
            expected["workspaceId"] = workspaceId;
        }

        if (itemId != null)
        {
            expected["itemId"] = itemId;
        }

        Assert.All(tables, table =>
        {
            Assert.Equal("abfss://" + workspace + "@onelake.test/" + item, table.SourceLocation);
            if (expected.Count == 0)
            {
                Assert.Null(table.SourceIdentifiers);
            }
            else
            {
                Assert.NotNull(table.SourceIdentifiers);
                Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal), table.SourceIdentifiers.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            }
        });
    }

    // ---- LocalStorage ----

    [Fact]
    public async Task On_local_storage_the_locations_are_the_full_paths_of_the_item_and_table_folders_with_no_identifiers()
    {
        var storage = new LocalStorage(Fixture.Folder);
        TableLocation sales = Assert.Single(await TableDiscovery.DiscoverAsync(storage, "Fix_Lakehouse.Lakehouse", CancellationToken.None));
        Assert.Equal(Path.Combine(Fixture.Folder, "Fix_Lakehouse.Lakehouse"), sales.SourceLocation);
        Assert.Equal(Path.Combine(Fixture.Folder, "Fix_Lakehouse.Lakehouse", "Tables", "sales"), sales.Location);
        Assert.Null(sales.SourceIdentifiers);

        TableLocation orders = Assert.Single(await TableDiscovery.DiscoverAsync(storage, "Fix_WH.Warehouse", CancellationToken.None));
        Assert.Equal(Path.Combine(Fixture.Folder, "Fix_WH.Warehouse"), orders.SourceLocation);
        Assert.Equal(Path.Combine(Fixture.Folder, "Fix_WH.Warehouse", "Tables", "dbo", "orders"), orders.Location);
        Assert.Null(orders.SourceIdentifiers);

        // The footprint of the default collection records them.
        Fixture.AssertLocalLocations(collection.Document);
        Assert.Equal(2, collection.Document.Summary.Sources.Count);
    }

    // ---- a storage that wraps another ----

    [Theory]
    [InlineData("local")]
    [InlineData("onelake")]
    public async Task A_storage_that_wraps_another_gives_no_location_and_no_identifiers(string inner)
    {
        // The workspace is an id, so that OneLake itself would record an identifier.
        using OneLakeStorage oneLake = OneLake(new FakeOneLake(Fixture.Folder, WorkspaceId), WorkspaceId, TestEndpoint);
        var storage = new TestStorage(inner == "local" ? new LocalStorage(Fixture.Folder) : oneLake);
        IReadOnlyList<TableLocation> tables = await Fixture.DiscoverAsync(storage);
        Assert.Equal(Fixture.Tables.Count, tables.Count);
        Assert.All(tables, table =>
        {
            Assert.Null(table.Location);
            Assert.Null(table.SourceLocation);
            Assert.Null(table.SourceIdentifiers);
        });

        Collected result = await Fixture.CollectAsync(storage: storage);
        using PqaxDocument doc = Fixture.Open(result.Data);
        Assert.Equal(2, doc.Summary.Sources.Count);
        Assert.All(doc.Summary.Sources, source =>
        {
            Assert.Null(source.Location);
            Assert.Null(source.Identifiers);
        });
        Assert.All(doc.Summary.Tables, table => Assert.Null(table.Location));
        Fixture.AssertValid(result.Data);
    }

    // ---- sources ----

    [Fact]
    public async Task Tables_share_a_source_when_they_have_the_same_kind_name_and_location()
    {
        DumpTable sales = Fixture.CheckpointTable;
        DumpTable orders = Fixture.JsonOnlyTable;
        const string one = "abfss://One Workspace@onelake.test/Sales.Lakehouse";
        const string two = "abfss://Two Workspace@onelake.test/Sales.Lakehouse";
        const string lakehouse = SourceKinds.OneLakeLakehouse;
        var tables = new List<TableLocation>
        {
            new(lakehouse, "Sales", [], "sales", sales.TablePath, one + "/Tables/sales", one),
            new(lakehouse, "Sales", ["dbo"], "orders", orders.TablePath, one + "/Tables/dbo/orders", one),
            new(lakehouse, "Sales", [], "sales", sales.TablePath, two + "/Tables/sales", two),
            new(lakehouse, "Sales", [], "sales", sales.TablePath),
            new(SourceKinds.OneLakeWarehouse, "Sales", ["dbo"], "orders", orders.TablePath, one + "/Tables/dbo/orders", one),
            new(lakehouse, "Other", [], "sales", sales.TablePath, one + "/Tables/sales", one),
        };

        using PqaxDocument doc = await CollectAsync(new LocalStorage(Fixture.Folder), tables);
        List<TableRow> rows = [.. doc.Summary.Tables];
        Assert.Equal(tables.Count, rows.Count);
        Assert.Equal(rows[0].Source, rows[1].Source);
        Assert.Equal(5, rows.Select(row => row.Source).Distinct().Count());
        Assert.Equal(5, doc.Summary.Sources.Count);
        for (int i = 0; i < tables.Count; i++)
        {
            SourceRow? source = doc.Summary.Sources.FindById(rows[i].Source);
            Assert.NotNull(source);
            Assert.Equal(tables[i].SourceKind, source.Kind);
            Assert.Equal(tables[i].SourceName, source.Name);
            Assert.Equal(tables[i].SourceLocation, source.Location);
            Assert.Equal(tables[i].Location, rows[i].Location);
        }
    }
}
