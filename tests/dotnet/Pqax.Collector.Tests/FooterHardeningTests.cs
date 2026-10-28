using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Pqax.Collector.ParquetNet;

namespace Pqax.Collector.Tests;

/// <summary>
/// The footer reader and parser on malformed and hostile footers, from the rules of conformance/footers.md (footers of
/// other writers, malformed footers, nested columns): R1 to R6, the outcome of a footer read whatever the bytes and the
/// stated size, and P1 to P5, the parse. Where a rule keeps the behaviour that the parser had before, the test pins it,
/// messages included. The footers are written as test_collect_footer_hardening.py of the Python
/// tests writes them, byte for byte (the hashes of the two base footers are pinned in both), so that the two suites
/// expect the same code and the same message for the same bytes, as R2 asks.
/// </summary>
public class FooterHardeningTests
{
    private const string Invalid = IssueCodes.FooterInvalid;

    // The messages of the checks that the rules keep as they were, the same in Python.
    private const string TooSmall = "The file is too small to be a Parquet file: {0} bytes.";
    private const string Shorter = "The file is shorter than the size that the table format gives.";
    private const string Encrypted = "The footer is encrypted.";
    private const string NoMagic = "The file does not end with the Parquet magic bytes.";
    private const string NotFitting = "The length of the footer, {0}, does not fit the file.";
    private const string TooLarge = "The footer takes {0} bytes, more than the limit of 67108864.";
    private const string Truncated = "The footer ends in the middle of a value.";
    private const string ListTooLong = "A list of the footer is too long.";
    private const string Varint32 = "A varint of the footer is longer than 32 bits.";
    private const string Lacks = "The footer lacks a required field: version, schema, num_rows, or row_groups.";
    private const string NoName = "A schema element of the footer has no name.";
    private const string GroupLacks = "A row group of the footer lacks its column chunks, its number of rows, or its size.";
    private const string MetaLacks = "The metadata of a column chunk lacks num_values, a size, or data_page_offset.";
    private const string Chunks = "A row group has {0} column chunks for {1} leaf columns.";
    private const string SchemaTooDeep = "The schema of the footer is nested too deep.";

    /// <summary>P1: the one message of a nesting deeper than 64 levels, for every kind of container.</summary>
    private const string TooDeep = "The footer nests values more than 64 levels deep.";

    // R5: the limits of the lists, the maps, and the column chunks of a footer.
    private const string ListLimit = "A list of the footer has {0} elements, more than the limit of 1000000.";
    private const string MapLimit = "A map of the footer has {0} entries, more than the limit of 1000000.";
    private const string ChunkLimit = "The row groups of the footer hold more column chunks than the limit of 4194304.";

    private const int Mib = 1024 * 1024;
    private const int Size = 1000;
    private const long Large = 100L * Mib;

    private const string BaselineSha256 = "475e962d4f73ab3a2d2ccecc6bb146227473f18c62909f787b14630b7ab49e99";
    private const string RichSha256 = "fe940985faa9087b3746fabddda49d73695dfb66128f7a62be9670ea84ac540c";

    private static readonly string[] Codes = [IssueCodes.FooterInvalid, IssueCodes.FooterEncrypted, IssueCodes.FooterTooLarge];

    private static byte[] Baseline { get; } = Footers.Build();

    private static byte[] Rich { get; } = Footers.Rich();

    private static string F(string format, long value) => string.Format(CultureInfo.InvariantCulture, format, value);

    private static TheoryData<string> Names(IEnumerable<string> names)
    {
        var data = new TheoryData<string>();
        foreach (string name in names)
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>The footer exception that an action throws; any other exception fails the test, as R1 says.</summary>
    private static ParquetFooterException Refusal(Action action)
    {
        try
        {
            action();
        }
        catch (ParquetFooterException error)
        {
            return error;
        }
        catch (Exception error)
        {
            Assert.Fail($"{error.GetType().Name} instead of a footer exception: {error.Message}");
        }

        Assert.Fail("No footer exception.");
        return null!;
    }

    private static void AssertRefused(byte[] metadata, string message, string code = Invalid, bool includeDataValues = false)
    {
        ParquetFooterException error = Refusal(() => ParquetFooter.Parse(metadata, includeDataValues));
        Assert.Equal((code, message), (error.Code, error.Message));
    }

    /// <summary>Runs an action that may succeed or throw the footer exception, with one of its three codes and a message.</summary>
    private static ParquetFooterException? OnlyFooterExceptions(Action action, string where)
    {
        try
        {
            action();
        }
        catch (ParquetFooterException error)
        {
            Assert.True(Codes.Contains(error.Code), $"{where}: code {error.Code}");
            Assert.False(string.IsNullOrEmpty(error.Message), $"{where}: no message");
            return error;
        }
        catch (Exception error)
        {
            Assert.Fail($"{where}: {error.GetType().Name}: {error.Message}");
        }

        return null;
    }

    private static ParquetFooter Read(IStorage storage, long size, int tailBytes = FooterReader.DefaultTailBytes, bool includeDataValues = false) =>
        new FooterReader(storage, tailBytes, includeDataValues).ReadAsync("file.parquet", size, CancellationToken.None).GetAwaiter().GetResult();

    private static ParquetFooterException ReadRefusal(IStorage storage, long size, int tailBytes = FooterReader.DefaultTailBytes) =>
        Refusal(() => Read(storage, size, tailBytes));

    /// <summary>Every value of a parsed footer, to compare two parses that must give the same footer.</summary>
    private static string Summary(ParquetFooter footer)
    {
        var text = new StringBuilder();
        text.Append(footer.Version).Append('|').Append(footer.NumRows).Append('|').Append(footer.CreatedBy).Append('|').Append(footer.HasEncryptionAlgorithm).Append('\n');
        foreach (ParquetKeyValue pair in footer.KeyValueMetadata)
        {
            text.Append("kv ").Append(pair.Key).Append('|').Append(pair.ValueLength).Append('|').Append(pair.Value).Append('\n');
        }

        foreach (ParquetSchemaElement e in footer.Schema)
        {
            text.Append("el ").AppendJoin('|', e.Name, e.Type, e.TypeLength, e.Repetition, e.NumChildren, e.ConvertedType, e.Scale, e.Precision, e.FieldId, e.LogicalType).Append('\n');
        }

        foreach (ParquetLeafColumn leaf in footer.Leaves)
        {
            text.Append("leaf ").AppendJoin('|', leaf.Ordinal, string.Join(".", leaf.Path), leaf.PhysicalType, leaf.TypeLength, leaf.Repetition, leaf.LogicalType, leaf.ConvertedType, leaf.Scale, leaf.Precision, leaf.FieldId, leaf.TopLevelFieldId, leaf.TypeAnnotation).Append('\n');
        }

        foreach (ParquetRowGroup group in footer.RowGroups)
        {
            string sorting = group.SortingColumns == null ? "-" : string.Join(",", group.SortingColumns.Select(s => s.ColumnIndex + ":" + s.Descending + ":" + s.NullsFirst));
            text.Append("group ").AppendJoin('|', group.NumRows, group.TotalByteSize, group.TotalCompressedSize, group.FileOffset, group.Ordinal, sorting).Append('\n');
            foreach (ParquetColumnChunk c in group.Columns)
            {
                text.Append("  chunk ").Append(Describe(c)).Append('\n');
            }
        }

        return text.ToString();
    }

    private static string Describe(ParquetColumnChunk c)
    {
        string stats = c.EncodingStats == null ? "-" : string.Join(",", c.EncodingStats.Select(s => s.ToString()));
        return string.Join('|', new object?[]
        {
            c.HasMetadata, c.IsEncrypted, c.FilePath, c.PhysicalType, c.Codec, string.Join(",", c.Encodings), c.NumValues,
            c.TotalUncompressedSize, c.TotalCompressedSize, c.DataPageOffset, c.IndexPageOffset, c.DictionaryPageOffset,
            c.HasStatistics, c.NullCount, c.DistinctCount, c.HasMinMax, c.MinValue, c.MaxValue, stats, c.BloomFilterOffset,
            c.BloomFilterLength, c.UnencodedByteArrayDataBytes, c.OffsetIndexOffset, c.OffsetIndexLength, c.ColumnIndexOffset,
            c.ColumnIndexLength,
        });
    }

    private static ParquetColumnChunk First(ParquetFooter footer) => footer.RowGroups[0].Columns[0];

    private static byte[] Hook(string where, Action<Compact> write)
    {
        var spec = new FooterSpec();
        spec.Extra[where] = write;
        return Footers.Build(spec);
    }

    private static byte[] Known(string name, Action<Compact> write)
    {
        var spec = new FooterSpec();
        spec.Replace[name] = write;
        return Footers.Build(spec);
    }

    // ---- R2: the bytes of the two suites ----

    [Fact]
    public void The_footers_of_the_tests_have_the_same_bytes_as_in_the_python_suite()
    {
        Assert.Equal(BaselineSha256, Convert.ToHexStringLower(SHA256.HashData(Baseline)));
        Assert.Equal(RichSha256, Convert.ToHexStringLower(SHA256.HashData(Rich)));
    }

    [Fact]
    public void The_rich_footer_is_a_valid_footer_with_every_value()
    {
        ParquetFooter parsed = ParquetFooter.Parse(Rich, includeDataValues: true);
        Assert.Equal((2, 1000L, "parquet-mr version 1.13.1 (build synthetic)"), (parsed.Version, parsed.NumRows, parsed.CreatedBy));
        Assert.Equal(Footers.RichLeaves.Select(leaf => string.Join(".", leaf.Path)), parsed.Leaves.Select(leaf => string.Join(".", leaf.Path)));
        Assert.Equal([1, 2, 3, 4, 6, null, null, 8, 9], parsed.Leaves.Select(leaf => leaf.FieldId));
        Assert.Equal([1, 2, 3, 4, 5, 7, 7, 8, 9], parsed.Leaves.Select(leaf => leaf.TopLevelFieldId));
        Assert.Equal(2, parsed.RowGroups.Count);
        ParquetColumnChunk price = parsed.RowGroups[1].Columns[2];
        Assert.Equal(("-1.2345", "98765.4321"), (price.MinValue, price.MaxValue));
        Assert.Null(price.UnencodedByteArrayDataBytes);
        Assert.Equal(301, parsed.RowGroups[1].Columns[1].UnencodedByteArrayDataBytes);
        Assert.Equal(["true", "default", "3.5.1", null, null], parsed.KeyValueMetadata.Select(pair => pair.Value));
    }

    // ---- R1: nothing but the footer exception ----

    [Fact]
    public void Every_truncation_of_a_footer_ends_in_the_middle_of_a_value()
    {
        foreach (byte[] data in new[] { Baseline, Rich })
        {
            for (int length = 0; length < data.Length; length++)
            {
                byte[] cut = data.AsSpan(0, length).ToArray();
                ParquetFooterException error = Refusal(() => ParquetFooter.Parse(cut));
                Assert.True((Invalid, Truncated) == (error.Code, error.Message), $"{length} bytes of {data.Length}: {error.Code}: {error.Message}");
            }
        }
    }

    [Fact]
    public void Every_truncation_of_a_file_is_refused_with_the_footer_exception_only()
    {
        byte[] data = ParquetFiles.EndingWith(Rich, Rich.Length + 100);
        for (int cut = 1; cut < Rich.Length + 9; cut++)
        {
            byte[] shorter = data.AsSpan(0, data.Length - cut).ToArray();

            // Stated as the size of the whole file: the tail read is short.
            ParquetFooterException error = ReadRefusal(new VirtualFileStorage(shorter.Length, shorter), data.Length);
            Assert.True((Invalid, Shorter) == (error.Code, error.Message), $"{cut} bytes cut: {error.Message}");

            // Stated as the size of what is left: the end is no longer the end of a footer.
            OnlyFooterExceptions(() => Read(new VirtualFileStorage(shorter.Length, shorter), shorter.Length), $"{cut} bytes cut, stated as the size left");
        }
    }

    /// <summary>A storage whose reads fail with an exception.</summary>
    private sealed class FailingStorage(Exception error) : IStorage
    {
        public Task<IReadOnlyList<StorageEntry>> ListAsync(string path, CancellationToken cancellation) => Task.FromResult<IReadOnlyList<StorageEntry>>([]);

        public Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellation) => Task.FromException<byte[]>(error);
    }

    [Fact]
    public async Task An_error_of_the_storage_propagates_as_it_is()
    {
        foreach (var error in new[] { new StorageException("The storage answered 500.", 500), new StorageException("A request timed out.") })
        {
            var thrown = await Assert.ThrowsAsync<StorageException>(() => new FooterReader(new FailingStorage(error)).ReadAsync("file.parquet", 1000, CancellationToken.None));
            Assert.Same(error, thrown);
        }
    }

    /// <summary>List headers that claim more elements than the bytes left can hold: the footer, and the message.</summary>
    private static readonly Dictionary<string, (Func<byte[]> Footer, string Message)> HugeCounts = new()
    {
        ["the schema"] = (() => Known("file.schema", w => w.ListOf(2, Compact.Struct, int.MaxValue)), Truncated),
        ["the row groups"] = (() => Known("file.row_groups", w => w.ListOf(4, Compact.Struct, int.MaxValue)), Truncated),
        ["the key-value metadata"] = (() => Known("file.key_value_metadata", w => w.ListOf(5, Compact.Struct, int.MaxValue)), Truncated),
        ["the columns of a row group"] = (() => Known("row_group.columns", w => w.ListOf(1, Compact.Struct, int.MaxValue)), Truncated),
        ["the encodings of a chunk"] = (() => Hook("column", w => w.ListOf(2, Compact.I32, int.MaxValue)), Truncated),
        ["the path of a chunk"] = (() => Hook("column", w => w.ListOf(3, Compact.Binary, int.MaxValue)), Truncated),
        ["the encoding statistics"] = (() => Hook("column", w => w.ListOf(13, Compact.Struct, int.MaxValue)), Truncated),
        ["the sorting columns"] = (() => Hook("row_group", w => w.ListOf(4, Compact.Struct, int.MaxValue)), Truncated),
        ["the column orders"] = (() => Hook("file", w => w.ListOf(7, Compact.Struct, int.MaxValue)), Truncated),
        ["an unknown list of structures"] = (() => Hook("file", w => w.ListOf(300, Compact.Struct, int.MaxValue)), Truncated),
        ["an unknown list of booleans"] = (() => Hook("statistics", w => w.ListOf(300, Compact.True, int.MaxValue)), Truncated),
        ["an unknown set of binaries"] = (() => Hook("leaf", w => w.ListOf(300, Compact.Binary, int.MaxValue, Compact.Set)), Truncated),
        ["an unknown map"] = (() => Hook("chunk", w => w.MapOf(300, Compact.Binary, Compact.Struct, uint.MaxValue)), Truncated),
        ["an unknown binary"] = (() => Hook("root", w => w.Header(300, Compact.Binary).Varint(uint.MaxValue)), Truncated),
        // Today a count over 2^31 - 1 is "too long" before the bytes left are counted; R5 puts the check of the bytes
        // left first, and does not say which of the two messages such a count keeps, so the test takes either.
        ["a list longer than 2^31 - 1"] = (() => Hook("file", w => w.ListOf(300, Compact.I64, uint.MaxValue)), ListTooLong + "|" + Truncated),
        ["a count of more than 32 bits"] = (() => Hook("file", w => w.ListOf(300, Compact.I64, 1L << 40)), Varint32),
    };

    public static TheoryData<string> HugeCountCases => Names(HugeCounts.Keys);

    [Theory]
    [MemberData(nameof(HugeCountCases))]
    public void A_list_that_claims_more_elements_than_the_bytes_left_is_invalid_and_allocates_nothing_for_them(string where)
    {
        (Func<byte[]> build, string message) = HugeCounts[where];
        byte[] data = build();
        long before = GC.GetAllocatedBytesForCurrentThread();
        ParquetFooterException error = Refusal(() => ParquetFooter.Parse(data));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(Invalid, error.Code);
        Assert.Contains(error.Message, message.Split('|'));
        Assert.True(allocated < Mib, $"{allocated} bytes allocated for a footer of {data.Length}");
    }

    // ---- R5: the limits of the lists, the maps, and the column chunks ----

    [Fact]
    public void The_limits_of_a_footer_are_a_million_elements_and_4194304_column_chunks()
    {
        // Read by reflection, so that this file compiles before the constants exist.
        long? Constant(string name) =>
            typeof(FooterReader).GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) is { } value
                ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
                : null;
        Assert.Equal(1_000_000L, Constant("MaxListLength"));
        Assert.Equal(4_194_304L, Constant("MaxColumnChunks"));
    }

    /// <summary>A hook that writes the header of a list (or a set) that claims count elements, and filler bytes after it, so that the elements fit the bytes left when filler &gt;= count.</summary>
    private static Action<Compact> Claim(int field, byte element, int count, int filler, byte kind = Compact.List) =>
        w => w.ListOf(field, element, count, kind).Raw(Enumerable.Repeat((byte)1, filler).ToArray());

    private static Action<Compact> ClaimMap(int field, int count, int filler) =>
        w => w.MapOf(field, Compact.True, Compact.True, count).Raw(Enumerable.Repeat((byte)1, filler).ToArray());

    /// <summary>A list or a map past the limit, whose elements would fit the bytes left: the footer and the message.</summary>
    private static readonly Dictionary<string, (Func<byte[]> Footer, string Message)> PastTheLimit = new()
    {
        ["an unknown list"] = (() => Hook("file", Claim(300, Compact.True, 1_000_001, 1_000_001)), F(ListLimit, 1_000_001)),
        ["an unknown set"] = (() => Hook("file", Claim(300, Compact.True, 1_000_001, 1_000_001, Compact.Set)), F(ListLimit, 1_000_001)),
        ["an unknown list of two million"] = (() => Hook("leaf", Claim(300, Compact.Struct, 2_000_000, 2_000_000)), F(ListLimit, 2_000_000)),
        ["an unknown map"] = (() => Hook("file", ClaimMap(300, 1_000_001, 1_000_001)), F(MapLimit, 1_000_001)),
        ["the schema"] = (() => Known("file.schema", Claim(2, Compact.Struct, 1_000_001, 1_000_001)), F(ListLimit, 1_000_001)),
        ["the row groups"] = (() => Known("file.row_groups", Claim(4, Compact.Struct, 1_000_001, 1_000_001)), F(ListLimit, 1_000_001)),
        ["the key-value metadata"] = (() => Known("file.key_value_metadata", Claim(5, Compact.Struct, 1_000_001, 1_000_001)), F(ListLimit, 1_000_001)),
        ["the columns of a row group"] = (() => Known("row_group.columns", Claim(1, Compact.Struct, 1_000_001, 1_000_001)), F(ListLimit, 1_000_001)),
        ["the encodings of a chunk"] = (() => Hook("column", Claim(2, Compact.I32, 1_000_001, 1_000_001)), F(ListLimit, 1_000_001)),
    };

    public static TheoryData<string> PastTheLimitCases => Names(PastTheLimit.Keys);

    [Theory]
    [MemberData(nameof(PastTheLimitCases))]
    public void A_list_or_a_map_of_more_than_a_million_elements_is_too_large(string where)
    {
        (Func<byte[]> build, string message) = PastTheLimit[where];
        AssertRefused(build(), message, IssueCodes.FooterTooLarge);
    }

    private static readonly Dictionary<string, Func<byte[]>> PastTheLimitAndTheBytesLeft = new()
    {
        ["a list"] = () => Hook("file", Claim(300, Compact.True, 1_000_001, 1_000_001 - 2)),
        ["a map"] = () => Hook("file", ClaimMap(300, 1_000_001, 1_000_001 - 3)),
        ["the schema"] = () => Known("file.schema", Claim(2, Compact.Struct, 2_000_000, 1_000_000)),
    };

    public static TheoryData<string> PastTheLimitAndTheBytesLeftCases => Names(PastTheLimitAndTheBytesLeft.Keys);

    [Theory]
    [MemberData(nameof(PastTheLimitAndTheBytesLeftCases))]
    public void A_list_past_the_limit_whose_elements_do_not_fit_the_bytes_left_stays_invalid(string where)
    {
        AssertRefused(PastTheLimitAndTheBytesLeft[where](), Truncated);
    }

    private static readonly Dictionary<string, Func<byte[]>> AtTheLimit = new()
    {
        ["a list of a million booleans"] = () => Hook("file", Claim(300, Compact.True, 1_000_000, 1_000_000)),
        ["a map of a million entries"] = () => Hook("file", ClaimMap(300, 1_000_000, 2_000_000)),
        ["key-value metadata of a million pairs without a key"] = () => Known("file.key_value_metadata", w => w.ListOf(5, Compact.Struct, 1_000_000).Raw(new byte[1_000_000])),
    };

    public static TheoryData<string> AtTheLimitCases => Names(AtTheLimit.Keys);

    [Theory]
    [MemberData(nameof(AtTheLimitCases))]
    public void A_list_or_a_map_of_a_million_elements_is_read(string where)
    {
        Assert.Equal(Summary(ParquetFooter.Parse(Baseline)), Summary(ParquetFooter.Parse(AtTheLimit[where]())));
    }

    /// <summary>
    /// The columns list of the footer, with one chunk, then lists of empty chunks in the same row group, each replacing
    /// the one before (P3), all of them counting: 4,194,304 chunks in all with the last list.
    /// </summary>
    private static byte[] ColumnChunksInAll(int last) => Hook("row_group", w =>
    {
        for (int i = 0; i < 83; i++)
        {
            w.ListOf(1, Compact.Struct, 50_000).Raw(new byte[50_000]);
        }

        w.ListOf(1, Compact.Struct, 44_302).Raw(new byte[44_302]);
        w.ListOf(1, Compact.Struct, last).Raw(new byte[last]);
    });

    [Fact]
    public void Column_chunks_up_to_4194304_in_all_the_lists_read_are_read_and_one_more_is_too_large()
    {
        // 1 + 83 * 50,000 + 44,302 + 1 = 4,194,304: the last list holds the one chunk of the one leaf.
        ParquetFooter footer = ParquetFooter.Parse(ColumnChunksInAll(1));
        Assert.False(Assert.Single(Assert.Single(footer.RowGroups).Columns).HasMetadata);
        AssertRefused(ColumnChunksInAll(2), ChunkLimit, IssueCodes.FooterTooLarge);
    }

    // ---- R6: a field header of type 0 ----

    private static readonly byte[] Marker = "MARKER-BEFORE-THE-STOP"u8.ToArray();

    /// <summary>A footer with an unknown binary field at the end of a structure, so that a test finds the stop byte after it.</summary>
    private static byte[] Marked(string where) => Hook(where, w => w.Bytes(200, Marker));

    private static byte[] WithStop(byte[] data, int at, byte header)
    {
        Assert.Equal(0, data[at]);
        byte[] changed = (byte[])data.Clone();
        changed[at] = header;
        return changed;
    }

    public static TheoryData<int> TypeZeroHeaders => new() { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xA0, 0xB0, 0xC0, 0xD0, 0xE0, 0xF0 };

    [Theory]
    [MemberData(nameof(TypeZeroHeaders))]
    public void A_field_header_of_type_0_ends_the_footer(int header)
    {
        Assert.Equal(Summary(ParquetFooter.Parse(Baseline)), Summary(ParquetFooter.Parse(WithStop(Baseline, Baseline.Length - 1, (byte)header))));
    }

    [Theory]
    [InlineData("statistics", 0x10)]
    [InlineData("statistics", 0xF0)]
    [InlineData("column", 0x70)]
    [InlineData("chunk", 0x10)]
    [InlineData("row_group", 0xF0)]
    [InlineData("leaf", 0x70)]
    [InlineData("root", 0x10)]
    public void A_field_header_of_type_0_ends_the_structure_that_it_is_in(string where, int header)
    {
        byte[] data = Marked(where);
        int at = data.AsSpan().IndexOf(Marker) + Marker.Length;
        Assert.Equal(Summary(ParquetFooter.Parse(data)), Summary(ParquetFooter.Parse(WithStop(data, at, (byte)header))));
    }

    [Fact]
    public void A_field_header_of_type_0_ends_the_footer_before_the_fields_after_it()
    {
        byte[] data = Known("file.created_by", w => w.Raw(0x10).Text(6, "after the end"));
        ParquetFooter parsed = ParquetFooter.Parse(data);
        Assert.Null(parsed.CreatedBy);
        Assert.Equal(Summary(ParquetFooter.Parse(Footers.Build(new FooterSpec { CreatedBy = null }))), Summary(parsed));
    }

    private static byte[] Mutate(Random random, byte[] data)
    {
        var output = new List<byte>(data);
        switch (random.Next(6))
        {
            case 0:
                for (int i = 1 + random.Next(4); i > 0; i--)
                {
                    output[random.Next(output.Count)] = (byte)random.Next(256);
                }

                break;
            case 1:
                for (int i = 1 + random.Next(3); i > 0; i--)
                {
                    int at = random.Next(output.Count);
                    output[at] ^= (byte)(1 << random.Next(8));
                }

                break;
            case 2:
            {
                int at = random.Next(output.Count);
                output.RemoveRange(at, output.Count - at);
                break;
            }

            case 3:
                output.InsertRange(random.Next(output.Count + 1), Enumerable.Range(0, 1 + random.Next(6)).Select(_ => (byte)random.Next(256)).ToArray());
                break;
            case 4:
            {
                int start = random.Next(output.Count);
                int stop = Math.Min(output.Count, start + 1 + random.Next(40));
                byte[] slice = output.GetRange(start, stop - start).ToArray();
                output.InsertRange(random.Next(output.Count + 1), slice);
                break;
            }

            default:
            {
                // A header of a list of a random type that claims a huge count.
                int at = random.Next(output.Count);
                output.RemoveAt(at);
                output.InsertRange(at, new byte[] { (byte)(0xF0 | random.Next(16)), 0xFF, 0xFF, 0xFF, 0xFF, 0x07 });
                break;
            }
        }

        return [.. output];
    }

    private static byte[] MetadataOf(string path)
    {
        byte[] file = File.ReadAllBytes(Fixture.LocalPath(path));
        int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(file.Length - 8, 4));
        return file.AsSpan(file.Length - 8 - length, length).ToArray();
    }

    [Fact]
    public void Changed_bytes_in_footers_and_files_raise_only_the_footer_exception_and_end_within_a_time_limit()
    {
        // On a large stack, so that a parser without the limit of P1 fails this test and not the test process.
        Stacks.Run(64 * Mib, () =>
        {
            var random = new Random(20261028);
            var sources = new List<byte[]>
            {
                Rich,
                Baseline,
                Footers.Build(new FooterSpec { Leaves = ["a", "b", "c"], RowGroupCount = 3, KeyValues = [("vorder", "true")] }),
                Footers.SchemaFooter([Footers.ListChain(3), Footers.ChainSchema(10)]),
                Footers.Bound(Footers.Fixed, Footers.BigEndian(-1, 16), Footers.BigEndian(1, 16), typeLength: 16, logical: (38, 4)),
            };
            sources.AddRange(Fixture.DataFiles().Take(2).Select(MetadataOf));
            int[] stated = [0, 0, -1, 1, -9, 100];
            int parsed = 0;
            int refused = 0;
            TimeSpan slowest = TimeSpan.Zero;
            var watch = Stopwatch.StartNew();
            for (int round = 0; round < 60000; round++)
            {
                byte[] changed = Mutate(random, sources[round % sources.Count]);
                bool include = round % 2 == 0;
                Action action;
                if (round % 4 == 3)
                {
                    // Through the reader, with the end of the file changed too and a stated size that may be wrong.
                    byte[] file = ParquetFiles.EndingWith(changed, changed.Length + 20);
                    if (random.Next(3) == 0)
                    {
                        file[file.Length - 1 - random.Next(8)] = (byte)random.Next(256);
                    }

                    long size = file.Length + stated[random.Next(stated.Length)];
                    int tailBytes = 12 + random.Next(64);
                    var storage = new VirtualFileStorage(file.Length, file);
                    action = () => Read(storage, size, tailBytes, include);
                }
                else
                {
                    action = () => ParquetFooter.Parse(changed, include);
                }

                var one = Stopwatch.StartNew();
                ParquetFooterException? error = OnlyFooterExceptions(action, $"round {round}");
                slowest = one.Elapsed > slowest ? one.Elapsed : slowest;
                if (error == null)
                {
                    parsed++;
                }
                else
                {
                    refused++;
                }
            }

            Assert.True(parsed > 0 && refused > 0, $"{parsed} parsed, {refused} refused");
            Assert.True(slowest < TimeSpan.FromSeconds(1), $"one footer took {slowest.TotalSeconds:F2} s");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"the loop took {watch.Elapsed.TotalSeconds:F1} s");
        });
    }

    // ---- R3: the checks of a footer read, in their order ----

    private static Func<VirtualFileStorage> Virtual(long size, byte[] end) => () => new VirtualFileStorage(size, end);

    private static byte[] Tail(int length, string magic = "PAR1")
    {
        byte[] tail = ParquetFiles.Tail(length);
        Encoding.ASCII.GetBytes(magic).CopyTo(tail, 4);
        return tail;
    }

    /// <summary>The cases of the checks: the storage, the stated size, the code, the message, and the reads.</summary>
    private static readonly Dictionary<string, (Func<VirtualFileStorage> Storage, long Stated, string Code, string Message, int Reads)> Checks = new()
    {
        ["a stated size of 11"] = (Virtual(11, Tail(1)), 11, Invalid, F(TooSmall, 11), 0),
        ["a stated size of 0"] = (Virtual(0, []), 0, Invalid, F(TooSmall, 0), 0),
        ["a negative stated size"] = (Virtual(0, []), -1, Invalid, F(TooSmall, -1), 0),
        ["a tail read shorter than asked"] = (Virtual(Size, Tail(100)), 2 * Size, Invalid, Shorter, 1),
        ["a short tail read before a wrong magic"] = (Virtual(Size, Tail(100, "XXXX")), 2 * Size, Invalid, Shorter, 1),
        ["PARE"] = (Virtual(Size, Tail(100, "PARE")), Size, IssueCodes.FooterEncrypted, Encrypted, 1),
        ["PARE before a length of 0"] = (Virtual(Size, Tail(0, "PARE")), Size, IssueCodes.FooterEncrypted, Encrypted, 1),
        ["PARE before a length over 64 MiB"] = (Virtual(Large, Tail(FooterReader.MaxFooterLength + 1, "PARE")), Large, IssueCodes.FooterEncrypted, Encrypted, 1),
        ["PAR2"] = (Virtual(Size, Tail(100, "PAR2")), Size, Invalid, NoMagic, 1),
        ["XXXX"] = (Virtual(Size, Tail(100, "XXXX")), Size, Invalid, NoMagic, 1),
        ["par1"] = (Virtual(Size, Tail(100, "par1")), Size, Invalid, NoMagic, 1),
        ["PARe"] = (Virtual(Size, Tail(100, "PARe")), Size, Invalid, NoMagic, 1),
        ["1RAP"] = (Virtual(Size, Tail(100, "1RAP")), Size, Invalid, NoMagic, 1),
        ["a wrong magic before a length of 0"] = (Virtual(Size, Tail(0, "PAR0")), Size, Invalid, NoMagic, 1),
        ["a length of 0"] = (Virtual(Size, Tail(0)), Size, Invalid, F(NotFitting, 0), 1),
        ["a length of -1"] = (Virtual(Size, Tail(-1)), Size, Invalid, F(NotFitting, -1), 1),
        ["the smallest length"] = (Virtual(Size, Tail(int.MinValue)), Size, Invalid, F(NotFitting, int.MinValue), 1),
        ["a length of the size minus 11"] = (Virtual(Size, Tail(Size - 11)), Size, Invalid, F(NotFitting, Size - 11), 1),
        ["a length over 64 MiB that does not fit"] = (Virtual(Size, Tail(FooterReader.MaxFooterLength + 1)), Size, Invalid, F(NotFitting, FooterReader.MaxFooterLength + 1), 1),
        ["a length of the size minus 12, whose zeros are parsed"] = (Virtual(Size, Tail(Size - 12)), Size, Invalid, Lacks, 1),
        ["a length over 64 MiB"] = (Virtual(Large, Tail(FooterReader.MaxFooterLength + 1)), Large, IssueCodes.FooterTooLarge, F(TooLarge, FooterReader.MaxFooterLength + 1), 1),
        ["a length of 64 MiB, read and parsed"] = (Virtual(Large, Tail(FooterReader.MaxFooterLength)), Large, Invalid, Lacks, 2),
    };

    public static TheoryData<string> CheckCases => Names(Checks.Keys);

    [Theory]
    [MemberData(nameof(CheckCases))]
    public void The_checks_of_a_footer_read_come_in_their_order_with_their_messages(string check)
    {
        (Func<VirtualFileStorage> storage, long stated, string code, string message, int reads) = Checks[check];
        VirtualFileStorage served = storage();
        ParquetFooterException error = ReadRefusal(served, stated);
        Assert.Equal((code, message), (error.Code, error.Message));
        Assert.Equal(reads, served.Reads.Count);
    }

    /// <summary>A storage that serves a file and cuts its second read in half.</summary>
    private sealed class ShortSecondRead(byte[] data) : IStorage
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyList<StorageEntry>> ListAsync(string path, CancellationToken cancellation) => Task.FromResult<IReadOnlyList<StorageEntry>>([]);

        public Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellation)
        {
            Reads++;
            byte[] range = data.AsSpan((int)offset, (int)Math.Min(length, data.Length - offset)).ToArray();
            return Task.FromResult(Reads == 1 ? range : range.AsSpan(0, range.Length / 2).ToArray());
        }
    }

    [Fact]
    public void A_second_read_that_is_short_is_invalid()
    {
        byte[] file = Footers.FileOf(Rich);
        var storage = new ShortSecondRead(file);
        ParquetFooterException error = ReadRefusal(storage, file.Length, tailBytes: 12);
        Assert.Equal((Invalid, Shorter), (error.Code, error.Message));
        Assert.Equal(2, storage.Reads);
    }

    private static readonly Dictionary<string, Func<byte[]>> AfterTheChecks = new()
    {
        ["a missing field"] = () => Footers.Build(new FooterSpec { Omit = { "file.num_rows" } }),
        ["chunks that do not match"] = () => Footers.Build(new FooterSpec { Leaves = ["a", "b"], Chunks = 1 }),
        ["too deep"] = () => Hook("file", w => Footers.NestedStructures(w, 300, 100)),
        ["a broken list"] = () => [0x19, 0xFC, .. Rich.Skip(2)],
    };

    public static TheoryData<string, int> AfterTheChecksCases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (string name in AfterTheChecks.Keys)
            {
                data.Add(name, 12);
                data.Add(name, 32768);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AfterTheChecksCases))]
    public void After_the_checks_the_reader_gives_the_outcome_of_the_parse_of_the_metadata(string footer, int tailBytes)
    {
        byte[] data = AfterTheChecks[footer]();
        ParquetFooterException expected = Refusal(() => ParquetFooter.Parse(data));
        byte[] file = Footers.FileOf(data);
        ParquetFooterException error = ReadRefusal(new VirtualFileStorage(file.Length, file), file.Length, tailBytes);
        Assert.Equal((expected.Code, expected.Message), (error.Code, error.Message));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(100)]
    [InlineData(32768)]
    public void A_valid_footer_reads_as_its_metadata_parses(int tailBytes)
    {
        byte[] file = ParquetFiles.EndingWith(Rich, Rich.Length + 5000);
        ParquetFooter read = Read(new VirtualFileStorage(file.Length, file), file.Length, tailBytes, includeDataValues: true);
        Assert.Equal(Summary(ParquetFooter.Parse(Rich, includeDataValues: true)), Summary(read));
        Assert.Equal((Rich.Length, tailBytes >= Rich.Length + 8 ? 1 : 2), (read.FooterLength, read.RequestCount));
    }

    // ---- R4: bounded work ----

    private static byte[] ManyLeaves(int count) => Footers.Build(new FooterSpec { Leaves = Enumerable.Range(0, count).Select(i => "c" + i).ToArray() });

    /// <summary>Many leaves at the bottom of a schema of 200 levels: their paths are long.</summary>
    private static byte[] DeepLeaves(int count)
    {
        Node field = Node.Group("g198", Enumerable.Range(0, count).Select(i => Node.Leaf("c" + i, "INT32")).ToArray());
        for (int level = 197; level >= 0; level--)
        {
            field = Node.Group("g" + level, field);
        }

        return Footers.SchemaFooter([field]);
    }

    private static byte[] UnknownFields(int count) => Hook("file", w =>
    {
        for (int i = 0; i < count; i++)
        {
            Footers.NestedChain(w, 300 + (i % 50), 30, [Compact.List, Compact.Struct, Compact.Map, Compact.Set]);
        }
    });

    /// <summary>A row group whose columns list comes count times: the last one wins.</summary>
    private static byte[] RepeatedColumns(int count) => Hook("row_group", w =>
    {
        for (int i = 0; i < count; i++)
        {
            Footers.ColumnsList(1, i)(w);
        }
    });

    private static readonly Dictionary<string, Func<int, byte[]>> Growth = new()
    {
        ["many leaves"] = ManyLeaves,
        ["many leaves deep in the schema"] = DeepLeaves,
        ["many unknown nested values"] = UnknownFields,
        ["a columns list repeated"] = RepeatedColumns,
    };

    public static TheoryData<string> GrowthCases => Names(Growth.Keys);

    private static long Allocated(byte[] data)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        OnlyFooterExceptions(() => ParquetFooter.Parse(data), "parse");
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static double BestSeconds(byte[] data)
    {
        double best = double.MaxValue;
        for (int run = 0; run < 3; run++)
        {
            var watch = Stopwatch.StartNew();
            OnlyFooterExceptions(() => ParquetFooter.Parse(data), "parse");
            best = Math.Min(best, watch.Elapsed.TotalSeconds);
        }

        return best;
    }

    [Theory]
    [MemberData(nameof(GrowthCases))]
    public void The_memory_and_the_time_of_a_parse_grow_with_the_length_of_the_footer(string footer)
    {
        Func<int, byte[]> build = Growth[footer];
        byte[] small = build(1000);
        byte[] large = build(8000);
        double ratio = (double)large.Length / small.Length;
        Assert.True(ratio > 6, $"{ratio} times the bytes");
        BestSeconds(small);
        double memory = (double)Allocated(large) / Math.Max(1, Allocated(small));
        Assert.True(memory < 2 * ratio, $"memory grows {memory:F1} times for {ratio:F1} times the bytes");
        double time = BestSeconds(large) / Math.Max(BestSeconds(small), 1e-4);
        Assert.True(time < 5 * ratio, $"time grows {time:F1} times for {ratio:F1} times the bytes");
    }

    [Fact]
    public void A_footer_of_a_megabyte_of_hostile_values_is_read_in_bounded_time()
    {
        byte[] data = Hook("file", w =>
        {
            for (int i = 0; i < 8500; i++)
            {
                Footers.NestedChain(w, 300 + (i % 50), 60, [Compact.List, Compact.Map]);
            }
        });
        Assert.True(data.Length > 1_000_000, $"{data.Length} bytes");
        byte[] file = Footers.FileOf(data);
        var watch = Stopwatch.StartNew();
        Stacks.Run(64 * Mib, () => OnlyFooterExceptions(() => Read(new VirtualFileStorage(file.Length, file), file.Length), "a megabyte of nested values"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.Elapsed.TotalSeconds:F1} s");
    }

    // ---- P1: one depth for every container ----

    private static readonly Dictionary<string, byte[]> Containers = new()
    {
        ["structures"] = [Compact.Struct],
        ["lists"] = [Compact.List],
        ["sets"] = [Compact.Set],
        ["maps"] = [Compact.Map],
        ["lists, structures, maps, and sets in turn"] = [Compact.List, Compact.Struct, Compact.Map, Compact.Set],
    };

    public static TheoryData<string> ContainerCases => Names(Containers.Keys);

    [Theory]
    [MemberData(nameof(ContainerCases))]
    public void Containers_nested_up_to_64_levels_with_the_footer_are_skipped_and_one_more_level_is_invalid(string kinds)
    {
        // The FileMetaData is the first level, as it is for the structures today.
        byte[] chain = Containers[kinds];
        byte[] fits = Hook("file", w => Footers.NestedChain(w, 300, 63, chain));
        Assert.Equal(Summary(ParquetFooter.Parse(Baseline)), Summary(ParquetFooter.Parse(fits)));
        AssertRefused(Hook("file", w => Footers.NestedChain(w, 300, 64, chain)), TooDeep);
        AssertRefused(Hook("file", w => Footers.NestedChain(w, 300, 65, chain)), TooDeep);
    }

    [Fact]
    public void The_lists_and_structures_of_the_footer_count_in_the_depth_of_what_they_hold()
    {
        // Statistics are the seventh level: FileMetaData, row_groups, RowGroup, columns, ColumnChunk, ColumnMetaData.
        byte[] fits = Hook("statistics", w => Footers.NestedLists(w, 300, 57));
        Assert.Equal(Summary(ParquetFooter.Parse(Baseline)), Summary(ParquetFooter.Parse(fits)));
        AssertRefused(Hook("statistics", w => Footers.NestedLists(w, 300, 58)), TooDeep);
    }

    /// <summary>
    /// Asserts that a footer that nests containers this deep is refused as too deep. A parser without the limit of P1
    /// recurses once for each level, so the same footer at 65 levels, which any stack holds, comes first: such a parser
    /// fails the test there, and not the test process. The deep one is parsed on a large stack, then on a small one.
    /// </summary>
    private static void AssertTooDeep(Func<int, byte[]> build, int depth)
    {
        AssertRefused(build(65), TooDeep);
        byte[] data = build(depth);
        Stacks.Run(256 * Mib, () => AssertRefused(data, TooDeep));
        Stacks.Run(256 * 1024, () => AssertRefused(data, TooDeep));
        byte[] file = Footers.FileOf(data);
        Stacks.Run(256 * 1024, () =>
        {
            ParquetFooterException error = ReadRefusal(new VirtualFileStorage(file.Length, file), file.Length);
            Assert.Equal((Invalid, TooDeep), (error.Code, error.Message));
        });
    }

    [Theory]
    [InlineData("file", 10_000)]
    [InlineData("file", 100_000)]
    [InlineData("statistics", 10_000)]
    [InlineData("statistics", 100_000)]
    [InlineData("leaf", 10_000)]
    [InlineData("leaf", 100_000)]
    public void Ten_thousand_and_a_hundred_thousand_nested_lists_are_too_deep_without_overflowing_the_stack(string where, int depth)
    {
        AssertTooDeep(levels => Hook(where, w => Footers.NestedLists(w, 300, levels)), depth);
    }

    private static readonly Dictionary<string, byte[]> PastSixtyFour = new()
    {
        ["maps"] = [Compact.Map],
        ["sets"] = [Compact.Set],
        ["maps and sets"] = [Compact.Map, Compact.Set],
        ["structures"] = [Compact.Struct],
    };

    public static TheoryData<string, int> PastSixtyFourCases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (int depth in new[] { 65, 100, 1000, 10_000 })
            {
                foreach (string kinds in PastSixtyFour.Keys)
                {
                    data.Add(kinds, depth);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(PastSixtyFourCases))]
    public void Maps_sets_and_structures_nested_past_64_levels_are_too_deep(string kinds, int depth)
    {
        byte[] chain = PastSixtyFour[kinds];
        AssertTooDeep(levels => Hook("column", w => Footers.NestedChain(w, 300, levels, chain)), depth);
    }

    // ---- P2: the schema tree ----

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(256)]
    public void A_schema_of_up_to_256_levels_below_the_root_is_parsed(int levels)
    {
        ParquetLeafColumn only = Assert.Single(ParquetFooter.Parse(Footers.SchemaFooter([Footers.ChainSchema(levels)])).Leaves);
        Assert.Equal(levels, only.Path.Count);
        Assert.Equal("bottom", only.Path[^1]);
    }

    [Fact]
    public void A_schema_of_257_levels_below_the_root_is_nested_too_deep()
    {
        AssertRefused(Footers.SchemaFooter([Footers.ChainSchema(257)]), SchemaTooDeep);
        AssertRefused(Footers.SchemaFooter([Node.Group("top", Footers.ListChain(128))]), SchemaTooDeep);
    }

    private static string Lists(int count) => string.Concat(Enumerable.Repeat("array<", count)) + "integer" + new string('>', count);

    private static readonly Dictionary<string, (Func<Node> Field, string Name, Func<string> DataType, int Levels)> DeepSchemas = new()
    {
        ["64 levels of structures"] = (() => Footers.ChainSchema(64), "g0", () => Footers.ChainType(64), 64),
        ["65 levels of structures"] = (() => Footers.ChainSchema(65), "g0", () => Footers.ChainType(65), 65),
        ["66 levels of structures"] = (() => Footers.ChainSchema(66), "g0", () => Footers.ChainType(66), 66),
        ["128 levels of structures"] = (() => Footers.ChainSchema(128), "g0", () => Footers.ChainType(128), 128),
        ["255 levels of structures"] = (() => Footers.ChainSchema(255), "g0", () => Footers.ChainType(255), 255),
        ["256 levels of structures"] = (() => Footers.ChainSchema(256), "g0", () => Footers.ChainType(256), 256),
        ["33 nested lists"] = (() => Footers.ListChain(33), "l0", () => Lists(33), 67),
        ["127 nested lists"] = (() => Footers.ListChain(127), "l0", () => Lists(127), 255),
        ["a structure of 127 nested lists"] = (() => Node.Group("top", Footers.ListChain(127)), "top", () => "struct<l0:" + Lists(127) + ">", 256),
    };

    public static TheoryData<string> DeepSchemaCases => Names(DeepSchemas.Keys);

    [Theory]
    [MemberData(nameof(DeepSchemaCases))]
    public async Task The_columns_of_a_folder_are_typed_for_every_schema_that_the_parser_accepts(string schema)
    {
        (Func<Node> field, string name, Func<string> dataType, int levels) = DeepSchemas[schema];
        using var lake = new TempLake();
        lake.Write("data/t/part-0001.parquet", Footers.FileOf(Footers.SchemaFooter([field()])));
        using PqaxDocument doc = await FolderFootprint.CollectAsync(lake, "data/t");
        TableRow table = Assert.Single(doc.Summary.Tables);
        Assert.Empty(FolderFootprint.IssuesOf(doc, table).Select(issue => issue.Code + ": " + issue.Message));
        Assert.Equal(((long?)2, (long?)1), (table.CollectedLevel, table.FooterCount));
        ColumnRow column = Assert.Single(FolderFootprint.ColumnsOf(doc, table));
        Assert.Equal((name, dataType(), (bool?)true), (column.Name, column.DataType, column.IsNullable));
        PhysicalColumnRow physical = Assert.Single(FolderFootprint.PhysicalColumnsOf(doc, table));
        Assert.Equal(levels, physical.Path!.Count);
        Assert.Equal((name, (long?)column.Id), (physical.Path[0], physical.Column));
    }

    [Fact]
    public async Task A_schema_of_257_levels_in_a_folder_is_an_issue_of_its_file_and_the_folder_has_no_columns()
    {
        using var lake = new TempLake();
        lake.Write("data/t/part-0001.parquet", Footers.FileOf(Footers.SchemaFooter([Footers.ChainSchema(257)])));
        using PqaxDocument doc = await FolderFootprint.CollectAsync(lake, "data/t");
        TableRow table = Assert.Single(doc.Summary.Tables);
        IssueRow issue = Assert.Single(FolderFootprint.IssuesOf(doc, table));
        Assert.Equal((Invalid, SchemaTooDeep, "part-0001.parquet"), (issue.Code, issue.Message, issue.Path));
        Assert.Empty(FolderFootprint.ColumnsOf(doc, table));
        Assert.Equal(0, table.FooterCount);
    }

    // ---- P3: repeated and missing fields ----

    [Theory]
    [InlineData("a", 2, 1)]
    [InlineData("a,b", 1, 2)]
    [InlineData("a,b", 2, 2)]
    [InlineData("a,b,c", 5, 3)]
    public void A_second_columns_list_of_a_row_group_replaces_the_first(string leaves, int first, int second)
    {
        var spec = new FooterSpec { Leaves = leaves.Split(','), Chunks = first };
        spec.Extra["row_group"] = Footers.ColumnsList(second, 70);
        byte[] data = Footers.Build(spec);
        ParquetFooterException? error = OnlyFooterExceptions(() => ParquetFooter.Parse(data), "a second columns list");
        Assert.True(error == null, error?.Message);
        ParquetRowGroup group = Assert.Single(ParquetFooter.Parse(data).RowGroups);
        Assert.Equal(Enumerable.Range(70, second).Select(i => (long)i), group.Columns.Select(chunk => chunk.NumValues));
        Assert.Equal(Enumerable.Repeat("SNAPPY", second), group.Columns.Select(chunk => chunk.Codec));
        Assert.Equal(Enumerable.Range(1000, second).Select(i => (long)i), group.Columns.Select(chunk => chunk.TotalCompressedSize));
        Assert.All(group.Columns, chunk => Assert.True(!chunk.HasStatistics && chunk.NullCount == null));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(1, 0)]
    public void A_second_columns_list_that_does_not_match_the_leaves_is_invalid_with_its_own_count(int first, int second)
    {
        var spec = new FooterSpec { Leaves = ["a"], Chunks = first };
        spec.Extra["row_group"] = Footers.ColumnsList(second);
        AssertRefused(Footers.Build(spec), string.Format(CultureInfo.InvariantCulture, Chunks, second, 1));
    }

    private static void SecondSchema(Compact w)
    {
        w.ListOf(2, Compact.Struct, 2);
        w.Element().Text(4, "root").Int32(5, 1).End();
        w.Element().Int32(1, Footers.Int32Type).Int32(3, Footers.Required).Text(4, "z").End();
    }

    private static void SecondRowGroups(Compact w)
    {
        w.ListOf(4, Compact.Struct, 2);
        for (int ordinal = 0; ordinal < 2; ordinal++)
        {
            w.Element();
            Footers.ColumnsList(1, 30 + ordinal)(w);
            w.Int64(2, 5).Int64(3, 6 + ordinal);
            w.End();
        }
    }

    private static void SecondPair(Compact w)
    {
        w.ListOf(5, Compact.Struct, 1);
        w.Element().Text(1, "first.key").Text(1, "org.apache.spark.version").Text(2, "3.4").Text(2, "3.5.1").End();
    }

    private static string Values(params object?[] values) => string.Join("|", values);

    /// <summary>A field written twice, the second time with another value: the hooks, what the parse gives, and what it must give.</summary>
    private static readonly Dictionary<string, (string Where, Action<Compact> Write, Func<ParquetFooter, string> Actual, string Expected)> Twice = new()
    {
        ["FileMetaData.version"] = ("file", w => w.Int32(1, 7), p => Values(p.Version), "7"),
        ["FileMetaData.num_rows"] = ("file", w => w.Int64(3, 55), p => Values(p.NumRows), "55"),
        ["FileMetaData.created_by"] = ("file", w => w.Text(6, "second writer"), p => Values(p.CreatedBy), "second writer"),
        ["FileMetaData.schema"] = ("file", SecondSchema, p => string.Join(";", p.Leaves.Select(l => Values(string.Join(".", l.Path), l.PhysicalType, l.Repetition))), "z|INT32|REQUIRED"),
        ["FileMetaData.row_groups"] = ("file", SecondRowGroups, p => string.Join(";", p.RowGroups.Select(g => Values(g.NumRows, g.Columns[0].NumValues))), "6|30;7|31"),
        ["FileMetaData.key_value_metadata"] = ("file", w =>
        {
            w.ListOf(5, Compact.Struct, 1).Element().Text(1, "vorder").Text(2, "x").End();
            w.ListOf(5, Compact.Struct, 1).Element().Text(1, "org.apache.spark.version").Text(2, "3.5.1").End();
        }, p => string.Join(";", p.KeyValueMetadata.Select(kv => Values(kv.Key, kv.ValueLength, kv.Value))), "org.apache.spark.version|5|3.5.1"),
        ["KeyValue.key and KeyValue.value"] = ("file", SecondPair, p => string.Join(";", p.KeyValueMetadata.Select(kv => Values(kv.Key, kv.ValueLength, kv.Value))), "org.apache.spark.version|5|3.5.1"),
        ["SchemaElement.name"] = ("leaf", w => w.Text(4, "renamed"), p => string.Join(".", p.Leaves[0].Path), "renamed"),
        ["SchemaElement.type"] = ("leaf", w => w.Int32(1, Footers.Int32Type), p => p.Leaves[0].PhysicalType, "INT32"),
        ["SchemaElement.repetition"] = ("leaf", w => w.Int32(3, Footers.Required), p => Values(p.Leaves[0].Repetition), "REQUIRED"),
        ["SchemaElement.converted_type"] = ("leaf", w => w.Int32(6, 0), p => Values(p.Leaves[0].ConvertedType), "UTF8"),
        ["SchemaElement.field_id"] = ("leaf", w => w.Int32(9, 5).Int32(9, 6), p => Values(p.Leaves[0].FieldId), "6"),
        ["SchemaElement.logicalType"] = ("leaf", w => w.Begin(10).Begin(1).End().End().Begin(10).Begin(6).End().End(), p => Values(p.Leaves[0].LogicalType), "DATE"),
        ["LogicalType, two members"] = ("leaf", w => w.Begin(10).Begin(1).End().Begin(6).End().End(), p => Values(p.Leaves[0].LogicalType), "DATE"),
        ["DecimalType.scale"] = ("leaf", w => w.Begin(10).Begin(5).Int32(1, 2).Int32(2, 10).Int32(1, 3).End().End(), p => Values(p.Leaves[0].LogicalType), "DECIMAL(10,3)"),
        ["RowGroup.num_rows"] = ("row_group", w => w.Int64(3, 77), p => Values(p.RowGroups[0].NumRows), "77"),
        ["RowGroup.total_byte_size"] = ("row_group", w => w.Int64(2, 88), p => Values(p.RowGroups[0].TotalByteSize), "88"),
        ["RowGroup.ordinal"] = ("row_group", w => w.Int16(7, 9), p => Values(p.RowGroups[0].Ordinal), "9"),
        ["RowGroup.sorting_columns"] = ("row_group", w =>
        {
            w.ListOf(4, Compact.Struct, 1).Element().Int32(1, 0).Bool(2, true).End();
            w.ListOf(4, Compact.Struct, 1).Element().Int32(1, 0).Bool(3, true).End();
        }, p => string.Join(";", p.RowGroups[0].SortingColumns!.Select(s => Values(s.ColumnIndex, s.Descending, s.NullsFirst))), "0|False|True"),
        ["ColumnChunk.file_path"] = ("chunk", w => w.Text(1, "a.parquet").Text(1, "b.parquet"), p => Values(First(p).FilePath), "b.parquet"),
        ["ColumnChunk.meta_data"] = ("chunk", Footers.SecondMetaData, p =>
        {
            ParquetColumnChunk c = First(p);
            return Values(c.Codec, string.Join(",", c.Encodings), c.NumValues, c.TotalUncompressedSize, c.TotalCompressedSize, c.DataPageOffset, c.DictionaryPageOffset, c.HasStatistics, c.NullCount, c.DistinctCount, c.HasMinMax);
        }, "SNAPPY|PLAIN|11|22|33|44||False|||False"),
        ["ColumnMetaData.codec"] = ("column", w => w.Int32(4, Footers.Snappy), p => Values(First(p).Codec), "SNAPPY"),
        ["ColumnMetaData.num_values"] = ("column", w => w.Int64(5, 33), p => Values(First(p).NumValues), "33"),
        ["ColumnMetaData.total_compressed_size"] = ("column", w => w.Int64(7, 44), p => Values(First(p).TotalCompressedSize), "44"),
        ["ColumnMetaData.encodings"] = ("column", w => w.ListOf(2, Compact.I32, 1).ValueI32(Footers.PlainDictionary), p => string.Join(",", First(p).Encodings), "PLAIN_DICTIONARY"),
        ["ColumnMetaData.dictionary_page_offset"] = ("column", w => w.Int64(11, 2).Int64(11, 3), p => Values(First(p).DictionaryPageOffset), "3"),
        ["ColumnMetaData.statistics"] = ("column", w => w.Begin(12).Int64(3, 9).End(), p => Values(First(p).NullCount, First(p).DistinctCount, First(p).HasMinMax), "9||False"),
        ["ColumnMetaData.encoding_stats"] = ("column", w =>
        {
            w.ListOf(13, Compact.Struct, 1).Element().Int32(1, Footers.DataPage).Int32(2, Footers.Plain).Int32(3, 1).End();
            w.ListOf(13, Compact.Struct, 1).Element().Int32(1, Footers.DataPage).Int32(2, Footers.RleDictionary).Int32(3, 4).End();
        }, p => string.Join(";", First(p).EncodingStats!.Select(s => s.ToString())), "DATA_PAGE:RLE_DICTIONARY:4"),
        ["PageEncodingStats.count"] = ("column", w => w.ListOf(13, Compact.Struct, 1).Element().Int32(1, Footers.DataPage).Int32(2, Footers.Plain).Int32(3, 1).Int32(3, 5).End(),
            p => string.Join(";", First(p).EncodingStats!.Select(s => s.ToString())), "DATA_PAGE:PLAIN:5"),
        ["ColumnMetaData.size_statistics"] = ("column", w => w.Begin(16).Int64(1, 5).End().Begin(16).End(), p => Values(First(p).UnencodedByteArrayDataBytes), ""),
        ["ColumnMetaData.bloom_filter_length"] = ("column", w => w.Int32(15, 3).Int32(15, 4), p => Values(First(p).BloomFilterLength), "4"),
        ["Statistics.null_count"] = ("statistics", w => w.Int64(3, 5), p => Values(First(p).NullCount), "5"),
        ["Statistics.distinct_count"] = ("statistics", w => w.Int64(4, 8), p => Values(First(p).DistinctCount), "8"),
        ["Statistics.min_value"] = ("statistics", w => w.Bytes(6, Footers.Le64(42)), p => Values(First(p).MinValue), "42"),
    };

    public static TheoryData<string> TwiceCases => Names(Twice.Keys);

    [Theory]
    [MemberData(nameof(TwiceCases))]
    public void A_field_that_appears_twice_takes_its_last_value_in_every_structure(string field)
    {
        (string where, Action<Compact> write, Func<ParquetFooter, string> actual, string expected) = Twice[field];
        byte[] data = Hook(where, write);
        ParquetFooterException? error = OnlyFooterExceptions(() => ParquetFooter.Parse(data, includeDataValues: true), "a field twice");
        Assert.True(error == null, error?.Message);
        Assert.Equal(expected, actual(ParquetFooter.Parse(data, includeDataValues: true)));
    }

    private static readonly Dictionary<string, (string Where, Action<Compact> Write, Func<ParquetFooter, string> Actual, string Expected)> UnexpectedTypes = new()
    {
        ["version"] = ("file", w => w.Text(1, "two"), p => Values(p.Version), "1"),
        ["num_rows"] = ("file", w => w.Text(3, "many"), p => Values(p.NumRows), "100"),
        ["name"] = ("leaf", w => w.Int32(4, 1), p => string.Join(".", p.Leaves[0].Path), "id"),
        ["row group num_rows"] = ("row_group", w => w.Text(3, "rows"), p => Values(p.RowGroups[0].NumRows), "100"),
        ["num_values"] = ("column", w => w.Text(5, "values"), p => Values(First(p).NumValues), "100"),
        ["null_count"] = ("statistics", w => w.Text(3, "nulls"), p => Values(First(p).NullCount), "0"),
    };

    public static TheoryData<string> UnexpectedTypeCases => Names(UnexpectedTypes.Keys);

    [Theory]
    [MemberData(nameof(UnexpectedTypeCases))]
    public void A_known_field_with_an_unexpected_wire_type_after_the_field_is_skipped(string field)
    {
        (string where, Action<Compact> write, Func<ParquetFooter, string> actual, string expected) = UnexpectedTypes[field];
        Assert.Equal(expected, actual(ParquetFooter.Parse(Hook(where, write))));
    }

    private static Action<Compact> MetaDataWithout(int missing) => w =>
    {
        w.Begin(3).Int32(1, Footers.Int64Type);
        foreach ((int field, long value) in new[] { (5, 100L), (6, 800L), (7, 400L), (9, 4L) })
        {
            if (field != missing)
            {
                w.Int64(field, value);
            }
        }

        w.End();
    };

    private static byte[] WithoutMetaData(int missing)
    {
        var spec = new FooterSpec { MetaData = false };
        spec.Extra["chunk"] = MetaDataWithout(missing);
        return Footers.Build(spec);
    }

    private static readonly Dictionary<string, (Func<byte[]> Footer, string Message)> Missing = new()
    {
        ["version"] = (() => Footers.Build(new FooterSpec { Omit = { "file.version" } }), Lacks),
        ["schema"] = (() => Footers.Build(new FooterSpec { Omit = { "file.schema" } }), Lacks),
        ["num_rows"] = (() => Footers.Build(new FooterSpec { Omit = { "file.num_rows" } }), Lacks),
        ["row_groups"] = (() => Footers.Build(new FooterSpec { Omit = { "file.row_groups" } }), Lacks),
        ["version with an unexpected type"] = (() => Known("file.version", w => w.Text(1, "1")), Lacks),
        ["the name of a schema element"] = (() => Footers.Build(new FooterSpec { Omit = { "leaf.name" } }), NoName),
        ["the columns of a row group"] = (() => Footers.Build(new FooterSpec { Omit = { "row_group.columns" } }), GroupLacks),
        ["the num_rows of a row group"] = (() => Footers.Build(new FooterSpec { Omit = { "row_group.num_rows" } }), GroupLacks),
        ["the total_byte_size of a row group"] = (() => Footers.Build(new FooterSpec { Omit = { "row_group.total_byte_size" } }), GroupLacks),
        ["columns with an unexpected type"] = (() => Known("row_group.columns", w => w.Text(1, "x")), GroupLacks),
        ["num_values"] = (() => WithoutMetaData(5), MetaLacks),
        ["total_uncompressed_size"] = (() => WithoutMetaData(6), MetaLacks),
        ["total_compressed_size"] = (() => WithoutMetaData(7), MetaLacks),
        ["data_page_offset"] = (() => WithoutMetaData(9), MetaLacks),
        ["more leaves than chunks"] = (() => Footers.Build(new FooterSpec { Leaves = ["a", "b"], Chunks = 1 }), string.Format(CultureInfo.InvariantCulture, Chunks, 1, 2)),
        ["more chunks than leaves"] = (() => Footers.Build(new FooterSpec { Leaves = ["a"], Chunks = 2 }), string.Format(CultureInfo.InvariantCulture, Chunks, 2, 1)),
        ["no chunks"] = (() => Footers.Build(new FooterSpec { Leaves = ["a", "b", "c"], Chunks = 0 }), string.Format(CultureInfo.InvariantCulture, Chunks, 0, 3)),
    };

    public static TheoryData<string> MissingCases => Names(Missing.Keys);

    [Theory]
    [MemberData(nameof(MissingCases))]
    public void A_required_field_that_is_missing_stays_invalid_as_today(string field)
    {
        (Func<byte[]> build, string message) = Missing[field];
        AssertRefused(build(), message);
    }

    // ---- P4: text ----

    public static TheoryData<string, string> Utf8Cases => new()
    {
        { "706c61696e204153434949", "plain ASCII" },
        { "c3a974c3a920e282ac20f09f9880", "été € \U0001F600" },
        { "ff", "�" },
        { "618062", "a�b" },
        { "e282", "�" },
        { "e28241", "�A" },
        { "f09f98", "�" },
        { "f09f98206f6b", "� ok" },
        { "eda080", "���" },
        { "c0af", "��" },
        { "e080af", "���" },
        { "f4908080", "����" },
        { "f888808080", "�����" },
        { "636166c3", "caf�" },
        { "c328", "�(" },
    };

    [Theory]
    [MemberData(nameof(Utf8Cases))]
    public void Every_string_is_utf8_with_a_replacement_character_for_each_maximal_invalid_subpart(string hex, string text)
    {
        byte[] raw = Convert.FromHexString(hex);
        var spec = new FooterSpec();
        spec.Replace["file.created_by"] = w => w.Bytes(6, raw);
        spec.Replace["leaf.name"] = w => w.Bytes(4, raw);
        spec.Replace["file.key_value_metadata"] = w =>
        {
            w.ListOf(5, Compact.Struct, 2);
            w.Element().Bytes(1, raw).Bytes(2, "v"u8.ToArray()).End();
            w.Element().Text(1, "org.apache.spark.version").Bytes(2, raw).End();
        };
        ParquetFooter parsed = ParquetFooter.Parse(Footers.Build(spec));
        Assert.Equal(text, parsed.CreatedBy);
        Assert.Equal([text], parsed.Leaves[0].Path);
        Assert.Equal([(text, (int?)1), ("org.apache.spark.version", raw.Length)], parsed.KeyValueMetadata.Select(pair => (pair.Key, pair.ValueLength)));
        Assert.Equal(text, parsed.KeyValueMetadata[1].Value);
    }

    /// <summary>
    /// The keys of the allow-list compare as OrdinalIgnoreCase compares them (P4 as amended: C# unchanged, Python follows
    /// it): the ASCII letters in any case.
    /// </summary>
    public static TheoryData<string> KeptKeys => Names(
    [
        "com.microsoft.parquet.vorder.enabled", "COM.MICROSOFT.PARQUET.VORDER.ENABLED", "x.VOrder.level", "vorder",
        "ORG.APACHE.SPARK.VERSION", "Org.Apache.Spark.Version", "org.apache.spark.version",
        "spark.sql.parquet.STRING_PREFIX_LENGTH", "String_Prefix_Length", "org.apache.ſpark.vorder.x",
    ]);

    /// <summary>
    /// Keys that OrdinalIgnoreCase does not match, because it does not fold a character outside ASCII into ASCII, as
    /// measured on .NET 10: the long s is not s, although its upper case is S; the dotless i is not i; the Kelvin sign is
    /// not k, although str.lower() of Python makes it k; the dotted capital I is not i.
    /// </summary>
    public static TheoryData<string> NotKeptKeys => Names(
    [
        "org.apache.ſpark.version", "org.apache.spark.verſion", "org.apache.spark.versıon",
        "ORG.APACHE.ſPARK.VERSıON", "spark.ſtring_prefix_length", "spark.string_prefıx_length",
        "org.apache.sparK.version", "org.apache.spark.versİon", "spark.strİng_prefix_length", "vörder",
        "org.apache.spark.version2", " org.apache.spark.version", "string_prefix_length.x", "ｖｏｒｄｅｒ",
    ]);

    [Theory]
    [MemberData(nameof(KeptKeys))]
    public void A_key_of_the_allow_list_is_kept_in_any_case_as_ordinal_ignore_case_compares_it(string key)
    {
        Assert.True(ParquetKeyValue.IsKept(key));
        ParquetFooter parsed = ParquetFooter.Parse(Footers.Build(new FooterSpec { KeyValues = [(key, "kept value")] }));
        Assert.Equal([(key, (int?)10, "kept value")], parsed.KeyValueMetadata.Select(pair => (pair.Key, pair.ValueLength, pair.Value)));
    }

    [Theory]
    [MemberData(nameof(NotKeptKeys))]
    public void A_key_that_ordinal_ignore_case_does_not_match_with_the_allow_list_is_not_kept(string key)
    {
        Assert.False(ParquetKeyValue.IsKept(key));
        ParquetFooter parsed = ParquetFooter.Parse(Footers.Build(new FooterSpec { KeyValues = [(key, "a data value")] }));
        Assert.Equal([(key, (int?)12, (string?)null)], parsed.KeyValueMetadata.Select(pair => (pair.Key, pair.ValueLength, pair.Value)));
    }

    private static readonly Dictionary<string, ((string Key, string? Value)[] Pairs, bool VOrdered, string? Level)> VOrderCases = new()
    {
        ["lower case"] = ([("com.microsoft.parquet.vorder.enabled", "true"), ("com.microsoft.parquet.vorder.level", "default")], true, "default"),
        ["any case"] = ([("COM.MICROSOFT.PARQUET.VORDER.ENABLED", "TRUE"), ("X.VOrder.Level", "High")], true, "High"),
        ["a long s before the suffix"] = ([("org.apache.ſpark.vorder.enabled", "True")], true, null),
        ["false"] = ([("com.microsoft.parquet.vorder.enabled", "false"), ("com.microsoft.parquet.vorder.level", "Default")], false, "Default"),
        ["another value"] = ([("com.microsoft.parquet.vorder.enabled", "yes")], false, null),
        ["another key"] = ([("com.microsoft.parquet.vorder.enabled.not", "true")], false, null),
    };

    public static TheoryData<string> VOrderCaseNames => Names(VOrderCases.Keys);

    [Theory]
    [MemberData(nameof(VOrderCaseNames))]
    public async Task The_v_order_keys_and_value_of_a_footer_compare_without_case(string pairs)
    {
        ((string Key, string? Value)[] keyValues, bool vordered, string? level) = VOrderCases[pairs];
        using var lake = new TempLake();
        lake.Write("data/t/part-0001.parquet", Footers.FileOf(Footers.Build(new FooterSpec { KeyValues = keyValues })));
        using PqaxDocument doc = await FolderFootprint.CollectAsync(lake, "data/t");
        TableRow table = Assert.Single(doc.Summary.Tables);
        WriterRow writer = Assert.Single(doc.Summary.Writers, row => row.Table == table.Id);
        Assert.Equal(((bool?)vordered, level), (writer.IsVOrdered, writer.VOrderLevel));
        FileRow file = Assert.Single(doc.ReadFiles(), row => row.Table == table.Id);
        Assert.Equal((bool?)vordered, file.IsVOrdered);
    }

    // ---- P5: the bounds of a decimal ----

    /// <summary>The text of a decimal from its unscaled value in big endian and its scale, as the collectors write it.</summary>
    private static string DecimalText(byte[] raw, int scale)
    {
        BigInteger unscaled = raw.Length == 0 ? BigInteger.Zero : new BigInteger(raw, isUnsigned: false, isBigEndian: true);
        if (scale <= 0)
        {
            return unscaled.ToString(CultureInfo.InvariantCulture);
        }

        string digits = BigInteger.Abs(unscaled).ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        string text = digits[..^scale] + "." + digits[^scale..];
        return unscaled.Sign < 0 ? "-" + text : text;
    }

    private static (string? Min, string? Max) Bounds(byte[] data)
    {
        ParquetFooterException? error = OnlyFooterExceptions(() => ParquetFooter.Parse(data, includeDataValues: true), "bounds");
        Assert.True(error == null, error?.Message);
        ParquetColumnChunk chunk = First(ParquetFooter.Parse(data, includeDataValues: true));
        Assert.True(chunk.HasMinMax);
        return (chunk.MinValue, chunk.MaxValue);
    }

    /// <summary>2^255 - 1, the largest value of 32 bytes.</summary>
    private static readonly byte[] Largest = [0x7F, .. Enumerable.Repeat((byte)0xFF, 31)];

    /// <summary>-2^255, the smallest value of 32 bytes.</summary>
    private static readonly byte[] Smallest = [0x80, .. new byte[31]];

    private static readonly Dictionary<string, (int Physical, int? Length, byte[] Min, byte[] Max, int Precision, int Scale)> Written = new()
    {
        ["16 bytes, scale 4"] = (Footers.Fixed, 16, Footers.BigEndian(-12345, 16), Footers.BigEndian(987654321, 16), 38, 4),
        ["1 byte, scale 2"] = (Footers.ByteArray, null, [0x85], [0x7F], 3, 2),
        ["no byte"] = (Footers.ByteArray, null, [], [0x01], 3, 2),
        ["32 bytes, scale 0"] = (Footers.ByteArray, null, Smallest, Largest, 77, 0),
        ["32 bytes, scale 76"] = (Footers.ByteArray, null, Smallest, Largest, 77, 76),
        ["32 bytes of a fixed length, scale 76"] = (Footers.Fixed, 32, Smallest, Largest, 76, 76),
        ["2 bytes, scale 76"] = (Footers.Fixed, 2, Footers.BigEndian(-2, 2), Footers.BigEndian(300, 2), 76, 76),
    };

    public static TheoryData<string> WrittenCases => Names(Written.Keys);

    [Theory]
    [MemberData(nameof(WrittenCases))]
    public void A_decimal_bound_of_at_most_32_bytes_and_a_scale_of_at_most_76_is_written(string bound)
    {
        (int physical, int? length, byte[] min, byte[] max, int precision, int scale) = Written[bound];
        (string?, string?) expected = (DecimalText(min, scale), DecimalText(max, scale));
        Assert.Equal(expected, Bounds(Footers.Bound(physical, min, max, typeLength: length, logical: (precision, scale))));

        // The same with the converted type, which gives the scale in its own field.
        Assert.Equal(expected, Bounds(Footers.Bound(physical, min, max, typeLength: length, converted: 5, scale: scale, precision: precision)));
    }

    public static TheoryData<int, string, int, string> IntegerDecimals => new()
    {
        { Footers.Int32Type, "39300000", 2, "123.45" },
        { Footers.Int32Type, "fbffffff", 2, "-0.05" },
        { Footers.Int64Type, "ffffffffffffffff", 76, "-0." + new string('0', 75) + "1" },
        { Footers.Int64Type, "ffffffffffffff7f", 0, "9223372036854775807" },
    };

    [Theory]
    [MemberData(nameof(IntegerDecimals))]
    public void A_decimal_bound_of_an_integer_is_written_up_to_a_scale_of_76(int physical, string hex, int scale, string text)
    {
        byte[] value = Convert.FromHexString(hex);
        Assert.Equal((text, text), Bounds(Footers.Bound(physical, value, value, logical: (18, scale))));
    }

    private static readonly Dictionary<string, (int Physical, int? Length, byte[] Value, int Precision, int Scale)> NotWritten = new()
    {
        ["33 bytes"] = (Footers.ByteArray, null, [0x01, .. new byte[32]], 77, 0),
        ["33 bytes of a fixed length"] = (Footers.Fixed, 33, [0x01, .. new byte[32]], 79, 2),
        ["2,000 bytes"] = (Footers.ByteArray, null, Enumerable.Repeat((byte)0x7F, 2000).ToArray(), 38, 2),
        ["scale 77"] = (Footers.Fixed, 16, Footers.BigEndian(5, 16), 38, 77),
        ["scale 1000"] = (Footers.Fixed, 16, Footers.BigEndian(5, 16), 38, 1000),
        ["scale 2^31 - 1"] = (Footers.Fixed, 16, Footers.BigEndian(5, 16), 38, int.MaxValue),
    };

    public static TheoryData<string> NotWrittenCases => Names(NotWritten.Keys);

    [Theory]
    [MemberData(nameof(NotWrittenCases))]
    public void A_decimal_bound_of_more_than_32_bytes_or_a_scale_over_76_is_null(string bound)
    {
        (int physical, int? length, byte[] value, int precision, int scale) = NotWritten[bound];

        // A scale over 76 is checked on 77 first, so that a parser that builds a string as long as the scale fails here
        // and not on the allocation of the larger scales.
        if (scale > 77)
        {
            Assert.Equal((null, null), Bounds(Footers.Bound(physical, value, value, typeLength: length, logical: (precision, 77))));
        }

        Assert.Equal((null, null), Bounds(Footers.Bound(physical, value, value, typeLength: length, logical: (precision, scale))));
        Assert.Equal((null, null), Bounds(Footers.Bound(physical, value, value, typeLength: length, converted: 5, scale: scale, precision: precision)));
    }

    [Fact]
    public void Each_bound_of_a_decimal_is_written_or_null_on_its_own()
    {
        byte[] small = Footers.BigEndian(-7, 2);
        byte[] tooLong = [0x01, .. new byte[32]];
        Assert.Equal(("-0.07", null), Bounds(Footers.Bound(Footers.ByteArray, small, tooLong, logical: (38, 2))));
        Assert.Equal((null, "-0.07"), Bounds(Footers.Bound(Footers.ByteArray, tooLong, small, logical: (38, 2))));
    }

    [Fact]
    public void A_decimal_bound_of_an_integer_with_a_scale_over_76_is_null()
    {
        Assert.Equal((null, null), Bounds(Footers.Bound(Footers.Int64Type, Footers.Le64(5), Footers.Le64(5), logical: (18, 77))));
        Assert.Equal((null, null), Bounds(Footers.Bound(Footers.Int32Type, Footers.Le32(5), Footers.Le32(5), converted: 5, scale: int.MaxValue, precision: 9)));
    }

    public static TheoryData<string, int, string, int?, int?, string> OtherBounds => new()
    {
        { "int32", Footers.Int32Type, "07000000", null, null, "7" },
        { "int64", Footers.Int64Type, "f7ffffffffffffff", null, null, "-9" },
        { "date", Footers.Int32Type, "01000000", 6, null, "1970-01-02" },
        { "timestamp", Footers.Int64Type, "0100000000000000", 10, null, "1970-01-01T00:00:00.000001" },
        { "string", Footers.ByteArray, "616263", 0, null, "abc" },
        { "binary", Footers.ByteArray, "ff00", null, null, "0xff00" },
        { "text without annotation", Footers.ByteArray, "6e61c3af7665", null, null, "naïve" },
        { "fixed", Footers.Fixed, "0102", null, 2, "0x0102" },
        { "double", Footers.DoubleType, "000000000000f83f", null, null, "1.5" },
        { "boolean", 0, "01", null, null, "true" },
    };

    [Theory]
    [MemberData(nameof(OtherBounds))]
    public void Every_other_bound_is_written_as_today(string type, int physical, string hex, int? converted, int? typeLength, string text)
    {
        Assert.NotEmpty(type);
        byte[] value = Convert.FromHexString(hex);
        Assert.Equal((text, text), Bounds(Footers.Bound(physical, value, value, typeLength: typeLength, converted: converted)));
    }
}

/// <summary>
/// Writes the Thrift compact protocol of parquet.thrift, as collect_kit.Thrift of the Python tests does, so that the
/// two suites write the same bytes. A field header takes the short form when the delta from the previous field id is 1
/// to 15, and the long form otherwise.
/// </summary>
internal sealed class Compact
{
    public const byte True = 1;
    public const byte False = 2;
    public const byte Byte = 3;
    public const byte I16 = 4;
    public const byte I32 = 5;
    public const byte I64 = 6;
    public const byte Double = 7;
    public const byte Binary = 8;
    public const byte List = 9;
    public const byte Set = 10;
    public const byte Map = 11;
    public const byte Struct = 12;
    public const byte Uuid = 13;

    private readonly List<byte> bytes = [];
    private readonly Stack<int> outer = new();
    private int last;

    public Compact(bool alwaysLong = false)
    {
        AlwaysLong = alwaysLong;
    }

    public bool AlwaysLong { get; }

    public static ulong Zigzag(long value) => (ulong)((value << 1) ^ (value >> 63));

    public Compact Varint(ulong value)
    {
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return this;
    }

    public Compact Header(int id, byte type, bool longForm = false)
    {
        int delta = id - last;
        if (delta > 0 && delta <= 15 && !(longForm || AlwaysLong))
        {
            bytes.Add((byte)((delta << 4) | type));
        }
        else
        {
            bytes.Add(type);
            Varint(Zigzag(id));
        }

        last = id;
        return this;
    }

    public Compact Int16(int id, short value) => Header(id, I16).Varint(Zigzag(value));

    public Compact Int32(int id, int value) => Header(id, I32).Varint(Zigzag(value));

    public Compact Int64(int id, long value) => Header(id, I64).Varint(Zigzag(value));

    public Compact Int8(int id, byte value)
    {
        Header(id, Byte);
        bytes.Add(value);
        return this;
    }

    public Compact Bool(int id, bool value) => Header(id, value ? True : False);

    public Compact Real(int id, double value)
    {
        Header(id, Double);
        return Raw(Footers.LeDouble(value));
    }

    public Compact Bytes(int id, byte[] value)
    {
        Header(id, Binary);
        return ValueBytes(value);
    }

    public Compact Text(int id, string value) => Bytes(id, Encoding.UTF8.GetBytes(value));

    public Compact UuidField(int id, byte[] value)
    {
        Header(id, Uuid);
        return Raw(value);
    }

    public Compact ValueBytes(byte[] value)
    {
        Varint((ulong)value.Length);
        bytes.AddRange(value);
        return this;
    }

    public Compact ValueText(string value) => ValueBytes(Encoding.UTF8.GetBytes(value));

    public Compact ValueI32(int value) => Varint(Zigzag(value));

    public Compact CollectionHeader(byte element, long count)
    {
        if (count < 15)
        {
            bytes.Add((byte)((count << 4) | element));
        }
        else
        {
            bytes.Add((byte)(0xF0 | element));
            Varint((ulong)count);
        }

        return this;
    }

    /// <summary>The header of a list (or, with kind <see cref="Set"/>, a set) field; the elements follow.</summary>
    public Compact ListOf(int id, byte element, long count, byte kind = List)
    {
        Header(id, kind);
        return CollectionHeader(element, count);
    }

    public Compact MapOf(int id, byte key, byte value, long count)
    {
        Header(id, Map);
        Varint((ulong)count);
        if (count != 0)
        {
            bytes.Add((byte)((key << 4) | value));
        }

        return this;
    }

    /// <summary>Starts a structure field: its field ids start again.</summary>
    public Compact Begin(int id)
    {
        Header(id, Struct);
        return Element();
    }

    /// <summary>Starts a structure that is an element of a list, or the value of a structure field.</summary>
    public Compact Element()
    {
        outer.Push(last);
        last = 0;
        return this;
    }

    /// <summary>Ends a structure, or the message at the top level, with the stop byte.</summary>
    public Compact End()
    {
        bytes.Add(0);
        last = outer.Count > 0 ? outer.Pop() : 0;
        return this;
    }

    public Compact Raw(params byte[] data)
    {
        bytes.AddRange(data);
        return this;
    }

    public byte[] ToArray() => [.. bytes];
}

/// <summary>What <see cref="Footers.Build"/> writes, as the arguments of collect_kit.footer() of the Python tests.</summary>
internal sealed class FooterSpec
{
    public string[] Leaves { get; init; } = ["id"];

    public int? Chunks { get; init; }

    public int RowGroupCount { get; init; } = 1;

    public (string Key, string? Value)[] KeyValues { get; init; } = [];

    public string? CreatedBy { get; init; } = "Synthetic writer 1.0";

    /// <summary>The fields left out, as <c>"struct.field"</c>.</summary>
    public HashSet<string> Omit { get; } = [];

    /// <summary>The fields written by a hook in place of their usual value.</summary>
    public Dictionary<string, Action<Compact>> Replace { get; } = new();

    /// <summary>More fields at the end of a structure: file, root, leaf, row_group, chunk, column, or statistics.</summary>
    public Dictionary<string, Action<Compact>> Extra { get; } = new();

    public bool MetaData { get; init; } = true;

    public bool Statistics { get; init; } = true;

    public bool AlwaysLong { get; init; }
}

/// <summary>A field of a schema for <see cref="Footers.SchemaFooter"/>, as collect_kit.Element of the Python tests.</summary>
internal sealed record Node(string Name, string? Type, string? Repetition, Node[] Children, string? Converted = null, string? Logical = null, int? TypeLength = null, int? Scale = null, int? Precision = null, int? FieldId = null)
{
    public static Node Leaf(string name, string physical, string? repetition = "OPTIONAL", string? converted = null, string? logical = null, int? typeLength = null, int? scale = null, int? precision = null, int? fieldId = null) =>
        new(name, physical, repetition, [], converted, logical, typeLength, scale, precision, fieldId);

    public static Node Group(string name, params Node[] children) => new(name, null, "OPTIONAL", children);

    public static Node Group(string name, string? repetition, string? converted, string? logical, params Node[] children) =>
        new(name, null, repetition, children, converted, logical);
}

/// <summary>The footers of the tests, written as collect_kit and test_collect_footer_hardening.py of the Python tests write them.</summary>
internal static class Footers
{
    public const int BooleanType = 0;
    public const int Int32Type = 1;
    public const int Int64Type = 2;
    public const int DoubleType = 5;
    public const int ByteArray = 6;
    public const int Fixed = 7;
    public const int Required = 0;
    public const int Optional = 1;
    public const int Repeated = 2;
    public const int Plain = 0;
    public const int PlainDictionary = 2;
    public const int Rle = 3;
    public const int RleDictionary = 8;
    public const int Uncompressed = 0;
    public const int Snappy = 1;
    public const int Zstd = 6;
    public const int DataPage = 0;
    public const int DictionaryPage = 2;
    public const int DataPageV2 = 3;

    private const int ConvertedInt64 = 18;

    public static readonly string[] PhysicalTypes = ["BOOLEAN", "INT32", "INT64", "INT96", "FLOAT", "DOUBLE", "BYTE_ARRAY", "FIXED_LEN_BYTE_ARRAY"];

    public static readonly string[] Repetitions = ["REQUIRED", "OPTIONAL", "REPEATED"];

    public static readonly string[] ConvertedTypes =
    [
        "UTF8", "MAP", "MAP_KEY_VALUE", "LIST", "ENUM", "DECIMAL", "DATE", "TIME_MILLIS", "TIME_MICROS", "TIMESTAMP_MILLIS", "TIMESTAMP_MICROS",
        "UINT_8", "UINT_16", "UINT_32", "UINT_64", "INT_8", "INT_16", "INT_32", "INT_64", "JSON", "BSON", "INTERVAL",
    ];

    private static readonly Dictionary<string, int> EmptyLogicalTypes = new()
    {
        ["STRING"] = 1, ["MAP"] = 2, ["LIST"] = 3, ["ENUM"] = 4, ["DATE"] = 6, ["UNKNOWN"] = 11, ["JSON"] = 12, ["BSON"] = 13, ["UUID"] = 14, ["FLOAT16"] = 15,
    };

    private static readonly Dictionary<string, int> TimeUnits = new() { ["MILLIS"] = 1, ["MICROS"] = 2, ["NANOS"] = 3 };

    public static byte[] Le32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] Le64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] LeDouble(double value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>A two's complement integer in big endian on a length, as a decimal of a byte array holds its unscaled value.</summary>
    public static byte[] BigEndian(BigInteger value, int length)
    {
        byte[] minimal = value.ToByteArray(isUnsigned: false, isBigEndian: true);
        var result = new byte[length];
        Array.Fill(result, value.Sign < 0 ? (byte)0xFF : (byte)0);
        Buffer.BlockCopy(minimal, 0, result, length - minimal.Length, minimal.Length);
        return result;
    }

    /// <summary>A small Parquet file: the magic, 100 zero bytes, the footer, its length, and the magic, as collect_kit.parquet_file.</summary>
    public static byte[] FileOf(byte[] metadata) => ParquetFiles.EndingWith(metadata, metadata.Length + 112);

    /// <summary>
    /// A FileMetaData with one INT64 leaf for each name of the leaves and row groups of 100 rows, each with one column
    /// chunk for each leaf (or <see cref="FooterSpec.Chunks"/>), as collect_kit.footer() of the Python tests.
    /// </summary>
    public static byte[] Build(FooterSpec? options = null)
    {
        FooterSpec o = options ?? new FooterSpec();
        var w = new Compact(o.AlwaysLong);

        void Field(string name, Action write)
        {
            if (o.Omit.Contains(name))
            {
                return;
            }

            if (o.Replace.TryGetValue(name, out Action<Compact>? hook))
            {
                hook(w);
            }
            else
            {
                write();
            }
        }

        void More(string name)
        {
            if (o.Extra.TryGetValue(name, out Action<Compact>? hook))
            {
                hook(w);
            }
        }

        int count = o.Chunks ?? o.Leaves.Length;
        Field("file.version", () => w.Int32(1, 1));
        Field("file.schema", () =>
        {
            w.ListOf(2, Compact.Struct, o.Leaves.Length + 1);
            w.Element();
            w.Text(4, "schema").Int32(5, o.Leaves.Length);
            More("root");
            w.End();
            foreach (string name in o.Leaves)
            {
                w.Element();
                w.Int32(1, Int64Type).Int32(3, Optional);
                Field("leaf.name", () => w.Text(4, name));
                w.Int32(6, ConvertedInt64);
                More("leaf");
                w.End();
            }
        });
        Field("file.num_rows", () => w.Int64(3, 100 * o.RowGroupCount));
        Field("file.row_groups", () =>
        {
            w.ListOf(4, Compact.Struct, o.RowGroupCount);
            for (int ordinal = 0; ordinal < o.RowGroupCount; ordinal++)
            {
                w.Element();
                Field("row_group.columns", () =>
                {
                    w.ListOf(1, Compact.Struct, count);
                    for (int index = 0; index < count; index++)
                    {
                        w.Element();
                        w.Int64(2, 4);
                        if (o.MetaData)
                        {
                            w.Begin(3);
                            w.Int32(1, Int64Type);
                            w.ListOf(2, Compact.I32, 2).ValueI32(Rle).ValueI32(Plain);
                            w.ListOf(3, Compact.Binary, 1).ValueText(o.Leaves[index % o.Leaves.Length]);
                            Field("column.codec", () => w.Int32(4, Zstd));
                            Field("column.num_values", () => w.Int64(5, 100));
                            w.Int64(6, 800 + index).Int64(7, 400 + index);
                            Field("column.data_page_offset", () => w.Int64(9, 4));
                            Field("column.dictionary_page_offset", () => { });
                            if (o.Statistics)
                            {
                                w.Begin(12);
                                Field("statistics.null_count", () => w.Int64(3, 0));
                                w.Int64(4, 7);
                                w.Bytes(5, Le64(99)).Bytes(6, Le64(1));
                                w.Bool(7, true).Bool(8, false);
                                More("statistics");
                                w.End();
                            }

                            More("column");
                            w.End();
                        }

                        More("chunk");
                        w.End();
                    }
                });
                Field("row_group.total_byte_size", () => w.Int64(2, 800 * count));
                Field("row_group.num_rows", () => w.Int64(3, 100));
                w.Int64(5, 4).Int64(6, 400 * count).Int16(7, (short)ordinal);
                More("row_group");
                w.End();
            }
        });
        if (o.KeyValues.Length > 0 || o.Replace.ContainsKey("file.key_value_metadata"))
        {
            Field("file.key_value_metadata", () =>
            {
                w.ListOf(5, Compact.Struct, o.KeyValues.Length);
                foreach ((string key, string? value) in o.KeyValues)
                {
                    w.Element();
                    w.Text(1, key);
                    if (value != null)
                    {
                        w.Text(2, value);
                    }

                    w.End();
                }
            });
        }

        if (o.CreatedBy != null)
        {
            Field("file.created_by", () => w.Text(6, o.CreatedBy));
        }

        More("file");
        w.End();
        return w.ToArray();
    }

    /// <summary>Fields that no version of parquet.thrift has, of every type of the compact protocol, as collect_kit.every_type.</summary>
    public static void EveryType(Compact w, int first = 100)
    {
        int id = first;
        w.Bool(id++, true);
        w.Bool(id++, false);
        w.Int8(id++, 0x7F);
        w.Int16(id++, -1234);
        w.Int32(id++, 2_000_000_000);
        w.Int64(id++, -(1L << 62));
        w.Real(id++, 3.25);
        w.Text(id++, "unknown text");
        w.Bytes(id++, Enumerable.Repeat(new byte[] { 0x00, 0xFF }, 150).SelectMany(pair => pair).ToArray());
        w.UuidField(id++, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        w.ListOf(id++, Compact.I32, 3).ValueI32(1).ValueI32(-2).ValueI32(3);
        w.ListOf(id++, Compact.I64, 20);
        for (long value = 0; value < 20; value++)
        {
            w.Varint(Compact.Zigzag(value * 1_000_000_007L));
        }

        w.ListOf(id++, Compact.Binary, 2).ValueText("a").ValueBytes(new byte[20]);
        w.ListOf(id++, Compact.True, 3).Raw(1, 2, 1);
        w.ListOf(id++, Compact.Double, 2).Raw(LeDouble(1.5)).Raw(LeDouble(-2.5));
        w.ListOf(id++, Compact.Uuid, 1).Raw(new byte[16]);
        w.ListOf(id++, Compact.I64, 2, Compact.Set).Varint(2).Varint(4);
        w.MapOf(id++, Compact.Binary, Compact.I32, 2).ValueText("k1").ValueI32(1).ValueText("k2").ValueI32(2);
        w.MapOf(id++, Compact.Binary, Compact.I32, 0);
        w.ListOf(id++, Compact.List, 2);
        w.CollectionHeader(Compact.I32, 1).ValueI32(5);
        w.CollectionHeader(Compact.I32, 0);
        w.Begin(id++);
        w.Int32(1, 1).Text(2, "nested").Bool(3, true);
        w.ListOf(4, Compact.Struct, 2);
        w.Element().Int64(1, 5).End();
        w.Element().Text(9, "x").End();
        w.Begin(5).Begin(1).Int32(1, 1).End().End();
        w.End();
        w.ListOf(id++, Compact.Map, 1);
        w.Varint(1).Raw((Compact.I32 << 4) | Compact.Struct).ValueI32(7).Element().Int32(1, 2).End();
    }

    /// <summary>An unknown field that holds structures nested depth levels deep, as collect_kit.nested_structures.</summary>
    public static void NestedStructures(Compact w, int field, int depth)
    {
        w.Begin(field);
        for (int i = 0; i < depth - 1; i++)
        {
            w.Begin(1);
        }

        for (int i = 0; i < depth; i++)
        {
            w.End();
        }
    }

    /// <summary>An unknown field that holds lists of lists, depth levels deep, with one i32 at the bottom, as collect_kit.nested_lists.</summary>
    public static void NestedLists(Compact w, int field, int depth)
    {
        w.ListOf(field, depth > 1 ? Compact.List : Compact.I32, 1);
        for (int level = depth - 1; level > 0; level--)
        {
            w.CollectionHeader(level > 1 ? Compact.List : Compact.I32, 1);
        }

        w.ValueI32(1);
    }

    /// <summary>
    /// An unknown field that holds containers nested depth levels deep, of the kinds in turn, with one i32 at the bottom:
    /// a list or a set of one element, a map of one entry whose value is the next level, a structure whose field 1 is the
    /// next level. Written without recursion, so that any depth can be built.
    /// </summary>
    public static void NestedChain(Compact w, int field, int depth, byte[] kinds)
    {
        var chain = new byte[depth];
        for (int i = 0; i < depth; i++)
        {
            chain[i] = kinds[i % kinds.Length];
        }

        w.Header(field, chain[0]);
        for (int i = 0; i < depth; i++)
        {
            byte inner = i + 1 < depth ? chain[i + 1] : Compact.I32;
            switch (chain[i])
            {
                case Compact.List:
                case Compact.Set:
                    w.CollectionHeader(inner, 1);
                    break;
                case Compact.Map:
                    w.Varint(1).Raw((byte)((Compact.I32 << 4) | inner)).ValueI32(0);
                    break;
                default:
                    w.Element().Header(1, inner);
                    break;
            }
        }

        w.ValueI32(1);
        for (int i = depth - 1; i >= 0; i--)
        {
            if (chain[i] == Compact.Struct)
            {
                w.End();
            }
        }
    }

    /// <summary>A second meta_data of a column chunk, with values of its own and no dictionary page, statistics, or anything else.</summary>
    public static void SecondMetaData(Compact w)
    {
        w.Begin(3);
        w.Int32(1, Int64Type);
        w.ListOf(2, Compact.I32, 1).ValueI32(Plain);
        w.ListOf(3, Compact.Binary, 1).ValueText("id");
        w.Int32(4, Snappy).Int64(5, 11).Int64(6, 22).Int64(7, 33).Int64(9, 44);
        w.End();
    }

    /// <summary>A hook that writes a columns list (field 1) of a row group with count chunks, whose values start at firstValues.</summary>
    public static Action<Compact> ColumnsList(int count, int firstValues = 0) => w =>
    {
        w.ListOf(1, Compact.Struct, count);
        for (int index = 0; index < count; index++)
        {
            w.Element();
            w.Int64(2, 4);
            w.Begin(3);
            w.Int32(1, Int64Type);
            w.ListOf(2, Compact.I32, 1).ValueI32(Plain);
            w.Int32(4, Snappy).Int64(5, firstValues + index).Int64(6, 2000 + index).Int64(7, 1000 + index);
            w.Int64(9, 4);
            w.End();
            w.End();
        }
    };

    /// <summary>The elements of the schema of the rich footer.</summary>
    private static readonly (string Name, int? Type, int? Repetition, int? Children, int? Converted, int? Scale, int? Precision, int? FieldId, int? TypeLength, string? Logical)[] RichSchema =
    [
        ("schema", null, null, 8, null, null, null, null, null, null),
        ("id", Int64Type, Required, null, 18, null, null, 1, null, "int64"),
        ("name", ByteArray, Optional, null, 0, null, null, 2, null, "string"),
        ("price", Fixed, Optional, null, 5, 4, 38, 3, 16, "decimal"),
        ("at", Int64Type, Optional, null, 10, null, null, 4, null, "timestamp"),
        ("tags", null, Optional, 1, 3, null, null, 5, null, "list"),
        ("list", null, Repeated, 1, null, null, null, null, null, null),
        ("element", Int32Type, Optional, null, null, null, null, 6, null, null),
        ("props", null, Optional, 1, 1, null, null, 7, null, "map"),
        ("key_value", null, Repeated, 2, null, null, null, null, null, null),
        ("key", ByteArray, Required, null, 0, null, null, null, null, "string"),
        ("value", DoubleType, Optional, null, null, null, null, null, null, null),
        ("day", Int32Type, Optional, null, 6, null, null, 8, null, "date"),
        ("blob", ByteArray, Optional, null, null, null, null, 9, null, null),
    ];

    /// <summary>The leaves of the rich footer: type, path, lower bound, upper bound.</summary>
    public static readonly (int Type, string[] Path, byte[] Min, byte[] Max)[] RichLeaves =
    [
        (Int64Type, ["id"], Le64(1), Le64(500)),
        (ByteArray, ["name"], "alpha"u8.ToArray(), "omega"u8.ToArray()),
        (Fixed, ["price"], BigEndian(-12345, 16), BigEndian(987654321, 16)),
        (Int64Type, ["at"], Le64(1_700_000_000_000_000), Le64(1_800_000_000_000_000)),
        (Int32Type, ["tags", "list", "element"], Le32(-3), Le32(42)),
        (ByteArray, ["props", "key_value", "key"], "a"u8.ToArray(), "z"u8.ToArray()),
        (DoubleType, ["props", "key_value", "value"], LeDouble(-1.5), LeDouble(2.25)),
        (Int32Type, ["day"], Le32(19000), Le32(20000)),
        (ByteArray, ["blob"], [0x00, 0xFF], [0xFF, 0xFE]),
    ];

    private static readonly (string Key, string? Value)[] RichKeys =
    [
        ("com.microsoft.parquet.vorder.enabled", "true"), ("com.microsoft.parquet.vorder.level", "default"),
        ("org.apache.spark.version", "3.5.1"), ("org.apache.spark.sql.parquet.row.metadata", "{\"type\":\"struct\"}"),
        ("no.value", null),
    ];

    private static void WriteLogical(Compact w, string kind)
    {
        w.Begin(10);
        switch (kind)
        {
            case "int64":
                w.Begin(10).Int8(1, 64).Bool(2, true).End();
                break;
            case "string":
                w.Begin(1).End();
                break;
            case "decimal":
                w.Begin(5).Int32(1, 4).Int32(2, 38).End();
                break;
            case "timestamp":
                w.Begin(8).Bool(1, true).Begin(2).Begin(2).End().End().End();
                break;
            case "list":
                w.Begin(3).End();
                break;
            case "map":
                w.Begin(2).End();
                break;
            case "date":
                w.Begin(6).End();
                break;
            default:
                throw new ArgumentException(kind);
        }

        w.End();
    }

    /// <summary>
    /// A footer with most of what parquet.thrift holds: logical and converted types, field ids, a list and a map, two row
    /// groups whose chunks have statistics with bounds, encoding statistics, size statistics, bloom filters, and indexes,
    /// sorting columns, key-value metadata, column orders, and unknown fields of every type.
    /// </summary>
    public static byte[] Rich()
    {
        var w = new Compact();
        w.Int32(1, 2);
        w.ListOf(2, Compact.Struct, RichSchema.Length);
        foreach (var e in RichSchema)
        {
            w.Element();
            if (e.Type != null)
            {
                w.Int32(1, e.Type.Value);
            }

            if (e.TypeLength != null)
            {
                w.Int32(2, e.TypeLength.Value);
            }

            if (e.Repetition != null)
            {
                w.Int32(3, e.Repetition.Value);
            }

            w.Text(4, e.Name);
            if (e.Children != null)
            {
                w.Int32(5, e.Children.Value);
            }

            if (e.Converted != null)
            {
                w.Int32(6, e.Converted.Value);
            }

            if (e.Scale != null)
            {
                w.Int32(7, e.Scale.Value);
            }

            if (e.Precision != null)
            {
                w.Int32(8, e.Precision.Value);
            }

            if (e.FieldId != null)
            {
                w.Int32(9, e.FieldId.Value);
            }

            if (e.Logical != null)
            {
                WriteLogical(w, e.Logical);
            }

            w.End();
        }

        w.Int64(3, 1000);
        w.ListOf(4, Compact.Struct, 2);
        for (int g = 0; g < 2; g++)
        {
            w.Element();
            w.ListOf(1, Compact.Struct, RichLeaves.Length);
            long uncompressed = 0;
            long compressed = 0;
            for (int i = 0; i < RichLeaves.Length; i++)
            {
                (int physical, string[] path, byte[] minimum, byte[] maximum) = RichLeaves[i];
                long start = 4 + (4000 * g) + (400 * i);
                w.Element();
                w.Int64(2, start + 50);
                w.Begin(3);
                w.Int32(1, physical);
                w.ListOf(2, Compact.I32, 3).ValueI32(Plain).ValueI32(Rle).ValueI32(RleDictionary);
                w.ListOf(3, Compact.Binary, path.Length);
                foreach (string name in path)
                {
                    w.ValueText(name);
                }

                w.Int32(4, (i + g) % 2 == 0 ? Zstd : Snappy);
                w.Int64(5, 500 + i).Int64(6, 2000 + (100 * i) + g).Int64(7, 1000 + (100 * i) + g);
                w.ListOf(8, Compact.Struct, 1).Element().Text(1, "chunk.key").Text(2, "chunk.value").End();
                w.Int64(9, start + 50).Int64(11, start);
                w.Begin(12);
                w.Bytes(1, maximum).Bytes(2, minimum).Int64(3, i).Int64(4, 10 + i).Bytes(5, maximum).Bytes(6, minimum);
                w.Bool(7, true).Bool(8, true);
                w.End();
                w.ListOf(13, Compact.Struct, 3);
                w.Element().Int32(1, DictionaryPage).Int32(2, Plain).Int32(3, 1).End();
                w.Element().Int32(1, DataPage).Int32(2, RleDictionary).Int32(3, 2).End();
                w.Element().Int32(1, DataPageV2).Int32(2, Plain).Int32(3, i % 3 == 0 ? 1 : 0).End();
                w.Int64(14, 90000 + i).Int32(15, 64 + i);
                w.Begin(16);
                if (physical == ByteArray)
                {
                    w.Int64(1, 300 + i);
                }

                w.ListOf(2, Compact.I64, 2).Varint(Compact.Zigzag(1)).Varint(Compact.Zigzag(2));
                w.ListOf(3, Compact.I64, 2).Varint(Compact.Zigzag(3)).Varint(Compact.Zigzag(4));
                w.End();
                w.End();
                w.Int64(4, 80000 + (100 * i) + g).Int32(5, 20 + i).Int64(6, 70000 + (100 * i) + g).Int32(7, 30 + i);
                w.End();
                uncompressed += 2000 + (100 * i) + g;
                compressed += 1000 + (100 * i) + g;
            }

            w.Int64(2, uncompressed).Int64(3, 500);
            w.ListOf(4, Compact.Struct, 2);
            w.Element().Int32(1, 0).Bool(2, false).Bool(3, true).End();
            w.Element().Int32(1, 3).Bool(2, true).Bool(3, false).End();
            w.Int64(5, 4 + (4000 * g)).Int64(6, compressed).Int16(7, (short)g);
            w.End();
        }

        w.ListOf(5, Compact.Struct, RichKeys.Length);
        foreach ((string key, string? value) in RichKeys)
        {
            w.Element().Text(1, key);
            if (value != null)
            {
                w.Text(2, value);
            }

            w.End();
        }

        w.Text(6, "parquet-mr version 1.13.1 (build synthetic)");
        w.ListOf(7, Compact.Struct, RichLeaves.Length);
        for (int i = 0; i < RichLeaves.Length; i++)
        {
            w.Element().Begin(1).End().End();
        }

        EveryType(w);
        w.End();
        return w.ToArray();
    }

    /// <summary>
    /// A footer with one leaf d of a physical type and its annotation, and one column chunk whose statistics have these
    /// bounds. logical is a DECIMAL logical type, as (precision, scale).
    /// </summary>
    public static byte[] Bound(int physical, byte[] minimum, byte[] maximum, int? typeLength = null, int? converted = null, int? scale = null, int? precision = null, (int Precision, int Scale)? logical = null)
    {
        var w = new Compact();
        w.Int32(1, 1);
        w.ListOf(2, Compact.Struct, 2);
        w.Element().Text(4, "schema").Int32(5, 1).End();
        w.Element().Int32(1, physical);
        if (typeLength != null)
        {
            w.Int32(2, typeLength.Value);
        }

        w.Int32(3, Optional).Text(4, "d");
        if (converted != null)
        {
            w.Int32(6, converted.Value);
        }

        if (scale != null)
        {
            w.Int32(7, scale.Value);
        }

        if (precision != null)
        {
            w.Int32(8, precision.Value);
        }

        if (logical != null)
        {
            w.Begin(10).Begin(5).Int32(1, logical.Value.Scale).Int32(2, logical.Value.Precision).End().End();
        }

        w.End();
        w.Int64(3, 1);
        w.ListOf(4, Compact.Struct, 1).Element();
        w.ListOf(1, Compact.Struct, 1).Element();
        w.Int64(2, 4);
        w.Begin(3).Int32(1, physical);
        w.ListOf(2, Compact.I32, 1).ValueI32(Plain);
        w.ListOf(3, Compact.Binary, 1).ValueText("d");
        w.Int32(4, Uncompressed).Int64(5, 1).Int64(6, 100).Int64(7, 50).Int64(9, 4);
        w.Begin(12).Int64(3, 0).Bytes(5, maximum).Bytes(6, minimum).End();
        w.End();
        w.End();
        w.Int64(2, 100).Int64(3, 1);
        w.End();
        w.End();
        return w.ToArray();
    }

    private static void WriteKitLogical(Compact w, string logical)
    {
        // The members of collect_kit._write_logical: a name of an empty member, DECIMAL:p:s, TIMESTAMP:unit:utc,
        // TIME:unit:utc, INTEGER:width:signed, or VARIANT.
        string[] parts = logical.Split(':');
        w.Begin(10);
        if (EmptyLogicalTypes.TryGetValue(logical, out int member))
        {
            w.Begin(member).End();
        }
        else if (parts[0] == "DECIMAL")
        {
            w.Begin(5).Int32(1, int.Parse(parts[2], CultureInfo.InvariantCulture)).Int32(2, int.Parse(parts[1], CultureInfo.InvariantCulture)).End();
        }
        else if (parts[0] == "TIME" || parts[0] == "TIMESTAMP")
        {
            w.Begin(parts[0] == "TIME" ? 7 : 8).Bool(1, bool.Parse(parts[2]));
            w.Begin(2).Begin(TimeUnits[parts[1]]).End().End();
            w.End();
        }
        else if (parts[0] == "INTEGER")
        {
            w.Begin(10).Int8(1, byte.Parse(parts[1], CultureInfo.InvariantCulture)).Bool(2, bool.Parse(parts[2])).End();
        }
        else if (parts[0] == "VARIANT")
        {
            w.Begin(16).End();
        }
        else
        {
            throw new ArgumentException("No such logical type in the kit: " + logical);
        }

        w.End();
    }

    /// <summary>
    /// A FileMetaData whose schema has these top-level fields, and one row group of rows with one column chunk for each
    /// leaf, as a writer orders them: depth first. As collect_kit.schema_footer of the Python tests, byte for byte.
    /// </summary>
    public static byte[] SchemaFooter(IReadOnlyList<Node> fields, long rows = 1)
    {
        var w = new Compact();
        w.Int32(1, 2);
        var elements = new List<(Node Node, string[] Path)>();
        var leaves = new List<(Node Node, string[] Path)>();

        void Walk(Node node, string[] path)
        {
            elements.Add((node, path));
            if (node.Type != null)
            {
                leaves.Add((node, path));
            }

            foreach (Node child in node.Children)
            {
                Walk(child, [.. path, child.Name]);
            }
        }

        foreach (Node field in fields)
        {
            Walk(field, [field.Name]);
        }

        w.ListOf(2, Compact.Struct, elements.Count + 1);
        w.Element().Text(4, "schema").Int32(5, fields.Count).End();
        foreach ((Node e, _) in elements)
        {
            w.Element();
            if (e.Type != null)
            {
                w.Int32(1, Array.IndexOf(PhysicalTypes, e.Type));
            }

            if (e.TypeLength != null)
            {
                w.Int32(2, e.TypeLength.Value);
            }

            if (e.Repetition != null)
            {
                w.Int32(3, Array.IndexOf(Repetitions, e.Repetition));
            }

            w.Text(4, e.Name);
            if (e.Type == null)
            {
                w.Int32(5, e.Children.Length);
            }

            if (e.Converted != null)
            {
                w.Int32(6, Array.IndexOf(ConvertedTypes, e.Converted));
            }

            if (e.Scale != null)
            {
                w.Int32(7, e.Scale.Value);
            }

            if (e.Precision != null)
            {
                w.Int32(8, e.Precision.Value);
            }

            // collect_kit.schema_footer writes no field id; a field id of a node is written as the Python tests of the
            // nested columns write it, in its place between the precision and the logical type.
            if (e.FieldId != null)
            {
                w.Int32(9, e.FieldId.Value);
            }

            if (e.Logical != null)
            {
                WriteKitLogical(w, e.Logical);
            }

            w.End();
        }

        w.Int64(3, rows);
        w.ListOf(4, Compact.Struct, 1);
        w.Element();
        w.ListOf(1, Compact.Struct, leaves.Count);
        for (int index = 0; index < leaves.Count; index++)
        {
            (Node e, string[] path) = leaves[index];
            w.Element();
            w.Int64(2, 4);
            w.Begin(3);
            w.Int32(1, Array.IndexOf(PhysicalTypes, e.Type ?? "INT64"));
            w.ListOf(2, Compact.I32, 1).ValueI32(Plain);
            w.ListOf(3, Compact.Binary, path.Length);
            foreach (string name in path)
            {
                w.ValueText(name);
            }

            w.Int32(4, Zstd).Int64(5, rows).Int64(6, 100 + index).Int64(7, 50 + index).Int64(9, 4);
            w.End();
            w.End();
        }

        w.Int64(2, 100L * leaves.Count).Int64(3, rows);
        w.End();
        w.Text(6, "Synthetic writer 1.0");
        w.End();
        return w.ToArray();
    }

    /// <summary>A top-level field with groups nested inside it, levels in all below the root, the leaf bottom included.</summary>
    public static Node ChainSchema(int levels)
    {
        Node field = Node.Leaf("bottom", "INT32");
        for (int level = levels - 2; level >= 0; level--)
        {
            field = Node.Group("g" + level.ToString(CultureInfo.InvariantCulture), field);
        }

        return field;
    }

    /// <summary>The type of the column of <see cref="ChainSchema"/>, as Spark reads it.</summary>
    public static string ChainType(int levels)
    {
        string text = "integer";
        string child = "bottom";
        for (int level = levels - 2; level >= 0; level--)
        {
            text = "struct<" + child + ":" + text + ">";
            child = "g" + level.ToString(CultureInfo.InvariantCulture);
        }

        return text;
    }

    /// <summary>Lists of the three levels of the specification nested in one another, 2 * lists + 1 levels with the leaf.</summary>
    public static Node ListChain(int lists, string name = "l0")
    {
        Node element = Node.Leaf("element", "INT32");
        for (int level = lists - 1; level >= 0; level--)
        {
            element = Node.Group(level == 0 ? name : "element", "OPTIONAL", null, "LIST", Node.Group("list", "REPEATED", null, null, element));
        }

        return element;
    }
}

/// <summary>Collects a folder of Parquet files with <see cref="PqaxCollector"/> on a <see cref="LocalStorage"/>, and reads the footprint back.</summary>
internal static class FolderFootprint
{
    public static async Task<PqaxDocument> CollectAsync(TempLake lake, string folder, CollectorOptions? options = null)
    {
        var storage = new LocalStorage(lake.Root);
        TableLocation location = Assert.Single(await TableDiscovery.DiscoverFolderAsync(storage, folder, CancellationToken.None));
        return await CollectAsync(storage, location, options);
    }

    public static async Task<PqaxDocument> CollectAsync(IStorage storage, TableLocation location, CollectorOptions? options = null)
    {
        FootprintBuilder builder = await new PqaxCollector(storage, options ?? new CollectorOptions(), new ParquetNetCheckpointReader()).CollectAsync([location], null, CancellationToken.None);
        using var stream = new MemoryStream();
        builder.Write(stream);
        byte[] data = stream.ToArray();
        Fixture.AssertValid(data);
        return Fixture.Open(data);
    }

    public static List<ColumnRow> ColumnsOf(PqaxDocument doc, TableRow table) => doc.Summary.Columns.Where(column => column.Table == table.Id).ToList();

    public static List<IssueRow> IssuesOf(PqaxDocument doc, TableRow table) => doc.Summary.Issues.Where(issue => issue.Table == table.Id).ToList();

    public static List<PhysicalSchemaRow> PhysicalSchemasOf(PqaxDocument doc, TableRow table) => doc.Summary.PhysicalSchemas.Where(schema => schema.Table == table.Id).ToList();

    public static List<PhysicalColumnRow> PhysicalColumnsOf(PqaxDocument doc, TableRow table)
    {
        var schemas = PhysicalSchemasOf(doc, table).Select(schema => schema.Id).ToHashSet();
        return doc.Summary.PhysicalColumns.Where(column => column.PhysicalSchema is long schema && schemas.Contains(schema)).ToList();
    }
}

/// <summary>Runs an action on a thread with a stack of a given size, and rethrows what it throws.</summary>
internal static class Stacks
{
    public static void Run(int stackBytes, Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    failure = ExceptionDispatchInfo.Capture(error);
                }
            },
            stackBytes);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
