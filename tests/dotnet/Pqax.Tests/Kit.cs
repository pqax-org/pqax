using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pqax.Tests;

/// <summary>
/// What the tests share: where the conformance kit is, what it expects, and the numbers of the expected shape. The
/// numbers are computed here from the rows that the reader returns, as conformance/README.md defines them, so that the
/// tests check the library and not a copy of its code.
/// </summary>
public static class Kit
{
    public const string EmbeddedPart = "Footprint.pqax";

    public static string Root { get; } = FindRoot();

    public static string Conformance => Path.Combine(Root, "conformance");

    public static string Samples => Path.Combine(Conformance, "samples");

    public static string Invalid => Path.Combine(Samples, "invalid");

    public static string ExpectedFolder => Path.Combine(Conformance, "expected");

    public static readonly string[] SummaryTables =
    [
        "sources", "tables", "columns", "partitionSpecs", "partitionFields", "sortOrders", "sortFields",
        "physicalSchemas", "physicalColumns", "writers", "operations", "issues", "modelBindings", "tableBindings",
        "columnBindings",
    ];

    public static readonly string[] DetailTables = ["partitions", "files", "rowGroups", "columnChunks", "deleteFiles", "commits"];

    public static readonly string[] FormatTables = [.. SummaryTables, .. DetailTables];

    /// <summary>The three invalid samples that a reader refuses when it opens them, with the code of the refusal.</summary>
    public static readonly IReadOnlyDictionary<string, string> Refused = new Dictionary<string, string>
    {
        ["container.manifestMissing.pqax"] = "container.manifestMissing",
        ["manifest.format.pqax"] = "manifest.format",
        ["manifest.versionUnknown.pqax"] = "manifest.versionUnknown",
    };

    /// <summary>The codes of section 4.1 of the contracts.</summary>
    public static readonly IReadOnlySet<string> PlanCodes = new HashSet<string>
    {
        "container.method", "container.encrypted", "container.zip64", "container.partName", "container.caseCollision",
        "container.manifestMissing", "json.invalid", "json.topLevel", "json.bom", "json.duplicateMember", "json.integer",
        "json.integerRange", "json.timestamp", "json.type", "manifest.required", "manifest.format",
        "manifest.formatVersion", "manifest.versionUnknown", "manifest.firstPart", "manifest.duplicatePart",
        "manifest.partMissing", "part.tables", "part.kind", "part.singleTable", "part.tooLarge", "part.rowCount",
        "table.required", "table.idUnique", "table.reference", "depth.files", "depth.rowGroups", "depth.columnChunks",
        "totals.fileCount", "totals.fileSize", "totals.rowGroupCount", "totals.compressedSize",
        "totals.columnCompressedSize", "embedded.partMissing",
    };

    /// <summary>The keys of an expected file that a reader computes.</summary>
    public static readonly string[] ShapeKeys =
    [
        "format", "formatVersion", "parts", "rows", "tablesFileSize", "tablesRowCount", "columnChunksCompressedSize",
        "tables", "largestColumns",
    ];

    private static string FindRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            if (Directory.Exists(Path.Combine(folder.FullName, "conformance")) && File.Exists(Path.Combine(folder.FullName, "Pqax.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException($"No folder with conformance/ and Pqax.slnx above {AppContext.BaseDirectory}");
    }

    // ---- the kit ----

    public static IEnumerable<string> ExpectedNames() =>
        Directory.GetFiles(ExpectedFolder, "*.json").Select(path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.Ordinal);

    public static JsonObject Expected(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ExpectedFolder, name + ".json")))!.AsObject();

    public static string SamplePath(JsonObject expected) => Path.Combine(Samples, (string)expected["file"]!);

    public static string? ExpectedPart(JsonObject expected) => (string?)expected["embeddedPart"];

    public static JsonObject InvalidCases() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Invalid, "expected.json")))!.AsObject();

    public static IReadOnlyList<string> InvalidCodes(string file) =>
        InvalidCases()[file]!["codes"]!.AsArray().Select(code => (string)code!).ToList();

    public static TheoryData<string> Data(IEnumerable<string> values)
    {
        var data = new TheoryData<string>();
        foreach (string value in values)
        {
            data.Add(value);
        }

        return data;
    }

    public static PqaxDocument OpenSample(JsonObject expected)
    {
        string? part = ExpectedPart(expected);
        return part is null ? PqaxDocument.Open(SamplePath(expected)) : PqaxDocument.OpenEmbedded(SamplePath(expected), part);
    }

    public static PqaxDocument OpenSample(string name) => OpenSample(Expected(name));

    public static byte[] SampleBytes(string file) => File.ReadAllBytes(Path.Combine(Samples, file));

    public static byte[] InvalidBytes(string file) => File.ReadAllBytes(Path.Combine(Invalid, file));

    // ---- the expected shape ----

    /// <summary>The tables that the manifest states, in the order in which they first appear.</summary>
    public static IReadOnlyList<string> StatedTables(Manifest manifest)
    {
        var names = new List<string>();
        foreach (PartInfo part in manifest.Parts)
        {
            names.AddRange(part.Tables.Keys.Where(name => !names.Contains(name)));
        }

        return names;
    }

    /// <summary>The rows of a table of the format, read whole, and the fields that this version does not know.</summary>
    public static (int Count, IReadOnlyDictionary<string, IReadOnlyList<JsonElement?>> Extra) Typed(PqaxDocument doc, string name) => name switch
    {
        "sources" => (doc.Summary.Sources.Count, doc.Summary.Sources.ExtraFields),
        "tables" => (doc.Summary.Tables.Count, doc.Summary.Tables.ExtraFields),
        "columns" => (doc.Summary.Columns.Count, doc.Summary.Columns.ExtraFields),
        "partitionSpecs" => (doc.Summary.PartitionSpecs.Count, doc.Summary.PartitionSpecs.ExtraFields),
        "partitionFields" => (doc.Summary.PartitionFields.Count, doc.Summary.PartitionFields.ExtraFields),
        "sortOrders" => (doc.Summary.SortOrders.Count, doc.Summary.SortOrders.ExtraFields),
        "sortFields" => (doc.Summary.SortFields.Count, doc.Summary.SortFields.ExtraFields),
        "physicalSchemas" => (doc.Summary.PhysicalSchemas.Count, doc.Summary.PhysicalSchemas.ExtraFields),
        "physicalColumns" => (doc.Summary.PhysicalColumns.Count, doc.Summary.PhysicalColumns.ExtraFields),
        "writers" => (doc.Summary.Writers.Count, doc.Summary.Writers.ExtraFields),
        "operations" => (doc.Summary.Operations.Count, doc.Summary.Operations.ExtraFields),
        "issues" => (doc.Summary.Issues.Count, doc.Summary.Issues.ExtraFields),
        "modelBindings" => (doc.Summary.ModelBindings.Count, doc.Summary.ModelBindings.ExtraFields),
        "tableBindings" => (doc.Summary.TableBindings.Count, doc.Summary.TableBindings.ExtraFields),
        "columnBindings" => (doc.Summary.ColumnBindings.Count, doc.Summary.ColumnBindings.ExtraFields),
        "partitions" => (doc.ReadPartitions().Count, doc.ReadPartitions().ExtraFields),
        "files" => (doc.ReadFiles().Count, doc.ReadFiles().ExtraFields),
        "rowGroups" => (doc.ReadRowGroups().Count, doc.ReadRowGroups().ExtraFields),
        "columnChunks" => (doc.ReadColumnChunks().Count, doc.ReadColumnChunks().ExtraFields),
        "deleteFiles" => (doc.ReadDeleteFiles().Count, doc.ReadDeleteFiles().ExtraFields),
        "commits" => (doc.ReadCommits().Count, doc.ReadCommits().ExtraFields),
        _ => throw new ArgumentException($"{name} is not a table of the format", nameof(name)),
    };

    /// <summary>The numbers of an expected file, computed from the rows that the reader returns.</summary>
    public static JsonObject Shape(PqaxDocument doc)
    {
        PqaxTable<TableRow> tables = doc.Summary.Tables;
        PqaxTable<ColumnChunkRow> chunks = doc.ReadColumnChunks();
        var schemaTable = new Dictionary<long, long?>();
        foreach (PhysicalSchemaRow schema in doc.Summary.PhysicalSchemas)
        {
            schemaTable[schema.Id] = schema.Table;
        }

        var columnSchema = new Dictionary<long, long?>();
        var columnColumn = new Dictionary<long, long?>();
        foreach (PhysicalColumnRow physical in doc.Summary.PhysicalColumns)
        {
            columnSchema[physical.Id] = physical.PhysicalSchema;
            columnColumn[physical.Id] = physical.Column;
        }

        var count = new Dictionary<long, long>();
        var size = new Dictionary<long, long>();
        var byColumn = new Dictionary<long, long>();
        foreach (ColumnChunkRow chunk in chunks)
        {
            long compressed = chunk.CompressedSize ?? 0;
            if (chunk.PhysicalColumn is not long physical)
            {
                continue;
            }

            if (columnSchema.TryGetValue(physical, out long? schema) && schema is long schemaId &&
                schemaTable.TryGetValue(schemaId, out long? owner) && owner is long table)
            {
                count[table] = count.GetValueOrDefault(table) + 1;
                size[table] = size.GetValueOrDefault(table) + compressed;
            }

            if (columnColumn.TryGetValue(physical, out long? column) && column is long columnId)
            {
                byColumn[columnId] = byColumn.GetValueOrDefault(columnId) + compressed;
            }
        }

        var rows = new JsonObject();
        foreach (string name in StatedTables(doc.Manifest))
        {
            rows[name] = doc.RowCount(name);
        }

        return new JsonObject
        {
            ["format"] = doc.Manifest.Format,
            ["formatVersion"] = doc.Manifest.FormatVersion,
            ["parts"] = new JsonArray(doc.Manifest.Parts.Select(part => (JsonNode?)JsonValue.Create(part.Name)).ToArray()),
            ["rows"] = rows,
            ["tablesFileSize"] = tables.Sum(table => table.FileSize ?? 0),
            ["tablesRowCount"] = tables.Sum(table => table.RowCount ?? 0),
            ["columnChunksCompressedSize"] = chunks.Sum(chunk => chunk.CompressedSize ?? 0),
            ["tables"] = new JsonArray(tables.Select(table => (JsonNode?)new JsonObject
            {
                ["id"] = table.Id,
                ["fileCount"] = table.FileCount,
                ["columnChunkCount"] = count.GetValueOrDefault(table.Id),
                ["columnChunkCompressedSize"] = size.GetValueOrDefault(table.Id),
            }).ToArray()),
            ["largestColumns"] = new JsonArray(byColumn.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(3)
                .Select(pair => (JsonNode?)new JsonObject { ["id"] = pair.Key, ["compressedSize"] = pair.Value }).ToArray()),
        };
    }

    /// <summary>The keys whose values differ, with both values.</summary>
    public static List<string> Differences(JsonObject actual, JsonObject expected, IEnumerable<string> keys) =>
        keys.Where(key => Canonical(actual[key]) != Canonical(expected[key]))
            .Select(key => $"{key}: got {Canonical(actual[key])}, expected {Canonical(expected[key])}")
            .ToList();

    /// <summary>JSON text with the members of every object sorted, to compare two values.</summary>
    public static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(Canonical)) + "]",
        _ => node.ToJsonString(),
    };

    public static string Canonical(JsonElement? element) =>
        element is null ? "null" : Canonical(JsonNode.Parse(element.Value.GetRawText()));

    // ---- zip archives ----

    /// <summary>For each entry, the bytes from its local header to the next header or to the central directory.</summary>
    public static Dictionary<string, (long Start, long Stop)> EntrySpans(byte[] zip, long offset = 0)
    {
        (List<(string Name, long Offset, int Method, long CompressedSize)> entries, long directory) = CentralDirectory(zip);
        var sorted = entries.OrderBy(entry => entry.Offset).ToList();
        var spans = new Dictionary<string, (long, long)>();
        for (int i = 0; i < sorted.Count; i++)
        {
            long stop = i + 1 < sorted.Count ? sorted[i + 1].Offset : directory;
            spans[sorted[i].Name] = (offset + sorted[i].Offset, offset + stop);
        }

        return spans;
    }

    public static (List<(string Name, long Offset, int Method, long CompressedSize)> Entries, long Directory) CentralDirectory(byte[] zip)
    {
        int end = -1;
        for (int i = zip.Length - 22; i >= Math.Max(0, zip.Length - 22 - 65535); i--)
        {
            if (BitConverter.ToUInt32(zip, i) == 0x06054b50)
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            throw new InvalidDataException("not a zip archive");
        }

        int count = BitConverter.ToUInt16(zip, end + 10);
        long directory = BitConverter.ToUInt32(zip, end + 16);
        var entries = new List<(string, long, int, long)>();
        long p = directory;
        for (int i = 0; i < count; i++)
        {
            int method = BitConverter.ToUInt16(zip, (int)p + 10);
            long compressed = BitConverter.ToUInt32(zip, (int)p + 20);
            int nameLength = BitConverter.ToUInt16(zip, (int)p + 28);
            int extraLength = BitConverter.ToUInt16(zip, (int)p + 30);
            int commentLength = BitConverter.ToUInt16(zip, (int)p + 32);
            long offset = BitConverter.ToUInt32(zip, (int)p + 42);
            entries.Add((Encoding.UTF8.GetString(zip, (int)p + 46, nameLength), offset, method, compressed));
            p += 46 + nameLength + extraLength + commentLength;
        }

        return (entries, directory);
    }

    /// <summary>Where the data of an entry starts, after its local header.</summary>
    public static long DataOffset(byte[] zip, long headerOffset) =>
        headerOffset + 30 + BitConverter.ToUInt16(zip, (int)headerOffset + 26) + BitConverter.ToUInt16(zip, (int)headerOffset + 28);

    public static HashSet<string> Touched(Dictionary<string, (long Start, long Stop)> spans, IEnumerable<(long Start, long Stop)> ranges)
    {
        var list = ranges.ToList();
        return spans.Where(span => list.Any(range => range.Start < span.Value.Stop && span.Value.Start < range.Stop))
            .Select(span => span.Key).ToHashSet();
    }

    /// <summary>The content of every entry of a zip archive, in order.</summary>
    public static List<(string Name, byte[] Content)> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var result = new List<(string, byte[])>();
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using Stream stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result.Add((entry.FullName, buffer.ToArray()));
        }

        return result;
    }

    /// <summary>A zip archive with these entries, deflated or stored.</summary>
    public static byte[] Zip(IEnumerable<(string Name, byte[] Content)> entries, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, level);
                entry.LastWriteTime = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
                using Stream stream = entry.Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A copy of a zip archive whose entries go through change: new content, or null to drop the entry.</summary>
    public static byte[] Rewrite(byte[] zip, Func<string, byte[], byte[]?> change) =>
        Zip(Entries(zip).Select(entry => (entry.Name, Content: change(entry.Name, entry.Content)))
            .Where(entry => entry.Content != null).Select(entry => (entry.Name, entry.Content!)));

    public static JsonNode Json(byte[] data) => JsonNode.Parse(data)!;

    public static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    /// <summary>A VPAX-like package with the entries of embedded-stored.vpax except the footprint.</summary>
    public static byte[] PackageWithoutFootprint() =>
        Rewrite(SampleBytes("embedded-stored.vpax"), (name, content) => name == EmbeddedPart ? null : content);

    /// <summary>A temporary file with these bytes, deleted when the test ends.</summary>
    public static TemporaryFile Temporary(byte[] content, string extension = ".pqax")
    {
        var file = new TemporaryFile(extension);
        File.WriteAllBytes(file.Path, content);
        return file;
    }
}

/// <summary>A file in the temporary folder, deleted on Dispose.</summary>
public sealed class TemporaryFile : IDisposable
{
    public TemporaryFile(string extension = ".pqax")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pqax-tests-" + Guid.NewGuid().ToString("N") + extension);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A seekable stream over bytes that records every range that is read.</summary>
public sealed class CountingStream : Stream
{
    private readonly MemoryStream inner;

    public CountingStream(byte[] data)
    {
        inner = new MemoryStream(data, writable: false);
    }

    public List<(long Start, long Stop)> Ranges { get; } = [];

    public bool Disposed { get; private set; }

    public override bool CanRead => !Disposed;

    public override bool CanSeek => !Disposed;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public long BytesRead => Ranges.Sum(range => range.Stop - range.Start);

    private int Record(long start, int count)
    {
        if (count > 0)
        {
            lock (Ranges)
            {
                Ranges.Add((start, start + count));
            }
        }

        return count;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long start = inner.Position;
        return Record(start, inner.Read(buffer, offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        long start = inner.Position;
        return Record(start, inner.Read(buffer));
    }

    public override int ReadByte()
    {
        long start = inner.Position;
        int value = inner.ReadByte();
        Record(start, value < 0 ? 0 : 1);
        return value;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer, offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
