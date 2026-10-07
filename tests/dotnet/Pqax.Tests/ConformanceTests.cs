using System.Text.Json.Nodes;

namespace Pqax.Tests;

/// <summary>Every valid sample of the kit gives its expected results (conformance/README.md, "A reader test").</summary>
public class ConformanceTests
{
    public static TheoryData<string> Samples => Kit.Data(Kit.ExpectedNames());

    [Theory]
    [MemberData(nameof(Samples))]
    public void Format_version_and_parts_are_those_of_the_manifest(string name)
    {
        JsonObject expected = Kit.Expected(name);
        using PqaxDocument doc = Kit.OpenSample(expected);
        Assert.Equal((string)expected["format"]!, doc.Manifest.Format);
        Assert.Equal((string)expected["formatVersion"]!, doc.Manifest.FormatVersion);
        Assert.Equal(expected["parts"]!.AsArray().Select(part => (string)part!), doc.Manifest.Parts.Select(part => part.Name));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void The_embedded_part_and_its_compression_are_stated(string name)
    {
        JsonObject expected = Kit.Expected(name);
        using PqaxDocument doc = Kit.OpenSample(expected);
        Assert.Equal((string?)expected["embeddedPart"], doc.EmbeddedPart);
        Assert.Equal((string?)expected["embeddedCompression"], doc.EmbeddedCompression);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_table_has_its_rows(string name)
    {
        JsonObject expected = Kit.Expected(name);
        using PqaxDocument doc = Kit.OpenSample(expected);
        JsonObject rows = expected["rows"]!.AsObject();
        foreach ((string table, JsonNode? value) in rows)
        {
            int count = (int)value!;
            Assert.True(doc.RowCount(table) == count, $"RowCount({table}) is {doc.RowCount(table)}, expected {count}");
            if (Kit.FormatTables.Contains(table))
            {
                (int typed, IReadOnlyDictionary<string, IReadOnlyList<System.Text.Json.JsonElement?>> extra) = Kit.Typed(doc, table);
                Assert.True(typed == count, $"{table} has {typed} rows, expected {count}");
                foreach ((string field, IReadOnlyList<System.Text.Json.JsonElement?> values) in extra)
                {
                    Assert.True(values.Count == count, $"{table}.{field} has {values.Count} values");
                }
            }

            RawTable raw = doc.ReadRaw(table);
            Assert.True(raw.Count == count, $"ReadRaw({table}).Count is {raw.Count}, expected {count}");
            Assert.NotEmpty(raw.Fields);
            foreach ((string field, IReadOnlyList<System.Text.Json.JsonElement?> values) in raw.Fields)
            {
                Assert.True(values.Count == count, $"ReadRaw({table}).{field} has {values.Count} values, expected {count}");
            }
        }

        foreach (string table in Kit.FormatTables.Where(table => !rows.ContainsKey(table)))
        {
            Assert.True(doc.RowCount(table) == 0, $"RowCount({table}) of a table that the file does not have");
            Assert.True(Kit.Typed(doc, table).Count == 0, $"{table} has rows and the file has none");
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Totals_joins_and_largest_columns_are_the_expected_ones(string name)
    {
        JsonObject expected = Kit.Expected(name);
        using PqaxDocument doc = Kit.OpenSample(expected);
        Assert.Empty(Kit.Differences(Kit.Shape(doc), expected, Kit.ShapeKeys));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void A_stream_gives_the_same_results_as_a_path(string name)
    {
        JsonObject expected = Kit.Expected(name);
        using var stream = new MemoryStream(File.ReadAllBytes(Kit.SamplePath(expected)));
        string? part = Kit.ExpectedPart(expected);
        using PqaxDocument doc = part is null ? PqaxDocument.Open(stream, leaveOpen: true) : PqaxDocument.OpenEmbedded(stream, part, leaveOpen: true);
        Assert.Empty(Kit.Differences(Kit.Shape(doc), expected, Kit.ShapeKeys));
        Assert.Equal((string?)expected["embeddedCompression"], doc.EmbeddedCompression);
    }
}
