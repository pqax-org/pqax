using System.Globalization;
using System.Text.RegularExpressions;
using Pqax.Collector;

namespace Pqax.Cli;

/// <summary>The arguments of <c>collect</c>, parsed by hand.</summary>
public sealed class CollectOptions
{
    /// <summary>The options that would carry a secret as their value. They are refused, and the value is never echoed.</summary>
    private static readonly string[] SecretOptions = ["--client-secret", "--access-token", "--client-certificate-password"];

    private static readonly Regex VariableName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The name of a filesystem (container) of a storage account: 3 to 63 lowercase letters, digits, and single dashes. It ends
    /// with <c>\z</c>, since <c>$</c> also matches before a final newline.
    /// </summary>
    private static readonly Regex FileSystemName = new(@"^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]\z", RegexOptions.CultureInvariant);

    private readonly List<string> items = [];

    private CollectOptions()
    {
        SignIn = new SignInOptions(SignInMode.DeviceCode, null, null, null, null, null, null);
    }

    /// <summary>The folder with the OneLake layout, from <c>--local</c>.</summary>
    public string? Local { get; private set; }

    /// <summary>The name or the id of the workspace, from <c>--workspace</c>.</summary>
    public string? Workspace { get; private set; }

    /// <summary>The URL of a folder of an Azure Data Lake Storage Gen2 container, from <c>--adls</c>.</summary>
    public string? Adls { get; private set; }

    /// <summary>The DFS endpoint of the storage account of <c>--adls</c>, such as <c>https://account.dfs.core.windows.net</c>.</summary>
    public Uri? AdlsEndpoint { get; private set; }

    /// <summary>The filesystem (container) of <c>--adls</c>.</summary>
    public string? AdlsFileSystem { get; private set; }

    /// <summary>The folder of <c>--adls</c> in its container, decoded, without a leading or trailing slash. Empty for the container itself.</summary>
    public string AdlsFolder { get; private set; } = string.Empty;

    /// <summary>The items, from <c>--item</c>, in the order given.</summary>
    public IReadOnlyList<string> Items => items;

    /// <summary>The file to write, from <c>--output</c>.</summary>
    public string? Output { get; private set; }

    /// <summary>Whether to print the identity and every OneLake request to the error output, from <c>--verbose</c>.</summary>
    public bool Verbose { get; private set; }

    /// <summary>The identity that signs in to OneLake or to the storage account.</summary>
    public SignInOptions SignIn { get; private set; }

    /// <summary>The detail kept for each table, from <c>--depth</c>.</summary>
    public DetailDepth Depth { get; private set; } = DetailDepth.ColumnChunks;

    /// <summary>The collection level, from <c>--level</c>.</summary>
    public int Level { get; private set; } = 2;

    /// <summary>The footers read for each table, 0 for all, from <c>--max-footers</c>.</summary>
    public int MaxFooters { get; private set; }

    /// <summary>The commit files read for the history of each table, from <c>--max-commits</c>.</summary>
    public int MaxCommits { get; private set; } = 30;

    /// <summary>The storage requests at the same time, from <c>--parallelism</c>.</summary>
    public int Parallelism { get; private set; } = 16;

    /// <summary>Parses the arguments of <c>collect</c>. A problem never quotes the value of an option that names a variable or that carries a secret.</summary>
    /// <param name="args">All the arguments, <c>collect</c> first.</param>
    /// <param name="problem">What is wrong, when the arguments cannot be parsed.</param>
    /// <returns>The options, or null when the arguments are wrong.</returns>
    public static CollectOptions? Parse(string[] args, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new CollectOptions();
        problem = null;
        string? identity = null;
        SignInMode mode = SignInMode.DeviceCode;
        string? tenant = null;
        string? clientId = null;
        string? secretVariable = null;
        string? certificate = null;
        string? passwordVariable = null;
        string? tokenVariable = null;
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            string? Next()
            {
                return i + 1 < args.Length && args[i + 1].Length > 0 && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            }

            // The identity options: one at a time. False when another one was given.
            bool Choose(SignInMode chosen)
            {
                if (identity != null && identity != arg)
                {
                    return false;
                }

                identity = arg;
                mode = chosen;
                return true;
            }

            string? value;
            switch (arg)
            {
                case "--local":
                    options.Local = value = Next();
                    break;
                case "--workspace":
                    options.Workspace = value = Next();
                    break;
                case "--adls":
                    options.Adls = value = Next();
                    break;
                case "--item":
                    value = Next();
                    if (value != null)
                    {
                        options.items.Add(value);
                    }

                    break;
                case "--output":
                    options.Output = value = Next();
                    break;
                case "--tenant":
                    tenant = value = Next();
                    break;
                case "--client-id":
                    clientId = value = Next();
                    break;
                case "--device-code":
                case "--browser":
                case "--managed-identity":
                case "--azure-cli":
                case "--env-credential":
                    if (!Choose(arg switch
                    {
                        "--device-code" => SignInMode.DeviceCode,
                        "--browser" => SignInMode.Browser,
                        "--managed-identity" => SignInMode.ManagedIdentity,
                        "--azure-cli" => SignInMode.AzureCli,
                        _ => SignInMode.Environment,
                    }))
                    {
                        problem = TwoIdentities(identity, arg);
                        return null;
                    }

                    value = string.Empty;
                    break;
                case "--client-secret-env":
                case "--access-token-env":
                    if (!Choose(arg == "--client-secret-env" ? SignInMode.ClientSecret : SignInMode.AccessToken))
                    {
                        problem = TwoIdentities(identity, arg);
                        return null;
                    }

                    value = Next();
                    if (value != null && !VariableName.IsMatch(value))
                    {
                        problem = NotAName(arg);
                        return null;
                    }

                    if (arg == "--client-secret-env")
                    {
                        secretVariable = value;
                    }
                    else
                    {
                        tokenVariable = value;
                    }

                    break;
                case "--client-certificate":
                    if (!Choose(SignInMode.ClientCertificate))
                    {
                        problem = TwoIdentities(identity, arg);
                        return null;
                    }

                    certificate = value = Next();
                    break;
                case "--client-certificate-password-env":
                    passwordVariable = value = Next();
                    if (value != null && !VariableName.IsMatch(value))
                    {
                        problem = NotAName(arg);
                        return null;
                    }

                    break;
                case "--verbose":
                    options.Verbose = true;
                    value = string.Empty;
                    break;
                case "--depth":
                    value = Next();
                    if (value != null)
                    {
                        DetailDepth? depth = value.ToLowerInvariant() switch
                        {
                            "summary" => DetailDepth.Summary,
                            "files" => DetailDepth.Files,
                            "rowgroups" => DetailDepth.RowGroups,
                            "columnchunks" => DetailDepth.ColumnChunks,
                            _ => null,
                        };
                        if (depth == null)
                        {
                            problem = $"unknown depth '{value}'; use summary, files, rowGroups, or columnChunks.";
                            return null;
                        }

                        options.Depth = depth.Value;
                    }

                    break;
                case "--level":
                    value = Next();
                    if (value != null)
                    {
                        if (value != "1" && value != "2")
                        {
                            problem = "--level is 1 or 2.";
                            return null;
                        }

                        options.Level = value == "1" ? 1 : 2;
                    }

                    break;
                case "--max-footers":
                case "--max-commits":
                case "--parallelism":
                    value = Next();
                    if (value != null)
                    {
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || (arg == "--parallelism" && number < 1))
                        {
                            problem = $"{arg} needs a number{(arg == "--parallelism" ? " of at least 1" : string.Empty)}.";
                            return null;
                        }

                        if (arg == "--max-footers")
                        {
                            options.MaxFooters = number;
                        }
                        else if (arg == "--max-commits")
                        {
                            options.MaxCommits = number;
                        }
                        else
                        {
                            options.Parallelism = number;
                        }
                    }

                    break;
                default:
                    {
                        // Never echo what follows an equals sign: it can be a secret.
                        int equals = arg.IndexOf('=', StringComparison.Ordinal);
                        string name = equals < 0 ? arg : arg.Substring(0, equals);
                        problem = Array.IndexOf(SecretOptions, name) >= 0
                            ? $"{name} is not accepted: a secret on the command line stays in the shell history and in the list of processes. Put it in an environment variable and give the name of the variable with {name}-env."
                            : $"unknown option '{name}'.";
                        return null;
                    }
            }

            if (value == null)
            {
                problem = $"{arg} needs a value.";
                return null;
            }
        }

        if ((options.Local != null ? 1 : 0) + (options.Workspace != null ? 1 : 0) + (options.Adls != null ? 1 : 0) != 1)
        {
            problem = "give one of --local, --workspace, or --adls.";
            return null;
        }

        if (options.Adls != null)
        {
            if (options.Items.Count > 0)
            {
                problem = "--item does not apply to --adls: the tables are found under the folder of the URL.";
                return null;
            }

            problem = options.ParseAdls();
            if (problem != null)
            {
                return null;
            }
        }

        if (options.Workspace != null && options.Items.Count == 0)
        {
            problem = "--workspace needs at least one --item.";
            return null;
        }

        if (options.Output == null)
        {
            problem = "--output is missing.";
            return null;
        }

        if ((identity != null || tenant != null || clientId != null || passwordVariable != null) && options.Workspace == null && options.Adls == null)
        {
            problem = "--tenant, --client-id, and the identity options apply to --workspace and --adls only.";
            return null;
        }

        problem = Check(mode, identity, tenant, clientId, passwordVariable);
        if (problem != null)
        {
            return null;
        }

        options.SignIn = new SignInOptions(mode, tenant, clientId, secretVariable, certificate, passwordVariable, tokenVariable);
        return options;
    }

    /// <summary>
    /// Splits the URL of <c>--adls</c> into the endpoint, the filesystem, and the folder. It takes
    /// <c>https://&lt;account&gt;.dfs.core.windows.net/&lt;filesystem&gt;[/&lt;folder&gt;]</c> and
    /// <c>abfss://&lt;filesystem&gt;@&lt;account&gt;.dfs.core.windows.net[/&lt;folder&gt;]</c>. A problem never quotes the URL,
    /// which could carry a SAS token.
    /// </summary>
    private string? ParseAdls()
    {
        const string Form = "--adls takes https://<account>.dfs.core.windows.net/<container>[/<folder>]";
        if (Adls!.IndexOf('?') >= 0 || Adls.IndexOf('#') >= 0)
        {
            return "--adls takes a URL without a query: a SAS token is not accepted, since it would stay in the shell history. Sign in with one of the identities.";
        }

        if (!Uri.TryCreate(Adls, UriKind.Absolute, out Uri? url) || url.Host.Length == 0 || !string.IsNullOrEmpty(url.UserInfo) && url.Scheme != "abfss")
        {
            return Form + ".";
        }

        string host = url.Host.ToLowerInvariant();
        if (host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal))
        {
            return "--adls takes the DFS endpoint of the account, <account>.dfs.core.windows.net, not the blob endpoint.";
        }

        if (host.EndsWith("fabric.microsoft.com", StringComparison.Ordinal))
        {
            return "--adls is for a storage account; for OneLake, use --workspace and --item.";
        }

        // System.Uri has already resolved the . and .. segments, %2E-encoded too, so none is left to refuse.
        List<string> segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        string? fileSystem;
        if (url.Scheme == "https")
        {
            fileSystem = segments.Count > 0 ? segments[0] : null;
            segments = segments.Skip(1).ToList();
        }
        else if (url.Scheme == "abfss")
        {
            fileSystem = url.UserInfo.Length > 0 ? Uri.UnescapeDataString(url.UserInfo) : null;
        }
        else
        {
            return Form + ".";
        }

        if (fileSystem == null)
        {
            return Form + ": the container is missing.";
        }

        if (!FileSystemName.IsMatch(fileSystem))
        {
            return "the container of --adls is not a valid name: 3 to 63 lowercase letters, digits, and dashes.";
        }

        AdlsEndpoint = new Uri("https://" + url.Authority);
        AdlsFileSystem = fileSystem;
        AdlsFolder = string.Join("/", segments);
        return null;
    }

    /// <summary>What is wrong with the combination of the identity options, or null.</summary>
    private static string? Check(SignInMode mode, string? identity, string? tenant, string? clientId, string? passwordVariable)
    {
        if (passwordVariable != null && mode != SignInMode.ClientCertificate)
        {
            return "--client-certificate-password-env goes with --client-certificate.";
        }

        switch (mode)
        {
            case SignInMode.ClientSecret:
            case SignInMode.ClientCertificate:
                return clientId == null || tenant == null ? $"{identity} needs --client-id and --tenant." : null;
            case SignInMode.ManagedIdentity:
                return tenant != null ? "--tenant does not apply to --managed-identity: the identity belongs to one tenant." : null;
            case SignInMode.Environment:
                return tenant != null || clientId != null
                    ? $"--env-credential takes the tenant and the client id from {Cli.SignIn.TenantVariable} and {Cli.SignIn.ClientIdVariable}; leave out --tenant and --client-id."
                    : null;
            case SignInMode.AccessToken:
                return tenant != null || clientId != null ? "--tenant and --client-id do not apply to --access-token-env." : null;
            default:
                return clientId != null ? "--client-id goes with --client-secret-env, --client-certificate, or --managed-identity." : null;
        }
    }

    private static string TwoIdentities(string? first, string second) => $"{first} and {second} are two identities; give one.";

    private static string NotAName(string option) =>
        $"{option} takes the name of an environment variable, made of letters, digits, and underscores, such as PQAX_SECRET; never the secret itself.";
}
