using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pqax;

/// <summary>
/// The derived measures of a footprint, as section 10 of the object model defines them: for each table the storage stack
/// with its waste, its overhead, and the share of each component, the file sizes, the small files, and the V-Order
/// shares, the history, the ratios, and the partitions; the measures of each column; and those of the collection. They
/// follow the view model of the storage views, <c>src/typescript/views/src/model.ts</c>, function by function and with
/// its names, so that the numbers are the same. The two measures over a set of columns stay in the views. A value that
/// cannot be computed is null, never 0.
/// </summary>
public static class StorageMeasures
{
    /// <summary>The default size below which a file is small: 128 MiB.</summary>
    public const long DefaultSmallFileSize = 128L * 1024 * 1024;

    /// <summary>The default size below which a file is very small: 8 MiB.</summary>
    public const long DefaultTinyFileSize = 8L * 1024 * 1024;

    private const double DayMilliseconds = 86_400_000;

    /// <summary>The components of a storage stack, in the order the stack draws them.</summary>
    public static IReadOnlyList<string> StackComponents { get; } = new[] { "live", "nonLive", "unreferenced", "deleted", "stale", "metadata", "footers" };

    /// <summary>The components that make the waste: the space that holds no live data.</summary>
    public static IReadOnlyList<string> WasteComponents { get; } = new[] { "nonLive", "unreferenced", "deleted", "stale" };

    /// <summary>The components that make the overhead: the metadata of the table format and the footers of the data files.</summary>
    public static IReadOnlyList<string> OverheadComponents { get; } = new[] { "metadata", "footers" };

    /// <summary><c>a / b</c> when both are known and <paramref name="b"/> is positive, else null.</summary>
    /// <param name="a">The numerator.</param>
    /// <param name="b">The denominator.</param>
    /// <returns>The ratio, or null.</returns>
    public static double? Ratio(double? a, double? b) => a.HasValue && b.HasValue && b.Value > 0 ? a.Value / b.Value : null;

    /// <summary>
    /// The bytes that the deleted rows of a table or of a file hold, estimated from the share of the rows:
    /// <c>fileSize * deletedRowCount / rowCount</c>, with <c>deletedRowCount</c> at most <c>rowCount</c>.
    /// </summary>
    /// <param name="fileSize">The bytes of the files.</param>
    /// <param name="deletedRowCount">The deleted rows.</param>
    /// <param name="rowCount">The rows.</param>
    /// <returns>0 when <paramref name="deletedRowCount"/> is 0, null when a value is missing or <paramref name="rowCount"/> is not positive.</returns>
    public static double? DeletedEstimate(double? fileSize, double? deletedRowCount, double? rowCount)
    {
        if (deletedRowCount == 0)
        {
            return 0;
        }

        if (!fileSize.HasValue || !deletedRowCount.HasValue || !rowCount.HasValue || rowCount.Value <= 0)
        {
            return null;
        }

        return fileSize.Value * Math.Min(deletedRowCount.Value, rowCount.Value) / rowCount.Value;
    }

    /// <summary>
    /// A lower bound of the share of the files below a threshold, from a distribution: 1 when the maximum is below it,
    /// 0.75 when the third quartile is, 0.5 for the median, 0.25 for the first quartile, a share of one file for the
    /// minimum, and 0 otherwise.
    /// </summary>
    /// <param name="count">The number of files.</param>
    /// <param name="minimum">The smallest file.</param>
    /// <param name="firstQuartile">The first quartile.</param>
    /// <param name="median">The median.</param>
    /// <param name="thirdQuartile">The third quartile.</param>
    /// <param name="maximum">The largest file.</param>
    /// <param name="threshold">The size in bytes.</param>
    /// <returns>The share, or null when the distribution has no quartile.</returns>
    public static double? SmallShareAtLeast(double? count, double? minimum, double? firstQuartile, double? median, double? thirdQuartile, double? maximum, double threshold)
    {
        if (maximum < threshold)
        {
            return 1;
        }

        if (thirdQuartile < threshold)
        {
            return 0.75;
        }

        if (median < threshold)
        {
            return 0.5;
        }

        if (firstQuartile < threshold)
        {
            return 0.25;
        }

        if (minimum < threshold)
        {
            return count > 0 ? 1 / count.Value : 0;
        }

        return !minimum.HasValue && !firstQuartile.HasValue && !median.HasValue && !thirdQuartile.HasValue && !maximum.HasValue ? null : 0;
    }

    /// <summary>Commits per day: the commits read over the days from the oldest to the newest of them, at least one day.</summary>
    /// <param name="commits">The commits that the span holds: <c>commitsRead</c>.</param>
    /// <param name="oldestCommitAt">The oldest of them, <c>oldestCommitAt</c>.</param>
    /// <param name="newestCommitAt">The newest of them.</param>
    /// <returns>The rate, or null when a value is missing.</returns>
    public static double? CommitsPerDay(double? commits, DateTimeOffset? oldestCommitAt, DateTimeOffset? newestCommitAt)
    {
        if (!commits.HasValue || !oldestCommitAt.HasValue || !newestCommitAt.HasValue)
        {
            return null;
        }

        return commits.Value / Math.Max(1, (newestCommitAt.Value.ToUnixTimeMilliseconds() - oldestCommitAt.Value.ToUnixTimeMilliseconds()) / DayMilliseconds);
    }

    /// <summary>Whole days from a time to a later one, at least 0.</summary>
    /// <param name="from">The start.</param>
    /// <param name="to">The end.</param>
    /// <returns>The days, or null when a time is missing.</returns>
    public static long? DaysBetween(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (!from.HasValue || !to.HasValue)
        {
            return null;
        }

        return Math.Max(0, (long)Math.Floor((to.Value.ToUnixTimeMilliseconds() - from.Value.ToUnixTimeMilliseconds()) / DayMilliseconds));
    }

    /// <summary>
    /// A storage stack from its inputs, as section 10.2 of the object model defines it. A null input is an absent
    /// component. When the unreferenced files are known, the non-live component is the part of them that the log still
    /// tracks, at most: the smaller of the two sizes, and the unreferenced component is the rest. The sums count an absent
    /// component as 0 and are null when every component they add is absent; the data, and so the shares, are null when
    /// the live data is.
    /// </summary>
    /// <param name="input">The inputs.</param>
    /// <returns>The stack.</returns>
    public static StorageStack StackOf(StackInput input)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        double? fileSize = input.FileSize;
        double? deleted = input.Deleted;
        double? footerSize = input.FooterSize;
        double? staleColumnSize = input.StaleColumnSize;
        double? nonLiveFileSize = input.NonLiveFileSize;
        double? unreferencedFileSize = input.UnreferencedFileSize;
        double? live = fileSize.HasValue ? Math.Max(0, fileSize.Value - (deleted ?? 0) - (footerSize ?? 0) - (staleColumnSize ?? 0)) : null;
        bool listed = unreferencedFileSize.HasValue;
        double? nonLive = listed && nonLiveFileSize.HasValue ? Math.Min(nonLiveFileSize.Value, unreferencedFileSize!.Value) : nonLiveFileSize;
        double? unreferenced = listed ? unreferencedFileSize!.Value - (nonLive ?? 0) : null;

        // The sums go in the order of the components, from the first present, as the views add them.
        double? waste = SumKnown(nonLive, unreferenced, deleted, staleColumnSize);
        double? overhead = SumKnown(input.MetadataSize, footerSize);
        double? data = live.HasValue ? live.Value + (waste ?? 0) : null;
        return new StorageStack(live, nonLive, unreferenced, deleted, staleColumnSize, input.MetadataSize, footerSize,
            deleted.HasValue ? input.DeletedSource : null, listed, data, SumKnown(live, waste, overhead), waste, overhead);
    }

    /// <summary>
    /// The measures of a document: the summary, and the <c>files</c> and <c>partitions</c> tables when the manifest states
    /// rows for them. A detail table that cannot be read is left out with a warning, and the measures use the totals of
    /// the summary.
    /// </summary>
    /// <param name="document">An open document.</param>
    /// <param name="options">The thresholds of the small files; null for the defaults.</param>
    /// <returns>The measures of every table, of every column, and of the collection.</returns>
    public static FootprintMeasures Compute(PqaxDocument document, MeasureOptions? options = null)
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        var warnings = new List<string>();
        IReadOnlyList<FileRow>? files = Detail<FileRow>(document, "files", document.ReadFiles, warnings);
        IReadOnlyList<PartitionRow>? partitions = Detail<PartitionRow>(document, "partitions", document.ReadPartitions, warnings);
        IReadOnlyList<TableStorage> tables = ForTables(document.Summary, files, document.Manifest.Collection, options, partitions);
        return new FootprintMeasures(tables, ForColumns(document.Summary), ForCollection(document.Summary, tables), files != null && files.Count > 0, warnings);
    }

    /// <summary>A detail table when the manifest states rows for it, else null; a warning when it cannot be read.</summary>
    private static IReadOnlyList<T>? Detail<T>(PqaxDocument document, string name, Func<IReadOnlyList<T>> read, List<string> warnings)
    {
        if (document.RowCount(name) <= 0)
        {
            return null;
        }

        try
        {
            return read();
        }
        catch (Exception e) when (e is PqaxFormatException || e is IOException)
        {
            string code = e is PqaxFormatException format ? $" ({format.Code})" : string.Empty;
            warnings.Add($"The {name} table could not be read{code}. The measures use the totals of the summary.");
            return null;
        }
    }

    /// <summary>The measures of the rows of <c>tables</c>, in the order of the file.</summary>
    /// <param name="summary">The summary tables.</param>
    /// <param name="files">The <c>files</c> table, or null when the file does not have it or it was not read.</param>
    /// <param name="collection">The collection of the manifest, for the days since the last commit; null when unknown.</param>
    /// <param name="options">The thresholds of the small files; null for the defaults.</param>
    /// <param name="partitions">The <c>partitions</c> table, or null when the file does not have it or it was not read.</param>
    /// <returns>One <see cref="TableStorage"/> for each row of <c>tables</c>.</returns>
    public static IReadOnlyList<TableStorage> ForTables(Summary summary, IReadOnlyList<FileRow>? files, CollectionInfo? collection, MeasureOptions? options = null, IReadOnlyList<PartitionRow>? partitions = null)
    {
        if (summary == null)
        {
            throw new ArgumentNullException(nameof(summary));
        }

        long threshold = options != null && options.SmallFileSize > 0 ? options.SmallFileSize : DefaultSmallFileSize;
        long tinyThreshold = options != null && options.TinyFileSize > 0 ? options.TinyFileSize : DefaultTinyFileSize;
        Dictionary<long, FilesOfTable> byTable = FilesByTable(files);
        Dictionary<long, PartitionsOfTable> partitionsByTable = PartitionsByTable(partitions);
        DateTimeOffset? collectedAt = collection?.CompletedAt ?? collection?.StartedAt;

        var operationsByTable = new Dictionary<long, List<OperationRow>>();
        foreach (OperationRow operation in summary.Operations)
        {
            if (IsId(operation.Table) && operation.Name != null)
            {
                Add(operationsByTable, operation.Table!.Value, operation);
            }
        }

        var columnsByTable = new Dictionary<long, List<ColumnRow>>();
        foreach (ColumnRow column in summary.Columns)
        {
            if (IsId(column.Table))
            {
                Add(columnsByTable, column.Table!.Value, column);
            }
        }

        var seen = new HashSet<long>();
        var result = new List<TableStorage>(summary.Tables.Count);
        for (int row = 0; row < summary.Tables.Count; row++)
        {
            TableRow table = summary.Tables[row];
            long? validId = table.Id > 0 && seen.Add(table.Id) ? table.Id : null;
            double? fileCount = Size(table.FileCount);
            double? fileSize = Size(table.FileSize);
            double? rowCount = Size(table.RowCount);
            double? deletedRowCount = Size(table.DeletedRowCount);
            double? compressedSize = Size(table.CompressedSize);
            double? uncompressedSize = Size(table.UncompressedSize);
            double? staleColumnSize = Size(table.StaleColumnSize);
            double? nonLiveFileSize = Size(table.NonLiveFileSize);
            FilesOfTable? fromFiles = validId.HasValue && byTable.TryGetValue(validId.Value, out FilesOfTable? found) ? found : null;

            // The deleted rows: from the files when every file of the table that has a size gives an estimate.
            double? deleted = DeletedEstimate(fileSize, deletedRowCount, rowCount);
            string? deletedSource = deleted.HasValue ? "table" : null;
            if (fromFiles != null && fromFiles.DeletedKnown > 0 && fromFiles.DeletedKnown == fromFiles.Sizes.Count)
            {
                deleted = fromFiles.Deleted;
                deletedSource = "files";
            }

            StorageStack stack = StackOf(new StackInput
            {
                FileSize = fileSize,
                Deleted = deleted,
                FooterSize = Size(table.FooterSize),
                StaleColumnSize = staleColumnSize,
                NonLiveFileSize = nonLiveFileSize,
                UnreferencedFileSize = Size(table.UnreferencedFileSize),
                MetadataSize = Size(table.MetadataSize),
                DeletedSource = deletedSource,
            });

            // The file sizes and the small files: from the files, else from fileSizes, whose quartiles bound the small files.
            SmallFiles smallFiles;
            SmallFiles tinyFiles;
            double? sizeCount;
            double? sum;
            double? minimum;
            double? firstQuartile;
            double? median;
            double? thirdQuartile;
            double? maximum;
            if (fromFiles != null && fromFiles.Sizes.Count > 0)
            {
                List<double> sorted = fromFiles.Sizes;
                int n = sorted.Count;
                sizeCount = n;
                sum = 0;
                foreach (double value in sorted)
                {
                    sum += value;
                }

                // The quartiles by position, as the collector takes them: sorted[n/4], sorted[n/2], sorted[3n/4].
                minimum = sorted[0];
                firstQuartile = sorted[n / 4];
                median = sorted[n / 2];
                thirdQuartile = sorted[3 * n / 4];
                maximum = sorted[n - 1];
                smallFiles = Below(sorted, threshold);
                tinyFiles = Below(sorted, tinyThreshold);
            }
            else
            {
                Distribution? sizes = table.FileSizes;
                sizeCount = Member(sizes?.Count) ?? fileCount;
                sum = Member(sizes?.Sum) ?? fileSize;
                minimum = Member(sizes?.Minimum);
                firstQuartile = Member(sizes?.FirstQuartile);
                median = Member(sizes?.Median);
                thirdQuartile = Member(sizes?.ThirdQuartile);
                maximum = Member(sizes?.Maximum);
                smallFiles = new SmallFiles(threshold, null, SmallShareAtLeast(sizeCount, minimum, firstQuartile, median, thirdQuartile, maximum, threshold));
                tinyFiles = new SmallFiles(tinyThreshold, null, SmallShareAtLeast(sizeCount, minimum, firstQuartile, median, thirdQuartile, maximum, tinyThreshold));
            }

            // The V-Order shares: from the files when one of them says, else from vOrderedFileCount over fileCount.
            double? vOrderShare = null;
            double? vOrderBytesShare = null;
            if (fromFiles != null && fromFiles.VOrderKnown > 0)
            {
                vOrderShare = (double)fromFiles.VOrdered / fromFiles.VOrderKnown;
                vOrderBytesShare = Ratio(fromFiles.VOrderedBytes, fromFiles.VOrderKnownBytes);
            }
            else
            {
                double? vOrdered = Size(table.VOrderedFileCount);
                if (vOrdered.HasValue && fileCount > 0)
                {
                    vOrderShare = Math.Min(1, vOrdered.Value / fileCount.Value);
                }
            }

            var fileMeasures = new FileMeasures(Whole(sizeCount), sum, minimum, firstQuartile, median, thirdQuartile, maximum, vOrderShare, vOrderBytesShare);

            // The history: the pace of the commits, and the VACUUM and OPTIMIZE among the operations, null without any.
            DateTimeOffset? lastOperationAt = null;
            DateTimeOffset? lastVacuumAt = null;
            DateTimeOffset? lastOptimizeAt = null;
            bool? hasVacuum = null;
            bool? hasOptimize = null;
            if (validId.HasValue && operationsByTable.TryGetValue(validId.Value, out List<OperationRow>? operations))
            {
                hasVacuum = false;
                hasOptimize = false;
                foreach (OperationRow operation in operations)
                {
                    lastOperationAt = Later(lastOperationAt, operation.LastAt);
                    string name = operation.Name!.ToUpperInvariant();
                    if (name.Contains("VACUUM"))
                    {
                        hasVacuum = true;
                        lastVacuumAt = Later(lastVacuumAt, operation.LastAt);
                    }

                    if (name.Contains("OPTIMIZE"))
                    {
                        hasOptimize = true;
                        lastOptimizeAt = Later(lastOptimizeAt, operation.LastAt);
                    }
                }
            }

            DateTimeOffset? lastCommitAt = table.SnapshotTimestamp ?? lastOperationAt ?? table.LastModifiedAt;
            var maintenance = new MaintenanceMeasures(
                lastCommitAt,
                CommitsPerDay(Size(table.CommitsRead), table.OldestCommitAt, lastCommitAt),
                DaysBetween(lastCommitAt, collectedAt),
                hasVacuum,
                lastVacuumAt,
                hasOptimize,
                lastOptimizeAt);

            // The partitions: partitionCount, else the partitions kept.
            PartitionsOfTable? kept = validId.HasValue && partitionsByTable.TryGetValue(validId.Value, out PartitionsOfTable? foundPartitions) ? foundPartitions : null;
            var partitionMeasures = new PartitionMeasures(Whole(table.PartitionCount) ?? kept?.Kept, kept?.Kept ?? 0, kept?.Oldest, kept?.Newest);

            // The ratios.
            Distribution? rowGroups = table.RowGroupRowCounts;
            double? footerCount = Size(table.FooterCount);
            Distribution? partitionSizes = table.PartitionSizes;
            double? fallback = null;
            double? dictionary = null;
            if (validId.HasValue && columnsByTable.TryGetValue(validId.Value, out List<ColumnRow>? columns))
            {
                foreach (ColumnRow column in columns)
                {
                    double? f = Size(column.FallbackChunkCount);
                    double? d = Size(column.DictionaryChunkCount);
                    if (f.HasValue && d.HasValue)
                    {
                        fallback = (fallback ?? 0) + f.Value;
                        dictionary = (dictionary ?? 0) + d.Value;
                    }
                }
            }

            var measures = new TableMeasures(
                Ratio(uncompressedSize, compressedSize),
                Ratio(deletedRowCount, rowCount),
                Ratio(fileSize, fileCount),
                Ratio(Member(rowGroups?.Sum), Member(rowGroups?.Count))
                    ?? (footerCount.HasValue && footerCount == fileCount ? Ratio(rowCount, Size(table.RowGroupCount)) : null),
                Ratio(footerCount, fileCount),
                Ratio(staleColumnSize, compressedSize),
                fileSize.HasValue && nonLiveFileSize.HasValue ? Ratio(nonLiveFileSize, fileSize.Value + nonLiveFileSize.Value) : null,
                Ratio(Member(partitionSizes?.Sum), Member(partitionSizes?.Count)) ?? Ratio(fileSize, Size(table.PartitionCount)),
                Ratio(fallback, dictionary));

            result.Add(new TableStorage(validId, row, table.Name, Label(table, row, validId), Whole(table.RowCount), Whole(table.FileCount), Whole(table.FileSize),
                stack, smallFiles, tinyFiles, fileMeasures, measures, maintenance, partitionMeasures));
        }

        return result;
    }

    /// <summary>The measures of the rows of <c>columns</c>, in the order of the file, as section 10.6 defines them.</summary>
    /// <param name="summary">The summary tables.</param>
    /// <returns>One <see cref="ColumnStorage"/> for each row of <c>columns</c>.</returns>
    public static IReadOnlyList<ColumnStorage> ForColumns(Summary summary)
    {
        if (summary == null)
        {
            throw new ArgumentNullException(nameof(summary));
        }

        // The footers read of each table, by the first row that has its id.
        var footerCountById = new Dictionary<long, double?>();
        foreach (TableRow table in summary.Tables)
        {
            if (table.Id > 0 && !footerCountById.ContainsKey(table.Id))
            {
                footerCountById.Add(table.Id, Size(table.FooterCount));
            }
        }

        var seen = new HashSet<long>();
        var result = new List<ColumnStorage>(summary.Columns.Count);
        for (int row = 0; row < summary.Columns.Count; row++)
        {
            ColumnRow column = summary.Columns[row];
            long? validId = column.Id > 0 && seen.Add(column.Id) ? column.Id : null;
            long? table = IsId(column.Table) && footerCountById.ContainsKey(column.Table!.Value) ? column.Table : null;
            double? valueCount = Size(column.ValueCount);
            double? nullCount = Size(column.NullCount);
            double? dictionaryChunkCount = Size(column.DictionaryChunkCount);
            double? nonNull = valueCount.HasValue && nullCount.HasValue ? valueCount.Value - nullCount.Value : null;
            double? footerCount = table.HasValue ? footerCountById[table.Value] : null;
            result.Add(new ColumnStorage(
                validId,
                row,
                table,
                column.Name,
                column.Name ?? (validId.HasValue ? $"Column {validId.Value}" : $"Column at row {row + 1}"),
                nullCount.HasValue && valueCount > 0 ? Math.Min(1, nullCount.Value / valueCount!.Value) : null,
                Ratio(Size(column.UncompressedSize), Size(column.CompressedSize)),
                Ratio(dictionaryChunkCount, Size(column.ChunkCount)),
                Ratio(Size(column.FallbackChunkCount), dictionaryChunkCount),
                Ratio(Size(column.UnencodedByteArrayDataBytes), nonNull),
                Ratio(Size(column.FileCountWithoutColumn), footerCount)));
        }

        return result;
    }

    /// <summary>The measures of the collection of section 10.7: the counts, the sums over the tables, and the depths.</summary>
    /// <param name="summary">The summary tables.</param>
    /// <param name="tables">The measures of the tables, from <see cref="ForTables"/>.</param>
    /// <returns>The measures of the collection.</returns>
    public static CollectionMeasures ForCollection(Summary summary, IReadOnlyList<TableStorage> tables)
    {
        if (summary == null)
        {
            throw new ArgumentNullException(nameof(summary));
        }

        if (tables == null)
        {
            throw new ArgumentNullException(nameof(tables));
        }

        var sources = new HashSet<long>(summary.Sources.Where(source => source.Id > 0).Select(source => source.Id));
        var depths = new Dictionary<string, long>();
        var order = new List<string>();
        foreach (TableRow table in summary.Tables)
        {
            string depth = table.DetailDepth ?? "Unknown";
            if (depths.TryGetValue(depth, out long count))
            {
                depths[depth] = count + 1;
            }
            else
            {
                depths.Add(depth, 1);
                order.Add(depth);
            }
        }

        return new CollectionMeasures(
            sources.Count,
            summary.Tables.Count,
            summary.Columns.Count,
            SumWhole(tables.Select(t => t.FileCount)),
            SumWhole(tables.Select(t => t.FileSize)),
            SumWhole(tables.Select(t => t.RowCount)),
            SumKnown(tables.Select(t => t.Stack.Total).ToArray()),
            SumKnown(tables.Select(t => t.Stack.Waste).ToArray()),
            SumKnown(tables.Select(t => t.Stack.Overhead).ToArray()),
            order.Select(depth => new KeyValuePair<string, long>(depth, depths[depth])).ToList());
    }

    /// <summary>What the <c>files</c> table says about one table.</summary>
    private sealed class FilesOfTable
    {
        public List<double> Sizes { get; } = new List<double>();

        public long VOrdered { get; set; }

        public long VOrderKnown { get; set; }

        public double VOrderedBytes { get; set; }

        public double VOrderKnownBytes { get; set; }

        public double Deleted { get; set; }

        public int DeletedKnown { get; set; }
    }

    /// <summary>What the <c>partitions</c> table says about one table: the rows, and the range of <c>lastModifiedAt</c>.</summary>
    private sealed class PartitionsOfTable
    {
        public long Kept { get; set; }

        public DateTimeOffset? Oldest { get; set; }

        public DateTimeOffset? Newest { get; set; }
    }

    private static Dictionary<long, PartitionsOfTable> PartitionsByTable(IReadOnlyList<PartitionRow>? partitions)
    {
        var result = new Dictionary<long, PartitionsOfTable>();
        if (partitions == null)
        {
            return result;
        }

        foreach (PartitionRow partition in partitions)
        {
            if (!IsId(partition.Table))
            {
                continue;
            }

            if (!result.TryGetValue(partition.Table!.Value, out PartitionsOfTable? entry))
            {
                entry = new PartitionsOfTable();
                result.Add(partition.Table.Value, entry);
            }

            entry.Kept++;
            DateTimeOffset? modified = partition.LastModifiedAt;
            if (modified.HasValue)
            {
                entry.Newest = Later(entry.Newest, modified);
                entry.Oldest = !entry.Oldest.HasValue || modified.Value < entry.Oldest.Value ? modified : entry.Oldest;
            }
        }

        return result;
    }

    private static Dictionary<long, FilesOfTable> FilesByTable(IReadOnlyList<FileRow>? files)
    {
        var result = new Dictionary<long, FilesOfTable>();
        if (files == null)
        {
            return result;
        }

        foreach (FileRow file in files)
        {
            if (!IsId(file.Table))
            {
                continue;
            }

            if (!result.TryGetValue(file.Table!.Value, out FilesOfTable? entry))
            {
                entry = new FilesOfTable();
                result.Add(file.Table.Value, entry);
            }

            double? fileSize = Size(file.FileSize);
            if (fileSize.HasValue)
            {
                entry.Sizes.Add(fileSize.Value);
            }

            if (file.IsVOrdered.HasValue)
            {
                entry.VOrderKnown++;
                entry.VOrderKnownBytes += fileSize ?? 0;
                if (file.IsVOrdered.Value)
                {
                    entry.VOrdered++;
                    entry.VOrderedBytes += fileSize ?? 0;
                }
            }

            // Only a file with a size counts, so that the files that give an estimate are among the sizes.
            double? estimate = fileSize.HasValue ? DeletedEstimate(fileSize, Size(file.DeletedRowCount), Size(file.RowCount)) : null;
            if (estimate.HasValue)
            {
                entry.Deleted += estimate.Value;
                entry.DeletedKnown++;
            }
        }

        // Sorted, for the quartiles by position and for the sum, which the views add from the smallest.
        foreach (FilesOfTable entry in result.Values)
        {
            entry.Sizes.Sort();
        }

        return result;
    }

    private static SmallFiles Below(List<double> sizes, long limit)
    {
        long count = sizes.Count(value => value < limit);
        return new SmallFiles(limit, count, (double)count / sizes.Count);
    }

    private static string Label(TableRow table, int row, long? id)
    {
        var parts = new List<string>();
        if (table.Namespace != null)
        {
            parts.AddRange(table.Namespace.Where(part => part != null));
        }

        parts.Add(table.Name ?? (id.HasValue ? $"Table {id.Value}" : $"Table at row {row + 1}"));
        return string.Join(".", parts);
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (!a.HasValue)
        {
            return b;
        }

        if (!b.HasValue)
        {
            return a;
        }

        return b.Value > a.Value ? b : a;
    }

    private static void Add<T>(Dictionary<long, List<T>> map, long key, T value)
    {
        if (!map.TryGetValue(key, out List<T>? list))
        {
            list = new List<T>();
            map.Add(key, list);
        }

        list.Add(value);
    }

    private static bool IsId(long? value) => value > 0;

    /// <summary>The sum of the values that are present, in their order, null when none is.</summary>
    private static double? SumKnown(params double?[] values)
    {
        double? total = null;
        foreach (double? value in values)
        {
            if (value.HasValue)
            {
                total = (total ?? 0) + value.Value;
            }
        }

        return total;
    }

    /// <summary>A size or a count: a value that is not negative, else null.</summary>
    private static double? Size(long? value) => value >= 0 ? value.Value : null;

    private static long? Whole(long? value) => value >= 0 ? value : null;

    private static long? Whole(double? value) => value >= 0 ? (long)value.Value : null;

    /// <summary>The sum of the values that are present, null when none is.</summary>
    private static long? SumWhole(IEnumerable<long?> values)
    {
        long? total = null;
        foreach (long? value in values)
        {
            if (value.HasValue)
            {
                total = (total ?? 0) + value.Value;
            }
        }

        return total;
    }

    /// <summary>A member of a distribution, as a size.</summary>
    private static double? Member(long? value) => Size(value);
}
