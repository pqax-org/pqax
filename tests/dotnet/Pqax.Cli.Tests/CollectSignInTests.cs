using System.IO.Compression;
using System.Net;
using System.Text;
using Azure.Identity;

namespace Pqax.Cli.Tests;

/// <summary>
/// <c>pqax collect --workspace</c> in the process, with a fake credential and a fake OneLake endpoint through
/// <see cref="CollectHost"/>: the line of a 401 and of a 403, the Authorization header, and no secret anywhere with
/// --verbose. A real Azure.Identity credential is built, so that the secret is read, but never asked for a token: a
/// fake credential signs in. The tests in a process of its own make no sign-in.
/// </summary>
public class CollectSignInTests
{
    private const string Workspace = "Test Workspace";
    private const string Item = "Fix_Lakehouse.Lakehouse";
    private const string Token = "fake-access-token-for-the-tests-7Q2";
    private const string Secret = "Synthetic~Client.Secret-5X9wQ3";

    private static string Lakehouse => Path.Combine(CliKit.Root, "conformance", "fixtures", "lakehouse");

    private sealed class Output : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pqax-cli-signin-" + Guid.NewGuid().ToString("N") + ".pqax");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    private static (int Exit, string Output, string Error) Run(CollectHost host, Output output, params string[] options)
    {
        using var standard = new StringWriter();
        using var error = new StringWriter();
        int exit = CollectCommand.Run(["collect", "--workspace", Workspace, "--item", Item, "--output", output.Path, .. options], standard, error, host);
        return (exit, standard.ToString(), error.ToString());
    }

    private static CollectHost Host(FakeOneLakeEndpoint endpoint, FakeCredential credential) => new()
    {
        GetVariable = _ => null,
        CreateCredential = _ => credential,
        HttpClient = new HttpClient(endpoint),
        Endpoint = FakeOneLakeEndpoint.Endpoint,
    };

    private static string[] Lines(string text) => text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).ToArray();

    [Fact]
    public void A_403_exits_with_2_and_one_line_that_names_the_workspace_role()
    {
        var endpoint = new FakeOneLakeEndpoint(Lakehouse, Workspace) { Status = HttpStatusCode.Forbidden };
        using var output = new Output();
        (int exit, string text, string error) = Run(Host(endpoint, new FakeCredential(Token)), output);
        Assert.True(exit == 2, $"exit code {exit}: {text}{error}");
        string line = Assert.Single(Lines(error));
        Assert.StartsWith("pqax collect: ", line, StringComparison.Ordinal);
        Assert.Contains("403", line, StringComparison.Ordinal);
        Assert.Contains("workspace", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Contributor", line, StringComparison.Ordinal);
        Assert.NotEmpty(endpoint.Authorizations);
    }

    [Fact]
    public void A_401_exits_with_2_and_one_line_that_names_the_tenant_setting()
    {
        var endpoint = new FakeOneLakeEndpoint(Lakehouse, Workspace) { Status = HttpStatusCode.Unauthorized };
        using var output = new Output();
        (int exit, string text, string error) = Run(Host(endpoint, new FakeCredential(Token)), output);
        Assert.True(exit == 2, $"exit code {exit}: {text}{error}");
        string line = Assert.Single(Lines(error));
        Assert.StartsWith("pqax collect: ", line, StringComparison.Ordinal);
        Assert.Contains("401", line, StringComparison.Ordinal);
        Assert.Contains("tenant", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_request_carries_the_token_of_the_credential()
    {
        var endpoint = new FakeOneLakeEndpoint(Lakehouse, Workspace) { Status = HttpStatusCode.Forbidden };
        using var output = new Output();
        Run(Host(endpoint, new FakeCredential(Token)), output);
        Assert.NotEmpty(endpoint.Authorizations);
        Assert.All(endpoint.Authorizations, header => Assert.Equal("Bearer " + Token, header));
        Assert.All(endpoint.Urls, url => Assert.StartsWith(FakeOneLakeEndpoint.Endpoint.ToString(), url, StringComparison.Ordinal));
    }

    [Fact]
    public void A_credential_that_fails_exits_with_2_and_one_line()
    {
        var endpoint = new FakeOneLakeEndpoint(Lakehouse, Workspace);
        var credential = new FakeCredential(_ => throw new AuthenticationFailedException("ClientSecretCredential authentication failed: AADSTS7000215: Synthetic.\r\nTrace ID: 0"));
        using var output = new Output();
        (int exit, string text, string error) = Run(Host(endpoint, credential), output);
        Assert.True(exit == 2, $"exit code {exit}: {text}{error}");
        string line = Assert.Single(Lines(error));
        Assert.StartsWith("pqax collect: ", line, StringComparison.Ordinal);
        Assert.Contains("AADSTS7000215", line, StringComparison.Ordinal);
        Assert.Empty(endpoint.Authorizations);
    }

    [Fact]
    public void A_variable_that_is_not_set_exits_with_2_without_its_name()
    {
        var endpoint = new FakeOneLakeEndpoint(Lakehouse, Workspace);
        var host = new CollectHost { GetVariable = _ => null, HttpClient = new HttpClient(endpoint), Endpoint = FakeOneLakeEndpoint.Endpoint };
        using var output = new Output();
        (int exit, string text, string error) = Run(host, output, "--client-id", SignInKit.ClientId, "--tenant", SignInKit.Tenant, "--client-secret-env", "PQAX_NAME_OF_A_VARIABLE");
        Assert.True(exit == 2, $"exit code {exit}: {text}{error}");
        Assert.Contains("--client-secret-env", error, StringComparison.Ordinal);
        Assert.DoesNotContain("PQAX_NAME_OF_A_VARIABLE", text + error, StringComparison.Ordinal);
        Assert.Empty(endpoint.Authorizations);
        Assert.False(File.Exists(output.Path));
    }

    /// <summary>The text of every entry of a zip archive, inflated, and its raw bytes.</summary>
    private static IEnumerable<byte[]> Contents(string path)
    {
        yield return File.ReadAllBytes(path);
        using ZipArchive archive = ZipFile.OpenRead(path);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using Stream stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            yield return buffer.ToArray();
        }
    }

    private static bool Holds(byte[] haystack, string text)
    {
        foreach (byte[] needle in new[] { Encoding.UTF8.GetBytes(text), Encoding.Unicode.GetBytes(text) })
        {
            if (haystack.AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void A_client_secret_with_verbose_writes_a_footprint_and_the_secret_appears_nowhere()
    {
        var endpoint = new FakeOneLakeEndpoint(Lakehouse, Workspace);
        Func<string, string?> variables = name => name == "PQAX_TEST_SECRET" ? Secret : null;
        Azure.Core.TokenCredential? created = null;
        var host = new CollectHost
        {
            GetVariable = variables,
            // The real credential is built from the variable, so that the secret is read, and a fake one signs in.
            CreateCredential = options =>
            {
                created = SignIn.CreateCredential(options, variables, TextWriter.Null);
                return new FakeCredential(Token);
            },
            HttpClient = new HttpClient(endpoint),
            Endpoint = FakeOneLakeEndpoint.Endpoint,
        };
        using var output = new Output();
        (int exit, string text, string error) = Run(host, output, "--client-id", SignInKit.ClientId, "--tenant", SignInKit.Tenant, "--client-secret-env", "PQAX_TEST_SECRET", "--verbose");
        Assert.True(exit == 0, $"exit code {exit}: {text}{error}");
        Assert.IsType<ClientSecretCredential>(created);
        foreach (string hidden in new[] { Secret, Token, "Bearer" })
        {
            Assert.DoesNotContain(hidden, text, StringComparison.Ordinal);
            Assert.DoesNotContain(hidden, error, StringComparison.Ordinal);
        }

        // The line of --verbose: the kind of identity, the client id, the tenant, and the option that holds the secret.
        Assert.Contains(SignInKit.ClientId, error, StringComparison.Ordinal);
        Assert.Contains("--client-secret-env", error, StringComparison.Ordinal);
        Assert.NotEmpty(endpoint.Authorizations);
        Assert.All(endpoint.Authorizations, header => Assert.Equal("Bearer " + Token, header));

        Assert.True(File.Exists(output.Path));
        foreach (byte[] content in Contents(output.Path))
        {
            Assert.False(Holds(content, Secret), "the footprint holds the secret");
            Assert.False(Holds(content, Token), "the footprint holds the token");
            Assert.False(Holds(content, SignInKit.ClientId), "the footprint holds the client id");
        }

        using PqaxDocument doc = PqaxDocument.Open(output.Path);
        TableRow table = Assert.Single(doc.Summary.Tables);
        Assert.Equal("sales", table.Name);
        Assert.True(PqaxValidator.Validate(output.Path).IsValid);
    }

    public static TheoryData<string, int> SecretsAndAnswers => new()
    {
        { "secret", 403 },
        { "secret", 401 },
        { "environment", 403 },
        { "environment", 401 },
    };

    [Theory]
    [MemberData(nameof(SecretsAndAnswers))]
    public void A_refused_sign_in_with_verbose_never_prints_the_secret(string identity, int status)
    {
        var endpoint = new FakeOneLakeEndpoint(Lakehouse, Workspace) { Status = (HttpStatusCode)status };
        var variables = new Dictionary<string, string?>
        {
            ["PQAX_TEST_SECRET"] = Secret,
            ["AZURE_TENANT_ID"] = SignInKit.Tenant,
            ["AZURE_CLIENT_ID"] = SignInKit.ClientId,
            ["AZURE_CLIENT_SECRET"] = Secret,
        };
        Func<string, string?> getVariable = name => variables.TryGetValue(name, out string? value) ? value : null;
        Azure.Core.TokenCredential? created = null;
        var host = new CollectHost
        {
            GetVariable = getVariable,
            CreateCredential = options =>
            {
                created = SignIn.CreateCredential(options, getVariable, TextWriter.Null);
                return new FakeCredential(Token);
            },
            HttpClient = new HttpClient(endpoint),
            Endpoint = FakeOneLakeEndpoint.Endpoint,
        };
        string[] options = identity == "secret"
            ? ["--client-id", SignInKit.ClientId, "--tenant", SignInKit.Tenant, "--client-secret-env", "PQAX_TEST_SECRET", "--verbose"]
            : ["--env-credential", "--verbose"];
        using var output = new Output();
        (int exit, string text, string error) = Run(host, output, options);
        Assert.True(exit == 2, $"exit code {exit}: {text}{error}");
        Assert.IsType<ClientSecretCredential>(created);
        foreach (string hidden in new[] { Secret, Token, "Bearer" })
        {
            Assert.DoesNotContain(hidden, text, StringComparison.Ordinal);
            Assert.DoesNotContain(hidden, error, StringComparison.Ordinal);
        }

        string last = Lines(error)[^1];
        Assert.StartsWith("pqax collect: ", last, StringComparison.Ordinal);
        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), last, StringComparison.Ordinal);
    }

    // ---- in a process of its own: no sign-in is attempted ----

    [Fact]
    public void A_variable_that_is_not_set_exits_with_2_in_a_process()
    {
        using var output = new Output();
        var environment = new Dictionary<string, string?> { ["PQAX_NAME_OF_A_VARIABLE"] = null };
        (int exit, string text, string error) = SignInKit.Run(environment,
            "collect", "--workspace", "W", "--item", "I.Lakehouse", "--output", output.Path,
            "--client-id", SignInKit.ClientId, "--tenant", SignInKit.Tenant, "--client-secret-env", "PQAX_NAME_OF_A_VARIABLE");
        Assert.True(exit == 2, $"exit code {exit}: {text}{error}");
        Assert.DoesNotContain("PQAX_NAME_OF_A_VARIABLE", text + error, StringComparison.Ordinal);
        Assert.False(File.Exists(output.Path));
    }

    [Fact]
    public void A_secret_on_the_command_line_exits_with_2_in_a_process_and_is_not_echoed()
    {
        (int exit, string text, string error) = CliKit.Run(
            "collect", "--workspace", "W", "--item", "I.Lakehouse", "--output", "unused.pqax",
            "--client-id", SignInKit.ClientId, "--tenant", SignInKit.Tenant, "--client-secret", Secret);
        Assert.Equal(2, exit);
        Assert.DoesNotContain(Secret, text + error, StringComparison.Ordinal);
    }
}
