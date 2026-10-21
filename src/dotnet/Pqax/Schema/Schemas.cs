using System;
using System.Collections.Generic;

namespace Pqax;

/// <summary>
/// Every table of the format and every nested object, with its fields in the order in which they are written.
/// The required fields follow the object model, plus the reference to the parent (except in <c>issues</c>) and the
/// <c>position</c> of serialization 5.3. The references follow section 4.4 of the rules of the conformance kit.
/// </summary>
internal static class Schemas
{
    public static readonly Schema<Distribution> DistributionSchema = new SchemaBuilder<Distribution>("distribution", false, () => new Distribution())
        .Integer("count", x => x.Count, (x, v) => x.Count = v)
        .Integer("sum", x => x.Sum, (x, v) => x.Sum = v)
        .Integer("minimum", x => x.Minimum, (x, v) => x.Minimum = v)
        .Integer("firstQuartile", x => x.FirstQuartile, (x, v) => x.FirstQuartile = v)
        .Integer("median", x => x.Median, (x, v) => x.Median = v)
        .Integer("thirdQuartile", x => x.ThirdQuartile, (x, v) => x.ThirdQuartile = v)
        .Integer("maximum", x => x.Maximum, (x, v) => x.Maximum = v)
        .Extensions(x => x.Extensions, (x, v) => x.Extensions = v)
        .Build();

    public static readonly Schema<DeltaDetails> DeltaSchema = new SchemaBuilder<DeltaDetails>("delta", false, () => new DeltaDetails())
        .Integer("minReaderVersion", x => x.MinReaderVersion, (x, v) => x.MinReaderVersion = v)
        .Integer("minWriterVersion", x => x.MinWriterVersion, (x, v) => x.MinWriterVersion = v)
        .StringList("readerFeatures", x => x.ReaderFeatures, (x, v) => x.ReaderFeatures = v)
        .StringList("writerFeatures", x => x.WriterFeatures, (x, v) => x.WriterFeatures = v)
        .Integer("checkpointVersion", x => x.CheckpointVersion, (x, v) => x.CheckpointVersion = v)
        .Integer("commitsAfterCheckpoint", x => x.CommitsAfterCheckpoint, (x, v) => x.CommitsAfterCheckpoint = v)
        .StringList("domains", x => x.Domains, (x, v) => x.Domains = v)
        .Extensions(x => x.Extensions, (x, v) => x.Extensions = v)
        .Build();

    public static readonly Schema<IcebergDetails> IcebergSchema = new SchemaBuilder<IcebergDetails>("iceberg", false, () => new IcebergDetails())
        .Integer("formatVersion", x => x.FormatVersion, (x, v) => x.FormatVersion = v)
        .Integer("currentSchemaId", x => x.CurrentSchemaId, (x, v) => x.CurrentSchemaId = v)
        .Integer("schemaCount", x => x.SchemaCount, (x, v) => x.SchemaCount = v)
        .Integer("snapshotCount", x => x.SnapshotCount, (x, v) => x.SnapshotCount = v)
        .Integer("refCount", x => x.RefCount, (x, v) => x.RefCount = v)
        .Integer("manifestCount", x => x.ManifestCount, (x, v) => x.ManifestCount = v)
        .Integer("manifestSize", x => x.ManifestSize, (x, v) => x.ManifestSize = v)
        .Integer("statisticsFileCount", x => x.StatisticsFileCount, (x, v) => x.StatisticsFileCount = v)
        .Extensions(x => x.Extensions, (x, v) => x.Extensions = v)
        .Build();

    public static readonly Schema<CollectionInfo> CollectionSchema = new SchemaBuilder<CollectionInfo>("collection", false, () => new CollectionInfo())
        .String("collectorApp", x => x.CollectorApp, (x, v) => x.CollectorApp = v)
        .String("collectorAppVersion", x => x.CollectorAppVersion, (x, v) => x.CollectorAppVersion = v)
        .String("collectorLib", x => x.CollectorLib, (x, v) => x.CollectorLib = v)
        .String("collectorLibVersion", x => x.CollectorLibVersion, (x, v) => x.CollectorLibVersion = v)
        .Timestamp("startedAt", x => x.StartedAt, (x, v) => x.StartedAt = v)
        .Timestamp("completedAt", x => x.CompletedAt, (x, v) => x.CompletedAt = v)
        .Integer("level", x => x.Level, (x, v) => x.Level = v, required: true)
        .Boolean("includesDataValues", x => x.IncludesDataValues, (x, v) => x.IncludesDataValues = v)
        .Boolean("includesPartitionValues", x => x.IncludesPartitionValues, (x, v) => x.IncludesPartitionValues = v)
        .Integer("requestCount", x => x.RequestCount, (x, v) => x.RequestCount = v)
        .Integer("bytesRead", x => x.BytesRead, (x, v) => x.BytesRead = v)
        .Extensions(x => x.Extensions, (x, v) => x.Extensions = v)
        .Build();

    public static readonly Schema<SourceRow> Sources = new SchemaBuilder<SourceRow>("sources", true, () => new SourceRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .String("kind", x => x.Kind, (x, v) => x.Kind = v, required: true)
        .String("name", x => x.Name, (x, v) => x.Name = v)
        .String("location", x => x.Location, (x, v) => x.Location = v)
        .Map("identifiers", x => x.Identifiers, (x, v) => x.Identifiers = v)
        .Build();

    public static readonly Schema<TableRow> Tables = new SchemaBuilder<TableRow>("tables", true, () => new TableRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .Reference("source", "sources", x => x.Source, (x, v) => x.Source = v)
        .StringList("namespace", x => x.Namespace, (x, v) => x.Namespace = v)
        .String("name", x => x.Name, (x, v) => x.Name = v, required: true)
        .String("location", x => x.Location, (x, v) => x.Location = v)
        .String("format", x => x.Format, (x, v) => x.Format = v, required: true)
        .String("tableId", x => x.TableId, (x, v) => x.TableId = v)
        .String("description", x => x.Description, (x, v) => x.Description = v)
        .Timestamp("createdAt", x => x.CreatedAt, (x, v) => x.CreatedAt = v)
        .Map("properties", x => x.Properties, (x, v) => x.Properties = v)
        .String("snapshotId", x => x.SnapshotId, (x, v) => x.SnapshotId = v)
        .Timestamp("snapshotTimestamp", x => x.SnapshotTimestamp, (x, v) => x.SnapshotTimestamp = v)
        .Object("delta", DeltaSchema, x => x.Delta, (x, v) => x.Delta = v)
        .Object("iceberg", IcebergSchema, x => x.Iceberg, (x, v) => x.Iceberg = v)
        .ReferenceList("clusteringColumns", "columns", x => x.ClusteringColumns, (x, v) => x.ClusteringColumns = v)
        .Integer("collectedLevel", x => x.CollectedLevel, (x, v) => x.CollectedLevel = v)
        .String("detailDepth", x => x.DetailDepth, (x, v) => x.DetailDepth = v, required: true)
        .Integer("fileCount", x => x.FileCount, (x, v) => x.FileCount = v)
        .Integer("fileSize", x => x.FileSize, (x, v) => x.FileSize = v)
        .Integer("rowCount", x => x.RowCount, (x, v) => x.RowCount = v)
        .Integer("deletedRowCount", x => x.DeletedRowCount, (x, v) => x.DeletedRowCount = v)
        .Timestamp("lastModifiedAt", x => x.LastModifiedAt, (x, v) => x.LastModifiedAt = v)
        .Integer("vOrderedFileCount", x => x.VOrderedFileCount, (x, v) => x.VOrderedFileCount = v)
        .Integer("partitionCount", x => x.PartitionCount, (x, v) => x.PartitionCount = v)
        .Integer("deleteFileCount", x => x.DeleteFileCount, (x, v) => x.DeleteFileCount = v)
        .Integer("deleteFileSize", x => x.DeleteFileSize, (x, v) => x.DeleteFileSize = v)
        .Integer("commitCount", x => x.CommitCount, (x, v) => x.CommitCount = v)
        .Integer("commitsRead", x => x.CommitsRead, (x, v) => x.CommitsRead = v)
        .Timestamp("oldestCommitAt", x => x.OldestCommitAt, (x, v) => x.OldestCommitAt = v)
        .Integer("nonLiveFileCount", x => x.NonLiveFileCount, (x, v) => x.NonLiveFileCount = v)
        .Integer("nonLiveFileSize", x => x.NonLiveFileSize, (x, v) => x.NonLiveFileSize = v)
        .Timestamp("nonLiveOldestRemovedAt", x => x.NonLiveOldestRemovedAt, (x, v) => x.NonLiveOldestRemovedAt = v)
        .Timestamp("nonLiveNewestRemovedAt", x => x.NonLiveNewestRemovedAt, (x, v) => x.NonLiveNewestRemovedAt = v)
        .Integer("unreferencedFileCount", x => x.UnreferencedFileCount, (x, v) => x.UnreferencedFileCount = v)
        .Integer("unreferencedFileSize", x => x.UnreferencedFileSize, (x, v) => x.UnreferencedFileSize = v)
        .Integer("metadataFileCount", x => x.MetadataFileCount, (x, v) => x.MetadataFileCount = v)
        .Integer("metadataSize", x => x.MetadataSize, (x, v) => x.MetadataSize = v)
        .Integer("footerCount", x => x.FooterCount, (x, v) => x.FooterCount = v)
        .Integer("rowGroupCount", x => x.RowGroupCount, (x, v) => x.RowGroupCount = v)
        .Integer("compressedSize", x => x.CompressedSize, (x, v) => x.CompressedSize = v)
        .Integer("uncompressedSize", x => x.UncompressedSize, (x, v) => x.UncompressedSize = v)
        .Integer("footerSize", x => x.FooterSize, (x, v) => x.FooterSize = v)
        .Integer("staleColumnSize", x => x.StaleColumnSize, (x, v) => x.StaleColumnSize = v)
        .String("columnTotalsSource", x => x.ColumnTotalsSource, (x, v) => x.ColumnTotalsSource = v)
        .Object("fileSizes", DistributionSchema, x => x.FileSizes, (x, v) => x.FileSizes = v)
        .Object("rowGroupRowCounts", DistributionSchema, x => x.RowGroupRowCounts, (x, v) => x.RowGroupRowCounts = v)
        .Object("partitionSizes", DistributionSchema, x => x.PartitionSizes, (x, v) => x.PartitionSizes = v)
        .Build();

    public static readonly Schema<ColumnRow> Columns = new SchemaBuilder<ColumnRow>("columns", true, () => new ColumnRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .Reference("table", "tables", x => x.Table, (x, v) => x.Table = v, required: true)
        .Integer("fieldId", x => x.FieldId, (x, v) => x.FieldId = v)
        .String("name", x => x.Name, (x, v) => x.Name = v, required: true)
        .String("dataType", x => x.DataType, (x, v) => x.DataType = v, required: true)
        .Boolean("isNullable", x => x.IsNullable, (x, v) => x.IsNullable = v)
        .String("physicalName", x => x.PhysicalName, (x, v) => x.PhysicalName = v)
        .Boolean("isPartitionColumn", x => x.IsPartitionColumn, (x, v) => x.IsPartitionColumn = v)
        .String("description", x => x.Description, (x, v) => x.Description = v)
        .Integer("compressedSize", x => x.CompressedSize, (x, v) => x.CompressedSize = v)
        .Integer("uncompressedSize", x => x.UncompressedSize, (x, v) => x.UncompressedSize = v)
        .Integer("valueCount", x => x.ValueCount, (x, v) => x.ValueCount = v)
        .Integer("nullCount", x => x.NullCount, (x, v) => x.NullCount = v)
        .Integer("chunkCount", x => x.ChunkCount, (x, v) => x.ChunkCount = v)
        .Integer("dictionaryChunkCount", x => x.DictionaryChunkCount, (x, v) => x.DictionaryChunkCount = v)
        .Integer("fallbackChunkCount", x => x.FallbackChunkCount, (x, v) => x.FallbackChunkCount = v)
        .Integer("dictionaryPageSize", x => x.DictionaryPageSize, (x, v) => x.DictionaryPageSize = v)
        .Integer("unencodedByteArrayDataBytes", x => x.UnencodedByteArrayDataBytes, (x, v) => x.UnencodedByteArrayDataBytes = v)
        .StringList("codecs", x => x.Codecs, (x, v) => x.Codecs = v)
        .StringList("encodings", x => x.Encodings, (x, v) => x.Encodings = v)
        .Integer("fileCountWithoutColumn", x => x.FileCountWithoutColumn, (x, v) => x.FileCountWithoutColumn = v)
        .Integer("largestChunkDistinctCount", x => x.LargestChunkDistinctCount, (x, v) => x.LargestChunkDistinctCount = v)
        .Integer("distinctCount", x => x.DistinctCount, (x, v) => x.DistinctCount = v)
        .Boolean("distinctCountIsEstimate", x => x.DistinctCountIsEstimate, (x, v) => x.DistinctCountIsEstimate = v)
        .String("distinctCountSource", x => x.DistinctCountSource, (x, v) => x.DistinctCountSource = v)
        .Build();

    public static readonly Schema<PartitionSpecRow> PartitionSpecs = new SchemaBuilder<PartitionSpecRow>("partitionSpecs", true, () => new PartitionSpecRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .Reference("table", "tables", x => x.Table, (x, v) => x.Table = v, required: true)
        .Integer("specId", x => x.SpecId, (x, v) => x.SpecId = v)
        .Boolean("isDefault", x => x.IsDefault, (x, v) => x.IsDefault = v)
        .Build();

    public static readonly Schema<PartitionFieldRow> PartitionFields = new SchemaBuilder<PartitionFieldRow>("partitionFields", true, () => new PartitionFieldRow())
        .Reference("partitionSpec", "partitionSpecs", x => x.PartitionSpec, (x, v) => x.PartitionSpec = v, required: true)
        .Integer("position", x => x.Position, (x, v) => x.Position = v, required: true)
        .String("name", x => x.Name, (x, v) => x.Name = v)
        .ReferenceList("sourceColumns", "columns", x => x.SourceColumns, (x, v) => x.SourceColumns = v)
        .String("transform", x => x.Transform, (x, v) => x.Transform = v)
        .Build();

    public static readonly Schema<SortOrderRow> SortOrders = new SchemaBuilder<SortOrderRow>("sortOrders", true, () => new SortOrderRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .Reference("table", "tables", x => x.Table, (x, v) => x.Table = v, required: true)
        .Integer("orderId", x => x.OrderId, (x, v) => x.OrderId = v)
        .Boolean("isDefault", x => x.IsDefault, (x, v) => x.IsDefault = v)
        .Build();

    public static readonly Schema<SortFieldRow> SortFields = new SchemaBuilder<SortFieldRow>("sortFields", true, () => new SortFieldRow())
        .Reference("sortOrder", "sortOrders", x => x.SortOrder, (x, v) => x.SortOrder = v, required: true)
        .Integer("position", x => x.Position, (x, v) => x.Position = v, required: true)
        .Reference("sourceColumn", "columns", x => x.SourceColumn, (x, v) => x.SourceColumn = v, required: true)
        .String("transform", x => x.Transform, (x, v) => x.Transform = v)
        .String("direction", x => x.Direction, (x, v) => x.Direction = v, required: true)
        .String("nullOrder", x => x.NullOrder, (x, v) => x.NullOrder = v)
        .Build();

    public static readonly Schema<PhysicalSchemaRow> PhysicalSchemas = new SchemaBuilder<PhysicalSchemaRow>("physicalSchemas", true, () => new PhysicalSchemaRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .Reference("table", "tables", x => x.Table, (x, v) => x.Table = v, required: true)
        .Integer("fileCount", x => x.FileCount, (x, v) => x.FileCount = v)
        .Build();

    public static readonly Schema<PhysicalColumnRow> PhysicalColumns = new SchemaBuilder<PhysicalColumnRow>("physicalColumns", true, () => new PhysicalColumnRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .Reference("physicalSchema", "physicalSchemas", x => x.PhysicalSchema, (x, v) => x.PhysicalSchema = v, required: true)
        .Integer("ordinal", x => x.Ordinal, (x, v) => x.Ordinal = v, required: true)
        .StringList("path", x => x.Path, (x, v) => x.Path = v, required: true)
        .String("physicalType", x => x.PhysicalType, (x, v) => x.PhysicalType = v, required: true)
        .Integer("typeLength", x => x.TypeLength, (x, v) => x.TypeLength = v)
        .String("logicalType", x => x.LogicalType, (x, v) => x.LogicalType = v)
        .String("repetition", x => x.Repetition, (x, v) => x.Repetition = v)
        .Integer("fieldId", x => x.FieldId, (x, v) => x.FieldId = v)
        .Reference("column", "columns", x => x.Column, (x, v) => x.Column = v)
        .Integer("compressedSize", x => x.CompressedSize, (x, v) => x.CompressedSize = v)
        .Integer("uncompressedSize", x => x.UncompressedSize, (x, v) => x.UncompressedSize = v)
        .Integer("chunkCount", x => x.ChunkCount, (x, v) => x.ChunkCount = v)
        .Build();

    public static readonly Schema<WriterRow> Writers = new SchemaBuilder<WriterRow>("writers", true, () => new WriterRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .Reference("table", "tables", x => x.Table, (x, v) => x.Table = v, required: true)
        .String("createdBy", x => x.CreatedBy, (x, v) => x.CreatedBy = v)
        .Boolean("isVOrdered", x => x.IsVOrdered, (x, v) => x.IsVOrdered = v)
        .String("vOrderLevel", x => x.VOrderLevel, (x, v) => x.VOrderLevel = v)
        .Integer("fileCount", x => x.FileCount, (x, v) => x.FileCount = v)
        .Integer("fileSize", x => x.FileSize, (x, v) => x.FileSize = v)
        .Build();

    public static readonly Schema<OperationRow> Operations = new SchemaBuilder<OperationRow>("operations", true, () => new OperationRow())
        .Reference("table", "tables", x => x.Table, (x, v) => x.Table = v, required: true)
        .String("name", x => x.Name, (x, v) => x.Name = v, required: true)
        .Integer("commitCount", x => x.CommitCount, (x, v) => x.CommitCount = v)
        .Timestamp("lastAt", x => x.LastAt, (x, v) => x.LastAt = v)
        .Build();

    public static readonly Schema<IssueRow> Issues = new SchemaBuilder<IssueRow>("issues", true, () => new IssueRow())
        .Reference("table", "tables", x => x.Table, (x, v) => x.Table = v)
        .String("path", x => x.Path, (x, v) => x.Path = v)
        .String("code", x => x.Code, (x, v) => x.Code = v, required: true)
        .String("message", x => x.Message, (x, v) => x.Message = v)
        .Build();

    public static readonly Schema<ModelBindingRow> ModelBindings = new SchemaBuilder<ModelBindingRow>("modelBindings", true, () => new ModelBindingRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .String("modelName", x => x.ModelName, (x, v) => x.ModelName = v, required: true)
        .String("serverName", x => x.ServerName, (x, v) => x.ServerName = v)
        .Build();

    public static readonly Schema<TableBindingRow> TableBindings = new SchemaBuilder<TableBindingRow>("tableBindings", true, () => new TableBindingRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .Reference("modelBinding", "modelBindings", x => x.ModelBinding, (x, v) => x.ModelBinding = v, required: true)
        .String("modelTable", x => x.ModelTable, (x, v) => x.ModelTable = v, required: true)
        .Reference("table", "tables", x => x.Table, (x, v) => x.Table = v, required: true)
        .Build();

    public static readonly Schema<ColumnBindingRow> ColumnBindings = new SchemaBuilder<ColumnBindingRow>("columnBindings", true, () => new ColumnBindingRow())
        .Reference("tableBinding", "tableBindings", x => x.TableBinding, (x, v) => x.TableBinding = v, required: true)
        .String("modelColumn", x => x.ModelColumn, (x, v) => x.ModelColumn = v, required: true)
        .Reference("column", "columns", x => x.Column, (x, v) => x.Column = v, required: true)
        .Build();

    public static readonly Schema<PartitionRow> Partitions = new SchemaBuilder<PartitionRow>("partitions", false, () => new PartitionRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .Reference("table", "tables", x => x.Table, (x, v) => x.Table = v, required: true)
        .Reference("partitionSpec", "partitionSpecs", x => x.PartitionSpec, (x, v) => x.PartitionSpec = v)
        .StringList("values", x => x.Values, (x, v) => x.Values = v)
        .Integer("fileCount", x => x.FileCount, (x, v) => x.FileCount = v)
        .Integer("fileSize", x => x.FileSize, (x, v) => x.FileSize = v)
        .Integer("rowCount", x => x.RowCount, (x, v) => x.RowCount = v)
        .Integer("deletedRowCount", x => x.DeletedRowCount, (x, v) => x.DeletedRowCount = v)
        .Timestamp("lastModifiedAt", x => x.LastModifiedAt, (x, v) => x.LastModifiedAt = v)
        .Build();

    public static readonly Schema<FileRow> Files = new SchemaBuilder<FileRow>("files", false, () => new FileRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .Reference("table", "tables", x => x.Table, (x, v) => x.Table = v, required: true)
        .String("path", x => x.Path, (x, v) => x.Path = v)
        .String("format", x => x.Format, (x, v) => x.Format = v)
        .Integer("fileSize", x => x.FileSize, (x, v) => x.FileSize = v)
        .Timestamp("modifiedAt", x => x.ModifiedAt, (x, v) => x.ModifiedAt = v)
        .Integer("rowCount", x => x.RowCount, (x, v) => x.RowCount = v)
        .Integer("deletedRowCount", x => x.DeletedRowCount, (x, v) => x.DeletedRowCount = v)
        .Integer("deletionVectorSize", x => x.DeletionVectorSize, (x, v) => x.DeletionVectorSize = v)
        .Reference("partition", "partitions", x => x.Partition, (x, v) => x.Partition = v)
        .Reference("sortOrder", "sortOrders", x => x.SortOrder, (x, v) => x.SortOrder = v)
        .String("clusteringProvider", x => x.ClusteringProvider, (x, v) => x.ClusteringProvider = v)
        .Boolean("isVOrdered", x => x.IsVOrdered, (x, v) => x.IsVOrdered = v)
        .Integer("footerSize", x => x.FooterSize, (x, v) => x.FooterSize = v)
        .Reference("physicalSchema", "physicalSchemas", x => x.PhysicalSchema, (x, v) => x.PhysicalSchema = v)
        .Reference("writer", "writers", x => x.Writer, (x, v) => x.Writer = v)
        .Integer("rowGroupCount", x => x.RowGroupCount, (x, v) => x.RowGroupCount = v)
        .Integer("compressedSize", x => x.CompressedSize, (x, v) => x.CompressedSize = v)
        .Integer("uncompressedSize", x => x.UncompressedSize, (x, v) => x.UncompressedSize = v)
        .Build();

    public static readonly Schema<RowGroupRow> RowGroups = new SchemaBuilder<RowGroupRow>("rowGroups", false, () => new RowGroupRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .Reference("file", "files", x => x.File, (x, v) => x.File = v, required: true)
        .Integer("ordinal", x => x.Ordinal, (x, v) => x.Ordinal = v, required: true)
        .Integer("rowCount", x => x.RowCount, (x, v) => x.RowCount = v, required: true)
        .Integer("uncompressedSize", x => x.UncompressedSize, (x, v) => x.UncompressedSize = v)
        .Integer("compressedSize", x => x.CompressedSize, (x, v) => x.CompressedSize = v)
        .Build();

    public static readonly Schema<ColumnChunkRow> ColumnChunks = new SchemaBuilder<ColumnChunkRow>("columnChunks", false, () => new ColumnChunkRow())
        .Reference("rowGroup", "rowGroups", x => x.RowGroup, (x, v) => x.RowGroup = v, required: true)
        .Reference("physicalColumn", "physicalColumns", x => x.PhysicalColumn, (x, v) => x.PhysicalColumn = v, required: true)
        .String("codec", x => x.Codec, (x, v) => x.Codec = v)
        .StringList("encodings", x => x.Encodings, (x, v) => x.Encodings = v)
        .Integer("valueCount", x => x.ValueCount, (x, v) => x.ValueCount = v)
        .Integer("nullCount", x => x.NullCount, (x, v) => x.NullCount = v)
        .Integer("compressedSize", x => x.CompressedSize, (x, v) => x.CompressedSize = v, required: true)
        .Integer("uncompressedSize", x => x.UncompressedSize, (x, v) => x.UncompressedSize = v, required: true)
        .Boolean("hasDictionaryPage", x => x.HasDictionaryPage, (x, v) => x.HasDictionaryPage = v)
        .Integer("dictionaryPageSize", x => x.DictionaryPageSize, (x, v) => x.DictionaryPageSize = v)
        .Integer("dataPageCount", x => x.DataPageCount, (x, v) => x.DataPageCount = v)
        .Integer("dictionaryEncodedPageCount", x => x.DictionaryEncodedPageCount, (x, v) => x.DictionaryEncodedPageCount = v)
        .Integer("unencodedByteArrayDataBytes", x => x.UnencodedByteArrayDataBytes, (x, v) => x.UnencodedByteArrayDataBytes = v)
        .Integer("bloomFilterSize", x => x.BloomFilterSize, (x, v) => x.BloomFilterSize = v)
        .String("minValue", x => x.MinValue, (x, v) => x.MinValue = v)
        .String("maxValue", x => x.MaxValue, (x, v) => x.MaxValue = v)
        .Integer("distinctCount", x => x.DistinctCount, (x, v) => x.DistinctCount = v)
        .Integer("dictionaryEntryCount", x => x.DictionaryEntryCount, (x, v) => x.DictionaryEntryCount = v)
        .Build();

    public static readonly Schema<DeleteFileRow> DeleteFiles = new SchemaBuilder<DeleteFileRow>("deleteFiles", false, () => new DeleteFileRow())
        .Id(x => x.Id, (x, v) => x.Id = v)
        .Reference("table", "tables", x => x.Table, (x, v) => x.Table = v, required: true)
        .String("kind", x => x.Kind, (x, v) => x.Kind = v, required: true)
        .String("path", x => x.Path, (x, v) => x.Path = v)
        .String("format", x => x.Format, (x, v) => x.Format = v)
        .Integer("fileSize", x => x.FileSize, (x, v) => x.FileSize = v)
        .Integer("recordCount", x => x.RecordCount, (x, v) => x.RecordCount = v)
        .Reference("referencedFile", "files", x => x.ReferencedFile, (x, v) => x.ReferencedFile = v)
        .ReferenceList("equalityColumns", "columns", x => x.EqualityColumns, (x, v) => x.EqualityColumns = v)
        .Reference("partition", "partitions", x => x.Partition, (x, v) => x.Partition = v)
        .Build();

    public static readonly Schema<CommitRow> Commits = new SchemaBuilder<CommitRow>("commits", false, () => new CommitRow())
        .Reference("table", "tables", x => x.Table, (x, v) => x.Table = v, required: true)
        .String("commitId", x => x.CommitId, (x, v) => x.CommitId = v, required: true)
        .String("parentId", x => x.ParentId, (x, v) => x.ParentId = v)
        .Timestamp("timestamp", x => x.Timestamp, (x, v) => x.Timestamp = v)
        .String("operation", x => x.Operation, (x, v) => x.Operation = v)
        .String("engine", x => x.Engine, (x, v) => x.Engine = v)
        .Boolean("isBlindAppend", x => x.IsBlindAppend, (x, v) => x.IsBlindAppend = v)
        .Map("metrics", x => x.Metrics, (x, v) => x.Metrics = v)
        .Map("parameters", x => x.Parameters, (x, v) => x.Parameters = v)
        .Build();

    /// <summary>The fifteen summary tables and the six detail tables, in the order in which a writer writes them.</summary>
    public static readonly IReadOnlyList<TableInfo> AllTables = new[]
    {
        Sources.Info, Tables.Info, Columns.Info, PartitionSpecs.Info, PartitionFields.Info, SortOrders.Info, SortFields.Info,
        PhysicalSchemas.Info, PhysicalColumns.Info, Writers.Info, Operations.Info, Issues.Info, ModelBindings.Info,
        TableBindings.Info, ColumnBindings.Info, Partitions.Info, Files.Info, RowGroups.Info, ColumnChunks.Info,
        DeleteFiles.Info, Commits.Info,
    };

    private static readonly Dictionary<string, TableInfo> ByName = CreateIndex();

    /// <summary>The table of the format with this name, or null when this version does not define it.</summary>
    public static TableInfo? FindTable(string name) => ByName.TryGetValue(name, out TableInfo? table) ? table : null;

    public static bool IsKnownTable(string name) => ByName.ContainsKey(name);

    private static Dictionary<string, TableInfo> CreateIndex()
    {
        var index = new Dictionary<string, TableInfo>(StringComparer.Ordinal);
        foreach (TableInfo table in AllTables)
        {
            index.Add(table.Name, table);
        }

        return index;
    }
}
