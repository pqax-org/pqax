using System.Text;

namespace Pqax.Tests;

/// <summary>
/// The three container samples of the kit, crafted byte by byte from the entries of sample.pqax: an encrypted entry, and
/// two uses of ZIP64 that a reader does not need. The validator reports their codes, the reader opens them, and their
/// bytes are what the kit says, read here with a parser of this file (the contract of the hardening before 1.0,
/// section 5).
/// </summary>
public class ContainerSampleTests
{
    private const uint LocalHeader = 0x04034b50;
    private const uint CentralHeader = 0x02014b50;
    private const uint EndRecord = 0x06054b50;
    private const uint Zip64EndRecord = 0x06064b50;
    private const uint Zip64Locator = 0x07064b50;
    private const int Zip64Extra = 0x0001;

    private static readonly Dictionary<string, string> Codes = new(StringComparer.Ordinal)
    {
        ["container.encrypted.pqax"] = "container.encrypted",
        ["container.zip64.pqax"] = "container.zip64",
        ["container.zip64.extraField.pqax"] = "container.zip64",
    };

    public static TheoryData<string> Samples => Kit.Data(Codes.Keys);

    public static TheoryData<string> Zip64Samples => Kit.Data(["container.zip64.pqax", "container.zip64.extraField.pqax"]);

    private static string CodeOf(Action action) => Assert.Throws<PqaxFormatException>(action).Code;

    /// <summary>The summary tables and the detail tables but commits, each with its rows as the reader returns them.</summary>
    private static readonly (string Name, Func<PqaxDocument, IEnumerable<object>> Rows)[] TablesButCommits =
    [
        ("sources", doc => doc.Summary.Sources.Rows), ("tables", doc => doc.Summary.Tables.Rows),
        ("columns", doc => doc.Summary.Columns.Rows), ("partitionSpecs", doc => doc.Summary.PartitionSpecs.Rows),
        ("partitionFields", doc => doc.Summary.PartitionFields.Rows), ("sortOrders", doc => doc.Summary.SortOrders.Rows),
        ("sortFields", doc => doc.Summary.SortFields.Rows), ("physicalSchemas", doc => doc.Summary.PhysicalSchemas.Rows),
        ("physicalColumns", doc => doc.Summary.PhysicalColumns.Rows), ("writers", doc => doc.Summary.Writers.Rows),
        ("operations", doc => doc.Summary.Operations.Rows), ("issues", doc => doc.Summary.Issues.Rows),
        ("modelBindings", doc => doc.Summary.ModelBindings.Rows), ("tableBindings", doc => doc.Summary.TableBindings.Rows),
        ("columnBindings", doc => doc.Summary.ColumnBindings.Rows), ("partitions", doc => doc.ReadPartitions().Rows),
        ("files", doc => doc.ReadFiles().Rows), ("rowGroups", doc => doc.ReadRowGroups().Rows),
        ("columnChunks", doc => doc.ReadColumnChunks().Rows), ("deleteFiles", doc => doc.ReadDeleteFiles().Rows),
    ];

    private static void AssertTablesOfSample(PqaxDocument doc, bool withCommits)
    {
        using PqaxDocument sample = PqaxDocument.Open(Path.Combine(Kit.Samples, "sample.pqax"));
        Assert.Equal(sample.Manifest.Collection, doc.Manifest.Collection);
        Assert.Equal(sample.Manifest.Parts.Select(part => part.Name), doc.Manifest.Parts.Select(part => part.Name));
        foreach ((string name, Func<PqaxDocument, IEnumerable<object>> rows) in TablesButCommits)
        {
            Assert.True(rows(sample).SequenceEqual(rows(doc)), $"{name} differs from sample.pqax");
        }

        if (withCommits)
        {
            Assert.Equal(sample.ReadCommits().Rows, doc.ReadCommits().Rows);
        }
    }

    // ---- a small zip parser ----

    private sealed record End(int Offset, int Disk, int DirectoryDisk, int DiskEntries, int Entries, long Size, long Start);

    private sealed record Central(string Name, int Flags, int Method, long CompressedSize, long Size, long Offset, List<(int Id, int Length)> Extra);

    private static End EndOf(byte[] zip)
    {
        for (int i = zip.Length - 22; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(zip, i) == EndRecord)
            {
                return new End(i, BitConverter.ToUInt16(zip, i + 4), BitConverter.ToUInt16(zip, i + 6), BitConverter.ToUInt16(zip, i + 8),
                    BitConverter.ToUInt16(zip, i + 10), BitConverter.ToUInt32(zip, i + 12), BitConverter.ToUInt32(zip, i + 16));
            }
        }

        throw new InvalidDataException("no end of central directory record");
    }

    private static List<Central> CentralOf(byte[] zip)
    {
        End end = EndOf(zip);
        var entries = new List<Central>();
        int p = (int)end.Start;
        for (int i = 0; i < end.Entries; i++)
        {
            Assert.Equal(CentralHeader, BitConverter.ToUInt32(zip, p));
            int nameLength = BitConverter.ToUInt16(zip, p + 28);
            int extraLength = BitConverter.ToUInt16(zip, p + 30);
            int commentLength = BitConverter.ToUInt16(zip, p + 32);
            var extra = new List<(int, int)>();
            int q = p + 46 + nameLength;
            int stop = q + extraLength;
            while (q + 4 <= stop)
            {
                int length = BitConverter.ToUInt16(zip, q + 2);
                extra.Add((BitConverter.ToUInt16(zip, q), length));
                q += 4 + length;
            }

            Assert.Equal(stop, q);
            entries.Add(new Central(Encoding.UTF8.GetString(zip, p + 46, nameLength), BitConverter.ToUInt16(zip, p + 8), BitConverter.ToUInt16(zip, p + 10),
                BitConverter.ToUInt32(zip, p + 20), BitConverter.ToUInt32(zip, p + 24), BitConverter.ToUInt32(zip, p + 42), extra));
            p = stop + commentLength;
        }

        Assert.Equal(end.Start + end.Size, p);
        return entries;
    }

    private static int LocalFlags(byte[] zip, long offset)
    {
        Assert.Equal(LocalHeader, BitConverter.ToUInt32(zip, (int)offset));
        return BitConverter.ToUInt16(zip, (int)offset + 6);
    }

    // ---- the kit, the validator, and the reader ----

    [Fact]
    public void The_kit_lists_the_three_samples_with_their_codes()
    {
        foreach ((string file, string code) in Codes)
        {
            Assert.True(File.Exists(Path.Combine(Kit.Invalid, file)), file);
            Assert.Contains(code, Kit.InvalidCodes(file));
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void The_validator_reports_the_code(string file)
    {
        ValidationResult byPath = PqaxValidator.Validate(Path.Combine(Kit.Invalid, file));
        using var stream = new MemoryStream(Kit.InvalidBytes(file));
        ValidationResult byStream = PqaxValidator.Validate(stream);
        foreach (ValidationResult result in new[] { byPath, byStream })
        {
            Assert.Contains(Codes[file], result.Issues.Select(issue => issue.Code));
            Assert.False(result.IsValid);
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void The_reader_opens_the_sample(string file)
    {
        using PqaxDocument byPath = PqaxDocument.Open(Path.Combine(Kit.Invalid, file));
        using PqaxDocument byStream = PqaxDocument.Open(new MemoryStream(Kit.InvalidBytes(file)));
        foreach (PqaxDocument doc in new[] { byPath, byStream })
        {
            Assert.Equal("pqax", doc.Manifest.Format);
            Assert.Equal(6, doc.RowCount("commits"));
            Assert.Equal(2, doc.Summary.Tables.Count);
        }
    }

    [Fact]
    public void An_encrypted_part_fails_only_the_read_of_its_table_with_container_encrypted()
    {
        using PqaxDocument doc = PqaxDocument.Open(Path.Combine(Kit.Invalid, "container.encrypted.pqax"));
        AssertTablesOfSample(doc, withCommits: false);
        Assert.Equal(6, doc.RowCount("commits"));
        Assert.Equal("container.encrypted", CodeOf(() => doc.ReadCommits()));
        Assert.Equal("container.encrypted", CodeOf(() => doc.ReadCommits()));
        Assert.Equal("container.encrypted", CodeOf(() => doc.ReadRaw("commits")));
        Assert.Equal(5, doc.ReadFiles().Count);
    }

    [Theory]
    [MemberData(nameof(Zip64Samples))]
    public void A_sample_with_zip64_records_reads_as_sample(string file)
    {
        using PqaxDocument doc = PqaxDocument.Open(Path.Combine(Kit.Invalid, file));
        Assert.Empty(Kit.Differences(Kit.Shape(doc), Kit.Expected("sample"), Kit.ShapeKeys));
        AssertTablesOfSample(doc, withCommits: true);
    }

    // ---- the bytes of the samples ----

    [Fact]
    public void Commits_json_of_container_encrypted_has_bit_0_of_its_flags_set_and_no_other_entry_has()
    {
        byte[] zip = Kit.InvalidBytes("container.encrypted.pqax");
        List<Central> entries = CentralOf(zip);
        Assert.Equal(Kit.CentralDirectory(Kit.SampleBytes("sample.pqax")).Entries.Select(entry => entry.Name), entries.Select(entry => entry.Name));
        foreach (Central entry in entries)
        {
            bool encrypted = entry.Name == "commits.json";
            Assert.True(encrypted == ((entry.Flags & 1) == 1), $"{entry.Name}: central directory");
            Assert.True(encrypted == ((LocalFlags(zip, entry.Offset) & 1) == 1), $"{entry.Name}: local header");
        }

        Central commits = entries.Single(entry => entry.Name == "commits.json");
        Assert.Equal(8, commits.Method);

        // The data is the 12-byte encryption header, then the encrypted deflated data.
        Assert.True(commits.CompressedSize > 12);

        // The other entries are those of sample.pqax.
        Dictionary<string, byte[]> sample = Kit.Entries(Kit.SampleBytes("sample.pqax")).ToDictionary(entry => entry.Name, entry => entry.Content);
        foreach (Central entry in entries.Where(entry => entry.Name != "commits.json"))
        {
            long start = Kit.DataOffset(zip, entry.Offset);
            using var inflated = new System.IO.Compression.DeflateStream(new MemoryStream(zip, (int)start, (int)entry.CompressedSize), System.IO.Compression.CompressionMode.Decompress);
            using var content = new MemoryStream();
            inflated.CopyTo(content);
            Assert.Equal(sample[entry.Name], content.ToArray());
        }
    }

    [Fact]
    public void Container_zip64_has_a_zip64_end_record_and_its_locator_before_the_end_record_which_keeps_the_real_values()
    {
        byte[] zip = Kit.InvalidBytes("container.zip64.pqax");
        End end = EndOf(zip);
        Assert.Equal((0, 0), (end.Disk, end.DirectoryDisk));
        Assert.Equal((7, 7), (end.DiskEntries, end.Entries));
        Assert.NotEqual(0xFFFFFFFFL, end.Size);
        Assert.NotEqual(0xFFFFFFFFL, end.Start);

        // The locator is right before the end record, and points to the ZIP64 end record.
        int locator = end.Offset - 20;
        Assert.Equal(Zip64Locator, BitConverter.ToUInt32(zip, locator));
        Assert.Equal(0u, BitConverter.ToUInt32(zip, locator + 4));
        long record = (long)BitConverter.ToUInt64(zip, locator + 8);
        Assert.Equal(1u, BitConverter.ToUInt32(zip, locator + 16));
        Assert.Equal(Zip64EndRecord, BitConverter.ToUInt32(zip, (int)record));

        // The record follows the central directory, ends where the locator starts, and has the values of the end record.
        long recordSize = (long)BitConverter.ToUInt64(zip, (int)record + 4);
        Assert.Equal(end.Start + end.Size, record);
        Assert.Equal(locator, record + 12 + recordSize);
        Assert.True(recordSize >= 44);
        Assert.Equal((0u, 0u), (BitConverter.ToUInt32(zip, (int)record + 16), BitConverter.ToUInt32(zip, (int)record + 20)));
        Assert.Equal(
            ((ulong)end.DiskEntries, (ulong)end.Entries, (ulong)end.Size, (ulong)end.Start),
            (BitConverter.ToUInt64(zip, (int)record + 24), BitConverter.ToUInt64(zip, (int)record + 32),
                BitConverter.ToUInt64(zip, (int)record + 40), BitConverter.ToUInt64(zip, (int)record + 48)));
        Assert.Equal(7, CentralOf(zip).Count);
    }

    [Fact]
    public void Container_zip64_extraField_gives_commits_json_a_zip64_extra_field_with_no_data()
    {
        byte[] zip = Kit.InvalidBytes("container.zip64.extraField.pqax");
        List<Central> entries = CentralOf(zip);
        Central commits = entries.Single(entry => entry.Name == "commits.json");
        Assert.Contains((Zip64Extra, 0), commits.Extra);

        // Its sizes and offset fit in 32 bits, so the field has nothing to hold.
        Assert.DoesNotContain(0xFFFFFFFFL, new[] { commits.CompressedSize, commits.Size, commits.Offset });
        foreach (Central entry in entries.Where(entry => entry.Name != "commits.json"))
        {
            Assert.DoesNotContain(Zip64Extra, entry.Extra.Select(field => field.Id));
        }
    }

    [Theory]
    [InlineData("container.encrypted.pqax")]
    [InlineData("container.zip64.extraField.pqax")]
    public void The_other_samples_have_no_zip64_locator(string file)
    {
        byte[] zip = Kit.InvalidBytes(file);
        End end = EndOf(zip);

        // A locator would be the 20 bytes right before the end record, after the central directory.
        Assert.Equal(end.Start + end.Size, end.Offset);
        Assert.NotEqual(Zip64Locator, BitConverter.ToUInt32(zip, end.Offset - 20));
    }
}
