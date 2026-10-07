using System.Globalization;
using System.Text.Json.Nodes;
using Pqax.Collector.ParquetNet;

namespace Pqax.Collector.Tests;

/// <summary>The Parquet.Net checkpoint reader on the checkpoint of the fixture, against the probe dump.</summary>
public class CheckpointTests
{
    private static DumpTable Table => Fixture.CheckpointTable;

    private static long Version => Dump.Long(Table.Delta["checkpointVersion"]);

    private static string LogFolder => Table.TablePath + "/_delta_log";

    private static string CheckpointPath => $"{LogFolder}/{Version.ToString("D20", CultureInfo.InvariantCulture)}.checkpoint.parquet";

    private static async Task<IReadOnlyList<DeltaAction>> ReadAsync(IStorage storage) =>
        await new ParquetNetCheckpointReader().ReadAsync(storage, CheckpointPath, CancellationToken.None);

    [Fact]
    public async Task The_checkpoint_is_read_through_the_storage_it_is_given()
    {
        var storage = new TestStorage(new LocalStorage(Fixture.Folder));
        IReadOnlyList<DeltaAction> actions = await ReadAsync(storage);
        Assert.NotEmpty(actions);
        Assert.NotEmpty(storage.Requests);
        Assert.All(storage.Requests, request => Assert.Equal(CheckpointPath, request.Path));
    }

    [Fact]
    public async Task The_protocol_the_metadata_and_the_domains_are_those_of_the_dump()
    {
        IReadOnlyList<DeltaAction> actions = await ReadAsync(new LocalStorage(Fixture.Folder));
        JsonObject delta = Table.Delta;

        ProtocolAction protocol = Assert.Single(actions.OfType<ProtocolAction>());
        Assert.Equal(Dump.Long(delta["minReaderVersion"]), protocol.MinReaderVersion);
        Assert.Equal(Dump.Long(delta["minWriterVersion"]), protocol.MinWriterVersion);
        Assert.Equal(Dump.Strings(delta["readerFeatures"]), protocol.ReaderFeatures!);
        Assert.Equal(Dump.Strings(delta["writerFeatures"]), protocol.WriterFeatures!);

        MetadataAction metadata = Assert.Single(actions.OfType<MetadataAction>());
        Assert.Equal(Dump.Text(delta["tableId"]), metadata.Id);
        Assert.Equal(Dump.Text(delta["provider"]), metadata.Provider);
        Assert.Equal(Dump.Long(delta["createdTime"]), metadata.CreatedTime);
        Assert.Equal(Dump.Strings(delta["partitionColumns"]), metadata.PartitionColumns!);
        Assert.Equal(Dump.Map(delta["configuration"]).OrderBy(pair => pair.Key), metadata.Configuration!.OrderBy(pair => pair.Key));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Dump.Text(delta["schemaString"])!), JsonNode.Parse(metadata.SchemaString!)));

        JsonObject domains = delta["domains"]!.AsObject();
        List<DomainMetadataAction> actual = actions.OfType<DomainMetadataAction>().Where(domain => !domain.Removed).ToList();
        Assert.Equal(domains.Select(pair => pair.Key).Order(StringComparer.Ordinal), actual.Select(domain => domain.Domain!).Order(StringComparer.Ordinal));
        foreach (DomainMetadataAction domain in actual)
        {
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse((string)domains[domain.Domain!]!), JsonNode.Parse(domain.Configuration!)));
        }

        List<RemoveAction> removes = actions.OfType<RemoveAction>().ToList();
        Assert.Equal(Dump.Long(delta["removedFileCount"]), removes.Count);
        Assert.Equal(Dump.Long(delta["removedFileSize"]), removes.Sum(remove => remove.Size));
    }

    private static long? Records(AddAction add) =>
        add.NumRecords ?? (add.Stats is string stats ? JsonNode.Parse(stats)?["numRecords"]?.GetValue<long>() : null);

    [Fact]
    public async Task The_checkpoint_and_the_later_commits_give_the_files_of_the_dump()
    {
        IReadOnlyList<DeltaAction> actions = await ReadAsync(new LocalStorage(Fixture.Folder));
        var live = new Dictionary<string, (long? Size, long? Records, long? DeletionVector)>(StringComparer.Ordinal);
        foreach (AddAction add in actions.OfType<AddAction>())
        {
            live[add.Path!] = (add.Size, Records(add), add.DeletionVector?.SizeInBytes);
        }

        string folder = Path.Combine(Fixture.Folder, LogFolder.Replace('/', Path.DirectorySeparatorChar));
        IEnumerable<string> commits = Directory.EnumerateFiles(folder, "*.json")
            .Where(path => long.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture) > Version)
            .Order(StringComparer.Ordinal);
        foreach (string commit in commits)
        {
            foreach (string line in File.ReadLines(commit).Where(line => line.Trim().Length > 0))
            {
                JsonNode action = JsonNode.Parse(line)!;
                if (action["remove"] is JsonNode remove)
                {
                    live.Remove((string)remove["path"]!);
                }

                if (action["add"] is JsonNode add)
                {
                    long? records = add["stats"] is JsonNode stats ? JsonNode.Parse((string)stats!)?["numRecords"]?.GetValue<long>() : null;
                    live[(string)add["path"]!] = ((long)add["size"]!, records, (long?)add["deletionVector"]?["sizeInBytes"]);
                }
            }
        }

        var expected = Table.Files.Select(file => $"{Dump.Long(file["size"])}|{Dump.NullableLong(file["numRecords"])}|{Dump.NullableLong(file["deletionVectorSize"])}").Order(StringComparer.Ordinal);
        var actual = live.Values.Select(file => $"{file.Size}|{file.Records}|{file.DeletionVector}").Order(StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task A_file_that_is_not_a_checkpoint_makes_the_read_fail()
    {
        string commit = Directory.EnumerateFiles(Path.Combine(Fixture.Folder, LogFolder.Replace('/', Path.DirectorySeparatorChar)), "*.json").First();
        string path = LogFolder + "/" + Path.GetFileName(commit);
        await Assert.ThrowsAnyAsync<Exception>(() => new ParquetNetCheckpointReader().ReadAsync(new LocalStorage(Fixture.Folder), path, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_read_throws()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ParquetNetCheckpointReader().ReadAsync(new LocalStorage(Fixture.Folder), CheckpointPath, cancel.Token));
    }
}
