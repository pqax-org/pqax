using System.Text.Json;
using System.Text.Json.Nodes;
using Pqax.Collector.ParquetNet;

namespace Pqax.Collector.Tests;

/// <summary>
/// The synthetic lakehouse and warehouse of the kit (conformance/fixtures/lakehouse), the output of the probe on it
/// (conformance/fixtures/probe-dump), and helpers to collect it. The tables, items, and their names come from the
/// files of the kit, never from the code under test.
/// </summary>
public static class Fixture
{
    public static string Root { get; } = FindRoot();

    public static string Folder => Path.Combine(Root, "conformance", "fixtures", "lakehouse");

    public static string DumpFolder => Path.Combine(Root, "conformance", "fixtures", "probe-dump");

    public static string Samples => Path.Combine(Root, "conformance", "samples");

    public static JsonObject Index { get; } = ParseFile(Path.Combine(DumpFolder, "index.json"));

    /// <summary>The tables of the probe dump, in the order of index.json.</summary>
    public static IReadOnlyList<DumpTable> Tables { get; } = LoadTables();

    /// <summary>The table whose log has a checkpoint: the lakehouse table.</summary>
    public static DumpTable CheckpointTable => Tables.Single(table => table.Delta["checkpointVersion"] is not null);

    /// <summary>The table whose log has only JSON commits, from version 0: the warehouse table.</summary>
    public static DumpTable JsonOnlyTable => Tables.Single(table => table.Delta["checkpointVersion"] is null);

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

    private static JsonObject ParseFile(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    public static TheoryData<string> Data(IEnumerable<string> values)
    {
        var data = new TheoryData<string>();
        foreach (string value in values)
        {
            data.Add(value);
        }

        return data;
    }

    private static List<DumpTable> LoadTables() =>
        Index["tables"]!.AsArray().Select(node => new DumpTable(
            (string)node!["id"]!,
            (string)node["item"]!,
            (string)node["itemKind"]!,
            node["namespace"]!.AsArray().Select(part => (string)part!).ToList(),
            (string)node["name"]!,
            ParseFile(Path.Combine(DumpFolder, (string)node["id"]! + ".json")))).ToList();

    /// <summary>The data files of the fixture, relative to its root with forward slashes: every Parquet file outside a Delta log.</summary>
    public static IReadOnlyList<string> DataFiles() =>
        Directory.EnumerateFiles(Folder, "*.parquet", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Folder, path).Replace('\\', '/'))
            .Where(path => !path.Contains("/_delta_log/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The dump of a data file, found by its size, which is distinct for every file of the fixture.</summary>
    public static (DumpTable Table, JsonObject File) DumpFileOfSize(long size)
    {
        var matches = Tables.SelectMany(table => table.Files.Select(file => (table, file))).Where(pair => Dump.Long(pair.file["size"]) == size).ToList();
        Assert.True(matches.Count == 1, $"{matches.Count} files of the dump have {size} bytes");
        return matches[0];
    }

    // ---- collecting ----

    public static async Task<IReadOnlyList<TableLocation>> DiscoverAsync(IStorage storage, CancellationToken cancellation = default)
    {
        var tables = new List<TableLocation>();
        foreach (string item in await TableDiscovery.ListItemsAsync(storage, cancellation))
        {
            tables.AddRange(await TableDiscovery.DiscoverAsync(storage, item, cancellation));
        }

        return tables;
    }

    /// <summary>Discovers the tables of the storage, the fixture by default, collects them, and writes the footprint.</summary>
    public static async Task<Collected> CollectAsync(
        CollectorOptions? options = null,
        IStorage? storage = null,
        ICheckpointReader? checkpoints = null,
        bool withoutCheckpointReader = false,
        IProgress<CollectionProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        storage ??= new LocalStorage(Folder);
        IReadOnlyList<TableLocation> tables = await DiscoverAsync(storage, cancellation);
        var statistics = storage as IStorageStatistics;
        long requests = statistics?.RequestCount ?? 0;
        long bytes = statistics?.BytesRead ?? 0;
        var collector = new PqaxCollector(storage, options ?? new CollectorOptions(), withoutCheckpointReader ? null : checkpoints ?? new ParquetNetCheckpointReader());
        FootprintBuilder builder = await collector.CollectAsync(tables, progress, cancellation);
        long requestsDuring = (statistics?.RequestCount ?? 0) - requests;
        long bytesDuring = (statistics?.BytesRead ?? 0) - bytes;
        using var stream = new MemoryStream();
        builder.Write(stream);
        return new Collected(stream.ToArray(), tables, requestsDuring, bytesDuring);
    }

    public static PqaxDocument Open(byte[] data) => PqaxDocument.Open(new MemoryStream(data));

    public static PqaxDocument OpenSample(string file) => PqaxDocument.Open(Path.Combine(Samples, file));

    public static ValidationResult Validate(byte[] data) => PqaxValidator.Validate(new MemoryStream(data));

    public static void AssertValid(byte[] data)
    {
        ValidationResult result = Validate(data);
        Assert.Empty(result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
        Assert.True(result.IsValid);
    }

    /// <summary>The collected table of a table of the dump, found by its name and namespace.</summary>
    public static TableRow TableOf(PqaxDocument doc, DumpTable dump) =>
        doc.Summary.Tables.Single(table => table.Name == dump.Name && (table.Namespace ?? []).SequenceEqual(dump.Namespace));

    // ---- comparing two files ----

    /// <summary>The tables that a manifest states, in the order in which they first appear.</summary>
    public static IReadOnlyList<string> StatedTables(PqaxDocument doc)
    {
        var names = new List<string>();
        foreach (PartInfo part in doc.Manifest.Parts)
        {
            names.AddRange(part.Tables.Keys.Where(name => !names.Contains(name)));
        }

        return names;
    }

    /// <summary>Every field of every table that differs between two files, read as JSON values with ReadRaw.</summary>
    public static List<string> RawDifferences(PqaxDocument actual, PqaxDocument expected, Func<string, string, bool>? skip = null)
    {
        var differences = new List<string>();
        foreach (string table in StatedTables(actual).Union(StatedTables(expected)))
        {
            RawTable left = actual.ReadRaw(table);
            RawTable right = expected.ReadRaw(table);
            if (left.Count != right.Count)
            {
                differences.Add($"{table}: {left.Count} rows, expected {right.Count}");
                continue;
            }

            foreach (string field in left.Fields.Keys.Union(right.Fields.Keys))
            {
                if (skip?.Invoke(table, field) == true)
                {
                    continue;
                }

                string got = Canonical(left.Fields.GetValueOrDefault(field), left.Count);
                string want = Canonical(right.Fields.GetValueOrDefault(field), right.Count);
                if (got != want)
                {
                    differences.Add($"{table}.{field}: {Short(got)}, expected {Short(want)}");
                }
            }
        }

        return differences;
    }

    private static string Short(string text) => text.Length <= 300 ? text : text[..300] + "...";

    /// <summary>A field as JSON text with sorted members; a missing field is null in every row.</summary>
    public static string Canonical(IReadOnlyList<JsonElement?>? values, int count) =>
        "[" + string.Join(",", values?.Select(Canonical) ?? Enumerable.Repeat("null", count)) + "]";

    public static string Canonical(JsonElement? element) => element is null ? "null" : Canonical(JsonNode.Parse(element.Value.GetRawText()));

    public static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(Canonical)) + "]",
        _ => node.ToJsonString(),
    };

    /// <summary>The text of every entry of a zip archive, to look for values that must not be there.</summary>
    public static string AllText(byte[] zip)
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        var text = new System.Text.StringBuilder();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            text.Append(reader.ReadToEnd()).Append('\n');
        }

        return text.ToString();
    }
}

/// <summary>A footprint written by the collector, with the tables it was given and the storage use it caused.</summary>
public sealed record Collected(byte[] Data, IReadOnlyList<TableLocation> Tables, long StorageRequests, long StorageBytes);

/// <summary>One table of the probe dump.</summary>
public sealed record DumpTable(string Id, string Item, string ItemKind, IReadOnlyList<string> Namespace, string Name, JsonObject Dump)
{
    public string ItemFolder => Item + "." + ItemKind;

    public string TablePath => ItemFolder + "/Tables/" + string.Concat(Namespace.Select(part => part + "/")) + Name;

    public JsonObject Delta => Dump["delta"]!.AsObject();

    public IReadOnlyList<JsonObject> Files => Dump["files"]!.AsArray().Select(file => file!.AsObject()).ToList();

    public IReadOnlyList<JsonObject> Commits => Dump["commits"]!.AsArray().Select(commit => commit!.AsObject()).ToList();

    public IReadOnlyList<JsonObject> Schemas => Dump["schemas"]!.AsArray().Select(schema => schema!.AsObject()).ToList();

    public override string ToString() => Id;
}

/// <summary>Reading values of the probe dump.</summary>
public static class Dump
{
    public static long Long(JsonNode? node) => node!.GetValue<long>();

    public static long? NullableLong(JsonNode? node) => node is null ? null : node.GetValue<long>();

    public static string? Text(JsonNode? node) => node is null ? null : (string?)node;

    public static bool? Bool(JsonNode? node) => node is null ? null : node.GetValue<bool>();

    public static DateTimeOffset Time(JsonNode? node) => DateTimeOffset.FromUnixTimeMilliseconds(Long(node));

    public static IReadOnlyList<string> Strings(JsonNode? node) => node!.AsArray().Select(value => (string)value!).ToList();

    public static IReadOnlyDictionary<string, string> Map(JsonNode? node) =>
        node!.AsObject().ToDictionary(pair => pair.Key, pair => (string)pair.Value!);

    /// <summary>The value of the footer key-value metadata whose key ends with the suffix, or null.</summary>
    public static string? KeyValue(JsonObject footer, string suffix) =>
        footer["keyValues"]!.AsArray().Select(pair => pair!.AsObject())
            .Where(pair => ((string)pair["key"]!).EndsWith(suffix, StringComparison.Ordinal))
            .Select(pair => (string?)pair["value"]).FirstOrDefault();

    /// <summary>Whether a file tag or the footer marks the file as V-Ordered, as the object model defines it.</summary>
    public static bool IsVOrdered(JsonObject file) =>
        (Text(file["tags"]?["VORDER"]) ?? "").Equals("true", StringComparison.OrdinalIgnoreCase)
        || (KeyValue(file["footer"]!.AsObject(), "vorder.enabled") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<JsonObject> RowGroups(JsonObject file) =>
        file["footer"]!["rowGroups"]!.AsArray().Select(group => group!.AsObject()).ToList();

    public static long? At(JsonObject group, string field, int index) => NullableLong(group[field]!.AsArray()[index]);

    public static string TextAt(JsonObject group, string field, int index) => (string)group[field]!.AsArray()[index]!;

    /// <summary>The encodings of a column chunk, which the dump writes separated by commas, sorted.</summary>
    public static IReadOnlyList<string> Encodings(JsonObject group, int index) =>
        TextAt(group, "encodings", index).Split(',').Order(StringComparer.Ordinal).ToList();

    public static int ColumnCount(JsonObject group) => group["codec"]!.AsArray().Count;
}
