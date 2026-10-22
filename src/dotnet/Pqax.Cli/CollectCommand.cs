using System.Globalization;
using System.Security.Cryptography;
using Azure;
using Azure.Core;
using Azure.Identity;
using Pqax.Collector;
using Pqax.Collector.ParquetNet;

namespace Pqax.Cli;

/// <summary>
/// <c>pqax collect</c>: collects the footprint of the tables of OneLake items, or of a folder with the OneLake layout,
/// and writes a PQAX file. It prints counts only, never the name of a table.
/// </summary>
public static class CollectCommand
{
    /// <summary>Parses the arguments after <c>collect</c>, collects, writes the file, and validates it.</summary>
    /// <param name="args">All the arguments, <c>collect</c> first.</param>
    /// <param name="output">Where the progress and the result go.</param>
    /// <param name="error">Where usage and errors go.</param>
    /// <returns>0 when the file is written and valid, 1 when the written file is invalid, 2 for a wrong command line or a failure.</returns>
    public static int Run(string[] args, TextWriter output, TextWriter error) => Run(args, output, error, CollectHost.Default);

    /// <summary>Parses the arguments after <c>collect</c>, collects, writes the file, and validates it, with the environment, the credential, and the HTTP client of a host.</summary>
    /// <param name="args">All the arguments, <c>collect</c> first.</param>
    /// <param name="output">Where the progress and the result go.</param>
    /// <param name="error">Where usage, errors, the device code, and the trace of <c>--verbose</c> go.</param>
    /// <param name="host">The environment variables, the credential, and the HTTP client of OneLake.</param>
    /// <returns>0 when the file is written and valid, 1 when the written file is invalid, 2 for a wrong command line or a failure.</returns>
    public static int Run(string[] args, TextWriter output, TextWriter error, CollectHost host)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(host);
        if (args.Skip(1).Any(a => a == "-h" || a == "--help"))
        {
            Usage(output);
            return Program.ExitOk;
        }

        CollectOptions? options = CollectOptions.Parse(args, out string? problem);
        if (options == null)
        {
            error.WriteLine($"pqax collect: {problem}");
            Usage(error);
            return Program.ExitError;
        }

        try
        {
            return RunAsync(options, output, error, host).GetAwaiter().GetResult();
        }
        catch (Exception e) when (options.Workspace != null && (e is SignInException || e is AuthenticationFailedException || e is RequestFailedException
            || e is CryptographicException || e is StorageException { IsAccessDenied: true }))
        {
            // One line with the likely cause. A message of Azure.Identity or of OneLake never holds the secret.
            error.WriteLine($"pqax collect: {SignIn.Describe(e)}");
            return Program.ExitError;
        }
        catch (Exception e) when (e is StorageException || e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
        {
            error.WriteLine($"pqax collect: {e.GetType().Name}: {e.Message}");
            return Program.ExitError;
        }
    }

    private static async Task<int> RunAsync(CollectOptions options, TextWriter output, TextWriter error, CollectHost host)
    {
        IStorage storage;
        IDisposable? owned = null;
        if (options.Local != null)
        {
            if (!Directory.Exists(options.Local))
            {
                error.WriteLine("pqax collect: the folder given with --local does not exist.");
                return Program.ExitError;
            }

            storage = new LocalStorage(options.Local);
        }
        else
        {
            if (options.Verbose)
            {
                error.WriteLine(SignIn.DescribeIdentity(options.SignIn));
            }

            TokenCredential credential = host.CreateCredential != null
                ? host.CreateCredential(options.SignIn)
                : SignIn.CreateCredential(options.SignIn, host.GetVariable, error);
            if (credential is AccessTokenCredential given)
            {
                if (given.ExpiresOn <= DateTimeOffset.UtcNow)
                {
                    error.WriteLine($"pqax collect: the access token of --access-token-env expired at {given.ExpiresOn.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}.");
                    return Program.ExitError;
                }

                if (given.Audience != null && !given.Audience.StartsWith("https://storage.azure.com", StringComparison.OrdinalIgnoreCase))
                {
                    error.WriteLine($"pqax collect: the access token is for {given.Audience}, and OneLake takes one for https://storage.azure.com; it may refuse it.");
                }
            }

            output.WriteLine(options.SignIn.Mode is SignInMode.DeviceCode or SignInMode.Browser
                ? "Waiting for the sign-in. The token stays in memory and is gone when the command ends."
                : "Signing in. The token stays in memory and is gone when the command ends.");
            var tokens = new TokenCache(credential);
            await tokens.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var storageOptions = new OneLakeStorageOptions { Trace = options.Verbose ? line => error.WriteLine(line) : null, HttpClient = host.HttpClient };
            if (host.Endpoint != null)
            {
                storageOptions.Endpoint = host.Endpoint;
            }

            var oneLake = new OneLakeStorage(options.Workspace!, tokens.GetAsync, storageOptions);
            storage = oneLake;
            owned = oneLake;
        }

        using (owned)
        {
            IReadOnlyList<string> items = options.Items.Count > 0
                ? options.Items
                : await TableDiscovery.ListItemsAsync(storage, CancellationToken.None).ConfigureAwait(false);
            var tables = new List<TableLocation>();
            foreach (string item in items)
            {
                tables.AddRange(await TableDiscovery.DiscoverAsync(storage, item, CancellationToken.None).ConfigureAwait(false));
            }

            output.WriteLine($"Found {Number(tables.Count)} tables in {Number(items.Count)} items.");
            if (tables.Count == 0)
            {
                error.WriteLine("pqax collect: no table found. An item is a folder such as Sales.Lakehouse, with a Tables folder.");
                return Program.ExitError;
            }

            var collectorOptions = new CollectorOptions
            {
                Level = options.Level,
                Depth = options.Depth,
                MaxFootersPerTable = options.MaxFooters,
                MaxCommitsPerTable = options.MaxCommits,
                Parallelism = options.Parallelism,
            };
            var collector = new PqaxCollector(storage, collectorOptions, new ParquetNetCheckpointReader());
            var progress = new Reporter(output);
            FootprintBuilder builder = await collector.CollectAsync(tables, progress, CancellationToken.None).ConfigureAwait(false);
            builder.Collection.CollectorApp = "pqax";
            builder.Collection.CollectorAppVersion = Program.Version();
            string? folder = Path.GetDirectoryName(Path.GetFullPath(options.Output!));
            if (folder != null)
            {
                Directory.CreateDirectory(folder);
            }

            builder.Write(options.Output!);
            ValidationResult result = PqaxValidator.Validate(options.Output!);
            output.WriteLine($"{Path.GetFileName(options.Output)}: {Number(tables.Count)} tables, {Number(progress.Footers)} footers read, {Number(progress.Issues)} issues, "
                + $"{Number(builder.Collection.RequestCount ?? 0)} requests, {Number(builder.Collection.BytesRead ?? 0)} bytes read; "
                + (result.IsValid ? "valid" : $"invalid, {Number(result.Issues.Count)} issues"));
            foreach (ValidationIssue issue in result.Issues)
            {
                error.WriteLine(issue.ToString());
            }

            return result.IsValid ? Program.ExitOk : Program.ExitInvalid;
        }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Usage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  pqax collect --local <folder> --output <file.pqax> [--item <name.Kind>]... [options]");
        writer.WriteLine("  pqax collect --workspace <name or id> --item <name.Kind> [--item ...] --output <file.pqax> [identity] [options]");
        writer.WriteLine();
        writer.WriteLine("--local reads a folder with the OneLake layout: <item>.<Kind>/Tables/[<schema>/]<table>. Without --item, every");
        writer.WriteLine("lakehouse, warehouse, mirrored database, and SQL database in the folder. --workspace reads OneLake after a sign-in;");
        writer.WriteLine("an item is named as in OneLake, such as Sales.Lakehouse.");
        writer.WriteLine("Name the workspace and the items both by name or both by id: OneLake refuses a workspace id with item names.");
        writer.WriteLine();
        writer.WriteLine("Identity, one of these; a secret is read from an environment variable, never from the command line:");
        writer.WriteLine("  (none), --device-code                         A person, with a device code that the command prints. The default.");
        writer.WriteLine("  --browser                                     A person, in the browser.");
        writer.WriteLine("  --client-id <id> --client-secret-env <NAME>   A service principal; the secret is in the variable NAME. Needs --tenant.");
        writer.WriteLine("  --client-id <id> --client-certificate <file>  A service principal with a PFX or PEM certificate. Needs --tenant.");
        writer.WriteLine("    [--client-certificate-password-env <NAME>]  The password of the certificate, in the variable NAME.");
        writer.WriteLine("  --managed-identity [--client-id <id>]         The managed identity: system-assigned, or user-assigned with its client id.");
        writer.WriteLine("  --azure-cli                                   The account of az login.");
        writer.WriteLine("  --access-token-env <NAME>                     An access token for https://storage.azure.com, in the variable NAME.");
        writer.WriteLine("  --env-credential                              AZURE_TENANT_ID, AZURE_CLIENT_ID, and AZURE_CLIENT_SECRET or");
        writer.WriteLine("                                                AZURE_CLIENT_CERTIFICATE_PATH (and AZURE_CLIENT_CERTIFICATE_PASSWORD).");
        writer.WriteLine("  --tenant <id>                                 The tenant, for a person, a service principal, or the Azure CLI.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --depth summary|files|rowGroups|columnChunks   The detail kept for each table. Default columnChunks.");
        writer.WriteLine("  --level 1|2                                   1 reads the Delta logs, 2 also the Parquet footers. Default 2.");
        writer.WriteLine("  --max-footers <n>                             Footers read for each table, 0 for all. Default 0.");
        writer.WriteLine("  --max-commits <n>                             Commit files read for the history of each table; the commits the state needs are read anyway. Default 30.");
        writer.WriteLine("  --parallelism <n>                             Storage requests at the same time. Default 16.");
        writer.WriteLine("  --verbose                                     Print the identity, and every OneLake request with its status code, to the error output.");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 written and valid, 1 written and invalid, 2 wrong command line or failure.");
    }

    /// <summary>Prints one line for each table that is done, with counts only.</summary>
    private sealed class Reporter(TextWriter output) : IProgress<CollectionProgress>
    {
        public long Footers { get; private set; }

        public long Issues { get; private set; }

        public void Report(CollectionProgress value)
        {
            Footers += value.FootersRead;
            Issues += value.IssueCount;
            output.WriteLine($"  table {Number(value.TablesCompleted)} of {Number(value.TableCount)}: {Number(value.FootersRead)} footers, "
                + $"{Number(value.IssueCount)} issues; {Number(value.RequestCount)} requests so far");
        }
    }
}
