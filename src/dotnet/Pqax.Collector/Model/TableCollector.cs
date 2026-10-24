using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// Collects one table: reads its Delta log, reads the footers of its files at level 2, and computes its columns,
/// partitions, physical schemas, writers, operations, details, and totals. A failure becomes an issue of the table.
/// </summary>
internal sealed class TableCollector
{
    private const string ConstraintPrefix = "delta.constraints.";

    /// <summary>The folders of the table folder that hold no data files of the table: the log, and the change data feed.</summary>
    private static readonly string[] NonDataFolders = { "_delta_log", "_change_data" };

    private readonly IStorage storage;
    private readonly CollectorOptions options;
    private readonly ICheckpointReader? checkpoints;
    private readonly FooterReader footerReader;

    public TableCollector(IStorage storage, CollectorOptions options, ICheckpointReader? checkpoints)
    {
        this.storage = storage;
        this.options = options;
        this.checkpoints = checkpoints;
        footerReader = new FooterReader(storage, options.FooterTailBytes, options.IncludeDataValues);
    }

    public async Task<CollectedTable> CollectAsync(TableLocation location, int source, CancellationToken cancellation)
    {
        if (location.Format == TableFormats.Parquet)
        {
            return await CollectParquetAsync(location, source, cancellation).ConfigureAwait(false);
        }

        var issues = new IssueList();
        var table = new CollectedTable(location, source) { Depth = DetailDepth.Summary };
        DeltaLog? log = null;
        try
        {
            var reader = new DeltaLogReader(storage, checkpoints, options.MaxCommitsPerTable, options.IncludeDataValues);
            log = await reader.ReadAsync(location.Path, issues, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (StorageException e)
        {
            issues.Add(e.IsAccessDenied ? IssueCodes.AccessDenied : IssueCodes.TableNotRead, "The Delta log could not be read: " + e.Message);
        }
        catch (Exception e)
        {
            issues.Add(IssueCodes.TableNotRead, "The Delta log could not be read: " + e.GetType().Name + ": " + e.Message);
        }

        if (log != null && log.Found)
        {
            try
            {
                await BuildAsync(table, log, issues, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // Whatever was built is dropped, so that the table stays consistent.
                issues.Add(IssueCodes.TableNotRead, "The table could not be collected: " + e.GetType().Name + ": " + e.Message);
                table = new CollectedTable(location, source) { Depth = DetailDepth.Summary };
            }
        }

        table.Issues.AddRange(issues.ToList());
        return table;
    }

    /// <summary>
    /// Collects a folder of Parquet files with no table format: its files from one listing of the folder, its columns from
    /// the footer of the most recent file, and at level 2 every footer. Nothing is known of a history, a version, or
    /// metadata files, and every Parquet file of the folder is a file of the table.
    /// </summary>
    private async Task<CollectedTable> CollectParquetAsync(TableLocation location, int source, CancellationToken cancellation)
    {
        var issues = new IssueList();
        var table = new CollectedTable(location, source) { Depth = DetailDepth.Summary };
        IReadOnlyList<StorageEntry>? entries = null;
        try
        {
            entries = await storage.ListAsync(location.Path, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (StorageException e)
        {
            issues.Add(e.IsAccessDenied ? IssueCodes.AccessDenied : IssueCodes.TableNotRead, "The table folder could not be listed: " + e.Message);
        }
        catch (Exception e)
        {
            issues.Add(IssueCodes.TableNotRead, "The table folder could not be listed: " + e.GetType().Name + ": " + e.Message);
        }

        if (entries != null)
        {
            try
            {
                await BuildParquetAsync(table, entries, issues, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                issues.Add(IssueCodes.TableNotRead, "The table could not be collected: " + e.GetType().Name + ": " + e.Message);
                table = new CollectedTable(location, source) { Depth = DetailDepth.Summary };
            }
        }

        table.Issues.AddRange(issues.ToList());
        return table;
    }

    private async Task BuildParquetAsync(CollectedTable table, IReadOnlyList<StorageEntry> entries, IssueList issues, CancellationToken cancellation)
    {
        var files = new List<LiveFile>();
        foreach (StorageEntry entry in entries.Where(TableDiscovery.IsParquetFile).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            files.Add(new LiveFile(Uri.EscapeDataString(entry.Name), entry.Name)
            {
                Size = entry.Size,
                ModificationTime = entry.LastModified?.ToUnixTimeMilliseconds(),
            });
        }

        // Every Parquet file of the folder is a file of the table, so none is unreferenced.
        table.UnreferencedFileCount = 0;
        table.UnreferencedFileSize = 0;
        table.FooterCount = 0;
        table.CollectedLevel = 1;

        // The columns come from the most recent file, as the object model says: the latest modification time, then the last name.
        int newest = -1;
        for (int i = 0; i < files.Count; i++)
        {
            if (newest < 0 || (files[i].ModificationTime ?? long.MinValue) >= (files[newest].ModificationTime ?? long.MinValue))
            {
                newest = i;
            }
        }

        var collected = new CollectedFile[files.Count];
        BuildFiles(table, files, collected);
        var planner = new FooterTailPlanner(options.FooterTailBytes, options.FooterTailFloorBytes, options.FooterTailMargin);
        ParquetFooter? schemaFooter = null;
        if (newest >= 0)
        {
            schemaFooter = await ReadFooterAsync(table.Location, files[newest], collected[newest], planner, issues, cancellation).ConfigureAwait(false);
            if (schemaFooter != null)
            {
                foreach (ParquetField field in ParquetTypes.TopLevelFields(schemaFooter))
                {
                    table.Columns.Add(new CollectedColumn(field.Name, field.DataType, field.Name) { IsNullable = field.IsNullable });
                }
            }
        }

        var aggregate = new FooterAggregate(table, options.Depth);
        if (options.Level >= 2)
        {
            await ReadFootersAsync(table, files, collected, aggregate, issues, cancellation, planner, newest < 0 ? null : newest, schemaFooter).ConfigureAwait(false);
        }

        Totals(table, files, collected, aggregate);
    }

    /// <summary>Reads the footer of a file, or records the issue of its failure and returns null.</summary>
    private async Task<ParquetFooter?> ReadFooterAsync(TableLocation location, LiveFile file, CollectedFile collected, FooterTailPlanner planner, IssueList issues, CancellationToken cancellation)
    {
        if (file.Size == null)
        {
            issues.Add(IssueCodes.FooterNotRead, "The listing gives no size for the file, so its footer was not read.", collected.Path);
            return null;
        }

        try
        {
            ParquetFooter footer = await footerReader.ReadAsync(location.Path + "/" + file.Path, file.Size.Value, planner.Current, cancellation).ConfigureAwait(false);
            planner.Observe(footer.FooterLength);
            return footer;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            CollectedIssue issue = FooterFailure(e, collected.Path);
            issues.Add(issue.Code, issue.Message, issue.Path);
            return null;
        }
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(long? milliseconds) =>
        milliseconds == null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value);

    /// <summary>The files whose footer is read: all of them, or a sample spread evenly over the sorted list.</summary>
    private static List<int> Sample(int count, int max)
    {
        var sample = new List<int>();
        if (max == 0 || count <= max)
        {
            for (int i = 0; i < count; i++)
            {
                sample.Add(i);
            }

            return sample;
        }

        if (max == 1)
        {
            sample.Add(0);
            return sample;
        }

        int previous = -1;
        for (int i = 0; i < max; i++)
        {
            int index = (int)((long)i * (count - 1) / (max - 1));
            if (index != previous)
            {
                sample.Add(index);
                previous = index;
            }
        }

        return sample;
    }

    private static int? FindColumn(List<CollectedColumn> columns, string name, bool byFileName)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (string.Equals(byFileName ? columns[i].FileName : columns[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        int? match = null;
        for (int i = 0; i < columns.Count; i++)
        {
            if (string.Equals(byFileName ? columns[i].FileName : columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                if (match != null)
                {
                    return null;
                }

                match = i;
            }
        }

        return match;
    }

    /// <summary>
    /// The column of a leaf: by the field id of its top-level field when the file and the table have field ids,
    /// otherwise by the name that the files use, which is the physical name with column mapping.
    /// </summary>
    private static int? MapLeaf(ParquetLeafColumn leaf, List<CollectedColumn> columns)
    {
        if (leaf.TopLevelFieldId != null && columns.Any(c => c.FieldId != null))
        {
            for (int i = 0; i < columns.Count; i++)
            {
                if (columns[i].FieldId == leaf.TopLevelFieldId.Value)
                {
                    return i;
                }
            }

            return null;
        }

        return leaf.Path.Count == 0 ? null : FindColumn(columns, leaf.Path[0], true);
    }

    private static string Signature(ParquetFooter footer)
    {
        var text = new StringBuilder();
        foreach (ParquetSchemaElement e in footer.Schema)
        {
            text.Append(e.Name).Append('\u0001')
                .Append(e.Type).Append('\u0001')
                .Append(e.TypeLength).Append('\u0001')
                .Append(e.Repetition).Append('\u0001')
                .Append(e.NumChildren).Append('\u0001')
                .Append(e.ConvertedType).Append('\u0001')
                .Append(e.Scale).Append('\u0001')
                .Append(e.Precision).Append('\u0001')
                .Append(e.FieldId).Append('\u0001')
                .Append(e.LogicalType).Append('\u0002');
        }

        return text.ToString();
    }

    /// <summary>
    /// Whether a chunk with a dictionary fell back to another encoding: from the encoding statistics when the footer has
    /// them; without them, false when the chunk lists only dictionary and level encodings, and unknown otherwise.
    /// </summary>
    private static bool? Fallback(ParquetColumnChunk chunk)
    {
        if (!chunk.HasDictionaryPage)
        {
            return false;
        }

        if (chunk.EncodingStats != null)
        {
            long data = 0;
            long dictionary = 0;
            foreach (ParquetPageEncodingStats stats in chunk.EncodingStats)
            {
                if (stats.IsDataPage)
                {
                    data += stats.Count;
                    if (stats.IsDictionaryEncoded)
                    {
                        dictionary += stats.Count;
                    }
                }
            }

            return dictionary < data;
        }

        foreach (string encoding in chunk.Encodings)
        {
            if (encoding != "PLAIN_DICTIONARY" && encoding != "RLE_DICTIONARY" && encoding != "RLE" && encoding != "BIT_PACKED")
            {
                return null;
            }
        }

        return false;
    }

    private static List<int>? Clustering(string configuration, List<CollectedColumn> columns)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(configuration);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("clusteringColumns", out JsonElement list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<int>();
            foreach (JsonElement path in list.EnumerateArray())
            {
                string? first = path.ValueKind == JsonValueKind.Array && path.GetArrayLength() > 0 && path[0].ValueKind == JsonValueKind.String
                    ? path[0].GetString()
                    : path.ValueKind == JsonValueKind.String ? path.GetString() : null;
                int? column = first == null ? null : FindColumn(columns, first, true);
                if (column != null && !result.Contains(column.Value))
                {
                    result.Add(column.Value);
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private IReadOnlyDictionary<string, string>? Properties(MetadataAction? metadata)
    {
        if (metadata == null)
        {
            return null;
        }

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata.Configuration != null)
        {
            foreach (KeyValuePair<string, string> property in metadata.Configuration)
            {
                // A check constraint is an expression that can hold data values.
                if (!options.IncludeDataValues && property.Key.StartsWith(ConstraintPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                properties[property.Key] = property.Value;
            }
        }

        return properties;
    }

    private async Task BuildAsync(CollectedTable table, DeltaLog log, IssueList issues, CancellationToken cancellation)
    {
        MetadataAction? metadata = log.Metadata;
        table.TableId = metadata?.Id;
        table.Description = string.IsNullOrEmpty(metadata?.Description) ? null : metadata!.Description;
        table.CreatedAt = Time(metadata?.CreatedTime);
        table.Properties = Properties(metadata);
        table.SnapshotId = log.Version == null ? null : Text(log.Version.Value);
        table.SnapshotTimestamp = log.SnapshotTimestamp;
        ProtocolAction? protocol = log.Protocol;
        table.Delta = new CollectedDelta
        {
            MinReaderVersion = protocol?.MinReaderVersion,
            MinWriterVersion = protocol?.MinWriterVersion,
            ReaderFeatures = protocol == null ? null : protocol.ReaderFeatures ?? new string[0],
            WriterFeatures = protocol == null ? null : protocol.WriterFeatures ?? new string[0],
            CheckpointVersion = log.CheckpointVersion,
            CommitsAfterCheckpoint = log.CommitsAfterCheckpoint,

            // Without the whole state, the domains of the checkpoint are unknown, so the list is too.
            Domains = log.FilesComplete ? log.Domains.Keys.ToList() : null,
        };
        table.CommitCount = log.CommitCount;
        table.MetadataFileCount = log.MetadataFileCount;
        table.MetadataSize = log.MetadataSize;

        BuildColumns(table, metadata, issues);
        if (log.Domains.TryGetValue("delta.clustering", out string? clustering))
        {
            table.ClusteringColumns = Clustering(clustering, table.Columns);
        }

        BuildHistory(table, log);
        table.FooterCount = 0;
        table.CollectedLevel = 1;
        if (!log.FilesComplete)
        {
            // The files are unknown, so are their totals, and no detail can be complete.
            return;
        }

        List<LiveFile> files = log.Files;
        var collected = new CollectedFile[files.Count];
        BuildFiles(table, files, collected);
        table.NonLiveFileCount = log.NonLiveFileCount;
        table.NonLiveFileSize = log.NonLiveFileSize;
        table.NonLiveOldestRemovedAt = Time(log.NonLiveOldestRemovedAt);
        table.NonLiveNewestRemovedAt = Time(log.NonLiveNewestRemovedAt);
        await UnreferencedAsync(table, files, issues, cancellation).ConfigureAwait(false);

        var aggregate = new FooterAggregate(table, options.Depth);
        if (options.Level >= 2)
        {
            await ReadFootersAsync(table, files, collected, aggregate, issues, cancellation).ConfigureAwait(false);
        }

        Totals(table, files, collected, aggregate);
    }

    /// <summary>
    /// Lists the table folder at any depth, outside <c>_delta_log</c> and <c>_change_data</c>, and counts the Parquet
    /// files that no live file of the version is: files removed and not yet vacuumed, and files that no commit
    /// referenced. A file whose size the listing does not give counts, and adds nothing to the size. When the listing
    /// fails, the issue says so and both totals stay unknown.
    /// </summary>
    private async Task UnreferencedAsync(CollectedTable table, List<LiveFile> files, IssueList issues, CancellationToken cancellation)
    {
        string root = table.Location.Path.Trim('/');
        IReadOnlyList<StorageEntry> listing;
        try
        {
            listing = await StorageListing.ListFilesAsync(storage, root, NonDataFolders, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (StorageException e)
        {
            issues.Add(e.IsAccessDenied ? IssueCodes.AccessDenied : IssueCodes.DataFilesNotListed, "The table folder could not be listed for its data files: " + e.Message);
            return;
        }
        catch (Exception e)
        {
            issues.Add(IssueCodes.DataFilesNotListed, "The table folder could not be listed for its data files: " + e.GetType().Name + ": " + e.Message);
            return;
        }

        // The decoded paths of the live files, relative to the table folder. An absolute path inside the table location is
        // made relative, so that it matches the listing.
        string? location = table.Location.Location?.Replace('\\', '/').TrimEnd('/');
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (LiveFile file in files)
        {
            string path = file.Path;
            if (file.IsOutsideTable && location != null && location.Length > 0 && path.Length > location.Length + 1
                && path[location.Length] == '/' && path.StartsWith(location, StringComparison.Ordinal))
            {
                path = path.Substring(location.Length + 1);
            }

            referenced.Add(path);
        }

        long count = 0;
        long size = 0;
        foreach (StorageEntry entry in listing)
        {
            if (entry.IsDirectory
                || !entry.Name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase)
                || StorageListing.IsExcluded(root, entry.Path, NonDataFolders))
            {
                continue;
            }

            string relative = root.Length == 0 ? entry.Path : entry.Path.Substring(root.Length + 1);
            if (referenced.Contains(relative))
            {
                continue;
            }

            count++;
            size += entry.Size ?? 0;
        }

        table.UnreferencedFileCount = count;
        table.UnreferencedFileSize = size;
    }

    private void BuildColumns(CollectedTable table, MetadataAction? metadata, IssueList issues)
    {
        var deltaColumns = new List<DeltaColumn>();
        if (metadata?.SchemaString != null)
        {
            try
            {
                deltaColumns = DeltaSchema.Parse(metadata.SchemaString);
            }
            catch (JsonException e)
            {
                issues.Add(IssueCodes.DeltaSchemaNotParsed, "The schema of the table could not be parsed: " + e.Message);
            }
        }

        IReadOnlyList<string> partitionColumns = metadata?.PartitionColumns ?? new string[0];
        foreach (DeltaColumn column in deltaColumns)
        {
            table.Columns.Add(new CollectedColumn(column.Name, column.DataType, column.FileName)
            {
                FieldId = column.FieldId,
                IsNullable = column.IsNullable,
                PhysicalName = column.MappedName != null && column.MappedName != column.Name ? column.MappedName : null,
                Description = column.Description,
            });
        }

        if (partitionColumns.Count > 0)
        {
            table.PartitionFields = new List<CollectedPartitionField>();
            foreach (string name in partitionColumns)
            {
                int? column = FindColumn(table.Columns, name, false);
                if (column != null)
                {
                    table.Columns[column.Value].IsPartitionColumn = true;
                }

                table.PartitionFields.Add(new CollectedPartitionField(name, column));
            }
        }
    }

    private void BuildHistory(CollectedTable table, DeltaLog log)
    {
        var operations = new Dictionary<string, CollectedOperation>(StringComparer.Ordinal);
        DateTimeOffset? oldest = null;
        foreach (CommitRecord commit in log.History)
        {
            if (commit.Timestamp != null && (oldest == null || commit.Timestamp < oldest))
            {
                oldest = commit.Timestamp;
            }

            CommitInfoAction? info = commit.Info;
            if (!string.IsNullOrEmpty(info?.Operation))
            {
                if (!operations.TryGetValue(info!.Operation!, out CollectedOperation? operation))
                {
                    operation = new CollectedOperation(info.Operation!);
                    operations[info.Operation!] = operation;
                    table.Operations.Add(operation);
                }

                operation.CommitCount++;
                if (commit.Timestamp != null && (operation.LastAt == null || commit.Timestamp > operation.LastAt))
                {
                    operation.LastAt = commit.Timestamp;
                }
            }

            if (options.Depth >= DetailDepth.Files)
            {
                table.Commits.Add(new CollectedCommit(Text(commit.Version))
                {
                    Timestamp = commit.Timestamp,
                    Operation = info?.Operation,
                    Engine = info?.EngineInfo,
                    IsBlindAppend = info?.IsBlindAppend,
                    Metrics = info == null ? null : info.OperationMetrics ?? new Dictionary<string, string>(),
                    Parameters = options.IncludeDataValues ? info?.OperationParameters : null,
                });
            }
        }

        table.CommitsRead = log.History.Count;
        table.OldestCommitAt = oldest;
    }

    private void BuildFiles(CollectedTable table, List<LiveFile> files, CollectedFile[] collected)
    {
        var ordinals = new PartitionOrdinals();
        var partitions = new Dictionary<string, int>(StringComparer.Ordinal);
        List<CollectedPartitionField>? fields = table.PartitionFields;
        var key = new StringBuilder();
        for (int i = 0; i < files.Count; i++)
        {
            LiveFile file = files[i];
            int? partition = null;
            if (fields != null)
            {
                var values = new string?[fields.Count];
                key.Clear();
                for (int f = 0; f < fields.Count; f++)
                {
                    values[f] = PartitionValue(file, fields[f], table.Columns);
                    ordinals.Get(fields[f].Name, values[f]);
                    key.Append(values[f] == null ? "\u0000" : "\u0001" + values[f]).Append('\u0002');
                }

                string signature = key.ToString();
                if (!partitions.TryGetValue(signature, out int index))
                {
                    index = table.Partitions.Count;
                    partitions[signature] = index;
                    table.Partitions.Add(new CollectedPartition(values));
                }

                partition = index;
            }

            string path = options.IncludePartitionValues ? file.Path : ordinals.Sanitize(file.Path);
            collected[i] = new CollectedFile(path)
            {
                FileSize = file.Size,
                ModifiedAt = Time(file.ModificationTime),
                RowCount = file.NumRecords,
                DeletedRowCount = file.DeletedRowCount,
                DeletionVectorSize = file.DeletionVectorSize,
                Partition = partition,
                ClusteringProvider = file.ClusteringProvider,
                IsVOrdered = file.VOrderTag,
            };
        }
    }

    private static string? PartitionValue(LiveFile file, CollectedPartitionField field, List<CollectedColumn> columns)
    {
        IReadOnlyDictionary<string, string?>? values = file.PartitionValues;
        if (values == null)
        {
            return null;
        }

        // With column mapping the log keys the partition values by physical name.
        if (field.SourceColumn != null && values.TryGetValue(columns[field.SourceColumn.Value].FileName, out string? physical))
        {
            return physical;
        }

        return values.TryGetValue(field.Name, out string? value) ? value : null;
    }

    private async Task ReadFootersAsync(
        CollectedTable table,
        List<LiveFile> files,
        CollectedFile[] collected,
        FooterAggregate aggregate,
        IssueList issues,
        CancellationToken cancellation,
        FooterTailPlanner? planner = null,
        int? known = null,
        ParquetFooter? knownFooter = null)
    {
        List<int> sample = Sample(files.Count, options.MaxFootersPerTable);
        var fileIssues = new CollectedIssue?[files.Count];
        int next = -1;
        planner ??= new FooterTailPlanner(options.FooterTailBytes, options.FooterTailFloorBytes, options.FooterTailMargin);
        if (known != null)
        {
            // The footer of this file was read already, for the columns: it is not read again, and a failure is not reported
            // twice. When the sample of a limit leaves the file out, it takes the place of the last file of the sample, so
            // that the limit holds.
            int index = known.Value;
            if (!sample.Remove(index) && sample.Count > 0)
            {
                sample.RemoveAt(sample.Count - 1);
            }

            if (knownFooter != null)
            {
                fileIssues[index] = Absorb(files[index], collected[index], index, knownFooter, aggregate);
            }
        }
        int workers = Math.Min(options.Parallelism, sample.Count);
        var tasks = new List<Task>(workers);
        for (int w = 0; w < workers; w++)
        {
            tasks.Add(Task.Run(
                async () =>
                {
                    int position;
                    while ((position = Interlocked.Increment(ref next)) < sample.Count)
                    {
                        int index = sample[position];
                        fileIssues[index] = await ReadFooterAsync(table.Location, files[index], collected[index], index, aggregate, planner, cancellation).ConfigureAwait(false);
                    }
                },
                cancellation));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (CollectedIssue? issue in fileIssues)
        {
            if (issue != null)
            {
                issues.Add(issue.Code, issue.Message, issue.Path);
            }
        }
    }

    private async Task<CollectedIssue?> ReadFooterAsync(
        TableLocation location,
        LiveFile file,
        CollectedFile collected,
        int index,
        FooterAggregate aggregate,
        FooterTailPlanner planner,
        CancellationToken cancellation)
    {
        if (file.IsOutsideTable)
        {
            return new CollectedIssue(IssueCodes.FileOutsideTable, "The file is outside the table folder, so its footer was not read.", collected.Path);
        }

        if (file.Size == null)
        {
            return new CollectedIssue(IssueCodes.FooterNotRead, "The log gives no size for the file, so its footer was not read.", collected.Path);
        }

        ParquetFooter footer;
        try
        {
            footer = await footerReader.ReadAsync(location.Path + "/" + file.Path, file.Size.Value, planner.Current, cancellation).ConfigureAwait(false);
            planner.Observe(footer.FooterLength);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return FooterFailure(e, collected.Path);
        }

        return Absorb(file, collected, index, footer, aggregate);
    }

    private static CollectedIssue? Absorb(LiveFile file, CollectedFile collected, int index, ParquetFooter footer, FooterAggregate aggregate)
    {
        if (footer.RowGroups.Any(g => g.Columns.Any(c => !c.HasMetadata)))
        {
            return new CollectedIssue(IssueCodes.FooterColumnsEncrypted, "Column chunks of the file have no metadata, as encrypted columns, so the footer counts as not read.", collected.Path);
        }

        aggregate.Absorb(index, file, collected, footer);
        return null;
    }

    /// <summary>
    /// The issue of a footer read that failed. This is the one place that maps a failure to a code, as the table in the
    /// README does. Whatever the code, the footer counts as not read: the file keeps what the log says, and
    /// <c>footerCount</c> and the totals of level 2 leave it out.
    /// </summary>
    /// <remarks>
    /// A <see cref="StorageException"/> with status 401 or 403 gives <see cref="IssueCodes.AccessDenied"/>. Any other
    /// storage failure gives <see cref="IssueCodes.FooterNotRead"/>: 404, another status after the retries, a timeout,
    /// a broken connection, an error of a local file. A footer that the reader refuses gives the code of its
    /// <see cref="ParquetFooterException"/>: <see cref="IssueCodes.FooterInvalid"/>, also for a file shorter than the log
    /// says and for 416, <see cref="IssueCodes.FooterEncrypted"/>, or <see cref="IssueCodes.FooterTooLarge"/>. Any other
    /// exception gives <see cref="IssueCodes.FooterNotRead"/>.
    /// </remarks>
    private static CollectedIssue FooterFailure(Exception failure, string path) => failure switch
    {
        StorageException storage when storage.IsAccessDenied => new CollectedIssue(IssueCodes.AccessDenied, "The footer could not be read: " + storage.Message, path),
        StorageException storage => new CollectedIssue(IssueCodes.FooterNotRead, "The footer could not be read: " + storage.Message, path),
        ParquetFooterException footer => new CollectedIssue(footer.Code, footer.Message, path),
        _ => new CollectedIssue(IssueCodes.FooterNotRead, "The footer could not be read: " + failure.GetType().Name + ": " + failure.Message, path),
    };

    private void Totals(CollectedTable table, List<LiveFile> files, CollectedFile[] collected, FooterAggregate aggregate)
    {
        Total fileSize = Total.Zero();
        Total rows = Total.Zero();
        Total deleted = Total.Zero();
        long vordered = 0;
        bool vorderKnown = true;
        DateTimeOffset? lastModified = null;
        bool modifiedKnown = files.Count > 0;
        var sizes = new List<long>(files.Count);
        for (int i = 0; i < collected.Length; i++)
        {
            CollectedFile file = collected[i];
            fileSize.Add(file.FileSize);
            rows.Add(file.RowCount);
            deleted.Add(file.DeletedRowCount);
            if (file.FileSize != null)
            {
                sizes.Add(file.FileSize.Value);
            }

            if (file.IsVOrdered == null)
            {
                vorderKnown = false;
            }
            else if (file.IsVOrdered.Value)
            {
                vordered++;
            }

            if (file.ModifiedAt == null)
            {
                modifiedKnown = false;
            }
            else if (lastModified == null || file.ModifiedAt > lastModified)
            {
                lastModified = file.ModifiedAt;
            }

            if (file.Partition != null)
            {
                CollectedPartition partition = table.Partitions[file.Partition.Value];
                partition.FileCount++;
                partition.FileSize.Add(file.FileSize);
                partition.RowCount.Add(file.RowCount);
                partition.DeletedRowCount.Add(file.DeletedRowCount);
                partition.AddModifiedAt(file.ModifiedAt);
            }
        }

        table.FileCount = files.Count;
        table.FileSize = fileSize.Value;
        table.RowCount = rows.Value;
        table.DeletedRowCount = deleted.Value;
        table.LastModifiedAt = modifiedKnown ? lastModified : null;
        table.VOrderedFileCount = vorderKnown ? vordered : (long?)null;
        table.FileSizes = sizes.Count == files.Count ? CollectedDistribution.Of(sizes) : null;
        if (table.PartitionFields != null)
        {
            table.PartitionCount = table.Partitions.Count;
            var partitionSizes = new List<long>();
            bool known = true;
            foreach (CollectedPartition partition in table.Partitions)
            {
                if (partition.FileSize.Value is long size)
                {
                    partitionSizes.Add(size);
                }
                else
                {
                    known = false;
                }
            }

            table.PartitionSizes = known ? CollectedDistribution.Of(partitionSizes) : null;
        }

        bool levelTwo = options.Level >= 2 && (aggregate.FooterCount > 0 || files.Count == 0);
        table.FooterCount = aggregate.FooterCount;
        table.FootersRead = (int)aggregate.FooterCount;
        table.CollectedLevel = levelTwo ? 2 : 1;
        if (levelTwo)
        {
            aggregate.Finish(table);
        }
        else
        {
            LogNullCounts(table, files);
        }

        DetailDepth depth = options.Depth;
        if (!levelTwo && depth > DetailDepth.Files)
        {
            depth = DetailDepth.Files;
        }

        table.Depth = depth;
        if (depth >= DetailDepth.Files)
        {
            table.Files.AddRange(collected);
        }
        else
        {
            table.Partitions.Clear();
        }
    }

    /// <summary>At level 1, the null count of a column is the sum over the statistics of the log, when every file has one.</summary>
    private static void LogNullCounts(CollectedTable table, List<LiveFile> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        bool any = false;
        foreach (CollectedColumn column in table.Columns)
        {
            long sum = 0;
            bool known = true;
            foreach (LiveFile file in files)
            {
                bool found = false;
                if (file.NullCounts != null)
                {
                    foreach (KeyValuePair<string, long> count in file.NullCounts)
                    {
                        if (string.Equals(count.Key, column.FileName, StringComparison.Ordinal))
                        {
                            sum += count.Value;
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    known = false;
                    break;
                }
            }

            if (known)
            {
                column.LogNullCount = sum;
                any = true;
            }
        }

        if (any)
        {
            table.ColumnTotalsSource = "Table format";
        }
    }

    /// <summary>The totals of level 2 over the footers read. <see cref="Absorb"/> is safe to call from several threads.</summary>
    private sealed class FooterAggregate
    {
        private readonly object sync = new object();
        private readonly CollectedTable table;
        private readonly DetailDepth depth;
        private readonly Dictionary<string, CollectedPhysicalSchema> schemas = new Dictionary<string, CollectedPhysicalSchema>(StringComparer.Ordinal);
        private readonly Dictionary<string, CollectedWriter> writers = new Dictionary<string, CollectedWriter>(StringComparer.Ordinal);
        private readonly List<long> rowGroupRows = new List<long>();
        private long rowGroupCount;
        private long compressed;
        private long uncompressed;
        private long footerSize;
        private long stale;

        public FooterAggregate(CollectedTable table, DetailDepth depth)
        {
            this.table = table;
            this.depth = depth;
        }

        public long FooterCount { get; private set; }

        public void Absorb(int index, LiveFile file, CollectedFile collected, ParquetFooter footer)
        {
            bool footerVOrder = false;
            string? level = null;
            foreach (ParquetKeyValue keyValue in footer.KeyValueMetadata)
            {
                if (keyValue.Key.EndsWith("vorder.enabled", StringComparison.OrdinalIgnoreCase))
                {
                    footerVOrder = string.Equals(keyValue.Value, "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (keyValue.Key.EndsWith("vorder.level", StringComparison.OrdinalIgnoreCase))
                {
                    level = keyValue.Value;
                }
            }

            bool vordered = file.VOrderTag == true || footerVOrder;
            string signature = Signature(footer);
            lock (sync)
            {
                if (!schemas.TryGetValue(signature, out CollectedPhysicalSchema? schema))
                {
                    var columns = new List<CollectedPhysicalColumn>(footer.Leaves.Count);
                    foreach (ParquetLeafColumn leaf in footer.Leaves)
                    {
                        columns.Add(new CollectedPhysicalColumn(leaf, MapLeaf(leaf, table.Columns)));
                    }

                    schema = new CollectedPhysicalSchema(index, columns);
                    schemas[signature] = schema;
                }

                schema.FirstFile = Math.Min(schema.FirstFile, index);
                schema.FileCount++;

                string writerKey = (footer.CreatedBy ?? "\u0000") + "\u0001" + (vordered ? "1" : "0") + "\u0001" + (level ?? "\u0000");
                if (!writers.TryGetValue(writerKey, out CollectedWriter? writer))
                {
                    writer = new CollectedWriter(index, footer.CreatedBy, vordered, level);
                    writers[writerKey] = writer;
                }

                writer.FirstFile = Math.Min(writer.FirstFile, index);
                writer.FileCount++;
                writer.FileSize.Add(file.Size);

                collected.IsVOrdered = vordered;
                collected.FooterSize = footer.FooterLength;
                collected.PhysicalSchema = schema;
                collected.Writer = writer;
                collected.RowGroupCount = footer.RowGroups.Count;
                collected.RowCount ??= footer.NumRows;
                List<CollectedRowGroup>? groups = depth >= DetailDepth.RowGroups ? new List<CollectedRowGroup>(footer.RowGroups.Count) : null;
                long fileCompressed = 0;
                long fileUncompressed = 0;
                for (int g = 0; g < footer.RowGroups.Count; g++)
                {
                    ParquetRowGroup group = footer.RowGroups[g];
                    rowGroupCount++;
                    rowGroupRows.Add(group.NumRows);
                    List<CollectedChunk>? chunks = depth >= DetailDepth.ColumnChunks ? new List<CollectedChunk>(group.Columns.Count) : null;
                    long groupCompressed = 0;
                    for (int c = 0; c < group.Columns.Count; c++)
                    {
                        ParquetColumnChunk chunk = group.Columns[c];
                        CollectedPhysicalColumn physical = schema.Columns[c];
                        physical.CompressedSize.Add(chunk.TotalCompressedSize);
                        physical.UncompressedSize.Add(chunk.TotalUncompressedSize);
                        physical.ChunkCount++;
                        groupCompressed += chunk.TotalCompressedSize;
                        fileCompressed += chunk.TotalCompressedSize;
                        fileUncompressed += chunk.TotalUncompressedSize;
                        if (physical.Column == null)
                        {
                            stale += chunk.TotalCompressedSize;
                        }
                        else
                        {
                            AddToColumn(table.Columns[physical.Column.Value], chunk, physical.Leaf);
                        }

                        chunks?.Add(Chunk(c, chunk));
                    }

                    groups?.Add(new CollectedRowGroup
                    {
                        Ordinal = g,
                        RowCount = group.NumRows,
                        UncompressedSize = group.TotalByteSize,
                        CompressedSize = group.TotalCompressedSize ?? groupCompressed,
                        Chunks = chunks,
                    });
                }

                collected.CompressedSize = fileCompressed;
                collected.UncompressedSize = fileUncompressed;
                collected.RowGroups = groups;
                compressed += fileCompressed;
                uncompressed += fileUncompressed;
                footerSize += footer.FooterLength;
                FooterCount++;
            }
        }

        /// <summary>Writes the totals of level 2 into the table, and orders the schemas and the writers by their first file.</summary>
        public void Finish(CollectedTable table)
        {
            table.RowGroupCount = rowGroupCount;
            table.CompressedSize = compressed;
            table.UncompressedSize = uncompressed;
            table.FooterSize = footerSize;
            table.StaleColumnSize = stale;
            table.ColumnTotalsSource = "Footers";
            table.RowGroupRowCounts = CollectedDistribution.Of(rowGroupRows);
            table.PhysicalSchemas.AddRange(schemas.Values.OrderBy(s => s.FirstFile));
            table.Writers.AddRange(writers.Values.OrderBy(w => w.FirstFile));
            for (int i = 0; i < table.Columns.Count; i++)
            {
                CollectedColumn column = table.Columns[i];
                long without = 0;
                foreach (CollectedPhysicalSchema schema in table.PhysicalSchemas)
                {
                    bool stores = schema.Columns.Any(p => p.Column == i);
                    if (stores)
                    {
                        column.IsStored = true;
                    }
                    else
                    {
                        without += schema.FileCount;
                    }
                }

                // A partition column that no file stores has no totals, as the object model says.
                column.FileCountWithoutColumn = column.IsPartitionColumn && !column.IsStored ? (long?)null : without;
            }
        }

        private static void AddToColumn(CollectedColumn column, ParquetColumnChunk chunk, ParquetLeafColumn leaf)
        {
            column.CompressedSize.Add(chunk.TotalCompressedSize);
            column.UncompressedSize.Add(chunk.TotalUncompressedSize);
            column.ValueCount.Add(chunk.NumValues);
            column.NullCount.Add(chunk.NullCount);
            column.ChunkCount.Add(1);
            bool dictionary = chunk.HasDictionaryPage;
            column.DictionaryChunkCount.Add(dictionary ? 1 : 0);
            bool? fallback = Fallback(chunk);
            column.FallbackChunkCount.Add(fallback == null ? (long?)null : fallback.Value ? 1 : 0);
            column.DictionaryPageSize.Add(dictionary ? chunk.DictionaryPageSize : 0);
            if (leaf.PhysicalType == "BYTE_ARRAY")
            {
                column.UnencodedByteArrayDataBytes.Add(chunk.UnencodedByteArrayDataBytes);
            }

            if (chunk.Codec != null)
            {
                column.Codecs.Add(chunk.Codec);
            }

            foreach (string encoding in chunk.Encodings)
            {
                column.Encodings.Add(encoding);
            }

            if (chunk.DistinctCount is long distinct && (column.LargestChunkDistinctCount == null || distinct > column.LargestChunkDistinctCount))
            {
                column.LargestChunkDistinctCount = distinct;
            }
        }

        private static CollectedChunk Chunk(int ordinal, ParquetColumnChunk chunk)
        {
            long? dataPages = null;
            long? dictionaryPages = null;
            if (chunk.EncodingStats != null)
            {
                dataPages = 0;
                dictionaryPages = 0;
                foreach (ParquetPageEncodingStats stats in chunk.EncodingStats)
                {
                    if (stats.IsDataPage)
                    {
                        dataPages += stats.Count;
                        if (stats.IsDictionaryEncoded)
                        {
                            dictionaryPages += stats.Count;
                        }
                    }
                }
            }

            bool dictionary = chunk.HasDictionaryPage;
            return new CollectedChunk(ordinal, SortedEncodings.Of(chunk.Encodings))
            {
                Codec = chunk.Codec,
                ValueCount = chunk.NumValues,
                NullCount = chunk.NullCount,
                CompressedSize = chunk.TotalCompressedSize,
                UncompressedSize = chunk.TotalUncompressedSize,
                HasDictionaryPage = dictionary,
                DictionaryPageSize = dictionary ? chunk.DictionaryPageSize : null,
                DataPageCount = dataPages,
                DictionaryEncodedPageCount = dictionaryPages,
                UnencodedByteArrayDataBytes = chunk.UnencodedByteArrayDataBytes,
                BloomFilterSize = chunk.BloomFilterLength,
                MinValue = chunk.MinValue,
                MaxValue = chunk.MaxValue,
                DistinctCount = chunk.DistinctCount,
            };
        }
    }
}
