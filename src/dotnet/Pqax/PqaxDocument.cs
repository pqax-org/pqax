using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Pqax;

/// <summary>
/// An open PQAX file. Opening reads the central directory of the zip, the manifest, and the summary. A detail table is
/// read from its parts only when it is asked for, and then kept. A PQAX file embedded in a VPAX file opens in place
/// when the part is stored, and from memory when it is deflated.
/// </summary>
/// <remarks>
/// The reader is tolerant: it skips a byte order mark, reads a missing field as null, ignores what it does not know,
/// and reads a value that does not have the type of the model as null. A reference to a row that the file does not
/// have keeps its number, and <see cref="PqaxTable{TRow}.FindById(long)"/> finds no row for it. The reader refuses a
/// file only when it has no <c>manifest.json</c>, when the format is not <c>pqax</c>, when it does not know the first
/// number of the format version, and when a part that it reads is not valid JSON. It then throws
/// <see cref="PqaxFormatException"/>. The methods are safe to call from several threads.
/// </remarks>
public sealed class PqaxDocument : IDisposable
{
    /// <summary>The name of the part of a VPAX file that holds the footprint: <c>Footprint.pqax</c>.</summary>
    public const string FootprintPartName = "Footprint.pqax";

    private const string SummaryPartName = "summary.json";

    private readonly object sync;
    private readonly ZipDirectory directory;
    private readonly List<PartInfo> distinctParts = new List<PartInfo>();
    private readonly List<string> summaryMembers = new List<string>();
    private Stream? ownedStream;
    private bool disposed;
    private PqaxTable<PartitionRow>? partitions;
    private PqaxTable<FileRow>? files;
    private PqaxTable<RowGroupRow>? rowGroups;
    private PqaxTable<ColumnChunkRow>? columnChunks;
    private PqaxTable<DeleteFileRow>? deleteFiles;
    private PqaxTable<CommitRow>? commits;

    private PqaxDocument(ZipDirectory directory, Manifest manifest, Stream? ownedStream, string? embeddedPart, string? embeddedCompression, object sync)
    {
        this.directory = directory;
        this.ownedStream = ownedStream;
        this.sync = sync;
        Manifest = manifest;
        EmbeddedPart = embeddedPart;
        EmbeddedCompression = embeddedCompression;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (PartInfo part in manifest.Parts)
        {
            if (seen.Add(part.Name))
            {
                distinctParts.Add(part);
            }
        }

        Summary = ReadSummary();
    }

    /// <summary>The content of <c>manifest.json</c>.</summary>
    public Manifest Manifest { get; }

    /// <summary>Every summary table, read when the document opens.</summary>
    public Summary Summary { get; }

    /// <summary>For a document opened with <c>OpenEmbedded</c>, the name of the part of the package that holds it, such as <c>Footprint.pqax</c>. Null for a PQAX file of its own.</summary>
    public string? EmbeddedPart { get; }

    /// <summary>
    /// For a document opened with <c>OpenEmbedded</c>, how the package writes the part: <c>stored</c>, read in place,
    /// or <c>deflated</c>, inflated in memory first. Null for a PQAX file of its own.
    /// </summary>
    public string? EmbeddedCompression { get; }

    /// <summary>Opens a PQAX file.</summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The document, which keeps the file open until it is disposed.</returns>
    /// <exception cref="PqaxFormatException">The file is not a PQAX file that this library reads.</exception>
    public static PqaxDocument Open(string path)
    {
        Stream stream = OpenFile(path);
        return Open(stream, false);
    }

    /// <summary>Opens a PQAX file from a stream. The parts are read on demand, so the stream must stay readable; a stream that cannot seek is copied into memory first.</summary>
    /// <param name="stream">The stream, from its start.</param>
    /// <param name="leaveOpen">True to leave the stream open when the document is disposed.</param>
    /// <returns>The document.</returns>
    /// <exception cref="PqaxFormatException">The stream does not hold a PQAX file that this library reads.</exception>
    public static PqaxDocument Open(Stream stream, bool leaveOpen = false)
    {
        Stream source = Archive.Seekable(stream, leaveOpen, out Stream? owned);
        try
        {
            var sync = new object();
            return Create(new ByteRange(source, 0, source.Length, sync), owned, null, null, sync);
        }
        catch
        {
            owned?.Dispose();
            throw;
        }
    }

    /// <summary>Opens the PQAX file that a VPAX file embeds as one of its parts.</summary>
    /// <param name="vpaxPath">The path of the VPAX file.</param>
    /// <param name="partName">The name of the part, <c>Footprint.pqax</c> by default.</param>
    /// <returns>The document.</returns>
    /// <exception cref="PqaxFormatException">The package has no part with that name (<c>embedded.partMissing</c>), or the part is not a PQAX file that this library reads.</exception>
    public static PqaxDocument OpenEmbedded(string vpaxPath, string partName = FootprintPartName)
    {
        Stream stream = OpenFile(vpaxPath);
        return OpenEmbedded(stream, partName, false);
    }

    /// <summary>
    /// Opens the PQAX file that a VPAX file embeds as one of its parts. A stored part is read in place, from the
    /// stream of the VPAX file. A deflated part is inflated into memory first.
    /// </summary>
    /// <param name="vpaxStream">The stream of the VPAX file, from its start.</param>
    /// <param name="partName">The name of the part, <c>Footprint.pqax</c> by default.</param>
    /// <param name="leaveOpen">True to leave the stream open when the document is disposed.</param>
    /// <returns>The document.</returns>
    /// <exception cref="PqaxFormatException">The package has no part with that name (<c>embedded.partMissing</c>), or the part is not a PQAX file that this library reads.</exception>
    public static PqaxDocument OpenEmbedded(Stream vpaxStream, string partName = FootprintPartName, bool leaveOpen = false)
    {
        if (partName == null)
        {
            throw new ArgumentNullException(nameof(partName));
        }

        Stream source = Archive.Seekable(vpaxStream, leaveOpen, out Stream? owned);
        try
        {
            var sync = new object();
            ByteRange range = Archive.LocateEmbedded(new ByteRange(source, 0, source.Length, sync), partName, out string name, out MemoryStream? inflated);
            if (inflated == null)
            {
                return Create(range, owned, name, "stored", sync);
            }

            // The footprint is in memory now, so the package is no longer needed.
            owned?.Dispose();
            owned = inflated;
            return Create(range, inflated, name, "deflated", sync);
        }
        catch
        {
            owned?.Dispose();
            throw;
        }
    }

    /// <summary>The number of rows of a table, from the manifest: the sum over the parts that hold it. Zero for a table that the file does not have.</summary>
    /// <param name="tableName">The name of the table in the file, such as <c>columnChunks</c>.</param>
    /// <returns>The number of rows.</returns>
    public int RowCount(string tableName)
    {
        if (tableName == null)
        {
            throw new ArgumentNullException(nameof(tableName));
        }

        long total = 0;
        foreach (PartInfo part in distinctParts)
        {
            if (part.Tables.TryGetValue(tableName, out int rows))
            {
                total += rows;
            }
        }

        return (int)Math.Min(total, int.MaxValue);
    }

    /// <summary>The <c>partitions</c> table, from all its parts. Empty when the file has none.</summary>
    /// <returns>The table, read on the first call and kept.</returns>
    /// <exception cref="PqaxFormatException">A part of the table is missing from the archive (<c>manifest.partMissing</c>), cannot be read, or is not valid JSON.</exception>
    public PqaxTable<PartitionRow> ReadPartitions() => ReadDetail(ref partitions, Schemas.Partitions);

    /// <summary>The <c>files</c> table, from all its parts. Empty when the file has none.</summary>
    /// <returns>The table, read on the first call and kept.</returns>
    /// <exception cref="PqaxFormatException">A part of the table is missing from the archive (<c>manifest.partMissing</c>), cannot be read, or is not valid JSON.</exception>
    public PqaxTable<FileRow> ReadFiles() => ReadDetail(ref files, Schemas.Files);

    /// <summary>The <c>rowGroups</c> table, from all its parts. Empty when the file has none.</summary>
    /// <returns>The table, read on the first call and kept.</returns>
    /// <exception cref="PqaxFormatException">A part of the table is missing from the archive (<c>manifest.partMissing</c>), cannot be read, or is not valid JSON.</exception>
    public PqaxTable<RowGroupRow> ReadRowGroups() => ReadDetail(ref rowGroups, Schemas.RowGroups);

    /// <summary>The <c>columnChunks</c> table, from all its parts. Empty when the file has none.</summary>
    /// <returns>The table, read on the first call and kept.</returns>
    /// <exception cref="PqaxFormatException">A part of the table is missing from the archive (<c>manifest.partMissing</c>), cannot be read, or is not valid JSON.</exception>
    public PqaxTable<ColumnChunkRow> ReadColumnChunks() => ReadDetail(ref columnChunks, Schemas.ColumnChunks);

    /// <summary>The <c>deleteFiles</c> table, from all its parts. Empty when the file has none.</summary>
    /// <returns>The table, read on the first call and kept.</returns>
    /// <exception cref="PqaxFormatException">A part of the table is missing from the archive (<c>manifest.partMissing</c>), cannot be read, or is not valid JSON.</exception>
    public PqaxTable<DeleteFileRow> ReadDeleteFiles() => ReadDetail(ref deleteFiles, Schemas.DeleteFiles);

    /// <summary>The <c>commits</c> table, from all its parts. Empty when the file has none.</summary>
    /// <returns>The table, read on the first call and kept.</returns>
    /// <exception cref="PqaxFormatException">A part of the table is missing from the archive (<c>manifest.partMissing</c>), cannot be read, or is not valid JSON.</exception>
    public PqaxTable<CommitRow> ReadCommits() => ReadDetail(ref commits, Schemas.Commits);

    /// <summary>
    /// Any table, summary or detail, known or not, with every field as JSON values, from all the parts that the
    /// manifest lists for it. The result is not kept: each call reads the parts again. Empty when the file has none.
    /// </summary>
    /// <exception cref="PqaxFormatException">A part of the table is missing from the archive (<c>manifest.partMissing</c>), cannot be read, or is not valid JSON.</exception>
    /// <param name="tableName">The name of the table in the file.</param>
    /// <returns>The table.</returns>
    public RawTable ReadRaw(string tableName)
    {
        if (tableName == null)
        {
            throw new ArgumentNullException(nameof(tableName));
        }

        lock (sync)
        {
            ThrowIfDisposed();
            var reader = new RawTableReader(tableName);
            bool listed = ReadParts(tableName, reader);
            if (!listed && summaryMembers.Contains(tableName))
            {
                // A table that summary.json holds and that the manifest does not list is still read.
                ReadPart(SummaryPartName, tableName, reader);
            }

            return reader.Build();
        }
    }

    /// <summary>Closes the file or the stream, unless the document was opened with <c>leaveOpen</c>.</summary>
    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            ownedStream?.Dispose();
            ownedStream = null;
        }
    }

    /// <summary>The names of the tables that the manifest lists or that summary.json holds, in the order of the file.</summary>
    internal IReadOnlyList<string> TableNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (PartInfo part in distinctParts)
        {
            foreach (string name in part.Tables.Keys)
            {
                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }
        }

        foreach (string name in summaryMembers)
        {
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Whether a table is in summary.json: listed there by the manifest, or found there.</summary>
    internal bool IsInSummaryPart(string tableName)
    {
        foreach (PartInfo part in distinctParts)
        {
            if (part.Name == SummaryPartName)
            {
                return part.Tables.ContainsKey(tableName) || summaryMembers.Contains(tableName);
            }
        }

        return summaryMembers.Contains(tableName);
    }

    private static Stream OpenFile(string path)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
    }

    private static PqaxDocument Create(ByteRange range, Stream? owned, string? embeddedPart, string? embeddedCompression, object sync)
    {
        ZipDirectory directory = Archive.OpenDirectory(range, "file");
        ZipEntry entry = directory.Find("manifest.json")
            ?? throw new PqaxFormatException("container.manifestMissing", "The file has no manifest.json entry, so it is not a PQAX file.");
        Manifest manifest = ManifestReader.Read(Archive.ReadEntry(directory, entry, Archive.MaxPartSize));
        return new PqaxDocument(directory, manifest, owned, embeddedPart, embeddedCompression, sync);
    }

    private Summary ReadSummary()
    {
        var context = new ReadContext();
        var sources = new TableReader<SourceRow>(Schemas.Sources, context);
        var tables = new TableReader<TableRow>(Schemas.Tables, context);
        var columns = new TableReader<ColumnRow>(Schemas.Columns, context);
        var partitionSpecs = new TableReader<PartitionSpecRow>(Schemas.PartitionSpecs, context);
        var partitionFields = new TableReader<PartitionFieldRow>(Schemas.PartitionFields, context);
        var sortOrders = new TableReader<SortOrderRow>(Schemas.SortOrders, context);
        var sortFields = new TableReader<SortFieldRow>(Schemas.SortFields, context);
        var physicalSchemas = new TableReader<PhysicalSchemaRow>(Schemas.PhysicalSchemas, context);
        var physicalColumns = new TableReader<PhysicalColumnRow>(Schemas.PhysicalColumns, context);
        var writers = new TableReader<WriterRow>(Schemas.Writers, context);
        var operations = new TableReader<OperationRow>(Schemas.Operations, context);
        var issues = new TableReader<IssueRow>(Schemas.Issues, context);
        var modelBindings = new TableReader<ModelBindingRow>(Schemas.ModelBindings, context);
        var tableBindings = new TableReader<TableBindingRow>(Schemas.TableBindings, context);
        var columnBindings = new TableReader<ColumnBindingRow>(Schemas.ColumnBindings, context);
        var readers = new Dictionary<string, ITableReader>(StringComparer.Ordinal)
        {
            ["sources"] = sources,
            ["tables"] = tables,
            ["columns"] = columns,
            ["partitionSpecs"] = partitionSpecs,
            ["partitionFields"] = partitionFields,
            ["sortOrders"] = sortOrders,
            ["sortFields"] = sortFields,
            ["physicalSchemas"] = physicalSchemas,
            ["physicalColumns"] = physicalColumns,
            ["writers"] = writers,
            ["operations"] = operations,
            ["issues"] = issues,
            ["modelBindings"] = modelBindings,
            ["tableBindings"] = tableBindings,
            ["columnBindings"] = columnBindings,
        };

        // A summary.json that the manifest lists and the archive does not have gives empty summary tables: the reader
        // refuses a file only for the reasons of the format.
        PartInfo? summaryPart = distinctParts.Find(part => part.Name == SummaryPartName);
        byte[]? bytes = summaryPart == null || directory.Find(SummaryPartName) == null ? null : ReadPartBytes(SummaryPartName);
        if (summaryPart != null && bytes != null)
        {
            foreach (ITableReader reader in readers.Values)
            {
                reader.BeginPart();
            }

            PartScanner.Scan(bytes, SummaryPartName, (string name, ref Utf8JsonReader json) =>
            {
                if (json.TokenType == JsonTokenType.StartObject && !summaryMembers.Contains(name))
                {
                    summaryMembers.Add(name);
                }

                if (readers.TryGetValue(name, out ITableReader? reader))
                {
                    reader.ReadTable(ref json);
                }
                else
                {
                    JsonRead.SkipValue(ref json);
                }
            });
        }

        return new Summary(
            sources.Build(), tables.Build(), columns.Build(), partitionSpecs.Build(), partitionFields.Build(), sortOrders.Build(),
            sortFields.Build(), physicalSchemas.Build(), physicalColumns.Build(), writers.Build(), operations.Build(), issues.Build(),
            modelBindings.Build(), tableBindings.Build(), columnBindings.Build());
    }

    private PqaxTable<T> ReadDetail<T>(ref PqaxTable<T>? cache, Schema<T> schema)
        where T : class
    {
        lock (sync)
        {
            if (cache != null)
            {
                return cache;
            }

            ThrowIfDisposed();
            var reader = new TableReader<T>(schema, new ReadContext());
            ReadParts(schema.Name, reader);
            cache = reader.Build();
            return cache;
        }
    }

    /// <summary>Reads a table from every part that the manifest lists for it. Returns false when no part lists it.</summary>
    private bool ReadParts(string tableName, ITableReader reader)
    {
        bool listed = false;
        foreach (PartInfo part in distinctParts)
        {
            if (part.Tables.ContainsKey(tableName))
            {
                listed = true;
                ReadPart(part.Name, tableName, reader);
            }
        }

        return listed;
    }

    private void ReadPart(string partName, string tableName, ITableReader reader)
    {
        byte[] bytes = ReadPartBytes(partName);
        reader.BeginPart();
        PartScanner.Scan(bytes, partName, (string name, ref Utf8JsonReader json) =>
        {
            if (name == tableName)
            {
                reader.ReadTable(ref json);
            }
            else
            {
                JsonRead.SkipValue(ref json);
            }
        });
    }

    /// <summary>The bytes of a part. A part that the manifest lists and the archive does not have cannot be read.</summary>
    private byte[] ReadPartBytes(string partName)
    {
        ZipEntry entry = directory.Find(partName)
            ?? throw new PqaxFormatException("manifest.partMissing", $"The manifest lists the part {partName}, and the archive has no entry with that name.");
        return Archive.ReadEntry(directory, entry, Archive.MaxPartSize);
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(PqaxDocument));
        }
    }
}
