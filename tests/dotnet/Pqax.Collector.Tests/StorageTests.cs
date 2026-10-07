using System.Net;
using System.Text.Json.Nodes;

namespace Pqax.Collector.Tests;

/// <summary>LocalStorage, the discovery of tables, and OneLakeStorage against a fake endpoint in the process.</summary>
public class StorageTests
{
    private static DumpTable Table => Fixture.CheckpointTable;

    private static string LogFolder => Table.TablePath + "/_delta_log";

    // ---- LocalStorage ----

    [Fact]
    public async Task A_listing_gives_the_entries_of_a_folder_sorted_with_their_sizes()
    {
        var storage = new LocalStorage(Fixture.Folder);
        IReadOnlyList<StorageEntry> entries = await storage.ListAsync(LogFolder, CancellationToken.None);
        Assert.Equal(Dump.Long(Table.Delta["logFileCount"]), entries.Count);
        Assert.Equal(Dump.Long(Table.Delta["logSize"]), entries.Sum(entry => entry.Size));
        Assert.Equal(entries.Select(entry => entry.Name).Order(StringComparer.Ordinal), entries.Select(entry => entry.Name));
        foreach (StorageEntry entry in entries)
        {
            Assert.False(entry.IsDirectory);
            Assert.Equal(LogFolder + "/" + entry.Name, entry.Path);
            Assert.Equal(new FileInfo(Path.Combine(Fixture.Folder, entry.Path)).Length, entry.Size);
            Assert.NotNull(entry.LastModified);
        }

        IReadOnlyList<StorageEntry> table = await storage.ListAsync(Table.TablePath, CancellationToken.None);
        Assert.Contains(table, entry => entry.Name == "_delta_log" && entry.IsDirectory && entry.Size is null);
    }

    [Fact]
    public async Task A_missing_folder_lists_as_empty()
    {
        var storage = new LocalStorage(Fixture.Folder);
        Assert.Empty(await storage.ListAsync(Table.TablePath + "/no_such_folder", CancellationToken.None));
        Assert.Empty(await new LocalStorage(Path.Combine(Fixture.Folder, "no_such_root")).ListAsync("", CancellationToken.None));
    }

    [Fact]
    public async Task A_range_is_read_as_asked_and_cut_at_the_end_of_the_file()
    {
        string path = Fixture.DataFiles()[0];
        byte[] bytes = File.ReadAllBytes(Path.Combine(Fixture.Folder, path));
        var storage = new LocalStorage(Fixture.Folder);
        Assert.Equal(bytes.AsSpan(10, 100).ToArray(), await storage.ReadRangeAsync(path, 10, 100, CancellationToken.None));
        Assert.Equal(bytes.AsSpan(bytes.Length - 5).ToArray(), await storage.ReadRangeAsync(path, bytes.Length - 5, 100, CancellationToken.None));
        Assert.Empty(await storage.ReadRangeAsync(path, bytes.Length, 10, CancellationToken.None));
        Assert.Empty(await storage.ReadRangeAsync(path, bytes.Length + 100, 10, CancellationToken.None));
        Assert.Empty(await storage.ReadRangeAsync(path, 0, 0, CancellationToken.None));
        Assert.Equal(bytes, await storage.ReadAllAsync(path, CancellationToken.None));
        Assert.Equal(bytes, await storage.ReadAllAsync(path, bytes.Length, CancellationToken.None));
    }

    [Fact]
    public async Task The_storage_counts_its_requests_and_bytes()
    {
        string path = Fixture.DataFiles()[0];
        var storage = new LocalStorage(Fixture.Folder);
        await storage.ListAsync(LogFolder, CancellationToken.None);
        await storage.ReadRangeAsync(path, 0, 100, CancellationToken.None);
        await storage.ReadRangeAsync(path, 100, 50, CancellationToken.None);
        Assert.Equal(3, storage.RequestCount);
        Assert.Equal(150, storage.BytesRead);
    }

    [Fact]
    public async Task A_missing_file_is_not_found_and_the_message_holds_no_path()
    {
        var storage = new LocalStorage(Fixture.Folder);
        var error = await Assert.ThrowsAsync<StorageException>(() => storage.ReadRangeAsync(Table.TablePath + "/no_such_file.parquet", 0, 10, CancellationToken.None));
        Assert.True(error.IsNotFound);
        Assert.Equal(404, error.StatusCode);
        Assert.DoesNotContain("no_such_file", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Table.Item, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../Pqax.slnx")]
    [InlineData("a/../../Pqax.slnx")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("..")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("a\\..\\..\\Pqax.slnx")]
    public async Task A_path_that_could_leave_the_root_is_refused(string path)
    {
        if (path.Contains(':') && !OperatingSystem.IsWindows())
        {
            return;
        }

        var storage = new LocalStorage(Fixture.Folder);
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ReadRangeAsync(path, 0, 10, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ListAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_request_throws()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var storage = new LocalStorage(Fixture.Folder);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.ListAsync(LogFolder, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.ReadRangeAsync(Fixture.DataFiles()[0], 0, 10, cancel.Token));
    }

    // ---- discovery ----

    [Fact]
    public async Task The_items_and_tables_of_the_fixture_are_those_of_the_probe_dump()
    {
        var storage = new LocalStorage(Fixture.Folder);
        IReadOnlyList<string> items = await TableDiscovery.ListItemsAsync(storage, CancellationToken.None);
        Assert.Equal(Fixture.Tables.Select(table => table.ItemFolder).Distinct().Order(StringComparer.Ordinal), items);
        foreach (string item in items)
        {
            IReadOnlyList<TableLocation> tables = await TableDiscovery.DiscoverAsync(storage, item, CancellationToken.None);
            List<DumpTable> expected = Fixture.Tables.Where(table => table.ItemFolder == item).ToList();
            Assert.Equal(expected.Count, tables.Count);
            foreach (DumpTable dump in expected)
            {
                TableLocation location = Assert.Single(tables, table => table.Name == dump.Name);
                Assert.Equal(dump.Namespace, location.Namespace);
                Assert.Equal(dump.TablePath, location.Path);
                Assert.Equal(dump.Item, location.SourceName);
                Assert.Equal(dump.ItemKind == "Warehouse" ? SourceKinds.OneLakeWarehouse : SourceKinds.OneLakeLakehouse, location.SourceKind);
            }
        }
    }

    [Fact]
    public async Task Discovery_follows_the_rules_of_items_schemas_and_tables()
    {
        string root = Path.Combine(Path.GetTempPath(), "pqax-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "One.Lakehouse", "Tables", "plain", "_delta_log"));
            Directory.CreateDirectory(Path.Combine(root, "One.Lakehouse", "Tables", "schema1", "nested", "_delta_log"));
            Directory.CreateDirectory(Path.Combine(root, "Two.Warehouse", "Tables", "dbo", "facts", "_delta_log"));
            Directory.CreateDirectory(Path.Combine(root, "Three.Notebook", "Tables", "other", "_delta_log"));
            Directory.CreateDirectory(Path.Combine(root, "Four.Lakehouse"));
            File.WriteAllText(Path.Combine(root, "Five.Lakehouse"), "a file, not an item");
            var storage = new LocalStorage(root);
            Assert.Equal(["Four.Lakehouse", "One.Lakehouse", "Two.Warehouse"], await TableDiscovery.ListItemsAsync(storage, CancellationToken.None));

            IReadOnlyList<TableLocation> lakehouse = await TableDiscovery.DiscoverAsync(storage, "One.Lakehouse", CancellationToken.None);
            Assert.Equal(2, lakehouse.Count);
            TableLocation plain = Assert.Single(lakehouse, table => table.Name == "plain");
            Assert.Empty(plain.Namespace);
            Assert.Equal("One.Lakehouse/Tables/plain", plain.Path);
            TableLocation nested = Assert.Single(lakehouse, table => table.Name == "nested");
            Assert.Equal(["schema1"], nested.Namespace);
            Assert.Equal("One.Lakehouse/Tables/schema1/nested", nested.Path);
            Assert.All(lakehouse, table => Assert.Equal(SourceKinds.OneLakeLakehouse, table.SourceKind));
            Assert.All(lakehouse, table => Assert.Equal("One", table.SourceName));

            TableLocation facts = Assert.Single(await TableDiscovery.DiscoverAsync(storage, "Two.Warehouse", CancellationToken.None));
            Assert.Equal(["dbo"], facts.Namespace);
            Assert.Equal("facts", facts.Name);
            Assert.Equal(SourceKinds.OneLakeWarehouse, facts.SourceKind);

            Assert.Empty(await TableDiscovery.DiscoverAsync(storage, "Four.Lakehouse", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- OneLakeStorage against a fake endpoint ----

    private const string Workspace = "Test Workspace";
    private const string Token = "test-token-not-a-secret";
    private static readonly Uri Endpoint = new("https://onelake.test");

    private static OneLakeStorage OneLake(FakeOneLake handler, int retries = 0, Func<CancellationToken, Task<string>>? tokens = null) =>
        new(Workspace, tokens ?? (_ => Task.FromResult(Token)), new OneLakeStorageOptions { Endpoint = Endpoint, HttpClient = new HttpClient(handler), MaxRetries = retries });

    [Fact]
    public async Task Collecting_through_a_fake_onelake_endpoint_gives_the_footprint_of_the_folder()
    {
        var handler = new FakeOneLake(Fixture.Folder, Workspace);
        int tokens = 0;
        using OneLakeStorage storage = OneLake(handler, tokens: _ =>
        {
            Interlocked.Increment(ref tokens);
            return Task.FromResult(Token);
        });
        Collected remote = await Fixture.CollectAsync(storage: storage);
        Collected local = await Fixture.CollectAsync();
        using PqaxDocument a = Fixture.Open(remote.Data);
        using PqaxDocument b = Fixture.Open(local.Data);
        Assert.Empty(Fixture.RawDifferences(a, b));
        Fixture.AssertValid(remote.Data);

        List<HttpCall> calls = [.. handler.Calls];
        Assert.NotEmpty(calls);
        Assert.All(calls, call =>
        {
            Assert.Equal("GET", call.Method);
            Assert.Equal(Endpoint.Host, call.Host);
            Assert.Equal("Bearer " + Token, call.Authorization);
            Assert.False(string.IsNullOrEmpty(call.Version));
            Assert.StartsWith("/Test%20Workspace", call.PathAndQuery, StringComparison.Ordinal);
        });
        Assert.Equal(calls.Count, tokens);
        Assert.Equal(calls.Count, storage.RequestCount);
        Assert.Equal(remote.StorageRequests, a.Manifest.Collection.RequestCount);
        Assert.Equal(remote.StorageBytes, a.Manifest.Collection.BytesRead);
        Assert.Contains(calls, call => call.PathAndQuery.Contains("continuation=", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.Range != null);
    }

    [Fact]
    public async Task A_listing_follows_the_continuation_of_every_page()
    {
        var handler = new FakeOneLake(Fixture.Folder, Workspace) { PageSize = 2 };
        using OneLakeStorage storage = OneLake(handler);
        IReadOnlyList<StorageEntry> entries = await storage.ListAsync(LogFolder, CancellationToken.None);
        IReadOnlyList<StorageEntry> local = await new LocalStorage(Fixture.Folder).ListAsync(LogFolder, CancellationToken.None);
        Assert.Equal(local.Select(entry => (entry.Path, entry.Name, entry.IsDirectory, entry.Size)), entries.Select(entry => (entry.Path, entry.Name, entry.IsDirectory, entry.Size)));
        Assert.Equal((local.Count + 1) / 2, handler.Calls.Count);
        Assert.Equal(handler.Calls.Count, storage.RequestCount);
    }

    [Fact]
    public async Task The_root_is_listed_without_a_directory_parameter()
    {
        var handler = new FakeOneLake(Fixture.Folder, Workspace) { PageSize = 100 };
        using OneLakeStorage storage = OneLake(handler);

        IReadOnlyList<StorageEntry> root = await storage.ListAsync("", CancellationToken.None);
        HttpCall call = Assert.Single(handler.Calls);
        Assert.Equal("/Test%20Workspace?resource=filesystem&recursive=false", call.PathAndQuery);
        Assert.DoesNotContain("directory", call.PathAndQuery, StringComparison.Ordinal);
        IReadOnlyList<StorageEntry> local = await new LocalStorage(Fixture.Folder).ListAsync("", CancellationToken.None);
        Assert.Equal(local.Select(entry => (entry.Path, entry.IsDirectory)), root.Select(entry => (entry.Path, entry.IsDirectory)));
        Assert.Equal(Fixture.Tables.Select(table => table.ItemFolder).Distinct().Order(StringComparer.Ordinal),
            await TableDiscovery.ListItemsAsync(storage, CancellationToken.None));

        while (handler.Calls.TryDequeue(out _))
        {
        }

        Assert.Empty(await storage.ListAsync("Item.Lakehouse/Tables", CancellationToken.None));
        call = Assert.Single(handler.Calls);
        Assert.Equal("/Test%20Workspace?resource=filesystem&recursive=false&directory=Item.Lakehouse%2FTables", call.PathAndQuery);

        string tables = Fixture.CheckpointTable.ItemFolder + "/Tables";
        Assert.NotEmpty(await storage.ListAsync(tables, CancellationToken.None));
        Assert.EndsWith("&directory=" + Uri.EscapeDataString(tables), handler.Calls.Last().PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fake_endpoint_refuses_an_empty_directory_as_onelake_does()
    {
        using var http = new HttpClient(new FakeOneLake(Fixture.Folder, Workspace));
        using HttpResponseMessage empty = await http.GetAsync(new Uri(Endpoint, "/Test%20Workspace?resource=filesystem&recursive=false&directory="));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using HttpResponseMessage none = await http.GetAsync(new Uri(Endpoint, "/Test%20Workspace?resource=filesystem&recursive=false"));
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
    }

    [Fact]
    public async Task A_missing_folder_lists_as_empty_on_onelake()
    {
        using OneLakeStorage storage = OneLake(new FakeOneLake(Fixture.Folder, Workspace));
        Assert.Empty(await storage.ListAsync("Nothing.Lakehouse/Tables", CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_returns_the_range_even_when_the_server_sends_the_whole_file(bool ignoreRange)
    {
        string path = Fixture.DataFiles()[0];
        byte[] bytes = File.ReadAllBytes(Path.Combine(Fixture.Folder, path));
        using OneLakeStorage storage = OneLake(new FakeOneLake(Fixture.Folder, Workspace) { IgnoreRange = ignoreRange });
        Assert.Equal(bytes.AsSpan(10, 100).ToArray(), await storage.ReadRangeAsync(path, 10, 100, CancellationToken.None));
        Assert.Equal(bytes.AsSpan(bytes.Length - 5).ToArray(), await storage.ReadRangeAsync(path, bytes.Length - 5, 100, CancellationToken.None));
        Assert.Empty(await storage.ReadRangeAsync(path, bytes.Length, 10, CancellationToken.None));
    }

    [Fact]
    public async Task Access_denied_is_a_storage_exception_that_names_no_path_and_no_token()
    {
        var handler = new FakeOneLake(Fixture.Folder, Workspace) { Override = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) };
        using OneLakeStorage storage = OneLake(handler);
        var listing = await Assert.ThrowsAsync<StorageException>(() => storage.ListAsync(LogFolder, CancellationToken.None));
        var reading = await Assert.ThrowsAsync<StorageException>(() => storage.ReadRangeAsync(Fixture.DataFiles()[0], 0, 10, CancellationToken.None));
        foreach (StorageException error in new[] { listing, reading })
        {
            Assert.True(error.IsAccessDenied);
            Assert.Equal(403, error.StatusCode);
            Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Table.Item, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Workspace", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_counted()
    {
        int calls = 0;
        var handler = new FakeOneLake(Fixture.Folder, Workspace)
        {
            Override = _ => Interlocked.Increment(ref calls) == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null,
        };
        string path = Fixture.DataFiles()[0];
        using OneLakeStorage storage = OneLake(handler, retries: 1);
        byte[] bytes = await storage.ReadRangeAsync(path, 0, 10, CancellationToken.None);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Fixture.Folder, path)).AsSpan(0, 10).ToArray(), bytes);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(2, storage.RequestCount);

        using OneLakeStorage noRetry = OneLake(new FakeOneLake(Fixture.Folder, Workspace) { Override = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) });
        var error = await Assert.ThrowsAsync<StorageException>(() => noRetry.ReadRangeAsync(path, 0, 10, CancellationToken.None));
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public void Wrong_arguments_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new OneLakeStorage("", _ => Task.FromResult(Token)));
        Assert.Throws<ArgumentNullException>(() => new OneLakeStorage(Workspace, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OneLakeStorage(Workspace, _ => Task.FromResult(Token), new OneLakeStorageOptions { MaxRetries = -1 }));
        Assert.Equal("https://storage.azure.com/.default", OneLakeStorage.Scope);
    }

    [Fact]
    public async Task Access_denied_by_onelake_becomes_an_issue_of_the_table()
    {
        string denied = Uri.EscapeDataString(LogFolder);
        var handler = new FakeOneLake(Fixture.Folder, Workspace)
        {
            Override = request => request.RequestUri!.Query.Contains("directory=" + denied, StringComparison.OrdinalIgnoreCase) ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null,
        };
        using OneLakeStorage storage = OneLake(handler);
        Collected result = await Fixture.CollectAsync(storage: storage);
        using PqaxDocument doc = Fixture.Open(result.Data);
        TableRow table = Fixture.TableOf(doc, Table);
        Assert.Contains(doc.Summary.Issues, issue => issue.Table == table.Id && issue.Code == IssueCodes.AccessDenied);
        TableRow other = Fixture.TableOf(doc, Fixture.JsonOnlyTable);
        Assert.Equal(Fixture.JsonOnlyTable.Files.Count, other.FileCount);
        Fixture.AssertValid(result.Data);
    }
}
