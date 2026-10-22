using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;

namespace Pqax.Cli.Tests;

/// <summary>
/// <see cref="SignIn.CreateCredential"/>: the credential of each identity, from a fake environment, and the refusal of a
/// variable that is not set, without the value or the name of the variable in the message.
/// </summary>
public class SignInCredentialTests
{
    private const string Tenant = SignInKit.Tenant;
    private const string ClientId = SignInKit.ClientId;
    private const string Secret = "Synthetic~Secret.Value-0123456789";

    private static TokenCredential Create(SignInOptions options, IReadOnlyDictionary<string, string?> variables) =>
        SignIn.CreateCredential(options, name => variables.TryGetValue(name, out string? value) ? value : null, TextWriter.Null);

    private static TokenCredential Create(SignInOptions options) => Create(options, new Dictionary<string, string?>());

    private static SignInException Throws(SignInOptions options, IReadOnlyDictionary<string, string?> variables) =>
        Assert.Throws<SignInException>(() => Create(options, variables));

    /// <summary>A self-signed certificate of a synthetic subject in a temporary PFX file, deleted at the end of the test.</summary>
    private sealed class Certificate : IDisposable
    {
        public Certificate(string? password)
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=pqax-test-synthetic", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            Folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pqax-cert-" + Guid.NewGuid().ToString("N"))).FullName;
            Path = System.IO.Path.Combine(Folder, "synthetic.pfx");
            File.WriteAllBytes(Path, certificate.Export(X509ContentType.Pfx, password));
        }

        public string Folder { get; }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Device_code_gives_a_device_code_credential()
    {
        Assert.IsType<DeviceCodeCredential>(Create(SignInKit.Valid()));
        Assert.IsType<DeviceCodeCredential>(Create(SignInKit.Valid("--device-code", "--tenant", Tenant)));
    }

    [Fact]
    public void Browser_gives_an_interactive_browser_credential()
    {
        Assert.IsType<InteractiveBrowserCredential>(Create(SignInKit.Valid("--browser")));
    }

    [Fact]
    public void A_client_secret_gives_a_client_secret_credential()
    {
        SignInOptions options = SignInKit.Valid("--client-id", ClientId, "--tenant", Tenant, "--client-secret-env", "PQAX_TEST_SECRET");
        Assert.IsType<ClientSecretCredential>(Create(options, new Dictionary<string, string?> { ["PQAX_TEST_SECRET"] = Secret }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_client_secret_variable_that_is_not_set_throws_without_the_name_of_the_variable(string? value)
    {
        SignInOptions options = SignInKit.Valid("--client-id", ClientId, "--tenant", Tenant, "--client-secret-env", "PQAX_NAME_OF_A_VARIABLE");
        SignInException exception = Throws(options, new Dictionary<string, string?> { ["PQAX_NAME_OF_A_VARIABLE"] = value, ["OTHER"] = Secret });
        Assert.Contains("--client-secret-env", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PQAX_NAME_OF_A_VARIABLE", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', exception.Message);
    }

    [Fact]
    public void A_client_certificate_gives_a_client_certificate_credential()
    {
        using var certificate = new Certificate(null);
        SignInOptions options = SignInKit.Valid("--client-id", ClientId, "--tenant", Tenant, "--client-certificate", certificate.Path);
        Assert.IsType<ClientCertificateCredential>(Create(options));
    }

    [Fact]
    public void A_client_certificate_with_a_password_reads_it_from_its_variable()
    {
        using var certificate = new Certificate(Secret);
        SignInOptions options = SignInKit.Valid("--client-id", ClientId, "--tenant", Tenant, "--client-certificate", certificate.Path, "--client-certificate-password-env", "PQAX_TEST_PFX");
        Assert.IsType<ClientCertificateCredential>(Create(options, new Dictionary<string, string?> { ["PQAX_TEST_PFX"] = Secret }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_certificate_password_variable_that_is_not_set_throws(string? value)
    {
        using var certificate = new Certificate(Secret);
        SignInOptions options = SignInKit.Valid("--client-id", ClientId, "--tenant", Tenant, "--client-certificate", certificate.Path, "--client-certificate-password-env", "PQAX_NAME_OF_A_VARIABLE");
        SignInException exception = Throws(options, new Dictionary<string, string?> { ["PQAX_NAME_OF_A_VARIABLE"] = value });
        Assert.Contains("--client-certificate-password-env", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PQAX_NAME_OF_A_VARIABLE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_managed_identity_gives_a_managed_identity_credential()
    {
        Assert.IsType<ManagedIdentityCredential>(Create(SignInKit.Valid("--managed-identity")));
        Assert.IsType<ManagedIdentityCredential>(Create(SignInKit.Valid("--managed-identity", "--client-id", ClientId)));
    }

    [Fact]
    public void Azure_cli_gives_an_azure_cli_credential()
    {
        Assert.IsType<AzureCliCredential>(Create(SignInKit.Valid("--azure-cli")));
        Assert.IsType<AzureCliCredential>(Create(SignInKit.Valid("--azure-cli", "--tenant", Tenant)));
    }

    [Fact]
    public void An_access_token_gives_an_access_token_credential_with_the_token()
    {
        DateTimeOffset expiry = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        string token = SignInKit.Jwt(new { aud = "https://storage.azure.com", exp = expiry.ToUnixTimeSeconds() });
        TokenCredential credential = Create(SignInKit.Valid("--access-token-env", "PQAX_TEST_TOKEN"), new Dictionary<string, string?> { ["PQAX_TEST_TOKEN"] = token });
        AccessTokenCredential typed = Assert.IsType<AccessTokenCredential>(credential);
        Assert.Equal("https://storage.azure.com", typed.Audience);
        AccessToken access = credential.GetToken(new TokenRequestContext([Pqax.Collector.OneLakeStorage.Scope]), CancellationToken.None);
        Assert.Equal(token, access.Token);
        Assert.Equal(expiry, access.ExpiresOn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_access_token_variable_that_is_not_set_throws(string? value)
    {
        SignInException exception = Throws(SignInKit.Valid("--access-token-env", "PQAX_NAME_OF_A_VARIABLE"), new Dictionary<string, string?> { ["PQAX_NAME_OF_A_VARIABLE"] = value });
        Assert.Contains("--access-token-env", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PQAX_NAME_OF_A_VARIABLE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Env_credential_with_a_secret_gives_a_client_secret_credential()
    {
        var variables = new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = Tenant,
            ["AZURE_CLIENT_ID"] = ClientId,
            ["AZURE_CLIENT_SECRET"] = Secret,
        };
        Assert.IsType<ClientSecretCredential>(Create(SignInKit.Valid("--env-credential"), variables));
    }

    [Fact]
    public void Env_credential_with_a_certificate_gives_a_client_certificate_credential()
    {
        using var plain = new Certificate(null);
        var variables = new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = Tenant,
            ["AZURE_CLIENT_ID"] = ClientId,
            ["AZURE_CLIENT_CERTIFICATE_PATH"] = plain.Path,
        };
        Assert.IsType<ClientCertificateCredential>(Create(SignInKit.Valid("--env-credential"), variables));

        using var protectedCertificate = new Certificate(Secret);
        variables["AZURE_CLIENT_CERTIFICATE_PATH"] = protectedCertificate.Path;
        variables["AZURE_CLIENT_CERTIFICATE_PASSWORD"] = Secret;
        Assert.IsType<ClientCertificateCredential>(Create(SignInKit.Valid("--env-credential"), variables));
    }

    public static TheoryData<string> EnvironmentGaps => new() { "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET" };

    [Theory]
    [MemberData(nameof(EnvironmentGaps))]
    public void Env_credential_without_a_variable_throws_without_the_secret(string missing)
    {
        var variables = new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = Tenant,
            ["AZURE_CLIENT_ID"] = ClientId,
            ["AZURE_CLIENT_SECRET"] = Secret,
        };
        variables[missing] = null;
        SignInException exception = Throws(SignInKit.Valid("--env-credential"), variables);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
        variables[missing] = "";
        exception = Throws(SignInKit.Valid("--env-credential"), variables);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }
}

/// <summary><see cref="AccessTokenCredential"/>: the expiry and the audience of a JWT, and a token that is not one.</summary>
public class AccessTokenCredentialTests
{
    private static readonly TokenRequestContext Context = new([Pqax.Collector.OneLakeStorage.Scope]);

    [Fact]
    public async Task A_jwt_gives_its_expiry_and_its_audience()
    {
        DateTimeOffset expiry = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        string token = SignInKit.Jwt(new { aud = "https://storage.azure.com/", exp = expiry.ToUnixTimeSeconds(), sub = "synthetic" });
        var credential = new AccessTokenCredential(token);
        Assert.Equal("https://storage.azure.com/", credential.Audience);
        AccessToken access = credential.GetToken(Context, CancellationToken.None);
        Assert.Equal(token, access.Token);
        Assert.Equal(expiry, access.ExpiresOn);
        AccessToken again = await credential.GetTokenAsync(Context, CancellationToken.None);
        Assert.Equal(token, again.Token);
        Assert.Equal(expiry, again.ExpiresOn);
    }

    [Fact]
    public void A_jwt_without_exp_never_expires_and_one_without_aud_has_no_audience()
    {
        var credential = new AccessTokenCredential(SignInKit.Jwt(new { sub = "synthetic" }));
        Assert.Null(credential.Audience);
        Assert.Equal(DateTimeOffset.MaxValue, credential.GetToken(Context, CancellationToken.None).ExpiresOn);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    [InlineData("opaque~token_0123456789")]
    public void A_token_that_is_not_a_jwt_never_expires(string token)
    {
        var credential = new AccessTokenCredential(token);
        AccessToken access = credential.GetToken(Context, CancellationToken.None);
        Assert.Equal(token, access.Token);
        Assert.Equal(DateTimeOffset.MaxValue, access.ExpiresOn);
        Assert.Null(credential.Audience);
    }
}
