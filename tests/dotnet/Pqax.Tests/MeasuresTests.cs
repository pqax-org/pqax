using System.Globalization;
using System.Text.Json.Nodes;

namespace Pqax.Tests;

/// <summary>
/// The derived measures, <see cref="StorageMeasures"/>: every valid sample of the kit against
/// <c>conformance/expected/measures</c>, which the views compute, and each function on synthetic values, as
/// <c>src/typescript/views/src/model.ts</c> and the README of the views define them.
/// </summary>
public class MeasuresTests
{
    /// <summary>
    /// The relative tolerance of a float. 0: the measures must equal the views to the bit, since both compute the same
    /// operations on doubles in the same order.
    /// </summary>
    private const double Tolerance = 0;

    private static string MeasuresFolder => Path.Combine(Kit.ExpectedFolder, "measures");

    public static TheoryData<string> Samples => Kit.Data(Kit.ExpectedNames());

    // ---- the shape of the expected files ----

    private static JsonNode? Number(double? value) => value is double number ? JsonValue.Create(number) : null;

    private static JsonNode? Number(long? value) => value is long number ? JsonValue.Create(number) : null;

    /// <summary>A table in the JSON shape of the interface, from the public members of the C# objects.</summary>
    public static JsonObject ToJson(TableStorage table) => new()
    {
        ["id"] = Number(table.Id),
        ["name"] = table.Name,
        ["label"] = table.Label,
        ["rowCount"] = Number(table.RowCount),
        ["fileCount"] = Number(table.FileCount),
        ["fileSize"] = Number(table.FileSize),
        ["stack"] = new JsonObject
        {
            ["live"] = Number(table.Stack.Live),
            ["nonLive"] = Number(table.Stack.NonLive),
            ["unreferenced"] = Number(table.Stack.Unreferenced),
            ["deleted"] = Number(table.Stack.Deleted),
            ["stale"] = Number(table.Stack.Stale),
            ["metadata"] = Number(table.Stack.Metadata),
            ["footers"] = Number(table.Stack.Footers),
            ["deletedSource"] = table.Stack.DeletedSource,
            ["listed"] = table.Stack.Listed,
            ["data"] = table.Stack.Data,
            ["total"] = table.Stack.Total,
            ["waste"] = table.Stack.Waste,
            ["overhead"] = table.Stack.Overhead,
            ["wasteShare"] = Number(table.Stack.WasteShare),
            ["overheadShare"] = Number(table.Stack.OverheadShare),
            ["shares"] = new JsonObject(StorageMeasures.StackComponents.Select(key => KeyValuePair.Create(key, Number(table.Stack.Share(key))))),
        },
        ["smallFiles"] = Small(table.SmallFiles),
        ["tinyFiles"] = Small(table.TinyFiles),
        ["files"] = new JsonObject
        {
            ["count"] = Number(table.Files.Count),
            ["sum"] = Number(table.Files.Sum),
            ["minimum"] = Number(table.Files.Minimum),
            ["firstQuartile"] = Number(table.Files.FirstQuartile),
            ["median"] = Number(table.Files.Median),
            ["thirdQuartile"] = Number(table.Files.ThirdQuartile),
            ["maximum"] = Number(table.Files.Maximum),
            ["vOrder"] = table.Files.VOrderShare is null ? null : new JsonObject
            {
                ["share"] = Number(table.Files.VOrderShare),
                ["bytesShare"] = Number(table.Files.VOrderBytesShare),
            },
        },
        ["measures"] = new JsonObject
        {
            ["compressionRatio"] = Number(table.Measures.CompressionRatio),
            ["deletedRowShare"] = Number(table.Measures.DeletedRowShare),
            ["averageFileSize"] = Number(table.Measures.AverageFileSize),
            ["averageRowGroupRows"] = Number(table.Measures.AverageRowGroupRows),
            ["footerCoverage"] = Number(table.Measures.FooterCoverage),
            ["staleColumnShare"] = Number(table.Measures.StaleColumnShare),
            ["nonLiveShare"] = Number(table.Measures.NonLiveShare),
            ["averagePartitionSize"] = Number(table.Measures.AveragePartitionSize),
            ["dictionaryFallbackShare"] = Number(table.Measures.DictionaryFallbackShare),
        },
        ["maintenance"] = new JsonObject
        {
            ["lastCommitAt"] = Time(table.Maintenance.LastCommitAt),
            ["commitsPerDay"] = Number(table.Maintenance.CommitsPerDay),
            ["daysSinceLastCommit"] = Number(table.Maintenance.DaysSinceLastCommit),
            ["hasVacuum"] = table.Maintenance.HasVacuum,
            ["lastVacuumAt"] = Time(table.Maintenance.LastVacuumAt),
            ["hasOptimize"] = table.Maintenance.HasOptimize,
            ["lastOptimizeAt"] = Time(table.Maintenance.LastOptimizeAt),
        },
        ["partitions"] = new JsonObject
        {
            ["count"] = Number(table.Partitions.Count),
            ["kept"] = table.Partitions.Kept,
            ["oldestModifiedAt"] = Time(table.Partitions.OldestModifiedAt),
            ["newestModifiedAt"] = Time(table.Partitions.NewestModifiedAt),
        },
    };

    /// <summary>A time as the expected files write it: UTC with milliseconds.</summary>
    private static string? Time(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A column in the JSON shape of the interface, from the public members of the C# object.</summary>
    public static JsonObject ToJson(ColumnStorage column) => new()
    {
        ["id"] = Number(column.Id),
        ["table"] = Number(column.Table),
        ["name"] = column.Name,
        ["label"] = column.Label,
        ["nullShare"] = Number(column.NullShare),
        ["compressionRatio"] = Number(column.CompressionRatio),
        ["dictionaryShare"] = Number(column.DictionaryShare),
        ["dictionaryFallbackShare"] = Number(column.DictionaryFallbackShare),
        ["averageValueLength"] = Number(column.AverageValueLength),
        ["filesWithoutColumnShare"] = Number(column.FilesWithoutColumnShare),
    };

    /// <summary>The collection in the JSON shape of the interface, from the public members of the C# object.</summary>
    public static JsonObject ToJson(CollectionMeasures collection) => new()
    {
        ["totals"] = new JsonObject
        {
            ["sources"] = collection.Sources,
            ["tables"] = collection.Tables,
            ["columns"] = collection.Columns,
            ["files"] = Number(collection.Files),
            ["fileSize"] = Number(collection.FileSize),
            ["rowCount"] = Number(collection.RowCount),
            ["footprint"] = Number(collection.Footprint),
            ["waste"] = Number(collection.Waste),
            ["overhead"] = Number(collection.Overhead),
        },
        ["depths"] = new JsonObject(collection.Depths.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value)))),
    };

    private static JsonObject Small(SmallFiles files) => new()
    {
        ["threshold"] = files.Threshold,
        ["count"] = Number(files.Count),
        ["shareAtLeast"] = Number(files.ShareAtLeast),
    };

    /// <summary>
    /// The differences between two JSON values: numbers as doubles, exactly or within a relative tolerance, a key
    /// that ends in At (lastCommitAt, lastVacuumAt, oldestModifiedAt, ...) as an instant, and every member of both objects.
    /// </summary>
    public static void Compare(JsonNode? actual, JsonNode? expected, string path, double tolerance, List<string> differences)
    {
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
            {
                differences.Add($"{path}: got {actual?.ToJsonString() ?? "null"}, expected {expected?.ToJsonString() ?? "null"}");
            }

            return;
        }

        switch (expected)
        {
            case JsonObject expectedObject:
                if (actual is not JsonObject actualObject)
                {
                    differences.Add($"{path}: got {actual.ToJsonString()}, expected an object");
                    return;
                }

                foreach (string key in expectedObject.Select(pair => pair.Key).Union(actualObject.Select(pair => pair.Key)))
                {
                    if (!expectedObject.ContainsKey(key) || !actualObject.ContainsKey(key))
                    {
                        differences.Add($"{path}.{key}: present in only one of the two");
                        continue;
                    }

                    Compare(actualObject[key], expectedObject[key], path + "." + key, tolerance, differences);
                }

                return;
            case JsonArray expectedArray:
                if (actual is not JsonArray actualArray || actualArray.Count != expectedArray.Count)
                {
                    differences.Add($"{path}: got {actual.ToJsonString()}, expected {expected.ToJsonString()}");
                    return;
                }

                for (int i = 0; i < expectedArray.Count; i++)
                {
                    Compare(actualArray[i], expectedArray[i], $"{path}[{i}]", tolerance, differences);
                }

                return;
        }

        JsonValue expectedValue = expected.AsValue();
        if (actual is not JsonValue actualValue)
        {
            differences.Add($"{path}: got {actual.ToJsonString()}, expected {expected.ToJsonString()}");
            return;
        }

        if (path[(path.LastIndexOf('.') + 1)..].EndsWith("At", StringComparison.Ordinal)
            && expectedValue.GetValueKind() == System.Text.Json.JsonValueKind.String && actualValue.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            DateTimeOffset a = DateTimeOffset.Parse((string)actualValue!, CultureInfo.InvariantCulture);
            DateTimeOffset e = DateTimeOffset.Parse((string)expectedValue!, CultureInfo.InvariantCulture);
            if (a != e)
            {
                differences.Add($"{path}: got {a:O}, expected {e:O}");
            }

            return;
        }

        if (expectedValue.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            if (actualValue.GetValueKind() != System.Text.Json.JsonValueKind.Number)
            {
                differences.Add($"{path}: got {actual.ToJsonString()}, expected {expected.ToJsonString()}");
                return;
            }

            // Through the text: a value built from a long does not convert to a double in place.
            double a = double.Parse(actualValue.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
            double e = double.Parse(expectedValue.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
            bool equal = tolerance == 0 ? a == e : Math.Abs(a - e) <= tolerance * Math.Max(Math.Abs(a), Math.Abs(e));
            if (!equal)
            {
                differences.Add($"{path}: got {a.ToString("R", CultureInfo.InvariantCulture)}, expected {e.ToString("R", CultureInfo.InvariantCulture)}");
            }

            return;
        }

        if (actual.ToJsonString() != expected.ToJsonString())
        {
            differences.Add($"{path}: got {actual.ToJsonString()}, expected {expected.ToJsonString()}");
        }
    }

    // ---- the kit ----

    [Theory]
    [MemberData(nameof(Samples))]
    public void The_measures_of_a_sample_are_those_of_the_views(string name)
    {
        string path = Path.Combine(MeasuresFolder, name + ".json");
        Assert.True(File.Exists(path), $"{path} is missing");
        JsonObject expected = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        JsonObject sample = Kit.Expected(name);
        Assert.Equal((string)sample["file"]!, (string)expected["file"]!);
        Assert.Equal((string?)sample["embeddedPart"], (string?)expected["embeddedPart"]);
        Assert.Equal(StorageMeasures.DefaultSmallFileSize, (long)expected["smallFileSize"]!);
        Assert.Equal(StorageMeasures.DefaultTinyFileSize, (long)expected["tinyFileSize"]!);

        using PqaxDocument doc = Kit.OpenSample(name);
        FootprintMeasures measures = StorageMeasures.Compute(doc);
        Assert.Equal((bool)expected["filesRead"]!, measures.FilesRead);
        Assert.Equal(expected["warnings"]!.AsArray().Count, measures.Warnings.Count);

        var differences = new List<string>();
        Compare(new JsonArray(measures.Tables.Select(table => (JsonNode?)ToJson(table)).ToArray()), expected["tables"], "tables", Tolerance, differences);
        Compare(new JsonArray(measures.Columns.Select(column => (JsonNode?)ToJson(column)).ToArray()), expected["columns"], "columns", Tolerance, differences);
        Compare(ToJson(measures.Collection), expected["collection"], "collection", Tolerance, differences);
        Assert.True(differences.Count == 0, string.Join("\n", differences));
        Assert.Equal(Enumerable.Range(0, measures.Tables.Count), measures.Tables.Select(table => table.Row));
    }

    [Fact]
    public void Every_valid_sample_has_its_expected_measures()
    {
        Assert.True(Directory.Exists(MeasuresFolder), $"{MeasuresFolder} is missing");
        Assert.Equal(Kit.ExpectedNames(), Directory.GetFiles(MeasuresFolder, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Compute_with_other_thresholds_counts_the_files_below_them()
    {
        using PqaxDocument doc = Kit.OpenSample("sample");
        FootprintMeasures measures = StorageMeasures.Compute(doc, new MeasureOptions { SmallFileSize = 40000, TinyFileSize = 2000 });
        // The files of sales are 47820, 83049, and 28252 bytes, those of orders 38103 and 1781.
        Assert.Equal(40000, measures.Tables[0].SmallFiles.Threshold);
        Assert.Equal(1, measures.Tables[0].SmallFiles.Count);
        Assert.Equal(1.0 / 3, measures.Tables[0].SmallFiles.ShareAtLeast);
        Assert.Equal(2, measures.Tables[1].SmallFiles.Count);
        Assert.Equal(1.0, measures.Tables[1].SmallFiles.ShareAtLeast);
        Assert.Equal(2000, measures.Tables[1].TinyFiles.Threshold);
        Assert.Equal(1, measures.Tables[1].TinyFiles.Count);
        Assert.Equal(0.5, measures.Tables[1].TinyFiles.ShareAtLeast);
        Assert.Equal(0, measures.Tables[0].TinyFiles.Count);
        Assert.Equal(0.0, measures.Tables[0].TinyFiles.ShareAtLeast);
    }

    [Fact]
    public void Compute_on_a_summary_gives_a_lower_bound_from_the_quartiles()
    {
        using PqaxDocument doc = Kit.OpenSample("summary-only");
        FootprintMeasures measures = StorageMeasures.Compute(doc, new MeasureOptions { SmallFileSize = 40000 });
        Assert.False(measures.FilesRead);
        // sales: minimum and first quartile 28252, median 47820. orders: maximum 38103.
        Assert.Null(measures.Tables[0].SmallFiles.Count);
        Assert.Equal(0.25, measures.Tables[0].SmallFiles.ShareAtLeast);
        Assert.Null(measures.Tables[1].SmallFiles.Count);
        Assert.Equal(1.0, measures.Tables[1].SmallFiles.ShareAtLeast);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_threshold_that_is_not_positive_takes_the_default(long value)
    {
        using PqaxDocument doc = Kit.OpenSample("sample");
        FootprintMeasures measures = StorageMeasures.Compute(doc, new MeasureOptions { SmallFileSize = value, TinyFileSize = value });
        Assert.All(measures.Tables, table =>
        {
            Assert.Equal(StorageMeasures.DefaultSmallFileSize, table.SmallFiles.Threshold);
            Assert.Equal(StorageMeasures.DefaultTinyFileSize, table.TinyFiles.Threshold);
        });
    }

    [Fact]
    public void The_constants_are_those_of_the_views()
    {
        Assert.Equal(134217728, StorageMeasures.DefaultSmallFileSize);
        Assert.Equal(8388608, StorageMeasures.DefaultTinyFileSize);
        Assert.Equal(["live", "nonLive", "unreferenced", "deleted", "stale", "metadata", "footers"], StorageMeasures.StackComponents);
        Assert.Equal(["nonLive", "unreferenced", "deleted", "stale"], StorageMeasures.WasteComponents);
        Assert.Equal(["metadata", "footers"], StorageMeasures.OverheadComponents);
    }

    // ---- one function each ----

    [Theory]
    [InlineData(1.0, 2.0, 0.5)]
    [InlineData(0.0, 5.0, 0.0)]
    [InlineData(1.0, 0.0, null)]
    [InlineData(3.0, -1.0, null)]
    [InlineData(null, 2.0, null)]
    [InlineData(1.0, null, null)]
    public void Ratio_is_null_without_a_positive_denominator(double? a, double? b, double? expected)
    {
        Assert.Equal(expected, StorageMeasures.Ratio(a, b));
    }

    [Theory]
    [InlineData(100.0, 0.0, null, 0.0)]
    [InlineData(null, 0.0, null, 0.0)]
    [InlineData(100.0, 25.0, 100.0, 25.0)]
    [InlineData(100.0, 200.0, 100.0, 100.0)]
    [InlineData(null, 5.0, 10.0, null)]
    [InlineData(100.0, null, 10.0, null)]
    [InlineData(100.0, 5.0, null, null)]
    [InlineData(100.0, 5.0, 0.0, null)]
    [InlineData(100.0, 5.0, -1.0, null)]
    public void DeletedEstimate_takes_the_share_of_the_rows(double? fileSize, double? deletedRowCount, double? rowCount, double? expected)
    {
        Assert.Equal(expected, StorageMeasures.DeletedEstimate(fileSize, deletedRowCount, rowCount));
    }

    [Fact]
    public void DeletedEstimate_multiplies_before_it_divides()
    {
        // fileSize * min(deleted, rows) / rows, in this order, as the views compute it.
        Assert.Equal(159121.0 * 25 / 17000, StorageMeasures.DeletedEstimate(159121, 25, 17000));
        Assert.Equal(47820.0 * 25 / 5000, StorageMeasures.DeletedEstimate(47820, 25, 5000));
    }

    public static TheoryData<double?, double?, double?, double?, double?, double?, double, double?> Distributions => new()
    {
        // count, minimum, firstQuartile, median, thirdQuartile, maximum, threshold, expected
        { 8, 10, 20, 30, 40, 50, 51, 1.0 },
        { 8, 10, 20, 30, 40, 50, 50, 0.75 },
        { 8, 10, 20, 30, 40, 50, 41, 0.75 },
        { 8, 10, 20, 30, 40, 50, 31, 0.5 },
        { 8, 10, 20, 30, 40, 50, 21, 0.25 },
        { 8, 10, 20, 30, 40, 50, 11, 0.125 },
        { null, 10, 20, 30, 40, 50, 11, 0.0 },
        { 0, 10, 20, 30, 40, 50, 11, 0.0 },
        { 8, 10, 20, 30, 40, 50, 10, 0.0 },
        { 8, 10, 20, 30, 40, 50, 5, 0.0 },
        { 8, null, null, null, null, null, 5, null },
        { null, null, null, null, null, null, 5, null },
        { 8, null, null, null, null, 50, 60, 1.0 },
        { 8, null, null, null, null, 50, 40, 0.0 },
        { 8, null, null, 30, null, null, 40, 0.5 },
        { 4, 10, null, null, null, null, 40, 0.25 },
    };

    [Theory]
    [MemberData(nameof(Distributions))]
    public void SmallShareAtLeast_is_a_lower_bound_from_the_quartiles(double? count, double? minimum, double? firstQuartile, double? median, double? thirdQuartile, double? maximum, double threshold, double? expected)
    {
        Assert.Equal(expected, StorageMeasures.SmallShareAtLeast(count, minimum, firstQuartile, median, thirdQuartile, maximum, threshold));
    }

    private static readonly DateTimeOffset T0 = new(2023, 11, 14, 22, 14, 50, TimeSpan.Zero);

    [Fact]
    public void CommitsPerDay_divides_by_the_days_of_the_span_at_least_one()
    {
        Assert.Equal(4.0, StorageMeasures.CommitsPerDay(4, T0, T0.AddDays(1)));
        Assert.Equal(2.0, StorageMeasures.CommitsPerDay(4, T0, T0.AddDays(2)));
        Assert.Equal(4.0, StorageMeasures.CommitsPerDay(6, T0, T0.AddHours(36)));
        Assert.Equal(4.0, StorageMeasures.CommitsPerDay(4, T0, T0.AddMinutes(30)));
        Assert.Equal(4.0, StorageMeasures.CommitsPerDay(4, T0, T0.AddDays(-3)));
        Assert.Equal(0.0, StorageMeasures.CommitsPerDay(0, T0, T0.AddDays(3)));
        Assert.Equal(7.0 / (100.0 / 24), StorageMeasures.CommitsPerDay(7, T0, T0.AddHours(100)));
        Assert.Null(StorageMeasures.CommitsPerDay(null, T0, T0.AddDays(1)));
        Assert.Null(StorageMeasures.CommitsPerDay(4, null, T0));
        Assert.Null(StorageMeasures.CommitsPerDay(4, T0, null));
    }

    [Fact]
    public void DaysBetween_counts_whole_days_and_never_less_than_0()
    {
        Assert.Equal(1055L, StorageMeasures.DaysBetween(T0, T0.AddDays(1055).AddHours(5)));
        Assert.Equal(0L, StorageMeasures.DaysBetween(T0, T0.AddHours(23).AddMinutes(59)));
        Assert.Equal(1L, StorageMeasures.DaysBetween(T0, T0.AddDays(1)));
        Assert.Equal(0L, StorageMeasures.DaysBetween(T0, T0.AddDays(-3)));
        Assert.Equal(0L, StorageMeasures.DaysBetween(T0, new DateTimeOffset(T0.UtcDateTime.AddHours(2)).ToOffset(TimeSpan.FromHours(-5))));
        Assert.Null(StorageMeasures.DaysBetween(null, T0));
        Assert.Null(StorageMeasures.DaysBetween(T0, null));
    }

    private static StorageStack Stack(double? fileSize = null, double? deleted = null, double? footerSize = null, double? staleColumnSize = null,
        double? nonLiveFileSize = null, double? unreferencedFileSize = null, double? metadataSize = null, string? deletedSource = null) =>
        StorageMeasures.StackOf(new StackInput
        {
            FileSize = fileSize,
            Deleted = deleted,
            FooterSize = footerSize,
            StaleColumnSize = staleColumnSize,
            NonLiveFileSize = nonLiveFileSize,
            UnreferencedFileSize = unreferencedFileSize,
            MetadataSize = metadataSize,
            DeletedSource = deletedSource,
        });

    [Fact]
    public void StackOf_draws_the_overlap_of_non_live_and_unreferenced_once()
    {
        StorageStack stack = Stack(1000, 100, 50, 20, 300, 200, 30, "files");
        Assert.Equal(830.0, stack.Live);
        Assert.Equal(200.0, stack.NonLive);
        Assert.Equal(0.0, stack.Unreferenced);
        Assert.Equal(100.0, stack.Deleted);
        Assert.Equal(20.0, stack.Stale);
        Assert.Equal(30.0, stack.Metadata);
        Assert.Equal(50.0, stack.Footers);
        Assert.Equal("files", stack.DeletedSource);
        Assert.True(stack.Listed);
        Assert.Equal(320.0, stack.Waste);
        Assert.Equal(80.0, stack.Overhead);
        Assert.Equal(1150.0, stack.Data);
        Assert.Equal(1230.0, stack.Total);
        Assert.Equal(320.0 / 1150, stack.WasteShare);
        Assert.Equal(80.0 / 1150, stack.OverheadShare);

        StorageStack more = Stack(1000, 0, 0, 0, 100, 250, 0);
        Assert.Equal(100.0, more.NonLive);
        Assert.Equal(150.0, more.Unreferenced);
        Assert.Equal(250.0, more.Waste);
        // The total is fileSize + metadataSize + unreferencedFileSize when nothing is clipped.
        Assert.Equal(1000.0 + 0 + 250, more.Total);
    }

    [Fact]
    public void StackOf_without_non_live_files_puts_every_unreferenced_byte_in_unreferenced()
    {
        StorageStack stack = Stack(1000, unreferencedFileSize: 250);
        Assert.Null(stack.NonLive);
        Assert.Equal(250.0, stack.Unreferenced);
        Assert.True(stack.Listed);
    }

    [Fact]
    public void StackOf_without_unreferenced_files_keeps_the_upper_bound()
    {
        StorageStack stack = Stack(1000, 0, 10, 0, 300, null, 30);
        Assert.Equal(300.0, stack.NonLive);
        Assert.Null(stack.Unreferenced);
        Assert.False(stack.Listed);
        Assert.Equal(300.0, stack.Waste);
        // The total is fileSize + metadataSize + nonLiveFileSize, format 0.1.
        Assert.Equal(1000.0 + 30 + 300, stack.Total);
    }

    [Fact]
    public void StackOf_clips_the_live_bytes_at_0()
    {
        StorageStack stack = Stack(100, 80, 50, 10);
        Assert.Equal(0.0, stack.Live);
        Assert.Equal(90.0, stack.Waste);
        Assert.Equal(90.0, stack.Data);
        Assert.Equal(140.0, stack.Total);
    }

    [Fact]
    public void StackOf_has_null_shares_when_the_data_is_0()
    {
        StorageStack stack = Stack(0, 0, 0, 0, 0, 0, 30);
        Assert.Equal(0.0, stack.Data);
        Assert.Equal(30.0, stack.Total);
        Assert.Equal(0.0, stack.Waste);
        Assert.Null(stack.WasteShare);
        Assert.Null(stack.OverheadShare);
    }

    [Fact]
    public void StackOf_counts_a_missing_component_as_0_and_keeps_it_null()
    {
        StorageStack stack = Stack();
        Assert.All(StorageMeasures.StackComponents, key => Assert.Null(stack.Component(key)));
        Assert.Null(stack.Live);
        Assert.Null(stack.DeletedSource);
        Assert.False(stack.Listed);
        // Section 10.2: with every component absent, the sums and the data are null.
        Assert.Null(stack.Data);
        Assert.Null(stack.Total);
        Assert.Null(stack.Waste);
        Assert.Null(stack.Overhead);
        Assert.Null(stack.WasteShare);

        StorageStack noFileSize = Stack(null, 10, 5, 2, 7, null, 3);
        Assert.Null(noFileSize.Live);
        Assert.Equal(19.0, noFileSize.Waste);
        Assert.Null(noFileSize.Data);
        Assert.Equal(27.0, noFileSize.Total);
        Assert.Null(noFileSize.WasteShare);
    }

    [Fact]
    public void StackOf_keeps_the_source_of_the_deleted_rows_only_with_the_deleted_rows()
    {
        Assert.Equal("table", Stack(100, 5, deletedSource: "table").DeletedSource);
        Assert.Null(Stack(100, null, deletedSource: "table").DeletedSource);
    }

    [Fact]
    public void Component_gives_each_component_by_its_name()
    {
        StorageStack stack = Stack(1000, 100, 50, 20, 300, 200, 30);
        Assert.Equal(stack.Live, stack.Component("live"));
        Assert.Equal(stack.NonLive, stack.Component("nonLive"));
        Assert.Equal(stack.Unreferenced, stack.Component("unreferenced"));
        Assert.Equal(stack.Deleted, stack.Component("deleted"));
        Assert.Equal(stack.Stale, stack.Component("stale"));
        Assert.Equal(stack.Metadata, stack.Component("metadata"));
        Assert.Equal(stack.Footers, stack.Component("footers"));
        Assert.Equal(stack.Waste, StorageMeasures.WasteComponents.Sum(key => stack.Component(key) ?? 0));
        Assert.Equal(stack.Overhead, StorageMeasures.OverheadComponents.Sum(key => stack.Component(key) ?? 0));
    }

    // ---- ForTables on synthetic tables ----

    /// <summary>A synthetic footprint: the manifest and summary.json, with the rows given as objects, opened by the reader.</summary>
    private static PqaxDocument Footprint(JsonObject[] tables, JsonObject[]? operations = null, JsonObject[]? columns = null, string formatVersion = "0.2", JsonObject? collection = null)
    {
        var summary = new JsonObject { ["tables"] = Columnar(tables) };
        var counts = new JsonObject { ["tables"] = tables.Length };
        if (operations != null)
        {
            summary["operations"] = Columnar(operations);
            counts["operations"] = operations.Length;
        }

        if (columns != null)
        {
            summary["columns"] = Columnar(columns);
            counts["columns"] = columns.Length;
        }

        var manifest = new JsonObject
        {
            ["format"] = "pqax",
            ["formatVersion"] = formatVersion,
            ["collection"] = collection ?? new JsonObject
            {
                ["collectorApp"] = "pqax-tests",
                ["collectorAppVersion"] = "0.0",
                ["startedAt"] = "2026-10-01T00:00:00.000Z",
                ["completedAt"] = "2026-10-11T12:00:00.000Z",
                ["level"] = 2,
            },
            ["parts"] = new JsonArray(new JsonObject { ["name"] = "summary.json", ["tables"] = counts }),
        };
        byte[] zip = Kit.Zip([("manifest.json", Kit.Bytes(manifest)), ("summary.json", Kit.Bytes(summary))]);
        return PqaxDocument.Open(new MemoryStream(zip));
    }

    private static JsonObject Columnar(JsonObject[] rows)
    {
        var table = new JsonObject();
        foreach (string key in rows.SelectMany(row => row.Select(pair => pair.Key)).Distinct())
        {
            table[key] = new JsonArray(rows.Select(row => row.TryGetPropertyValue(key, out JsonNode? value) ? value?.DeepClone() : null).ToArray());
        }

        return table;
    }

    private static IReadOnlyList<TableStorage> Measure(PqaxDocument doc, IReadOnlyList<FileRow>? files = null, MeasureOptions? options = null) =>
        StorageMeasures.ForTables(doc.Summary, files, doc.Manifest.Collection, options);

    private static JsonObject Table(long id, string name = "t", long? fileSize = 1000, long? rowCount = 100, long? deletedRowCount = 10) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["fileCount"] = 2,
        ["fileSize"] = fileSize,
        ["rowCount"] = rowCount,
        ["deletedRowCount"] = deletedRowCount,
        ["footerSize"] = 0,
        ["staleColumnSize"] = 0,
    };

    private static FileRow File_(long id, long? table, long? size, long? rows, long? deleted) =>
        new() { Id = id, Table = table, FileSize = size, RowCount = rows, DeletedRowCount = deleted };

    [Fact]
    public void The_deleted_rows_come_from_the_files_when_every_file_with_a_size_gives_an_estimate()
    {
        using PqaxDocument doc = Footprint([Table(1)]);
        FileRow[] files =
        [
            File_(1, 1, 600, 60, 6),
            File_(2, 1, 400, 40, 0),
            File_(3, 1, null, null, null),
            File_(4, 2, 999, 10, 10),
        ];
        TableStorage table = Assert.Single(Measure(doc, files));
        Assert.Equal(60.0, table.Stack.Deleted);
        Assert.Equal("files", table.Stack.DeletedSource);
        Assert.Equal(940.0, table.Stack.Live);
    }

    [Fact]
    public void A_file_with_a_size_and_no_estimate_sends_the_deleted_rows_back_to_the_table()
    {
        using PqaxDocument doc = Footprint([Table(1)]);
        TableStorage table = Assert.Single(Measure(doc, [File_(1, 1, 600, 60, 6), File_(2, 1, 400, null, 4)]));
        Assert.Equal(100.0, table.Stack.Deleted);
        Assert.Equal("table", table.Stack.DeletedSource);
    }

    [Fact]
    public void A_file_with_no_deleted_row_gives_an_estimate_of_0_without_its_row_count()
    {
        using PqaxDocument doc = Footprint([Table(1)]);
        TableStorage table = Assert.Single(Measure(doc, [File_(1, 1, 600, 60, 6), File_(2, 1, 400, null, 0)]));
        Assert.Equal(60.0, table.Stack.Deleted);
        Assert.Equal("files", table.Stack.DeletedSource);
    }

    [Fact]
    public void A_file_whose_deleted_rows_exceed_its_rows_counts_its_whole_size()
    {
        using PqaxDocument doc = Footprint([Table(1)]);
        TableStorage table = Assert.Single(Measure(doc, [File_(1, 1, 600, 60, 90), File_(2, 1, 400, 40, 0)]));
        Assert.Equal(600.0, table.Stack.Deleted);
    }

    [Fact]
    public void Without_files_of_the_table_the_deleted_rows_come_from_the_table()
    {
        using PqaxDocument doc = Footprint([Table(1)]);
        TableStorage table = Assert.Single(Measure(doc, [File_(1, 2, 600, 60, 6)]));
        Assert.Equal(100.0, table.Stack.Deleted);
        Assert.Equal("table", table.Stack.DeletedSource);
        Assert.Equal("table", Assert.Single(Measure(doc)).Stack.DeletedSource);
    }

    [Fact]
    public void Without_a_row_count_and_files_the_deleted_rows_are_null()
    {
        using PqaxDocument doc = Footprint([Table(1, rowCount: null)]);
        TableStorage table = Assert.Single(Measure(doc));
        Assert.Null(table.Stack.Deleted);
        Assert.Null(table.Stack.DeletedSource);
        Assert.Equal(1000.0, table.Stack.Live);
    }

    [Fact]
    public void A_repeated_id_is_null_for_the_second_row_and_the_files_go_to_the_first()
    {
        using PqaxDocument doc = Footprint([Table(1, "first"), Table(1, "second")]);
        IReadOnlyList<TableStorage> tables = Measure(doc, [File_(1, 1, 600, 60, 30), File_(2, 1, 400, 40, 0)]);
        Assert.Equal(2, tables.Count);
        Assert.Equal(1L, tables[0].Id);
        Assert.Null(tables[1].Id);
        Assert.Equal(0, tables[0].Row);
        Assert.Equal(1, tables[1].Row);
        Assert.Equal("first", tables[0].Label);
        Assert.Equal("second", tables[1].Label);
        Assert.Equal(300.0, tables[0].Stack.Deleted);
        Assert.Equal("files", tables[0].Stack.DeletedSource);
        Assert.Equal(100.0, tables[1].Stack.Deleted);
        Assert.Equal("table", tables[1].Stack.DeletedSource);
        Assert.Equal(2, tables[0].SmallFiles.Count);
        Assert.Null(tables[1].SmallFiles.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void An_id_that_is_not_positive_is_null(long id)
    {
        using PqaxDocument doc = Footprint([Table(id)]);
        TableStorage table = Assert.Single(Measure(doc, [File_(1, id, 600, 60, 30)]));
        Assert.Null(table.Id);
        Assert.Equal("table", table.Stack.DeletedSource);
        Assert.Null(table.SmallFiles.Count);
    }

    [Fact]
    public void The_label_joins_the_namespace_and_the_name_or_names_the_id_or_the_row()
    {
        using PqaxDocument doc = Footprint(
        [
            new JsonObject { ["id"] = 4, ["namespace"] = new JsonArray("dbo", "sub"), ["name"] = "orders" },
            new JsonObject { ["id"] = 7 },
            new JsonObject { ["id"] = 7 },
        ]);
        IReadOnlyList<TableStorage> tables = Measure(doc);
        Assert.Equal("dbo.sub.orders", tables[0].Label);
        Assert.Equal("orders", tables[0].Name);
        Assert.Equal("Table 7", tables[1].Label);
        Assert.Null(tables[1].Name);
        Assert.Equal("Table at row 3", tables[2].Label);
    }

    [Fact]
    public void A_missing_input_gives_null_and_never_0()
    {
        using PqaxDocument doc = Footprint([new JsonObject { ["id"] = 1, ["name"] = "bare" }]);
        TableStorage table = Assert.Single(Measure(doc));
        Assert.Null(table.RowCount);
        Assert.Null(table.FileCount);
        Assert.Null(table.FileSize);
        Assert.All(StorageMeasures.StackComponents, key => Assert.Null(table.Stack.Component(key)));
        Assert.Null(table.Stack.DeletedSource);
        Assert.False(table.Stack.Listed);
        Assert.Null(table.Stack.Data);
        Assert.Null(table.Stack.Total);
        Assert.Null(table.Stack.WasteShare);
        Assert.Null(table.Stack.OverheadShare);
        Assert.Null(table.SmallFiles.Count);
        Assert.Null(table.SmallFiles.ShareAtLeast);
        Assert.Null(table.TinyFiles.ShareAtLeast);
        Assert.Null(table.Measures.CompressionRatio);
        Assert.Null(table.Measures.DeletedRowShare);
        Assert.Null(table.Measures.AverageFileSize);
        Assert.Null(table.Measures.AverageRowGroupRows);
        Assert.Null(table.Measures.FooterCoverage);
        Assert.Null(table.Measures.StaleColumnShare);
        Assert.Null(table.Measures.NonLiveShare);
        Assert.Null(table.Measures.AveragePartitionSize);
        Assert.Null(table.Measures.DictionaryFallbackShare);
        Assert.Null(table.Maintenance.LastCommitAt);
        Assert.Null(table.Maintenance.CommitsPerDay);
        Assert.Null(table.Maintenance.DaysSinceLastCommit);
    }

    [Fact]
    public void A_negative_size_or_count_is_missing()
    {
        using PqaxDocument doc = Footprint([new JsonObject { ["id"] = 1, ["name"] = "t", ["fileSize"] = -5, ["rowCount"] = -1, ["fileCount"] = -2, ["metadataSize"] = -9 }]);
        TableStorage table = Assert.Single(Measure(doc));
        Assert.Null(table.FileSize);
        Assert.Null(table.RowCount);
        Assert.Null(table.FileCount);
        Assert.Null(table.Stack.Live);
        Assert.Null(table.Stack.Metadata);
    }

    [Fact]
    public void The_files_give_the_exact_count_below_each_threshold()
    {
        using PqaxDocument doc = Footprint([Table(1)]);
        const long MiB = 1024 * 1024;
        FileRow[] files = [File_(1, 1, 1 * MiB, 1, 0), File_(2, 1, 8 * MiB, 1, 0), File_(3, 1, 10 * MiB, 1, 0), File_(4, 1, 128 * MiB, 1, 0), File_(5, 1, 200 * MiB, 1, 0)];
        TableStorage table = Assert.Single(Measure(doc, files));
        Assert.Equal(StorageMeasures.DefaultTinyFileSize, table.TinyFiles.Threshold);
        Assert.Equal(1, table.TinyFiles.Count);
        Assert.Equal(0.2, table.TinyFiles.ShareAtLeast);
        Assert.Equal(StorageMeasures.DefaultSmallFileSize, table.SmallFiles.Threshold);
        Assert.Equal(3, table.SmallFiles.Count);
        Assert.Equal(0.6, table.SmallFiles.ShareAtLeast);

        TableStorage other = Assert.Single(Measure(doc, files, new MeasureOptions { SmallFileSize = 300 * MiB, TinyFileSize = 9 * MiB }));
        Assert.Equal(5, other.SmallFiles.Count);
        Assert.Equal(1.0, other.SmallFiles.ShareAtLeast);
        Assert.Equal(2, other.TinyFiles.Count);
    }

    [Fact]
    public void Without_files_the_thresholds_use_the_quartiles_of_the_table()
    {
        const long MiB = 1024 * 1024;
        JsonObject row = Table(1);
        row["fileSizes"] = new JsonObject { ["count"] = 4, ["sum"] = 400 * MiB, ["minimum"] = 1 * MiB, ["firstQuartile"] = 4 * MiB, ["median"] = 9 * MiB, ["thirdQuartile"] = 100 * MiB, ["maximum"] = 200 * MiB };
        using PqaxDocument doc = Footprint([row]);
        TableStorage table = Assert.Single(Measure(doc));
        Assert.Null(table.TinyFiles.Count);
        Assert.Equal(0.25, table.TinyFiles.ShareAtLeast);
        Assert.Null(table.SmallFiles.Count);
        Assert.Equal(0.75, table.SmallFiles.ShareAtLeast);
    }

    [Fact]
    public void A_table_of_format_0_1_has_no_unreferenced_files()
    {
        JsonObject row = Table(1);
        row["nonLiveFileSize"] = 300;
        row["metadataSize"] = 30;
        using PqaxDocument doc = Footprint([row], formatVersion: "0.1");
        TableStorage table = Assert.Single(Measure(doc));
        Assert.Null(table.Stack.Unreferenced);
        Assert.False(table.Stack.Listed);
        Assert.Equal(300.0, table.Stack.NonLive);
        Assert.Equal(400.0, table.Stack.Waste);
    }

    [Fact]
    public void A_table_of_format_0_2_with_a_listing_splits_the_two_sets()
    {
        JsonObject row = Table(1);
        row["nonLiveFileSize"] = 300;
        row["unreferencedFileSize"] = 120;
        using PqaxDocument doc = Footprint([row]);
        TableStorage table = Assert.Single(Measure(doc));
        Assert.True(table.Stack.Listed);
        Assert.Equal(120.0, table.Stack.NonLive);
        Assert.Equal(0.0, table.Stack.Unreferenced);
        Assert.Equal(300.0 / 1300, table.Measures.NonLiveShare);
    }

    [Fact]
    public void The_last_commit_is_the_snapshot_else_the_latest_operation_else_the_last_change()
    {
        JsonObject snapshot = Table(1, "a");
        snapshot["snapshotTimestamp"] = "2026-10-01T12:00:00.000Z";
        snapshot["lastModifiedAt"] = "2026-09-01T00:00:00.000Z";
        snapshot["commitCount"] = 10;
        snapshot["commitsRead"] = 4;
        snapshot["oldestCommitAt"] = "2026-09-29T12:00:00.000Z";
        JsonObject operations = Table(2, "b");
        operations["lastModifiedAt"] = "2026-09-01T00:00:00.000Z";
        operations["commitCount"] = 6;
        operations["oldestCommitAt"] = "2026-09-20T00:00:00.000Z";
        JsonObject modified = Table(3, "c");
        modified["lastModifiedAt"] = "2026-09-01T00:00:00.000Z";
        using PqaxDocument doc = Footprint([snapshot, operations, modified],
        [
            new JsonObject { ["table"] = 2, ["name"] = "WRITE", ["commitCount"] = 3, ["lastAt"] = "2026-09-25T00:00:00.000Z" },
            new JsonObject { ["table"] = 2, ["name"] = "OPTIMIZE", ["commitCount"] = 1, ["lastAt"] = "2026-09-26T00:00:00.000Z" },
            new JsonObject { ["table"] = 2, ["name"] = "DELETE", ["commitCount"] = 2, ["lastAt"] = "2026-09-22T00:00:00.000Z" },
        ]);
        IReadOnlyList<TableStorage> tables = Measure(doc);

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), tables[0].Maintenance.LastCommitAt);
        Assert.Equal(2.0, tables[0].Maintenance.CommitsPerDay);
        Assert.Equal(10L, tables[0].Maintenance.DaysSinceLastCommit);

        Assert.Equal(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero), tables[1].Maintenance.LastCommitAt);
        // Section 10.4: the commits per day divide CommitsRead only, which this table lacks; CommitCount is not a fallback.
        Assert.Null(tables[1].Maintenance.CommitsPerDay);
        Assert.Equal(15L, tables[1].Maintenance.DaysSinceLastCommit);

        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), tables[2].Maintenance.LastCommitAt);
        Assert.Null(tables[2].Maintenance.CommitsPerDay);
        Assert.Equal(40L, tables[2].Maintenance.DaysSinceLastCommit);
    }

    [Fact]
    public void The_days_since_the_last_commit_count_to_the_start_without_an_end_and_are_null_without_either()
    {
        JsonObject row = Table(1);
        row["snapshotTimestamp"] = "2026-10-01T12:00:00.000Z";
        using (PqaxDocument started = Footprint([row], collection: new JsonObject { ["startedAt"] = "2026-10-04T00:00:00.000Z", ["level"] = 2 }))
        {
            Assert.Equal(2L, Assert.Single(Measure(started)).Maintenance.DaysSinceLastCommit);
            Assert.Null(Assert.Single(StorageMeasures.ForTables(started.Summary, null, null)).Maintenance.DaysSinceLastCommit);
        }

        using PqaxDocument neither = Footprint([row], collection: new JsonObject { ["level"] = 2 });
        Assert.Null(Assert.Single(Measure(neither)).Maintenance.DaysSinceLastCommit);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), Assert.Single(Measure(neither)).Maintenance.LastCommitAt);
    }

    [Fact]
    public void The_measures_of_a_table_follow_the_views()
    {
        JsonObject full = Table(1, "full", fileSize: 1200, rowCount: 1000, deletedRowCount: 50);
        full["fileCount"] = 4;
        full["compressedSize"] = 1000;
        full["uncompressedSize"] = 2500;
        full["staleColumnSize"] = 100;
        full["nonLiveFileSize"] = 300;
        full["footerCount"] = 4;
        full["rowGroupCount"] = 8;
        full["partitionCount"] = 3;
        full["rowGroupRowCounts"] = new JsonObject { ["count"] = 5, ["sum"] = 1000 };
        full["partitionSizes"] = new JsonObject { ["count"] = 2, ["sum"] = 1200 };
        JsonObject fallback = Table(2, "fallback", fileSize: 1200, rowCount: 1000, deletedRowCount: 0);
        fallback["fileCount"] = 4;
        fallback["footerCount"] = 4;
        fallback["rowGroupCount"] = 8;
        fallback["partitionCount"] = 3;
        JsonObject partial = Table(3, "partial");
        partial["fileCount"] = 4;
        partial["footerCount"] = 3;
        partial["rowGroupCount"] = 8;
        using PqaxDocument doc = Footprint([full, fallback, partial], columns:
        [
            new JsonObject { ["id"] = 1, ["table"] = 1, ["name"] = "a", ["dictionaryChunkCount"] = 10, ["fallbackChunkCount"] = 1 },
            new JsonObject { ["id"] = 2, ["table"] = 1, ["name"] = "b", ["dictionaryChunkCount"] = 6, ["fallbackChunkCount"] = 3 },
            new JsonObject { ["id"] = 3, ["table"] = 1, ["name"] = "c", ["dictionaryChunkCount"] = 100 },
            new JsonObject { ["id"] = 4, ["table"] = 2, ["name"] = "d", ["dictionaryChunkCount"] = 0, ["fallbackChunkCount"] = 0 },
        ]);
        IReadOnlyList<TableStorage> tables = Measure(doc);

        TableMeasures m = tables[0].Measures;
        Assert.Equal(2.5, m.CompressionRatio);
        Assert.Equal(0.05, m.DeletedRowShare);
        Assert.Equal(300.0, m.AverageFileSize);
        Assert.Equal(200.0, m.AverageRowGroupRows);
        Assert.Equal(1.0, m.FooterCoverage);
        Assert.Equal(0.1, m.StaleColumnShare);
        Assert.Equal(300.0 / 1500, m.NonLiveShare);
        Assert.Equal(600.0, m.AveragePartitionSize);
        Assert.Equal(4.0 / 16, m.DictionaryFallbackShare);

        TableMeasures f = tables[1].Measures;
        Assert.Equal(125.0, f.AverageRowGroupRows);
        Assert.Equal(400.0, f.AveragePartitionSize);
        Assert.Null(f.DictionaryFallbackShare);
        Assert.Null(f.CompressionRatio);
        Assert.Equal(0.0, f.DeletedRowShare);

        Assert.Null(tables[2].Measures.AverageRowGroupRows);
        Assert.Equal(0.75, tables[2].Measures.FooterCoverage);
        Assert.Null(tables[2].Measures.DictionaryFallbackShare);
    }
}
