using Azure.Identity;
using Pqax.Collector;

namespace Pqax.Cli.Tests;

/// <summary>
/// <see cref="SignIn.Describe"/>: one line for each error of the sign-in and of OneLake, with the code and the likely
/// cause, and <see cref="SignIn.DescribeIdentity"/>, the line of --verbose, without a secret.
/// </summary>
public class SignInErrorTests
{
    /// <summary>A message as Azure.Identity writes it: several lines, with a trace id.</summary>
    private static AuthenticationFailedException Failed(string code) => new(
        $"ClientSecretCredential authentication failed: {code}: Synthetic description of the error.\r\nTrace ID: 00000000-0000-0000-0000-000000000001\r\nCorrelation ID: 00000000-0000-0000-0000-000000000002\r\nTimestamp: 2026-10-22 08:00:00Z");

    private static string Line(Exception exception)
    {
        string line = SignIn.Describe(exception);
        Assert.False(string.IsNullOrWhiteSpace(line));
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.False(line.StartsWith("pqax collect:", StringComparison.Ordinal), line);
        return line;
    }

    private static void ContainsAll(string line, params string[] words)
    {
        foreach (string word in words)
        {
            Assert.True(line.Contains(word, StringComparison.OrdinalIgnoreCase), $"\"{word}\" is not in: {line}");
        }
    }

    [Theory]
    [InlineData("AADSTS7000215")]
    [InlineData("AADSTS7000222")]
    public void A_wrong_or_expired_secret_is_named(string code)
    {
        string line = Line(Failed(code));
        ContainsAll(line, code, "secret");
        Assert.True(line.Contains("wrong", StringComparison.OrdinalIgnoreCase) || line.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || line.Contains("invalid", StringComparison.OrdinalIgnoreCase), line);
    }

    [Fact]
    public void A_client_id_that_is_not_an_application_of_the_tenant_is_named()
    {
        string line = Line(Failed("AADSTS700016"));
        ContainsAll(line, "AADSTS700016", "tenant");
        Assert.True(line.Contains("application", StringComparison.OrdinalIgnoreCase) || line.Contains("client id", StringComparison.OrdinalIgnoreCase), line);
    }

    [Theory]
    [InlineData("AADSTS90002")]
    [InlineData("AADSTS900023")]
    public void A_tenant_that_is_not_found_is_named(string code)
    {
        ContainsAll(Line(Failed(code)), code, "tenant", "not found");
    }

    [Theory]
    [InlineData("AADSTS700027")]
    [InlineData("AADSTS700024")]
    public void A_certificate_that_is_not_the_registered_one_is_named(string code)
    {
        ContainsAll(Line(Failed(code)), code, "certificate");
    }

    [Theory]
    [InlineData("AADSTS50034")]
    [InlineData("AADSTS53003")]
    public void Another_code_is_given(string code)
    {
        ContainsAll(Line(Failed(code)), code);
    }

    [Fact]
    public void An_identity_that_is_not_available_is_named_with_the_message()
    {
        var exception = new CredentialUnavailableException("ManagedIdentityCredential authentication unavailable. No managed identity endpoint found.\r\nSee the troubleshooting guide.");
        string line = Line(exception);
        ContainsAll(line, "ManagedIdentityCredential authentication unavailable");
        Assert.True(line.Contains("available", StringComparison.OrdinalIgnoreCase), line);
    }

    [Fact]
    public void A_401_names_the_tenant_setting()
    {
        string line = Line(new StorageException("OneLake answered 401 Unauthorized.", 401));
        ContainsAll(line, "401", "tenant");
    }

    [Fact]
    public void A_403_names_the_workspace_role()
    {
        string line = Line(new StorageException("OneLake answered 403 Forbidden.", 403));
        ContainsAll(line, "403", "workspace", "Contributor", "Viewer");
    }

    [Fact]
    public void A_wrapped_error_keeps_its_code()
    {
        string line = Line(new AggregateException(new StorageException("OneLake answered 403 Forbidden.", 403)));
        Assert.Contains("403", line, StringComparison.Ordinal);
    }

    private const string Secret = "Synthetic~Secret.Value-0123456789";

    [Fact]
    public void The_verbose_line_of_a_client_secret_names_the_identity_and_not_the_secret()
    {
        SignInOptions options = SignInKit.Valid("--client-id", SignInKit.ClientId, "--tenant", SignInKit.Tenant, "--client-secret-env", "PQAX_NAME_OF_A_VARIABLE");
        string line = SignIn.DescribeIdentity(options);
        Assert.DoesNotContain('\n', line);
        ContainsAll(line, SignInKit.ClientId, SignInKit.Tenant, "--client-secret-env");
        Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--device-code")]
    [InlineData("--browser")]
    [InlineData("--managed-identity")]
    [InlineData("--azure-cli")]
    [InlineData("--env-credential")]
    public void Every_identity_has_a_verbose_line(string option)
    {
        string line = SignIn.DescribeIdentity(SignInKit.Valid(option));
        Assert.False(string.IsNullOrWhiteSpace(line));
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void The_verbose_line_of_an_access_token_names_the_option()
    {
        string line = SignIn.DescribeIdentity(SignInKit.Valid("--access-token-env", "PQAX_TEST_TOKEN"));
        Assert.Contains("--access-token-env", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }
}
