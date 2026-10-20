using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// Collects the footprint of Delta tables: reads their logs, and at level 2 the footers of their Parquet files, and
/// fills a <see cref="FootprintBuilder"/> with sources, tables, columns, partitions, physical schemas, writers,
/// operations, issues, and the detail tables down to the depth of the options.
/// </summary>
/// <remarks>
/// A failure on one table or file becomes an issue in the footprint, and the collection goes on. Every read goes
/// through the storage, so the collection of the footprint states the requests and the bytes. Data values are never
/// kept unless the options ask for them, and footer metadata is kept only for the keys of an allow-list.
/// </remarks>
public sealed class PqaxCollector
{
    /// <summary>The name that the footprint records as <c>collectorLib</c>.</summary>
    public const string LibraryName = "Pqax.Collector";

    private readonly IStorage storage;
    private readonly CollectorOptions options;
    private readonly ICheckpointReader? checkpoints;

    /// <summary>Creates a collector.</summary>
    /// <param name="storage">The storage that holds the tables.</param>
    /// <param name="options">What to read and what to keep. The values are checked here and copied when a collection starts.</param>
    /// <param name="checkpoints">
    /// Reads Delta checkpoints in Parquet, such as <c>ParquetNetCheckpointReader</c> of <c>Pqax.Collector.ParquetNet</c>.
    /// Without it, a table that needs its checkpoint gets the issue <see cref="IssueCodes.DeltaCheckpointNotRead"/>, and
    /// its summary holds what the JSON commits say.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public PqaxCollector(IStorage storage, CollectorOptions options, ICheckpointReader? checkpoints = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.checkpoints = checkpoints;
        options.Validate();
    }

    /// <summary>The version of this library, which the footprint records as <c>collectorLibVersion</c>.</summary>
    public static string LibraryVersion
    {
        get
        {
            Assembly assembly = typeof(PqaxCollector).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (informational != null)
            {
                int plus = informational.IndexOf('+');
                return plus >= 0 ? informational.Substring(0, plus) : informational;
            }

            return assembly.GetName().Version?.ToString() ?? "0";
        }
    }

    /// <summary>Collects the tables.</summary>
    /// <param name="tables">The tables, as <see cref="TableDiscovery"/> finds them. The footprint keeps their order.</param>
    /// <param name="progress">Receives a report each time a table is done, one report at a time, from the thread that finished the table. Null for none.</param>
    /// <param name="cancellation">Cancels the collection.</param>
    /// <returns>
    /// A builder with the footprint. Its <see cref="FootprintBuilder.Collection"/> states the library, the times, the
    /// level, the requests, and the bytes; the caller can set the application before writing it.
    /// </returns>
    /// <exception cref="OperationCanceledException">The collection was cancelled.</exception>
    public async Task<FootprintBuilder> CollectAsync(IReadOnlyList<TableLocation> tables, IProgress<CollectionProgress>? progress, CancellationToken cancellation)
    {
        if (tables == null)
        {
            throw new ArgumentNullException(nameof(tables));
        }

        CollectorOptions run = Copy(options);
        run.Validate();
        DateTimeOffset started = Now();
        var statistics = storage as IStorageStatistics;
        long requestsBefore = statistics?.RequestCount ?? 0;
        long bytesBefore = statistics?.BytesRead ?? 0;

        var sources = new List<TableLocation>();
        var sourceIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var tableSource = new int[tables.Count];
        for (int i = 0; i < tables.Count; i++)
        {
            TableLocation table = tables[i] ?? throw new ArgumentException("A table location is null.", nameof(tables));

            // Two lakehouses of the same name in two workspaces are two sources: the location tells them apart.
            string key = table.SourceKind + "\u0001" + table.SourceName + "\u0001" + (table.SourceLocation == null ? "\u0000" : "\u0002" + table.SourceLocation);
            if (!sourceIndex.TryGetValue(key, out int index))
            {
                index = sources.Count;
                sourceIndex[key] = index;
                sources.Add(table);
            }

            tableSource[i] = index;
        }

        var results = new CollectedTable[tables.Count];
        using (var counting = new CountingStorage(storage, run.Parallelism))
        {
            var collector = new TableCollector(counting, run, checkpoints);
            int next = -1;
            int completed = 0;
            int workers = Math.Min(run.Parallelism, tables.Count);
            var tasks = new List<Task>(workers);
            for (int w = 0; w < workers; w++)
            {
                tasks.Add(Task.Run(
                    async () =>
                    {
                        int i;
                        while ((i = Interlocked.Increment(ref next)) < tables.Count)
                        {
                            CollectedTable result = await collector.CollectAsync(tables[i], tableSource[i], cancellation).ConfigureAwait(false);
                            results[i] = result;
                            if (progress != null)
                            {
                                // One report at a time, so that the receiver does not have to be thread-safe.
                                lock (results)
                                {
                                    completed++;
                                    progress.Report(new CollectionProgress(tables[i], completed, tables.Count, result.FootersRead, result.Issues.Count, counting.RequestCount, counting.BytesRead));
                                }
                            }
                        }
                    },
                    cancellation));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var collection = new CollectionInfo
            {
                CollectorLib = LibraryName,
                CollectorLibVersion = LibraryVersion,
                StartedAt = started,
                CompletedAt = Now(),
                Level = run.Level,
                IncludesDataValues = run.IncludeDataValues,
                IncludesPartitionValues = run.IncludePartitionValues,
                RequestCount = statistics != null ? statistics.RequestCount - requestsBefore : counting.RequestCount,
                BytesRead = statistics != null ? statistics.BytesRead - bytesBefore : counting.BytesRead,
            };
            return FootprintAssembler.Assemble(collection, sources, results, run);
        }
    }

    private static DateTimeOffset Now()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private static CollectorOptions Copy(CollectorOptions options) => new CollectorOptions
    {
        Level = options.Level,
        Depth = options.Depth,
        MaxFootersPerTable = options.MaxFootersPerTable,
        MaxCommitsPerTable = options.MaxCommitsPerTable,
        Parallelism = options.Parallelism,
        IncludeDataValues = options.IncludeDataValues,
        IncludePartitionValues = options.IncludePartitionValues,
        FooterTailBytes = options.FooterTailBytes,
        FooterTailMargin = options.FooterTailMargin,
        FooterTailFloorBytes = options.FooterTailFloorBytes,
    };
}
