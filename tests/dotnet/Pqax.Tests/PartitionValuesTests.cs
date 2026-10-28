using System.Reflection;
using System.Text.Json.Nodes;

namespace Pqax.Tests;

/// <summary>
/// The values of a partition are nullable strings: <see cref="PartitionRow.Values"/> is <c>IReadOnlyList&lt;string?&gt;?</c>,
/// so a nullable-aware caller is warned when it reads an element as a string, and a null value survives a write and a
/// read. The other lists of strings of the model keep elements that are not null (the contract of the hardening before
/// 1.0, section 4).
/// </summary>
public class PartitionValuesTests
{
    private static readonly NullabilityInfoContext Context = new();

    private static NullabilityInfo Nullability(Type type, string property) => Context.Create(type.GetProperty(property)!);

    private static byte[] Write(FootprintBuilder builder)
    {
        using var stream = new MemoryStream();
        builder.Write(stream);
        return stream.ToArray();
    }

    private static PqaxDocument Open(byte[] data) => PqaxDocument.Open(new MemoryStream(data));

    private static void AssertValid(byte[] data)
    {
        using var stream = new MemoryStream(data);
        ValidationResult result = PqaxValidator.Validate(stream);
        Assert.Empty(result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void The_elements_of_PartitionRow_Values_are_nullable()
    {
        NullabilityInfo values = Nullability(typeof(PartitionRow), nameof(PartitionRow.Values));
        Assert.Equal(typeof(IReadOnlyList<string>), values.Type);
        Assert.Equal(NullabilityState.Nullable, values.ReadState);
        Assert.Equal(NullabilityState.Nullable, values.WriteState);
        NullabilityInfo element = Assert.Single(values.GenericTypeArguments);
        Assert.Equal(NullabilityState.Nullable, element.ReadState);
    }

    [Fact]
    public void The_elements_of_TableRow_Namespace_are_not_nullable()
    {
        NullabilityInfo names = Nullability(typeof(TableRow), nameof(TableRow.Namespace));
        Assert.Equal(typeof(IReadOnlyList<string>), names.Type);
        Assert.Equal(NullabilityState.Nullable, names.ReadState);
        Assert.Equal(NullabilityState.NotNull, Assert.Single(names.GenericTypeArguments).ReadState);
    }

    [Fact]
    public void The_other_lists_of_strings_of_the_model_have_elements_that_are_not_nullable()
    {
        var lists = typeof(PartitionRow).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "Pqax" && (type.Name.EndsWith("Row", StringComparison.Ordinal) || type == typeof(DeltaDetails) || type == typeof(IcebergDetails)))
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => (Type: type, Property: property)))
            .Where(pair => pair.Property.PropertyType == typeof(IReadOnlyList<string>))
            .ToList();
        Assert.Contains(lists, pair => pair.Type == typeof(TableRow) && pair.Property.Name == nameof(TableRow.Namespace));
        Assert.Contains(lists, pair => pair.Type == typeof(DeltaDetails) && pair.Property.Name == nameof(DeltaDetails.ReaderFeatures));
        Assert.Contains(lists, pair => pair.Type == typeof(PartitionRow) && pair.Property.Name == nameof(PartitionRow.Values));
        foreach ((Type type, PropertyInfo property) in lists)
        {
            bool values = type == typeof(PartitionRow) && property.Name == nameof(PartitionRow.Values);
            NullabilityState state = Assert.Single(Context.Create(property).GenericTypeArguments).ReadState;
            Assert.True((values ? NullabilityState.Nullable : NullabilityState.NotNull) == state, $"{type.Name}.{property.Name}: {state}");
        }
    }

    [Fact]
    public void A_partition_with_a_null_value_is_written_and_read_back_with_the_null()
    {
        var builder = new FootprintBuilder(new CollectionInfo { CollectorApp = "pqax-tests", Level = 1, IncludesPartitionValues = true });
        builder.AddSource(new SourceRow { Kind = "Storage folder" });
        builder.AddTable(new TableRow { Source = 1, Name = "a", Format = "Delta", DetailDepth = "Files" });
        builder.AddColumn(new ColumnRow { Table = 1, Name = "year", DataType = "string", IsPartitionColumn = true });
        builder.AddColumn(new ColumnRow { Table = 1, Name = "region", DataType = "string", IsPartitionColumn = true });
        builder.AddPartitionSpec(new PartitionSpecRow { Table = 1 });
        builder.AddPartitionField(new PartitionFieldRow { PartitionSpec = 1, Position = 0, Name = "year", SourceColumns = [1], Transform = "identity" });
        builder.AddPartitionField(new PartitionFieldRow { PartitionSpec = 1, Position = 1, Name = "region", SourceColumns = [2], Transform = "identity" });
        var first = new PartitionRow { Table = 1, PartitionSpec = 1, Values = ["2024", null] };
        var second = new PartitionRow { Table = 1, PartitionSpec = 1, Values = [null, "west"] };
        builder.AddPartition(first);
        builder.AddPartition(second);
        byte[] data = Write(builder);

        JsonNode part = Kit.Json(Kit.Entries(data).Single(entry => entry.Name == "partitions.json").Content);
        Assert.Equal("[[\"2024\",null],[null,\"west\"]]", Kit.Canonical(part["partitions"]!["values"]));
        AssertValid(data);

        using PqaxDocument doc = Open(data);
        IReadOnlyList<string?>? values = doc.ReadPartitions()[0].Values;
        Assert.NotNull(values);
        Assert.Equal(["2024", null], values);
        Assert.Null(values[1]);
        Assert.Equal([null, "west"], doc.ReadPartitions()[1].Values!);
        Assert.Equal([first, second], doc.ReadPartitions().Rows);

        byte[] again = Write(FootprintBuilder.From(doc));
        using PqaxDocument copy = Open(again);
        Assert.Equal(doc.ReadPartitions().Rows, copy.ReadPartitions().Rows);
        Assert.Equal(["2024", null], copy.ReadPartitions()[0].Values!);
        AssertValid(again);
    }
}
