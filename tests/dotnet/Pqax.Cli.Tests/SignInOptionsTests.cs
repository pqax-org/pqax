namespace Pqax.Cli.Tests;

/// <summary>
/// The identity options of <c>pqax collect</c> through <see cref="CollectOptions.Parse"/>: each identity of the table,
/// the default, the conflicts, the options that would put a secret on the command line, and the names of variables.
/// </summary>
public class SignInOptionsTests
{
    private const string Tenant = SignInKit.Tenant;
    private const string ClientId = SignInKit.ClientId;

    [Fact]
    public void Without_an_identity_option_the_device_code_signs_in()
    {
        SignInOptions options = SignInKit.Valid();
        Assert.Equal(SignInMode.DeviceCode, options.Mode);
        Assert.Null(options.Tenant);
        Assert.Null(options.ClientId);
        Assert.Null(options.ClientSecretVariable);
        Assert.Null(options.ClientCertificatePath);
        Assert.Null(options.ClientCertificatePasswordVariable);
        Assert.Null(options.AccessTokenVariable);
    }

    [Fact]
    public void Device_code_takes_an_optional_tenant()
    {
        Assert.Equal(SignInMode.DeviceCode, SignInKit.Valid("--device-code").Mode);
        SignInOptions options = SignInKit.Valid("--device-code", "--tenant", Tenant);
        Assert.Equal(SignInMode.DeviceCode, options.Mode);
        Assert.Equal(Tenant, options.Tenant);
        Assert.Equal(Tenant, SignInKit.Valid("--tenant", Tenant).Tenant);
        Assert.Equal(SignInMode.DeviceCode, SignInKit.Valid("--tenant", Tenant).Mode);
    }

    [Fact]
    public void Browser_takes_an_optional_tenant()
    {
        Assert.Equal(SignInMode.Browser, SignInKit.Valid("--browser").Mode);
        SignInOptions options = SignInKit.Valid("--browser", "--tenant", Tenant);
        Assert.Equal(SignInMode.Browser, options.Mode);
        Assert.Equal(Tenant, options.Tenant);
    }

    [Fact]
    public void A_client_secret_takes_the_name_of_its_variable()
    {
        SignInOptions options = SignInKit.Valid("--client-id", ClientId, "--tenant", Tenant, "--client-secret-env", "PQAX_SECRET");
        Assert.Equal(SignInMode.ClientSecret, options.Mode);
        Assert.Equal(ClientId, options.ClientId);
        Assert.Equal(Tenant, options.Tenant);
        Assert.Equal("PQAX_SECRET", options.ClientSecretVariable);
        Assert.Null(options.AccessTokenVariable);
    }

    [Fact]
    public void A_client_certificate_takes_a_path_and_an_optional_password_variable()
    {
        SignInOptions options = SignInKit.Valid("--client-id", ClientId, "--tenant", Tenant, "--client-certificate", "cert.pfx");
        Assert.Equal(SignInMode.ClientCertificate, options.Mode);
        Assert.Equal(ClientId, options.ClientId);
        Assert.Equal(Tenant, options.Tenant);
        Assert.Equal("cert.pfx", options.ClientCertificatePath);
        Assert.Null(options.ClientCertificatePasswordVariable);

        options = SignInKit.Valid("--client-certificate", "cert.pfx", "--client-certificate-password-env", "_PQAX_PFX_1", "--client-id", ClientId, "--tenant", Tenant);
        Assert.Equal(SignInMode.ClientCertificate, options.Mode);
        Assert.Equal("_PQAX_PFX_1", options.ClientCertificatePasswordVariable);
    }

    [Fact]
    public void A_managed_identity_is_system_assigned_or_user_assigned()
    {
        SignInOptions system = SignInKit.Valid("--managed-identity");
        Assert.Equal(SignInMode.ManagedIdentity, system.Mode);
        Assert.Null(system.ClientId);
        SignInOptions user = SignInKit.Valid("--managed-identity", "--client-id", ClientId);
        Assert.Equal(SignInMode.ManagedIdentity, user.Mode);
        Assert.Equal(ClientId, user.ClientId);
    }

    [Fact]
    public void Azure_cli_takes_an_optional_tenant()
    {
        Assert.Equal(SignInMode.AzureCli, SignInKit.Valid("--azure-cli").Mode);
        SignInOptions options = SignInKit.Valid("--azure-cli", "--tenant", Tenant);
        Assert.Equal(SignInMode.AzureCli, options.Mode);
        Assert.Equal(Tenant, options.Tenant);
    }

    [Fact]
    public void An_access_token_takes_the_name_of_its_variable()
    {
        SignInOptions options = SignInKit.Valid("--access-token-env", "PQAX_TOKEN");
        Assert.Equal(SignInMode.AccessToken, options.Mode);
        Assert.Equal("PQAX_TOKEN", options.AccessTokenVariable);
    }

    [Fact]
    public void Env_credential_reads_the_azure_variables()
    {
        Assert.Equal(SignInMode.Environment, SignInKit.Valid("--env-credential").Mode);
    }

    public static TheoryData<string[]> TwoIdentities => new()
    {
        new[] { "--device-code", "--browser" },
        new[] { "--browser", "--azure-cli" },
        new[] { "--azure-cli", "--managed-identity" },
        new[] { "--managed-identity", "--access-token-env", "PQAX_TOKEN" },
        new[] { "--access-token-env", "PQAX_TOKEN", "--env-credential" },
        new[] { "--env-credential", "--device-code" },
        new[] { "--device-code", "--access-token-env", "PQAX_TOKEN" },
        new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-secret-env", "PQAX_SECRET", "--client-certificate", "cert.pfx" },
        new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-secret-env", "PQAX_SECRET", "--browser" },
        new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-certificate", "cert.pfx", "--managed-identity" },
        new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-secret-env", "PQAX_SECRET", "--access-token-env", "PQAX_TOKEN" },
    };

    [Theory]
    [MemberData(nameof(TwoIdentities))]
    public void Two_identities_are_an_error(string[] identity)
    {
        SignInKit.Refused(SignInKit.Workspace(identity));
    }

    public static TheoryData<string[]> MissingParts => new()
    {
        new[] { "--client-id", ClientId },
        new[] { "--client-id", ClientId, "--tenant", Tenant },
        new[] { "--client-id", ClientId, "--browser" },
        new[] { "--client-id", ClientId, "--device-code" },
        new[] { "--client-id", ClientId, "--azure-cli" },
        new[] { "--client-id", ClientId, "--access-token-env", "PQAX_TOKEN" },
        new[] { "--client-secret-env", "PQAX_SECRET" },
        new[] { "--client-secret-env", "PQAX_SECRET", "--client-id", ClientId },
        new[] { "--client-secret-env", "PQAX_SECRET", "--tenant", Tenant },
        new[] { "--client-certificate", "cert.pfx" },
        new[] { "--client-certificate", "cert.pfx", "--client-id", ClientId },
        new[] { "--client-certificate", "cert.pfx", "--tenant", Tenant },
        new[] { "--client-certificate-password-env", "PQAX_PFX" },
        new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-secret-env", "PQAX_SECRET", "--client-certificate-password-env", "PQAX_PFX" },
        new[] { "--env-credential", "--tenant", Tenant },
        new[] { "--env-credential", "--client-id", ClientId },
    };

    [Theory]
    [MemberData(nameof(MissingParts))]
    public void An_identity_without_what_it_needs_is_an_error(string[] identity)
    {
        SignInKit.Refused(SignInKit.Workspace(identity));
    }

    public static TheoryData<string[]> IdentityOptions => new()
    {
        new[] { "--device-code" },
        new[] { "--browser" },
        new[] { "--tenant", Tenant },
        new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-secret-env", "PQAX_SECRET" },
        new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-certificate", "cert.pfx" },
        new[] { "--managed-identity" },
        new[] { "--azure-cli" },
        new[] { "--access-token-env", "PQAX_TOKEN" },
        new[] { "--env-credential" },
    };

    [Theory]
    [MemberData(nameof(IdentityOptions))]
    public void Identity_options_with_local_are_an_error(string[] identity)
    {
        string[] arguments = ["collect", "--local", Path.Combine(CliKit.Root, "conformance", "fixtures", "lakehouse"), "--output", "x.pqax", .. identity];
        SignInKit.Refused(arguments);
    }

    [Fact]
    public void Local_without_identity_options_is_still_valid()
    {
        CollectOptions? options = CollectOptions.Parse(["collect", "--local", Path.Combine(CliKit.Root, "conformance", "fixtures", "lakehouse"), "--output", "x.pqax"], out string? problem);
        Assert.True(options != null, problem);
        Assert.NotNull(options!.Local);
        Assert.Null(options.Workspace);
    }

    [Fact]
    public void The_options_of_a_workspace_are_read()
    {
        CollectOptions? options = CollectOptions.Parse(["collect", "--workspace", "Test Workspace", "--item", "Item.Lakehouse", "--output", "x.pqax", "--verbose"], out string? problem);
        Assert.True(options != null, problem);
        Assert.Equal("Test Workspace", options!.Workspace);
        Assert.Equal(["Item.Lakehouse"], options.Items);
        Assert.Equal("x.pqax", options.Output);
        Assert.True(options.Verbose);
        Assert.Null(options.Local);
        Assert.Equal(SignInMode.DeviceCode, options.SignIn.Mode);
    }

    private const string Value = "Sup3r-S3cret~Value.42";

    public static TheoryData<string[], string> SecretsOnTheCommandLine => new()
    {
        { new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-secret", Value }, "--client-secret-env" },
        { new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-secret=" + Value }, "--client-secret-env" },
        { new[] { "--access-token", Value }, "--access-token-env" },
        { new[] { "--access-token=" + Value }, "--access-token-env" },
        { new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-certificate", "cert.pfx", "--client-certificate-password", Value }, "--client-certificate-password-env" },
        { new[] { "--client-id", ClientId, "--tenant", Tenant, "--client-certificate", "cert.pfx", "--client-certificate-password=" + Value }, "--client-certificate-password-env" },
    };

    [Theory]
    [MemberData(nameof(SecretsOnTheCommandLine))]
    public void A_secret_on_the_command_line_is_refused_without_echoing_it(string[] identity, string environmentOption)
    {
        string problem = SignInKit.Refused(SignInKit.Workspace(identity));
        Assert.DoesNotContain(Value, problem, StringComparison.Ordinal);
        Assert.DoesNotContain("S3cret", problem, StringComparison.Ordinal);
        Assert.Contains("environment variable", problem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(environmentOption, problem, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> BadNames => new()
    {
        { "--client-secret-env", "1PQAX_SECRET" },
        { "--client-secret-env", "PQAX-SECRET" },
        { "--client-secret-env", "Sup3r S3cret" },
        { "--client-secret-env", "S3cret!Value" },
        { "--client-secret-env", "" },
        { "--access-token-env", "eyJ0.S3cret.Token" },
        { "--access-token-env", "S3cret=Value" },
        { "--client-certificate-password-env", "S3cret$Value" },
    };

    [Theory]
    [MemberData(nameof(BadNames))]
    public void A_name_that_is_not_a_variable_name_is_refused_without_echoing_it(string option, string name)
    {
        string[] identity = option switch
        {
            "--client-secret-env" => ["--client-id", ClientId, "--tenant", Tenant, option, name],
            "--client-certificate-password-env" => ["--client-id", ClientId, "--tenant", Tenant, "--client-certificate", "cert.pfx", option, name],
            _ => [option, name],
        };
        string problem = SignInKit.Refused(SignInKit.Workspace(identity));
        Assert.DoesNotContain("S3cret", problem, StringComparison.Ordinal);
        if (name.Length > 0)
        {
            // An empty value may be refused as a missing value instead.
            Assert.DoesNotContain(name, problem, StringComparison.Ordinal);
            Assert.Contains("environment variable", problem, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("PQAX_SECRET")]
    [InlineData("_x")]
    [InlineData("a1_B2")]
    public void A_variable_name_of_letters_digits_and_underscores_is_accepted(string name)
    {
        Assert.Equal(name, SignInKit.Valid("--access-token-env", name).AccessTokenVariable);
        Assert.Equal(name, SignInKit.Valid("--client-id", ClientId, "--tenant", Tenant, "--client-secret-env", name).ClientSecretVariable);
    }

    [Fact]
    public void An_unknown_option_with_a_value_is_echoed_up_to_the_equals_sign()
    {
        string problem = SignInKit.Refused(SignInKit.Workspace("--no-such-option=" + Value));
        Assert.Contains("--no-such-option", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(Value, problem, StringComparison.Ordinal);
        Assert.DoesNotContain("S3cret", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_command_line_exits_with_2_and_never_prints_the_secret()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = CollectCommand.Run(SignInKit.Workspace("--client-id", ClientId, "--tenant", Tenant, "--client-secret=" + Value), output, error);
        Assert.Equal(2, exit);
        Assert.DoesNotContain(Value, output.ToString() + error.ToString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(error.ToString()));
    }
}
