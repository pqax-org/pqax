# Pqax.Collector

Collects the footprint of Delta tables and writes it as a PQAX file. It reads the Delta log of each table and, at collection level 2, the footer of each Parquet file. It never reads row data, and it keeps no data value unless asked. The format is defined by the [PQAX serialization](https://github.com/pqax-org/pqax/blob/main/docs/serialization.md) and the [PQAX object model](https://github.com/pqax-org/pqax/blob/main/docs/object-model.md); the file is written with the [Pqax](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax/README.md) library.

| | |
|---|---|
| Targets | netstandard2.0 and net8.0 |
| Dependencies | `Pqax`. On netstandard2.0, the `System.Text.Json` package. No Parquet library: the collector parses footers itself. |
| Storage | A folder with the OneLake layout, or OneLake through its DFS endpoint, with GET requests only |
| Levels | 1, the Delta logs, and 2, also the Parquet footers |
| Checkpoints | Through an `ICheckpointReader`. [Pqax.Collector.ParquetNet](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Collector.ParquetNet/README.md) provides one with Parquet.Net. |

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

For OneLake, the storage takes a delegate that returns an access token for the scope `https://storage.azure.com/.default`, so that the library does not depend on Azure.Identity. The application signs in with any `TokenCredential` of Azure.Identity, or gets the token another way:

```csharp
// Azure.Identity, in the application: any TokenCredential.
TokenCredential credential = new ClientSecretCredential(tenantId, clientId, Environment.GetEnvironmentVariable("PQAX_CLIENT_SECRET"));
// or new ClientCertificateCredential(tenantId, clientId, "collector.pfx"), new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned),
// new AzureCliCredential(), new DefaultAzureCredential(), new InteractiveBrowserCredential()

using var storage = new OneLakeStorage("My Workspace", async cancellation =>
    (await credential.GetTokenAsync(new TokenRequestContext(new[] { OneLakeStorage.Scope }), cancellation)).Token);
IReadOnlyList<TableLocation> tables = await TableDiscovery.DiscoverAsync(storage, "Sales.Lakehouse", CancellationToken.None);
```

The delegate is called before every request. The credentials of Azure.Identity cache their tokens, but a delegate that calls another source should cache the token itself until shortly before it expires, as `TokenCache` of [Pqax.Cli](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Cli/README.md) does. A token acquired elsewhere, such as the one of a notebook, is passed as it is: `new OneLakeStorage("My Workspace", _ => Task.FromResult(token))`.

A service principal or a managed identity needs the Fabric tenant setting that lets service principals use Fabric APIs, and a workspace role that reads OneLake: Contributor or higher is known to work, and Viewer alone does not. The [Pqax.Cli README](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Cli/README.md#permissions-of-a-service-principal) has the details and what is not verified yet. A refused request is a `StorageException` with `StatusCode` 401 or 403 and `IsAccessDenied` true.

On a real workspace of 78 tables and 5,236 files, a collection at level 2 with every footer took 7,623 requests, one per footer, and wrote a 1.8 MB file. With a fixed 32 KB tail it read 177 MB in 2 minutes; with the adaptive tail it read 103 MB in 38 seconds, with the same output.

The workspace and the items are named both by name or both by id. OneLake answers `400 FriendlyNameSupportDisabled` to a workspace id followed by item names.

`pqax collect` in [Pqax.Cli](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Cli/README.md) does both.

## Public API

| Type | What it is |
|---|---|
| `PqaxCollector` | `CollectAsync(tables, progress, cancellation)` returns a `FootprintBuilder` with every table, its details, and the collection: library, times, level, requests, and bytes read |
| `CollectorOptions` | The options below. `Validate()` checks them; the constructor of the collector calls it. |
| `DetailDepth` | `Summary`, `Files`, `RowGroups`, `ColumnChunks` |
| `TableLocation` | A table to collect: source kind and name, namespace, name, folder in the storage, and the locations and identifiers to record. See [Locations](#locations). |
| `TableDiscovery` | `ListItemsAsync` lists the lakehouses, warehouses, mirrored databases, and SQL databases at the root of a storage. `DiscoverAsync` finds the tables of one item: `Tables/<table>` or `Tables/<schema>/<table>`, by where `_delta_log` is. In a warehouse, every folder under `Tables` is a schema. |
| `SourceKinds` | The kinds of source: `OneLake lakehouse`, `OneLake warehouse`, and others |
| `CollectionProgress` | Reported each time a table is done |
| `IssueCodes` | The codes of the issues that the collector records |
| `IStorage`, `StorageEntry` | Read-only storage: `ListAsync(path)` and `ReadRangeAsync(path, offset, length)`. A listing gives path, size, folder or file, and modification time. |
| `LocalStorage`, `OneLakeStorage`, `OneLakeStorageOptions` | The two storages. Both count their requests and bytes through `IStorageStatistics`. |
| `StorageException` | A failed request, with its HTTP status when there is one: a timeout or a broken connection has none. The message never holds a path. |
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
| `MaxCommitsPerTable` | 30 | The commit files read for the history: operations and commits. The commits that the state needs are read anyway, so a table reads the greater of the limit and those. See [Commits](#commits). |
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
| The commits that the state needs | One request each, replayed into the state: the commits after the checkpoint, or every commit when the state comes from the JSON commits alone |
| Older commits | One request each, for the history, the most recent first, until the commit files read reach `MaxCommitsPerTable`. See [Commits](#commits). |
| One listing of the table folder, at any depth | When the files of the version are known, at every level. `OneLakeStorage` lists with `recursive=true`, in as many pages as the listing takes, and `LocalStorage` counts one request. Through another storage, each folder is a listing of its own, and `_delta_log` and `_change_data` are not listed. It gives `unreferencedFileCount` and `unreferencedFileSize`. See [Unreferenced files](#unreferenced-files). |
| The footer of each file | At level 2: the last `FooterTailBytes` bytes, and a second request only for a longer footer |

Every request goes through the `IStorage`, so the collection states its requests and bytes. When the storage implements `IStorageStatistics`, those numbers come from it and include retries and the pages of a listing.

The log is replayed by the rules of the Delta protocol: a file is identified by its path and its deletion vector, and the latest action wins. A table whose checkpoint is not read keeps what the JSON commits say: when the log still has every commit from version 0, the collector replays them and the table is complete; otherwise its files are unknown, its file totals are `null`, it gets the issue `DeltaCheckpointNotRead`, and it has the `Summary` depth.

### Commits

The collector chooses the commit files to read from the latest version back:

1. Every commit that the state needs: those after the checkpoint, or all of them when the state comes from the JSON commits alone. The limit does not apply to them.
2. Then older commits, the most recent first, until the commit files read reach `MaxCommitsPerTable` or the log has no older one.

So a table reads the greater of the limit and the commits that the state needs, within the commits that the log keeps. A limit of 0 reads only the commits that the state needs. `commitsRead` is the number of commit files read. The commits of the table, kept from the `Files` depth, hold every one of them, and the operations and `oldestCommitAt` cover them all. A commit file whose read fails counts toward the limit, since it cost a request, and gets an issue instead of a place in `commitsRead` and in the commits.

| Limit | Checkpoint | Latest version | Commit files in the log | Commits read |
|---|---|---|---|---|
| 30 | 100 | 145 | 0 to 145 | 45: 101 to 145 |
| 0 | 100 | 145 | 0 to 145 | 45: 101 to 145 |
| 50 | 100 | 145 | 0 to 145 | 50: 96 to 145 |
| 60 | 100 | 145 | 90 to 145 | 56: 90 to 145 |
| 30 | None | 39 | 0 to 39 | 40: 0 to 39 |

## What the footprint holds

The collector fills every field of the object model that a Delta table and its footers give, and follows the rule that unknown is not zero: a value that was not read is `null`, a total with an unknown term is `null`, and a known empty list is empty.

| Object | From |
|---|---|
| Source | The `TableLocation` of its tables: kind, name, location, and identifiers. See [Locations](#locations). |
| Table | The `TableLocation` for the location, then `metaData`, `protocol`, the domains, the listings, the history, and totals over the files, the footers, and the row groups. `fileSizes`, `rowGroupRowCounts`, and `partitionSizes` are distributions with quartiles taken by position in the sorted values. `nonLiveOldestRemovedAt` and `nonLiveNewestRemovedAt` are the earliest and the latest `remove.deletionTimestamp` of the files that `nonLiveFileCount` counts. |
| Column | `schemaString`: name, type as Spark writes it in DDL, nullability, field id and physical name of column mapping, comment. Totals over the column chunks of the footers read, or at level 1 the null count of the statistics of the log, when every file has one. |
| Partition spec and partitions | `partitionColumns` and `add.partitionValues`, by physical name with column mapping. `lastModifiedAt` of a partition is the latest `add.modificationTime` of its files, and `null` when one of them has none, as for the table. |
| Physical schema and physical column | The schemas of the footers. A leaf maps to a column by the field id of its top-level field when the table has field ids, and otherwise by the physical name, or the name. |
| Writer | `created_by`, and V-Order from the `VORDER` tag of the file or the footer keys |
| Operation and commit | `commitInfo` of the commits read: time, operation, engine, blind append, metrics. Never a user name, a notebook, or a cluster. |
| File, row group, column chunk | The log and the footers, from the depth that keeps them. The encodings of a column chunk are sorted. |
| Issue | What went wrong, with a code. A table keeps 100 issues, and one more counts the rest. |

Partitions and commits are kept from the `Files` depth, as in the prototype.

### Unreferenced files

| Rule | Detail |
|---|---|
| Data file | A file of the listing of the table folder, at any depth, whose name ends with `.parquet`, in any case, outside `_delta_log` and `_change_data` |
| Referenced | Its path relative to the table folder equals the decoded path of a live file of the version. An absolute path of the log that starts with the table location is compared without it. The comparison is case-sensitive. |
| Unreferenced | Every other data file: removed files that a VACUUM has not deleted yet, and files that no commit referenced, such as those of an aborted write |
| Size | The sum of the sizes that the listing gives. A file without a size counts in `unreferencedFileCount` only. |
| Unknown | Both fields are `null` when the files of the version are unknown, and when the listing fails, which is then an issue: `AccessDenied` or `DataFilesNotListed` |

### Locations

`TableDiscovery` fills the location of each table, and the location and the identifiers of its source, from the storage and from the item as the caller names it.

| Storage | Source `location` | Table `location` | Source `identifiers` |
|---|---|---|---|
| `OneLakeStorage`, by names | `abfss://<workspace>@<host>/<item>`, such as `abfss://My Workspace@onelake.dfs.fabric.microsoft.com/Sales.Lakehouse` | The source location, then `/Tables/<table>`, or `/Tables/<schema>/<table>` | None |
| `OneLakeStorage`, by ids | `abfss://<workspace id>@<host>/<item id>` | The same | `workspaceId` and `itemId` |
| `LocalStorage` | The full path of the item folder, with the separators of the platform, such as `C:\data\Sales.Lakehouse` | The full path of the table folder | None |
| Another storage, or a storage that wraps one of these | None | None | None |

- The workspace and the item are written as the caller gave them, without escaping. `<host>` is the host of the endpoint, `onelake.dfs.fabric.microsoft.com` by default.
- An identifier is recorded only for a workspace or an item given as an id: a GUID with hyphens. A listing of the DFS endpoint has no item id, so with names the identifiers stay `null`: the collector does not guess them.
- The tables with the same source kind, name, and location share one source. A `TableLocation` built by hand records what its constructor is given.
- A local location is a full path, so it can hold the name of the user of the machine. The object model marks the locations and the identifiers as names, for the obfuscation to replace.

## Issues

| Code | When |
|---|---|
| `AccessDenied` | The storage denied access, with status 401 or 403, to a folder, a commit, or the footer of a file. On the listing of the table folder, `unreferencedFileCount` and `unreferencedFileSize` are `null`. |
| `TableNotRead` | The table could not be read for another reason |
| `DeltaLogMissing` | The table folder has no Delta log |
| `DeltaLogIncomplete` | Commits that the state needs are missing |
| `DeltaCheckpointNotRead` | The checkpoint was not read. See above. |
| `DeltaCheckpointMissing` | `_last_checkpoint` names a checkpoint that the log does not have |
| `DeltaCommitNotRead` | A commit file could not be read or parsed |
| `DeltaSchemaNotParsed` | `schemaString` is not valid |
| `DeltaStatsNotParsed` | The statistics of some files are not valid JSON |
| `DataFilesNotListed` | The listing of the table folder for its data files failed for another reason than a denied access; `unreferencedFileCount` and `unreferencedFileSize` are `null` |
| `FileOutsideTable` | A file has an absolute path, so its footer was not read |
| `FooterNotRead` | The read of a footer failed for another reason than a denied access, or the log gives no size |
| `FooterEncrypted` | The footer is encrypted: the file ends with `PARE` |
| `FooterInvalid` | Not a valid Parquet footer, or the file is shorter than the log says |
| `FooterTooLarge` | The footer is larger than 64 MB |
| `FooterColumnsEncrypted` | Column chunks have no metadata because their columns are encrypted; the footer counts as not read |
| `IssuesTruncated` | The table had more than 100 issues |

### Footer read failures

A footer that cannot be read becomes an issue with the path of its file, and the collection goes on. The code depends on the failure:

| Status or failure | Code | Effect on the totals |
|---|---|---|
| 401 or 403 | `AccessDenied` | The footer counts as not read: `footerCount` and the totals of level 2 leave out the file and its row groups and column chunks, and the file keeps what the log says |
| 404, another status after the retries, a timeout, a broken connection, or an error of a local file | `FooterNotRead` | The same |
| 416, or fewer bytes than the log says | `FooterInvalid` | The same |
| Bytes that are not a valid footer | `FooterInvalid`, `FooterEncrypted`, or `FooterTooLarge` | The same |
| Column chunks with no metadata | `FooterColumnsEncrypted` | The same |
| No size in the log, or a file outside the table | `FooterNotRead` or `FileOutsideTable` | The same, with no request |
| Any other exception | `FooterNotRead` | The same |

A table with files and no footer read has the collection level 1 and at most the `Files` depth.

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
