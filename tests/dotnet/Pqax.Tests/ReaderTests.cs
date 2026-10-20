using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pqax.Tests;

/// <summary>
/// The reader: what it reads and when, the facts of conformance/README.md that the expected files do not hold, the
/// invalid samples, and the cases that the kit has no file for (plan 4.1 and 4.3).
/// </summary>
public class ReaderTests
{
    public static TheoryData<string> PqaxSamples =>
        Kit.Data(Kit.ExpectedNames().Where(name => Kit.ExpectedPart(Kit.Expected(name)) is null));

    public static TheoryData<string> RefusedFiles => Kit.Data(Kit.Refused.Keys);

    public static TheoryData<string> OtherInvalidFiles =>
        Kit.Data(Kit.InvalidCases().Select(pair => pair.Key).Where(file => !Kit.Refused.ContainsKey(file)));

    private static PqaxDocument OpenInvalid(string file) => PqaxDocument.Open(Path.Combine(Kit.Invalid, file));

    private static string CodeOf(Action action) => Assert.Throws<PqaxFormatException>(action).Code;

    // ---- what the reader reads ----

    [Theory]
    [MemberData(nameof(PqaxSamples))]
    public void Opening_reads_the_manifest_and_the_summary_and_no_detail_part(string name)
    {
        byte[] data = File.ReadAllBytes(Kit.SamplePath(Kit.Expected(name)));
        using var stream = new CountingStream(data);
        using PqaxDocument doc = PqaxDocument.Open(stream, leaveOpen: true);
        Assert.Equal(["manifest.json", "summary.json"], Kit.Touched(Kit.EntrySpans(data), stream.Ranges).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_detail_table_is_read_from_its_parts_only_and_then_kept()
    {
        byte[] data = Kit.SampleBytes("multipart.pqax");
        Dictionary<string, (long, long)> spans = Kit.EntrySpans(data);
        using var stream = new CountingStream(data);
        using PqaxDocument doc = PqaxDocument.Open(stream, leaveOpen: true);
        stream.Ranges.Clear();
        Assert.Equal(52, doc.ReadColumnChunks().Count);
        Assert.Equal(["columnChunks.1.json", "columnChunks.2.json", "columnChunks.3.json"],
            Kit.Touched(spans, stream.Ranges).Order(StringComparer.Ordinal));
        stream.Ranges.Clear();
        Assert.Equal(5, doc.ReadFiles().Count);
        Assert.Equal(["files.1.json", "files.2.json"], Kit.Touched(spans, stream.Ranges).Order(StringComparer.Ordinal));
        stream.Ranges.Clear();
        Assert.Same(doc.ReadColumnChunks(), doc.ReadColumnChunks());
        Assert.Empty(stream.Ranges);
    }

    [Fact]
    public void A_stored_embedded_footprint_is_read_in_place()
    {
        byte[] vpax = Kit.SampleBytes("embedded-stored.vpax");
        var entry = Kit.CentralDirectory(vpax).Entries.Single(e => e.Name == Kit.EmbeddedPart);
        Assert.Equal(0, entry.Method);
        long start = Kit.DataOffset(vpax, entry.Offset);
        byte[] inner = vpax.AsSpan((int)start, (int)entry.CompressedSize).ToArray();
        using var stream = new CountingStream(vpax);
        using PqaxDocument doc = PqaxDocument.OpenEmbedded(stream, Kit.EmbeddedPart, leaveOpen: true);
        Assert.Equal("stored", doc.EmbeddedCompression);
        Assert.Equal(["manifest.json", "summary.json"], Kit.Touched(Kit.EntrySpans(inner, start), stream.Ranges).Order(StringComparer.Ordinal));
        Assert.True(stream.BytesRead < vpax.Length, "the whole package was read");
    }

    [Fact]
    public void A_stream_is_left_open_only_when_asked()
    {
        byte[] data = Kit.SampleBytes("sample.pqax");
        var kept = new CountingStream(data);
        PqaxDocument.Open(kept, leaveOpen: true).Dispose();
        Assert.False(kept.Disposed);
        var closed = new CountingStream(data);
        PqaxDocument.Open(closed, leaveOpen: false).Dispose();
        Assert.True(closed.Disposed);
    }

    // ---- the facts of conformance/README.md ----

    [Fact]
    public void Sample_keeps_known_empty_values()
    {
        using PqaxDocument doc = PqaxDocument.Open(Path.Combine(Kit.Samples, "sample.pqax"));
        IReadOnlyDictionary<string, string>? metrics = doc.ReadCommits()[1].Metrics;
        Assert.NotNull(metrics);
        Assert.Empty(metrics);
        IReadOnlyList<string>? features = doc.Summary.Tables[1].Delta?.ReaderFeatures;
        Assert.NotNull(features);
        Assert.Empty(features);
    }

    [Fact]
    public void Multipart_joins_the_parts_and_fills_missing_fields_with_null()
    {
        using PqaxDocument doc = PqaxDocument.Open(Path.Combine(Kit.Samples, "multipart.pqax"));
        PqaxTable<FileRow> files = doc.ReadFiles();
        Assert.Equal([40L, null, null, null, null], files.Select(file => file.DeletionVectorSize));
        Assert.Equal([1L, 1L, 2L, null, null], files.Select(file => file.Partition));
        using PqaxDocument sample = PqaxDocument.Open(Path.Combine(Kit.Samples, "sample.pqax"));
        Assert.Equal(sample.ReadFiles().Rows, files.Rows);
        Assert.Equal(sample.ReadColumnChunks().Rows, doc.ReadColumnChunks().Rows);
        Assert.Equal(sample.ReadRowGroups().Rows, doc.ReadRowGroups().Rows);
    }

    [Fact]
    public void Extensions_are_ignored_or_kept_where_the_api_keeps_them()
    {
        using PqaxDocument doc = PqaxDocument.Open(Path.Combine(Kit.Samples, "extensions.pqax"));
        PqaxTable<ColumnChunkRow> chunks = doc.ReadColumnChunks();
        Assert.All(chunks, chunk => Assert.Null(chunk.Encodings));
        Assert.False(chunks.ExtraFields.ContainsKey("encodings"));
        Assert.Equal("[7,9]", Kit.Canonical(new JsonArray(doc.Summary.Tables.ExtraFields["acme_score"]
            .Select(value => JsonNode.Parse(value!.Value.GetRawText())).ToArray())));
        Assert.All(doc.ReadFiles().ExtraFields["acme_tier"], value => Assert.Equal("hot", value!.Value.GetString()));
        Assert.Equal(5, doc.ReadFiles().ExtraFields["acme_tier"].Count);
        JsonElement build = doc.Manifest.Extensions["acme_build"];
        Assert.Equal("acme-writer", build.GetProperty("tool").GetString());
        Assert.Equal(7, build.GetProperty("number").GetInt32());
        Assert.Equal(2, doc.RowCount("acme_reviews"));
        Assert.Equal(2, doc.ReadRaw("acme_reviews").Count);
        Assert.Equal(3, doc.RowCount("acme_details"));
        RawTable details = doc.ReadRaw("acme_details");
        Assert.Equal(3, details.Count);
        Assert.Equal(["a", "b", "c"], details.Fields["label"].Select(value => value!.Value.GetString()));
        Distribution? sizes = doc.Summary.Tables[0].FileSizes;
        Assert.NotNull(sizes);
        Assert.Equal(3, sizes.Count);
        Assert.Equal(159121, sizes.Sum);
        Assert.NotNull(sizes.Extensions);
        Assert.Equal(83049, sizes.Extensions["ninetiethPercentile"].GetInt64());
        Assert.DoesNotContain(doc.Manifest.Parts, part => part.Name == "acme_readme.txt");
        using PqaxDocument sample = PqaxDocument.Open(Path.Combine(Kit.Samples, "sample.pqax"));
        Assert.Equal(sample.ReadRowGroups().Rows, doc.ReadRowGroups().Rows);
        Assert.Equal(sample.ReadCommits().Rows, doc.ReadCommits().Rows);
        Assert.Equal(sample.ReadPartitions().Rows, doc.ReadPartitions().Rows);
    }

    [Fact]
    public void A_later_depth_is_kept_as_written()
    {
        using PqaxDocument doc = PqaxDocument.Open(Path.Combine(Kit.Samples, "later-depth.pqax"));
        Assert.Equal(["Pages", "Pages"], doc.Summary.Tables.Select(table => table.DetailDepth));
    }

    [Fact]
    public void A_later_second_number_of_the_version_is_read()
    {
        using PqaxDocument doc = PqaxDocument.Open(Path.Combine(Kit.Samples, "later-version.pqax"));
        Assert.Equal("0.9", doc.Manifest.FormatVersion);
        Assert.Equal(52, doc.ReadColumnChunks().Count);
    }

    [Theory]
    [InlineData("embedded-stored.vpax", "stored")]
    [InlineData("embedded-deflated.vpax", "deflated")]
    public void An_embedded_footprint_has_the_tables_of_the_sample(string file, string compression)
    {
        using PqaxDocument sample = PqaxDocument.Open(Path.Combine(Kit.Samples, "sample.pqax"));
        using PqaxDocument fromPath = PqaxDocument.OpenEmbedded(Path.Combine(Kit.Samples, file));
        using var stream = new MemoryStream(Kit.SampleBytes(file));
        using PqaxDocument fromStream = PqaxDocument.OpenEmbedded(stream);
        foreach (PqaxDocument doc in new[] { fromPath, fromStream })
        {
            Assert.Equal(Kit.EmbeddedPart, doc.EmbeddedPart);
            Assert.Equal(compression, doc.EmbeddedCompression);
            Assert.Equal(sample.Manifest.Collection, doc.Manifest.Collection);
            Assert.Equal(sample.Summary.Tables.Rows, doc.Summary.Tables.Rows);
            Assert.Equal(sample.Summary.Columns.Rows, doc.Summary.Columns.Rows);
            Assert.Equal(sample.ReadFiles().Rows, doc.ReadFiles().Rows);
            Assert.Equal(sample.ReadColumnChunks().Rows, doc.ReadColumnChunks().Rows);
            Assert.Equal(sample.ReadCommits().Rows, doc.ReadCommits().Rows);
        }

        using PqaxDocument plain = PqaxDocument.Open(Path.Combine(Kit.Samples, "sample.pqax"));
        Assert.Null(plain.EmbeddedPart);
        Assert.Null(plain.EmbeddedCompression);
    }

    // ---- the invalid samples, read by a reader ----

    [Theory]
    [MemberData(nameof(RefusedFiles))]
    public void A_reader_refuses_three_invalid_files(string file)
    {
        Assert.Equal(Kit.Refused[file], CodeOf(() => OpenInvalid(file).Dispose()));
        using var stream = new MemoryStream(Kit.InvalidBytes(file));
        Assert.Equal(Kit.Refused[file], CodeOf(() => PqaxDocument.Open(stream, leaveOpen: true).Dispose()));
    }

    [Theory]
    [MemberData(nameof(OtherInvalidFiles))]
    public void A_reader_opens_every_other_invalid_file(string file)
    {
        using PqaxDocument doc = OpenInvalid(file);
        Assert.NotNull(doc.Summary);
        Assert.True(doc.RowCount("tables") >= 0);
    }

    [Fact]
    public void A_byte_order_mark_is_skipped()
    {
        using PqaxDocument doc = OpenInvalid("json.bom.pqax");
        Assert.Empty(Kit.Differences(Kit.Shape(doc), Kit.Expected("sample"), Kit.ShapeKeys));
    }

    [Fact]
    public void A_detail_part_that_is_not_json_fails_only_the_read_of_its_table()
    {
        using PqaxDocument doc = OpenInvalid("json.invalid.pqax");
        Assert.Equal(2, doc.Summary.Tables.Count);
        Assert.Equal(2, doc.ReadPartitions().Count);
        Assert.Equal(5, doc.ReadFiles().Count);
        Assert.Equal(13, doc.ReadRowGroups().Count);
        Assert.Equal(52, doc.ReadColumnChunks().Count);
        Assert.Equal("json.invalid", CodeOf(() => doc.ReadCommits()));
    }

    [Fact]
    public void A_detail_part_that_is_missing_fails_only_the_read_of_its_table()
    {
        using PqaxDocument doc = OpenInvalid("manifest.partMissing.pqax");
        Assert.Equal(5, doc.ReadFiles().Count);
        Assert.Equal("manifest.partMissing", CodeOf(() => doc.ReadCommits()));
    }

    [Fact]
    public void A_value_of_another_json_type_reads_as_null_and_raw_keeps_it()
    {
        using PqaxDocument doc = OpenInvalid("json.type.pqax");
        Assert.Null(doc.ReadFiles()[0].FooterSize);
        Assert.Equal(1813, doc.ReadFiles()[1].FooterSize);
        JsonElement? raw = doc.ReadRaw("files").Fields["footerSize"][0];
        Assert.NotNull(raw);
        Assert.Equal(JsonValueKind.String, raw.Value.ValueKind);
        Assert.Equal("1139", raw.Value.GetString());
    }

    [Fact]
    public void A_reference_to_a_missing_row_is_kept_and_finds_no_row()
    {
        using PqaxDocument doc = OpenInvalid("table.reference.single.pqax");
        Assert.Equal([99L, 1L, 2L, 3L, 3L], doc.ReadFiles().Select(file => file.Writer));
        Assert.Null(doc.Summary.Writers.FindById(99));
        Assert.Null(doc.Summary.Writers.FindById((long?)null));
        Assert.Equal(1, doc.Summary.Writers.FindById(1)?.Id);
    }

    [Fact]
    public void The_rows_of_a_part_come_from_its_longest_array()
    {
        using PqaxDocument stated = OpenInvalid("part.rowCount.manifest.pqax");
        Assert.Equal(7, stated.RowCount("commits"));
        Assert.Equal(6, stated.ReadCommits().Count);
        using PqaxDocument shorter = OpenInvalid("part.rowCount.array.pqax");
        PqaxTable<CommitRow> commits = shorter.ReadCommits();
        Assert.Equal(6, commits.Count);
        Assert.Null(commits[5].Engine);
        Assert.Equal("9", commits[0].CommitId);
    }

    // ---- cases that the kit has no file for ----

    [Fact]
    public void A_listed_summary_that_is_missing_reads_as_empty_summary_tables()
    {
        byte[] data = Kit.Rewrite(Kit.SampleBytes("sample.pqax"), (name, content) => name == "summary.json" ? null : content);
        using var stream = new MemoryStream(data);
        using PqaxDocument doc = PqaxDocument.Open(stream);
        foreach (string table in Kit.SummaryTables)
        {
            Assert.Equal(0, Kit.Typed(doc, table).Count);
        }

        Assert.Equal(5, doc.ReadFiles().Count);
        Assert.Equal(52, doc.ReadColumnChunks().Count);
    }

    [Fact]
    public void A_package_without_the_footprint_is_refused_with_embedded_partMissing()
    {
        byte[] package = Kit.PackageWithoutFootprint();
        using (var stream = new MemoryStream(package))
        {
            Assert.Equal("embedded.partMissing", CodeOf(() => PqaxDocument.OpenEmbedded(stream).Dispose()));
        }

        using TemporaryFile file = Kit.Temporary(package, ".vpax");
        Assert.Equal("embedded.partMissing", CodeOf(() => PqaxDocument.OpenEmbedded(file.Path).Dispose()));
        Assert.Equal("embedded.partMissing",
            CodeOf(() => PqaxDocument.OpenEmbedded(Path.Combine(Kit.Samples, "embedded-stored.vpax"), "Nothing.pqax").Dispose()));
    }

    [Fact]
    public void A_failed_open_of_a_package_by_path_leaves_the_file_closed()
    {
        using TemporaryFile file = Kit.Temporary(Kit.PackageWithoutFootprint(), ".vpax");
        Assert.Throws<ArgumentNullException>(() => PqaxDocument.OpenEmbedded(file.Path, null!));
        Assert.Equal("embedded.partMissing", CodeOf(() => PqaxDocument.OpenEmbedded(file.Path).Dispose()));

        // No handle is left on the file: it can be opened for exclusive access at once.
        using (new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
    }

    [Fact]
    public void A_file_that_is_not_a_zip_archive_is_refused()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not a zip archive"));
        Assert.Equal("container.manifestMissing", CodeOf(() => PqaxDocument.Open(stream).Dispose()));
    }

    [Fact]
    public void An_integer_written_with_a_fraction_of_zero_reads_as_the_integer()
    {
        byte[] data = Kit.Rewrite(Kit.SampleBytes("sample.pqax"), (name, content) =>
            name == "rowGroups.json" ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content).Replace("\"rowCount\":[2048,", "\"rowCount\":[2048.0,")) : content);
        Assert.Contains("2048.0", Encoding.UTF8.GetString(Kit.Entries(data).Single(e => e.Name == "rowGroups.json").Content));
        using var stream = new MemoryStream(data);
        using PqaxDocument doc = PqaxDocument.Open(stream, leaveOpen: true);
        Assert.Equal(2048, doc.ReadRowGroups()[0].RowCount);
        stream.Position = 0;
        Assert.Contains(PqaxValidator.Validate(stream).Issues, issue => issue.Code == "json.integer");
    }

    // ---- the table API ----

    [Fact]
    public void A_table_is_a_list_with_find_by_id()
    {
        using PqaxDocument doc = PqaxDocument.Open(Path.Combine(Kit.Samples, "sample.pqax"));
        PqaxTable<ColumnRow> columns = doc.Summary.Columns;
        Assert.Equal("columns", columns.Name);
        Assert.Equal(10, columns.Count);
        Assert.Equal(10, columns.Rows.Count);
        Assert.Equal(10, columns.Count());
        Assert.Same(columns.Rows[3], columns[3]);
        Assert.Equal("amount", columns.FindById(4)?.Name);
        Assert.Equal("double", columns.FindById((long?)4)?.DataType);
        Assert.Null(columns.FindById(99));
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L, 10L], columns.Select(column => column.Id));
        Assert.Equal("tables", doc.Summary.Tables.Name);
        Assert.Equal("columnChunks", doc.ReadColumnChunks().Name);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 22, 15, 0, TimeSpan.Zero), doc.ReadFiles()[0].ModifiedAt);
        Assert.Equal(TimeSpan.Zero, doc.ReadFiles()[0].ModifiedAt!.Value.Offset);
        Assert.Empty(doc.ReadDeleteFiles());
        Assert.Equal("deleteFiles", doc.ReadDeleteFiles().Name);
    }
}
