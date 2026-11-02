using System.Net;
using Pqax.Collector;

namespace Pqax.Cli.Tests;

/// <summary>
/// <c>pqax collect --adls</c> in the process, with a fake credential and the fake DFS endpoint through
/// <see cref="CollectHost"/>, which answers for the account <c>pqaxtest</c> and its filesystem <c>lake</c>: the forms of
/// the URL, the lines it prints, the footprint of a folder, the errors of the command line, the identity options that it
/// shares with --workspace, and the line of a 401 and of a 403 of a storage account.
/// </summary>
public class CollectAdlsTests
{
    private const string Filesystem = "lake";
    private const string Account = "https://pqaxtest.dfs.core.windows.net";
    private const string Container = "abfss://lake@pqaxtest.dfs.core.windows.net";
    private const string Url = Account + "/lake/data";
    private const string Token = "fake-access-token-for-the-adls-tests-3K8";
    private const string Tenant = SignInKit.Tenant;
    private const string ClientId = SignInKit.ClientId;

    private static string FixtureTable => Path.Combine(CliKit.Root, "conformance", "fixtures", "lakehouse", "Fix_Lakehouse.Lakehouse", "Tables", "sales");

    /// <summary>
    /// A container in a temporary folder: under <c>data</c>, a copy of the Delta table of the fixture, a folder of two of
    /// its Parquet files, and a folder of other files; under <c>my data</c>, another folder of Parquet files.
    /// </summary>
    private sealed class Lake : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pqax-cli-adls-" + Guid.NewGuid().ToString("N"));

        public Lake()
        {
            foreach (string file in Directory.EnumerateFiles(FixtureTable, "*", SearchOption.AllDirectories))
            {
                Copy(file, Path.Combine("data", "sales", Path.GetRelativePath(FixtureTable, file)));
            }

            DateTime time = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
            foreach (string folder in new[] { Path.Combine("data", "raw"), Path.Combine("my data", "raw") })
            {
                Copy(Path.Combine(FixtureTable, "part-0001.parquet"), Path.Combine(folder, "part-0001.parquet"), time);
                Copy(Path.Combine(FixtureTable, "part-0002.parquet"), Path.Combine(folder, "part-0002.parquet"), time.AddHours(1));
                File.WriteAllText(Path.Combine(Root, folder, "_SUCCESS"), "");
            }

            Directory.CreateDirectory(Path.Combine(Root, "data", "other"));
            File.WriteAllText(Path.Combine(Root, "data", "other", "a.csv"), "id,name\n1,synthetic\n");
            File.WriteAllText(Path.Combine(Root, "data", "other", "b.json"), "{\"id\":1}\n");
        }

        private void Copy(string from, string to, DateTime? modified = null)
        {
            string target = Path.Combine(Root, to);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(from, target);
            if (modified != null)
            {
                File.SetLastWriteTimeUtc(target, modified.Value);
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class Output : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pqax-cli-adls-" + Guid.NewGuid().ToString("N") + ".pqax");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    /// <summary>
    /// A host whose credential is fake and whose HTTP client is the fake endpoint. Its OneLake endpoint is another host,
    /// which --adls must not use.
    /// </summary>
    private static CollectHost Host(FakeOneLakeEndpoint endpoint, FakeCredential credential, Action<SignInOptions>? signIn = null) => new()
    {
        GetVariable = _ => null,
        CreateCredential = options =>
        {
            signIn?.Invoke(options);
            return credential;
        },
        HttpClient = new HttpClient(endpoint),
        Endpoint = FakeOneLakeEndpoint.Endpoint,
    };

    private static (int Exit, string Output, string Error) Run(CollectHost host, params string[] arguments)
    {
        using var standard = new StringWriter();
        using var error = new StringWriter();
        int exit = CollectCommand.Run(arguments, standard, error, host);
        return (exit, standard.ToString(), error.ToString());
    }

    private static (int Exit, string Output, string Error) Collect(CollectHost host, Output output, string url, params string[] options) =>
        Run(host, ["collect", "--adls", url, "--output", output.Path, .. options]);

    private static string[] Lines(string text) => text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).ToArray();

    // ---- a collection ----

    [Theory]
    [InlineData(Account + "/lake/data")]
    [InlineData(Account + "/lake/data/")]
    [InlineData(Container + "/data")]
    [InlineData(Container + "/data/")]
    public void A_folder_of_a_container_is_collected_with_the_token_of_the_credential(string url)
    {
        using var lake = new Lake();
        var endpoint = new FakeOneLakeEndpoint(lake.Root, Filesystem);
        var credential = new FakeCredential(Token);
        SignInOptions? signIn = null;
        using var output = new Output();
        (int exit, string text, string error) = Collect(Host(endpoint, credential, options => signIn = options), output, url, "--azure-cli");
        Assert.True(exit == 0, $"exit code {exit}: {text}{error}");

        // The lines of a workspace collection, after the count of the tables of the folder; no name of a table.
        string[] lines = Lines(text);
        // The folder is not named: the command prints counts only.
        Assert.Contains("Found 2 tables in the folder.", lines);
        Assert.Equal(2, lines.Count(line => line.StartsWith("  table ", StringComparison.Ordinal)));
        string last = lines[^1];
        Assert.StartsWith(Path.GetFileName(output.Path) + ": 2 tables, ", last, StringComparison.Ordinal);
        Assert.EndsWith("; valid", last, StringComparison.Ordinal);
        foreach (string name in new[] { "sales", "raw", Token, "Bearer" })
        {
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);
            Assert.DoesNotContain(name, error, StringComparison.Ordinal);
        }

        // The identity of the command line signs in, and every request goes to the account with its token.
        Assert.NotNull(signIn);
        Assert.Equal(SignInMode.AzureCli, signIn.Mode);
        Assert.True(credential.Calls >= 1);
        Assert.All(credential.Scopes, scopes => Assert.Equal(["https://storage.azure.com/.default"], scopes));
        Assert.NotEmpty(endpoint.Urls);
        Assert.All(endpoint.Urls, address => Assert.StartsWith(Account + "/lake", address, StringComparison.Ordinal));
        Assert.All(endpoint.Authorizations, header => Assert.Equal("Bearer " + Token, header));

        // One source, the folder, and a Delta table and a Parquet table.
        Assert.True(PqaxValidator.Validate(output.Path).IsValid);
        using PqaxDocument doc = PqaxDocument.Open(output.Path);
        SourceRow source = Assert.Single(doc.Summary.Sources);
        Assert.Equal("Storage folder", source.Kind);
        Assert.Equal("data", source.Name);
        Assert.Equal(Container + "/data", source.Location);
        Assert.Null(source.Identifiers);
        Assert.Equal(
            [("raw", "Parquet", Container + "/data/raw"), ("sales", "Delta", Container + "/data/sales")],
            doc.Summary.Tables.Select(table => (table.Name, table.Format, table.Location)).OrderBy(table => table.Name, StringComparer.Ordinal));
        Assert.Empty(doc.Summary.Issues);
        Assert.Equal(2, doc.Manifest.Collection.Level);
    }

    [Theory]
    [InlineData(Account + "/lake")]
    [InlineData(Account + "/lake/")]
    [InlineData(Container)]
    [InlineData(Container + "/")]
    public void The_root_of_a_container_is_collected_as_the_container(string url)
    {
        using var lake = new Lake();
        var endpoint = new FakeOneLakeEndpoint(lake.Root, Filesystem);
        using var output = new Output();
        (int exit, string text, string error) = Collect(Host(endpoint, new FakeCredential(Token)), output, url);
        Assert.True(exit == 0, $"exit code {exit}: {text}{error}");
        Assert.Contains("Found 3 tables in the container.", Lines(text));
        using PqaxDocument doc = PqaxDocument.Open(output.Path);
        SourceRow source = Assert.Single(doc.Summary.Sources);
        Assert.Equal("lake", source.Name);
        Assert.Equal(Container, source.Location);
        Assert.Equal(3, doc.Summary.Tables.Count);
        Assert.Contains(doc.Summary.Tables, table => table.Name == "raw" && (table.Namespace ?? []).SequenceEqual(["my data"]));
    }

    [Theory]
    [InlineData(Account + "/lake/my%20data")]
    [InlineData(Container + "/my%20data")]
    public void The_segments_of_the_url_are_decoded(string url)
    {
        using var lake = new Lake();
        var endpoint = new FakeOneLakeEndpoint(lake.Root, Filesystem);
        using var output = new Output();
        (int exit, string text, string error) = Collect(Host(endpoint, new FakeCredential(Token)), output, url);
        Assert.True(exit == 0, $"exit code {exit}: {text}{error}");
        using PqaxDocument doc = PqaxDocument.Open(output.Path);
        SourceRow source = Assert.Single(doc.Summary.Sources);
        Assert.Equal("my data", source.Name);
        Assert.Equal(Container + "/my data", source.Location);
        TableRow table = Assert.Single(doc.Summary.Tables);
        Assert.Equal("Parquet", table.Format);
        Assert.Equal(Container + "/my data/raw", table.Location);
    }

    // System.Uri reads the URL before CollectOptions splits it: the host in lower case, port 443 left out, a backslash taken
    // for a slash, the dot segments resolved, %2E-encoded too, and the segments and the container percent-decoded.
    [Theory]
    [InlineData(Account + "/lake/a/../b", "https://pqaxtest.dfs.core.windows.net/", "lake", "b")]
    [InlineData(Account + "/lake/a//../b", "https://pqaxtest.dfs.core.windows.net/", "lake", "a/b")]
    [InlineData(Account + "/lake/./a/%2E/b", "https://pqaxtest.dfs.core.windows.net/", "lake", "a/b")]
    [InlineData(Account + "/lake/a/%2E%2E/b", "https://pqaxtest.dfs.core.windows.net/", "lake", "b")]
    [InlineData(Account + "/../lake/x", "https://pqaxtest.dfs.core.windows.net/", "lake", "x")]
    [InlineData(Container + "/a/../b", "https://pqaxtest.dfs.core.windows.net/", "lake", "b")]
    [InlineData("https://PQAXTEST.dfs.core.windows.net:443/lake/data", "https://pqaxtest.dfs.core.windows.net/", "lake", "data")]
    [InlineData(Account + "/lake\\a\\b", "https://pqaxtest.dfs.core.windows.net/", "lake", "a/b")]
    [InlineData(Account + "/la%6Be/my%20data", "https://pqaxtest.dfs.core.windows.net/", "lake", "my data")]
    [InlineData("abfss://la%6Be@pqaxtest.dfs.core.windows.net/my%20data", "https://pqaxtest.dfs.core.windows.net/", "lake", "my data")]
    [InlineData("https://127.0.0.1:10000/lake/data", "https://127.0.0.1:10000/", "lake", "data")]
    public void The_url_is_read_as_system_uri_reads_it(string url, string endpoint, string fileSystem, string folder)
    {
        CollectOptions? options = CollectOptions.Parse(["collect", "--adls", url, "--output", UnusedOutput], out string? problem);
        Assert.True(options != null, $"{url} was refused: {problem}");
        Assert.Equal(endpoint, options.AdlsEndpoint!.AbsoluteUri);
        Assert.Equal(fileSystem, options.AdlsFileSystem);
        Assert.Equal(folder, options.AdlsFolder);
    }

    [Fact]
    public void The_options_of_a_collection_apply_to_a_container()
    {
        using var lake = new Lake();
        var endpoint = new FakeOneLakeEndpoint(lake.Root, Filesystem);
        using var output = new Output();
        (int exit, string text, string error) = Collect(Host(endpoint, new FakeCredential(Token)), output, Url, "--level", "1", "--depth", "summary");
        Assert.True(exit == 0, $"exit code {exit}: {text}{error}");
        using PqaxDocument doc = PqaxDocument.Open(output.Path);
        Assert.Equal(1, doc.Manifest.Collection.Level);
        Assert.All(doc.Summary.Tables, table =>
        {
            Assert.Equal(0, table.FooterCount);
            Assert.Equal("Summary", table.DetailDepth);
        });
    }

    [Theory]
    [InlineData(Url + "/other")]
    [InlineData(Url + "/no_such_folder")]
    public void A_folder_with_no_table_exits_with_2_and_writes_nothing(string url)
    {
        using var lake = new Lake();
        var endpoint = new FakeOneLakeEndpoint(lake.Root, Filesystem);
        using var output = new Output();
        (int exit, string text, string error) = Collect(Host(endpoint, new FakeCredential(Token)), output, url);
        Assert.True(exit == 2, $"exit code {exit}: {text}{error}");
        Assert.Contains("pqax collect: no table found", error, StringComparison.Ordinal);
        Assert.Contains("--adls", error, StringComparison.Ordinal);
        Assert.False(File.Exists(output.Path));
    }

    // ---- the answers of the storage account ----

    [Fact]
    public void A_403_of_the_account_exits_with_2_and_one_line_that_names_the_data_role()
    {
        using var lake = new Lake();
        var endpoint = new FakeOneLakeEndpoint(lake.Root, Filesystem) { Status = HttpStatusCode.Forbidden };
        using var output = new Output();
        (int exit, string text, string error) = Collect(Host(endpoint, new FakeCredential(Token)), output, Url);
        Assert.True(exit == 2, $"exit code {exit}: {text}{error}");
        string line = Assert.Single(Lines(error));
        Assert.StartsWith("pqax collect: ", line, StringComparison.Ordinal);
        Assert.Contains("403", line, StringComparison.Ordinal);
        Assert.Contains("Storage Blob Data Reader", line, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, text + error, StringComparison.Ordinal);
        Assert.NotEmpty(endpoint.Authorizations);
        Assert.All(endpoint.Authorizations, header => Assert.Equal("Bearer " + Token, header));
        Assert.False(File.Exists(output.Path));
    }

    [Fact]
    public void A_401_of_the_account_exits_with_2_and_one_line_that_names_the_audience_and_the_tenant()
    {
        using var lake = new Lake();
        var endpoint = new FakeOneLakeEndpoint(lake.Root, Filesystem) { Status = HttpStatusCode.Unauthorized };
        using var output = new Output();
        (int exit, string text, string error) = Collect(Host(endpoint, new FakeCredential(Token)), output, Url);
        Assert.True(exit == 2, $"exit code {exit}: {text}{error}");
        string line = Assert.Single(Lines(error));
        Assert.StartsWith("pqax collect: ", line, StringComparison.Ordinal);
        Assert.Contains("401", line, StringComparison.Ordinal);
        Assert.Contains("https://storage.azure.com", line, StringComparison.Ordinal);
        Assert.Contains("tenant", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fabric", line, StringComparison.Ordinal);
    }

    private static void ContainsAll(string line, params string[] words)
    {
        Assert.DoesNotContain('\n', line);
        foreach (string word in words)
        {
            Assert.True(line.Contains(word, StringComparison.OrdinalIgnoreCase), $"\"{word}\" is not in: {line}");
        }
    }

    [Fact]
    public void The_line_of_a_403_of_a_storage_account_names_the_role_the_acls_and_the_network_rules()
    {
        string line = SignIn.Describe(new StorageException("The storage account answered 403 Forbidden.", 403), storageAccount: true);
        ContainsAll(line, "403", "Storage Blob Data Reader", "container", "account", "ACL", "read", "execute", "network");
        Assert.DoesNotContain("workspace", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_line_of_a_401_of_a_storage_account_names_the_audience_and_the_tenant_of_the_account()
    {
        string line = SignIn.Describe(new StorageException("The storage account answered 401 Unauthorized.", 401), storageAccount: true);
        ContainsAll(line, "401", "https://storage.azure.com", "tenant", "account");
    }

    [Fact]
    public void The_lines_of_onelake_stay_those_of_a_workspace()
    {
        ContainsAll(SignIn.Describe(new StorageException("OneLake answered 403 Forbidden.", 403), storageAccount: false), "403", "workspace", "Contributor");
        ContainsAll(SignIn.Describe(new StorageException("OneLake answered 401 Unauthorized.", 401), storageAccount: false), "401", "tenant");
        Assert.Equal(SignIn.Describe(new StorageException("OneLake answered 403 Forbidden.", 403)),
            SignIn.Describe(new StorageException("OneLake answered 403 Forbidden.", 403), storageAccount: false));
    }

    // ---- the command line ----

    private static string UnusedOutput => Path.Combine(Path.GetTempPath(), "pqax-adls-unused.pqax");

    private const string Signature = "SyntheticSignatureValue0123";

    public static TheoryData<string, string[], string?> WrongCommandLines => new()
    {
        { "with --local", ["collect", "--adls", Url, "--local", ".", "--output", UnusedOutput], "give one of --local, --workspace, or --adls" },
        { "with --workspace", ["collect", "--adls", Url, "--workspace", "W", "--item", "I.Lakehouse", "--output", UnusedOutput], "give one of --local, --workspace, or --adls" },
        { "with --item", ["collect", "--adls", Url, "--item", "I.Lakehouse", "--output", UnusedOutput], "--item" },
        { "no source", ["collect", "--output", UnusedOutput], "give one of --local, --workspace, or --adls" },
        { "local and workspace", ["collect", "--local", ".", "--workspace", "W", "--item", "I.Lakehouse", "--output", UnusedOutput], "give one of --local, --workspace, or --adls" },
        { "no value", ["collect", "--adls", "--output", UnusedOutput], "--adls" },
        { "http", ["collect", "--adls", "http://pqaxtest.dfs.core.windows.net/lake/data", "--output", UnusedOutput], null },
        { "s3", ["collect", "--adls", "s3://lake/data", "--output", UnusedOutput], null },
        { "no scheme", ["collect", "--adls", "pqaxtest.dfs.core.windows.net/lake/data", "--output", UnusedOutput], null },
        { "blob host", ["collect", "--adls", "https://pqaxtest.blob.core.windows.net/lake/data", "--output", UnusedOutput], ".dfs.core.windows.net" },
        { "blob host abfss", ["collect", "--adls", "abfss://lake@pqaxtest.blob.core.windows.net/data", "--output", UnusedOutput], ".dfs.core.windows.net" },
        { "onelake host", ["collect", "--adls", "https://onelake.dfs.fabric.microsoft.com/Workspace/Item.Lakehouse/Tables", "--output", UnusedOutput], "--workspace" },
        { "onelake host abfss", ["collect", "--adls", "abfss://work@onelake.dfs.fabric.microsoft.com/Item.Lakehouse", "--output", UnusedOutput], "--workspace" },
        { "no filesystem", ["collect", "--adls", Account, "--output", UnusedOutput], null },
        { "no filesystem slash", ["collect", "--adls", Account + "/", "--output", UnusedOutput], null },
        { "no filesystem abfss", ["collect", "--adls", "abfss://pqaxtest.dfs.core.windows.net/data", "--output", UnusedOutput], null },
        { "uppercase filesystem", ["collect", "--adls", Account + "/Lake/data", "--output", UnusedOutput], null },
        { "uppercase filesystem abfss", ["collect", "--adls", "abfss://Lake@pqaxtest.dfs.core.windows.net/data", "--output", UnusedOutput], null },
        { "short filesystem", ["collect", "--adls", Account + "/ab/data", "--output", UnusedOutput], null },
        { "long filesystem", ["collect", "--adls", Account + "/" + new string('a', 64) + "/data", "--output", UnusedOutput], null },
        { "underscore filesystem", ["collect", "--adls", Account + "/my_lake/data", "--output", UnusedOutput], null },
        { "newline filesystem", ["collect", "--adls", Account + "/lake%0A/data", "--output", UnusedOutput], "not a valid name" },
        { "newline filesystem abfss", ["collect", "--adls", "abfss://lake%0A@pqaxtest.dfs.core.windows.net/data", "--output", UnusedOutput], "not a valid name" },
        { "dot segments above the filesystem", ["collect", "--adls", Account + "/lake/..", "--output", UnusedOutput], "the container is missing" },
        { "encoded slash and ..", ["collect", "--adls", Account + "/lake/x%2F..%2Fy", "--output", UnusedOutput], "cannot hold . or .. segments" },
        { "encoded slash and .", ["collect", "--adls", Account + "/lake/x%2F.", "--output", UnusedOutput], "cannot hold . or .. segments" },
        { "encoded slash and encoded ..", ["collect", "--adls", Account + "/lake/x%2F%2E%2E%2Fy", "--output", UnusedOutput], "cannot hold . or .. segments" },
        { "encoded slash and .. abfss", ["collect", "--adls", Container + "/..%2Fy", "--output", UnusedOutput], "cannot hold . or .. segments" },
        { "encoded backslash", ["collect", "--adls", Account + "/lake/x%5Cy", "--output", UnusedOutput], "cannot hold a backslash" },
        { "encoded backslash and ..", ["collect", "--adls", Account + "/lake/x%5C..%5Cy", "--output", UnusedOutput], "cannot hold a backslash" },
        { "encoded backslash abfss", ["collect", "--adls", Container + "/x%5c", "--output", UnusedOutput], "cannot hold a backslash" },
        { "sas", ["collect", "--adls", Url + "?sv=2024-01-01&sp=rl&sig=" + Signature, "--output", UnusedOutput], null },
        { "sas abfss", ["collect", "--adls", Container + "/data?sig=" + Signature, "--output", UnusedOutput], null },
    };

    [Theory]
    [MemberData(nameof(WrongCommandLines))]
    public void A_wrong_command_line_with_adls_exits_with_2_before_any_request(string wrong, string[] arguments, string? words)
    {
        using var lake = new Lake();
        var endpoint = new FakeOneLakeEndpoint(lake.Root, Filesystem);
        var credential = new FakeCredential(Token);
        (int exit, string text, string error) = Run(Host(endpoint, credential), arguments);
        Assert.True(exit == 2, $"{wrong}: exit code {exit}: {text}{error}");
        string first = Lines(error)[0];
        Assert.StartsWith("pqax collect: ", first, StringComparison.Ordinal);
        if (words != null)
        {
            Assert.Contains(words, first, StringComparison.Ordinal);
        }

        // A URL with a query is never quoted: the query can be a SAS token.
        foreach (string hidden in new[] { Signature, "sig=", "sv=2024", "pqaxtest" })
        {
            if (wrong.StartsWith("sas", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(hidden, text + error, StringComparison.Ordinal);
            }
        }

        Assert.Equal(0, credential.Calls);
        Assert.Empty(endpoint.Urls);
        Assert.False(File.Exists(UnusedOutput));
    }

    // ---- the identity options ----

    private static string[] Adls(params string[] identity) => ["collect", "--adls", Url, "--output", UnusedOutput, .. identity];

    public static TheoryData<string[]> Identities => new()
    {
        Array.Empty<string>(),
        new[] { "--device-code" },
        new[] { "--device-code", "--tenant", Tenant },
        new[] { "--browser", "--tenant", Tenant },
        new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-secret-env", "PQAX_SECRET" },
        new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-certificate", "cert.pfx" },
        new[] { "--client-certificate", "cert.pfx", "--client-certificate-password-env", "PQAX_PFX", "--client-id", ClientId, "--tenant", Tenant },
        new[] { "--managed-identity" },
        new[] { "--managed-identity", "--client-id", ClientId },
        new[] { "--azure-cli" },
        new[] { "--azure-cli", "--tenant", Tenant },
        new[] { "--access-token-env", "PQAX_TOKEN" },
        new[] { "--env-credential" },
    };

    [Theory]
    [MemberData(nameof(Identities))]
    public void Each_identity_parses_with_adls_as_with_workspace(string[] identity)
    {
        SignInOptions expected = SignInKit.Valid(identity);
        CollectOptions? options = CollectOptions.Parse(Adls(identity), out string? problem);
        Assert.True(options != null, $"{string.Join(' ', identity)} was refused with --adls: {problem}");
        Assert.Null(problem);
        SignInOptions actual = options.SignIn;
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.Tenant, actual.Tenant);
        Assert.Equal(expected.ClientId, actual.ClientId);
        Assert.Equal(expected.ClientSecretVariable, actual.ClientSecretVariable);
        Assert.Equal(expected.ClientCertificatePath, actual.ClientCertificatePath);
        Assert.Equal(expected.ClientCertificatePasswordVariable, actual.ClientCertificatePasswordVariable);
        Assert.Equal(expected.AccessTokenVariable, actual.AccessTokenVariable);
        Assert.Null(options.Workspace);
        Assert.Null(options.Local);
        Assert.Empty(options.Items);
        Assert.Equal(UnusedOutput, options.Output);
    }

    public static TheoryData<string[]> WrongIdentities => new()
    {
        new[] { "--device-code", "--browser" },
        new[] { "--azure-cli", "--managed-identity" },
        new[] { "--client-secret-env", "PQAX_SECRET" },
        new[] { "--client-id", ClientId, "--client-certificate", "cert.pfx" },
        new[] { "--managed-identity", "--tenant", Tenant },
        new[] { "--env-credential", "--tenant", Tenant },
        new[] { "--client-certificate-password-env", "PQAX_PFX" },
        new[] { "--access-token-env", "not-a-name" },
        new[] { "--client-secret", "Synthetic~Secret.Value-0123" },
        new[] { "--access-token=Synthetic~Token.Value-0123" },
    };

    [Theory]
    [MemberData(nameof(WrongIdentities))]
    public void A_wrong_identity_is_refused_with_adls_as_with_workspace(string[] identity)
    {
        string workspace = SignInKit.Refused(SignInKit.Workspace(identity));
        string adls = SignInKit.Refused(Adls(identity));
        Assert.Equal(workspace, adls);
        Assert.DoesNotContain("Synthetic~", adls, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--azure-cli")]
    [InlineData("--managed-identity")]
    public void An_identity_is_still_refused_with_local(string option)
    {
        SignInKit.Refused("collect", "--local", ".", "--output", UnusedOutput, option);
    }

    [Fact]
    public void A_url_without_identity_options_signs_in_with_a_device_code_by_default()
    {
        CollectOptions? options = CollectOptions.Parse(Adls(), out string? problem);
        Assert.True(options != null, problem);
        Assert.Equal(SignInMode.DeviceCode, options.SignIn.Mode);
    }
}
