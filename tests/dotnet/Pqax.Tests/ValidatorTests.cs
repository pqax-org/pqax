using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pqax.Tests;

/// <summary>The validator against the kit (conformance/README.md, "A validator test", and plan 4.1 and 4.3).</summary>
public class ValidatorTests
{
    public static TheoryData<string> ValidSamples => Kit.Data(Kit.ExpectedNames().Select(name => (string)Kit.Expected(name)["file"]!));

    public static TheoryData<string> InvalidFiles => Kit.Data(Kit.InvalidCases().Select(pair => pair.Key));

    /// <summary>Validates a sample: a VPAX through the footprint inside it, which is the PQAX file.</summary>
    private static ValidationResult ValidateSample(string path) =>
        path.EndsWith(".vpax", StringComparison.Ordinal) ? PqaxValidator.ValidateEmbedded(path, Kit.EmbeddedPart) : PqaxValidator.Validate(path);

    private static HashSet<string> Codes(ValidationResult result) => result.Issues.Select(issue => issue.Code).ToHashSet();

    [Theory]
    [MemberData(nameof(ValidSamples))]
    public void A_valid_sample_has_no_issue(string file)
    {
        ValidationResult result = ValidateSample(Path.Combine(Kit.Samples, file));
        Assert.Empty(result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
        Assert.True(result.IsValid);
        Assert.True(result.ChecksRun > 0);
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void An_invalid_sample_reports_at_least_its_codes(string file)
    {
        ValidationResult result = ValidateSample(Path.Combine(Kit.Invalid, file));
        HashSet<string> reported = Codes(result);
        IEnumerable<string> missing = Kit.InvalidCodes(file).Where(code => !reported.Contains(code));
        Assert.True(!missing.Any(), $"{file}: missing {string.Join(", ", missing)}; reported {string.Join(", ", reported)}");
        Assert.False(result.IsValid);
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void An_invalid_sample_reports_only_codes_of_the_kit(string file)
    {
        HashSet<string> reported = Codes(ValidateSample(Path.Combine(Kit.Invalid, file)));
        Assert.All(reported, code => Assert.Contains(code, Kit.PlanCodes));
    }

    [Fact]
    public void Rules_lists_the_codes_of_the_plan()
    {
        string text = File.ReadAllText(Path.Combine(Kit.Conformance, "rules.md"));
        string section = text.Split("## 1. The codes")[1].Split("\n## ")[0];
        var listed = Regex.Matches(section, @"^\| `([A-Za-z0-9]+\.[A-Za-z0-9]+)` \|", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value).ToHashSet();
        Assert.True(listed.SetEquals(Kit.PlanCodes), string.Join(", ", listed.Except(Kit.PlanCodes).Concat(Kit.PlanCodes.Except(listed))));
    }

    [Fact]
    public void An_issue_has_a_code_a_message_and_its_place()
    {
        ValidationResult result = PqaxValidator.Validate(Path.Combine(Kit.Invalid, "part.rowCount.array.pqax"));
        Assert.NotEmpty(result.Issues);
        Assert.All(result.Issues, issue => Assert.False(string.IsNullOrWhiteSpace(issue.Message)));
        ValidationIssue rowCount = result.Issues.First(issue => issue.Code == "part.rowCount");
        Assert.Equal("commits.json", rowCount.Part);
        Assert.Equal("commits", rowCount.Table);
    }

    [Fact]
    public void A_stream_gives_the_same_issues_as_a_path()
    {
        string path = Path.Combine(Kit.Invalid, "totals.fileSize.pqax");
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        ValidationResult fromStream = PqaxValidator.Validate(stream);
        ValidationResult fromPath = PqaxValidator.Validate(path);
        Assert.Equal(["totals.fileSize"], Codes(fromStream));
        Assert.Equal(Codes(fromPath), Codes(fromStream));
        Assert.Equal(fromPath.ChecksRun, fromStream.ChecksRun);
    }

    [Fact]
    public void Every_issue_is_reported_and_not_only_the_first()
    {
        byte[] data = Kit.Rewrite(Kit.SampleBytes("sample.pqax"), (name, content) =>
        {
            JsonNode json;
            switch (name)
            {
                case "summary.json":
                    json = Kit.Json(content);
                    json["tables"]!["fileCount"]![0] = (long)json["tables"]!["fileCount"]![0]! + 1;
                    return Kit.Bytes(json);
                case "files.json":
                    json = Kit.Json(content);
                    json["files"]!["modifiedAt"]![0] = "2023-11-14T22:15:00Z";
                    return Kit.Bytes(json);
                case "commits.json":
                    json = Kit.Json(content);
                    json["commits"]!["commitId"]![2] = null;
                    return Kit.Bytes(json);
                default:
                    return content;
            }
        });
        using var stream = new MemoryStream(data);
        HashSet<string> reported = Codes(PqaxValidator.Validate(stream));
        Assert.Superset(new HashSet<string> { "totals.fileCount", "json.timestamp", "table.required" }, reported);
    }

    [Fact]
    public void A_valid_sample_written_by_another_zip_writer_stays_valid()
    {
        byte[] data = Kit.Rewrite(Kit.SampleBytes("sample.pqax"), (_, content) => content);
        using var stream = new MemoryStream(data);
        ValidationResult result = PqaxValidator.Validate(stream);
        Assert.Empty(result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
    }

    [Fact]
    public void A_byte_order_mark_is_reported_and_the_part_is_read_on()
    {
        HashSet<string> reported = Codes(PqaxValidator.Validate(Path.Combine(Kit.Invalid, "json.bom.pqax")));
        Assert.Contains("json.bom", reported);
        Assert.DoesNotContain("json.invalid", reported);
    }

    [Fact]
    public void A_file_that_is_not_a_zip_archive_is_an_issue_and_not_an_exception()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not a zip archive"));
        ValidationResult result = PqaxValidator.Validate(stream);
        Assert.False(result.IsValid);
        Assert.Contains("container.manifestMissing", Codes(result));
    }

    [Fact]
    public void A_package_given_to_validate_has_no_manifest()
    {
        Assert.Contains("container.manifestMissing", Codes(PqaxValidator.Validate(Path.Combine(Kit.Samples, "embedded-stored.vpax"))));
    }

    [Fact]
    public void A_package_without_the_footprint_reports_embedded_partMissing()
    {
        using var stream = new MemoryStream(Kit.PackageWithoutFootprint());
        ValidationResult result = PqaxValidator.ValidateEmbedded(stream, Kit.EmbeddedPart);
        Assert.False(result.IsValid);
        Assert.Contains("embedded.partMissing", Codes(result));
        using TemporaryFile file = Kit.Temporary(Kit.PackageWithoutFootprint(), ".vpax");
        Assert.Contains("embedded.partMissing", Codes(PqaxValidator.ValidateEmbedded(file.Path)));
    }

    [Fact]
    public void A_listed_summary_that_is_missing_is_reported()
    {
        byte[] data = Kit.Rewrite(Kit.SampleBytes("sample.pqax"), (name, content) => name == "summary.json" ? null : content);
        using var stream = new MemoryStream(data);
        Assert.Contains("manifest.partMissing", Codes(PqaxValidator.Validate(stream)));
    }

    [Theory]
    [InlineData("json.type.pqax")]
    [InlineData("table.required.missing.pqax")]
    [InlineData("manifest.firstPart.pqax")]
    [InlineData("part.singleTable.pqax")]
    public void The_schema_check_of_the_tests_rejects_what_the_schema_can_express(string file)
    {
        Assert.NotEmpty(SchemaCheck.Errors(Kit.InvalidBytes(file)));
        Assert.Empty(SchemaCheck.Errors(Kit.SampleBytes("sample.pqax")));
    }

    [Theory]
    [InlineData("embedded-stored.vpax")]
    [InlineData("embedded-deflated.vpax")]
    public void An_embedded_footprint_is_validated_from_a_stream(string file)
    {
        using var stream = new MemoryStream(Kit.SampleBytes(file));
        ValidationResult result = PqaxValidator.ValidateEmbedded(stream);
        Assert.True(result.IsValid, string.Join(", ", result.Issues.Select(issue => issue.Code)));
    }

    // ---- the fields of format 0.2 ----

    [Theory]
    [InlineData("sample.pqax", "0.2")]
    [InlineData("format-0.1.pqax", "0.1")]
    public void A_sample_of_format_0_2_and_one_of_format_0_1_are_valid(string file, string version)
    {
        string path = Path.Combine(Kit.Samples, file);
        using (PqaxDocument doc = PqaxDocument.Open(path))
        {
            Assert.Equal(version, doc.Manifest.FormatVersion);
        }

        ValidationResult result = PqaxValidator.Validate(path);
        Assert.Empty(result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("json.type.unreferencedFileCount.pqax")]
    [InlineData("json.timestamp.nonLiveOldestRemovedAt.pqax")]
    [InlineData("json.timestamp.partitionLastModifiedAt.pqax")]
    public void The_invalid_samples_of_the_fields_of_format_0_2_report_their_codes(string file)
    {
        Assert.Contains(file, Kit.InvalidCases().Select(pair => pair.Key));
        HashSet<string> reported = Codes(PqaxValidator.Validate(Path.Combine(Kit.Invalid, file)));
        Assert.All(Kit.InvalidCodes(file), code => Assert.Contains(code, reported));
    }

    [Theory]
    [InlineData("summary.json", "tables", "unreferencedFileCount", "\"1\"", "json.type")]
    [InlineData("summary.json", "tables", "unreferencedFileCount", "true", "json.type")]
    [InlineData("summary.json", "tables", "unreferencedFileSize", "\"737\"", "json.type")]
    [InlineData("summary.json", "tables", "unreferencedFileSize", "[737]", "json.type")]
    [InlineData("summary.json", "tables", "nonLiveOldestRemovedAt", "1700000050000", "json.type")]
    [InlineData("summary.json", "tables", "nonLiveNewestRemovedAt", "{\"at\":\"2023-11-14T22:14:10.000Z\"}", "json.type")]
    [InlineData("partitions.json", "partitions", "lastModifiedAt", "1700000100000", "json.type")]
    [InlineData("summary.json", "tables", "nonLiveOldestRemovedAt", "\"2023-11-14T22:14:10Z\"", "json.timestamp")]
    [InlineData("summary.json", "tables", "nonLiveNewestRemovedAt", "\"2023-11-14 22:14:10.000\"", "json.timestamp")]
    [InlineData("partitions.json", "partitions", "lastModifiedAt", "\"2023-11-14T22:15:00.000+00:00\"", "json.timestamp")]
    public void A_wrong_value_in_a_field_of_format_0_2_is_reported_with_its_table_and_field(string part, string table, string field, string value, string code)
    {
        byte[] data = Kit.Rewrite(Kit.SampleBytes("sample.pqax"), (name, content) =>
        {
            if (name != part)
            {
                return content;
            }

            JsonNode json = Kit.Json(content);
            json[table]![field]![0] = JsonNode.Parse(value);
            return Kit.Bytes(json);
        });
        using var stream = new MemoryStream(data);
        ValidationResult result = PqaxValidator.Validate(stream);
        Assert.False(result.IsValid);
        List<ValidationIssue> matching = result.Issues.Where(issue => issue.Code == code).ToList();
        Assert.True(matching.Count > 0, string.Join(", ", result.Issues.Select(issue => issue.Code)));
        Assert.True(matching.Any(issue => issue.Table == table && issue.Field == field && issue.Part == part),
            string.Join("; ", matching.Select(issue => $"{issue.Part} {issue.Table} {issue.Field}")));
    }
}
