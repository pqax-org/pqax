using System.Globalization;

namespace Pqax.Cli;

/// <summary><c>pqax info</c>: shows the format, the collection, the parts with their tables and rows, and the totals of the summary.</summary>
public static class InfoCommand
{
    /// <summary>Opens the file with the reader and writes what it holds. Numbers are written without separators.</summary>
    /// <param name="line">The arguments.</param>
    /// <param name="output">Where the result goes.</param>
    /// <param name="error">Where the reason goes when the reader refuses the file.</param>
    /// <returns>0 when the file opens, 1 when the reader refuses it.</returns>
    public static int Run(CommandLine line, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        PqaxDocument document;
        try
        {
            document = line.IsEmbedded ? PqaxDocument.OpenEmbedded(line.File, line.PartName) : PqaxDocument.Open(line.File);
        }
        catch (PqaxFormatException e)
        {
            error.WriteLine($"pqax info: {Path.GetFileName(line.File)} is not a PQAX file that this version reads ({e.Code}): {e.Message}");
            return Program.ExitInvalid;
        }

        using (document)
        {
            Write(document, line, output);
        }

        return Program.ExitOk;
    }

    private static void Write(PqaxDocument document, CommandLine line, TextWriter output)
    {
        Manifest manifest = document.Manifest;
        CollectionInfo collection = manifest.Collection;
        output.WriteLine($"File               {Path.GetFileName(line.File)}, {Number(new FileInfo(line.File).Length)} bytes");
        if (document.EmbeddedPart != null)
        {
            output.WriteLine($"Embedded part      {document.EmbeddedPart}, {document.EmbeddedCompression}");
        }

        output.WriteLine($"Format             {manifest.Format} {manifest.FormatVersion}");
        output.WriteLine($"Collector          {Join(collection.CollectorApp, collection.CollectorAppVersion)}");
        if (collection.CollectorLib != null)
        {
            output.WriteLine($"Library            {Join(collection.CollectorLib, collection.CollectorLibVersion)}");
        }

        output.WriteLine($"Level              {(collection.Level.HasValue ? Number(collection.Level.Value) : "unknown")}");
        if (collection.StartedAt.HasValue || collection.CompletedAt.HasValue)
        {
            output.WriteLine($"Collected          {Time(collection.StartedAt)} to {Time(collection.CompletedAt)}");
        }

        output.WriteLine($"Data values        {YesNo(collection.IncludesDataValues)}");
        output.WriteLine($"Partition values   {YesNo(collection.IncludesPartitionValues)}");
        if (manifest.ObfuscatorDictionaryId != null || manifest.ObfuscatorLib != null)
        {
            output.WriteLine($"Obfuscated         {Join(manifest.ObfuscatorLib, manifest.ObfuscatorLibVersion)}, dictionary {manifest.ObfuscatorDictionaryId ?? "unknown"}");
        }

        output.WriteLine($"Parts              {Number(manifest.Parts.Count)}");
        int width = 0;
        foreach (PartInfo info in manifest.Parts)
        {
            width = Math.Max(width, info.Name.Length);
        }

        foreach (PartInfo info in manifest.Parts)
        {
            string tables = info.Tables.Count == 0
                ? "no tables"
                : string.Join(", ", info.Tables.Select(table => $"{table.Key} {Number(table.Value)}"));
            output.WriteLine($"  {info.Name.PadRight(width)}  {tables}");
        }

        var names = new List<string>();
        foreach (PartInfo info in manifest.Parts)
        {
            foreach (string name in info.Tables.Keys)
            {
                if (!names.Contains(name))
                {
                    names.Add(name);
                }
            }
        }

        output.WriteLine($"Tables             {Number(names.Count)}");
        int tableWidth = names.Count == 0 ? 0 : names.Max(name => name.Length);
        foreach (string name in names)
        {
            int count = document.RowCount(name);
            output.WriteLine($"  {name.PadRight(tableWidth)}  {Number(count).PadLeft(10)} {(count == 1 ? "row" : "rows")}");
        }

        Summary summary = document.Summary;
        long files = 0;
        long bytes = 0;
        long rows = 0;
        foreach (TableRow table in summary.Tables)
        {
            files += table.FileCount ?? 0;
            bytes += table.FileSize ?? 0;
            rows += table.RowCount ?? 0;
        }

        output.WriteLine($"Summary            {Number(summary.Tables.Count)} tables, {Number(summary.Columns.Count)} columns, {Number(files)} files, {Number(bytes)} bytes, {Number(rows)} rows");
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Join(string? name, string? version) => name == null ? "unknown" : version == null ? name : $"{name} {version}";

    private static string YesNo(bool? value) => value.HasValue ? (value.Value ? "yes" : "no") : "unknown";

    private static string Time(DateTimeOffset? value) => value.HasValue ? value.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "unknown";
}
