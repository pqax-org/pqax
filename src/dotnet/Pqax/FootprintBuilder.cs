using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pqax;

/// <summary>
/// Builds a PQAX file: add the rows of each table, then write. In a table with ids, an Add method keeps the id that a
/// row brings, gives a row whose id is 0 the next free id, one more than the largest id so far, and returns the id; the
/// caller uses that id in the references of other rows.
/// </summary>
/// <remarks>
/// The file has <c>manifest.json</c> first, then <c>summary.json</c>, then one or more parts for each detail table, with
/// at most <see cref="FootprintBuilderOptions.RowsPerPart"/> rows in a part. JSON is compact UTF-8 without a byte order
/// mark, a field with no value in any row of a part is left out, and an empty table is left out. The Add methods and
/// <see cref="Write(Stream)"/> are safe to call from several threads. Write refuses a table with a repeated id or an id
/// that is not positive. Apart from the ids, the builder does not validate what it writes: <see cref="PqaxValidator"/> does.
/// </remarks>
public sealed class FootprintBuilder
{
    /// <summary>The version of the format that the builder writes.</summary>
    internal const string WrittenFormatVersion = "0.1";

    private const string SummaryPartName = "summary.json";

    private static readonly HashSet<string> ManifestMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "format", "formatVersion", "collection", "obfuscatorDictionaryId", "obfuscatorLib", "obfuscatorLibVersion", "parts",
    };

    private static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private readonly object sync = new object();
    private readonly BuilderTable<SourceRow> sources = new BuilderTable<SourceRow>(Schemas.Sources);
    private readonly BuilderTable<TableRow> tables = new BuilderTable<TableRow>(Schemas.Tables);
    private readonly BuilderTable<ColumnRow> columns = new BuilderTable<ColumnRow>(Schemas.Columns);
    private readonly BuilderTable<PartitionSpecRow> partitionSpecs = new BuilderTable<PartitionSpecRow>(Schemas.PartitionSpecs);
    private readonly BuilderTable<PartitionFieldRow> partitionFields = new BuilderTable<PartitionFieldRow>(Schemas.PartitionFields);
    private readonly BuilderTable<SortOrderRow> sortOrders = new BuilderTable<SortOrderRow>(Schemas.SortOrders);
    private readonly BuilderTable<SortFieldRow> sortFields = new BuilderTable<SortFieldRow>(Schemas.SortFields);
    private readonly BuilderTable<PhysicalSchemaRow> physicalSchemas = new BuilderTable<PhysicalSchemaRow>(Schemas.PhysicalSchemas);
    private readonly BuilderTable<PhysicalColumnRow> physicalColumns = new BuilderTable<PhysicalColumnRow>(Schemas.PhysicalColumns);
    private readonly BuilderTable<WriterRow> writers = new BuilderTable<WriterRow>(Schemas.Writers);
    private readonly BuilderTable<OperationRow> operations = new BuilderTable<OperationRow>(Schemas.Operations);
    private readonly BuilderTable<IssueRow> issues = new BuilderTable<IssueRow>(Schemas.Issues);
    private readonly BuilderTable<ModelBindingRow> modelBindings = new BuilderTable<ModelBindingRow>(Schemas.ModelBindings);
    private readonly BuilderTable<TableBindingRow> tableBindings = new BuilderTable<TableBindingRow>(Schemas.TableBindings);
    private readonly BuilderTable<ColumnBindingRow> columnBindings = new BuilderTable<ColumnBindingRow>(Schemas.ColumnBindings);
    private readonly BuilderTable<PartitionRow> partitions = new BuilderTable<PartitionRow>(Schemas.Partitions);
    private readonly BuilderTable<FileRow> files = new BuilderTable<FileRow>(Schemas.Files);
    private readonly BuilderTable<RowGroupRow> rowGroups = new BuilderTable<RowGroupRow>(Schemas.RowGroups);
    private readonly BuilderTable<ColumnChunkRow> columnChunks = new BuilderTable<ColumnChunkRow>(Schemas.ColumnChunks);
    private readonly BuilderTable<DeleteFileRow> deleteFiles = new BuilderTable<DeleteFileRow>(Schemas.DeleteFiles);
    private readonly BuilderTable<CommitRow> commits = new BuilderTable<CommitRow>(Schemas.Commits);
    private readonly List<UnknownTable> unknownTables = new List<UnknownTable>();
    private readonly OrderedMap<JsonElement> manifestExtensions = new OrderedMap<JsonElement>();

    /// <summary>Creates a builder for a footprint collected as <paramref name="collection"/> says.</summary>
    /// <param name="collection">How and when the footprint was collected. It is written as the collection of the manifest when the file is written, so later changes to it count.</param>
    public FootprintBuilder(CollectionInfo collection)
    {
        Collection = collection ?? throw new ArgumentNullException(nameof(collection));
    }

    /// <summary>How and when the footprint was collected.</summary>
    public CollectionInfo Collection { get; }

    /// <summary>How the file is written: the rows in a detail part and the compression level.</summary>
    public FootprintBuilderOptions Options { get; } = new FootprintBuilderOptions();

    /// <summary>Set when names were obfuscated, as in VPAX.</summary>
    public string? ObfuscatorDictionaryId { get; set; }

    /// <summary>The library that obfuscated the names.</summary>
    public string? ObfuscatorLib { get; set; }

    /// <summary>The version of the library that obfuscated the names.</summary>
    public string? ObfuscatorLibVersion { get; set; }

    /// <summary>
    /// A builder with every table of a document: the rows with their ids, the fields and the tables that this version
    /// does not know, the members of the manifest that it does not know, and the collection. A tool that rewrites a
    /// file, such as an obfuscator, starts here. The rows are copies, so later changes to the document do not count.
    /// </summary>
    /// <param name="document">The document to copy. Its detail tables are read.</param>
    /// <returns>The builder.</returns>
    public static FootprintBuilder From(PqaxDocument document)
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        Manifest manifest = document.Manifest;
        var builder = new FootprintBuilder(Schemas.CollectionSchema.Clone(manifest.Collection))
        {
            ObfuscatorDictionaryId = manifest.ObfuscatorDictionaryId,
            ObfuscatorLib = manifest.ObfuscatorLib,
            ObfuscatorLibVersion = manifest.ObfuscatorLibVersion,
        };
        foreach (KeyValuePair<string, JsonElement> extension in manifest.Extensions)
        {
            builder.manifestExtensions.Set(extension.Key, extension.Value);
        }

        Summary summary = document.Summary;
        builder.sources.CopyFrom(summary.Sources);
        builder.tables.CopyFrom(summary.Tables);
        builder.columns.CopyFrom(summary.Columns);
        builder.partitionSpecs.CopyFrom(summary.PartitionSpecs);
        builder.partitionFields.CopyFrom(summary.PartitionFields);
        builder.sortOrders.CopyFrom(summary.SortOrders);
        builder.sortFields.CopyFrom(summary.SortFields);
        builder.physicalSchemas.CopyFrom(summary.PhysicalSchemas);
        builder.physicalColumns.CopyFrom(summary.PhysicalColumns);
        builder.writers.CopyFrom(summary.Writers);
        builder.operations.CopyFrom(summary.Operations);
        builder.issues.CopyFrom(summary.Issues);
        builder.modelBindings.CopyFrom(summary.ModelBindings);
        builder.tableBindings.CopyFrom(summary.TableBindings);
        builder.columnBindings.CopyFrom(summary.ColumnBindings);
        builder.partitions.CopyFrom(document.ReadPartitions());
        builder.files.CopyFrom(document.ReadFiles());
        builder.rowGroups.CopyFrom(document.ReadRowGroups());
        builder.columnChunks.CopyFrom(document.ReadColumnChunks());
        builder.deleteFiles.CopyFrom(document.ReadDeleteFiles());
        builder.commits.CopyFrom(document.ReadCommits());
        foreach (string name in document.TableNames())
        {
            if (!Schemas.IsKnownTable(name))
            {
                RawTable raw = document.ReadRaw(name);
                if (raw.Count > 0)
                {
                    builder.unknownTables.Add(new UnknownTable(raw, document.IsInSummaryPart(name)));
                }
            }
        }

        return builder;
    }

    /// <summary>Adds a row to <c>sources</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="SourceRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddSource(SourceRow row) => Add(sources, row);

    /// <summary>Adds a row to <c>tables</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="TableRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddTable(TableRow row) => Add(tables, row);

    /// <summary>Adds a row to <c>columns</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="ColumnRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddColumn(ColumnRow row) => Add(columns, row);

    /// <summary>Adds a row to <c>partitionSpecs</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="PartitionSpecRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddPartitionSpec(PartitionSpecRow row) => Add(partitionSpecs, row);

    /// <summary>Adds a row to <c>partitionFields</c>, which has no ids.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The position of the row in its table, from 0.</returns>
    public long AddPartitionField(PartitionFieldRow row) => Add(partitionFields, row);

    /// <summary>Adds a row to <c>sortOrders</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="SortOrderRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddSortOrder(SortOrderRow row) => Add(sortOrders, row);

    /// <summary>Adds a row to <c>sortFields</c>, which has no ids.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The position of the row in its table, from 0.</returns>
    public long AddSortField(SortFieldRow row) => Add(sortFields, row);

    /// <summary>Adds a row to <c>physicalSchemas</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="PhysicalSchemaRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddPhysicalSchema(PhysicalSchemaRow row) => Add(physicalSchemas, row);

    /// <summary>Adds a row to <c>physicalColumns</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="PhysicalColumnRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddPhysicalColumn(PhysicalColumnRow row) => Add(physicalColumns, row);

    /// <summary>Adds a row to <c>writers</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="WriterRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddWriter(WriterRow row) => Add(writers, row);

    /// <summary>Adds a row to <c>operations</c>, which has no ids.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The position of the row in its table, from 0.</returns>
    public long AddOperation(OperationRow row) => Add(operations, row);

    /// <summary>Adds a row to <c>issues</c>, which has no ids.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The position of the row in its table, from 0.</returns>
    public long AddIssue(IssueRow row) => Add(issues, row);

    /// <summary>Adds a row to <c>modelBindings</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="ModelBindingRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddModelBinding(ModelBindingRow row) => Add(modelBindings, row);

    /// <summary>Adds a row to <c>tableBindings</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="TableBindingRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddTableBinding(TableBindingRow row) => Add(tableBindings, row);

    /// <summary>Adds a row to <c>columnBindings</c>, which has no ids.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The position of the row in its table, from 0.</returns>
    public long AddColumnBinding(ColumnBindingRow row) => Add(columnBindings, row);

    /// <summary>Adds a row to <c>partitions</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="PartitionRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddPartition(PartitionRow row) => Add(partitions, row);

    /// <summary>Adds a row to <c>files</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="FileRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddFile(FileRow row) => Add(files, row);

    /// <summary>Adds a row to <c>rowGroups</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="RowGroupRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddRowGroup(RowGroupRow row) => Add(rowGroups, row);

    /// <summary>Adds a row to <c>columnChunks</c>, which has no ids.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The position of the row in its table, from 0.</returns>
    public long AddColumnChunk(ColumnChunkRow row) => Add(columnChunks, row);

    /// <summary>Adds a row to <c>deleteFiles</c>. A row keeps the id that it brings; a row whose id is 0 gets the next free id.</summary>
    /// <param name="row">The row. When its <see cref="DeleteFileRow.Id"/> is 0, it is set.</param>
    /// <returns>The id of the row.</returns>
    public long AddDeleteFile(DeleteFileRow row) => Add(deleteFiles, row);

    /// <summary>Adds a row to <c>commits</c>, which has no ids.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The position of the row in its table, from 0.</returns>
    public long AddCommit(CommitRow row) => Add(commits, row);

    /// <summary>Writes the PQAX file to a stream. The stream is left open.</summary>
    /// <param name="stream">A writable stream.</param>
    /// <exception cref="InvalidOperationException">A table has the same id in two rows, or an id that is not positive. The message names the table and the id. Nothing is written.</exception>
    public void Write(Stream stream)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        lock (sync)
        {
            CheckIds();
            WriteArchive(stream);
        }
    }

    /// <summary>Writes the PQAX file to a path, replacing a file that is there.</summary>
    /// <param name="path">The path of the file.</param>
    /// <exception cref="InvalidOperationException">A table has the same id in two rows, or an id that is not positive. The message names the table and the id. No file is created.</exception>
    public void Write(string path)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        lock (sync)
        {
            // The ids are checked before the file is created, so that a footprint that is refused leaves no file behind.
            CheckIds();
            using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            WriteArchive(stream);
        }
    }

    private static string PartStem(string tableName)
    {
        var stem = new StringBuilder(tableName.Length);
        foreach (char c in tableName)
        {
            bool allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_';
            stem.Append(allowed ? c : '_');
        }

        return stem.Length == 0 ? "table" : stem.ToString();
    }

    private static string UniqueName(string stem, string suffix, HashSet<string> used)
    {
        string name = stem + suffix;
        for (int n = 2; !used.Add(name); n++)
        {
            name = stem + "_" + n + suffix;
        }

        return name;
    }

    private static void WriteEntry(ZipArchive archive, string name, DateTimeOffset stamp, CompressionLevel level, Action<Utf8JsonWriter> write)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, level);
        entry.LastWriteTime = stamp;
        using Stream stream = entry.Open();
        using var unflushed = new NonFlushingStream(stream);
        using var writer = new Utf8JsonWriter(unflushed, WriterOptions);
        write(writer);
        writer.Flush();
    }

    /// <summary>Refuses a table with a repeated id, or an id that is not positive, before anything is written.</summary>
    private void CheckIds()
    {
        foreach (ITableWriter table in SummaryTables())
        {
            table.CheckIds();
        }

        foreach (ITableWriter table in DetailTables())
        {
            table.CheckIds();
        }
    }

    private void WriteArchive(Stream stream)
    {
        List<PartPlan> plan = Plan();
        DateTimeOffset stamp = EntryTime();
        CompressionLevel level = Options.CompressionLevel;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, true);
        WriteEntry(archive, "manifest.json", stamp, level, writer => WriteManifest(writer, plan));
        foreach (PartPlan part in plan)
        {
            WriteEntry(archive, part.Name, stamp, level, writer =>
            {
                writer.WriteStartObject();
                foreach (PartSlice slice in part.Slices)
                {
                    slice.Table.Write(writer, slice.Start, slice.Count);
                }

                writer.WriteEndObject();
            });
        }
    }

    private long Add<T>(BuilderTable<T> table, T row)
        where T : class
    {
        if (row == null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        lock (sync)
        {
            return table.Add(row);
        }
    }

    private IEnumerable<ITableWriter> SummaryTables()
    {
        yield return sources;
        yield return tables;
        yield return columns;
        yield return partitionSpecs;
        yield return partitionFields;
        yield return sortOrders;
        yield return sortFields;
        yield return physicalSchemas;
        yield return physicalColumns;
        yield return writers;
        yield return operations;
        yield return issues;
        yield return modelBindings;
        yield return tableBindings;
        yield return columnBindings;
        foreach (UnknownTable table in unknownTables)
        {
            if (table.IsSummary)
            {
                yield return table;
            }
        }
    }

    private IEnumerable<ITableWriter> DetailTables()
    {
        yield return partitions;
        yield return files;
        yield return rowGroups;
        yield return columnChunks;
        yield return deleteFiles;
        yield return commits;
        foreach (UnknownTable table in unknownTables)
        {
            if (!table.IsSummary)
            {
                yield return table;
            }
        }
    }

    /// <summary>The parts: summary.json, always, then the detail tables, split at <see cref="FootprintBuilderOptions.RowsPerPart"/>.</summary>
    private List<PartPlan> Plan()
    {
        var summary = new PartPlan(SummaryPartName);
        foreach (ITableWriter table in SummaryTables())
        {
            if (table.Count > 0)
            {
                summary.Slices.Add(new PartSlice(table, 0, table.Count));
            }
        }

        var plan = new List<PartPlan> { summary };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json", SummaryPartName };
        int perPart = Options.RowsPerPart;
        foreach (ITableWriter table in DetailTables())
        {
            if (table.Count == 0)
            {
                continue;
            }

            int count = (int)((table.Count + (long)perPart - 1) / perPart);
            string stem = PartStem(table.Name);
            for (int k = 0; k < count; k++)
            {
                string name = UniqueName(count == 1 ? stem : stem + "." + (k + 1), ".json", used);
                int start = k * perPart;
                var part = new PartPlan(name);
                part.Slices.Add(new PartSlice(table, start, Math.Min(perPart, table.Count - start)));
                plan.Add(part);
            }
        }

        return plan;
    }

    /// <summary>The time of the zip entries: when the collection completed, so that the same footprint gives the same bytes.</summary>
    private DateTimeOffset EntryTime()
    {
        DateTimeOffset time = Collection.CompletedAt ?? Collection.StartedAt ?? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var earliest = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var latest = new DateTimeOffset(2107, 12, 31, 0, 0, 0, TimeSpan.Zero);
        time = time.ToUniversalTime();
        return time < earliest ? earliest : time > latest ? latest : time;
    }

    private void WriteManifest(Utf8JsonWriter writer, List<PartPlan> plan)
    {
        writer.WriteStartObject();
        writer.WriteString("format", "pqax");
        writer.WriteString("formatVersion", WrittenFormatVersion);
        writer.WritePropertyName("collection");
        Schemas.CollectionSchema.WriteObject(writer, Collection);
        if (ObfuscatorDictionaryId != null)
        {
            writer.WriteString("obfuscatorDictionaryId", ObfuscatorDictionaryId);
        }

        if (ObfuscatorLib != null)
        {
            writer.WriteString("obfuscatorLib", ObfuscatorLib);
        }

        if (ObfuscatorLibVersion != null)
        {
            writer.WriteString("obfuscatorLibVersion", ObfuscatorLibVersion);
        }

        writer.WriteStartArray("parts");
        foreach (PartPlan part in plan)
        {
            writer.WriteStartObject();
            writer.WriteString("name", part.Name);
            writer.WriteStartObject("tables");
            foreach (PartSlice slice in part.Slices)
            {
                writer.WriteNumber(slice.Table.Name, slice.Count);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        foreach (KeyValuePair<string, JsonElement> extension in manifestExtensions)
        {
            if (!ManifestMembers.Contains(extension.Key))
            {
                writer.WritePropertyName(extension.Key);
                extension.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }
}
