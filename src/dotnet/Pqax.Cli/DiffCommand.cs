using System.Globalization;
using System.Numerics;
using System.Text;

namespace Pqax.Cli;

/// <summary>
/// <c>pqax diff OLDER NEWER</c>: the comparison of two footprints of the same lakehouse, with <see cref="StorageDiff"/>.
/// The text report lists the tables added, removed, and kept, the largest change first, and the columns that changed;
/// <c>--json</c> writes one object with both headers and the three lists. The text and the JSON are those of
/// <c>python -m pqax diff</c>, apart from the line ends.
/// </summary>
public static class DiffCommand
{
    private const string Missing = "n/a";
    private const string Absent = "-";

    /// <summary>Parses the arguments after <c>diff</c>, reads the two files, and writes the report or the JSON.</summary>
    /// <param name="args">All the arguments, <c>diff</c> first.</param>
    /// <param name="output">Where the result goes.</param>
    /// <param name="error">Where usage, warnings, and errors go.</param>
    /// <returns>0 when the files are compared, 1 when the reader refuses one, 2 for a wrong command line or a file that cannot be read.</returns>
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
            error.WriteLine($"pqax diff: {problem}");
            Usage(error);
            return Program.ExitError;
        }

        PqaxDocument? older = null;
        PqaxDocument? newer = null;
        try
        {
            string current = options.Older;
            try
            {
                older = Open(options, options.Older);
                current = options.Newer;
                newer = Open(options, options.Newer);
            }
            catch (PqaxFormatException e)
            {
                error.WriteLine($"pqax diff: {Path.GetFileName(current)} is not a PQAX file that this version reads ({e.Code}): {e.Message}");
                return Program.ExitInvalid;
            }

            FootprintDiff diff = StorageDiff.Compare(older, newer);
            Header olderHeader = new(options.Older, older, diff.Older);
            Header newerHeader = new(options.Newer, newer, diff.Newer);
            foreach (Header header in new[] { olderHeader, newerHeader })
            {
                foreach (string warning in header.Warnings)
                {
                    error.WriteLine($"pqax diff: {header.File}: {warning}");
                }
            }

            IReadOnlyList<TableChange> ranked = StorageDiff.Rank(diff, options.Sort, options.Top);
            string text = options.Json ? Json(diff, olderHeader, newerHeader, ranked, options) : Text(diff, olderHeader, newerHeader, ranked, options);
            foreach (string line in text.Split('\n'))
            {
                output.WriteLine(line);
            }

            return Program.ExitOk;
        }
        finally
        {
            older?.Dispose();
            newer?.Dispose();
        }
    }

    private static PqaxDocument Open(Options options, string file) =>
        options.Part != null || string.Equals(Path.GetExtension(file), ".vpax", StringComparison.OrdinalIgnoreCase)
            ? PqaxDocument.OpenEmbedded(file, options.Part ?? PqaxDocument.FootprintPartName)
            : PqaxDocument.Open(file);

    // ---- the text ----

    private static string Text(FootprintDiff diff, Header older, Header newer, IReadOnlyList<TableChange> ranked, Options options)
    {
        var lines = new List<string>();
        foreach ((string title, Header header) in new[] { ("Older", older), ("Newer", newer) })
        {
            lines.Add($"{title}: {header.File}, {Count(header.TableCount)} {(header.TableCount == 1 ? "table" : "tables")}, collected {header.CollectedAt ?? Missing}");
        }

        lines.Add($"Tables: {Count(diff.Added.Count)} added, {Count(diff.Removed.Count)} removed, {Count(diff.Kept.Count)} kept, {Count(diff.ChangedCount)} changed; by {options.Sort}"
            + (options.Top.HasValue ? $", the first {Count(options.Top.Value)}" : string.Empty));
        ValueChange footprint = diff.CollectionValue("footprint")!, waste = diff.CollectionValue("waste")!;
        lines.Add($"Footprint: {Bytes(footprint.Older)} to {Bytes(footprint.Newer)}, {SignedBytes(footprint.Delta)}; waste: {Bytes(waste.Older)} to {Bytes(waste.Newer)}, {SignedBytes(waste.Delta)}");
        lines.Add(string.Empty);
        var rows = new List<string[]>
        {
            new[] { "Change", "Table", "Total before", "Total after", "Total change", "Waste change", "Files change", "Rows change", "Files added", "Files removed" },
        };
        var columns = new List<string[]> { new[] { "Table", "Column", "Change", "Detail" } };
        foreach (TableChange change in ranked)
        {
            if (change.Entry != null)
            {
                TableSide side = change.Entry.Table;
                bool added = change.Kind == "added";
                rows.Add(added
                    ? new[] { change.Entry.Recreated ? "recreated" : "added", side.Label, Absent, Bytes(side.Total), SignedBytes(side.Total), SignedBytes(side.Waste),
                        SignedCount(side.FileCount), SignedCount(side.RowCount), Absent, Absent }
                    : new[] { change.Entry.Recreated ? "replaced" : "removed", side.Label, Bytes(side.Total), Absent, SignedBytes(-side.Total), SignedBytes(-side.Waste),
                        SignedCount(-side.FileCount), SignedCount(-side.RowCount), Absent, Absent });
                continue;
            }

            KeptTable kept = change.Kept!;
            string label = kept.Newer.Label + (kept.Renamed ? $" (was {kept.Older.Label})" : string.Empty);
            rows.Add(new[]
            {
                kept.Renamed ? "renamed" : kept.IsChanged ? "changed" : "unchanged", label, Bytes(kept.Older.Total), Bytes(kept.Newer.Total),
                SignedBytes(kept.Measure("stack.total")!.Delta), SignedBytes(kept.Measure("stack.waste")!.Delta),
                SignedCount(Whole(kept.Total("fileCount")!.Delta)), SignedCount(Whole(kept.Total("rowCount")!.Delta)),
                kept.Files != null ? Count(kept.Files.Added) : Missing, kept.Files != null ? Count(kept.Files.Removed) : Missing,
            });
            string table = kept.Newer.Label;
            foreach (ColumnSide column in kept.Columns.Added)
            {
                columns.Add(new[] { table, column.Label, "added", column.DataType ?? Missing });
            }

            foreach (ColumnSide column in kept.Columns.Removed)
            {
                columns.Add(new[] { table, column.Label, "removed", column.DataType ?? Missing });
            }

            foreach (KeptColumn column in kept.Columns.Kept.Where(c => c.IsChanged))
            {
                var parts = new List<string>();
                if (column.Renamed)
                {
                    parts.Add($"was {column.Older.Name ?? Missing}");
                }

                if (!string.Equals(column.Older.DataType, column.Newer.DataType, StringComparison.Ordinal))
                {
                    parts.Add($"type {column.Older.DataType ?? Missing} to {column.Newer.DataType ?? Missing}");
                }

                if (column.Older.IsNullable != column.Newer.IsNullable)
                {
                    parts.Add($"nullable {Flag(column.Older.IsNullable)} to {Flag(column.Newer.IsNullable)}");
                }

                double? size = column.Totals.First(value => value.Name == "compressedSize").Delta;
                if (size.HasValue && size.Value != 0)
                {
                    parts.Add($"size {SignedBytes(size)}");
                }

                columns.Add(new[] { table, column.Newer.Label, column.Renamed ? "renamed" : "changed", string.Join("; ", parts) });
            }
        }

        lines.AddRange(Align(rows, 2));
        if (columns.Count > 1)
        {
            lines.Add(string.Empty);
            lines.AddRange(Align(columns, 4));
        }

        return string.Join("\n", lines);
    }

    private static IEnumerable<string> Align(List<string[]> rows, int rightFrom)
    {
        int[] widths = Enumerable.Range(0, rows[0].Length).Select(column => rows.Max(row => row[column].Length)).ToArray();
        return rows.Select(row => string.Join("  ", row.Select((cell, i) => i >= rightFrom ? cell.PadLeft(widths[i]) : cell.PadRight(widths[i]))).TrimEnd());
    }

    private static string Bytes(object? value) => TablesCommand.Bytes(value == null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture));

    private static string SignedBytes(double? value) => !value.HasValue ? Missing : value.Value == 0 ? "0 B" : (value.Value > 0 ? "+" : string.Empty) + TablesCommand.Bytes(value);

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string SignedCount(long? value) => !value.HasValue ? Missing : (value.Value > 0 ? "+" : string.Empty) + Count(value.Value);

    private static long? Whole(double? value) => value.HasValue ? (long)value.Value : null;

    private static string Flag(bool? value) => value.HasValue ? (value.Value ? "true" : "false") : Missing;

    // ---- the JSON ----

    private static string Json(FootprintDiff diff, Header older, Header newer, IReadOnlyList<TableChange> ranked, Options options)
    {
        var json = new JsonText();
        json.StartObject();
        json.Name("older");
        older.Write(json);
        json.Name("newer");
        newer.Write(json);
        json.Property("smallFileSize", diff.SmallFileSize);
        json.Property("tinyFileSize", diff.TinyFileSize);
        json.Property("sort", options.Sort);
        json.Property("top", options.Top.HasValue ? options.Top.Value : null);
        json.Name("counts");
        json.StartObject();
        json.Property("added", diff.Added.Count);
        json.Property("removed", diff.Removed.Count);
        json.Property("kept", diff.Kept.Count);
        json.Property("changed", diff.ChangedCount);
        json.EndObject();
        json.Name("collection");
        Changes(json, diff.Collection);
        foreach (string kind in new[] { "added", "removed" })
        {
            json.Name(kind);
            json.StartArray();
            foreach (TableChange change in ranked.Where(c => c.Kind == kind))
            {
                TableSide(json, change.Entry!.Table, change.Entry.Recreated);
            }

            json.EndArray();
        }

        json.Name("kept");
        json.StartArray();
        foreach (TableChange change in ranked.Where(c => c.Kind == "kept"))
        {
            KeptTable kept = change.Kept!;
            json.StartObject();
            json.Property("status", kept.IsChanged ? "changed" : "unchanged");
            json.Property("matchedBy", kept.MatchedBy);
            json.Property("renamed", kept.Renamed);
            json.Name("older");
            TableSide(json, kept.Older, null);
            json.Name("newer");
            TableSide(json, kept.Newer, null);
            json.Name("totals");
            Changes(json, kept.Totals);
            json.Name("measures");
            Changes(json, kept.Measures);
            json.Name("columns");
            json.StartObject();
            foreach ((string name, IReadOnlyList<ColumnSide> list) in new[] { ("added", kept.Columns.Added), ("removed", kept.Columns.Removed) })
            {
                json.Name(name);
                json.StartArray();
                foreach (ColumnSide column in list)
                {
                    ColumnSide(json, column);
                }

                json.EndArray();
            }

            json.Name("kept");
            json.StartArray();
            foreach (KeptColumn column in kept.Columns.Kept)
            {
                json.StartObject();
                json.Property("status", column.IsChanged ? "changed" : "unchanged");
                json.Property("matchedBy", column.MatchedBy);
                json.Property("renamed", column.Renamed);
                json.Name("older");
                ColumnSide(json, column.Older);
                json.Name("newer");
                ColumnSide(json, column.Newer);
                json.Name("totals");
                Changes(json, column.Totals);
                json.Name("measures");
                Changes(json, column.Measures);
                json.EndObject();
            }

            json.EndArray();
            json.EndObject();
            Detail(json, "files", kept.Files, true);
            Detail(json, "partitions", kept.Partitions, true);
            Detail(json, "commits", kept.Commits, false);
            json.EndObject();
        }

        json.EndArray();
        json.EndObject();
        return json.ToString();
    }

    private static void TableSide(JsonText json, TableSide side, bool? recreated)
    {
        json.StartObject();
        json.Property("id", side.Id);
        json.Property("label", side.Label);
        json.Property("name", side.Name);
        json.Property("namespace", side.Namespace);
        json.Property("tableId", side.TableId);
        json.Property("snapshotId", side.SnapshotId);
        json.Property("detailDepth", side.DetailDepth);
        json.Property("columnCount", side.ColumnCount);
        json.Property("fileCount", side.FileCount);
        json.Property("fileSize", side.FileSize);
        json.Property("rowCount", side.RowCount);
        json.Property("total", side.Total);
        json.Property("waste", side.Waste);
        if (recreated.HasValue)
        {
            json.Property("recreated", recreated.Value);
        }

        json.EndObject();
    }

    private static void ColumnSide(JsonText json, ColumnSide side)
    {
        json.StartObject();
        json.Property("id", side.Id);
        json.Property("label", side.Label);
        json.Property("name", side.Name);
        json.Property("fieldId", side.FieldId);
        json.Property("dataType", side.DataType);
        json.Property("isNullable", side.IsNullable);
        json.Property("compressedSize", side.CompressedSize);
        json.EndObject();
    }

    private static void Changes(JsonText json, IReadOnlyList<ValueChange> changes)
    {
        json.StartObject();
        foreach (ValueChange change in changes)
        {
            json.Name(change.Name);
            json.StartObject();
            json.Property("older", change.Older);
            json.Property("newer", change.Newer);
            json.Property("delta", change.Delta);
            json.EndObject();
        }

        json.EndObject();
    }

    private static void Detail(JsonText json, string name, DetailChanges? detail, bool sizes)
    {
        json.Name(name);
        if (detail == null)
        {
            json.Value(null);
            return;
        }

        json.StartObject();
        json.Property("added", detail.Added);
        json.Property("removed", detail.Removed);
        json.Property("kept", detail.Kept);
        if (sizes)
        {
            json.Property("changed", detail.Changed);
            json.Property("addedFileSize", detail.AddedFileSize);
            json.Property("removedFileSize", detail.RemovedFileSize);
        }

        json.EndObject();
    }

    /// <summary>
    /// A number as Python writes it in JSON, so that both command lines write the same text: an integer with all its
    /// digits, and any other number as the shortest decimal that gives the double back, in the form of <c>repr</c>, with an
    /// exponent below 1e-4 and from 1e16.
    /// </summary>
    /// <param name="value">A finite number.</param>
    /// <returns>The text.</returns>
    public static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "JSON has no NaN or infinity.");
        }

        if (value == Math.Floor(value))
        {
            return new BigInteger(value).ToString(CultureInfo.InvariantCulture);
        }

        string shortest = value.ToString("R", CultureInfo.InvariantCulture);
        string sign = shortest.StartsWith('-') ? "-" : string.Empty;
        string body = shortest.TrimStart('-');
        int exponent = 0;
        int e = body.IndexOfAny(new[] { 'E', 'e' });
        if (e >= 0)
        {
            exponent = int.Parse(body[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            body = body[..e];
        }

        int dot = body.IndexOf('.');
        string digits = body.Replace(".", string.Empty, StringComparison.Ordinal);
        int point = dot < 0 ? body.Length : dot;
        int leading = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0').TrimEnd('0');
        point -= leading;

        // The value is 0.digits times 10 to the power point. Python writes it in fixed form when -4 < point <= 16.
        int decimalPoint = point + exponent;
        if (decimalPoint > -4 && decimalPoint <= 16)
        {
            if (decimalPoint <= 0)
            {
                return sign + "0." + new string('0', -decimalPoint) + digits;
            }

            return decimalPoint >= digits.Length
                ? sign + digits + new string('0', decimalPoint - digits.Length) + ".0"
                : sign + digits[..decimalPoint] + "." + digits[decimalPoint..];
        }

        int power = decimalPoint - 1;
        string mantissa = digits.Length > 1 ? digits[..1] + "." + digits[1..] : digits;
        return sign + mantissa + "e" + (power < 0 ? "-" : "+") + Math.Abs(power).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>A JSON text indented as Python's <c>json.dumps(value, indent=2, ensure_ascii=False)</c> writes it.</summary>
    private sealed class JsonText
    {
        private readonly StringBuilder text = new();
        private readonly Stack<bool> empty = new();
        private bool afterName;

        public void StartObject() => Start('{');

        public void EndObject() => End('}');

        public void StartArray() => Start('[');

        public void EndArray() => End(']');

        public void Name(string name)
        {
            Separate();
            text.Append(Quote(name)).Append(": ");
            afterName = true;
        }

        public void Property(string name, object? value)
        {
            Name(name);
            Value(value);
        }

        public void Value(object? value)
        {
            if (value is IReadOnlyList<string> list)
            {
                StartArray();
                foreach (string item in list)
                {
                    Value(item);
                }

                EndArray();
                return;
            }

            Separate();
            switch (value)
            {
                case null:
                    text.Append("null");
                    break;
                case bool flag:
                    text.Append(flag ? "true" : "false");
                    break;
                case string s:
                    text.Append(Quote(s));
                    break;
                case DateTimeOffset moment:
                    text.Append(Quote(moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)));
                    break;
                case int or long or double:
                    text.Append(FormatNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture)));
                    break;
                default:
                    throw new ArgumentException($"A value of type {value.GetType().Name} has no JSON form here.", nameof(value));
            }
        }

        public override string ToString() => text.ToString();

        private void Start(char bracket)
        {
            Separate();
            text.Append(bracket);
            empty.Push(true);
        }

        private void End(char bracket)
        {
            if (!empty.Pop())
            {
                text.Append('\n').Append(' ', 2 * empty.Count);
            }

            text.Append(bracket);
        }

        /// <summary>Before a value or a name: nothing after a name, else a comma after an earlier item, and the new line and indentation of the level.</summary>
        private void Separate()
        {
            if (afterName)
            {
                afterName = false;
                return;
            }

            if (empty.Count == 0)
            {
                return;
            }

            if (!empty.Peek())
            {
                text.Append(',');
            }

            empty.Pop();
            empty.Push(false);
            text.Append('\n').Append(' ', 2 * empty.Count);
        }

        private static string Quote(string value)
        {
            var quoted = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        quoted.Append("\\\"");
                        break;
                    case '\\':
                        quoted.Append("\\\\");
                        break;
                    case '\n':
                        quoted.Append("\\n");
                        break;
                    case '\r':
                        quoted.Append("\\r");
                        break;
                    case '\t':
                        quoted.Append("\\t");
                        break;
                    case '\b':
                        quoted.Append("\\b");
                        break;
                    case '\f':
                        quoted.Append("\\f");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            quoted.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            quoted.Append(c);
                        }

                        break;
                }
            }

            return quoted.Append('"').ToString();
        }
    }

    /// <summary>A header of the JSON: the file and its collection, as <c>pqax tables --json</c> writes them.</summary>
    private sealed class Header
    {
        public Header(string path, PqaxDocument document, FootprintMeasures measures)
        {
            File = Path.GetFileName(path);
            Document = document;
            CollectionInfo collection = document.Manifest.Collection;
            CollectedAt = (collection.CompletedAt ?? collection.StartedAt)?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            TableCount = measures.Tables.Count;
            FilesRead = measures.FilesRead;
            Warnings = measures.Warnings;
        }

        public string File { get; }

        public PqaxDocument Document { get; }

        public string? CollectedAt { get; }

        public int TableCount { get; }

        public bool FilesRead { get; }

        public IReadOnlyList<string> Warnings { get; }

        public void Write(JsonText json)
        {
            CollectionInfo collection = Document.Manifest.Collection;
            json.StartObject();
            json.Property("file", File);
            json.Property("embeddedPart", Document.EmbeddedPart);
            json.Property("format", Document.Manifest.Format);
            json.Property("formatVersion", Document.Manifest.FormatVersion);
            json.Property("collector", Join(collection.CollectorApp, collection.CollectorAppVersion));
            json.Property("library", Join(collection.CollectorLib, collection.CollectorLibVersion));
            json.Property("collectedAt", CollectedAt);
            json.Property("level", collection.Level);
            json.Property("tableCount", TableCount);
            json.Property("filesRead", FilesRead);
            json.Name("warnings");
            json.StartArray();
            foreach (string warning in Warnings)
            {
                json.Value(warning);
            }

            json.EndArray();
            json.EndObject();
        }

        private static string? Join(string? name, string? version) => name == null ? null : version == null ? name : $"{name} {version}";
    }

    private static void Usage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  pqax diff <older> <newer> [--part <name>] [--sort total|waste|rows|files|name] [--top <n>] [--json]");
        writer.WriteLine();
        writer.WriteLine("Compares two footprints of the same lakehouse: the tables and the columns added, removed, and kept, and the change");
        writer.WriteLine("of the total size, the waste, the files, and the rows of each table, the largest change first. A table is the same");
        writer.WriteLine("table when its TableId is, and by its source, namespace, and name when one of the two has none.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --sort total|waste|rows|files|name   The order: the largest change first, or by name. Default total.");
        writer.WriteLine("  --top <n>                            The first n changes of the order.");
        writer.WriteLine("  --json                               Writes one JSON object with both headers and the tables added, removed, and kept,");
        writer.WriteLine("                                       with the change of every total and measure.");
        writer.WriteLine("  --part <name>                        The part of a VPAX file that holds the footprint, for both files. Default Footprint.pqax.");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 compared, 1 the reader refuses a file, 2 wrong command line or file that cannot be read.");
    }

    /// <summary>The arguments of <c>diff</c>.</summary>
    private sealed class Options
    {
        public string Older { get; private set; } = string.Empty;

        public string Newer { get; private set; } = string.Empty;

        public string? Part { get; private set; }

        public string Sort { get; private set; } = "total";

        public int? Top { get; private set; }

        public bool Json { get; private set; }

        public static Options? Parse(string[] args, out string? problem)
        {
            var options = new Options();
            var files = new List<string>();
            problem = null;
            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                string? Next() => i + 1 < args.Length && args[i + 1].Length > 0 ? args[++i] : null;
                switch (arg)
                {
                    case "--part":
                        options.Part = Next();
                        if (options.Part == null)
                        {
                            problem = "--part needs the name of a part.";
                            return null;
                        }

                        break;
                    case "--sort":
                        string? order = Next();
                        if (order == null || !StorageDiff.Orders.Contains(order))
                        {
                            problem = "--sort is total, waste, rows, files, or name.";
                            return null;
                        }

                        options.Sort = order;
                        break;
                    case "--top":
                        string? top = Next();
                        if (top == null || !int.TryParse(top, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1)
                        {
                            problem = "--top needs a number of at least 1.";
                            return null;
                        }

                        options.Top = count;
                        break;
                    case "--json":
                        options.Json = true;
                        break;
                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
                        {
                            problem = $"unknown option '{arg}'.";
                            return null;
                        }

                        if (files.Count == 2)
                        {
                            problem = $"two files at a time; '{arg}' is one too many.";
                            return null;
                        }

                        files.Add(arg);
                        break;
                }
            }

            if (files.Count < 2 || files.Any(string.IsNullOrEmpty))
            {
                problem = files.Count == 0 ? "the older and the newer file are missing." : "the newer file is missing.";
                return null;
            }

            options.Older = files[0];
            options.Newer = files[1];
            return options;
        }
    }
}
