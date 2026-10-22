using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pqax;

/// <summary>
/// The derived measures of the tables of a footprint: the storage stack with its waste and overhead, the deleted rows,
/// the small files, the ratios, and the pace of the commits, as section 10 of the object model defines them. They follow
/// the view model of the storage views, <c>src/typescript/views/src/model.ts</c>, function by function and with its
/// names, so that the numbers are the same. A value that cannot be computed is null, never 0.
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
    /// The measures of a document: the summary, and the <c>files</c> table when the manifest states rows for it. A
    /// <c>files</c> table that cannot be read is left out with a warning, and the measures use the totals of the summary.
    /// </summary>
    /// <param name="document">An open document.</param>
    /// <param name="options">The thresholds of the small files; null for the defaults.</param>
    /// <returns>The measures of every table.</returns>
    public static FootprintMeasures Compute(PqaxDocument document, MeasureOptions? options = null)
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        var warnings = new List<string>();
        IReadOnlyList<FileRow>? files = null;
        if (document.RowCount("files") > 0)
        {
            try
            {
                files = document.ReadFiles();
            }
            catch (Exception e) when (e is PqaxFormatException || e is IOException)
            {
                string code = e is PqaxFormatException format ? $" ({format.Code})" : string.Empty;
                warnings.Add($"The files table could not be read{code}. The measures use the totals of the summary.");
            }
        }

        IReadOnlyList<TableStorage> tables = ForTables(document.Summary, files, document.Manifest.Collection, options);
        return new FootprintMeasures(tables, files != null && files.Count > 0, warnings);
    }

    /// <summary>The measures of the rows of <c>tables</c>, in the order of the file.</summary>
    /// <param name="summary">The summary tables.</param>
    /// <param name="files">The <c>files</c> table, or null when the file does not have it or it was not read.</param>
    /// <param name="collection">The collection of the manifest, for the days since the last commit; null when unknown.</param>
    /// <param name="options">The thresholds of the small files; null for the defaults.</param>
    /// <returns>One <see cref="TableStorage"/> for each row of <c>tables</c>.</returns>
    public static IReadOnlyList<TableStorage> ForTables(Summary summary, IReadOnlyList<FileRow>? files, CollectionInfo? collection, MeasureOptions? options = null)
    {
        if (summary == null)
        {
            throw new ArgumentNullException(nameof(summary));
        }

        long threshold = options != null && options.SmallFileSize > 0 ? options.SmallFileSize : DefaultSmallFileSize;
        long tinyThreshold = options != null && options.TinyFileSize > 0 ? options.TinyFileSize : DefaultTinyFileSize;
        Dictionary<long, FilesOfTable> byTable = FilesByTable(files);
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

            // The small files: counted from the files, else bounded from the quartiles of fileSizes.
            SmallFiles smallFiles;
            SmallFiles tinyFiles;
            if (fromFiles != null && fromFiles.Sizes.Count > 0)
            {
                smallFiles = Below(fromFiles.Sizes, threshold);
                tinyFiles = Below(fromFiles.Sizes, tinyThreshold);
            }
            else
            {
                Distribution? sizes = table.FileSizes;
                double? count = Member(sizes?.Count) ?? fileCount;
                double? minimum = Member(sizes?.Minimum);
                double? firstQuartile = Member(sizes?.FirstQuartile);
                double? median = Member(sizes?.Median);
                double? thirdQuartile = Member(sizes?.ThirdQuartile);
                double? maximum = Member(sizes?.Maximum);
                smallFiles = new SmallFiles(threshold, null, SmallShareAtLeast(count, minimum, firstQuartile, median, thirdQuartile, maximum, threshold));
                tinyFiles = new SmallFiles(tinyThreshold, null, SmallShareAtLeast(count, minimum, firstQuartile, median, thirdQuartile, maximum, tinyThreshold));
            }

            // The pace of the commits.
            DateTimeOffset? lastOperationAt = null;
            if (validId.HasValue && operationsByTable.TryGetValue(validId.Value, out List<OperationRow>? operations))
            {
                foreach (OperationRow operation in operations)
                {
                    lastOperationAt = Later(lastOperationAt, operation.LastAt);
                }
            }

            DateTimeOffset? lastCommitAt = table.SnapshotTimestamp ?? lastOperationAt ?? table.LastModifiedAt;
            var maintenance = new MaintenanceMeasures(
                lastCommitAt,
                CommitsPerDay(Size(table.CommitsRead), table.OldestCommitAt, lastCommitAt),
                DaysBetween(lastCommitAt, collectedAt));

            // The ratios.
            Distribution? rowGroups = table.RowGroupRowCounts;
            double? footerCount = Size(table.FooterCount);
            Distribution? partitions = table.PartitionSizes;
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
                Ratio(Member(partitions?.Sum), Member(partitions?.Count)) ?? Ratio(fileSize, Size(table.PartitionCount)),
                Ratio(fallback, dictionary));

            result.Add(new TableStorage(validId, row, table.Name, Label(table, row, validId), Whole(table.RowCount), Whole(table.FileCount), Whole(table.FileSize),
                stack, smallFiles, tinyFiles, measures, maintenance));
        }

        return result;
    }

    /// <summary>What the <c>files</c> table says about one table.</summary>
    private sealed class FilesOfTable
    {
        public List<double> Sizes { get; } = new List<double>();

        public double Deleted { get; set; }

        public int DeletedKnown { get; set; }
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

            // Only a file with a size counts, so that the files that give an estimate are among the sizes.
            double? estimate = fileSize.HasValue ? DeletedEstimate(fileSize, Size(file.DeletedRowCount), Size(file.RowCount)) : null;
            if (estimate.HasValue)
            {
                entry.Deleted += estimate.Value;
                entry.DeletedKnown++;
            }
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

    /// <summary>A member of a distribution, as a size.</summary>
    private static double? Member(long? value) => Size(value);
}
