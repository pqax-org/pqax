using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Pqax;

/// <summary>
/// The comparison of two footprints of the same lakehouse taken at different times, as section 13 of the object model
/// defines it. A table is the same table when its TableId is, and otherwise, when one of the two has none, when its
/// source, namespace, and name are; a column when its FieldId is, and otherwise by name; a partition by its values, a
/// file by its path, and a commit by its CommitId. Each kept table and column has the change of each total of section 6
/// and of each measure of section 10, with a null delta where either footprint lacks the value.
/// </summary>
public static class StorageDiff
{
    /// <summary>The orders of <see cref="Rank"/> and of <c>pqax diff --sort</c>. The default is <c>total</c>.</summary>
    public static IReadOnlyList<string> Orders { get; } = new[] { "total", "waste", "rows", "files", "name" };

    private static readonly string[] Members = { "count", "sum", "minimum", "firstQuartile", "median", "thirdQuartile", "maximum" };

    private static readonly HashSet<string> Times = new()
    {
        "lastModifiedAt", "oldestCommitAt", "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt", "maintenance.lastCommitAt", "maintenance.lastVacuumAt",
        "maintenance.lastOptimizeAt", "partitions.oldestModifiedAt", "partitions.newestModifiedAt",
    };

    /// <summary>Compares two open documents, the older and the newer.</summary>
    /// <param name="older">The older footprint.</param>
    /// <param name="newer">The newer footprint.</param>
    /// <param name="options">The thresholds of the small files, for the measures of both; null for the defaults.</param>
    /// <returns>The tables added, removed, and kept, with the change of every total and measure.</returns>
    public static FootprintDiff Compare(PqaxDocument older, PqaxDocument newer, MeasureOptions? options = null)
    {
        if (older == null)
        {
            throw new ArgumentNullException(nameof(older));
        }

        if (newer == null)
        {
            throw new ArgumentNullException(nameof(newer));
        }

        var a = new Footprint(older, options);
        var b = new Footprint(newer, options);
        (List<(int Old, int New, int Step)> pairs, List<int> removedRows, List<int> addedRows) = Pair(
            Enumerable.Range(0, a.Tables.Count).ToList(), Enumerable.Range(0, b.Tables.Count).ToList(),
            row => a.Tables[row].TableId, row => b.Tables[row].TableId, a.NameKey, b.NameKey);
        var removedKeys = new HashSet<string>(removedRows.Select(a.NameKey), StringComparer.Ordinal);
        var addedKeys = new HashSet<string>(addedRows.Select(b.NameKey), StringComparer.Ordinal);
        var added = addedRows.Select(row => new TableEntry(b.Side(row), removedKeys.Contains(b.NameKey(row)))).ToList();
        var removed = removedRows.Select(row => new TableEntry(a.Side(row), addedKeys.Contains(a.NameKey(row)))).ToList();
        var kept = pairs.OrderBy(pair => pair.New).Select(pair => Kept(a, b, pair.Old, pair.New, pair.Step)).ToList();

        CollectionMeasures oc = a.Measures.Collection, nc = b.Measures.Collection;
        var collection = new List<ValueChange>
        {
            Change("sources", oc.Sources, nc.Sources),
            Change("tables", oc.Tables, nc.Tables),
            Change("columns", oc.Columns, nc.Columns),
            Change("files", oc.Files, nc.Files),
            Change("fileSize", oc.FileSize, nc.FileSize),
            Change("rowCount", oc.RowCount, nc.RowCount),
            Change("footprint", oc.Footprint, nc.Footprint),
            Change("waste", oc.Waste, nc.Waste),
            Change("overhead", oc.Overhead, nc.Overhead),
        };
        var depths = oc.Depths.Keys.Union(nc.Depths.Keys).ToList();
        depths.Sort(string.CompareOrdinal);
        foreach (string depth in depths)
        {
            collection.Add(Change("depths." + depth, oc.Depths.TryGetValue(depth, out long o) ? o : 0, nc.Depths.TryGetValue(depth, out long n) ? n : 0));
        }

        MeasureOptions thresholds = options ?? new MeasureOptions();
        return new FootprintDiff(a.Measures, b.Measures, thresholds.SmallFileSize > 0 ? thresholds.SmallFileSize : StorageMeasures.DefaultSmallFileSize,
            thresholds.TinyFileSize > 0 ? thresholds.TinyFileSize : StorageMeasures.DefaultTinyFileSize, collection, added, removed, kept);
    }

    /// <summary>
    /// The changes of a comparison in one order. <c>total</c>, <c>waste</c>, <c>rows</c>, and <c>files</c> order by the
    /// size of the change, the largest first: the absolute delta of the stack Total, the Waste, the rows, or the files of a
    /// kept table, and the value itself for a table added or removed. A change whose size is null goes last. <c>name</c>
    /// skips the size. Ties go by the label, the newer one for a kept table, in ordinal order, then added before removed
    /// before kept, then the order of the list.
    /// </summary>
    /// <param name="diff">A comparison.</param>
    /// <param name="order">One of <see cref="Orders"/>.</param>
    /// <param name="top">The changes to keep, the first of the order; null for all.</param>
    /// <returns>The changes, in the order.</returns>
    public static IReadOnlyList<TableChange> Rank(FootprintDiff diff, string order = "total", int? top = null)
    {
        if (diff == null)
        {
            throw new ArgumentNullException(nameof(diff));
        }

        if (!Orders.Contains(order))
        {
            throw new ArgumentException($"The order is one of {string.Join(", ", Orders)}, not '{order}'.", nameof(order));
        }

        double? Size(TableChange change)
        {
            if (order == "name")
            {
                return null;
            }

            if (change.Entry != null)
            {
                TableSide side = change.Entry.Table;
                return order switch
                {
                    "total" => side.Total,
                    "waste" => side.Waste,
                    "rows" => side.RowCount,
                    _ => side.FileCount,
                };
            }

            ValueChange? value = order switch
            {
                "total" => change.Kept!.Measure("stack.total"),
                "waste" => change.Kept!.Measure("stack.waste"),
                "rows" => change.Kept!.Total("rowCount"),
                _ => change.Kept!.Total("fileCount"),
            };
            return value?.Delta.HasValue == true ? Math.Abs(value.Delta.Value) : null;
        }

        var items = new List<(TableChange Change, double? Size, int Kind, int Position)>();
        for (int i = 0; i < diff.Added.Count; i++)
        {
            var change = new TableChange("added", diff.Added[i], null);
            items.Add((change, Size(change), 0, i));
        }

        for (int i = 0; i < diff.Removed.Count; i++)
        {
            var change = new TableChange("removed", diff.Removed[i], null);
            items.Add((change, Size(change), 1, i));
        }

        for (int i = 0; i < diff.Kept.Count; i++)
        {
            var change = new TableChange("kept", null, diff.Kept[i]);
            items.Add((change, Size(change), 2, i));
        }

        int Compare((TableChange Change, double? Size, int Kind, int Position) x, (TableChange Change, double? Size, int Kind, int Position) y)
        {
            int size = x.Size.HasValue && y.Size.HasValue ? y.Size.Value.CompareTo(x.Size.Value) : x.Size.HasValue ? -1 : y.Size.HasValue ? 1 : 0;
            if (size != 0)
            {
                return size;
            }

            int label = string.CompareOrdinal(x.Change.Label, y.Change.Label);
            if (label != 0)
            {
                return label;
            }

            return x.Kind != y.Kind ? x.Kind.CompareTo(y.Kind) : x.Position.CompareTo(y.Position);
        }

        items.Sort(Compare);
        IEnumerable<TableChange> ranked = items.Select(item => item.Change);
        return (top.HasValue ? ranked.Take(top.Value) : ranked).ToList();
    }

    // ---- pairing ----

    /// <summary>
    /// Pairs the items one to one in three passes: the same identifier and the same key, the same identifier, and the
    /// same key when at least one of the two has no identifier. Each pass takes the older items in their order and pairs
    /// each with the first newer item, in its order, that matches and is not paired yet.
    /// </summary>
    private static (List<(int Old, int New, int Step)> Pairs, List<int> Removed, List<int> Added) Pair(List<int> old, List<int> @new,
        Func<int, string?> oldIdent, Func<int, string?> newIdent, Func<int, string> oldKey, Func<int, string> newKey)
    {
        var pairs = new List<(int Old, int New, int Step)>();
        var usedOld = new HashSet<int>();
        var usedNew = new HashSet<int>();
        for (int step = 0; step < 3; step++)
        {
            var candidates = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (int n in @new)
            {
                if (usedNew.Contains(n))
                {
                    continue;
                }

                string? ident = newIdent(n);
                string? candidate = step switch
                {
                    0 => ident == null ? null : Encode(ident) + newKey(n),
                    1 => ident == null ? null : Encode(ident),
                    _ => newKey(n) + (ident == null ? "N" : "I"),
                };
                if (candidate != null)
                {
                    if (!candidates.TryGetValue(candidate, out List<int>? list))
                    {
                        candidates[candidate] = list = new List<int>();
                    }

                    list.Add(n);
                }
            }

            foreach (int o in old)
            {
                if (usedOld.Contains(o))
                {
                    continue;
                }

                string? ident = oldIdent(o);
                var lookups = new List<string>();
                if (step == 0 && ident != null)
                {
                    lookups.Add(Encode(ident) + oldKey(o));
                }
                else if (step == 1 && ident != null)
                {
                    lookups.Add(Encode(ident));
                }
                else if (step == 2)
                {
                    // Without an identifier, any newer item with the key; with one, only a newer item without.
                    lookups.Add(oldKey(o) + "N");
                    if (ident == null)
                    {
                        lookups.Add(oldKey(o) + "I");
                    }
                }

                int? match = null;
                foreach (string lookup in lookups)
                {
                    if (candidates.TryGetValue(lookup, out List<int>? list))
                    {
                        foreach (int n in list)
                        {
                            if (!usedNew.Contains(n))
                            {
                                match = match.HasValue ? Math.Min(match.Value, n) : n;
                                break;
                            }
                        }
                    }
                }

                if (match.HasValue)
                {
                    usedOld.Add(o);
                    usedNew.Add(match.Value);
                    pairs.Add((o, match.Value, step));
                }
            }
        }

        return (pairs, old.Where(o => !usedOld.Contains(o)).ToList(), @new.Where(n => !usedNew.Contains(n)).ToList());
    }

    /// <summary>A text that identifies a value among others, null included: its length before it, so that no two lists of values give the same text.</summary>
    private static string Encode(string? value) => value == null ? "~" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private static string Encode(IReadOnlyList<string?>? values)
    {
        if (values == null)
        {
            return "~";
        }

        var text = new StringBuilder(values.Count.ToString(CultureInfo.InvariantCulture)).Append('[');
        foreach (string? value in values)
        {
            text.Append(Encode(value));
        }

        return text.Append(']').ToString();
    }

    // ---- values ----

    private static bool IsNumber(object? value) => value is long || value is double;

    private static ValueChange Change(string name, object? older, object? newer)
    {
        double? delta = null;
        if (older != null && newer != null)
        {
            if (older is DateTimeOffset o && newer is DateTimeOffset n)
            {
                delta = (n.UtcTicks - o.UtcTicks) / TimeSpan.TicksPerMillisecond;
            }
            else if (IsNumber(older) && IsNumber(newer))
            {
                delta = Convert.ToDouble(newer, CultureInfo.InvariantCulture) - Convert.ToDouble(older, CultureInfo.InvariantCulture);
            }
        }

        return new ValueChange(name, older, newer, delta, Differs(older, newer));
    }

    private static ValueChange Change(string name, long? older, long? newer) => Change(name, (object?)older, newer);

    private static ValueChange Change(string name, double? older, double? newer) => Change(name, (object?)older, newer);

    private static bool Differs(object? older, object? newer)
    {
        if (older == null || newer == null)
        {
            return older != null || newer != null;
        }

        if (IsNumber(older) && IsNumber(newer))
        {
            return Convert.ToDouble(older, CultureInfo.InvariantCulture) != Convert.ToDouble(newer, CultureInfo.InvariantCulture);
        }

        if (older is IReadOnlyList<string> a && newer is IReadOnlyList<string> b)
        {
            return !a.SequenceEqual(b, StringComparer.Ordinal);
        }

        if (older is string s && newer is string t)
        {
            return !string.Equals(s, t, StringComparison.Ordinal);
        }

        return !older.Equals(newer);
    }

    private static object? Box(long? value) => value;

    private static object? Box(double? value) => value;

    private static object? Box(bool? value) => value;

    private static object? Box(DateTimeOffset? value) => value;

    // ---- one footprint ----

    private sealed class Footprint
    {
        private readonly PqaxDocument document;
        private readonly Dictionary<long, (string? Kind, string? Name)> sources = new();
        private readonly Dictionary<long, List<int>> columnsByTable;
        private readonly Dictionary<long, List<int>> filesByTable;
        private readonly Dictionary<long, List<int>> partitionsByTable;
        private readonly Dictionary<long, List<int>> commitsByTable;
        private readonly string[] nameKeys;

        public Footprint(PqaxDocument document, MeasureOptions? options)
        {
            this.document = document;
            Measures = StorageMeasures.Compute(document, options);
            Tables = document.Summary.Tables.Rows;
            Columns = document.Summary.Columns.Rows;
            foreach (SourceRow source in document.Summary.Sources.Rows)
            {
                if (source.Id > 0 && !sources.ContainsKey(source.Id))
                {
                    sources[source.Id] = (source.Kind, source.Name);
                }
            }

            columnsByTable = ByTable(Columns, column => column.Table);
            Files = Detail(document.ReadFiles, "files");
            Partitions = Detail(document.ReadPartitions, "partitions");
            Commits = Detail(document.ReadCommits, "commits");
            filesByTable = ByTable(Files ?? Array.Empty<FileRow>(), file => file.Table);
            partitionsByTable = ByTable(Partitions ?? Array.Empty<PartitionRow>(), partition => partition.Table);
            commitsByTable = ByTable(Commits ?? Array.Empty<CommitRow>(), commit => commit.Table);
            nameKeys = Tables.Select(NameKeyOf).ToArray();
        }

        public FootprintMeasures Measures { get; }

        public IReadOnlyList<TableRow> Tables { get; }

        public IReadOnlyList<ColumnRow> Columns { get; }

        /// <summary>The files, empty when the file has none, null when they cannot be read.</summary>
        public IReadOnlyList<FileRow>? Files { get; }

        public IReadOnlyList<PartitionRow>? Partitions { get; }

        public IReadOnlyList<CommitRow>? Commits { get; }

        /// <summary>Whether the footprint keeps the partitions: it has rows of them, and they can be read.</summary>
        public bool KeepsPartitions => Partitions != null && Partitions.Count > 0;

        public bool KeepsCommits => Commits != null && Commits.Count > 0;

        public string NameKey(int row) => nameKeys[row];

        public static long? Key(long id) => id > 0 ? id : null;

        public List<int> ColumnsOf(long? table) => Rows(columnsByTable, table);

        public List<int> FilesOf(long? table) => Rows(filesByTable, table);

        public List<int> PartitionsOf(long? table) => Rows(partitionsByTable, table);

        public List<int> CommitsOf(long? table) => Rows(commitsByTable, table);

        public bool KeepsFiles(int row)
        {
            string? depth = Tables[row].DetailDepth;
            return Files != null && depth != null && depth != "Summary";
        }

        public TableSide Side(int row)
        {
            TableRow table = Tables[row];
            TableStorage measured = Measures.Tables[row];
            return new TableSide(measured.Id, row, measured.Label, table.Name, table.Namespace, table.TableId, table.SnapshotId, table.DetailDepth,
                ColumnsOf(Key(table.Id)).Count, table.FileCount, table.FileSize, table.RowCount, measured.Stack.Total, measured.Stack.Waste);
        }

        public List<(string Name, object? Value)> TotalsOf(int row)
        {
            TableRow t = Tables[row];
            var values = new List<(string, object?)>
            {
                ("fileCount", Box(t.FileCount)), ("fileSize", Box(t.FileSize)), ("rowCount", Box(t.RowCount)), ("deletedRowCount", Box(t.DeletedRowCount)),
                ("lastModifiedAt", Box(t.LastModifiedAt)), ("vOrderedFileCount", Box(t.VOrderedFileCount)), ("partitionCount", Box(t.PartitionCount)),
                ("deleteFileCount", Box(t.DeleteFileCount)), ("deleteFileSize", Box(t.DeleteFileSize)), ("commitCount", Box(t.CommitCount)),
                ("commitsRead", Box(t.CommitsRead)), ("oldestCommitAt", Box(t.OldestCommitAt)), ("nonLiveFileCount", Box(t.NonLiveFileCount)),
                ("nonLiveFileSize", Box(t.NonLiveFileSize)), ("nonLiveOldestRemovedAt", Box(t.NonLiveOldestRemovedAt)),
                ("nonLiveNewestRemovedAt", Box(t.NonLiveNewestRemovedAt)), ("unreferencedFileCount", Box(t.UnreferencedFileCount)),
                ("unreferencedFileSize", Box(t.UnreferencedFileSize)), ("metadataFileCount", Box(t.MetadataFileCount)), ("metadataSize", Box(t.MetadataSize)),
                ("footerCount", Box(t.FooterCount)), ("rowGroupCount", Box(t.RowGroupCount)), ("compressedSize", Box(t.CompressedSize)),
                ("uncompressedSize", Box(t.UncompressedSize)), ("footerSize", Box(t.FooterSize)), ("staleColumnSize", Box(t.StaleColumnSize)),
            };
            foreach ((string name, Distribution? distribution) in new[] { ("fileSizes", t.FileSizes), ("rowGroupRowCounts", t.RowGroupRowCounts), ("partitionSizes", t.PartitionSizes) })
            {
                long?[] members = { distribution?.Count, distribution?.Sum, distribution?.Minimum, distribution?.FirstQuartile, distribution?.Median, distribution?.ThirdQuartile, distribution?.Maximum };
                for (int i = 0; i < Members.Length; i++)
                {
                    values.Add((name + "." + Members[i], Box(members[i])));
                }
            }

            return values;
        }

        public List<(string Name, object? Value)> MeasuresOf(int row)
        {
            TableStorage t = Measures.Tables[row];
            StorageStack stack = t.Stack;
            var values = new List<(string, object?)>();
            foreach (string key in StorageMeasures.StackComponents)
            {
                values.Add(("stack." + key, Box(stack.Component(key))));
            }

            values.Add(("stack.listed", stack.Listed));
            values.Add(("stack.data", Box(stack.Data)));
            values.Add(("stack.total", Box(stack.Total)));
            values.Add(("stack.waste", Box(stack.Waste)));
            values.Add(("stack.overhead", Box(stack.Overhead)));
            values.Add(("stack.wasteShare", Box(stack.WasteShare)));
            values.Add(("stack.overheadShare", Box(stack.OverheadShare)));
            foreach (string key in StorageMeasures.StackComponents)
            {
                values.Add(("stack.shares." + key, Box(stack.Share(key))));
            }

            values.Add(("smallFiles.count", Box(t.SmallFiles.Count)));
            values.Add(("smallFiles.shareAtLeast", Box(t.SmallFiles.ShareAtLeast)));
            values.Add(("tinyFiles.count", Box(t.TinyFiles.Count)));
            values.Add(("tinyFiles.shareAtLeast", Box(t.TinyFiles.ShareAtLeast)));
            FileMeasures f = t.Files;
            values.Add(("files.count", Box(f.Count)));
            values.Add(("files.sum", Box(f.Sum)));
            values.Add(("files.minimum", Box(f.Minimum)));
            values.Add(("files.firstQuartile", Box(f.FirstQuartile)));
            values.Add(("files.median", Box(f.Median)));
            values.Add(("files.thirdQuartile", Box(f.ThirdQuartile)));
            values.Add(("files.maximum", Box(f.Maximum)));
            values.Add(("files.vOrder.share", Box(f.VOrderShare)));
            values.Add(("files.vOrder.bytesShare", f.VOrderShare.HasValue ? Box(f.VOrderBytesShare) : null));
            TableMeasures m = t.Measures;
            values.Add(("measures.compressionRatio", Box(m.CompressionRatio)));
            values.Add(("measures.deletedRowShare", Box(m.DeletedRowShare)));
            values.Add(("measures.averageFileSize", Box(m.AverageFileSize)));
            values.Add(("measures.averageRowGroupRows", Box(m.AverageRowGroupRows)));
            values.Add(("measures.footerCoverage", Box(m.FooterCoverage)));
            values.Add(("measures.staleColumnShare", Box(m.StaleColumnShare)));
            values.Add(("measures.nonLiveShare", Box(m.NonLiveShare)));
            values.Add(("measures.averagePartitionSize", Box(m.AveragePartitionSize)));
            values.Add(("measures.dictionaryFallbackShare", Box(m.DictionaryFallbackShare)));
            MaintenanceMeasures h = t.Maintenance;
            values.Add(("maintenance.lastCommitAt", Box(h.LastCommitAt)));
            values.Add(("maintenance.commitsPerDay", Box(h.CommitsPerDay)));
            values.Add(("maintenance.daysSinceLastCommit", Box(h.DaysSinceLastCommit)));
            values.Add(("maintenance.hasVacuum", Box(h.HasVacuum)));
            values.Add(("maintenance.lastVacuumAt", Box(h.LastVacuumAt)));
            values.Add(("maintenance.hasOptimize", Box(h.HasOptimize)));
            values.Add(("maintenance.lastOptimizeAt", Box(h.LastOptimizeAt)));
            PartitionMeasures p = t.Partitions;
            values.Add(("partitions.count", Box(p.Count)));
            values.Add(("partitions.kept", p.Kept));
            values.Add(("partitions.oldestModifiedAt", Box(p.OldestModifiedAt)));
            values.Add(("partitions.newestModifiedAt", Box(p.NewestModifiedAt)));
            return values;
        }

        public ColumnSide ColumnSide(int row)
        {
            ColumnRow column = Columns[row];
            ColumnStorage measured = Measures.Columns[row];
            return new ColumnSide(measured.Id, row, measured.Label, column.Name, column.FieldId, column.DataType, column.IsNullable, column.CompressedSize);
        }

        public List<(string Name, object? Value)> ColumnTotalsOf(int row)
        {
            ColumnRow c = Columns[row];
            return new List<(string, object?)>
            {
                ("compressedSize", Box(c.CompressedSize)), ("uncompressedSize", Box(c.UncompressedSize)), ("valueCount", Box(c.ValueCount)),
                ("nullCount", Box(c.NullCount)), ("chunkCount", Box(c.ChunkCount)), ("dictionaryChunkCount", Box(c.DictionaryChunkCount)),
                ("fallbackChunkCount", Box(c.FallbackChunkCount)), ("dictionaryPageSize", Box(c.DictionaryPageSize)),
                ("unencodedByteArrayDataBytes", Box(c.UnencodedByteArrayDataBytes)), ("codecs", c.Codecs), ("encodings", c.Encodings),
                ("fileCountWithoutColumn", Box(c.FileCountWithoutColumn)), ("largestChunkDistinctCount", Box(c.LargestChunkDistinctCount)),
                ("distinctCount", Box(c.DistinctCount)),
            };
        }

        public List<(string Name, object? Value)> ColumnMeasuresOf(int row)
        {
            ColumnStorage c = Measures.Columns[row];
            return new List<(string, object?)>
            {
                ("nullShare", Box(c.NullShare)), ("compressionRatio", Box(c.CompressionRatio)), ("dictionaryShare", Box(c.DictionaryShare)),
                ("dictionaryFallbackShare", Box(c.DictionaryFallbackShare)), ("averageValueLength", Box(c.AverageValueLength)),
                ("filesWithoutColumnShare", Box(c.FilesWithoutColumnShare)),
            };
        }

        private string NameKeyOf(TableRow table)
        {
            (string? kind, string? name) = table.Source.HasValue && sources.TryGetValue(table.Source.Value, out (string?, string?) found) ? found : (null, null);
            return Encode(kind) + Encode(name) + Encode(table.Namespace) + Encode(table.Name);
        }

        private IReadOnlyList<T>? Detail<T>(Func<PqaxTable<T>> read, string name)
            where T : class
        {
            if (document.RowCount(name) <= 0)
            {
                return Array.Empty<T>();
            }

            try
            {
                return read().Rows;
            }
            catch (Exception e) when (e is PqaxFormatException || e is IOException)
            {
                return null;
            }
        }

        private static Dictionary<long, List<int>> ByTable<T>(IReadOnlyList<T> rows, Func<T, long?> table)
        {
            var result = new Dictionary<long, List<int>>();
            for (int row = 0; row < rows.Count; row++)
            {
                long? key = table(rows[row]);
                if (key.HasValue)
                {
                    if (!result.TryGetValue(key.Value, out List<int>? list))
                    {
                        result[key.Value] = list = new List<int>();
                    }

                    list.Add(row);
                }
            }

            return result;
        }

        private static List<int> Rows(Dictionary<long, List<int>> byTable, long? table) =>
            table.HasValue && byTable.TryGetValue(table.Value, out List<int>? rows) ? rows : new List<int>();
    }

    // ---- the comparison of two tables ----

    private static List<ValueChange> Changes(List<(string Name, object? Value)> older, List<(string Name, object? Value)> newer)
    {
        var result = new List<ValueChange>(older.Count);
        for (int i = 0; i < older.Count; i++)
        {
            result.Add(Change(older[i].Name, older[i].Value, newer[i].Value));
        }

        return result;
    }

    private static ColumnChanges Columns(Footprint a, Footprint b, long? oldTable, long? newTable)
    {
        (List<(int Old, int New, int Step)> pairs, List<int> removed, List<int> added) = Pair(a.ColumnsOf(oldTable), b.ColumnsOf(newTable),
            row => a.Columns[row].FieldId?.ToString(CultureInfo.InvariantCulture), row => b.Columns[row].FieldId?.ToString(CultureInfo.InvariantCulture),
            row => Encode(a.Columns[row].Name), row => Encode(b.Columns[row].Name));
        var kept = new List<KeptColumn>();
        foreach ((int o, int n, int step) in pairs.OrderBy(pair => pair.New))
        {
            List<ValueChange> totals = Changes(a.ColumnTotalsOf(o), b.ColumnTotalsOf(n));
            List<ValueChange> measures = Changes(a.ColumnMeasuresOf(o), b.ColumnMeasuresOf(n));
            ColumnSide older = a.ColumnSide(o), newer = b.ColumnSide(n);
            bool renamed = !string.Equals(older.Name, newer.Name, StringComparison.Ordinal);
            bool changed = renamed || !string.Equals(older.DataType, newer.DataType, StringComparison.Ordinal) || older.IsNullable != newer.IsNullable
                || totals.Any(value => value.IsChanged);
            kept.Add(new KeptColumn(step < 2 ? "fieldId" : "name", renamed, changed, older, newer, totals, measures));
        }

        return new ColumnChanges(added.Select(b.ColumnSide).ToList(), removed.Select(a.ColumnSide).ToList(), kept);
    }

    /// <summary>The objects of a detail list added, removed, kept, and changed, paired by a key; null when one has no key.</summary>
    private static DetailChanges? Detail<T>(IReadOnlyList<T> oldRows, List<int> oldOf, IReadOnlyList<T> newRows, List<int> newOf,
        Func<T, string?> key, Func<T, T, bool>? differs, Func<T, long?>? size)
    {
        var oldKeys = oldOf.Select(row => key(oldRows[row])).ToList();
        var newKeys = newOf.Select(row => key(newRows[row])).ToList();
        if (oldKeys.Any(k => k == null) || newKeys.Any(k => k == null))
        {
            return null;
        }

        var waiting = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (int i = 0; i < newKeys.Count; i++)
        {
            if (!waiting.TryGetValue(newKeys[i]!, out Queue<int>? queue))
            {
                waiting[newKeys[i]!] = queue = new Queue<int>();
            }

            queue.Enqueue(i);
        }

        var taken = new bool[newKeys.Count];
        var kept = new List<(T Old, T New)>();
        var removed = new List<T>();
        for (int i = 0; i < oldKeys.Count; i++)
        {
            if (waiting.TryGetValue(oldKeys[i]!, out Queue<int>? queue) && queue.Count > 0)
            {
                int match = queue.Dequeue();
                taken[match] = true;
                kept.Add((oldRows[oldOf[i]], newRows[newOf[match]]));
            }
            else
            {
                removed.Add(oldRows[oldOf[i]]);
            }
        }

        var added = newOf.Where((row, i) => !taken[i]).Select(row => newRows[row]).ToList();
        if (differs == null || size == null)
        {
            return new DetailChanges(added.Count, removed.Count, kept.Count, null, null, null);
        }

        long? Sum(List<T> rows)
        {
            if (rows.Count == 0)
            {
                return 0;
            }

            long? total = null;
            foreach (T row in rows)
            {
                long? value = size(row);
                if (value.HasValue)
                {
                    total = (total ?? 0) + value.Value;
                }
            }

            return total;
        }

        return new DetailChanges(added.Count, removed.Count, kept.Count, kept.Count(pair => differs(pair.Old, pair.New)), Sum(added), Sum(removed));
    }

    private static KeptTable Kept(Footprint a, Footprint b, int o, int n, int step)
    {
        TableSide older = a.Side(o), newer = b.Side(n);
        List<ValueChange> totals = Changes(a.TotalsOf(o), b.TotalsOf(n));
        List<ValueChange> measures = Changes(a.MeasuresOf(o), b.MeasuresOf(n));
        long? oldId = Footprint.Key(a.Tables[o].Id), newId = Footprint.Key(b.Tables[n].Id);
        ColumnChanges columns = Columns(a, b, oldId, newId);
        DetailChanges? files = null, partitions = null, commits = null;
        if (a.KeepsFiles(o) && b.KeepsFiles(n))
        {
            files = Detail(a.Files!, a.FilesOf(oldId), b.Files!, b.FilesOf(newId), file => file.Path,
                (x, y) => x.FileSize != y.FileSize || x.RowCount != y.RowCount || x.DeletedRowCount != y.DeletedRowCount || x.DeletionVectorSize != y.DeletionVectorSize,
                file => file.FileSize);
        }

        if (a.KeepsPartitions && b.KeepsPartitions)
        {
            partitions = Detail(a.Partitions!, a.PartitionsOf(oldId), b.Partitions!, b.PartitionsOf(newId), partition => partition.Values == null ? null : Encode(partition.Values),
                (x, y) => x.FileCount != y.FileCount || x.FileSize != y.FileSize || x.RowCount != y.RowCount || x.DeletedRowCount != y.DeletedRowCount,
                partition => partition.FileSize);
        }

        if (a.KeepsCommits && b.KeepsCommits)
        {
            commits = Detail(a.Commits!, a.CommitsOf(oldId), b.Commits!, b.CommitsOf(newId), commit => commit.CommitId, null, null);
        }

        bool renamed = !string.Equals(older.Name, newer.Name, StringComparison.Ordinal) || Encode(older.Namespace) != Encode(newer.Namespace);
        bool changed = renamed || !string.Equals(older.SnapshotId, newer.SnapshotId, StringComparison.Ordinal) || totals.Any(value => value.IsChanged)
            || columns.Added.Count > 0 || columns.Removed.Count > 0 || columns.Kept.Any(column => column.IsChanged)
            || new[] { files, partitions, commits }.Any(d => d != null && (d.Added > 0 || d.Removed > 0 || d.Changed > 0));
        return new KeptTable(step < 2 ? "tableId" : "name", renamed, changed, older, newer, totals, measures, columns, files, partitions, commits);
    }
}
