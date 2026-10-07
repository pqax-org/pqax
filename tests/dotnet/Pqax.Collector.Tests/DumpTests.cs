using System.Text.Json.Nodes;

namespace Pqax.Collector.Tests;

/// <summary>
/// The fixture, collected at level 2 and depth Column chunks through LocalStorage, against the probe dump of the same
/// fixture (conformance/fixtures/probe-dump): files, rows, deleted rows, row groups, column chunks, sizes, distinct
/// counts, footer sizes, writers, operations, commits, and log files (plan 5). The dump replaces partition values in
/// paths with ordinals, so files are matched by their size, which is distinct in the fixture, and paths are not compared.
/// </summary>
public class DumpTests(DefaultCollection collection) : IClassFixture<DefaultCollection>
{
    private PqaxDocument Doc => collection.Document;

    public static TheoryData<string> TableIds => Fixture.Data(Fixture.Tables.Select(table => table.Id));

    private static DumpTable DumpOf(string id) => Fixture.Tables.Single(table => table.Id == id);

    private List<FileRow> FilesOf(TableRow table) => Doc.ReadFiles().Where(file => file.Table == table.Id).ToList();

    private FileRow FileOf(TableRow table, JsonObject dumpFile) => FilesOf(table).Single(file => file.FileSize == Dump.Long(dumpFile["size"]));

    private static long ChunkSum(JsonObject file, string field) =>
        Dump.RowGroups(file).Sum(group => Enumerable.Range(0, Dump.ColumnCount(group)).Sum(i => Dump.At(group, field, i) ?? 0));

    [Theory]
    [MemberData(nameof(TableIds))]
    public void Files_have_the_sizes_rows_deleted_rows_and_footers_of_the_dump(string id)
    {
        DumpTable dump = DumpOf(id);
        TableRow table = Fixture.TableOf(Doc, dump);
        List<FileRow> files = FilesOf(table);
        Assert.Equal(dump.Files.Count, files.Count);
        Assert.Equal(files.Count, files.Select(file => file.FileSize).Distinct().Count());
        foreach (JsonObject file in dump.Files)
        {
            FileRow row = FileOf(table, file);
            JsonObject footer = file["footer"]!.AsObject();
            Assert.Equal(Dump.Time(file["modificationTime"]), row.ModifiedAt);
            Assert.Equal(Dump.NullableLong(file["numRecords"]) ?? Dump.Long(footer["numRows"]), row.RowCount);
            Assert.Equal(Dump.NullableLong(file["deletedRows"]) ?? 0, row.DeletedRowCount);
            Assert.Equal(Dump.NullableLong(file["deletionVectorSize"]), row.DeletionVectorSize);
            Assert.Equal(Dump.Long(footer["footerSize"]), row.FooterSize);
            Assert.Equal(Dump.RowGroups(file).Count, row.RowGroupCount);
            Assert.Equal(ChunkSum(file, "compressed"), row.CompressedSize);
            Assert.Equal(ChunkSum(file, "uncompressed"), row.UncompressedSize);
            Assert.Equal(Dump.IsVOrdered(file), row.IsVOrdered);
        }
    }

    [Theory]
    [MemberData(nameof(TableIds))]
    public void Files_of_the_same_partition_share_it(string id)
    {
        DumpTable dump = DumpOf(id);
        TableRow table = Fixture.TableOf(Doc, dump);
        bool partitioned = dump.Delta["partitionColumns"]!.AsArray().Count > 0;
        var byOrdinal = new Dictionary<string, HashSet<long?>>();
        foreach (JsonObject file in dump.Files)
        {
            FileRow row = FileOf(table, file);
            if (!partitioned)
            {
                Assert.Null(row.Partition);
                continue;
            }

            Assert.NotNull(row.Partition);
            string key = Fixture.Canonical(file["partition"]);
            byOrdinal.TryAdd(key, []);
            byOrdinal[key].Add(row.Partition);
        }

        Assert.All(byOrdinal.Values, ids => Assert.Single(ids));
        Assert.Equal(byOrdinal.Count, byOrdinal.Values.Select(ids => ids.Single()).Distinct().Count());
        if (partitioned)
        {
            Assert.Equal(byOrdinal.Count, Doc.ReadPartitions().Count(partition => partition.Table == table.Id));
        }
    }

    [Theory]
    [MemberData(nameof(TableIds))]
    public void Row_groups_and_column_chunks_are_those_of_the_footers(string id)
    {
        DumpTable dump = DumpOf(id);
        TableRow table = Fixture.TableOf(Doc, dump);
        var ordinals = Doc.Summary.PhysicalColumns.ToDictionary(column => column.Id, column => column.Ordinal);
        foreach (JsonObject file in dump.Files)
        {
            FileRow row = FileOf(table, file);
            List<RowGroupRow> groups = Doc.ReadRowGroups().Where(group => group.File == row.Id).OrderBy(group => group.Ordinal).ToList();
            IReadOnlyList<JsonObject> dumpGroups = Dump.RowGroups(file);
            Assert.Equal(dumpGroups.Count, groups.Count);
            for (int g = 0; g < groups.Count; g++)
            {
                JsonObject expected = dumpGroups[g];
                Assert.Equal(g, groups[g].Ordinal);
                Assert.Equal(Dump.Long(expected["numRows"]), groups[g].RowCount);
                Assert.Equal(Dump.Long(expected["totalByteSize"]), groups[g].UncompressedSize);
                Assert.Equal(Dump.Long(expected["totalCompressedSize"]), groups[g].CompressedSize);
                List<ColumnChunkRow> chunks = Doc.ReadColumnChunks().Where(chunk => chunk.RowGroup == groups[g].Id)
                    .OrderBy(chunk => ordinals[chunk.PhysicalColumn!.Value]).ToList();
                Assert.Equal(Dump.ColumnCount(expected), chunks.Count);
                for (int c = 0; c < chunks.Count; c++)
                {
                    ColumnChunkRow chunk = chunks[c];
                    string where = $"{id} file of {row.FileSize} bytes, row group {g}, column {c}";
                    Assert.True(ordinals[chunk.PhysicalColumn!.Value] == c, where);
                    Assert.True(Dump.At(expected, "compressed", c) == chunk.CompressedSize, where + ": compressedSize");
                    Assert.True(Dump.At(expected, "uncompressed", c) == chunk.UncompressedSize, where + ": uncompressedSize");
                    Assert.True(Dump.At(expected, "numValues", c) == chunk.ValueCount, where + ": valueCount");
                    Assert.True(Dump.At(expected, "nullCount", c) == chunk.NullCount, where + ": nullCount");
                    Assert.True(Dump.At(expected, "distinctCount", c) == chunk.DistinctCount, where + ": distinctCount");
                    Assert.True(Dump.TextAt(expected, "codec", c) == chunk.Codec, where + ": codec");
                    Assert.Equal(Dump.Encodings(expected, c), chunk.Encodings);
                    long? dictionary = Dump.At(expected, "dictionaryPageOffset", c);
                    Assert.True((dictionary != null) == chunk.HasDictionaryPage, where + ": hasDictionaryPage");
                    Assert.True((dictionary == null ? null : Dump.At(expected, "dataPageOffset", c) - dictionary) == chunk.DictionaryPageSize, where + ": dictionaryPageSize");
                    Assert.True(Dump.At(expected, "bloomFilterLength", c) == chunk.BloomFilterSize, where + ": bloomFilterSize");
                    Assert.True(Dump.At(expected, "unencodedByteArrayBytes", c) == chunk.UnencodedByteArrayDataBytes, where + ": unencodedByteArrayDataBytes");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(TableIds))]
    public void Table_totals_are_the_sums_over_the_dump(string id)
    {
        DumpTable dump = DumpOf(id);
        TableRow table = Fixture.TableOf(Doc, dump);
        IReadOnlyList<JsonObject> files = dump.Files;
        Assert.Equal("Delta", table.Format);
        Assert.Equal("Column chunks", table.DetailDepth);
        Assert.Equal(2, table.CollectedLevel);
        Assert.Equal(files.Count, table.FileCount);
        Assert.Equal(files.Sum(file => Dump.Long(file["size"])), table.FileSize);
        Assert.Equal(files.Sum(file => Dump.NullableLong(file["numRecords"]) ?? Dump.Long(file["footer"]!["numRows"])), table.RowCount);
        Assert.Equal(files.Sum(file => Dump.NullableLong(file["deletedRows"]) ?? 0), table.DeletedRowCount);
        Assert.Equal(files.Max(file => Dump.Time(file["modificationTime"])), table.LastModifiedAt);
        Assert.Equal(files.Count(Dump.IsVOrdered), table.VOrderedFileCount);
        Assert.Equal(files.Count, table.FooterCount);
        Assert.Equal(files.Sum(file => Dump.RowGroups(file).Count), table.RowGroupCount);
        Assert.Equal(files.Sum(file => ChunkSum(file, "compressed")), table.CompressedSize);
        Assert.Equal(files.Sum(file => ChunkSum(file, "uncompressed")), table.UncompressedSize);
        Assert.Equal(files.Sum(file => Dump.Long(file["footer"]!["footerSize"])), table.FooterSize);
        bool partitioned = dump.Delta["partitionColumns"]!.AsArray().Count > 0;
        long? partitions = partitioned ? files.Select(file => Fixture.Canonical(file["partition"])).Distinct().Count() : null;
        Assert.Equal(partitions, table.PartitionCount);

        IReadOnlyList<long> rowCounts = files.SelectMany(Dump.RowGroups).Select(group => Dump.Long(group["numRows"])).Order().ToList();
        Assert.NotNull(table.RowGroupRowCounts);
        Assert.Equal(rowCounts.Count, table.RowGroupRowCounts.Count);
        Assert.Equal(rowCounts.Sum(), table.RowGroupRowCounts.Sum);
        Assert.Equal(rowCounts[0], table.RowGroupRowCounts.Minimum);
        Assert.Equal(rowCounts[^1], table.RowGroupRowCounts.Maximum);
        IReadOnlyList<long> sizes = files.Select(file => Dump.Long(file["size"])).Order().ToList();
        Assert.NotNull(table.FileSizes);
        Assert.Equal(sizes.Count, table.FileSizes.Count);
        Assert.Equal(sizes.Sum(), table.FileSizes.Sum);
        Assert.Equal(sizes[0], table.FileSizes.Minimum);
        Assert.Equal(sizes[^1], table.FileSizes.Maximum);
    }

    [Theory]
    [MemberData(nameof(TableIds))]
    public void The_delta_log_details_are_those_of_the_dump(string id)
    {
        DumpTable dump = DumpOf(id);
        JsonObject delta = dump.Delta;
        TableRow table = Fixture.TableOf(Doc, dump);
        Assert.Equal(Dump.Text(delta["tableId"]), table.TableId);
        Assert.Equal(Dump.Long(delta["version"]).ToString(System.Globalization.CultureInfo.InvariantCulture), table.SnapshotId);
        Assert.Equal(Dump.Time(delta["createdTime"]), table.CreatedAt);
        Assert.Equal(Dump.Map(delta["configuration"]).OrderBy(pair => pair.Key), table.Properties!.OrderBy(pair => pair.Key));
        Assert.Equal(Dump.Long(delta["logFileCount"]), table.MetadataFileCount);
        Assert.Equal(Dump.Long(delta["logSize"]), table.MetadataSize);
        Assert.Equal(Dump.Long(delta["removedFileCount"]), table.NonLiveFileCount);
        Assert.Equal(Dump.Long(delta["removedFileSize"]), table.NonLiveFileSize);
        Assert.Equal(Dump.Long(delta["commitFileCount"]), table.CommitCount);
        Assert.Equal(dump.Commits.Count, table.CommitsRead);
        Assert.Equal(dump.Commits.Min(commit => Dump.Time(commit["timestamp"])), table.OldestCommitAt);
        JsonObject last = dump.Commits.Single(commit => Dump.Long(commit["version"]) == Dump.Long(delta["version"]));
        Assert.Equal(Dump.Time(last["timestamp"]), table.SnapshotTimestamp);

        DeltaDetails? details = table.Delta;
        Assert.NotNull(details);
        Assert.Equal(Dump.Long(delta["minReaderVersion"]), details.MinReaderVersion);
        Assert.Equal(Dump.Long(delta["minWriterVersion"]), details.MinWriterVersion);
        Assert.Equal(Dump.Strings(delta["readerFeatures"]), details.ReaderFeatures);
        Assert.Equal(Dump.Strings(delta["writerFeatures"]), details.WriterFeatures);
        Assert.Equal(Dump.NullableLong(delta["checkpointVersion"]), details.CheckpointVersion);
        Assert.Equal(delta["domains"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal), (details.Domains ?? []).Order(StringComparer.Ordinal));
        if (Dump.NullableLong(delta["checkpointVersion"]) is long checkpoint)
        {
            Assert.Equal(Dump.Long(delta["version"]) - checkpoint, details.CommitsAfterCheckpoint);
        }
    }

    [Theory]
    [MemberData(nameof(TableIds))]
    public void Columns_follow_the_schema_string_and_the_clustering_domain(string id)
    {
        DumpTable dump = DumpOf(id);
        TableRow table = Fixture.TableOf(Doc, dump);
        JsonArray fields = JsonNode.Parse(Dump.Text(dump.Delta["schemaString"])!)!["fields"]!.AsArray();
        IReadOnlyList<string> partitionColumns = Dump.Strings(dump.Delta["partitionColumns"]);
        List<ColumnRow> columns = Doc.Summary.Columns.Where(column => column.Table == table.Id).OrderBy(column => column.Id).ToList();
        Assert.Equal(fields.Count, columns.Count);
        for (int i = 0; i < fields.Count; i++)
        {
            Assert.Equal((string)fields[i]!["name"]!, columns[i].Name);
            Assert.Equal((string)fields[i]!["type"]!, columns[i].DataType);
            Assert.Equal((bool)fields[i]!["nullable"]!, columns[i].IsNullable);
            Assert.Equal(partitionColumns.Contains(columns[i].Name!), columns[i].IsPartitionColumn);
        }

        if (dump.Delta["domains"]!["delta.clustering"] is JsonNode clustering)
        {
            JsonArray names = JsonNode.Parse((string)clustering!)!["clusteringColumns"]!.AsArray();
            Assert.Equal(names.Select(path => columns.Single(column => column.Name == (string)path![0]!).Id), table.ClusteringColumns!);
        }
        else
        {
            Assert.Null(table.ClusteringColumns);
        }
    }

    [Theory]
    [MemberData(nameof(TableIds))]
    public void Physical_schemas_and_columns_are_those_of_the_footers(string id)
    {
        DumpTable dump = DumpOf(id);
        TableRow table = Fixture.TableOf(Doc, dump);
        List<PhysicalSchemaRow> schemas = Doc.Summary.PhysicalSchemas.Where(schema => schema.Table == table.Id).ToList();
        Assert.Equal(dump.Schemas.Count, schemas.Count);
        var columnsByName = Doc.Summary.Columns.Where(column => column.Table == table.Id).ToDictionary(column => column.Name!, column => column.Id);
        foreach (PhysicalSchemaRow schema in schemas)
        {
            List<FileRow> files = FilesOf(table).Where(file => file.PhysicalSchema == schema.Id).ToList();
            Assert.Equal(files.Count, schema.FileCount);
            JsonObject sample = dump.Files.Single(file => Dump.Long(file["size"]) == files[0].FileSize);
            JsonObject expected = dump.Schemas[(int)Dump.Long(sample["footer"]!["schema"])];
            JsonArray leaves = expected["columns"]!.AsArray();
            List<PhysicalColumnRow> physical = Doc.Summary.PhysicalColumns.Where(column => column.PhysicalSchema == schema.Id).OrderBy(column => column.Ordinal).ToList();
            Assert.Equal(leaves.Count, physical.Count);
            for (int i = 0; i < leaves.Count; i++)
            {
                Assert.Equal(i, physical[i].Ordinal);
                Assert.Equal(Dump.Strings(leaves[i]!["path"]), physical[i].Path!);
                Assert.Equal((string)leaves[i]!["type"]!, physical[i].PhysicalType);
                Assert.Equal(Dump.Text(leaves[i]!["repetition"]), physical[i].Repetition);
                Assert.Equal(Dump.Text(leaves[i]!["converted"]), physical[i].LogicalType);
                Assert.Equal(columnsByName.GetValueOrDefault(Dump.Strings(leaves[i]!["path"])[0]), physical[i].Column);
            }
        }
    }

    [Theory]
    [MemberData(nameof(TableIds))]
    public void Writers_group_the_files_by_writer_and_v_order(string id)
    {
        DumpTable dump = DumpOf(id);
        TableRow table = Fixture.TableOf(Doc, dump);
        var expected = dump.Files
            .GroupBy(file => (CreatedBy: Dump.Text(file["footer"]!["createdBy"]), VOrdered: Dump.IsVOrdered(file), Level: Dump.KeyValue(file["footer"]!.AsObject(), "vorder.level")))
            .Select(group => $"{group.Key.CreatedBy}|{group.Key.VOrdered}|{group.Key.Level}|{group.Count()}|{group.Sum(file => Dump.Long(file["size"]))}")
            .Order(StringComparer.Ordinal).ToList();
        List<WriterRow> writers = Doc.Summary.Writers.Where(writer => writer.Table == table.Id).ToList();
        var actual = writers.Select(writer => $"{writer.CreatedBy}|{writer.IsVOrdered}|{writer.VOrderLevel}|{writer.FileCount}|{writer.FileSize}")
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
        foreach (JsonObject file in dump.Files)
        {
            WriterRow? writer = Doc.Summary.Writers.FindById(FileOf(table, file).Writer);
            Assert.NotNull(writer);
            Assert.Equal(Dump.IsVOrdered(file), writer.IsVOrdered);
        }
    }

    [Theory]
    [MemberData(nameof(TableIds))]
    public void Operations_and_commits_are_those_of_the_commits_read(string id)
    {
        DumpTable dump = DumpOf(id);
        TableRow table = Fixture.TableOf(Doc, dump);
        var expectedOperations = dump.Commits.GroupBy(commit => (string)commit["operation"]!)
            .Select(group => $"{group.Key}|{group.Count()}|{group.Max(commit => Dump.Time(commit["timestamp"])):O}")
            .Order(StringComparer.Ordinal);
        var actualOperations = Doc.Summary.Operations.Where(operation => operation.Table == table.Id)
            .Select(operation => $"{operation.Name}|{operation.CommitCount}|{operation.LastAt:O}")
            .Order(StringComparer.Ordinal);
        Assert.Equal(expectedOperations, actualOperations);

        List<CommitRow> commits = Doc.ReadCommits().Where(commit => commit.Table == table.Id).ToList();
        Assert.Equal(dump.Commits.Count, commits.Count);
        foreach (JsonObject expected in dump.Commits)
        {
            CommitRow commit = commits.Single(row => row.CommitId == Dump.Long(expected["version"]).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(Dump.Time(expected["timestamp"]), commit.Timestamp);
            Assert.Equal(Dump.Text(expected["operation"]), commit.Operation);
            Assert.Equal(Dump.Text(expected["engine"]), commit.Engine);
            Assert.Equal(Dump.Bool(expected["isBlindAppend"]), commit.IsBlindAppend);
            Assert.Equal(Dump.Map(expected["metrics"]).OrderBy(pair => pair.Key), (commit.Metrics ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key));
            Assert.NotNull(commit.Metrics);
            Assert.Null(commit.Parameters);
        }
    }

    [Fact]
    public void No_issue_is_recorded_for_the_fixture()
    {
        Assert.Empty(Doc.Summary.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
    }
}
