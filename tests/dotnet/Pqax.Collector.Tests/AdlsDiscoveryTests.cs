using System.Net;
using System.Text.Json.Nodes;

namespace Pqax.Collector.Tests;

/// <summary>
/// The layout of an ADLS Gen2 container that the tests of a storage folder share, in a temporary folder that the fake DFS
/// endpoint serves as the filesystem <c>lake</c> of the account <c>pqaxtest</c>. Under <c>data</c>: Delta tables at depths
/// 1, 2, 4, and 5, folders of Parquet files, an empty folder, a folder of other files, a partitioned folder, and folders
/// whose names start with <c>_</c> or <c>.</c>. The Parquet files are copies of the fixture, the logs those of the
/// fixture or of <see cref="LogLines"/>.
/// </summary>
public static class FolderLayout
{
    public const string Filesystem = "lake";
    public const string Host = "pqaxtest.dfs.core.windows.net";
    public const string Token = "test-token-not-a-secret";
    public static readonly Uri Endpoint = new("https://" + Host);

    /// <summary>The ABFSS URI of the root of the container.</summary>
    public const string Container = "abfss://" + Filesystem + "@" + Host;

    /// <summary>The modification times of the files: whole seconds, which the listing of the DFS endpoint keeps.</summary>
    public static readonly DateTime Older = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    public static readonly DateTime Newer = Older.AddHours(1);

    /// <summary>The two Parquet files at the top of the folder of the fixture table, and their dump.</summary>
    public const string OlderFile = "part-0001.parquet";

    public const string NewerFile = "part-0002.parquet";

    public static string FixtureFile(string name) => Fixture.LocalPath(Fixture.CheckpointTable.TablePath + "/" + name);

    public static JsonObject DumpOf(string name) => Fixture.CheckpointTable.Files.Single(file => (string)file["path"]! == name);

    /// <summary>Bytes that end with <c>.parquet</c> in their name and are no Parquet file: a test fails if the collector reads them.</summary>
    public static readonly byte[] NotParquet = "This text is not a Parquet file and must not be read."u8.ToArray();

    /// <summary>The tables that the default discovery of <c>data</c> finds, in their order: path, namespace under data, and format.</summary>
    public static IReadOnlyList<(string Path, string[] Namespace, string Format)> DataTables { get; } =
    [
        ("data/a/b/c/deep", ["a", "b", "c"], TableFormats.Delta),
        ("data/eu/sales", ["eu"], TableFormats.Delta),
        ("data/mixed/inner", ["mixed"], TableFormats.Parquet),
        ("data/raw", [], TableFormats.Parquet),
        ("data/sales", [], TableFormats.Delta),
        ("data/upper", [], TableFormats.Parquet),
    ];

    /// <summary>The table at depth 5 under data, which only a larger maximum depth finds.</summary>
    public const string Deeper = "data/a/b/c/d/deeper";

    public static TempLake Create()
    {
        var lake = new TempLake();
        CopyDelta(lake, "data/sales");
        CopyDelta(lake, "data/eu/sales");
        LogOnly(lake, "data/a/b/c/deep");
        LogOnly(lake, Deeper);
        ParquetFolder(lake, "data/raw");

        // A folder of Parquet files and a subfolder: not a table, but the subfolder is one.
        CopyFile(lake, OlderFile, "data/mixed/x.parquet", Older);
        CopyFile(lake, OlderFile, "data/mixed/inner/y.parquet", Older);

        // The extension in another case still names a data file.
        CopyFile(lake, OlderFile, "data/upper/DATA.PARQUET", Older);

        // No table: an empty folder, other files, partitions, and folders that the walk skips.
        Directory.CreateDirectory(lake.FullPath("data/empty"));
        lake.Write("data/other/a.csv", "id,name\n1,synthetic\n");
        lake.Write("data/other/b.json", "{\"id\":1}\n");
        CopyFile(lake, OlderFile, "data/events/year=2024/part-0001.parquet", Older);
        CopyFile(lake, OlderFile, "data/events/year=2025/part-0001.parquet", Older);
        LogOnly(lake, "data/_staging/t");
        LogOnly(lake, "data/.hidden/t");
        lake.Write("data/readme.txt", "A file next to the folders.");
        return lake;
    }

    /// <summary>A copy of the Delta table of the fixture, with its log and all its files.</summary>
    public static void CopyDelta(TempLake lake, string table)
    {
        string from = Fixture.LocalPath(Fixture.CheckpointTable.TablePath);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = lake.FullPath(table + "/" + Path.GetRelativePath(from, file).Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>A Delta table whose log has one commit and no file.</summary>
    public static void LogOnly(TempLake lake, string table) =>
        lake.Commit(table, 0, LogLines.Protocol(), LogLines.Metadata(), LogLines.CommitInfo(0));

    /// <summary>A copy of a Parquet file of the fixture, with a modification time.</summary>
    public static void CopyFile(TempLake lake, string fixtureFile, string path, DateTime modified)
    {
        lake.Write(path, File.ReadAllBytes(FixtureFile(fixtureFile)));
        File.SetLastWriteTimeUtc(lake.FullPath(path), modified);
    }

    /// <summary>
    /// A folder of Parquet files as a Spark job leaves it: the two files of the fixture, the newer one second, a
    /// <c>_SUCCESS</c> file, a <c>_temporary</c> folder, and files that end with <c>.parquet</c> but start with <c>_</c>
    /// or <c>.</c>, which are not data files.
    /// </summary>
    public static void ParquetFolder(TempLake lake, string folder)
    {
        CopyFile(lake, OlderFile, folder + "/" + OlderFile, Older);
        CopyFile(lake, NewerFile, folder + "/" + NewerFile, Newer);
        lake.Write(folder + "/_SUCCESS", "");
        lake.Write(folder + "/_temporary/0/part-0009.parquet", NotParquet);
        lake.Write(folder + "/_ignored.parquet", NotParquet);
        lake.Write(folder + "/.hidden.parquet", NotParquet);
        foreach (string name in new[] { "_temporary/0/part-0009.parquet", "_ignored.parquet", ".hidden.parquet" })
        {
            // Newer than the data files, so that a collector that took them for data files would read them first.
            File.SetLastWriteTimeUtc(lake.FullPath(folder + "/" + name), Newer.AddHours(1));
        }
    }

    /// <summary>The storage of the container: a OneLake storage on the endpoint of the account, with the filesystem as its workspace.</summary>
    public static OneLakeStorage Adls(FakeOneLake handler, Uri? endpoint = null) =>
        new(Filesystem, _ => Task.FromResult(Token), new OneLakeStorageOptions { Endpoint = endpoint ?? Endpoint, HttpClient = new HttpClient(handler), MaxRetries = 0 });

    public static FakeOneLake Handler(TempLake lake) => new(lake.Root, Filesystem);

    /// <summary>The value of the directory parameter of a listing, decoded, or null for a read.</summary>
    public static string? ListedDirectory(HttpCall call)
    {
        int query = call.PathAndQuery.IndexOf('?', StringComparison.Ordinal);
        if (query < 0 || !call.PathAndQuery.Contains("resource=filesystem", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (string pair in call.PathAndQuery[(query + 1)..].Split('&'))
        {
            if (pair.StartsWith("directory=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair["directory=".Length..]);
            }
        }

        return "";
    }

    /// <summary>The path of the file of a read, decoded, or null for a listing.</summary>
    public static string? ReadPath(HttpCall call)
    {
        if (call.PathAndQuery.Contains("resource=filesystem", StringComparison.Ordinal))
        {
            return null;
        }

        string path = call.PathAndQuery.Split('?')[0].TrimStart('/');
        return Uri.UnescapeDataString(path[(path.IndexOf('/', StringComparison.Ordinal) + 1)..]);
    }
}

/// <summary>
/// <see cref="TableDiscovery.DiscoverFolderAsync(IStorage, string, CancellationToken)"/> on an ADLS Gen2 container through
/// the fake DFS endpoint, and on a local folder: which folders are tables and of which format, the walk and its depth,
/// the names and locations of the tables, and no identifiers. And the constructor of <see cref="TableLocation"/> with a
/// format, and the wording of the storage errors of an account.
/// </summary>
public class AdlsDiscoveryTests
{
    private const string Guid1 = "aaaaaaaa-bbbb-4ccc-8ddd-000000000002";
    private const string Guid2 = "aaaaaaaa-bbbb-4ccc-8ddd-000000000003";

    private static Task<IReadOnlyList<TableLocation>> DiscoverAsync(IStorage storage, string folder, int? maxDepth = null) =>
        maxDepth == null
            ? TableDiscovery.DiscoverFolderAsync(storage, folder, CancellationToken.None)
            : TableDiscovery.DiscoverFolderAsync(storage, folder, maxDepth.Value, CancellationToken.None);

    private static IEnumerable<string> PathsOf(IReadOnlyList<TableLocation> tables) => tables.Select(table => table.Path);

    // ---- the tables of a folder ----

    [Fact]
    public async Task The_tables_of_a_folder_are_its_delta_and_parquet_folders_in_ordinal_path_order()
    {
        using TempLake lake = FolderLayout.Create();
        using OneLakeStorage storage = FolderLayout.Adls(FolderLayout.Handler(lake));
        IReadOnlyList<TableLocation> tables = await DiscoverAsync(storage, "data");

        Assert.Equal(FolderLayout.DataTables.Select(table => table.Path), PathsOf(tables));
        for (int i = 0; i < tables.Count; i++)
        {
            (string path, string[] ns, string format) = FolderLayout.DataTables[i];
            TableLocation table = tables[i];
            Assert.Equal(format, table.Format);
            Assert.Equal(SourceKinds.StorageFolder, table.SourceKind);
            Assert.Equal("data", table.SourceName);
            Assert.Equal(ns, table.Namespace);
            Assert.Equal(path[(path.LastIndexOf('/') + 1)..], table.Name);
            Assert.Equal(FolderLayout.Container + "/data", table.SourceLocation);
            Assert.Equal(FolderLayout.Container + "/" + path, table.Location);
            Assert.Null(table.SourceIdentifiers);
        }
    }

    [Fact]
    public async Task The_walk_lists_one_folder_at_a_time_and_skips_logs_partitions_and_folders_that_start_with_an_underscore_or_a_dot()
    {
        using TempLake lake = FolderLayout.Create();
        FakeOneLake handler = FolderLayout.Handler(lake);
        using OneLakeStorage storage = FolderLayout.Adls(handler);
        await DiscoverAsync(storage, "data");

        List<HttpCall> calls = [.. handler.Calls];
        Assert.NotEmpty(calls);
        Assert.All(calls, call =>
        {
            Assert.Equal("GET", call.Method);
            Assert.Equal(FolderLayout.Host, call.Host);
            Assert.Equal("Bearer " + FolderLayout.Token, call.Authorization);
            Assert.StartsWith("/lake?resource=filesystem&recursive=false", call.PathAndQuery, StringComparison.Ordinal);
            Assert.Null(call.Range);
        });

        var listed = calls.Select(FolderLayout.ListedDirectory).ToHashSet();
        Assert.Contains("data", listed);
        Assert.Contains("data/a/b/c", listed);

        // A folder at the maximum depth is tested for being a table, and not walked further.
        Assert.Contains("data/a/b/c/d", listed);
        Assert.DoesNotContain(FolderLayout.Deeper, listed);
        foreach (string skipped in new[] { "_delta_log", "year=2024", "year=2025", "_temporary", "_staging", ".hidden" })
        {
            Assert.DoesNotContain(listed, directory => directory!.Split('/').Contains(skipped));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public async Task At_the_root_the_source_is_the_filesystem_and_the_namespace_starts_with_the_first_folder(string root)
    {
        using TempLake lake = FolderLayout.Create();
        using OneLakeStorage storage = FolderLayout.Adls(FolderLayout.Handler(lake));
        IReadOnlyList<TableLocation> tables = await DiscoverAsync(storage, root);

        // One level deeper than under data: the table at depth 4 under data is at depth 5 here.
        Assert.Equal(FolderLayout.DataTables.Skip(1).Select(table => table.Path), PathsOf(tables));
        Assert.All(tables, table =>
        {
            Assert.Equal(SourceKinds.StorageFolder, table.SourceKind);
            Assert.Equal(FolderLayout.Filesystem, table.SourceName);
            Assert.Equal(FolderLayout.Container, table.SourceLocation);
            Assert.Equal(FolderLayout.Container + "/" + table.Path, table.Location);
            Assert.Null(table.SourceIdentifiers);
        });
        Assert.Equal(["data", "eu"], tables[0].Namespace);
        Assert.Equal(["data"], Assert.Single(tables, table => table.Path == "data/raw").Namespace);
    }

    [Theory]
    [InlineData("data/sales", "Delta")]
    [InlineData("data/raw", "Parquet")]
    public async Task A_folder_that_is_a_table_gives_that_table_with_no_namespace_and_the_name_of_the_source(string folder, string format)
    {
        using TempLake lake = FolderLayout.Create();
        using OneLakeStorage storage = FolderLayout.Adls(FolderLayout.Handler(lake));
        foreach (int? depth in new int?[] { null, 0, 3 })
        {
            TableLocation table = Assert.Single(await DiscoverAsync(storage, folder, depth));
            string name = folder[(folder.LastIndexOf('/') + 1)..];
            Assert.Equal(format, table.Format);
            Assert.Equal(name, table.Name);
            Assert.Equal(name, table.SourceName);
            Assert.Empty(table.Namespace);
            Assert.Equal(folder, table.Path);
            Assert.Equal(FolderLayout.Container + "/" + folder, table.SourceLocation);
            Assert.Equal(FolderLayout.Container + "/" + folder, table.Location);
        }
    }

    [Fact]
    public async Task The_maximum_depth_bounds_the_walk()
    {
        using TempLake lake = FolderLayout.Create();
        using OneLakeStorage storage = FolderLayout.Adls(FolderLayout.Handler(lake));
        Assert.Equal(4, TableDiscovery.DefaultFolderDepth);
        Assert.Equal(PathsOf(await DiscoverAsync(storage, "data")), PathsOf(await DiscoverAsync(storage, "data", TableDiscovery.DefaultFolderDepth)));

        Assert.Empty(await DiscoverAsync(storage, "data", 0));
        Assert.Equal(["data/raw", "data/sales", "data/upper"], PathsOf(await DiscoverAsync(storage, "data", 1)));
        Assert.Equal(["data/eu/sales", "data/mixed/inner", "data/raw", "data/sales", "data/upper"], PathsOf(await DiscoverAsync(storage, "data", 3)));

        IReadOnlyList<TableLocation> five = await DiscoverAsync(storage, "data", 5);
        Assert.Equal(new[] { FolderLayout.Deeper }.Concat(FolderLayout.DataTables.Select(table => table.Path)), PathsOf(five));
        TableLocation deeper = five[0];
        Assert.Equal(["a", "b", "c", "d"], deeper.Namespace);
        Assert.Equal("deeper", deeper.Name);
        Assert.Equal(TableFormats.Delta, deeper.Format);
    }

    [Theory]
    [InlineData("data/empty")]
    [InlineData("data/other")]
    [InlineData("data/events")]
    [InlineData("data/no_such_folder")]
    [InlineData("nothing/at/all")]
    public async Task A_folder_with_no_table_or_that_does_not_exist_gives_no_table_and_no_exception(string folder)
    {
        using TempLake lake = FolderLayout.Create();
        using OneLakeStorage storage = FolderLayout.Adls(FolderLayout.Handler(lake));
        Assert.Empty(await DiscoverAsync(storage, folder));
        Assert.Empty(await DiscoverAsync(storage, folder, 0));
    }

    [Fact]
    public async Task A_folder_named_like_a_guid_gives_no_identifier()
    {
        using var lake = new TempLake();
        FolderLayout.LogOnly(lake, Guid1 + "/" + Guid2);
        FolderLayout.CopyFile(lake, FolderLayout.OlderFile, Guid1 + "/files/a.parquet", FolderLayout.Older);
        using OneLakeStorage storage = FolderLayout.Adls(FolderLayout.Handler(lake));

        IReadOnlyList<TableLocation> tables = await DiscoverAsync(storage, Guid1);
        Assert.Equal([Guid1 + "/" + Guid2, Guid1 + "/files"], PathsOf(tables));
        Assert.All(tables, table =>
        {
            Assert.Equal(SourceKinds.StorageFolder, table.SourceKind);
            Assert.Equal(Guid1, table.SourceName);
            Assert.Equal(FolderLayout.Container + "/" + Guid1, table.SourceLocation);
            Assert.Null(table.SourceIdentifiers);
        });

        TableLocation itself = Assert.Single(await DiscoverAsync(storage, Guid1 + "/" + Guid2));
        Assert.Equal(Guid2, itself.SourceName);
        Assert.Equal(Guid2, itself.Name);
        Assert.Null(itself.SourceIdentifiers);

        Assert.All(await DiscoverAsync(storage, ""), table => Assert.Null(table.SourceIdentifiers));
    }

    public static TheoryData<string, string> Endpoints => new()
    {
        { "https://pqaxtest.dfs.core.windows.net", "pqaxtest.dfs.core.windows.net" },
        { "https://onelake.test", "onelake.test" },
        { "https://pqaxtest.dfs.core.windows.net:8443", "pqaxtest.dfs.core.windows.net:8443" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task The_locations_are_abfss_uris_with_the_host_of_the_endpoint(string endpoint, string host)
    {
        using TempLake lake = FolderLayout.Create();
        using OneLakeStorage storage = FolderLayout.Adls(FolderLayout.Handler(lake), new Uri(endpoint));
        IReadOnlyList<TableLocation> tables = await DiscoverAsync(storage, "data/eu");
        TableLocation sales = Assert.Single(tables);
        Assert.Equal("abfss://lake@" + host + "/data/eu", sales.SourceLocation);
        Assert.Equal("abfss://lake@" + host + "/data/eu/sales", sales.Location);
        Assert.Equal(SourceKinds.StorageFolder, sales.SourceKind);
        Assert.Null(sales.SourceIdentifiers);
    }

    // ---- other storages ----

    [Fact]
    public async Task On_local_storage_the_tables_are_the_same_and_the_locations_are_full_paths()
    {
        using TempLake lake = FolderLayout.Create();
        using OneLakeStorage adls = FolderLayout.Adls(FolderLayout.Handler(lake));
        var storage = new LocalStorage(lake.Root);
        IReadOnlyList<TableLocation> local = await DiscoverAsync(storage, "data");
        IReadOnlyList<TableLocation> remote = await DiscoverAsync(adls, "data");
        Assert.Equal(
            remote.Select(table => (table.Path, string.Join("/", table.Namespace), table.Name, table.Format, table.SourceKind, table.SourceName)),
            local.Select(table => (table.Path, string.Join("/", table.Namespace), table.Name, table.Format, table.SourceKind, table.SourceName)));
        Assert.All(local, table =>
        {
            Assert.Equal(Path.Combine(storage.Root, "data"), table.SourceLocation);
            Assert.Equal(Path.Combine(storage.Root, table.Path.Replace('/', Path.DirectorySeparatorChar)), table.Location);
            Assert.Null(table.SourceIdentifiers);
        });

        // At the root, the source is named after the folder of the storage.
        IReadOnlyList<TableLocation> root = await DiscoverAsync(storage, "");
        Assert.Equal(PathsOf(await DiscoverAsync(adls, "")), PathsOf(root));
        Assert.All(root, table =>
        {
            Assert.Equal(Path.GetFileName(storage.Root.TrimEnd(Path.DirectorySeparatorChar)), table.SourceName);
            Assert.Equal(storage.Root, table.SourceLocation);
        });

        Assert.Empty(await DiscoverAsync(storage, "data/no_such_folder"));
        Assert.Equal(PathsOf(await DiscoverAsync(adls, "data", 5)), PathsOf(await DiscoverAsync(storage, "data", 5)));
    }

    [Fact]
    public async Task A_storage_that_wraps_another_gives_the_tables_with_no_location()
    {
        using TempLake lake = FolderLayout.Create();
        var storage = new TestStorage(new LocalStorage(lake.Root));
        IReadOnlyList<TableLocation> tables = await DiscoverAsync(storage, "data");
        Assert.Equal(FolderLayout.DataTables.Select(table => table.Path), PathsOf(tables));
        Assert.All(tables, table =>
        {
            Assert.Equal("data", table.SourceName);
            Assert.Null(table.Location);
            Assert.Null(table.SourceLocation);
            Assert.Null(table.SourceIdentifiers);
        });
        Assert.All(await DiscoverAsync(storage, ""), table => Assert.Equal("", table.SourceName));
        Assert.DoesNotContain(storage.Requests, request => request.Kind == "read");
    }

    [Fact]
    public async Task Wrong_arguments_are_refused()
    {
        using var lake = new TempLake();
        var storage = new LocalStorage(lake.Root);
        await Assert.ThrowsAsync<ArgumentNullException>(() => TableDiscovery.DiscoverFolderAsync(storage, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => TableDiscovery.DiscoverFolderAsync(storage, null!, 2, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => TableDiscovery.DiscoverFolderAsync(null!, "data", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TableDiscovery.DiscoverFolderAsync(storage, "data", -1, CancellationToken.None));
    }

    // ---- TableLocation and its format ----

    [Fact]
    public void The_constructor_without_a_format_gives_a_delta_table()
    {
        var table = new TableLocation(SourceKinds.OneLakeLakehouse, "Sales", ["dbo"], "orders", "Sales.Lakehouse/Tables/dbo/orders");
        Assert.Equal(TableFormats.Delta, table.Format);
        Assert.Equal("Delta", TableFormats.Delta);
        Assert.Equal("Parquet", TableFormats.Parquet);
    }

    [Theory]
    [InlineData("Delta")]
    [InlineData("Parquet")]
    public void The_constructor_with_a_format_takes_delta_or_parquet(string format)
    {
        var identifiers = new Dictionary<string, string> { ["key"] = "value" };
        var table = new TableLocation(SourceKinds.StorageFolder, "data", ["eu"], "sales", "data/eu/sales",
            FolderLayout.Container + "/data/eu/sales", FolderLayout.Container + "/data", identifiers, format);
        Assert.Equal(format, table.Format);
        Assert.Equal(SourceKinds.StorageFolder, table.SourceKind);
        Assert.Equal("data", table.SourceName);
        Assert.Equal(["eu"], table.Namespace);
        Assert.Equal("sales", table.Name);
        Assert.Equal("data/eu/sales", table.Path);
        Assert.Equal(FolderLayout.Container + "/data/eu/sales", table.Location);
        Assert.Equal(FolderLayout.Container + "/data", table.SourceLocation);
        Assert.Equal("value", table.SourceIdentifiers!["key"]);

        var bare = new TableLocation(SourceKinds.StorageFolder, "data", null, "sales", "data/sales", null, null, null, format);
        Assert.Equal(format, bare.Format);
        Assert.Null(bare.Location);
    }

    [Theory]
    [InlineData("Iceberg")]
    [InlineData("delta")]
    [InlineData("PARQUET")]
    [InlineData("")]
    [InlineData(null)]
    public void The_constructor_refuses_another_format(string? format)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TableLocation(SourceKinds.StorageFolder, "data", [], "sales", "data/sales", null, null, null, format!));
    }

    // ---- the wording of the storage errors ----

    public static TheoryData<string?, string> Services => new()
    {
        { "https://pqaxtest.dfs.core.windows.net", "The storage account answered 403" },
        { "https://pqaxtest.dfs.core.chinacloudapi.cn", "The storage account answered 403" },
        { "https://onelake.test", "OneLake answered 403" },
        { null, "OneLake answered 403" },
    };

    [Theory]
    [MemberData(nameof(Services))]
    public async Task A_storage_error_names_the_storage_account_on_an_adls_endpoint_and_onelake_otherwise(string? endpoint, string start)
    {
        using TempLake lake = FolderLayout.Create();
        var handler = new FakeOneLake(lake.Root, FolderLayout.Filesystem) { Override = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) };
        var options = new OneLakeStorageOptions { HttpClient = new HttpClient(handler), MaxRetries = 0 };
        if (endpoint != null)
        {
            options.Endpoint = new Uri(endpoint);
        }

        using var storage = new OneLakeStorage(FolderLayout.Filesystem, _ => Task.FromResult(FolderLayout.Token), options);
        var listing = await Assert.ThrowsAsync<StorageException>(() => storage.ListAsync("data", CancellationToken.None));
        var reading = await Assert.ThrowsAsync<StorageException>(() => storage.ReadRangeAsync("data/raw/" + FolderLayout.OlderFile, 0, 10, CancellationToken.None));
        foreach (StorageException error in new[] { listing, reading })
        {
            Assert.StartsWith(start, error.Message, StringComparison.Ordinal);
            Assert.Equal(403, error.StatusCode);
            Assert.True(error.IsAccessDenied);
            Assert.DoesNotContain(FolderLayout.Token, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("data", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_storage_error_of_an_account_says_so_for_other_codes_too()
    {
        using TempLake lake = FolderLayout.Create();
        var handler = new FakeOneLake(lake.Root, FolderLayout.Filesystem) { Override = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        using OneLakeStorage storage = FolderLayout.Adls(handler);
        var error = await Assert.ThrowsAsync<StorageException>(() => storage.ListAsync("data", CancellationToken.None));
        Assert.StartsWith("The storage account answered 500", error.Message, StringComparison.Ordinal);
        Assert.Equal(500, error.StatusCode);
    }
}
