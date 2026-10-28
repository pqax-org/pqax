// The types of the manifest and of the 21 tables, from the object model and the serialization. A table is columnar: one
// array for each field, with one value for each row. A field that a part leaves out is undefined, and a missing value is
// null. The doc comments say which fields the object model requires, but every field is optional in the types, because
// a table that the file does not have is an empty table with no fields.

/** The manifest of a PQAX file, `manifest.json`. Members that this version does not know are kept on the object. */
export interface Manifest {
  /** Always `pqax`. */
  readonly format: string;
  /** The version of the format: two numbers separated by a dot, such as `0.1`. */
  readonly formatVersion: string;
  /** How the footprint was collected. */
  readonly collection: CollectionInfo;
  /** Set when names were obfuscated, as in VPAX. */
  readonly obfuscatorDictionaryId?: string;
  /** The library that obfuscated the names. */
  readonly obfuscatorLib?: string;
  /** The version of the library that obfuscated the names. */
  readonly obfuscatorLibVersion?: string;
  /** The parts of the file, in order. The first is `summary.json`. */
  readonly parts: readonly PartInfo[];
}

/** The `Collection` object of the model: what was asked of the collector and what it did. */
export interface CollectionInfo {
  /** The application that ran the collection. */
  readonly collectorApp?: string;
  /** The version of the application that ran the collection. */
  readonly collectorAppVersion?: string;
  /** The library that implemented the collection. */
  readonly collectorLib?: string;
  /** The version of the library that implemented the collection. */
  readonly collectorLibVersion?: string;
  /** When the collection started, as `2026-10-05T08:15:30.000Z`. */
  readonly startedAt?: string;
  /** When the collection completed, as `2026-10-05T08:15:30.000Z`. */
  readonly completedAt?: string;
  /** The highest collection level requested, 1 to 4. Required. */
  readonly level: number;
  /** Whether minimum and maximum values were collected. */
  readonly includesDataValues?: boolean;
  /** Whether partition values were collected. */
  readonly includesPartitionValues?: boolean;
  /** The storage requests made. */
  readonly requestCount?: number;
  /** The bytes read from storage. */
  readonly bytesRead?: number;
}

/**
 * A part of the file, as the manifest lists it. Members that this version does not know, such as those that a producer
 * adds, are kept on the object.
 */
export interface PartInfo {
  /** The name of the zip entry. */
  readonly name: string;
  /** The number of rows that the part holds for each table. */
  readonly tables: Readonly<Record<string, number>>;
}

/** The shape of a set of values: count, sum, extremes, and quartiles. */
export interface Distribution {
  /** The number of values. */
  readonly count?: number | null;
  /** Their sum. */
  readonly sum?: number | null;
  /** The smallest value. */
  readonly minimum?: number | null;
  /** The largest value. */
  readonly maximum?: number | null;
  /** The first quartile. */
  readonly firstQuartile?: number | null;
  /** The median. */
  readonly median?: number | null;
  /** The third quartile. */
  readonly thirdQuartile?: number | null;
}

/** What only a Delta table has. */
export interface DeltaDetails {
  /** The minimum reader version of the protocol. */
  readonly minReaderVersion?: number | null;
  /** The minimum writer version of the protocol. */
  readonly minWriterVersion?: number | null;
  /** The reader features of the table. */
  readonly readerFeatures?: string[] | null;
  /** The writer features of the table. */
  readonly writerFeatures?: string[] | null;
  /** The version of the last checkpoint. */
  readonly checkpointVersion?: number | null;
  /** The commit files newer than the last checkpoint. */
  readonly commitsAfterCheckpoint?: number | null;
  /** The names of the metadata domains in use. */
  readonly domains?: string[] | null;
}

/** What only an Iceberg table has. */
export interface IcebergDetails {
  /** The version of the table format. */
  readonly formatVersion?: number | null;
  /** The schema that the columns describe. */
  readonly currentSchemaId?: number | null;
  /** The schemas that the metadata still lists. */
  readonly schemaCount?: number | null;
  /** The snapshots that the metadata still lists. */
  readonly snapshotCount?: number | null;
  /** The branches and tags. */
  readonly refCount?: number | null;
  /** The manifests of the current snapshot. */
  readonly manifestCount?: number | null;
  /** The size of the manifests of the current snapshot. */
  readonly manifestSize?: number | null;
  /** The statistics files attached to the table. */
  readonly statisticsFileCount?: number | null;
}

/**
 * A table in columnar form: one array for each field, each with one value for each row, and the number of rows. A
 * field that the file does not have is undefined, and a missing value is null.
 */
export interface ColumnarTable {
  /** The number of rows. */
  readonly length: number;
}

/** `sources`: one row for each lakehouse, warehouse, catalog, or storage folder. */
export interface SourcesTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** For example `OneLake lakehouse` or `Storage folder`. Required. */
  readonly kind?: (string | null)[];
  /** The name of the lakehouse, warehouse, or catalog. */
  readonly name?: (string | null)[];
  /** The base location of the source. */
  readonly location?: (string | null)[];
  /** Identifiers that the platform assigns, such as a workspace id and an item id. */
  readonly identifiers?: (Record<string, string> | null)[];
}

/** `tables`: one row for each table, at the version described, with its totals. */
export interface TablesTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The source that holds the table: an `id` of `sources`. */
  readonly source?: (number | null)[];
  /** The schema or namespace path of the table. */
  readonly namespace?: (string[] | null)[];
  /** The table name. Required. */
  readonly name?: (string | null)[];
  /** The root location of the table. */
  readonly location?: (string | null)[];
  /** `Delta`, `Iceberg`, or `Parquet`. Required. */
  readonly format?: (string | null)[];
  /** An identifier that stays the same across versions of the table. */
  readonly tableId?: (string | null)[];
  /** The description of the table. */
  readonly description?: (string | null)[];
  /** When the table was created. */
  readonly createdAt?: (string | null)[];
  /** The table properties. */
  readonly properties?: (Record<string, string> | null)[];
  /** The version of the table that the footprint describes. */
  readonly snapshotId?: (string | null)[];
  /** When that version was created. */
  readonly snapshotTimestamp?: (string | null)[];
  /** Present for Delta tables. */
  readonly delta?: (DeltaDetails | null)[];
  /** Present for Iceberg tables. */
  readonly iceberg?: (IcebergDetails | null)[];
  /** The columns of a Delta clustered table: `id`s of `columns`. */
  readonly clusteringColumns?: (number[] | null)[];
  /** The collection level actually reached for this table. */
  readonly collectedLevel?: (number | null)[];
  /** `Summary`, `Files`, `Row groups`, or `Column chunks`. Required. */
  readonly detailDepth?: (string | null)[];
  /** The data files of the version described. */
  readonly fileCount?: (number | null)[];
  /** The sum of their sizes. */
  readonly fileSize?: (number | null)[];
  /** The rows stored, including rows marked as deleted. */
  readonly rowCount?: (number | null)[];
  /** The rows marked as deleted. */
  readonly deletedRowCount?: (number | null)[];
  /** When the most recent data file was written. */
  readonly lastModifiedAt?: (string | null)[];
  /** The files that a file tag or the footer marks as V-Ordered. */
  readonly vOrderedFileCount?: (number | null)[];
  /** The distinct partitions that hold data. */
  readonly partitionCount?: (number | null)[];
  /** The delete files of an Iceberg table. */
  readonly deleteFileCount?: (number | null)[];
  /** The size of the delete files of an Iceberg table. */
  readonly deleteFileSize?: (number | null)[];
  /** The commits that the table format still keeps. */
  readonly commitCount?: (number | null)[];
  /** The most recent commits that the collector read. */
  readonly commitsRead?: (number | null)[];
  /** The time of the oldest of those commits. */
  readonly oldestCommitAt?: (string | null)[];
  /** Files that left the table and that the log still tracks. */
  readonly nonLiveFileCount?: (number | null)[];
  /** The size of those files. */
  readonly nonLiveFileSize?: (number | null)[];
  /** The earliest removal time of the non-live files. Absent when no remove action has a timestamp. Since format 0.2. */
  readonly nonLiveOldestRemovedAt?: (string | null)[];
  /** The latest removal time of the non-live files. Since format 0.2. */
  readonly nonLiveNewestRemovedAt?: (string | null)[];
  /** The data files under the table location that the version described does not reference. Since format 0.2. */
  readonly unreferencedFileCount?: (number | null)[];
  /** The sum of their sizes, as the storage listing gives them. Since format 0.2. */
  readonly unreferencedFileSize?: (number | null)[];
  /** The files of the table format itself. */
  readonly metadataFileCount?: (number | null)[];
  /** The size of the files of the table format itself. */
  readonly metadataSize?: (number | null)[];
  /** The files whose footer was read. */
  readonly footerCount?: (number | null)[];
  /** The row groups of those files. */
  readonly rowGroupCount?: (number | null)[];
  /** The sum of the compressed sizes of the column chunks. */
  readonly compressedSize?: (number | null)[];
  /** The sum of the uncompressed sizes of the column chunks. */
  readonly uncompressedSize?: (number | null)[];
  /** The sum of the footer sizes. */
  readonly footerSize?: (number | null)[];
  /** The compressed size of the physical columns that the table no longer declares. */
  readonly staleColumnSize?: (number | null)[];
  /** `Footers` or `Table format`: where the totals of the columns come from. */
  readonly columnTotalsSource?: (string | null)[];
  /** The sizes of the data files. */
  readonly fileSizes?: (Distribution | null)[];
  /** The rows of the row groups. */
  readonly rowGroupRowCounts?: (Distribution | null)[];
  /** The data size of the partitions. */
  readonly partitionSizes?: (Distribution | null)[];
}

/** `columns`: one row for each top-level column that a table declares, with its totals. */
export interface ColumnsTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The table of the column: an `id` of `tables`. Required. */
  readonly table?: (number | null)[];
  /** The identifier that the table format assigns, which survives a rename. */
  readonly fieldId?: (number | null)[];
  /** The name of the column. Required. */
  readonly name?: (string | null)[];
  /** The type as the table format declares it. Required. */
  readonly dataType?: (string | null)[];
  /** Whether the column accepts nulls. */
  readonly isNullable?: (boolean | null)[];
  /** The name used inside the files when it differs from the name. */
  readonly physicalName?: (string | null)[];
  /** Whether a partition field uses the column. */
  readonly isPartitionColumn?: (boolean | null)[];
  /** The comment on the column. */
  readonly description?: (string | null)[];
  /** The compressed size of the column. */
  readonly compressedSize?: (number | null)[];
  /** The size before compression. */
  readonly uncompressedSize?: (number | null)[];
  /** The values, including nulls. */
  readonly valueCount?: (number | null)[];
  /** The null values. */
  readonly nullCount?: (number | null)[];
  /** The column chunks. */
  readonly chunkCount?: (number | null)[];
  /** The column chunks with a dictionary page. */
  readonly dictionaryChunkCount?: (number | null)[];
  /** The column chunks that fell back from a dictionary to another encoding. */
  readonly fallbackChunkCount?: (number | null)[];
  /** The compressed size of the dictionary pages. */
  readonly dictionaryPageSize?: (number | null)[];
  /** The bytes of the variable-length values before encoding. */
  readonly unencodedByteArrayDataBytes?: (number | null)[];
  /** The codecs that the column chunks use. */
  readonly codecs?: (string[] | null)[];
  /** The encodings that the column chunks use. */
  readonly encodings?: (string[] | null)[];
  /** The files that do not store the column. */
  readonly fileCountWithoutColumn?: (number | null)[];
  /** The largest distinct count of any column chunk. */
  readonly largestChunkDistinctCount?: (number | null)[];
  /** The number of distinct values in the table. */
  readonly distinctCount?: (number | null)[];
  /** Whether the distinct count is an estimate. */
  readonly distinctCountIsEstimate?: (boolean | null)[];
  /** `Iceberg statistics`, `Scan`, or `Semantic model`. */
  readonly distinctCountSource?: (string | null)[];
}

/** `partitionSpecs`: one row for each partition spec of a table. */
export interface PartitionSpecsTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The table of the spec: an `id` of `tables`. Required. */
  readonly table?: (number | null)[];
  /** The identifier that Iceberg assigns to the spec. */
  readonly specId?: (number | null)[];
  /** Whether new files use this spec. */
  readonly isDefault?: (boolean | null)[];
}

/** `partitionFields`: one row for each field of a partition spec. */
export interface PartitionFieldsTable extends ColumnarTable {
  /** The spec of the field: an `id` of `partitionSpecs`. Required. */
  readonly partitionSpec?: (number | null)[];
  /** The position of the field in its spec, from 0. Required. */
  readonly position?: (number | null)[];
  /** The name of the partition field. */
  readonly name?: (string | null)[];
  /** The columns that the value comes from: `id`s of `columns`. */
  readonly sourceColumns?: (number[] | null)[];
  /** `identity`, or an Iceberg transform such as `bucket[N]` or `day`. */
  readonly transform?: (string | null)[];
}

/** `sortOrders`: one row for each sort order that a table declares. */
export interface SortOrdersTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The table of the sort order: an `id` of `tables`. Required. */
  readonly table?: (number | null)[];
  /** The identifier that Iceberg assigns to the sort order. */
  readonly orderId?: (number | null)[];
  /** Whether new files use this order. */
  readonly isDefault?: (boolean | null)[];
}

/** `sortFields`: one row for each field of a sort order. */
export interface SortFieldsTable extends ColumnarTable {
  /** The sort order of the field: an `id` of `sortOrders`. Required. */
  readonly sortOrder?: (number | null)[];
  /** The position of the field in its sort order, from 0. Required. */
  readonly position?: (number | null)[];
  /** The column that the field sorts by: an `id` of `columns`. */
  readonly sourceColumn?: (number | null)[];
  /** The transform applied to the column. */
  readonly transform?: (string | null)[];
  /** The direction of the sort. */
  readonly direction?: (string | null)[];
  /** Where nulls sort. */
  readonly nullOrder?: (string | null)[];
}

/** `physicalSchemas`: one row for each distinct Parquet schema found among the files of a table. */
export interface PhysicalSchemasTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The table of the schema: an `id` of `tables`. Required. */
  readonly table?: (number | null)[];
  /** The files that use the schema. */
  readonly fileCount?: (number | null)[];
}

/** `physicalColumns`: one row for each leaf column of a physical schema. */
export interface PhysicalColumnsTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The schema of the column: an `id` of `physicalSchemas`. Required. */
  readonly physicalSchema?: (number | null)[];
  /** The position among the leaf columns, from 0. Required. */
  readonly ordinal?: (number | null)[];
  /** The names from the root of the schema to the leaf. Required. */
  readonly path?: (string[] | null)[];
  /** The Parquet physical type, such as `INT64` or `BYTE_ARRAY`. Required. */
  readonly physicalType?: (string | null)[];
  /** The length of a fixed-length value. */
  readonly typeLength?: (number | null)[];
  /** The logical type annotation as text. */
  readonly logicalType?: (string | null)[];
  /** `REQUIRED`, `OPTIONAL`, or `REPEATED`. */
  readonly repetition?: (string | null)[];
  /** The field identifier written in the file. */
  readonly fieldId?: (number | null)[];
  /** The top-level column that the leaf belongs to: an `id` of `columns`. */
  readonly column?: (number | null)[];
  /** The sum of the compressed sizes of its column chunks. */
  readonly compressedSize?: (number | null)[];
  /** The sum of the uncompressed sizes of its column chunks. */
  readonly uncompressedSize?: (number | null)[];
  /** Its column chunks. */
  readonly chunkCount?: (number | null)[];
}

/** `writers`: one row for each combination of writing engine and optimization found in the files of a table. */
export interface WritersTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The table of the writer: an `id` of `tables`. Required. */
  readonly table?: (number | null)[];
  /** The writer, as the footer names it. */
  readonly createdBy?: (string | null)[];
  /** Whether the files are V-Ordered. */
  readonly isVOrdered?: (boolean | null)[];
  /** The V-Order level that the footer records. */
  readonly vOrderLevel?: (string | null)[];
  /** The files written this way. */
  readonly fileCount?: (number | null)[];
  /** The size of the files written this way. */
  readonly fileSize?: (number | null)[];
}

/** `operations`: one row for each kind of operation in the history of a table. */
export interface OperationsTable extends ColumnarTable {
  /** The table of the operation: an `id` of `tables`. Required. */
  readonly table?: (number | null)[];
  /** For example `WRITE`, `MERGE`, `OPTIMIZE`. Required. */
  readonly name?: (string | null)[];
  /** The commits with this operation. */
  readonly commitCount?: (number | null)[];
  /** The time of the most recent one. */
  readonly lastAt?: (string | null)[];
}

/** `issues`: one row for each problem met while collecting. */
export interface IssuesTable extends ColumnarTable {
  /** The table concerned: an `id` of `tables`, absent when the issue concerns no single table. */
  readonly table?: (number | null)[];
  /** The file concerned, when the issue is about one file. */
  readonly path?: (string | null)[];
  /** A stable code, such as `AccessDenied`. Required. */
  readonly code?: (string | null)[];
  /** The detail. */
  readonly message?: (string | null)[];
}

/** `modelBindings`: one row for each semantic model that reads the tables. */
export interface ModelBindingsTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The semantic model. Required. */
  readonly modelName?: (string | null)[];
  /** The server or workspace of the model. */
  readonly serverName?: (string | null)[];
}

/** `tableBindings`: one row for each table of a semantic model that reads a table of the footprint. */
export interface TableBindingsTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The semantic model: an `id` of `modelBindings`. Required. */
  readonly modelBinding?: (number | null)[];
  /** The name of the table in the model. Required. */
  readonly modelTable?: (string | null)[];
  /** The table of the footprint that it reads: an `id` of `tables`. Required. */
  readonly table?: (number | null)[];
}

/** `columnBindings`: one row for each column of a semantic model that reads a column of the footprint. */
export interface ColumnBindingsTable extends ColumnarTable {
  /** The table binding: an `id` of `tableBindings`. Required. */
  readonly tableBinding?: (number | null)[];
  /** The name of the column in the model. Required. */
  readonly modelColumn?: (string | null)[];
  /** The column of the footprint that it reads: an `id` of `columns`. Required. */
  readonly column?: (number | null)[];
}

/** `partitions`: one row for each partition that holds data. Optional detail. */
export interface PartitionsTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The table of the partition: an `id` of `tables`. Required. */
  readonly table?: (number | null)[];
  /** The spec that produced the values: an `id` of `partitionSpecs`. */
  readonly partitionSpec?: (number | null)[];
  /** One value for each field of the spec, null when the partition value is null. Kept only when the collection includes partition values. */
  readonly values?: ((string | null)[] | null)[];
  /** The files of the partition. */
  readonly fileCount?: (number | null)[];
  /** The size of the files of the partition. */
  readonly fileSize?: (number | null)[];
  /** The rows of the files of the partition. */
  readonly rowCount?: (number | null)[];
  /** The rows marked as deleted in the files of the partition. */
  readonly deletedRowCount?: (number | null)[];
  /** The most recent `modifiedAt` of the files of the partition. Since format 0.2. */
  readonly lastModifiedAt?: (string | null)[];
}

/** `files`: one row for each data file. Kept from the `Files` depth. */
export interface FilesTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The table of the file: an `id` of `tables`. Required. */
  readonly table?: (number | null)[];
  /** The path, relative to the table location when the file is inside it. */
  readonly path?: (string | null)[];
  /** `Parquet`, `ORC`, or `Avro`. */
  readonly format?: (string | null)[];
  /** The size of the file. */
  readonly fileSize?: (number | null)[];
  /** When the file was written. */
  readonly modifiedAt?: (string | null)[];
  /** The rows stored in the file, including rows marked as deleted. */
  readonly rowCount?: (number | null)[];
  /** The rows marked as deleted. */
  readonly deletedRowCount?: (number | null)[];
  /** The size of the deletion vector. */
  readonly deletionVectorSize?: (number | null)[];
  /** The partition of the file: an `id` of `partitions`. */
  readonly partition?: (number | null)[];
  /** The order the file was written with: an `id` of `sortOrders`. */
  readonly sortOrder?: (number | null)[];
  /** The clustering implementation that wrote the file. */
  readonly clusteringProvider?: (string | null)[];
  /** Whether a file tag or the footer marks the file as V-Ordered. */
  readonly isVOrdered?: (boolean | null)[];
  /** The size of the footer, when it was read. */
  readonly footerSize?: (number | null)[];
  /** The schema of the file: an `id` of `physicalSchemas`. */
  readonly physicalSchema?: (number | null)[];
  /** The engine and optimization that wrote the file: an `id` of `writers`. */
  readonly writer?: (number | null)[];
  /** The row groups of the file. */
  readonly rowGroupCount?: (number | null)[];
  /** The sum of the compressed sizes of its column chunks. */
  readonly compressedSize?: (number | null)[];
  /** The sum of the uncompressed sizes of its column chunks. */
  readonly uncompressedSize?: (number | null)[];
}

/** `rowGroups`: one row for each row group. Kept from the `Row groups` depth. */
export interface RowGroupsTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The file of the row group: an `id` of `files`. Required. */
  readonly file?: (number | null)[];
  /** The position in the file, from 0. Required. */
  readonly ordinal?: (number | null)[];
  /** The rows in the row group. Required. */
  readonly rowCount?: (number | null)[];
  /** The uncompressed size of the column data. */
  readonly uncompressedSize?: (number | null)[];
  /** The compressed size of the column data. */
  readonly compressedSize?: (number | null)[];
}

/** `columnChunks`: one row for each column chunk. Kept at the `Column chunks` depth. */
export interface ColumnChunksTable extends ColumnarTable {
  /** The row group of the chunk: an `id` of `rowGroups`. Required. */
  readonly rowGroup?: (number | null)[];
  /** The column that the chunk stores: an `id` of `physicalColumns`. Required. */
  readonly physicalColumn?: (number | null)[];
  /** `UNCOMPRESSED`, `SNAPPY`, `ZSTD`, and the other Parquet codecs. */
  readonly codec?: (string | null)[];
  /** The encodings used in the chunk. */
  readonly encodings?: (string[] | null)[];
  /** The values, including nulls. */
  readonly valueCount?: (number | null)[];
  /** The null values. */
  readonly nullCount?: (number | null)[];
  /** The size of all pages, with their headers and the dictionary page. Required. */
  readonly compressedSize?: (number | null)[];
  /** The same before compression. Required. */
  readonly uncompressedSize?: (number | null)[];
  /** Whether the chunk has a dictionary page. */
  readonly hasDictionaryPage?: (boolean | null)[];
  /** The compressed size of the dictionary page. */
  readonly dictionaryPageSize?: (number | null)[];
  /** The data pages of the chunk. */
  readonly dataPageCount?: (number | null)[];
  /** The data pages that use the dictionary. */
  readonly dictionaryEncodedPageCount?: (number | null)[];
  /** The bytes of the variable-length values before encoding. */
  readonly unencodedByteArrayDataBytes?: (number | null)[];
  /** The size of the bloom filter. */
  readonly bloomFilterSize?: (number | null)[];
  /** The smallest value. Kept only when the collection includes data values. */
  readonly minValue?: (string | null)[];
  /** The largest value. Kept only when the collection includes data values. */
  readonly maxValue?: (string | null)[];
  /** The distinct values in the chunk, not counting nulls. */
  readonly distinctCount?: (number | null)[];
  /** The entries in the dictionary. */
  readonly dictionaryEntryCount?: (number | null)[];
}

/** `deleteFiles`: one row for each file that marks rows of data files as deleted. Optional detail. */
export interface DeleteFilesTable extends ColumnarTable {
  /** Identifier in the footprint. Required. */
  readonly id?: (number | null)[];
  /** The table of the delete file: an `id` of `tables`. Required. */
  readonly table?: (number | null)[];
  /** `Position deletes`, `Equality deletes`, or `Deletion vector`. Required. */
  readonly kind?: (string | null)[];
  /** The location of the file. */
  readonly path?: (string | null)[];
  /** `Parquet`, `ORC`, `Avro`, or `Puffin`. */
  readonly format?: (string | null)[];
  /** The size of the file, or of the deletion vector inside it. */
  readonly fileSize?: (number | null)[];
  /** The delete records, or the rows that a deletion vector removes. */
  readonly recordCount?: (number | null)[];
  /** The data file, when the delete file applies to one file only: an `id` of `files`. */
  readonly referencedFile?: (number | null)[];
  /** The columns that an equality delete compares: `id`s of `columns`. */
  readonly equalityColumns?: (number[] | null)[];
  /** The partition of the delete file: an `id` of `partitions`. */
  readonly partition?: (number | null)[];
}

/** `commits`: one row for each commit that was read. Optional detail. */
export interface CommitsTable extends ColumnarTable {
  /** The table of the commit: an `id` of `tables`. Required. */
  readonly table?: (number | null)[];
  /** The identifier of the commit. Required. */
  readonly commitId?: (string | null)[];
  /** The commit it follows. */
  readonly parentId?: (string | null)[];
  /** When it was committed. */
  readonly timestamp?: (string | null)[];
  /** The kind of operation. */
  readonly operation?: (string | null)[];
  /** The engine that committed. */
  readonly engine?: (string | null)[];
  /** Whether the commit only added rows. */
  readonly isBlindAppend?: (boolean | null)[];
  /** The counters that the engine recorded. */
  readonly metrics?: (Record<string, string> | null)[];
  /** The parameters of the operation. Kept only when the collection includes data values. */
  readonly parameters?: (Record<string, string> | null)[];
}

/** The 15 summary tables, by name. They are all in `summary.json`. */
export interface SummaryTables {
  sources: SourcesTable;
  tables: TablesTable;
  columns: ColumnsTable;
  partitionSpecs: PartitionSpecsTable;
  partitionFields: PartitionFieldsTable;
  sortOrders: SortOrdersTable;
  sortFields: SortFieldsTable;
  physicalSchemas: PhysicalSchemasTable;
  physicalColumns: PhysicalColumnsTable;
  writers: WritersTable;
  operations: OperationsTable;
  issues: IssuesTable;
  modelBindings: ModelBindingsTable;
  tableBindings: TableBindingsTable;
  columnBindings: ColumnBindingsTable;
}

/** The 6 detail tables, by name. Each has parts of its own. */
export interface DetailTables {
  partitions: PartitionsTable;
  files: FilesTable;
  rowGroups: RowGroupsTable;
  columnChunks: ColumnChunksTable;
  deleteFiles: DeleteFilesTable;
  commits: CommitsTable;
}

/** The name of a summary table. */
export type SummaryTableName = keyof SummaryTables;
/** The name of a detail table. */
export type DetailTableName = keyof DetailTables;
/** The name of one of the 21 tables of this version. */
export type TableName = SummaryTableName | DetailTableName;

/** The summary tables of a file. A table that the file does not have is undefined. */
export type Summary = { readonly [K in SummaryTableName]?: SummaryTables[K] };

/** One row of a columnar table: one member for each field that the table has, with its value in that row. */
export type RowOf<T extends ColumnarTable> = {
  [K in Exclude<keyof T, "length">]?: NonNullable<T[K]> extends readonly (infer E)[] ? E : never;
};

/** A row of `sources`. */
export type SourceRow = RowOf<SourcesTable>;
/** A row of `tables`. */
export type TableRow = RowOf<TablesTable>;
/** A row of `columns`. */
export type ColumnRow = RowOf<ColumnsTable>;
/** A row of `partitionSpecs`. */
export type PartitionSpecRow = RowOf<PartitionSpecsTable>;
/** A row of `partitionFields`. */
export type PartitionFieldRow = RowOf<PartitionFieldsTable>;
/** A row of `sortOrders`. */
export type SortOrderRow = RowOf<SortOrdersTable>;
/** A row of `sortFields`. */
export type SortFieldRow = RowOf<SortFieldsTable>;
/** A row of `physicalSchemas`. */
export type PhysicalSchemaRow = RowOf<PhysicalSchemasTable>;
/** A row of `physicalColumns`. */
export type PhysicalColumnRow = RowOf<PhysicalColumnsTable>;
/** A row of `writers`. */
export type WriterRow = RowOf<WritersTable>;
/** A row of `operations`. */
export type OperationRow = RowOf<OperationsTable>;
/** A row of `issues`. */
export type IssueRow = RowOf<IssuesTable>;
/** A row of `modelBindings`. */
export type ModelBindingRow = RowOf<ModelBindingsTable>;
/** A row of `tableBindings`. */
export type TableBindingRow = RowOf<TableBindingsTable>;
/** A row of `columnBindings`. */
export type ColumnBindingRow = RowOf<ColumnBindingsTable>;
/** A row of `partitions`. */
export type PartitionRow = RowOf<PartitionsTable>;
/** A row of `files`. */
export type FileRow = RowOf<FilesTable>;
/** A row of `rowGroups`. */
export type RowGroupRow = RowOf<RowGroupsTable>;
/** A row of `columnChunks`. */
export type ColumnChunkRow = RowOf<ColumnChunksTable>;
/** A row of `deleteFiles`. */
export type DeleteFileRow = RowOf<DeleteFilesTable>;
/** A row of `commits`. */
export type CommitRow = RowOf<CommitsTable>;
