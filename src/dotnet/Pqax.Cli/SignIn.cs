using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Identity;
using Pqax.Collector;

namespace Pqax.Cli;

/// <summary>
/// The identities of <c>pqax collect</c>: builds the credential of Azure.Identity for an identity, and turns a failed
/// sign-in or a refused request into one line with its likely cause. No method writes or returns a secret.
/// </summary>
public static class SignIn
{
    /// <summary>The variable of the Azure SDK with the tenant.</summary>
    public const string TenantVariable = "AZURE_TENANT_ID";

    /// <summary>The variable of the Azure SDK with the client id.</summary>
    public const string ClientIdVariable = "AZURE_CLIENT_ID";

    /// <summary>The variable of the Azure SDK with the client secret.</summary>
    public const string ClientSecretVariable = "AZURE_CLIENT_SECRET";

    /// <summary>The variable of the Azure SDK with the path of the certificate.</summary>
    public const string ClientCertificatePathVariable = "AZURE_CLIENT_CERTIFICATE_PATH";

    /// <summary>The variable of the Azure SDK with the password of the certificate.</summary>
    public const string ClientCertificatePasswordVariable = "AZURE_CLIENT_CERTIFICATE_PASSWORD";

    private static readonly Regex AadCode = new(@"AADSTS\d+", RegexOptions.CultureInvariant);

    /// <summary>Builds the credential of an identity. The secrets are read from the environment here, and nowhere else.</summary>
    /// <param name="options">The identity.</param>
    /// <param name="getVariable">Reads an environment variable.</param>
    /// <param name="deviceCodeOutput">Where the message of a device code goes.</param>
    /// <returns>A credential of Azure.Identity, or an <see cref="AccessTokenCredential"/>.</returns>
    /// <exception cref="SignInException">A variable is not set or is empty, or the certificate file does not exist.</exception>
    public static TokenCredential CreateCredential(SignInOptions options, Func<string, string?> getVariable, TextWriter deviceCodeOutput)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getVariable);
        ArgumentNullException.ThrowIfNull(deviceCodeOutput);
        switch (options.Mode)
        {
            case SignInMode.Browser:
                return new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions { TenantId = options.Tenant });
            case SignInMode.ClientSecret:
                return new ClientSecretCredential(options.Tenant, options.ClientId, Required(getVariable, options.ClientSecretVariable!, "--client-secret-env"));
            case SignInMode.ClientCertificate:
                {
                    string? password = options.ClientCertificatePasswordVariable == null
                        ? null
                        : Required(getVariable, options.ClientCertificatePasswordVariable, "--client-certificate-password-env");
                    return Certificate(options.Tenant!, options.ClientId!, options.ClientCertificatePath!, password, "--client-certificate");
                }

            case SignInMode.ManagedIdentity:
                return new ManagedIdentityCredential(options.ClientId == null ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(options.ClientId));
            case SignInMode.AzureCli:
                return new AzureCliCredential(new AzureCliCredentialOptions { TenantId = options.Tenant });
            case SignInMode.AccessToken:
                return new AccessTokenCredential(Required(getVariable, options.AccessTokenVariable!, "--access-token-env"));
            case SignInMode.Environment:
                {
                    string tenant = Standard(getVariable, TenantVariable);
                    string clientId = Standard(getVariable, ClientIdVariable);
                    string? secret = getVariable(ClientSecretVariable);
                    if (!string.IsNullOrEmpty(secret))
                    {
                        return new ClientSecretCredential(tenant, clientId, secret);
                    }

                    string? path = getVariable(ClientCertificatePathVariable);
                    if (string.IsNullOrEmpty(path))
                    {
                        throw new SignInException($"--env-credential needs {ClientSecretVariable} or {ClientCertificatePathVariable}, and neither is set.");
                    }

                    string? password = getVariable(ClientCertificatePasswordVariable);
                    return Certificate(tenant, clientId, path, string.IsNullOrEmpty(password) ? null : password, ClientCertificatePathVariable);
                }

            default:
                // The device code is the default: a browser window can open where nobody sees it, and the code is printed here.
                return new DeviceCodeCredential(new DeviceCodeCredentialOptions
                {
                    TenantId = options.Tenant,
                    DeviceCodeCallback = (code, _) =>
                    {
                        deviceCodeOutput.WriteLine(code.Message);
                        return Task.CompletedTask;
                    },
                });
        }
    }

    /// <summary>The line that <c>--verbose</c> prints about the identity: its kind and its ids, never a secret or the name of a variable that holds one.</summary>
    /// <param name="options">The identity.</param>
    /// <returns>One line.</returns>
    public static string DescribeIdentity(SignInOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string tenant = options.Tenant != null ? $", tenant {options.Tenant}" : string.Empty;
        return options.Mode switch
        {
            SignInMode.Browser => $"Sign-in: a person, in the browser{tenant}.",
            SignInMode.ClientSecret => $"Sign-in: the service principal {options.ClientId}{tenant}, with the client secret of --client-secret-env.",
            SignInMode.ClientCertificate => $"Sign-in: the service principal {options.ClientId}{tenant}, with the certificate {Path.GetFileName(options.ClientCertificatePath)}"
                + (options.ClientCertificatePasswordVariable != null ? " and the password of --client-certificate-password-env." : "."),
            SignInMode.ManagedIdentity => options.ClientId == null ? "Sign-in: the system-assigned managed identity." : $"Sign-in: the user-assigned managed identity {options.ClientId}.",
            SignInMode.AzureCli => $"Sign-in: the account of the Azure CLI{tenant}.",
            SignInMode.AccessToken => "Sign-in: none; the access token of --access-token-env.",
            SignInMode.Environment => $"Sign-in: the service principal of {ClientIdVariable} and {TenantVariable}, with {ClientSecretVariable} or {ClientCertificatePathVariable}.",
            _ => $"Sign-in: a person, with a device code{tenant}.",
        };
    }

    /// <summary>
    /// One line for a failed sign-in or a request that OneLake refused, with its code and its likely cause: an
    /// <c>AADSTS</c> code of Microsoft Entra ID, an identity that is not available here, or a status 401 or 403 of OneLake.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns>The line, without the name of the command and without a line break.</returns>
    public static string Describe(Exception exception) => Describe(exception, storageAccount: false);

    /// <summary>
    /// One line for a failed sign-in or a request that the storage refused, with its code and its likely cause. For a
    /// storage account, a status 401 or 403 names the token and the data role of Azure Storage instead of Fabric.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="storageAccount">Whether the storage is a container of a storage account, from <c>--adls</c>, rather than OneLake.</param>
    /// <returns>The line, without the name of the command and without a line break.</returns>
    public static string Describe(Exception exception, bool storageAccount)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string line = exception switch
        {
            SignInException e => e.Message,
            StorageException { StatusCode: 401 } e when storageAccount => $"{Stem(e.Message)}: the storage account refused the token. An access token must be for https://storage.azure.com, from the tenant of the account, and not expired.",
            StorageException { StatusCode: 403 } e when storageAccount => $"{Stem(e.Message)}: the identity cannot read the container. It needs the role Storage Blob Data Reader on the container or the account, or ACLs that give it read and execute on the folders; Reader, Contributor, or Owner of the account alone do not read data. A network rule of the account also answers 403.",
            CredentialUnavailableException e => $"the identity is not available here: {Text(e)} For --managed-identity, the command must run where Azure gives the identity; for --azure-cli, run az login first.",
            AuthenticationFailedException e => Aad(e),
            StorageException { StatusCode: 401 } e => $"{Stem(e.Message)}: OneLake refused the token. For a service principal or a managed identity, the Fabric tenant setting \"Service principals can use Fabric APIs\" must be enabled for it, or for a security group it is in. An access token must be for https://storage.azure.com and not expired.",
            StorageException { StatusCode: 403 } e => $"{Stem(e.Message)}: the identity has no access to the workspace. It needs a workspace role that reads OneLake: Contributor, Member, or Admin. Viewer alone does not.",
            RequestFailedException e => $"the sign-in failed with status {e.Status}: {Text(e)}",
            CryptographicException e => $"the certificate could not be read: {Text(e)}",
            _ => $"{exception.GetType().Name}: {Text(exception)}",
        };
        return line.Replace("\r", " ").Replace("\n", " ");
    }

    /// <summary>The value of a variable named by an option. The message names the option, never the variable or the value.</summary>
    private static string Required(Func<string, string?> getVariable, string name, string option)
    {
        string? value = getVariable(name);
        return string.IsNullOrEmpty(value)
            ? throw new SignInException($"the environment variable named by {option} is not set, or is empty.")
            : value;
    }

    /// <summary>The value of a variable of the Azure SDK, whose name is not a secret.</summary>
    private static string Standard(Func<string, string?> getVariable, string name)
    {
        string? value = getVariable(name);
        return string.IsNullOrEmpty(value) ? throw new SignInException($"--env-credential needs {name}, which is not set or is empty.") : value;
    }

    private static ClientCertificateCredential Certificate(string tenant, string clientId, string path, string? password, string source)
    {
        if (!File.Exists(path))
        {
            throw new SignInException($"the certificate file of {source} does not exist.");
        }

        if (password == null)
        {
            // A PFX without a password or a PEM with its private key: Azure.Identity reads both from the path.
            return new ClientCertificateCredential(tenant, clientId, path);
        }

#if NET9_0_OR_GREATER
        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
#else
        var certificate = new X509Certificate2(path, password);
#endif
        return new ClientCertificateCredential(tenant, clientId, certificate);
    }

    /// <summary>A failed sign-in of Microsoft Entra ID, by its <c>AADSTS</c> code.</summary>
    private static string Aad(AuthenticationFailedException exception)
    {
        string messages = string.Join(" ", Chain(exception).Select(e => e.Message));
        Match match = AadCode.Match(messages);
        if (!match.Success)
        {
            return $"the sign-in failed: {Text(exception)}";
        }

        string code = match.Value;
        string cause = code switch
        {
            "AADSTS7000215" or "AADSTS7000222" => "the client secret is wrong or has expired. Create a new secret for the application and put it in the variable.",
            "AADSTS700016" => "the client id is not an application of the tenant. Check --client-id and --tenant.",
            "AADSTS7000229" => "the application has no service principal in the tenant.",
            "AADSTS90002" or "AADSTS900023" => "the tenant is not found. Check --tenant.",
            "AADSTS700027" or "AADSTS700024" => "the certificate is not one that the application has registered, or the clock of this machine is wrong.",
            "AADSTS53003" => "a Conditional Access policy blocked the sign-in.",
            _ => $"see https://login.microsoftonline.com/error?code={code.Substring(6)}.",
        };
        return $"the sign-in was refused ({code}): {cause}";
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? e = exception; e != null; e = e.InnerException)
        {
            yield return e;
        }
    }

    /// <summary>The first line of the message.</summary>
    private static string Text(Exception exception)
    {
        string message = exception.Message.Trim();
        int end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message.Substring(0, end).TrimEnd();
    }

    /// <summary>A message of the storage without its final period, such as <c>OneLake answered 403 Forbidden</c>.</summary>
    private static string Stem(string message) => message.TrimEnd().TrimEnd('.');
}
