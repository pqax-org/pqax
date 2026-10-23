using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pqax.Cli;

/// <summary>
/// <c>pqax tables</c>: one row per table with the derived measures of the storage views: rows, files, live bytes, the
/// components of the waste, and the waste share, sorted by waste. <c>--json</c> writes the same as one JSON object.
/// </summary>
public static class TablesCommand
{
    /// <summary>The orders of <c>--sort</c>.</summary>
    public static IReadOnlyList<string> Orders { get; } = ["waste", "total", "rows", "files", "name"];

    /// <summary>Parses the arguments after <c>tables</c>, reads the file, and writes the table or the JSON.</summary>
    /// <param name="args">All the arguments, <c>tables</c> first.</param>
    /// <param name="output">Where the result goes.</param>
    /// <param name="error">Where usage, warnings, and errors go.</param>
    /// <returns>0 when the file is read, 1 when the reader refuses it, 2 for a wrong command line or a file that cannot be read.</returns>
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
            error.WriteLine($"pqax tables: {problem}");
            Usage(error);
            return Program.ExitError;
        }

        PqaxDocument document;
        try
        {
            document = options.IsEmbedded ? PqaxDocument.OpenEmbedded(options.File, options.PartName) : PqaxDocument.Open(options.File);
        }
        catch (PqaxFormatException e)
        {
            error.WriteLine($"pqax tables: {Path.GetFileName(options.File)} is not a PQAX file that this version reads ({e.Code}): {e.Message}");
            return Program.ExitInvalid;
        }

        using (document)
        {
            FootprintMeasures measures = StorageMeasures.Compute(document);
            foreach (string warning in measures.Warnings)
            {
                error.WriteLine($"pqax tables: {warning}");
            }

            List<TableStorage> tables = Sort(measures.Tables, options.Sort);
            if (options.Top.HasValue)
            {
                tables = tables.Take(options.Top.Value).ToList();
            }

            if (options.Json)
            {
                WriteJson(document, options, measures, tables, output);
            }
            else
            {
                WriteText(options, measures.Tables.Count, tables, output);
            }
        }

        return Program.ExitOk;
    }

    /// <summary>
    /// The tables in a new list, sorted: by waste, stack total, rows, or files, the largest first, or by label. Ties go by
    /// the stack total, the largest first, then by the label in ordinal order, then by the order of the file. A table
    /// without rows or files goes last for those orders, and a table whose waste or total is null after those that have it.
    /// </summary>
    /// <param name="tables">The tables.</param>
    /// <param name="order">One of <see cref="Orders"/>.</param>
    /// <returns>The sorted tables.</returns>
    public static List<TableStorage> Sort(IEnumerable<TableStorage> tables, string order)
    {
        ArgumentNullException.ThrowIfNull(tables);
        static int Descending(long? a, long? b) => a.HasValue && b.HasValue ? b.Value.CompareTo(a.Value) : a.HasValue ? -1 : b.HasValue ? 1 : 0;
        static int DescendingBytes(double? a, double? b) => a.HasValue && b.HasValue ? b.Value.CompareTo(a.Value) : a.HasValue ? -1 : b.HasValue ? 1 : 0;
        int Compare(TableStorage a, TableStorage b)
        {
            int primary = order switch
            {
                "waste" => DescendingBytes(a.Stack.Waste, b.Stack.Waste),
                "rows" => Descending(a.RowCount, b.RowCount),
                "files" => Descending(a.FileCount, b.FileCount),
                _ => 0,
            };
            if (primary != 0)
            {
                return primary;
            }

            int total = order == "name" ? 0 : DescendingBytes(a.Stack.Total, b.Stack.Total);
            if (total != 0)
            {
                return total;
            }

            int label = string.CompareOrdinal(a.Label, b.Label);
            return label != 0 ? label : a.Row.CompareTo(b.Row);
        }

        var sorted = tables.ToList();
        sorted.Sort(Compare);
        return sorted;
    }

    private static void WriteJson(PqaxDocument document, Options options, FootprintMeasures measures, List<TableStorage> tables, TextWriter output)
    {
        CollectionInfo collection = document.Manifest.Collection;
        DateTimeOffset? collectedAt = collection.CompletedAt ?? collection.StartedAt;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("file", Path.GetFileName(options.File));
            String(writer, "embeddedPart", document.EmbeddedPart);
            writer.WriteString("format", document.Manifest.Format);
            writer.WriteString("formatVersion", document.Manifest.FormatVersion);
            String(writer, "collector", Join(collection.CollectorApp, collection.CollectorAppVersion));
            String(writer, "library", Join(collection.CollectorLib, collection.CollectorLibVersion));
            String(writer, "collectedAt", collectedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            if (collection.Level.HasValue)
            {
                writer.WriteNumber("level", collection.Level.Value);
            }
            else
            {
                writer.WriteNull("level");
            }

            writer.WriteNumber("smallFileSize", StorageMeasures.DefaultSmallFileSize);
            writer.WriteNumber("tinyFileSize", StorageMeasures.DefaultTinyFileSize);
            writer.WriteString("sort", options.Sort);
            if (options.Top.HasValue)
            {
                writer.WriteNumber("top", options.Top.Value);
            }
            else
            {
                writer.WriteNull("top");
            }

            writer.WriteNumber("tableCount", measures.Tables.Count);
            writer.WriteBoolean("filesRead", measures.FilesRead);
            writer.WriteStartArray("warnings");
            foreach (string warning in measures.Warnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("tables");
            foreach (TableStorage table in tables)
            {
                table.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        output.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteText(Options options, int tableCount, List<TableStorage> tables, TextWriter output)
    {
        output.WriteLine($"{Path.GetFileName(options.File)}: {Count(tableCount)} {(tableCount == 1 ? "table" : "tables")}, by {options.Sort}"
            + (options.Top.HasValue ? $", the first {Count(options.Top.Value)}" : string.Empty));
        var header = new List<string> { "Table", "Rows", "Files", "Live", "Non-live", "Unreferenced", "Deleted", "Stale", "Waste", "Waste share" };
        if (options.Wide)
        {
            header.AddRange(["Under 8 MiB", "Under 128 MiB", "Metadata", "Footers"]);
        }

        var rows = new List<List<string>> { header };
        foreach (TableStorage table in tables)
        {
            StorageStack stack = table.Stack;
            var cells = new List<string>
            {
                table.Label, Count(table.RowCount), Count(table.FileCount), Bytes(stack.Live), Bytes(stack.NonLive), Bytes(stack.Unreferenced),
                Bytes(stack.Deleted), Bytes(stack.Stale), Bytes(stack.Waste), Share(stack.WasteShare),
            };
            if (options.Wide)
            {
                cells.AddRange([Small(table.TinyFiles), Small(table.SmallFiles), Bytes(stack.Metadata), Bytes(stack.Footers)]);
            }

            rows.Add(cells);
        }

        int[] widths = Enumerable.Range(0, header.Count).Select(column => rows.Max(row => row[column].Length)).ToArray();
        foreach (List<string> row in rows)
        {
            var line = new StringBuilder();
            for (int column = 0; column < row.Count; column++)
            {
                if (column > 0)
                {
                    line.Append("  ");
                }

                line.Append(column == 0 ? row[column].PadRight(widths[column]) : row[column].PadLeft(widths[column]));
            }

            output.WriteLine(line.ToString().TrimEnd());
        }
    }

    /// <summary>Bytes in 1024-based units, as the storage views write them: <c>512 B</c>, <c>1.50 KB</c>, <c>21.5 MB</c>.</summary>
    /// <param name="value">The bytes.</param>
    /// <returns>The text, or <c>n/a</c>.</returns>
    public static string Bytes(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return Missing;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double scaled = Math.Abs(value.Value);
        int unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        int digits = unit == 0 ? 0 : scaled < 10 ? 2 : scaled < 100 ? 1 : 0;
        return $"{(value.Value < 0 ? "-" : string.Empty)}{Fixed(scaled, digits)} {units[unit]}";
    }

    /// <summary>A share from 0 to 1 as a percentage with one decimal: <c>36.7%</c>, and <c>&lt;0.1%</c> for a share above 0 that rounds to 0.</summary>
    /// <param name="value">The share.</param>
    /// <returns>The text, or <c>n/a</c>.</returns>
    public static string Share(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return Missing;
        }

        return value.Value > 0 && value.Value < 0.0005 ? "<0.1%" : Fixed(value.Value * 100, 1) + "%";
    }

    private const string Missing = "n/a";

    private static string Count(long? value) => value.HasValue ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : Missing;

    /// <summary>A share of small files. A share from the quartiles is a lower bound, written with <c>&gt;=</c>, except 0 and 1, which are exact.</summary>
    private static string Small(SmallFiles files) => files.ShareAtLeast is > 0 and < 1 && !files.Count.HasValue ? ">=" + Share(files.ShareAtLeast) : Share(files.ShareAtLeast);

    /// <summary>
    /// A number with separators and a fixed number of decimals, as the views write it: the shortest decimal that gives
    /// back the double, rounded half away from zero, so that 1.125 and 1.005 become 1.13 and 1.01.
    /// </summary>
    private static string Fixed(double value, int digits)
    {
        decimal shortest = decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        return Math.Round(shortest, digits, MidpointRounding.AwayFromZero).ToString("N" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static string? Join(string? name, string? version) => name == null ? null : version == null ? name : $"{name} {version}";

    private static void String(Utf8JsonWriter writer, string name, string? value)
    {
        if (value != null)
        {
            writer.WriteString(name, value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void Usage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  pqax tables <file> [--part <name>] [--sort waste|total|rows|files|name] [--top <n>] [--wide] [--json]");
        writer.WriteLine();
        writer.WriteLine("One row per table: rows, files, live bytes, the non-live and unreferenced files, the deleted rows, the stale");
        writer.WriteLine("columns, the waste, and the waste share, as the storage views compute them.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --sort waste|total|rows|files|name   The order: the largest first, or by name. Default waste.");
        writer.WriteLine("  --top <n>                            The first n tables of the order.");
        writer.WriteLine("  --wide                               Adds the shares of the files under 8 MiB and 128 MiB, the metadata, and the footers.");
        writer.WriteLine("  --json                               Writes one JSON object with the collection and every measure of each table.");
        writer.WriteLine("  --part <name>                        The part of a VPAX file that holds the footprint. Default Footprint.pqax.");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 read, 1 the reader refuses the file, 2 wrong command line or file that cannot be read.");
    }

    /// <summary>The arguments of <c>tables</c>.</summary>
    private sealed class Options
    {
        public string File { get; private set; } = string.Empty;

        public string? Part { get; private set; }

        public string Sort { get; private set; } = "waste";

        public int? Top { get; private set; }

        public bool Json { get; private set; }

        public bool Wide { get; private set; }

        public bool IsEmbedded => Part != null || string.Equals(Path.GetExtension(File), ".vpax", StringComparison.OrdinalIgnoreCase);

        public string PartName => Part ?? PqaxDocument.FootprintPartName;

        public static Options? Parse(string[] args, out string? problem)
        {
            var options = new Options();
            string? file = null;
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
                        if (order == null || !Orders.Contains(order))
                        {
                            problem = "--sort is waste, total, rows, files, or name.";
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
                    case "--wide":
                        options.Wide = true;
                        break;
                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
                        {
                            problem = $"unknown option '{arg}'.";
                            return null;
                        }

                        if (file != null)
                        {
                            problem = $"one file at a time; '{arg}' is one too many.";
                            return null;
                        }

                        file = arg;
                        break;
                }
            }

            if (string.IsNullOrEmpty(file))
            {
                problem = "the file is missing.";
                return null;
            }

            options.File = file;
            return options;
        }
    }
}
