using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Pqax.Collector.Tests;

/// <summary>
/// The footer parser against the probe dump, on every data file of the fixture: row groups, chunk sizes, distinct
/// counts, encodings, codec, and footer size (plan 5); and on bytes that are not a footer.
/// </summary>
public class FooterTests
{
    public static TheoryData<string> DataFiles => Fixture.Data(Fixture.DataFiles());

    private static LocalStorage Storage() => new(Fixture.Folder);

    private static byte[] FileBytes(string path) => File.ReadAllBytes(Path.Combine(Fixture.Folder, path));

    /// <summary>The encoded FileMetaData of a Parquet file: the bytes before its length and the closing magic.</summary>
    private static byte[] Metadata(byte[] file)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(file.Length - 8, 4));
        return file.AsSpan(file.Length - 8 - length, length).ToArray();
    }

    private static void AssertSameAsDump(ParquetFooter footer, DumpTable table, JsonObject file)
    {
        JsonObject expected = file["footer"]!.AsObject();
        Assert.Equal(Dump.Long(expected["version"]), footer.Version);
        Assert.Equal(Dump.Long(expected["numRows"]), footer.NumRows);
        Assert.Equal(Dump.Text(expected["createdBy"]), footer.CreatedBy);

        var keyValues = expected["keyValues"]!.AsArray().Select(pair => pair!.AsObject()).ToList();
        Assert.Equal(keyValues.Select(pair => (string)pair["key"]!), footer.KeyValueMetadata.Select(pair => pair.Key));
        for (int i = 0; i < keyValues.Count; i++)
        {
            Assert.Equal(Dump.NullableLong(keyValues[i]["valueLength"]), footer.KeyValueMetadata[i].ValueLength);
            if (keyValues[i]["value"] is JsonNode value)
            {
                Assert.Equal((string)value!, footer.KeyValueMetadata[i].Value);
            }
        }

        JsonArray leaves = table.Schemas[(int)Dump.Long(expected["schema"])]["columns"]!.AsArray();
        Assert.Equal(leaves.Count, footer.Leaves.Count);
        for (int i = 0; i < leaves.Count; i++)
        {
            ParquetLeafColumn leaf = footer.Leaves[i];
            Assert.Equal(i, leaf.Ordinal);
            Assert.Equal(Dump.Strings(leaves[i]!["path"]), leaf.Path);
            Assert.Equal((string)leaves[i]!["type"]!, leaf.PhysicalType);
            Assert.Equal(Dump.Text(leaves[i]!["repetition"]), leaf.Repetition);
            Assert.Equal(Dump.Text(leaves[i]!["converted"]), leaf.ConvertedType);
        }

        IReadOnlyList<JsonObject> groups = Dump.RowGroups(file);
        Assert.Equal(groups.Count, footer.RowGroups.Count);
        for (int g = 0; g < groups.Count; g++)
        {
            JsonObject group = groups[g];
            ParquetRowGroup actual = footer.RowGroups[g];
            Assert.Equal(Dump.Long(group["numRows"]), actual.NumRows);
            Assert.Equal(Dump.Long(group["totalByteSize"]), actual.TotalByteSize);
            Assert.Equal(Dump.Long(group["totalCompressedSize"]), actual.TotalCompressedSize);
            Assert.Equal(Dump.ColumnCount(group), actual.Columns.Count);
            for (int c = 0; c < actual.Columns.Count; c++)
            {
                ParquetColumnChunk chunk = actual.Columns[c];
                string where = $"row group {g}, column {c}";
                Assert.True(chunk.HasMetadata, where);
                Assert.True(Dump.TextAt(group, "codec", c) == chunk.Codec, where + ": codec");
                Assert.Equal(Dump.Encodings(group, c), chunk.Encodings.Order(StringComparer.Ordinal));
                Assert.True(Dump.At(group, "numValues", c) == chunk.NumValues, where + ": numValues");
                Assert.True(Dump.At(group, "compressed", c) == chunk.TotalCompressedSize, where + ": compressed");
                Assert.True(Dump.At(group, "uncompressed", c) == chunk.TotalUncompressedSize, where + ": uncompressed");
                Assert.True(Dump.At(group, "dataPageOffset", c) == chunk.DataPageOffset, where + ": dataPageOffset");
                Assert.True(Dump.At(group, "dictionaryPageOffset", c) == chunk.DictionaryPageOffset, where + ": dictionaryPageOffset");
                Assert.True(Dump.At(group, "nullCount", c) == chunk.NullCount, where + ": nullCount");
                Assert.True(Dump.At(group, "distinctCount", c) == chunk.DistinctCount, where + ": distinctCount");
                Assert.True(Dump.Bool(group["hasMinMax"]!.AsArray()[c]) == chunk.HasMinMax, where + ": hasMinMax");
                Assert.True(Dump.At(group, "bloomFilterLength", c) == chunk.BloomFilterLength, where + ": bloomFilterLength");
                Assert.True(Dump.At(group, "unencodedByteArrayBytes", c) == chunk.UnencodedByteArrayDataBytes, where + ": unencodedByteArrayBytes");
                Assert.True(Dump.At(group, "columnIndexLength", c) == chunk.ColumnIndexLength, where + ": columnIndexLength");
                Assert.True(Dump.At(group, "offsetIndexLength", c) == chunk.OffsetIndexLength, where + ": offsetIndexLength");
                if (group["encodingStats"]!.AsArray()[c] is null)
                {
                    Assert.True(chunk.EncodingStats is null || chunk.EncodingStats.Count == 0, where + ": encodingStats");
                }

                long? dictionary = Dump.At(group, "dictionaryPageOffset", c);
                Assert.Equal(dictionary != null, chunk.HasDictionaryPage);
                Assert.Equal(dictionary == null ? null : Dump.At(group, "dataPageOffset", c) - dictionary, chunk.DictionaryPageSize);
                Assert.Null(chunk.MinValue);
                Assert.Null(chunk.MaxValue);
            }
        }
    }

    [Fact]
    public void The_fixture_has_a_footer_in_the_dump_for_every_data_file()
    {
        IReadOnlyList<string> files = Fixture.DataFiles();
        Assert.Equal(Fixture.Tables.Sum(table => table.Files.Count), files.Count);
        foreach (string path in files)
        {
            Fixture.DumpFileOfSize(FileBytes(path).Length);
        }

        // The one Parquet file without a footer in the dump is the file that no commit references, which the dump
        // counts in unreferencedFileCount and unreferencedFileSize of its table (conformance/README.md).
        string unreferenced = Assert.Single(Fixture.UnreferencedDataFiles());
        Assert.Equal(Fixture.CheckpointTable.TablePath + "/Year=2024/part-0004.parquet", unreferenced);
        Assert.Equal(737, FileBytes(unreferenced).Length);
        Assert.Equal(Fixture.AllDataFiles().Count, files.Count + 1);
        foreach (DumpTable table in Fixture.Tables)
        {
            List<string> extra = Fixture.UnreferencedDataFiles().Where(path => path.StartsWith(table.TablePath + "/", StringComparison.Ordinal)).ToList();
            Assert.Equal(Dump.Long(table.Dump["unreferencedFileCount"]), extra.Count);
            Assert.Equal(Dump.Long(table.Dump["unreferencedFileSize"]), extra.Sum(path => (long)FileBytes(path).Length));
        }
    }

    [Theory]
    [MemberData(nameof(DataFiles))]
    public async Task The_footer_reader_agrees_with_the_dump(string path)
    {
        long size = FileBytes(path).Length;
        (DumpTable table, JsonObject file) = Fixture.DumpFileOfSize(size);
        ParquetFooter footer = await new FooterReader(Storage()).ReadAsync(path, size, CancellationToken.None);
        Assert.Equal(Dump.Long(file["footer"]!["footerSize"]), footer.FooterLength);
        Assert.Equal(1, footer.RequestCount);
        AssertSameAsDump(footer, table, file);
    }

    [Theory]
    [MemberData(nameof(DataFiles))]
    public void Parsing_the_metadata_bytes_gives_the_same_footer(string path)
    {
        byte[] bytes = FileBytes(path);
        (DumpTable table, JsonObject file) = Fixture.DumpFileOfSize(bytes.Length);
        byte[] metadata = Metadata(bytes);
        Assert.Equal(Dump.Long(file["footer"]!["footerSize"]), metadata.Length);
        ParquetFooter footer = ParquetFooter.Parse(metadata);
        Assert.Equal(0, footer.FooterLength);
        Assert.Equal(0, footer.RequestCount);
        AssertSameAsDump(footer, table, file);

        var padded = new byte[metadata.Length + 10];
        metadata.CopyTo(padded, 7);
        AssertSameAsDump(ParquetFooter.Parse(padded, 7, metadata.Length), table, file);
    }

    [Theory]
    [MemberData(nameof(DataFiles))]
    public async Task A_tail_shorter_than_the_footer_takes_a_second_request(string path)
    {
        long size = FileBytes(path).Length;
        (DumpTable table, JsonObject file) = Fixture.DumpFileOfSize(size);
        var storage = new TestStorage(Storage());
        ParquetFooter footer = await new FooterReader(storage, tailBytes: 12).ReadAsync(path, size, CancellationToken.None);
        Assert.Equal(2, footer.RequestCount);
        Assert.Equal(2, storage.Requests.Count);
        Assert.All(storage.Requests, request => Assert.Equal("read", request.Kind));
        Assert.Equal(Dump.Long(file["footer"]!["footerSize"]), footer.FooterLength);
        AssertSameAsDump(footer, table, file);
    }

    [Theory]
    [MemberData(nameof(DataFiles))]
    public async Task The_default_tail_reads_only_the_end_of_the_file_once(string path)
    {
        long size = FileBytes(path).Length;
        var storage = new TestStorage(Storage());
        await new FooterReader(storage).ReadAsync(path, size, CancellationToken.None);
        StorageRequest request = Assert.Single(storage.Requests);
        Assert.Equal(size, request.Offset + request.Length);
        Assert.Equal(Math.Min(size, FooterReader.DefaultTailBytes), request.Length);
    }

    [Fact]
    public void Data_values_are_kept_only_when_asked()
    {
        foreach (string path in Fixture.DataFiles())
        {
            byte[] metadata = Metadata(FileBytes(path));
            ParquetFooter without = ParquetFooter.Parse(metadata);
            ParquetFooter with = ParquetFooter.Parse(metadata, includeDataValues: true);
            Assert.All(without.RowGroups.SelectMany(group => group.Columns), chunk => Assert.Null(chunk.MinValue));
            var chunks = with.RowGroups.SelectMany(group => group.Columns).Where(chunk => chunk.HasMinMax).ToList();
            Assert.NotEmpty(chunks);
            Assert.All(chunks, chunk =>
            {
                Assert.NotNull(chunk.MinValue);
                Assert.NotNull(chunk.MaxValue);
            });
        }
    }

    // ---- bytes that are not a footer ----

    private static byte[] Tail(int length, string magic)
    {
        var tail = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(tail, length);
        Encoding.ASCII.GetBytes(magic).CopyTo(tail, 4);
        return tail;
    }

    private static async Task<string> CodeOf(IStorage storage, long size, int tailBytes = FooterReader.DefaultTailBytes)
    {
        var error = await Assert.ThrowsAsync<ParquetFooterException>(() => new FooterReader(storage, tailBytes).ReadAsync("file.parquet", size, CancellationToken.None));
        Assert.False(string.IsNullOrEmpty(error.Message));
        return error.Code;
    }

    [Fact]
    public async Task A_file_that_does_not_end_with_the_magic_is_invalid()
    {
        Assert.Equal(IssueCodes.FooterInvalid, await CodeOf(new VirtualFileStorage(1000, Tail(100, "XXXX")), 1000));
        Assert.Equal(IssueCodes.FooterInvalid, await CodeOf(new VirtualFileStorage(1000, Tail(100, "PAR2")), 1000));
    }

    [Fact]
    public async Task An_encrypted_footer_is_reported_as_such()
    {
        Assert.Equal(IssueCodes.FooterEncrypted, await CodeOf(new VirtualFileStorage(1000, Tail(100, "PARE")), 1000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(989)]
    [InlineData(int.MaxValue)]
    public async Task A_footer_length_that_does_not_fit_the_file_is_invalid(int length)
    {
        Assert.Equal(IssueCodes.FooterInvalid, await CodeOf(new VirtualFileStorage(1000, Tail(length, "PAR1")), 1000));
    }

    [Fact]
    public async Task A_file_smaller_than_twelve_bytes_is_invalid()
    {
        Assert.Equal(IssueCodes.FooterInvalid, await CodeOf(new VirtualFileStorage(11, Tail(1, "PAR1")), 11));
    }

    [Fact]
    public async Task A_file_shorter_than_its_stated_size_is_invalid()
    {
        byte[] bytes = FileBytes(Fixture.DataFiles()[0]);
        var storage = new VirtualFileStorage(bytes.Length - 100, bytes.AsSpan(0, bytes.Length - 100).ToArray());
        Assert.Equal(IssueCodes.FooterInvalid, await CodeOf(storage, bytes.Length));
    }

    [Fact]
    public async Task A_footer_over_64_mb_is_too_large_and_is_not_read()
    {
        long size = 100L * 1024 * 1024;
        var storage = new VirtualFileStorage(size, Tail(FooterReader.MaxFooterLength + 1, "PAR1"));
        Assert.Equal(IssueCodes.FooterTooLarge, await CodeOf(storage, size));
        Assert.Single(storage.Reads);
        Assert.True(storage.Reads[0].Length <= FooterReader.DefaultTailBytes);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 0x15, 0x02 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
    public void Bytes_that_are_not_a_footer_throw_a_footer_exception(byte[] bytes)
    {
        var error = Assert.Throws<ParquetFooterException>(() => ParquetFooter.Parse(bytes));
        Assert.Equal(IssueCodes.FooterInvalid, error.Code);
    }

    [Fact]
    public void Every_truncation_of_a_footer_throws_only_a_footer_exception()
    {
        foreach (string path in Fixture.DataFiles())
        {
            byte[] metadata = Metadata(FileBytes(path));
            for (int length = 0; length < metadata.Length; length++)
            {
                byte[] cut = metadata.AsSpan(0, length).ToArray();
                Exception? error = Record.Exception(() => ParquetFooter.Parse(cut));
                Assert.True(error is null or ParquetFooterException, $"{path}, {length} bytes: {error?.GetType().Name}: {error?.Message}");
            }
        }
    }

    [Fact]
    public void Changed_bytes_in_a_footer_throw_only_a_footer_exception()
    {
        var random = new Random(20261007);
        var started = DateTime.UtcNow;
        int parsed = 0;
        int refused = 0;
        foreach (string path in Fixture.DataFiles())
        {
            byte[] metadata = Metadata(FileBytes(path));
            for (int round = 0; round < 1500; round++)
            {
                byte[] changed = (byte[])metadata.Clone();
                int changes = 1 + random.Next(3);
                for (int i = 0; i < changes; i++)
                {
                    changed[random.Next(changed.Length)] = (byte)random.Next(256);
                }

                Exception? error = Record.Exception(() => ParquetFooter.Parse(changed, includeDataValues: round % 2 == 0));
                Assert.True(error is null or ParquetFooterException, $"{path}, round {round}: {error?.GetType().Name}: {error?.Message}");
                if (error is null)
                {
                    parsed++;
                }
                else
                {
                    refused++;
                }
            }
        }

        Assert.True(refused > 0, "no change made a footer invalid");
        Assert.True(parsed + refused == 1500 * Fixture.DataFiles().Count);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "the parser is too slow on changed bytes");
    }
}
