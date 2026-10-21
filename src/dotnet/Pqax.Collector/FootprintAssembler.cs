using System;
using System.Collections.Generic;
using System.Linq;

namespace Pqax.Collector;

/// <summary>
/// Turns what was collected into the rows of a <see cref="FootprintBuilder"/>. This is the only class of the collector
/// that uses the builder and the row classes of the Pqax library. It adds the rows of each table together, so that
/// rows of the same parent stay together in every table of the file, and it turns positions into ids.
/// </summary>
internal static class FootprintAssembler
{
    public static FootprintBuilder Assemble(CollectionInfo collection, List<TableLocation> sources, IReadOnlyList<CollectedTable> tables, CollectorOptions options)
    {
        var builder = new FootprintBuilder(collection);
        var sourceIds = new long[sources.Count];
        for (int i = 0; i < sources.Count; i++)
        {
            TableLocation source = sources[i];
            sourceIds[i] = builder.AddSource(new SourceRow
            {
                Kind = source.SourceKind,
                Name = source.SourceName,
                Location = source.SourceLocation,
                Identifiers = source.SourceIdentifiers,
            });
        }

        foreach (CollectedTable table in tables)
        {
            AddTable(builder, table, sourceIds[table.Source], options);
        }

        return builder;
    }

    private static string DepthText(DetailDepth depth) => depth switch
    {
        DetailDepth.Summary => "Summary",
        DetailDepth.Files => "Files",
        DetailDepth.RowGroups => "Row groups",
        _ => "Column chunks",
    };

    /// <summary>Partition values for <see cref="PartitionRow.Values"/>, which is the one list of the format that allows a null element.</summary>
    private static IReadOnlyList<string> WithNulls(IReadOnlyList<string?> values)
    {
        var array = new string[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            array[i] = values[i]!;
        }

        return array;
    }

    private static Distribution? Distribution(CollectedDistribution? distribution) => distribution == null ? null : new Distribution
    {
        Count = distribution.Count,
        Sum = distribution.Sum,
        Minimum = distribution.Minimum,
        FirstQuartile = distribution.FirstQuartile,
        Median = distribution.Median,
        ThirdQuartile = distribution.ThirdQuartile,
        Maximum = distribution.Maximum,
    };

    private static void AddTable(FootprintBuilder builder, CollectedTable table, long sourceId, CollectorOptions options)
    {
        CollectedDelta? delta = table.Delta;
        var row = new TableRow
        {
            Source = sourceId,
            Namespace = table.Location.Namespace.ToArray(),
            Name = table.Location.Name,
            Location = table.Location.Location,
            Format = "Delta",
            TableId = table.TableId,
            Description = table.Description,
            CreatedAt = table.CreatedAt,
            Properties = table.Properties,
            SnapshotId = table.SnapshotId,
            SnapshotTimestamp = table.SnapshotTimestamp,
            Delta = delta == null ? null : new DeltaDetails
            {
                MinReaderVersion = delta.MinReaderVersion,
                MinWriterVersion = delta.MinWriterVersion,
                ReaderFeatures = delta.ReaderFeatures,
                WriterFeatures = delta.WriterFeatures,
                CheckpointVersion = delta.CheckpointVersion,
                CommitsAfterCheckpoint = delta.CommitsAfterCheckpoint,
                Domains = delta.Domains,
            },
            CollectedLevel = table.CollectedLevel,
            DetailDepth = DepthText(table.Depth),
            FileCount = table.FileCount,
            FileSize = table.FileSize,
            RowCount = table.RowCount,
            DeletedRowCount = table.DeletedRowCount,
            LastModifiedAt = table.LastModifiedAt,
            VOrderedFileCount = table.VOrderedFileCount,
            PartitionCount = table.PartitionCount,
            CommitCount = table.CommitCount,
            CommitsRead = table.CommitsRead,
            OldestCommitAt = table.OldestCommitAt,
            NonLiveFileCount = table.NonLiveFileCount,
            NonLiveFileSize = table.NonLiveFileSize,
            NonLiveOldestRemovedAt = table.NonLiveOldestRemovedAt,
            NonLiveNewestRemovedAt = table.NonLiveNewestRemovedAt,
            UnreferencedFileCount = table.UnreferencedFileCount,
            UnreferencedFileSize = table.UnreferencedFileSize,
            MetadataFileCount = table.MetadataFileCount,
            MetadataSize = table.MetadataSize,
            FooterCount = table.FooterCount,
            RowGroupCount = table.RowGroupCount,
            CompressedSize = table.CompressedSize,
            UncompressedSize = table.UncompressedSize,
            FooterSize = table.FooterSize,
            StaleColumnSize = table.StaleColumnSize,
            ColumnTotalsSource = table.ColumnTotalsSource,
            FileSizes = Distribution(table.FileSizes),
            RowGroupRowCounts = Distribution(table.RowGroupRowCounts),
            PartitionSizes = Distribution(table.PartitionSizes),
        };
        long tableId = builder.AddTable(row);

        var columnIds = new long[table.Columns.Count];
        for (int i = 0; i < table.Columns.Count; i++)
        {
            CollectedColumn column = table.Columns[i];
            columnIds[i] = builder.AddColumn(new ColumnRow
            {
                Table = tableId,
                FieldId = column.FieldId,
                Name = column.Name,
                DataType = column.DataType,
                IsNullable = column.IsNullable,
                PhysicalName = column.PhysicalName,
                IsPartitionColumn = column.IsPartitionColumn,
                Description = column.Description,
                CompressedSize = column.CompressedSize.Value,
                UncompressedSize = column.UncompressedSize.Value,
                ValueCount = column.ValueCount.Value,
                NullCount = column.NullCount.Value ?? column.LogNullCount,
                ChunkCount = column.ChunkCount.Value,
                DictionaryChunkCount = column.DictionaryChunkCount.Value,
                FallbackChunkCount = column.FallbackChunkCount.Value,
                DictionaryPageSize = column.DictionaryPageSize.Value,
                UnencodedByteArrayDataBytes = column.UnencodedByteArrayDataBytes.Value,
                Codecs = column.Codecs.Count > 0 ? column.Codecs.ToArray() : null,
                Encodings = column.Encodings.Count > 0 ? column.Encodings.ToArray() : null,
                FileCountWithoutColumn = column.FileCountWithoutColumn,
                LargestChunkDistinctCount = column.LargestChunkDistinctCount,
            });
        }

        // The builder keeps the row it was given, so the references to the columns, which have ids only now, go in here.
        if (table.ClusteringColumns != null)
        {
            row.ClusteringColumns = table.ClusteringColumns.Select(i => columnIds[i]).ToArray();
        }

        long? specId = null;
        if (table.PartitionFields != null)
        {
            specId = builder.AddPartitionSpec(new PartitionSpecRow { Table = tableId, IsDefault = true });
            for (int position = 0; position < table.PartitionFields.Count; position++)
            {
                CollectedPartitionField field = table.PartitionFields[position];
                builder.AddPartitionField(new PartitionFieldRow
                {
                    PartitionSpec = specId,
                    Position = position,
                    Name = field.Name,
                    SourceColumns = field.SourceColumn == null ? null : new[] { columnIds[field.SourceColumn.Value] },
                    Transform = "identity",
                });
            }
        }

        var schemaIds = new Dictionary<CollectedPhysicalSchema, long>();
        var physicalIds = new Dictionary<CollectedPhysicalSchema, long[]>();
        foreach (CollectedPhysicalSchema schema in table.PhysicalSchemas)
        {
            long schemaId = builder.AddPhysicalSchema(new PhysicalSchemaRow { Table = tableId, FileCount = schema.FileCount });
            schemaIds[schema] = schemaId;
            var ids = new long[schema.Columns.Count];
            for (int i = 0; i < schema.Columns.Count; i++)
            {
                CollectedPhysicalColumn physical = schema.Columns[i];
                ParquetLeafColumn leaf = physical.Leaf;
                ids[i] = builder.AddPhysicalColumn(new PhysicalColumnRow
                {
                    PhysicalSchema = schemaId,
                    Ordinal = leaf.Ordinal,
                    Path = leaf.Path.ToArray(),
                    PhysicalType = leaf.PhysicalType,
                    TypeLength = leaf.TypeLength,
                    LogicalType = leaf.TypeAnnotation,
                    Repetition = leaf.Repetition,
                    FieldId = leaf.FieldId,
                    Column = physical.Column == null ? null : columnIds[physical.Column.Value],
                    CompressedSize = physical.CompressedSize.Value,
                    UncompressedSize = physical.UncompressedSize.Value,
                    ChunkCount = physical.ChunkCount,
                });
            }

            physicalIds[schema] = ids;
        }

        var writerIds = new Dictionary<CollectedWriter, long>();
        foreach (CollectedWriter writer in table.Writers)
        {
            writerIds[writer] = builder.AddWriter(new WriterRow
            {
                Table = tableId,
                CreatedBy = writer.CreatedBy,
                IsVOrdered = writer.IsVOrdered,
                VOrderLevel = writer.VOrderLevel,
                FileCount = writer.FileCount,
                FileSize = writer.FileSize.Value,
            });
        }

        foreach (CollectedOperation operation in table.Operations)
        {
            builder.AddOperation(new OperationRow { Table = tableId, Name = operation.Name, CommitCount = operation.CommitCount, LastAt = operation.LastAt });
        }

        foreach (CollectedIssue issue in table.Issues)
        {
            builder.AddIssue(new IssueRow { Table = tableId, Path = issue.Path, Code = issue.Code, Message = issue.Message });
        }

        var partitionIds = new long[table.Partitions.Count];
        for (int i = 0; i < table.Partitions.Count; i++)
        {
            CollectedPartition partition = table.Partitions[i];
            partitionIds[i] = builder.AddPartition(new PartitionRow
            {
                Table = tableId,
                PartitionSpec = specId,
                Values = options.IncludePartitionValues ? WithNulls(partition.Values) : null,
                FileCount = partition.FileCount,
                FileSize = partition.FileSize.Value,
                RowCount = partition.RowCount.Value,
                DeletedRowCount = partition.DeletedRowCount.Value,
                LastModifiedAt = partition.LastModifiedAt,
            });
        }

        foreach (CollectedFile file in table.Files)
        {
            long fileId = builder.AddFile(new FileRow
            {
                Table = tableId,
                Path = file.Path,
                FileSize = file.FileSize,
                ModifiedAt = file.ModifiedAt,
                RowCount = file.RowCount,
                DeletedRowCount = file.DeletedRowCount,
                DeletionVectorSize = file.DeletionVectorSize,
                Partition = file.Partition == null ? null : partitionIds[file.Partition.Value],
                ClusteringProvider = file.ClusteringProvider,
                IsVOrdered = file.IsVOrdered,
                FooterSize = file.FooterSize,
                PhysicalSchema = file.PhysicalSchema == null ? null : schemaIds[file.PhysicalSchema],
                Writer = file.Writer == null ? null : writerIds[file.Writer],
                RowGroupCount = file.RowGroupCount,
                CompressedSize = file.CompressedSize,
                UncompressedSize = file.UncompressedSize,
            });

            if (table.Depth < DetailDepth.RowGroups || file.RowGroups == null || file.PhysicalSchema == null)
            {
                continue;
            }

            long[] leafIds = physicalIds[file.PhysicalSchema];
            foreach (CollectedRowGroup group in file.RowGroups)
            {
                long groupId = builder.AddRowGroup(new RowGroupRow
                {
                    File = fileId,
                    Ordinal = group.Ordinal,
                    RowCount = group.RowCount,
                    UncompressedSize = group.UncompressedSize,
                    CompressedSize = group.CompressedSize,
                });
                if (table.Depth < DetailDepth.ColumnChunks || group.Chunks == null)
                {
                    continue;
                }

                foreach (CollectedChunk chunk in group.Chunks)
                {
                    builder.AddColumnChunk(new ColumnChunkRow
                    {
                        RowGroup = groupId,
                        PhysicalColumn = leafIds[chunk.LeafOrdinal],
                        Codec = chunk.Codec,
                        Encodings = chunk.Encodings,
                        ValueCount = chunk.ValueCount,
                        NullCount = chunk.NullCount,
                        CompressedSize = chunk.CompressedSize,
                        UncompressedSize = chunk.UncompressedSize,
                        HasDictionaryPage = chunk.HasDictionaryPage,
                        DictionaryPageSize = chunk.DictionaryPageSize,
                        DataPageCount = chunk.DataPageCount,
                        DictionaryEncodedPageCount = chunk.DictionaryEncodedPageCount,
                        UnencodedByteArrayDataBytes = chunk.UnencodedByteArrayDataBytes,
                        BloomFilterSize = chunk.BloomFilterSize,
                        MinValue = options.IncludeDataValues ? chunk.MinValue : null,
                        MaxValue = options.IncludeDataValues ? chunk.MaxValue : null,
                        DistinctCount = chunk.DistinctCount,
                    });
                }
            }
        }

        foreach (CollectedCommit commit in table.Commits)
        {
            builder.AddCommit(new CommitRow
            {
                Table = tableId,
                CommitId = commit.CommitId,
                Timestamp = commit.Timestamp,
                Operation = commit.Operation,
                Engine = commit.Engine,
                IsBlindAppend = commit.IsBlindAppend,
                Metrics = commit.Metrics,
                Parameters = options.IncludeDataValues ? commit.Parameters : null,
            });
        }
    }
}
