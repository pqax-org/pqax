# PQAX object model

Draft 0.7, 24 October 2026.

This document defines the logical content of a PQAX file: the objects, their fields, and how they relate. It does not define how the objects are written to a file. The [serialization](serialization.md) does that, and section 11 keeps the option of several representations open.

Statements marked **Decided** were taken by the maintainers and are recorded in [decisions.md](decisions.md). Everything else is **Proposed**.

Draft 0.3 takes into account a test on the tables of a real Fabric lakehouse and warehouse.

Draft 0.4 aligns identifiers and a few names with the serialization, so that a field has the same name in both documents. Every object that another object refers to has an `Id`. The identifiers that Iceberg assigns are now `SpecId` and `OrderId`, the identifier of a commit is `CommitId`, and `Partition.Spec` is `Partition.PartitionSpec`. A footprint can also hold issues that concern no single table.

Draft 0.5 takes into account a test of storage views on a real lakehouse. It showed that the non-live files of a table are only an upper bound of the space it wastes: a VACUUM may have deleted some of them already, and the storage can hold files that no commit references. `Table` now has the unreferenced files that a listing of the storage finds, and the removal times of the non-live files. `Partition` has `LastModifiedAt`. Format version 0.2 carries the new fields.

Draft 0.6 defines the derived measures in a new section 10: the storage stack of a table with its waste and overhead, the small files, the V-Order share, the commits per day, and the ratios and shares of the tables, the columns, and the collection. They follow the storage views and the decisions of 21 October 2026. The format does not change. Views become section 11, and the open questions section 12. Section 4.3 states how the quartiles of a `Distribution` are taken, which the small-file shares of section 10.3 depend on, and the maintainer decided the measures and their rules on 22 October 2026.

Draft 0.7 proposes, in a new section 13, how two footprints of the same lakehouse taken at different times are compared: when a table, a column, a partition, a file, and a commit of one are the same as one of the other, and what a comparison reports. It answers open question 7. The format does not change.

## 1. Purpose

A PQAX file holds the footprint of a set of tables stored as Parquet files, with or without a table format such as Delta or Iceberg. The footprint says how much space the data takes and how it is arranged, without the data itself.

The model plays the role that the VPAX object model plays for VertiPaq. A VPAX file describes a semantic model as tables, columns, partitions, and column segments. A PQAX file describes a lakehouse as tables, columns, files, row groups, and column chunks.

The model must answer these questions without access to the storage:

1. Which tables and columns take the most space, compressed and uncompressed?
2. How well does each column compress, and with which encodings and codec?
3. Are files and row groups well sized?
4. How is each table partitioned, clustered, or sorted, and is the distribution even?
5. How many rows are deleted but still stored?
6. Which engine wrote the files, and with which optimizations?
7. What did schema changes leave behind in older files?
8. Which table format features and properties are in use?
9. Which maintenance operations ran, and when, and how much space do the files take that the table no longer uses?
10. How do the tables relate to the semantic model that reads them, when there is one?

## 2. Principles

| Principle | Status |
|---|---|
| **A diagnostic, not a duplicate.** A PQAX file supports an analysis and is easy to copy between developers. It does not reproduce the Parquet format, and it keeps only what an analysis uses. | Decided |
| **Any depth is valid.** A footprint can be populated down to a chosen depth only. A reader works with whatever depth it finds. | Decided |
| **Room to grow.** The model can be extended later without breaking the files and the readers that exist. | Decided |
| **Top-level columns first.** This version declares only the top-level columns of a table. | Decided |
| **Names.** The root is a `Footprint` and a lakehouse is a `Source`, for now. | Decided |
| **One model for every table format.** Files, row groups, and column chunks are described in Parquet terms, which are the same under Delta, under Iceberg, and in a plain folder. A small object per table format holds what does not generalize. | Proposed |
| **Logical and physical columns are separate.** A `Column` is what the table declares today. A `PhysicalColumn` is what a file stores. Keeping them apart records type changes, renamed columns, columns added later, and dropped columns that older files still store. | Proposed |
| **Unknown is not zero.** A field without a value means that the value was not collected or cannot be known. A collector never writes zero for a value that it did not read. | Proposed |

## 3. Overview

```
Footprint
├── Collection                 how and when the footprint was collected
├── Sources                    lakehouses, warehouses, catalogs, or folders
├── Tables                     always present, with totals
│   ├── Columns                always present, with totals
│   ├── PartitionSpecs         how the table is partitioned
│   ├── SortOrders             declared sort orders
│   ├── PhysicalSchemas        the distinct Parquet schemas found in the files
│   │   └── PhysicalColumns
│   ├── Writers                who wrote the files
│   ├── Operations             what happened to the table, by kind of operation
│   ├── Partitions             optional detail
│   ├── Files                  optional detail
│   │   └── RowGroups          optional detail
│   │       └── ColumnChunks   optional detail
│   ├── DeleteFiles            optional detail
│   ├── Commits                optional detail
│   └── Issues                 problems met while collecting
├── ModelBindings              the semantic model that reads the tables
└── Issues                     problems that concern no single table
```

## 4. Depth and totals

This section turns the first two principles into rules.

### 4.1 Two separate scales

| Scale | What it measures | Values |
|---|---|---|
| Collection level | What the collector reads from the storage | 1: the table format metadata. 2: the Parquet footers. 3: the dictionary page headers. 4: a scan or an external source. |
| Detail depth | What the file keeps for a table | `Summary`, `Files`, `Row groups`, `Column chunks` |

The two are independent. A collector can read every footer of a table and keep only its summary.

| Depth | Objects kept for the table | Grows with |
|---|---|---|
| Summary | The table, its columns, partition specs, sort orders, physical schemas, writers, and operations | The number of columns |
| Files | Also one object for each data file | The number of files |
| Row groups | Also one object for each row group | The number of row groups |
| Column chunks | Also one object for each column of each row group | Row groups times columns |

Three lists are optional on their own, whatever the depth: `Partitions`, `DeleteFiles`, and `Commits`.

The collector chooses the depth of each table, so that large tables do not make the file large. For scale, 1.1 million column chunks, with their 26,000 files, took 10 MB in the [prototype](../research/prototype/README.md).

### 4.2 Totals

An object that can lose its detail stores totals. They are the fields marked `Total` in the tables of section 6.

1. A collector computes totals from everything it read, including what it does not keep.
2. When the detail is present, a total equals the same sum over the detail.
3. The totals of level 2 cover the files whose footer was read. `FooterCount` and `FileCount` say how complete they are. A collector does not extrapolate.
4. A total is absent when a value it needs is unknown.

The fields of `Table` that count the non-live and the unreferenced files follow rules 1 and 4 as well, although no detail list holds those files, so rule 2 has nothing to compare. UnreferencedFileSize is the one exception to rule 4: a file whose size the listing does not give counts in UnreferencedFileCount and not in the size, and the size is still present. NonLiveOldestRemovedAt and NonLiveNewestRemovedAt are an exception too: they are taken over the remove actions that have a timestamp, and they are absent only when none has one.

### 4.3 Distributions

A sum hides the shape of the data, and the shape is what tells a table with a few large files from one with many small files. Where the shape matters, the table stores a `Distribution`.

| Field | Type | Description |
|---|---|---|
| Count | integer | The number of values |
| Sum | integer | Their sum |
| Minimum, Maximum | integer | The smallest and the largest value |
| FirstQuartile, Median, ThirdQuartile | integer | The three quartiles |

The quartiles are values of the set, taken by position in the sorted values, with no interpolation: for n values sorted from the smallest, counted from 0, FirstQuartile is the value at position `floor(n/4)`, Median the value at `floor(n/2)`, and ThirdQuartile the value at `floor(3n/4)`. For one value, all three are that value. A collector computes them so, and the measures of section 10.3 rely on it. **Decided** on 22 October 2026.

### 4.4 Rules for a reader

1. A reader accepts a footprint at any depth, and with any of the optional lists missing.
2. A missing list means that the detail was not kept. It does not mean that the table has none. The totals say how many there are.
3. A reader shows what the footprint has, and says which analysis needs more depth.

## 5. Conventions

Each object has a table of fields with these columns:

| Column | Meaning |
|---|---|
| Field | The logical name. The serialization writes it in lower camel case, with a lower-case first letter. |
| Type | `string`, `integer`, `number`, `boolean`, `timestamp`, a named object, `list of`, `map`, or `reference to` another object |
| From | Where the value comes from: the Parquet footer, the Delta log, the Iceberg metadata, a listing of the storage, the collector, another tool, `Total` for a total as defined in 4.2, or `derived` when it is computed from other fields |
| Level | The collection level that supplies the value |
| Mark | `Name` for an identifier that obfuscation must replace. `Data` for a data value, which is excluded unless the collection asks for it. |

Other conventions:

- Sizes are in bytes. Timestamps are in UTC.
- A row is what Parquet calls a row and what Delta and Iceberg call a record.
- Every field is optional unless the line below the table lists it as required.
- An `Id` is a positive integer that identifies an object among all the objects of its kind in the footprint. It has no other meaning.
- A reference points to an object in the same footprint, and holds its `Id`.

## 6. Objects

### 6.1 Footprint

The root object. A footprint usually describes one lakehouse, and it can hold tables from several sources.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| FormatVersion | string | Collector | | | Version of the format, which covers this object model and its serialization. See section 7. |
| Collection | Collection | Collector | | | How the footprint was collected |
| Sources | list of Source | Collector | 1 | | Where the tables come from |
| Tables | list of Table | | 1 | | The tables |
| ModelBindings | list of ModelBinding | Collector | | | Present when the collection started from a semantic model |
| Issues | list of Issue | Collector | | | Problems that concern no single table. See 6.16. |
| ObfuscatorDictionaryId | string | Obfuscator | | | Set when names were obfuscated, as in VPAX |
| ObfuscatorLib, ObfuscatorLibVersion | string | Obfuscator | | | The library that obfuscated the names |

Required: FormatVersion, Tables.

### 6.2 Collection

What was asked of the collector and what it did. This is the counterpart of the extractor properties in VPAX.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| CollectorApp, CollectorAppVersion | string | Collector | | | The application that ran the collection |
| CollectorLib, CollectorLibVersion | string | Collector | | | The library that implemented it |
| StartedAt, CompletedAt | timestamp | Collector | | | When the collection ran |
| Level | integer | Collector | | | The highest collection level requested, 1 to 4 |
| IncludesDataValues | boolean | Collector | | | Whether minimum and maximum values were collected |
| IncludesPartitionValues | boolean | Collector | | | Whether partition values were collected |
| RequestCount | integer | Collector | | | Storage requests made |
| BytesRead | integer | Collector | | | Bytes read from storage |

Required: Level.

### 6.3 Source

A place that holds tables: a lakehouse, a warehouse, a catalog, or a storage folder.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| Kind | string | Collector | 1 | | For example `OneLake lakehouse`, `OneLake warehouse`, `Unity Catalog`, `Iceberg catalog`, `Storage folder` |
| Name | string | Collector | 1 | Name | The name of the lakehouse, warehouse, or catalog |
| Location | string | Collector | 1 | Name | The base location of the source |
| Identifiers | map of string to string | Collector | 1 | Name | Identifiers that the platform assigns, such as a workspace id and an item id |

Required: Id, Kind.

### 6.4 Table

A table at one version. The footprint describes the data files of that version only.

Identity and format:

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| Source | reference to Source | Collector | 1 | | The source that holds the table |
| Namespace | list of string | Collector | 1 | Name | The schema or namespace path of the table |
| Name | string | Collector | 1 | Name | The table name |
| Location | string | Delta, Iceberg `location`, Collector | 1 | Name | The root location of the table |
| Format | string | Collector | 1 | | `Delta`, `Iceberg`, or `Parquet` for a folder of files with no table format |
| TableId | string | Delta `metaData.id`, Iceberg `table-uuid` | 1 | | An identifier that stays the same across versions of the table |
| Description | string | Delta `metaData.description`, Iceberg property | 1 | Name | The description of the table |
| CreatedAt | timestamp | Delta `metaData.createdTime` | 1 | | When the table was created |
| Properties | map of string to string | Delta `metaData.configuration`, Iceberg `properties` | 1 | | The table properties |
| SnapshotId | string | Delta version, Iceberg `snapshot-id` | 1 | | The version of the table that the footprint describes |
| SnapshotTimestamp | timestamp | Delta commit time, Iceberg `timestamp-ms` | 1 | | When that version was created |
| Delta | DeltaDetails | Delta | 1 | | Present for Delta tables. See 6.4.1. |
| Iceberg | IcebergDetails | Iceberg | 1 | | Present for Iceberg tables. See 6.4.2. |
| ClusteringColumns | list of reference to Column | Delta `delta.clustering` domain | 1 | | The columns of a Delta clustered table |
| CollectedLevel | integer | Collector | | | The collection level actually reached for this table |
| DetailDepth | string | Collector | | | `Summary`, `Files`, `Row groups`, or `Column chunks`. See section 4. |

Totals:

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| FileCount | integer | Total | 1 | | The data files of the version described |
| FileSize | integer | Total | 1 | | The sum of their sizes |
| RowCount | integer | Total | 1 | | The rows stored, including rows marked as deleted |
| DeletedRowCount | integer | Total | 1 | | The rows marked as deleted. Absent when the table format cannot say. |
| LastModifiedAt | timestamp | Total | 1 | | When the most recent data file was written |
| VOrderedFileCount | integer | Total | 1 | | The files that a file tag or the footer marks as V-Ordered |
| PartitionCount | integer | Total | 1 | | The distinct partitions that hold data |
| DeleteFileCount, DeleteFileSize | integer | Total | 1 | | The delete files of an Iceberg table |
| CommitCount | integer | Total | 1 | | The commits that the table format still keeps |
| CommitsRead | integer | Collector | 1 | | The most recent commits that the collector read. Each one costs a request, so a collector reads only some. |
| OldestCommitAt | timestamp | Total | 1 | | The time of the oldest of those commits |
| NonLiveFileCount, NonLiveFileSize | integer | Delta `remove` actions | 1 | | Files that left the table and that the log still tracks, so they may still be stored |
| NonLiveOldestRemovedAt, NonLiveNewestRemovedAt | timestamp | Delta `remove.deletionTimestamp` | 1 | | The earliest and the latest removal time of the files that NonLiveFileCount counts. Absent when none of their remove actions has a timestamp. |
| UnreferencedFileCount, UnreferencedFileSize | integer | Storage listing | 1 | | The data files under the table location that the version described does not reference, and the sum of their sizes as the listing gives them. See below the table. |
| MetadataFileCount, MetadataSize | integer | Storage listing | 1 | | The files of the table format itself: the Delta log, or the Iceberg metadata and manifests |
| FooterCount | integer | Total | 2 | | The files whose footer was read |
| RowGroupCount | integer | Total | 2 | | The row groups of those files |
| CompressedSize, UncompressedSize | integer | Total | 2 | | The sums of the column chunk sizes |
| FooterSize | integer | Total | 2 | | The sum of the footer sizes |
| StaleColumnSize | integer | Total | 2 | | The compressed size of the physical columns that the table no longer declares |
| ColumnTotalsSource | string | Collector | | | `Footers` or `Table format`. Says where the totals of the columns come from. |
| FileSizes | Distribution | Total | 1 | | The sizes of the data files |
| RowGroupRowCounts | Distribution | Total | 2 | | The rows of the row groups |
| PartitionSizes | Distribution | Total | 1 | | The data size of the partitions |

A data file, for the unreferenced files, is a file whose name ends with `.parquet`, at any depth under the table location and outside the folders `_delta_log` and `_change_data`. It is referenced when its path relative to the table location equals the path of a live file of the version described, after the path of the log is decoded. The comparison is case-sensitive. A file that no commit ever referenced, such as one of an aborted write, is unreferenced too. A file whose size the listing does not give counts in UnreferencedFileCount and not in UnreferencedFileSize. Both fields are absent when the live files are unknown or when the listing fails, which is then an issue.

The unreferenced files include the non-live files that are still stored, so a reader that compares them with NonLiveFileCount and NonLiveFileSize can tell the non-live files still in storage from those that a VACUUM already deleted, and the removal times say how old the tombstones are against the retention of the table.

Children:

| Field | Type | Kept | Description |
|---|---|---|---|
| Columns | list of Column | Always | See 6.5 |
| PartitionSpecs | list of PartitionSpec | Always | See 6.6 |
| SortOrders | list of SortOrder | Always | See 6.7 |
| PhysicalSchemas | list of PhysicalSchema | Always, from level 2 | See 6.8 |
| Writers | list of Writer | Always, from level 2 | See 6.9 |
| Operations | list of Operation | Always | See 6.10 |
| Issues | list of Issue | Always | See 6.16 |
| Partitions | list of Partition | Optional | See 6.6 |
| Files | list of File | From the `Files` depth | See 6.11 |
| DeleteFiles | list of DeleteFile | Optional | See 6.14 |
| Commits | list of Commit | Optional | See 6.15 |

Required: Id, Name, Format, DetailDepth.

#### 6.4.1 DeltaDetails

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| MinReaderVersion, MinWriterVersion | integer | `protocol` | 1 | | The protocol versions |
| ReaderFeatures, WriterFeatures | list of string | `protocol` | 1 | | The table features |
| CheckpointVersion | integer | `_last_checkpoint` | 1 | | The version of the last checkpoint |
| CommitsAfterCheckpoint | integer | Storage listing | 1 | | Commit files newer than the last checkpoint |
| Domains | list of string | `domainMetadata` | 1 | | The names of the metadata domains in use |

#### 6.4.2 IcebergDetails

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| FormatVersion | integer | `format-version` | 1 | | The version of the table format |
| CurrentSchemaId | integer | `current-schema-id` | 1 | | The schema that the columns describe |
| SchemaCount | integer | `schemas` | 1 | | Schemas that the metadata still lists |
| SnapshotCount | integer | `snapshots` | 1 | | Snapshots that the metadata still lists |
| RefCount | integer | `refs` | 1 | | Branches and tags |
| ManifestCount, ManifestSize | integer | Manifest list | 1 | | The manifests of the current snapshot |
| StatisticsFileCount | integer | `statistics` | 1 | | Statistics files attached to the table |

### 6.5 Column

A top-level column that the table declares at the version described.

A column of a nested type is still one column. Its DataType holds the complete definition, and each leaf that the files store for it is a `PhysicalColumn` that refers to it. Section 7 says how nested fields can be added later.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| FieldId | integer | Delta `delta.columnMapping.id`, Iceberg field id | 1 | | The identifier that the table format assigns, which survives a rename |
| Name | string | Delta `schemaString`, Iceberg schema | 1 | Name | The name of the column |
| DataType | string | Delta, Iceberg | 1 | | The type as the table format declares it, for example `decimal(18,2)` or `array<string>` |
| IsNullable | boolean | Delta `nullable`, Iceberg `required` | 1 | | Whether the column accepts nulls |
| PhysicalName | string | Delta `delta.columnMapping.physicalName` | 1 | Name | The name used inside the files when it differs from Name |
| IsPartitionColumn | boolean | Delta `partitionColumns`, Iceberg partition specs | 1 | | Whether a partition field uses the column. Delta usually does not store such a column in the files, and the column then has no totals. |
| Description | string | Delta, Iceberg `doc` | 1 | Name | The comment on the column |

Totals. They come from the footers when the collection reads them, and from the table format otherwise. Only Iceberg records sizes in the table format.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| CompressedSize | integer | Total | 2, or 1 for Iceberg | | The compressed size of the column |
| UncompressedSize | integer | Total | 2 | | The size before compression |
| ValueCount | integer | Total | 2, or 1 for Iceberg | | The values, including nulls |
| NullCount | integer | Total | 2, or 1 | | The null values |
| ChunkCount | integer | Total | 2 | | The column chunks |
| DictionaryChunkCount | integer | Total | 2 | | The column chunks with a dictionary page |
| FallbackChunkCount | integer | Total | 2 | | The column chunks that started with a dictionary and fell back to another encoding |
| DictionaryPageSize | integer | Total | 2 | | The compressed size of the dictionary pages |
| UnencodedByteArrayDataBytes | integer | Total | 2 | | The bytes of the variable-length values before encoding. Divided by the non-null values, it gives the average length of a string. |
| Codecs, Encodings | list of string | Total | 2 | | The codecs and the encodings that the column chunks use |
| FileCountWithoutColumn | integer | Total | 2 | | The files that do not store the column, for example because it was added later |
| LargestChunkDistinctCount | integer | Total | 2 or 3 | | The largest distinct count of any column chunk. The column has at least this many distinct values, and exactly this many when it has a single column chunk. |
| DistinctCount | integer | See DistinctCountSource | 4 | | The number of distinct values in the table |
| DistinctCountIsEstimate | boolean | | 4 | | Whether DistinctCount is an estimate |
| DistinctCountSource | string | Collector | 4 | | `Iceberg statistics`, `Scan`, or `Semantic model` |

Required: Id, Name, DataType.

For a table with no table format, the collector builds the columns from the schema of the most recent file.

### 6.6 Partitioning

A `PartitionSpec` says how rows are assigned to partitions. A Delta table has one. An Iceberg table has one for each time its partitioning changed.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| SpecId | integer | Iceberg `spec-id` | 1 | | The identifier that Iceberg assigns to the spec |
| IsDefault | boolean | Iceberg `default-spec-id` | 1 | | Whether new files use this spec |
| Fields | list of PartitionField | | 1 | | The partition fields, in order |

A `PartitionField` is one element of a spec.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Name | string | Delta `partitionColumns`, Iceberg | 1 | Name | The name of the partition field |
| SourceColumns | list of reference to Column | | 1 | | The column that the value comes from. Iceberg allows more than one. |
| Transform | string | Iceberg | 1 | | `identity` for Delta. For Iceberg also `bucket[N]`, `truncate[W]`, `year`, `month`, `day`, `hour`, `void`. |

A `Partition` is one distinct combination of partition values that has at least one file. The list is optional detail.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| PartitionSpec | reference to PartitionSpec | | 1 | | The spec that produced the values |
| Values | list of string | Delta `add.partitionValues`, Iceberg `partition` | 1 | Data | One value for each field of the spec, null when the partition value is null. Excluded by default, and partitions then differ only by Id. |
| FileCount, FileSize, RowCount, DeletedRowCount | integer | Total | 1 | | The totals of the files of the partition |
| LastModifiedAt | timestamp | Total | 1 | | The most recent ModifiedAt of the files of the partition |

### 6.7 SortOrder

A sort order that the table declares. Files refer to the order they were written with.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| OrderId | integer | Iceberg `order-id` | 1 | | The identifier that Iceberg assigns to the sort order |
| IsDefault | boolean | Iceberg `default-sort-order-id` | 1 | | Whether new files use this order |
| Fields | list of SortField | Iceberg | 1 | | The sort fields, in order |

A `SortField` is one element of a sort order.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| SourceColumn | reference to Column | Iceberg `source-id` | 1 | | The column that the order uses |
| Transform | string | Iceberg `transform` | 1 | | `identity`, or a transform as in a partition field |
| Direction | string | Iceberg `direction` | 1 | | `asc` or `desc` |
| NullOrder | string | Iceberg `null-order` | 1 | | `nulls-first` or `nulls-last` |

Required: SourceColumn, Direction.

### 6.8 PhysicalSchema and PhysicalColumn

A `PhysicalSchema` is one distinct Parquet schema found among the files of a table. A table whose files were all written with the same schema has one. Schema changes over time produce more.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| FileCount | integer | Total | 2 | | The files that use the schema |
| Columns | list of PhysicalColumn | Parquet `schema` | 2 | | The leaf columns, in the order in which row groups store them |

A `PhysicalColumn` is a leaf column of that schema.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| Ordinal | integer | Parquet | 2 | | Position among the leaf columns, from 0, which is also the position of its column chunk in a row group |
| Path | list of string | Parquet `path_in_schema` | 2 | Name | The names from the root of the schema to the leaf |
| PhysicalType | string | Parquet `type` | 2 | | `BOOLEAN`, `INT32`, `INT64`, `INT96`, `FLOAT`, `DOUBLE`, `BYTE_ARRAY`, or `FIXED_LEN_BYTE_ARRAY` |
| TypeLength | integer | Parquet `type_length` | 2 | | The length of a fixed-length value |
| LogicalType | string | Parquet `logicalType` | 2 | | The annotation as text: the name in upper case, with its parameters in parentheses, for example `STRING`, `DECIMAL(18,2)`, `TIMESTAMP(MICROS, UTC)`, `TIMESTAMP(MICROS, LOCAL)`, `INTEGER(64, SIGNED)`, `DATE`, `UUID`, `LIST`, `MAP`. A file with a legacy converted type and no logical type gives the name of the converted type, such as `UTF8` or `INT_64`. |
| Repetition | string | Parquet `repetition_type` | 2 | | `REQUIRED`, `OPTIONAL`, or `REPEATED` |
| FieldId | integer | Parquet `field_id` | 2 | | The field identifier written in the file |
| Column | reference to Column | Collector | 2 | | The top-level column that this leaf belongs to. Absent when the table no longer declares it. |
| CompressedSize, UncompressedSize | integer | Total | 2 | | The sums of its column chunk sizes |
| ChunkCount | integer | Total | 2 | | Its column chunks |

Required: Id, Ordinal, Path, PhysicalType.

The collector maps a physical column to a column by field identifier when the file has one. Otherwise it uses PhysicalName, or Name when the column has no physical name.

### 6.9 Writer

One combination of writing engine and optimization found in the files of a table.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| CreatedBy | string | Parquet `created_by` | 2 | | The writer, as the footer names it |
| IsVOrdered | boolean | derived | 2 | | True when the footer metadata or a file tag marks the files as V-Ordered |
| VOrderLevel | string | Parquet footer metadata | 2 | | The V-Order level that the footer records |
| FileCount, FileSize | integer | Total | 2 | | The files written this way |

### 6.10 Operation

One kind of operation in the history of the table, with how often and how recently it ran. It keeps the useful part of the history when the list of commits is not kept. It covers the commits that the collector read, and it is empty when the commits carry no operation name.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Name | string | Delta `commitInfo.operation`, Iceberg `summary.operation` | 1 | | For example `WRITE`, `MERGE`, `OPTIMIZE`, `VACUUM END`, `append`, `overwrite` |
| CommitCount | integer | Total | 1 | | The commits with this operation |
| LastAt | timestamp | Total | 1 | | The time of the most recent one |

Required: Name.

### 6.11 File

A data file that belongs to the table at the version described. Files are kept from the `Files` depth.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| Path | string | Delta `add.path`, Iceberg `file_path` | 1 | Name | Relative to the table location when the file is inside it, complete otherwise. A path can contain partition values. |
| Format | string | Iceberg `file_format` | 1 | | `Parquet`, `ORC`, or `Avro`. Only Parquet files have row groups in the footprint. |
| FileSize | integer | Delta `add.size`, Iceberg `file_size_in_bytes` | 1 | | The size of the file |
| ModifiedAt | timestamp | Delta `add.modificationTime` | 1 | | When the file was written |
| RowCount | integer | Delta `stats.numRecords`, Iceberg `record_count`, Parquet `num_rows` | 1 or 2 | | The rows stored in the file, including rows marked as deleted. It comes from the footer when the table format does not record it. |
| DeletedRowCount | integer | Delta `deletionVector.cardinality`, Iceberg deletion vector | 1 | | The rows marked as deleted. Zero when the table format says there are none, absent when it cannot say. |
| DeletionVectorSize | integer | Delta `deletionVector.sizeInBytes`, Iceberg | 1 | | The size of the deletion vector |
| Partition | reference to Partition | | 1 | | The partition of the file |
| SortOrder | reference to SortOrder | Iceberg `sort_order_id` | 1 | | The order the file was written with |
| ClusteringProvider | string | Delta `add.clusteringProvider` | 1 | | The clustering implementation that wrote the file |
| IsVOrdered | boolean | Delta `add.tags`, Parquet footer metadata | 1 or 2 | | Whether a file tag or the footer marks the file as V-Ordered |
| FooterSize | integer | Parquet | 2 | | The size of the footer. Present when the footer was read. |
| PhysicalSchema | reference to PhysicalSchema | Parquet | 2 | | The schema of the file |
| Writer | reference to Writer | Parquet | 2 | | The engine and optimization that wrote the file |
| RowGroupCount | integer | Total | 2 | | The row groups of the file |
| CompressedSize, UncompressedSize | integer | Total | 2 | | The sums of the column chunk sizes |
| RowGroups | list of RowGroup | Parquet | 2 | | Kept from the `Row groups` depth. See 6.12. |

Required: Id.

### 6.12 RowGroup

Row groups are kept from the `Row groups` depth.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| Ordinal | integer | Parquet | 2 | | Position in the file, from 0 |
| RowCount | integer | Parquet `num_rows` | 2 | | The rows in the row group |
| UncompressedSize | integer | Parquet `total_byte_size` | 2 | | The uncompressed size of the column data |
| CompressedSize | integer | Parquet `total_compressed_size` | 2 | | The compressed size of the column data. The sum of the column chunks when the footer omits it. |
| ColumnChunks | list of ColumnChunk | Parquet | 2 | | Kept at the `Column chunks` depth. One for each physical column. |

Required: Id, Ordinal, RowCount.

### 6.13 ColumnChunk

The values of one physical column in one row group. This is the finest grain of the model, and it is kept only at the `Column chunks` depth.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| PhysicalColumn | reference to PhysicalColumn | Parquet | 2 | | The column that the chunk stores |
| Codec | string | Parquet `codec` | 2 | | `UNCOMPRESSED`, `SNAPPY`, `GZIP`, `ZSTD`, and the other Parquet codecs |
| Encodings | list of string | Parquet `encodings` | 2 | | The encodings used in the chunk |
| ValueCount | integer | Parquet `num_values` | 2 | | The values, including nulls |
| NullCount | integer | Parquet `statistics.null_count` | 2 | | The null values |
| CompressedSize | integer | Parquet `total_compressed_size` | 2 | | The size of all pages, with their headers and the dictionary page |
| UncompressedSize | integer | Parquet `total_uncompressed_size` | 2 | | The same before compression |
| HasDictionaryPage | boolean | Parquet `dictionary_page_offset` | 2 | | Whether the chunk has a dictionary page |
| DictionaryPageSize | integer | derived | 2 | | The compressed size of the dictionary page: `data_page_offset` minus `dictionary_page_offset` |
| DataPageCount | integer | Parquet `encoding_stats` | 2 | | The data pages of the chunk |
| DictionaryEncodedPageCount | integer | Parquet `encoding_stats` | 2 | | The data pages that use the dictionary. Fewer than DataPageCount means that the chunk fell back to another encoding. |
| UnencodedByteArrayDataBytes | integer | Parquet `size_statistics` | 2 | | The bytes of the variable-length values before encoding |
| BloomFilterSize | integer | Parquet `bloom_filter_length` | 2 | | The size of the bloom filter |
| MinValue, MaxValue | string | Parquet `statistics` | 2 | Data | The bounds of the values |
| DistinctCount | integer | Parquet `statistics.distinct_count` | 2 | | The distinct values in the chunk, not counting nulls. Fabric writers set it for the chunks that have a dictionary. |
| DictionaryEntryCount | integer | Parquet dictionary page header | 3 | | The entries in the dictionary, for files whose statistics have no distinct count |

Required: PhysicalColumn, CompressedSize, UncompressedSize.

### 6.14 DeleteFile

A file that marks rows of data files as deleted. Iceberg tables have them. A Delta table records its deletion vectors on each data file instead, so it has none. The list is optional detail.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| Kind | string | Iceberg `content` | 1 | | `Position deletes`, `Equality deletes`, or `Deletion vector` |
| Path | string | Iceberg `file_path` | 1 | Name | The location of the file |
| Format | string | Iceberg `file_format` | 1 | | `Parquet`, `ORC`, `Avro`, or `Puffin` |
| FileSize | integer | Iceberg `file_size_in_bytes` | 1 | | The size of the file, or of the deletion vector inside it |
| RecordCount | integer | Iceberg `record_count` | 1 | | The delete records, or the rows that a deletion vector removes |
| ReferencedFile | reference to File | Iceberg `referenced_data_file` | 1 | | The data file, when the delete file applies to one file only |
| EqualityColumns | list of reference to Column | Iceberg `equality_ids` | 1 | | The columns that an equality delete compares |
| Partition | reference to Partition | Iceberg | 1 | | The partition of the delete file |

Required: Id, Kind.

### 6.15 Commit

One change to the table: a Delta commit or an Iceberg snapshot. The list is optional detail, and it holds the most recent commits up to a limit that the collector sets.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| CommitId | string | Delta version, Iceberg `snapshot-id` | 1 | | The identifier of the commit |
| ParentId | string | Iceberg `parent-snapshot-id` | 1 | | The commit it follows |
| Timestamp | timestamp | Delta `commitInfo.timestamp`, Iceberg `timestamp-ms`, Storage listing | 1 | | When it was committed. The modification time of the commit file when the commit does not say. |
| Operation | string | Delta `commitInfo.operation`, Iceberg `summary.operation` | 1 | | The kind of operation |
| Engine | string | Delta `commitInfo.engineInfo`, Iceberg summary | 1 | | The engine that committed |
| IsBlindAppend | boolean | Delta `commitInfo.isBlindAppend` | 1 | | Whether the commit only added rows |
| Metrics | map of string to string | Delta `operationMetrics`, Iceberg summary | 1 | | The counters that the engine recorded, such as files and rows added and removed |
| Parameters | map of string to string | Delta `operationParameters` | 1 | Data | The parameters of the operation. They can contain predicates, so they are excluded by default. |

Required: CommitId.

The collector never records the user names, notebook identifiers, or cluster identifiers that a commit can carry.

### 6.16 Issue

A problem met while collecting. A collection continues after a problem, so the footprint says what is missing. An issue belongs to a table, or to the footprint when it concerns no single table.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Path | string | Collector | | Name | The file concerned, when the issue is about one file |
| Code | string | Collector | | | A stable code, for example `AccessDenied` or `FooterNotRead` |
| Message | string | Collector | | | The detail |

Required: Code.

### 6.17 ModelBinding

The link between the tables of a semantic model and the tables of the footprint. It is written when the collection starts from a semantic model, because resolving the link afterwards is unreliable.

In a Direct Lake model, each row group becomes one segment with the same number of rows. The segments of a VPAX and the row groups of a PQAX of the same model can therefore be matched.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| ModelName | string | Semantic model | | Name | The semantic model |
| ServerName | string | Semantic model | | Name | The server or workspace of the model, as VPAX records it |
| Tables | list of TableBinding | Semantic model | | | One for each table of the model that reads a table of the footprint |

A `TableBinding` links one table of the model.

| Field | Type | From | Level | Mark | Description |
|---|---|---|---|---|---|
| Id | integer | Collector | | | Identifier in the footprint |
| ModelTable | string | Semantic model | | Name | The name of the table in the model |
| Table | reference to Table | Collector | | | The table of the footprint that it reads |
| Columns | list of ColumnBinding | Semantic model | | | Each with a ModelColumn name and a reference to a Column |

Required: Id and ModelName, for each table binding Id, ModelTable, and Table, and for each column binding ModelColumn and Column.

## 7. Extending the model

These rules keep the door open that the third principle asks for.

1. A reader ignores the objects and the fields that it does not know.
2. A later version adds optional objects and optional fields. It does not change the meaning of what exists, and it does not make an optional field required.
3. Lists of values are open. A reader accepts a codec, an encoding, a table format, or a kind that it does not know, and shows it as it is.
4. FormatVersion has two numbers. A change that follows rules 1 to 3 raises the second. A reader accepts any footprint whose first number it knows.

How the parts left out of this version would come back under these rules:

| Extension | How it fits |
|---|---|
| Nested fields | A new optional list under `Column` describes the fields of a nested type. `PhysicalColumn.Column` keeps pointing to the top-level column, and a new optional reference points to the nested field. Readers of this version still get correct sizes for each top-level column. |
| Pages | A new optional list under `ColumnChunk`, and one more depth |
| Statistics per file and column | A new optional list under `File` |
| Footer metadata and file tags | New optional maps on `File` or `Writer` |
| Another table format | A new value of `Table.Format` and a new details object next to `DeltaDetails` |

## 8. What the model leaves out

- The raw footers and the raw log.
- Nested fields as objects, pages, and statistics per file and column. Section 7 says how they can be added.
- Footer metadata and file tags, apart from what `Writer` derives from them.
- File offsets, apart from the two that give the size of a dictionary page.
- The repetition and definition level histograms.
- The content of bloom filters, deletion vectors, and delete files.
- Encryption details. A file that cannot be read produces an issue.
- Geospatial bounding boxes, which are data values.
- Change data feed files.
- The identity of the people and jobs that committed.

## 9. Coverage

Each question of section 1, with what answers it at the `Summary` depth and what more depth adds.

| Question | At the Summary depth | More depth adds |
|---|---|---|
| Space by table and column | Table and Column totals | Which files and row groups hold it |
| Compression, encodings, codec | Column totals, with the codecs and encodings in use | The same for each row group |
| Dictionaries | DictionaryChunkCount, FallbackChunkCount, DictionaryPageSize, LargestChunkDistinctCount | The dictionary of each column chunk |
| File and row group sizing | FileSizes, RowGroupRowCounts, FileCount, RowGroupCount | The files and row groups that are out of range |
| Partitioning and distribution | PartitionSpecs, PartitionCount, PartitionSizes | Each partition, and when its files last changed |
| Clustering and sort | ClusteringColumns, SortOrders | Which files were clustered. Judging how well needs data values. |
| Deleted rows | DeletedRowCount, DeleteFileCount | The files that carry them |
| Writer and optimizations | Writers, VOrderedFileCount | The writer of each file |
| Leftovers of schema changes | PhysicalSchemas, StaleColumnSize, FileCountWithoutColumn | The files of each schema |
| Features and properties | Properties, DeltaDetails, IcebergDetails | |
| Maintenance history | Operations, CommitCount, OldestCommitAt | Each commit, with its metrics |
| Storage that the table no longer uses | NonLiveFileCount, NonLiveFileSize, UnreferencedFileCount, UnreferencedFileSize, NonLiveOldestRemovedAt, NonLiveNewestRemovedAt | |
| Link to the semantic model | ModelBindings | |
| Distinct values per column | LargestChunkDistinctCount at level 2 or 3, DistinctCount at level 4 | |

Every question has an answer at the `Summary` depth, whose size depends on the number of columns and not on the amount of data.

The model also covers what the existing analyzers show. The figures of the Delta Analyzer in DAX Studio and of the Delta Analyzer notebook can be computed from it at the `Column chunks` depth, including the cardinality that the notebook gets from a scan. The exception is the raw page offsets that the notebook lists, which the model replaces with the size of the dictionary page.

Section 10 defines the measures that a reader derives from these fields, and 10.9 maps the figures of the two analyzers to them.

## 10. Derived measures

A derived measure is computed by a reader from the fields of section 6. The file does not store it. This section defines the measures once, so that every implementation computes them the same way and gives them the same names. They follow the storage views of `@pqax/views` 0.2.0 and the decisions of 21 October 2026 recorded in [decisions.md](decisions.md). The section, its rules, and the measures are **Decided** on 22 October 2026.

### 10.1 Rules

1. **Only the fields.** A measure reads the fields of section 6 and nothing else. A formula names them as section 6 does: `FileSize` is `Table.FileSize` unless another object is named.
2. **Null, never zero.** A measure is null when a field that it needs is absent, or when its denominator is zero. A reader treats a negative size or count as absent. This applies the principle "Unknown is not zero" of section 2. The exceptions are stated with their measure: a stack component that is absent counts as zero in the sums of 10.2, which are then lower bounds; Deleted is zero when DeletedRowCount is zero; and a sum over the tables or the columns adds those that have the value.
3. **Kind.** Each measure is **exact**, an **estimate**, or a **bound**. Exact means exact over the fields it reads. An estimate says which assumption it makes, and a bound says in which direction it errs.
4. **Coverage.** A measure over totals of level 2 covers the files whose footer was read, as rule 3 of section 4.2 says. FooterCoverage says how complete they are. No measure extrapolates.
5. **Detail.** Some measures have two forms: one from the files, when the table keeps them at the `Files` depth or deeper, and one from the totals. The form from the files reads the files that the footprint keeps, which at that depth are every file of the table, as rule 2 of section 4.2 requires. The measure says when the form from the files needs more.
6. **Units.** Sizes are in bytes, and an estimate in bytes need not be an integer. A share is a part over a whole, from 0 to 1 when the fields agree with each other; OverheadShare and the shares of Metadata and Footers can exceed 1, and only NullShare and VOrderShare are clipped at 1. A ratio is a number. A count of days is a whole number.
7. **Names.** A measure has a logical name, written as a field is, with a capital first letter. Each language writes it in its own casing, as rule 5 of section 3 of the [contracts](contracts.md) says for fields. The names are those of the storage views, and 10.8 maps them.

Two thresholds define the small files. **Decided** on 21 October 2026.

| Threshold | Value | Meaning |
|---|---|---|
| SmallFileSize | 128 MiB, 134,217,728 bytes | A file below it is small |
| TinyFileSize | 8 MiB, 8,388,608 bytes | A file below it is very small |

A file is below a threshold when its size is strictly less. An implementation may let the caller change the two values, and these are then the defaults.

The level and depth column of the tables below gives the lowest collection level and the lowest detail depth at which the measure can be computed. `Summary` means any depth.

### 10.2 Storage stack of a table

The stack says where the bytes of a table go: the live data, the waste, and the overhead. It has seven components, and together they add up to what the storage holds for the table.

| Measure | Formula | Unit | Level, depth | Kind | Meaning |
|---|---|---|---|---|---|
| Live | `max(0, FileSize - Deleted - FooterSize - StaleColumnSize)`, an absent term counting as 0. Null when FileSize is absent. | bytes | 1, Summary | Exact less the estimate of Deleted. An upper bound when Deleted, FooterSize, or StaleColumnSize is absent, or when not every footer was read. | The bytes of the data files that hold rows the table still has, in the columns it still declares |
| NonLive | When UnreferencedFileSize is present: `min(NonLiveFileSize, UnreferencedFileSize)`. Otherwise `NonLiveFileSize`. Null when NonLiveFileSize is absent. | bytes | 1, Summary | Upper bound of the non-live files still stored, tighter with the listing | The files that left the table and that the log still tracks, as far as the storage can still hold them |
| Unreferenced | `UnreferencedFileSize - NonLive`, with NonLive as 0 when it is null. Null when UnreferencedFileSize is absent: a file of format 0.1, a listing that failed, or live files that are unknown. | bytes | 1, Summary, format 0.2 | Lower bound of the files that no non-live file accounts for, and of their bytes, since a file whose size the listing does not give is not in UnreferencedFileSize. NonLive plus Unreferenced is exactly UnreferencedFileSize. | The data files in the storage that no non-live file of the log explains: files that no commit referenced, or whose remove the log no longer holds |
| Deleted | From the totals: 0 when DeletedRowCount is 0, whatever the other fields; otherwise `FileSize * min(DeletedRowCount, RowCount) / RowCount`, null when FileSize, DeletedRowCount, or RowCount is absent, or RowCount is 0. From the files, when at least one file is kept with a FileSize and each of them gives the estimate by the same rule over `File.FileSize`, `File.DeletedRowCount`, and `File.RowCount`: the sum of the estimates. A kept file without a FileSize is left out. | bytes | 1, Summary; the form from the files at `Files` | Estimate. It assumes that a deleted row takes the average bytes of a row of its file, or of the table, footers and stale columns included. The form from the files is closer, since deleted rows gather in some files. | The bytes of the rows that are marked as deleted and still stored |
| Stale | `StaleColumnSize` | bytes | 2, Summary | Exact over the footers read | The bytes of the physical columns that the table no longer declares |
| Metadata | `MetadataSize` | bytes | 1, Summary | Exact | The files of the table format: the Delta log, or the Iceberg metadata and manifests |
| Footers | `FooterSize` | bytes | 2, Summary | Exact over the footers read | The footers of the data files |
| Waste | `NonLive + Unreferenced + Deleted + Stale`, an absent component counting as 0. Null when all four are absent. | bytes | 1, Summary | As its components, and a lower bound when one is absent. Without a listing, NonLive is an upper bound and Unreferenced is absent, so the waste is then neither bound. | The space that holds no live data |
| Overhead | `Metadata + Footers`, an absent component counting as 0. Null when both are absent. | bytes | 1, Summary | Lower bound when a component is absent | The space that describes the data, outside the waste |
| Data | `Live + Waste`. Null when Live is null. | bytes | 1, Summary | As its terms | The denominator of the shares |
| Total | The sum of the seven components, an absent one counting as 0. Null when all seven are absent. When Live is not clipped at 0, it equals `FileSize + MetadataSize` plus `UnreferencedFileSize` when it is present, or plus `NonLiveFileSize` otherwise. | bytes | 1, Summary | Exact when FileSize, MetadataSize, and UnreferencedFileSize are present, even when not every footer was read. Without a listing, it counts the non-live files that a VACUUM may have deleted. A lower bound when a component is absent. | Everything that the storage holds for the table |
| WasteShare | `Waste / Data` | share | 1, Summary | As its terms | The part of the data that is waste |
| OverheadShare | `Overhead / Data`. WasteShare plus OverheadShare can exceed 1, since the overhead is outside the data. | share | 1, Summary | As its terms | The overhead against the data |
| Share of a component | `component / Data`, for each of the seven. Null when the component is absent. | share | 1, Summary | As its terms | The part of the data that the component takes |
| Listed | Whether UnreferencedFileSize is present | boolean | 1, Summary | Exact | Whether NonLive and Unreferenced come from a listing of the storage |

The components that make the waste and the overhead are **Decided** on 21 October 2026: the waste is the non-live files, the unreferenced files, the deleted rows, and the stale columns. The metadata and the footers are overhead. They are outside the waste, outside the order of the tables by waste, and outside the denominator of the shares.

The non-live and the unreferenced files overlap, and a summary does not say which files are in both. The unreferenced files include the non-live files that are still stored, as section 6.4 says. So the stack counts the bytes of the listing once: NonLive takes as much of UnreferencedFileSize as the non-live files can account for, and Unreferenced takes the rest. The non-live files beyond that were already deleted, by a VACUUM for example, and are not in the stack. Without a listing, NonLive is NonLiveFileSize, an upper bound, and Unreferenced is null. **Decided** on 21 October 2026.

The footers and the stale columns are bytes inside the data files, so Live leaves them out, and Live, Deleted, Stale, and Footers together are FileSize when Live is not clipped. Deleted is an estimate over the whole file, so it holds a share of the footers and of the stale columns too, and the split between the four is approximate even when their sum is exact. Live also holds what the files store besides the column chunks and the footer, such as page indexes and bloom filters. The tables are ordered by Waste, the largest first, then by Total, then by name.

The stack leaves out the delete files of an Iceberg table, DeleteFileSize, and the files of the Delta deletion vectors, whose sizes are DeletionVectorSize on each file. Both are stored bytes that a later draft may add as a component.

### 10.3 Files of a table

The measures read a `Distribution` with its quartiles taken by position in the sorted values: for n values, FirstQuartile is the value at position `floor(n/4)` from 0, Median at `floor(n/2)`, and ThirdQuartile at `floor(3n/4)`. The collector computes them so, as section 4.3 states, and the small-file shares below depend on it.

The size figures of the files of a table come from the files when the footprint keeps them, and otherwise from FileSizes:

| Measure | Formula | Unit | Level, depth | Kind | Meaning |
|---|---|---|---|---|---|
| File size figures | From the files: the count, the sum, the minimum, the three quartiles by position, and the maximum of `File.FileSize` over the files of the table that have one. From the totals: the members of `FileSizes`, with `FileCount` for a missing Count and `FileSize` for a missing Sum. The minimum, the quartiles, and the maximum are null when neither is there. | files, bytes | 1, Summary | Exact | The shape of the file sizes |
| AverageFileSize | `FileSize / FileCount` | bytes | 1, Summary | Exact | The average size of a data file |
| SmallFileCount | The files with `File.FileSize < SmallFileSize`. Null without the files. | files | 1, `Files` | Exact | The small files |
| TinyFileCount | The same below TinyFileSize | files | 1, `Files` | Exact | The very small files |
| SmallFileShare | From the files: `SmallFileCount` over the files with a FileSize. From FileSizes: 1 when Maximum is below SmallFileSize, else 0.75 when ThirdQuartile is, else 0.5 when Median is, else 0.25 when FirstQuartile is, else `1 / FileSizes.Count` when Minimum is, else 0. FileCount stands for a missing Count, and when both are absent the share for the minimum is 0. Null when FileSizes has none of the five values. | share | 1, Summary | Exact from the files. A lower bound from FileSizes. | The part of the files that are small |
| TinyFileShare | The same with TinyFileSize | share | 1, Summary | As SmallFileShare | The part of the files that are very small |
| VOrderShare | From the files, when at least one has `IsVOrdered`: the files with `File.IsVOrdered` true over the files where it is present. Otherwise `min(1, VOrderedFileCount / FileCount)`. | share | 1, Summary | Exact over the files whose V-Order is known. From the totals, a file that no tag marks and whose footer was not read counts as not V-Ordered, so the share is a lower bound when FooterCount is less than FileCount. | The part of the files that are V-Ordered |
| VOrderBytesShare | The sum of `File.FileSize` of the V-Ordered files over the sum of `File.FileSize` of the files where `IsVOrdered` is present. Null without the files. | share | 1, `Files` | Exact over those files | The part of the bytes in V-Ordered files |

The two small-file shares are **Decided** on 21 October 2026.

Why the share from FileSizes is a lower bound. At the `Summary` depth the footprint keeps five values of the sizes, not the sizes. When ThirdQuartile, the value at position `floor(3n/4)`, is below the threshold, the `floor(3n/4) + 1` smallest files are all below it, and `floor(3n/4) + 1` is at least `3n/4`, so at least 75% of the files are small. The same holds for the median at 50% and for the first quartile at 25%, and the minimum gives one file. The share between two of these steps is unknown, so the measure takes the step it can prove, and the true share is at most the next step. The bound holds for quartiles taken by position. With interpolated quartiles it does not: for two files, an interpolated third quartile below the threshold proves only one file. Open question 2 of section 12 asks whether fixed size classes would say more.

### 10.4 History of a table

| Measure | Formula | Unit | Level, depth | Kind | Meaning |
|---|---|---|---|---|---|
| LastCommitAt | `SnapshotTimestamp`; else the latest `Operation.LastAt` of the table; else `LastModifiedAt`. The first that is present. | timestamp | 1, Summary | Exact from SnapshotTimestamp or from the operations. From LastModifiedAt an estimate, and a lower bound: the newest data file was written at or before the last commit. | When the table last changed |
| CommitsPerDay | `CommitsRead / max(1, days from OldestCommitAt to LastCommitAt)`, the days as a fraction. Null when a value is absent. | commits per day | 1, Summary | Exact over the commits read, as LastCommitAt is. An estimate when LastCommitAt comes from LastModifiedAt. | How often the table is committed, over the span of the commits read |
| DaysSinceLastCommit | `max(0, floor(days from LastCommitAt to Collection.CompletedAt))`, with `Collection.StartedAt` when CompletedAt is absent. Null when a value is absent. | days | 1, Summary | Exact as LastCommitAt is. An upper bound when LastCommitAt comes from LastModifiedAt. | How long ago the table last changed, counted to the collection |
| HasVacuum | Whether the Name of an Operation of the table contains `VACUUM`, in any case. Null when the table has no Operation, since the list is empty when the commits carry no operation name. | boolean | 1, Summary | Exact over the commits read | Whether a VACUUM ran among the commits read |
| LastVacuumAt | The latest `Operation.LastAt` of those operations | timestamp | 1, Summary | Exact over the commits read | When the last VACUUM ran |
| HasOptimize, LastOptimizeAt | The same with `OPTIMIZE` | boolean, timestamp | 1, Summary | Exact over the commits read | Whether and when an OPTIMIZE ran |

Commits per day are over the span of the commits read, from OldestCommitAt, the oldest of them, to the newest, with the days since the last commit beside it, counted to the collection. **Decided** on 21 October 2026. A span shorter than a day counts as one day, so that a burst of commits gives their number and not a rate per hour.

HasVacuum and HasOptimize false say that no such operation is among the commits read, not that none ever ran. The names are those of Delta. Iceberg names a rewrite `replace` and expires snapshots outside the commits, so for an Iceberg table the two say nothing.

The removal times of the non-live files, NonLiveOldestRemovedAt and NonLiveNewestRemovedAt, are shown as they are, beside the stack.

### 10.5 Ratios and averages of a table

| Measure | Formula | Unit | Level, depth | Kind | Meaning |
|---|---|---|---|---|---|
| CompressionRatio | `UncompressedSize / CompressedSize` | ratio | 2, Summary | Exact over the footers read | How much the codecs and the encodings shrink the data |
| DeletedRowShare | `DeletedRowCount / RowCount` | share | 1, Summary | Exact | The part of the stored rows that are deleted |
| AverageRowGroupRows | `RowGroupRowCounts.Sum / RowGroupRowCounts.Count`; else `RowCount / RowGroupCount` when FooterCount equals FileCount | rows | 2, Summary | Exact over the footers read | The average rows of a row group |
| FooterCoverage | `FooterCount / FileCount` | share | 2, Summary | Exact | How complete the totals of level 2 are |
| StaleColumnShare | `StaleColumnSize / CompressedSize` | share | 2, Summary | Exact over the footers read | The part of the compressed column data that the table no longer declares |
| NonLiveShare | `NonLiveFileSize / (FileSize + NonLiveFileSize)` | share | 1, Summary | Upper bound, since it reads NonLiveFileSize and not the NonLive of the stack | The non-live files that the log tracks, against the files of the table and those together |
| AveragePartitionSize | `PartitionSizes.Sum / PartitionSizes.Count`; else `FileSize / PartitionCount` | bytes | 1, Summary | Exact | The average data size of a partition |
| DictionaryFallbackShare | The sum of `Column.FallbackChunkCount` over the sum of `Column.DictionaryChunkCount`, over the columns of the table that have both | share | 2, Summary | Exact over those columns and the footers read | The part of the dictionary-encoded column chunks that fell back to another encoding |
| Partitions | `PartitionCount`; else the number of Partitions kept for the table. Null when the table has neither. | partitions | 1, Summary | Exact | The partitions that hold data |
| PartitionsKept | The number of Partitions kept for the table, 0 when there are none | partitions | 1, Summary | Exact | How many partitions the footprint keeps |
| PartitionsOldestModifiedAt, PartitionsNewestModifiedAt | The earliest and the latest `Partition.LastModifiedAt` over the partitions kept. Null when none has it. | timestamp | 1, Summary, format 0.2 | Exact over the partitions kept | The range of the last change of the partitions |

### 10.6 Column

| Measure | Formula | Unit | Level, depth | Kind | Meaning |
|---|---|---|---|---|---|
| NullShare | `min(1, NullCount / ValueCount)` | share | 2, or 1 for Iceberg; Summary | Exact | The part of the values that are null |
| CompressionRatio | `UncompressedSize / CompressedSize` | ratio | 2, Summary | Exact over the footers read | How much the column shrinks |
| DictionaryShare | `DictionaryChunkCount / ChunkCount` | share | 2, Summary | Exact over the footers read | The part of the column chunks with a dictionary page |
| DictionaryFallbackShare | `FallbackChunkCount / DictionaryChunkCount` | share | 2, Summary | Exact over the footers read | The part of the dictionary-encoded column chunks that fell back to another encoding |
| AverageValueLength | `UnencodedByteArrayDataBytes / (ValueCount - NullCount)` | bytes | 2, Summary | Exact over the footers read | The average length of a variable-length value, such as a string |
| FilesWithoutColumnShare | `FileCountWithoutColumn / FooterCount` of its table | share | 2, Summary | Exact over the footers read | The part of the files that do not store the column, for example because it was added later |

A partition column that the files do not store has no totals, so its measures are null.

Over a set of columns, two measures add the columns first. The storage views use them for a table, a source, and the collection:

| Measure | Formula | Unit | Level, depth | Kind | Meaning |
|---|---|---|---|---|---|
| ColumnNullShare | The sum of NullCount over the sum of ValueCount, over the columns that have both | share | 2, Summary | Exact over those columns | The part of the values that are null, over the set |
| ColumnCompressionRatio | The sum of UncompressedSize over the sum of CompressedSize, over the columns that have both | ratio | 2, Summary | Exact over those columns | How much the set shrinks |

The CompressionRatio of a table is that of 10.5, from the totals of the table. ColumnCompressionRatio of the same table leaves out the stale columns, the partition columns, and the columns that lack a size, and its sizes come from the table format when ColumnTotalsSource says so, so the two can differ.

### 10.7 Collection

| Measure | Formula | Unit | Level, depth | Kind | Meaning |
|---|---|---|---|---|---|
| Sources | The sources with a distinct Id | sources | 1, Summary | Exact | The sources |
| Tables | The tables | tables | 1, Summary | Exact | The tables |
| Columns | The columns | columns | 1, Summary | Exact | The columns |
| Files | The sum of `FileCount` over the tables that have it | files | 1, Summary | Lower bound when a table lacks it | The data files |
| FileSize | The sum of `FileSize` over the tables that have it | bytes | 1, Summary | As Files | The bytes of the data files |
| RowCount | The sum of `RowCount` over the tables that have it | rows | 1, Summary | As Files | The rows stored |
| Footprint | The sum of the Total of the stacks, over the tables whose Total is not null | bytes | 1, Summary | As Total | Everything that the storage holds for the tables |
| Waste | The sum of the Waste of the stacks, over the tables whose Waste is not null | bytes | 1, Summary | As Waste | The space that holds no live data |
| Overhead | The sum of the Overhead of the stacks, over the tables whose Overhead is not null | bytes | 1, Summary | As Overhead | The metadata and the footers |
| Depths | The number of tables for each value of DetailDepth | tables | 1, Summary | Exact | How deep the footprint goes |

A sum is null when no table has the value. A reader that shows a sum says how many tables lack the value.

### 10.8 Names in the views

The storage views, `@pqax/views`, compute the measures in their view model: `buildModel`, and `columnTree` for the two measures over a set of columns. Their names in lower camel case:

| Measures | In the view model |
|---|---|
| Live, NonLive, Unreferenced, Deleted, Stale, Metadata, Footers | `table.stack.components`, a list in this order whose `key` is `live`, `nonLive`, `unreferenced`, `deleted`, `stale`, `metadata`, `footers`, with the value in `bytes` |
| Waste, Overhead, Data, Total, WasteShare, OverheadShare, Listed, the share of a component | `table.stack.waste`, `overhead`, `data`, `total`, `wasteShare`, `overheadShare`, `listed`, and `share` on each component |
| File size figures | `table.files.count`, `sum`, `minimum`, `firstQuartile`, `median`, `thirdQuartile`, `maximum` |
| SmallFileCount, SmallFileShare, TinyFileCount, TinyFileShare | `table.files.smallFiles.count`, `smallFiles.shareAtLeast`, `tinyFiles.count`, `tinyFiles.shareAtLeast` |
| VOrderShare, VOrderBytesShare | `table.files.vOrder.share`, `vOrder.bytesShare`, with `vOrder` null when the share is |
| LastCommitAt, CommitsPerDay, DaysSinceLastCommit, HasVacuum, LastVacuumAt, HasOptimize, LastOptimizeAt | `table.maintenance.lastCommitAt`, `commitsPerDay`, `daysSinceLastCommit`, `hasVacuum`, `lastVacuumAt`, `hasOptimize`, `lastOptimizeAt` |
| CompressionRatio, DeletedRowShare, AverageFileSize, AverageRowGroupRows, FooterCoverage, StaleColumnShare, NonLiveShare, AveragePartitionSize, DictionaryFallbackShare of a table | `table.measures.compressionRatio`, `deletedRowShare`, `averageFileSize`, `averageRowGroupRows`, `footerCoverage`, `staleColumnShare`, `nonLiveShare`, `averagePartitionSize`, `dictionaryFallbackShare` |
| Partitions, PartitionsKept, PartitionsOldestModifiedAt, PartitionsNewestModifiedAt | `table.partitions.count`, `kept`, `oldestModifiedAt`, `newestModifiedAt` |
| NullShare, CompressionRatio, DictionaryShare, DictionaryFallbackShare, AverageValueLength, FilesWithoutColumnShare of a column | `column.nullShare`, `compressionRatio`, `dictionaryShare`, `dictionaryFallbackShare`, `averageValueLength`, `filesWithoutColumnShare` |
| ColumnNullShare, ColumnCompressionRatio | `nullShare` and `compressionRatio` of a node of `columnTree` |
| The measures of the collection | `collection.totals.sources`, `tables`, `columns`, `files`, `fileSize`, `rowCount`, `footprint`, `waste`, `overhead`, and `collection.depths` |
| SmallFileSize, TinyFileSize | The options `smallFileSize` and `tinyFileSize` of `buildModel` |

### 10.9 The existing analyzers

The figures of two analyzers of Delta tables, against the fields and the measures. The Delta Analyzer of DAX Studio is described from its documentation, and `delta_analyzer` of semantic-link-labs from its source, both as of October 2026.

| DAX Studio | semantic-link-labs | PQAX |
|---|---|---|
| Files | Summary: Parquet Files | FileCount |
| Avg File | | AverageFileSize |
| Max File | | FileSizes.Maximum |
| Small Files, below 128 MB | | SmallFileCount at the `Files` depth; SmallFileShare, a lower bound, at the `Summary` depth |
| Row Groups | Summary: Row Groups | RowGroupCount |
| Min Rows/RG, Max Rows/RG | Summary: Min Rows Per Row Group, Max Rows Per Row Group | RowGroupRowCounts.Minimum, RowGroupRowCounts.Maximum |
| Avg Rows/RG | Summary: Avg Rows Per Row Group | AverageRowGroupRows. semantic-link-labs divides the rows that the table returns, without the deleted rows, so the two differ when rows are deleted. |
| Rows | Summary: Row Count | RowCount includes the deleted rows; semantic-link-labs counts `RowCount - DeletedRowCount` |
| Compressed, Uncompressed | Summary: Total Size; Columns: Compressed Size, Uncompressed Size | CompressedSize, UncompressedSize of the table and of the column |
| | Row Groups: Compression Ratio | `RowGroup.CompressedSize / RowGroup.UncompressedSize`, the inverse of a CompressionRatio, at the `Row groups` depth |
| V-Order | Summary: VOrder Enabled | VOrderShare, a share rather than a flag |
| Deletion Vectors | | DeletedRowCount, DeletedRowShare |
| Last Modified | | LastModifiedAt |
| | Columns: Size Percent Of Table | Not defined here: `Column.CompressedSize` over the sum of the CompressedSize of the columns of its table |
| | Columns: Cardinality | DistinctCount at level 4, or LargestChunkDistinctCount as a lower bound |

The considerations of DAX Studio read the same measures: SmallFileCount and SmallFileShare against 30% for compacting small files, AverageRowGroupRows against 1,000,000 rows, RowGroupRowCounts.Maximum against 16,000,000 rows, and DeletedRowShare against 5% for purging deletion vectors.

## 11. Views

VPAX carries the same content twice: the full object model, and flat views that the Excel workbook loads directly. PQAX can do the same if it helps tools that cannot follow references, such as a spreadsheet or a BI tool.

A view would follow three rules:

1. It contains nothing that cannot be computed from the object model.
2. It replaces references with names, so that each row can be read on its own.
3. A reader may ignore it, and a writer may omit it.

Candidate views, each a flat table over whatever depth the footprint has:

| View | One row per | Content |
|---|---|---|
| Tables | Table | The fields and the totals of the table |
| Columns | Column of a table | The fields and the totals of the column |
| Partitions | Partition | The partition and its totals |
| Files | File | The fields of the file, its partition, and its totals |
| Row groups | Row group | Table name, file path, and the fields of the row group |
| Column chunks | Column chunk | Table, column, file, row group, and the fields of the chunk |

The first version of the format has no views. The tables of the [serialization](serialization.md) are already flat, and a later version can add views without breaking a reader. **Decided** on 6 October 2026.

## 12. Open questions

The [backlog](backlog.md) tracks questions 1, 3, and 5 before version 1.0, and question 7 later.

1. **Choosing the depth.** How a collector decides the depth of each table: a limit on the objects of one table, a limit for the whole footprint, or both. The limits need real lakehouses to be tuned.
2. **Distribution.** Whether the quartiles are enough, or whether fixed size classes would say more about small files.
3. **File paths.** Paths are long and mostly random, so they dominate the size of the `Files` depth. Whether a collection may leave them out.
4. **Reading a sample of footers.** ~~Whether a collector may read only some footers of a very large table. The totals are then partial, as rule 3 of section 4.2 already allows.~~ Answered in version 0.1: the collector reads every footer by default, and with `MaxFootersPerTable` a sample spread evenly over the files sorted by modification time, and the totals of level 2 then cover the footers read, as `FooterCount` says.
5. **Commit limit.** How many recent commits to read and keep by default. In the test, reading 30 commits for each table took most of the requests.
6. **Dictionary page size.** On 9,613 column chunks written by Fabric, the size derived from the two offsets was always within the size of the chunk. It still has to be compared with the page header, and tested on files from other writers.
7. **Comparing footprints.** Whether TableId, File.Path, and FieldId are enough to compare two footprints of the same lakehouse taken at different times. Proposed answer in section 13: they are, with the name as the fallback where an identifier is absent, the partition values, and the commit version.
8. **Shortcuts.** Whether a table that is a shortcut records where its data really is.
9. **Distinct counts in the footer.** For files written by the Fabric warehouse, the count was exact on all 79 columns compared with a VPAX. A semantic model counts blank as one more value. The same comparison is still missing for files written by the lakehouse.

## 13. Comparing two footprints

A comparison takes two footprints of the same lakehouse, the older and the newer, and says which tables and columns were added, removed, or kept, and how much each kept one changed. This section answers open question 7. Its rules and its cases are **Proposed** on 24 October 2026, and wait for the maintainer.

### 13.1 Identity

An `Id` identifies an object in its own footprint only, as section 5 says, so no rule compares the `Id` of two footprints. These rules decide when an object of the older footprint and one of the newer are the same object:

| Object | The same object when | Fallback |
|---|---|---|
| Table | Both have a TableId and the two are equal | When at least one of the two has no TableId: the same source, the same Namespace, and the same Name. Two tables whose TableIds differ are never the same table. |
| Source, for the fallback of a table | The same Kind and the same Name | |
| Column of a kept table | Both have a FieldId and the two are equal | When at least one of the two has no FieldId: the same Name. Two columns whose FieldIds differ are never the same column. |
| Partition of a kept table | The same Values, one by one, a null value equal to a null value | None. A partition without Values is not compared, and nor are the partitions of its table. |
| File of a kept table | The same Path | None. A file without a Path is not compared, and nor are the files of its table. |
| Commit of a kept table | The same CommitId | |

A name, a path, and a value compare as they are written, case included, as the paths of the unreferenced files do in section 6.4. A Namespace compares element by element.

Each object is the same as one object of the other footprint at most. Tables pair in three passes: first the tables with the same TableId and the same source, Namespace, and Name; then those with the same TableId; then, by the fallback, those with the same source, Namespace, and Name. Columns pair the same way with FieldId and Name. Each pass takes the objects of the older footprint in the order of the file, and pairs each with the first object of the newer footprint, in the order of the file, that matches and is not paired yet. Files, partitions, and commits pair in the same way, in one pass.

Why each rule:

- **TableId.** Delta keeps `metaData.id`, and Iceberg `table-uuid`, as long as the table lives: a rename does not change it, and a table dropped and created again gets a new one. It is the only field of a table that says it is the same table.
- **The fallback by name, for a table.** A folder of Parquet files with no table format has no TableId, and neither has a table whose log the collector could not read. The name in its source is then the only identity left. The fallback is rejected when both tables have a TableId and the two differ: a table created again under the same name holds none of the files of the old one, and pairing the two would show a delta that no change of the table made. Matching by name first was considered and rejected for that reason.
- **The source.** A source has no identifier that survives across footprints: its `Id` is local, and Location and Identifiers are absent when the collector reads a local folder. Kind and Name are what every collector writes.
- **FieldId.** The column mapping of Delta and the field ids of Iceberg survive a rename, and a column dropped and added again gets a new one.
- **The fallback by name, for a column.** Without column mapping, a Delta column has no field id, and its name is how the files store it, so a rename is impossible and the name identifies the column.
- **Partition values.** A partition has no identifier of its own. Its values are what makes it a partition, so they identify it. They are data values, excluded by default as section 6.6 says, so most footprints do not have them, and their partitions are then compared by PartitionCount and PartitionSizes only.
- **File path.** The table format identifies a data file by its path relative to the table, and a writer never writes a new file under the path of an old one. A deletion vector that changes keeps the path of its file, so the file is kept and changed.
- **CommitId.** It is the version of a Delta commit and the snapshot id of an Iceberg snapshot.

### 13.2 Cases

| Case | Result |
|---|---|
| A table renamed, with the same TableId | Kept and renamed |
| A table dropped and created again: a new TableId, the same name | The older table is removed and the newer one added, and both are marked as recreated |
| A table without a TableId on either side, renamed | Removed and added: nothing else identifies it |
| A table with a TableId on one side only, the same name | Kept, by the fallback |
| A column renamed, with the same FieldId | Kept and renamed |
| A column renamed, without FieldId | Removed and added |
| A column dropped and added again under the same name | Removed and added when the two have FieldIds, which then differ; kept otherwise |
| A column whose DataType or IsNullable changed | Kept and changed |
| A table kept at a depth below `Files` in one of the footprints | Its files are not compared. Its totals and measures are, from what each footprint holds. |
| A footprint that keeps no Partitions, or no Commits | The partitions, or the commits, are not compared |
| A value absent in either footprint | Its delta is null, as rule 2 of section 10.1 says for a measure |

### 13.3 What is compared

| Object | Values |
|---|---|
| Collection | The measures of section 10.7, and the tables of each DetailDepth, a depth that one footprint lacks counting 0 tables |
| Kept table | Each total of section 6.4, with the members of its distributions, and each measure of section 10 for a table |
| Kept column | Each total of section 6.5, and each measure of section 10.6 for a column |
| Files of a kept table | The files added, removed, kept, and changed, and the FileSize of those added and of those removed |
| Partitions of a kept table | The same, over the partitions |
| Commits of a kept table | The commits added, removed, and kept |

Each value is given with its older value, its newer value, and the delta, the newer minus the older. The delta of a timestamp is in milliseconds. A boolean, a text, and a list have no delta. The delta is null when either value is absent.

A kept file is changed when its FileSize, RowCount, DeletedRowCount, or DeletionVectorSize differs, and a kept partition when its FileCount, FileSize, RowCount, or DeletedRowCount differs. A kept table is changed when its Name or Namespace, its SnapshotId, a total, a column, a file, a partition, or a commit differs; a kept column when its Name, DataType, IsNullable, or a total differs. The measures do not make an object changed, since DaysSinceLastCommit changes with the time of the collection alone, and the other measures follow from the totals.

A report of the changes orders the tables by the size of their change, the largest first: the change of the Total of the stack of section 10.2 by default, the Total itself for a table added or removed. A table whose change is null comes after those that have one.
