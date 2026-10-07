using System.Globalization;
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
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Skip(1).Any(a => a == "-h" || a == "--help"))
        {
            Usage(output);
            return Program.ExitOk;
        }

        Options? options = Options.Parse(args, out string? problem);
        if (options == null)
        {
            error.WriteLine($"pqax collect: {problem}");
            Usage(error);
            return Program.ExitError;
        }

        try
        {
            return RunAsync(options, output, error).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is StorageException || e is IOException || e is UnauthorizedAccessException || e is AuthenticationFailedException || e is ArgumentException)
        {
            error.WriteLine($"pqax collect: {e.GetType().Name}: {e.Message}");
            return Program.ExitError;
        }
    }

    private static async Task<int> RunAsync(Options options, TextWriter output, TextWriter error)
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
            // The device code is the default: a browser window can open where nobody sees it, and the code is printed here.
            TokenCredential credential = !options.Browser
                ? new DeviceCodeCredential(new DeviceCodeCredentialOptions
                {
                    TenantId = options.Tenant,
                    DeviceCodeCallback = (code, _) =>
                    {
                        error.WriteLine(code.Message);
                        return Task.CompletedTask;
                    },
                })
                : new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions { TenantId = options.Tenant });
            output.WriteLine("Waiting for the sign-in. The token stays in memory and is gone when the command ends.");
            var tokens = new TokenCache(credential);
            await tokens.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var oneLake = new OneLakeStorage(options.Workspace!, tokens.GetAsync,
                new OneLakeStorageOptions { Trace = options.Verbose ? line => error.WriteLine(line) : null });
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
        writer.WriteLine("  pqax collect --workspace <name or id> --item <name.Kind> [--item ...] --output <file.pqax> [--tenant <id>] [--browser] [options]");
        writer.WriteLine();
        writer.WriteLine("--local reads a folder with the OneLake layout: <item>.<Kind>/Tables/[<schema>/]<table>. Without --item, every");
        writer.WriteLine("lakehouse, warehouse, mirrored database, and SQL database in the folder. --workspace reads OneLake after a sign-in");
        writer.WriteLine("with a device code, which the command prints, or in the browser with --browser; an item is named as in OneLake,");
        writer.WriteLine("such as Sales.Lakehouse.");
        writer.WriteLine("Name the workspace and the items both by name or both by id: OneLake refuses a workspace id with item names.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --depth summary|files|rowGroups|columnChunks   The detail kept for each table. Default columnChunks.");
        writer.WriteLine("  --level 1|2                                   1 reads the Delta logs, 2 also the Parquet footers. Default 2.");
        writer.WriteLine("  --max-footers <n>                             Footers read for each table, 0 for all. Default 0.");
        writer.WriteLine("  --max-commits <n>                             Recent commits read for the history of each table. Default 30.");
        writer.WriteLine("  --parallelism <n>                             Storage requests at the same time. Default 16.");
        writer.WriteLine("  --verbose                                     Print every OneLake request and its status code to the error output.");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 written and valid, 1 written and invalid, 2 wrong command line or failure.");
    }

    /// <summary>Caches the access token until five minutes before it expires.</summary>
    private sealed class TokenCache(TokenCredential credential)
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private AccessToken? token;

        public async Task<string> GetAsync(CancellationToken cancellation)
        {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (token is not { } current || current.ExpiresOn < DateTimeOffset.UtcNow.AddMinutes(5))
                {
                    token = current = await credential.GetTokenAsync(new TokenRequestContext([OneLakeStorage.Scope]), cancellation).ConfigureAwait(false);
                }

                return current.Token;
            }
            finally
            {
                gate.Release();
            }
        }
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

    /// <summary>The arguments of <c>collect</c>.</summary>
    private sealed class Options
    {
        public string? Local { get; private set; }

        public string? Workspace { get; private set; }

        public List<string> Items { get; } = [];

        public string? Output { get; private set; }

        public string? Tenant { get; private set; }

        public bool DeviceCode { get; private set; }

        public bool Browser { get; private set; }

        public bool Verbose { get; private set; }

        public DetailDepth Depth { get; private set; } = DetailDepth.ColumnChunks;

        public int Level { get; private set; } = 2;

        public int MaxFooters { get; private set; }

        public int MaxCommits { get; private set; } = 30;

        public int Parallelism { get; private set; } = 16;

        public static Options? Parse(string[] args, out string? problem)
        {
            var options = new Options();
            problem = null;
            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                string? Next()
                {
                    return i + 1 < args.Length && args[i + 1].Length > 0 && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
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
                    case "--item":
                        value = Next();
                        if (value != null)
                        {
                            options.Items.Add(value);
                        }

                        break;
                    case "--output":
                        options.Output = value = Next();
                        break;
                    case "--tenant":
                        options.Tenant = value = Next();
                        break;
                    case "--device-code":
                        options.DeviceCode = true;   // the default; accepted for scripts that ask for it
                        value = string.Empty;
                        break;
                    case "--browser":
                        options.Browser = true;
                        value = string.Empty;
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
                        problem = $"unknown option '{arg}'.";
                        return null;
                }

                if (value == null)
                {
                    problem = $"{arg} needs a value.";
                    return null;
                }
            }

            if ((options.Local == null) == (options.Workspace == null))
            {
                problem = "give either --local or --workspace.";
                return null;
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

            if ((options.Tenant != null || options.DeviceCode || options.Browser) && options.Workspace == null)
            {
                problem = "--tenant, --device-code, and --browser apply to --workspace only.";
                return null;
            }

            return options;
        }
    }
}
