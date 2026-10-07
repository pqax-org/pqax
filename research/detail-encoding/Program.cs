// Compares ways of encoding a large PQAX detail table (one row per column chunk) inside a zip package.
// Usage: dotnet run -c Release -- <rows> <output folder>
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

var rows = int.Parse(args[0]);
var folder = args[1];
Directory.CreateDirectory(folder);

var data = Chunks.Generate(rows);
Console.WriteLine($"{rows:N0} column chunks: {data.File[^1] + 1:N0} files, {Chunks.RowGroupsPerFile} row groups per file, {Chunks.ColumnCount} columns");

var encodings = new (string Name, string Entry, bool Deflate, Action<Stream> Write, Func<Stream, int> Read)[]
{
    ("rows-json", "chunks.json", true, s => Encoders.WriteRowsJson(s, data), Encoders.ReadRowsJson),
    ("columnar-json", "chunks.json", true, s => Encoders.WriteColumnarJson(s, data), Encoders.ReadColumnarJson),
    ("csv", "chunks.csv", true, s => Encoders.WriteCsv(s, data), Encoders.ReadCsv),
    ("parquet", "chunks.parquet", false, s => Encoders.WriteParquet(s, data), Encoders.ReadParquet),
};

foreach (var (name, entryName, deflate, write, read) in encodings)
{
    var zipPath = Path.Combine(folder, $"{name}-{rows}.zip");
    long rawSize;
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        // Parquet needs a seekable stream, so every encoding is written to a temporary file first.
        var temp = Path.Combine(folder, $"{name}.tmp");
        using (var file = File.Create(temp)) write(file);
        rawSize = new FileInfo(temp).Length;
        zip.CreateEntryFromFile(temp, entryName, deflate ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
        File.Delete(temp);
    }

    var best = double.MaxValue;
    for (var i = 0; i < 2; i++)
    {
        var watch = Stopwatch.StartNew();
        using var zip = ZipFile.OpenRead(zipPath);
        using var buffer = new MemoryStream();
        using (var entry = zip.GetEntry(entryName)!.Open()) entry.CopyTo(buffer);
        buffer.Position = 0;
        if (read(buffer) != rows) throw new InvalidOperationException($"{name}: row count mismatch.");
        best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
    }

    Console.WriteLine($"  {name,-14} raw {rawSize / 1048576.0,7:F1} MB | in the zip {new FileInfo(zipPath).Length / 1048576.0,6:F1} MB | .NET read {best,6:F0} ms");
}

/// <summary>A synthetic column chunk table with the value distribution of a real one.</summary>
sealed class Chunks(int rows)
{
    public const int RowGroupsPerFile = 4;
    public const int ColumnCount = 50;
    public static readonly string[] Codecs = ["ZSTD", "SNAPPY"];
    public static readonly string[] EncodingSets = ["RLE_DICTIONARY,PLAIN,RLE", "PLAIN,RLE", "DELTA_BINARY_PACKED,RLE"];

    public int Rows { get; } = rows;
    public int[] File { get; } = new int[rows];
    public int[] RowGroup { get; } = new int[rows];
    public int[] Column { get; } = new int[rows];
    public int[] Codec { get; } = new int[rows];
    public int[] Encodings { get; } = new int[rows];
    public long[] NumValues { get; } = new long[rows];
    public long[] NullCount { get; } = new long[rows];
    public long[] CompressedSize { get; } = new long[rows];
    public long[] UncompressedSize { get; } = new long[rows];
    public long[] DictionaryPageSize { get; } = new long[rows];

    public static Chunks Generate(int rows)
    {
        var random = new Random(42);
        var data = new Chunks(rows);

        // Each column has its own typical size, compression ratio, encoding, and share of nulls.
        var baseSize = new double[ColumnCount];
        var ratio = new double[ColumnCount];
        var encoding = new int[ColumnCount];
        var nullShare = new double[ColumnCount];
        for (var c = 0; c < ColumnCount; c++)
        {
            baseSize[c] = Math.Exp(8 + random.NextDouble() * 7);
            ratio[c] = 1.2 + random.NextDouble() * 6;
            encoding[c] = random.NextDouble() < 0.8 ? 0 : random.NextDouble() < 0.7 ? 1 : 2;
            nullShare[c] = random.NextDouble() < 0.8 ? 0 : random.NextDouble() * 0.3;
        }

        var row = 0;
        for (var file = 0; row < rows; file++)
        {
            var codec = random.NextDouble() < 0.9 ? 0 : 1;
            for (var group = 0; group < RowGroupsPerFile && row < rows; group++)
            {
                long groupRows = group < RowGroupsPerFile - 1 ? 1_048_576 : random.Next(1, 1_048_576);
                for (var c = 0; c < ColumnCount && row < rows; c++, row++)
                {
                    var compressed = (long)(baseSize[c] * (0.8 + random.NextDouble() * 0.4) * groupRows / 1_048_576) + 64;
                    data.File[row] = file;
                    data.RowGroup[row] = group;
                    data.Column[row] = c;
                    data.Codec[row] = codec;
                    data.Encodings[row] = encoding[c];
                    data.NumValues[row] = groupRows;
                    data.NullCount[row] = (long)(groupRows * nullShare[c] * random.NextDouble());
                    data.CompressedSize[row] = compressed;
                    data.UncompressedSize[row] = (long)(compressed * ratio[c] * (0.9 + random.NextDouble() * 0.2));
                    data.DictionaryPageSize[row] = encoding[c] == 0 ? (long)(compressed * 0.05 * (0.5 + random.NextDouble())) : 0;
                }
            }
        }
        return data;
    }
}

static class Encoders
{
    private static readonly string[] Names =
        ["file", "rowGroup", "column", "codec", "encodings", "numValues", "nullCount", "compressedSize", "uncompressedSize", "dictionaryPageSize"];

    // One JSON object per column chunk, with names and enumeration values spelled out.
    public static void WriteRowsJson(Stream output, Chunks d)
    {
        using var json = new Utf8JsonWriter(output);
        json.WriteStartArray();
        for (var i = 0; i < d.Rows; i++)
        {
            json.WriteStartObject();
            json.WriteNumber("file", d.File[i]);
            json.WriteNumber("rowGroup", d.RowGroup[i]);
            json.WriteNumber("column", d.Column[i]);
            json.WriteString("codec", Chunks.Codecs[d.Codec[i]]);
            json.WriteString("encodings", Chunks.EncodingSets[d.Encodings[i]]);
            json.WriteNumber("numValues", d.NumValues[i]);
            json.WriteNumber("nullCount", d.NullCount[i]);
            json.WriteNumber("compressedSize", d.CompressedSize[i]);
            json.WriteNumber("uncompressedSize", d.UncompressedSize[i]);
            json.WriteNumber("dictionaryPageSize", d.DictionaryPageSize[i]);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    public static int ReadRowsJson(Stream input) => JsonSerializer.Deserialize<ChunkRow[]>(input)!.Length;

    // One JSON array per field. Enumerations are stored once, and rows refer to them by position.
    public static void WriteColumnarJson(Stream output, Chunks d)
    {
        using var json = new Utf8JsonWriter(output);
        json.WriteStartObject();
        json.WriteNumber("rowCount", d.Rows);
        json.WriteStartObject("enumerations");
        WriteStrings(json, "codec", Chunks.Codecs);
        WriteStrings(json, "encodings", Chunks.EncodingSets);
        json.WriteEndObject();
        json.WriteStartObject("columns");
        WriteNumbers(json, "file", d.File);
        WriteNumbers(json, "rowGroup", d.RowGroup);
        WriteNumbers(json, "column", d.Column);
        WriteNumbers(json, "codec", d.Codec);
        WriteNumbers(json, "encodings", d.Encodings);
        WriteNumbers(json, "numValues", d.NumValues);
        WriteNumbers(json, "nullCount", d.NullCount);
        WriteNumbers(json, "compressedSize", d.CompressedSize);
        WriteNumbers(json, "uncompressedSize", d.UncompressedSize);
        WriteNumbers(json, "dictionaryPageSize", d.DictionaryPageSize);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    public static int ReadColumnarJson(Stream input) => JsonSerializer.Deserialize<ColumnarDocument>(input)!.columns.compressedSize.Length;

    public static void WriteCsv(Stream output, Chunks d)
    {
        using var text = new StreamWriter(output, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
        text.WriteLine(string.Join(',', Names));
        for (var i = 0; i < d.Rows; i++)
        {
            text.Write(d.File[i]); text.Write(',');
            text.Write(d.RowGroup[i]); text.Write(',');
            text.Write(d.Column[i]); text.Write(',');
            text.Write(Chunks.Codecs[d.Codec[i]]); text.Write(',');
            text.Write('"'); text.Write(Chunks.EncodingSets[d.Encodings[i]]); text.Write("\",");
            text.Write(d.NumValues[i]); text.Write(',');
            text.Write(d.NullCount[i]); text.Write(',');
            text.Write(d.CompressedSize[i]); text.Write(',');
            text.Write(d.UncompressedSize[i]); text.Write(',');
            text.Write(d.DictionaryPageSize[i]); text.Write('\n');
        }
    }

    public static int ReadCsv(Stream input)
    {
        using var text = new StreamReader(input);
        text.ReadLine();
        var sizes = new List<long>();
        while (text.ReadLine() is { } line)
        {
            // The only quoted field is the fifth one, so split around it.
            var quote = line.IndexOf('"');
            var endQuote = line.IndexOf('"', quote + 1);
            var head = line[..(quote - 1)].Split(',');
            var tail = line[(endQuote + 2)..].Split(',');
            _ = int.Parse(head[0]) + int.Parse(head[1]) + int.Parse(head[2]);
            sizes.Add(long.Parse(tail[0]) + long.Parse(tail[1]) + long.Parse(tail[2]) + long.Parse(tail[3]) + long.Parse(tail[4]));
        }
        return sizes.Count;
    }

    public static void WriteParquet(Stream output, Chunks d)
    {
        var intFields = new[] { "file", "rowGroup", "column" }.Select(n => new DataField<int>(n)).ToArray();
        var stringFields = new[] { "codec", "encodings" }.Select(n => new DataField<string>(n)).ToArray();
        var longFields = Names[5..].Select(n => new DataField<long>(n)).ToArray();
        var schema = new ParquetSchema([.. intFields, .. stringFields, .. longFields]);

        using var writer = ParquetWriter.CreateAsync(schema, output).GetAwaiter().GetResult();
        using var group = writer.CreateRowGroup();
        group.WriteColumnAsync(new DataColumn(intFields[0], d.File)).GetAwaiter().GetResult();
        group.WriteColumnAsync(new DataColumn(intFields[1], d.RowGroup)).GetAwaiter().GetResult();
        group.WriteColumnAsync(new DataColumn(intFields[2], d.Column)).GetAwaiter().GetResult();
        group.WriteColumnAsync(new DataColumn(stringFields[0], d.Codec.Select(c => Chunks.Codecs[c]).ToArray())).GetAwaiter().GetResult();
        group.WriteColumnAsync(new DataColumn(stringFields[1], d.Encodings.Select(e => Chunks.EncodingSets[e]).ToArray())).GetAwaiter().GetResult();
        group.WriteColumnAsync(new DataColumn(longFields[0], d.NumValues)).GetAwaiter().GetResult();
        group.WriteColumnAsync(new DataColumn(longFields[1], d.NullCount)).GetAwaiter().GetResult();
        group.WriteColumnAsync(new DataColumn(longFields[2], d.CompressedSize)).GetAwaiter().GetResult();
        group.WriteColumnAsync(new DataColumn(longFields[3], d.UncompressedSize)).GetAwaiter().GetResult();
        group.WriteColumnAsync(new DataColumn(longFields[4], d.DictionaryPageSize)).GetAwaiter().GetResult();
    }

    public static int ReadParquet(Stream input)
    {
        using var reader = ParquetReader.CreateAsync(input).GetAwaiter().GetResult();
        var count = 0;
        for (var g = 0; g < reader.RowGroupCount; g++)
        {
            using var group = reader.OpenRowGroupReader(g);
            foreach (var field in reader.Schema.GetDataFields())
                count = group.ReadColumnAsync(field).GetAwaiter().GetResult().Data.Length;
        }
        return count;
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, string[] values)
    {
        json.WriteStartArray(name);
        foreach (var value in values) json.WriteStringValue(value);
        json.WriteEndArray();
    }

    private static void WriteNumbers(Utf8JsonWriter json, string name, int[] values)
    {
        json.WriteStartArray(name);
        foreach (var value in values) json.WriteNumberValue(value);
        json.WriteEndArray();
    }

    private static void WriteNumbers(Utf8JsonWriter json, string name, long[] values)
    {
        json.WriteStartArray(name);
        foreach (var value in values) json.WriteNumberValue(value);
        json.WriteEndArray();
    }
}

#pragma warning disable IDE1006 // Property names follow the JSON documents
record struct ChunkRow(int file, int rowGroup, int column, string codec, string encodings,
    long numValues, long nullCount, long compressedSize, long uncompressedSize, long dictionaryPageSize);

record ColumnarDocument(int rowCount, Dictionary<string, string[]> enumerations, ColumnarColumns columns);

record ColumnarColumns(int[] file, int[] rowGroup, int[] column, int[] codec, int[] encodings,
    long[] numValues, long[] nullCount, long[] compressedSize, long[] uncompressedSize, long[] dictionaryPageSize);
