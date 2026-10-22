namespace Pqax.Cli;

/// <summary>How <c>pqax collect</c> signs in to OneLake.</summary>
public enum SignInMode
{
    /// <summary>A person, with a device code that the command prints. The default.</summary>
    DeviceCode,

    /// <summary>A person, in the browser.</summary>
    Browser,

    /// <summary>A service principal with a client secret read from an environment variable.</summary>
    ClientSecret,

    /// <summary>A service principal with a certificate file.</summary>
    ClientCertificate,

    /// <summary>The managed identity of the machine: system-assigned, or user-assigned with its client id.</summary>
    ManagedIdentity,

    /// <summary>The account that the Azure CLI signed in with <c>az login</c>.</summary>
    AzureCli,

    /// <summary>An access token acquired before, read from an environment variable.</summary>
    AccessToken,

    /// <summary>A service principal from the variables of the Azure SDK: <c>AZURE_TENANT_ID</c>, <c>AZURE_CLIENT_ID</c>, and a secret or a certificate.</summary>
    Environment,
}

/// <summary>
/// The identity that <c>pqax collect</c> signs in with, as the command line gives it. It holds the names of the
/// environment variables that hold secrets, never a secret.
/// </summary>
public sealed class SignInOptions
{
    internal SignInOptions(SignInMode mode, string? tenant, string? clientId, string? clientSecretVariable, string? clientCertificatePath, string? clientCertificatePasswordVariable, string? accessTokenVariable)
    {
        Mode = mode;
        Tenant = tenant;
        ClientId = clientId;
        ClientSecretVariable = clientSecretVariable;
        ClientCertificatePath = clientCertificatePath;
        ClientCertificatePasswordVariable = clientCertificatePasswordVariable;
        AccessTokenVariable = accessTokenVariable;
    }

    /// <summary>The kind of identity.</summary>
    public SignInMode Mode { get; }

    /// <summary>The tenant, from <c>--tenant</c>: an id or a domain. Null for the default of the identity.</summary>
    public string? Tenant { get; }

    /// <summary>The client id of the application or of the user-assigned managed identity, from <c>--client-id</c>.</summary>
    public string? ClientId { get; }

    /// <summary>The name of the environment variable that holds the client secret, from <c>--client-secret-env</c>.</summary>
    public string? ClientSecretVariable { get; }

    /// <summary>The path of the certificate file, PFX or PEM, from <c>--client-certificate</c>.</summary>
    public string? ClientCertificatePath { get; }

    /// <summary>The name of the environment variable that holds the password of the certificate, from <c>--client-certificate-password-env</c>.</summary>
    public string? ClientCertificatePasswordVariable { get; }

    /// <summary>The name of the environment variable that holds the access token, from <c>--access-token-env</c>.</summary>
    public string? AccessTokenVariable { get; }
}
