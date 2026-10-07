# Pqax.Collector

Collects the footprint of Delta tables and writes it as a PQAX file. It reads the Delta log of each table and, at collection level 2, the footer of each Parquet file. It never reads row data, and it keeps no data value unless asked. The format is defined by the [PQAX serialization](../../../docs/serialization.md) and the [PQAX object model](../../../docs/object-model.md); the file is written with the [Pqax](../Pqax/README.md) library.

| | |
|---|---|
| Targets | netstandard2.0 and net8.0 |
| Dependencies | `Pqax`. On netstandard2.0, the `System.Text.Json` package. No Parquet library: the collector parses footers itself. |
| Storage | A folder with the OneLake layout, or OneLake through its DFS endpoint, with GET requests only |
| Levels | 1, the Delta logs, and 2, also the Parquet footers |
| Checkpoints | Through an `ICheckpointReader`. [Pqax.Collector.ParquetNet](../Pqax.Collector.ParquetNet/README.md) provides one with Parquet.Net. |

The package is not published yet. Reference the project `src/dotnet/Pqax.Collector/Pqax.Collector.csproj`.

## Collect

```csharp
using Pqax;
using Pqax.Collector;
using Pqax.Collector.ParquetNet;

var storage = new LocalStorage(@"conformance\fixtures\lakehouse");   // the folder plays the workspace
var tables = new List<TableLocation>();
foreach (string item in await TableDiscovery.ListItemsAsync(storage, CancellationToken.None))
{
    tables.AddRange(await TableDiscovery.DiscoverAsync(storage, item, CancellationToken.None));
}

var collector = new PqaxCollector(storage, new CollectorOptions(), new ParquetNetCheckpointReader());
FootprintBuilder footprint = await collector.CollectAsync(tables, progress: null, CancellationToken.None);
footprint.Collection.CollectorApp = "MyTool";
footprint.Write("lakehouse.pqax");
```

For OneLake, the storage takes a delegate that returns an access token for the scope `https://storage.azure.com/.default`, so that the library does not depend on Azure.Identity. The application signs in:

```csharp
var credential = new InteractiveBrowserCredential();   // Azure.Identity, in the application
using var storage = new OneLakeStorage("My Workspace", async cancellation =>
    (await credential.GetTokenAsync(new TokenRequestContext(new[] { OneLakeStorage.Scope }), cancellation)).Token);
IReadOnlyList<TableLocation> tables = await TableDiscovery.DiscoverAsync(storage, "Sales.Lakehouse", CancellationToken.None);
```

On a real workspace of 78 tables and 5,236 files, a collection at level 2 with every footer took 7,623 requests, one per footer, and wrote a 1.8 MB file. With a fixed 32 KB tail it read 177 MB in 2 minutes; with the adaptive tail it read 103 MB in 38 seconds, with the same output.

The workspace and the items are named both by name or both by id. OneLake answers `400 FriendlyNameSupportDisabled` to a workspace id followed by item names.

`pqax collect` in [Pqax.Cli](../Pqax.Cli/README.md) does both.

## Public API

| Type | What it is |
|---|---|
| `PqaxCollector` | `CollectAsync(tables, progress, cancellation)` returns a `FootprintBuilder` with every table, its details, and the collection: library, times, level, requests, and bytes read |
| `CollectorOptions` | The options below. `Validate()` checks them; the constructor of the collector calls it. |
| `DetailDepth` | `Summary`, `Files`, `RowGroups`, `ColumnChunks` |
| `TableLocation` | A table to collect: source kind and name, namespace, name, folder in the storage, and an optional location to record |
| `TableDiscovery` | `ListItemsAsync` lists the lakehouses, warehouses, mirrored databases, and SQL databases at the root of a storage. `DiscoverAsync` finds the tables of one item: `Tables/<table>` or `Tables/<schema>/<table>`, by where `_delta_log` is. In a warehouse, every folder under `Tables` is a schema. |
| `SourceKinds` | The kinds of source: `OneLake lakehouse`, `OneLake warehouse`, and others |
| `CollectionProgress` | Reported each time a table is done |
| `IssueCodes` | The codes of the issues that the collector records |
| `IStorage`, `StorageEntry` | Read-only storage: `ListAsync(path)` and `ReadRangeAsync(path, offset, length)`. A listing gives path, size, folder or file, and modification time. |
| `LocalStorage`, `OneLakeStorage`, `OneLakeStorageOptions` | The two storages. Both count their requests and bytes through `IStorageStatistics`. |
| `StorageException` | A failed request, with its HTTP status. The message never holds a path. |
| `StorageExtensions` | `ReadAllAsync`: a whole file in one request, with or without its size |
| `ICheckpointReader`, `DeltaAction` and its kinds | The contract of a checkpoint reader: `AddAction`, `RemoveAction`, `MetadataAction`, `ProtocolAction`, `DomainMetadataAction`, `SidecarAction`, `CheckpointMetadataAction`, and `CommitInfoAction` for commits |
| `FooterReader`, `ParquetFooter` | The footer parser, usable alone: `FooterReader.ReadAsync(path, fileSize)` reads the tail of a file, and `ParquetFooter.Parse(bytes)` decodes a `FileMetaData` |
| `ParquetSchemaElement`, `ParquetLeafColumn`, `ParquetRowGroup`, `ParquetColumnChunk`, `ParquetPageEncodingStats`, `ParquetKeyValue`, `ParquetSortingColumn` | What a footer holds |
| `ParquetFooterException` | A footer that cannot be read, with the issue code |

## Options

| Option | Default | Meaning |
|---|---|---|
| `Level` | 2 | 1 reads the Delta logs only. 2 also reads the Parquet footers. |
| `Depth` | `ColumnChunks` | The detail kept for every table. A table keeps `Summary` when its files are unknown, and at most `Files` when no footer was read. |
| `MaxFootersPerTable` | 0 | The footers read for each table, 0 for all. A limit picks files evenly over the files sorted by modification time, and the totals of level 2 cover the files read, as `footerCount` says. |
| `MaxCommitsPerTable` | 30 | The most recent commits read for the history: operations and commits. The commits that the state needs are read anyway. |
| `Parallelism` | 16 | The storage requests in flight, for the whole collection |
| `IncludeDataValues` | false | Keeps the bounds of the column chunks, the parameters of the operations, and the check constraints of the table properties |
| `IncludePartitionValues` | false | Keeps the values of the partitions and the file paths as the log has them. Without it, a value in a path becomes a number: `Year=2024/part-1.parquet` is written `Year=#1/part-1.parquet`. |
| `FooterTailBytes` | 32768 | The bytes read from the end of the first file of a table, and the most read from any file in one request |
| `FooterTailMargin` | 1.25 | The next files of a table are read with the largest footer seen so far times this factor. The footers of a table have about the same size, so one request per footer stays the rule while the bytes drop to what the footers take. |
| `FooterTailFloorBytes` | 8192 | The fewest bytes read from the end of a file |

## What it reads

For each table:

| Request | When |
|---|---|
| One listing of `_delta_log` | Always. Its files give `metadataFileCount` and `metadataSize`, and `_delta_log/_sidecars` is listed too when it exists. |
| `_last_checkpoint` | When it exists. A hint only: the newest complete checkpoint of the listing is read. |
| The checkpoint | Through the checkpoint reader: a classic checkpoint, every part of a multi-part checkpoint, a v2 checkpoint in Parquet, or in JSON, which the collector reads itself, and its sidecar files |
| The commits after the checkpoint | One request each, replayed into the state |
| Older commits | One request each, for the history, up to `MaxCommitsPerTable` commits in all |
| The footer of each file | At level 2: the last `FooterTailBytes` bytes, and a second request only for a longer footer |

Every request goes through the `IStorage`, so the collection states its requests and bytes. When the storage implements `IStorageStatistics`, those numbers come from it and include retries and the pages of a listing.

The log is replayed by the rules of the Delta protocol: a file is identified by its path and its deletion vector, and the latest action wins. A table whose checkpoint is not read keeps what the JSON commits say: when the log still has every commit from version 0, the collector replays them and the table is complete; otherwise its files are unknown, its file totals are `null`, it gets the issue `DeltaCheckpointNotRead`, and it has the `Summary` depth.

## What the footprint holds

The collector fills every field of the object model that a Delta table and its footers give, and follows the rule that unknown is not zero: a value that was not read is `null`, a total with an unknown term is `null`, and a known empty list is empty.

| Object | From |
|---|---|
| Source | The location of the table: kind and name |
| Table | `metaData`, `protocol`, the domains, the listing, the history, and totals over the files, the footers, and the row groups. `fileSizes`, `rowGroupRowCounts`, and `partitionSizes` are distributions with quartiles taken by position in the sorted values. |
| Column | `schemaString`: name, type as Spark writes it in DDL, nullability, field id and physical name of column mapping, comment. Totals over the column chunks of the footers read, or at level 1 the null count of the statistics of the log, when every file has one. |
| Partition spec and partitions | `partitionColumns` and `add.partitionValues`, by physical name with column mapping |
| Physical schema and physical column | The schemas of the footers. A leaf maps to a column by the field id of its top-level field when the table has field ids, and otherwise by the physical name, or the name. |
| Writer | `created_by`, and V-Order from the `VORDER` tag of the file or the footer keys |
| Operation and commit | `commitInfo` of the commits read: time, operation, engine, blind append, metrics. Never a user name, a notebook, or a cluster. |
| File, row group, column chunk | The log and the footers, from the depth that keeps them. The encodings of a column chunk are sorted. |
| Issue | What went wrong, with a code. A table keeps 100 issues, and one more counts the rest. |

Partitions and commits are kept from the `Files` depth, as in the prototype.

## Issues

| Code | When |
|---|---|
| `AccessDenied` | The storage denied access |
| `TableNotRead` | The table could not be read for another reason |
| `DeltaLogMissing` | The table folder has no Delta log |
| `DeltaLogIncomplete` | Commits that the state needs are missing |
| `DeltaCheckpointNotRead` | The checkpoint was not read. See above. |
| `DeltaCheckpointMissing` | `_last_checkpoint` names a checkpoint that the log does not have |
| `DeltaCommitNotRead` | A commit file could not be read or parsed |
| `DeltaSchemaNotParsed` | `schemaString` is not valid |
| `DeltaStatsNotParsed` | The statistics of some files are not valid JSON |
| `FileOutsideTable` | A file has an absolute path, so its footer was not read |
| `FooterNotRead` | The read of a footer failed, or the log gives no size |
| `FooterEncrypted` | The footer is encrypted: the file ends with `PARE` |
| `FooterInvalid` | Not a valid Parquet footer, or the file is shorter than the log says |
| `FooterTooLarge` | The footer is larger than 64 MB |
| `FooterColumnsEncrypted` | Column chunks have no metadata because their columns are encrypted; the footer counts as not read |
| `IssuesTruncated` | The table had more than 100 issues |

## Footer parser

The parser reads the Thrift compact protocol: field headers with short and long deltas, booleans in the header, zigzag varints, doubles, binaries, lists, sets, maps, structures, and UUIDs. It skips a field that it does not know, of any type, and a known field with an unexpected type. It decodes `FileMetaData`, `SchemaElement` with its logical and converted types, `RowGroup`, `ColumnChunk`, `ColumnMetaData`, `Statistics`, `PageEncodingStats`, `SizeStatistics`, the bloom filter, column index, and offset index lengths, and the key-value metadata.

| Kept | Dropped without allocating |
|---|---|
| Null and distinct counts, and whether the statistics have bounds | The bounds, unless the footer is parsed with data values; then they are kept as text |
| The keys of the key-value metadata, with the value for keys that contain `vorder`, the key `org.apache.spark.version`, and keys that end with `string_prefix_length`, up to 200 characters | The values of other keys, which can hold data values: Fabric copies the statistics of the log into the footer |
| The schema, once | `path_in_schema` of every column chunk, column-level key-value metadata, the level histograms, geospatial statistics, column orders, and the encryption details |

It refuses an encrypted footer (`PARE`), a file that does not end with `PAR1`, a footer length that does not fit the file, a footer over 64 MB, a required field that is missing, a row group whose column chunks do not match the leaves of the schema, and any length or count that goes beyond the bytes left. It never throws anything but `ParquetFooterException` on bad bytes.

## Limits

- Collection levels 3 and 4, Iceberg tables, and folders of Parquet files with no table format are not supported in this version.
- A checkpoint larger than 512 MB is not read.
- The log compaction files, `<x>.<y>.compacted.json`, are counted with the log and not used.
- All the files of a table are kept in memory until the footprint is built, as row objects in the builder.
