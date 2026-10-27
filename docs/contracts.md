# PQAX contracts

How the implementations of PQAX are organized, what each one must offer, and what comes next. Started on 7 October 2026. When this document disagrees with the [serialization](serialization.md) or the [object model](object-model.md), those two win.

## 1. Version 0.1

What the first version delivers:

| Piece | Content |
|---|---|
| C# `Pqax` | Reads, writes, and validates PQAX files. Targets netstandard2.0 and net8.0. No dependency beyond zip and JSON. Published as a NuGet package from release 0.1.0. |
| C# `Pqax.Cli` | `pqax check <file>`, `pqax info <file>`, and `pqax collect`, over the library and the collector |
| C# `Pqax.Collector` | Collects Delta tables from a local folder or from OneLake, at collection levels 1 and 2, and writes a PQAX file. Its own Parquet footer parser. No Parquet library. |
| C# `Pqax.Collector.ParquetNet` | Reads Delta checkpoints, which are Parquet files with nested data, with Parquet.Net. It plugs into the collector through an interface. |
| TypeScript `@pqax/pqax` | Reads and validates PQAX files in a browser and in Node, with no dependency |
| Python `pqax` | Reads, validates, and writes PQAX files with the standard library, loads tables into data frames when pandas or polars is installed, and has a command line |
| Conformance kit | A JSON Schema of every part, sample files with expected results, the list of validation rules with their codes, and the fixture that the collector tests use |
| Tests | For every piece, written from the conformance kit and the contracts of section 4, not from the code |

Not in 0.1: the view model and the derived measures, Iceberg tables, obfuscation, collection level 3 and 4. The packages are published from release 0.1.0, as section 7.1 says.

### 1.1 Version 0.2

Version 0.2, started on 21 October 2026, adds five optional fields to the format and changes nothing else:

| Piece | Content |
|---|---|
| Format | Draft 0.5 of the object model and draft 0.3 of the serialization: `unreferencedFileCount`, `unreferencedFileSize`, `nonLiveOldestRemovedAt`, and `nonLiveNewestRemovedAt` on `tables`, and `lastModifiedAt` on `partitions` |
| Conformance kit | The schema has the five fields. The valid samples are format 0.2, and `format-0.1.pqax` keeps a sample of format 0.1. The fixture has a data file that no commit references. |
| C#, TypeScript, Python | The rows and the types have the five fields. The readers open files of format 0.1 and 0.2, and the C# and Python writers write format 0.2. |
| C# `Pqax.Collector` | Lists the folder of each table to find the unreferenced files, and computes the removal times of the non-live files and the last modification of each partition |

## 2. Layout

```
README.md
CHANGELOG.md                      what each release holds
RELEASING.md                      how a release is published
LICENSE                           MIT
brand/                            the logo: the mark, the wordmark, the lock-ups, and their PNG exports
package.json                      the npm workspaces of the two TypeScript packages
Pqax.slnx                         every .NET project
docs/
├── object-model.md               the logical content of a footprint
├── serialization.md              how a PQAX file is written
├── contracts.md                  this document
├── decisions.md                  what was decided, and when
└── backlog.md                    what comes after version 0.1
conformance/                      the kit, language neutral
├── README.md
├── rules.md                      the validation rules and their codes
├── schema/                       JSON Schema 2020-12 of the manifest and of every table
├── samples/                      valid files, and invalid/ files that every validator must reject
├── expected/                     the results that a reader must get from each sample
├── fixtures/                     a synthetic lakehouse and warehouse for the collector tests
├── make_kit.py                   rebuilds samples and expected results
└── cross_check.py                checks every writer against every reader and validator
src/dotnet/Pqax                   the format library
src/dotnet/Pqax.Cli               the command line
src/dotnet/Pqax.Collector         the collector
src/dotnet/Pqax.Collector.ParquetNet
src/typescript/pqax               the TypeScript package
src/typescript/views              the storage views: a view model and three views
src/python/pqax                   the Python package
tests/dotnet/Pqax.Tests           xunit
tests/dotnet/Pqax.Collector.Tests
tests/dotnet/Pqax.Cli.Tests
tests/typescript                  vitest
tests/typescript-views            vitest, in Node and in browsers
tests/python                      pytest
research/prototype                the prototype that tested the serialization
research/detail-encoding          the experiment that compared encodings of a large table
.github/workflows/ci.yml          builds and runs every suite
.github/workflows/publish.yml     publishes a release to NuGet, npm, and PyPI
.github/workflows/pages.yml       deploys site/ to GitHub Pages, the site of pqax.org
site/                             the one-page site of pqax.org, with its favicons; tools/ redirects to pqax.tools
templates/                        files to copy elsewhere: the scheduled collection, and the Power BI template over pqax export
```

Nothing under `src/`, `tests/`, and `conformance/` depends on a path outside those three folders. `research/` holds the experiments that the documents cite.

## 3. Rules for the code

1. Read first: the [serialization](serialization.md), the [object model](object-model.md), [decisions.md](decisions.md), this document, and `conformance/README.md`.
2. Tests, samples, fixtures, and documents use synthetic data only. Nothing comes from a real lakehouse, not even a name.
3. Runtime dependencies: none in `Pqax`, in the TypeScript package, and in the Python package. `Pqax` may reference the `System.Text.Json` package for netstandard2.0. Development and test dependencies are allowed: xunit, vitest, pytest, a JSON Schema validator.
4. Public members of a library have documentation comments. Nullable reference types are on. A build ends with no warnings.
5. Names: a table or field keeps the name of the file, in the casing of the language: `fileCount` in TypeScript and in the file, `FileCount` in C#, `file_count` only for Python functions and never for the keys of a table, which stay as in the file.
6. Every package has a README in the style of the documents of this repository: short sentences, tables, no marketing.
7. A deviation from the contracts of section 4 is documented in the README of the package, with its reason.
8. Nothing is published from a working copy. Releases follow the versioning of section 7.1.

## 4. Contracts

The contracts are what the tests check and what lets three implementations stay consistent. The tests are written from this section and from the conformance kit, not from the code.

### 4.1 Validation rules and their codes

Every validator reports the same codes. An issue has a code, a message, and when they apply the part, the table, and the field. The section numbers refer to the [serialization](serialization.md); "model" refers to the [object model](object-model.md).

| Code | The rule that is broken | Where |
|---|---|---|
| `container.method` | An entry is neither stored nor deflated | 3.2 |
| `container.encrypted` | An entry is encrypted | 3.2 |
| `container.zip64` | The archive uses ZIP64 | 3.3 |
| `container.partName` | The name of a part has a character other than ASCII letters, digits, `.`, `-`, `_` | 3.4 |
| `container.caseCollision` | Two entry names differ only in case | 3.4 |
| `container.manifestMissing` | There is no `manifest.json` entry | 3.5 |
| `json.invalid` | A part is not valid JSON | 4.1 |
| `json.topLevel` | The top level of a part is not an object | 4.1 |
| `json.bom` | A part starts with a byte order mark | 4.2 |
| `json.duplicateMember` | An object repeats a member name | 4.3 |
| `json.integer` | A number is not an integer | 4 |
| `json.integerRange` | An integer is beyond 2^53 - 1 in absolute value | 4 |
| `json.timestamp` | A timestamp is not in the form `2026-10-05T08:15:30.000Z` | 4 |
| `json.type` | A value has not the JSON type of its field: a string where the model says integer, an array where it says map, and so on | 4 |
| `manifest.required` | `format`, `formatVersion`, `collection`, `collection.level`, or `parts` is missing, or a part descriptor has no `name` or `tables` | 7.1 |
| `manifest.format` | `format` is not `pqax` | 7.1 |
| `manifest.formatVersion` | `formatVersion` is not two numbers separated by a dot | 7.1 |
| `manifest.versionUnknown` | The first number of `formatVersion` is not one the validator knows | 9.4 |
| `manifest.firstPart` | The first part is not `summary.json` | 7.2 rule 2 |
| `manifest.duplicatePart` | A part is listed twice | 7.2 |
| `manifest.partMissing` | A listed part has no entry in the archive | 7.1 |
| `part.tables` | The tables in a part differ from the tables that the manifest states for it | 7.2 rules 1 and 7 |
| `part.kind` | A detail table is in `summary.json`, or a summary table is in another part | 7.2 rules 2 and 3 |
| `part.singleTable` | A detail part holds more than one table | 7.2 rule 3 |
| `part.tooLarge` | A detail part holds more than 250,000 rows | 7.2 rule 4 |
| `part.rowCount` | An array of a table has not the length that the manifest states for that part | 5.1 rule 2, 7.2 rule 7 |
| `table.required` | A required field is missing, or has `null` in a row. The required fields are those of the object model, plus the parent reference and `position` of serialization 5.3 | model, 5.3 |
| `table.idUnique` | An `id` is not a positive integer, or is repeated in its table | 6 rule 1 |
| `table.reference` | A reference points to a row that the file does not have | 6 rule 4 |
| `depth.files` | A table at the `Summary` depth has rows in `files` | 8 |
| `depth.rowGroups` | A table below the `Row groups` depth has rows in `rowGroups` | 8 |
| `depth.columnChunks` | A table below the `Column chunks` depth has rows in `columnChunks` | 8 |
| `totals.fileCount` | `fileCount` of a table differs from the number of its files kept | 8 rule 2 |
| `totals.fileSize` | `fileSize` of a table differs from the sum over its files kept | 8 rule 2 |
| `totals.rowGroupCount` | `rowGroupCount` of a table differs from the number of its row groups kept | model 4.2 |
| `totals.compressedSize` | `compressedSize` of a table differs from the sum over its column chunks kept | model 4.2 |
| `totals.columnCompressedSize` | `compressedSize` of a column differs from the sum over its column chunks kept | model 4.2 |
| `embedded.partMissing` | A VPAX has no part with the name of the footprint. Reported by a reader that opens an embedded footprint and by a validator asked to validate one, never for a standalone file | 12 |

The `json.*` rules apply to the manifest as well as to the parts. A total that is `null` is not compared. A validator reads what a reader reads, and it reports every issue it finds instead of stopping at the first. A rule that it cannot evaluate because an earlier rule failed is skipped, not reported.

A reader is more tolerant than a validator: it skips a byte order mark, ignores what it does not know, reads a listed `summary.json` that is missing as empty summary tables, fails the read of a listed detail part that is missing with `manifest.partMissing`, and refuses a file only for `manifest.format`, `manifest.versionUnknown`, `container.manifestMissing`, `embedded.partMissing`, and JSON that does not parse.

### 4.2 Expected results of a sample

For each sample in `conformance/samples`, `conformance/expected/<sample>.json` holds what every reader must get from it:

- the `format`, the `formatVersion`, and the names of the parts, in order;
- the number of rows of every table;
- totals: the sum of `fileSize` and of `rowCount` over `tables`, and the sum of `compressedSize` over `columnChunks`;
- for every table: its `id`, its `fileCount`, the number of its column chunks, and the sum of their `compressedSize`, followed from column chunk to physical column to physical schema to table;
- the three columns with the largest sum of `compressedSize` over their column chunks, with their `id`;
- for a VPAX sample: the name of the embedded part and whether it is stored or deflated.

`conformance/README.md` fixes the exact JSON shape. For each file in `conformance/samples/invalid`, `conformance/samples/invalid/expected.json` lists the codes that a validator must report, and a validator must report at least those.

### 4.3 C# library `Pqax`

Namespace `Pqax`. One class per table of the file, named after its object with the suffix `Row`: `SourceRow`, `TableRow`, `ColumnRow`, `PartitionSpecRow`, `PartitionFieldRow`, `SortOrderRow`, `SortFieldRow`, `PhysicalSchemaRow`, `PhysicalColumnRow`, `WriterRow`, `OperationRow`, `IssueRow`, `ModelBindingRow`, `TableBindingRow`, `ColumnBindingRow`, `PartitionRow`, `FileRow`, `RowGroupRow`, `ColumnChunkRow`, `DeleteFileRow`, `CommitRow`. A row has one property per field of the object model, named as in the file with a capital first letter: `long?` for integers, `string?`, `bool?`, `DateTimeOffset?` for timestamps, `IReadOnlyList<string>?` for lists, `IReadOnlyDictionary<string, string>?` for maps, `Distribution?`, `DeltaDetails?`, `IcebergDetails?`, and `long?` for a reference, with the name of the reference field: `ColumnRow.Table`, `ColumnChunkRow.RowGroup`. An `id` is `long`. The fields of format 0.2 follow the same rule: `TableRow` has `UnreferencedFileCount`, `UnreferencedFileSize`, `NonLiveOldestRemovedAt`, and `NonLiveNewestRemovedAt`, and `PartitionRow` has `LastModifiedAt`.

```csharp
public sealed class PqaxDocument : IDisposable
{
    public static PqaxDocument Open(string path);
    public static PqaxDocument Open(Stream stream, bool leaveOpen = false);   // seekable; parts are read on demand
    public static PqaxDocument OpenEmbedded(string vpaxPath, string partName = "Footprint.pqax");
    public static PqaxDocument OpenEmbedded(Stream vpaxStream, string partName = "Footprint.pqax", bool leaveOpen = false);
    public Manifest Manifest { get; }
    public Summary Summary { get; }            // every summary table, read when the document opens
    public string? EmbeddedPart { get; }       // the name of the part when the document was opened inside a VPAX, and
    public string? EmbeddedCompression { get; } // "stored" or "deflated"; both null for a standalone file
    public int RowCount(string tableName);     // from the manifest
    public PqaxTable<PartitionRow> ReadPartitions();   // and ReadFiles, ReadRowGroups, ReadColumnChunks, ReadDeleteFiles, ReadCommits:
                                                        // all the parts of the table, concatenated and cached; empty when the file has none
    public RawTable ReadRaw(string tableName); // every field, including the ones this version does not know, as JSON values
}
public sealed class PqaxTable<TRow>
{
    string Name; int Count; IReadOnlyList<TRow> Rows; TRow? FindById(long id);
    IReadOnlyDictionary<string, IReadOnlyList<JsonElement?>> ExtraFields;   // the fields that are not in the model
}
public sealed class Summary { PqaxTable<SourceRow> Sources; PqaxTable<TableRow> Tables; /* one property per summary table */ }
public sealed class Manifest
{
    string Format; string FormatVersion; CollectionInfo Collection;
    string? ObfuscatorDictionaryId; string? ObfuscatorLib; string? ObfuscatorLibVersion;
    IReadOnlyList<PartInfo> Parts; IReadOnlyDictionary<string, JsonElement> Extensions;
}
public sealed class PartInfo { string Name; IReadOnlyDictionary<string, int> Tables; }

public sealed class FootprintBuilder
{
    public FootprintBuilder(CollectionInfo collection);
    public FootprintBuilderOptions Options { get; }   // RowsPerPart = 250000, CompressionLevel
    public long AddSource(SourceRow row);             // one Add method per table; for a table with ids it assigns the next Id when the row
                                                      // has none, keeps a positive Id that the row brings, and returns it; for the others
                                                      // it returns the row position; Write refuses a repeated id
    public static FootprintBuilder From(PqaxDocument document);   // copies every table, extra fields included, for tools that rewrite a file
    public void Write(Stream stream);
    public void Write(string path);
}

public static class PqaxValidator { ValidationResult Validate(string path); ValidationResult Validate(Stream stream);
                                    ValidationResult ValidateEmbedded(string vpaxPath, string partName = "Footprint.pqax"); ValidationResult ValidateEmbedded(Stream vpaxStream, string partName = "Footprint.pqax"); }
public sealed class ValidationResult { bool IsValid; IReadOnlyList<ValidationIssue> Issues; int ChecksRun; }
public sealed class ValidationIssue { string Code; string Message; string? Part; string? Table; string? Field; }
```

Rules of the library: the reader reads the central directory itself, so that an embedded footprint is read in place and a part is read only when asked; the writer may use `ZipArchive`. The reader keeps unknown fields and unknown manifest members, and `FootprintBuilder.From` writes them back. Writing a footprint into a VPAX package needs `System.IO.Packaging` and belongs to the VPAX library, not here. The `System.Text.Json`, `System.Memory`, and `Microsoft.Bcl.*` packages are allowed for netstandard2.0, because they are the base library itself.

The derived measures of section 10 of the [object model](object-model.md) are in the static class `StorageMeasures`, in the namespace `Pqax` of the same library, as decided on 22 October 2026. They are pure functions over the rows that the reader returns, with no new dependency. A measure that cannot be computed is null, never 0: `double?` for bytes, shares, and ratios, `long?` for counts and days, `DateTimeOffset?` for times. The two thresholds of section 10.1 are optional, through `MeasureOptions`, 128 MiB and 8 MiB by default.

```csharp
public static class StorageMeasures
{
    public const long DefaultSmallFileSize = 134217728, DefaultTinyFileSize = 8388608;
    public static IReadOnlyList<string> StackComponents, WasteComponents, OverheadComponents;   // "live", "nonLive", ...
    public static FootprintMeasures Compute(PqaxDocument document, MeasureOptions? options = null);   // reads files and partitions when the manifest lists rows
    public static IReadOnlyList<TableStorage> ForTables(Summary summary, IReadOnlyList<FileRow>? files, CollectionInfo? collection,
                                                        MeasureOptions? options = null, IReadOnlyList<PartitionRow>? partitions = null);
                                                        // one per row of tables, in the order of the file
    public static IReadOnlyList<ColumnStorage> ForColumns(Summary summary);   // one per row of columns, in the order of the file
    public static CollectionMeasures ForCollection(Summary summary, IReadOnlyList<TableStorage> tables);   // tables: from ForTables
    public static StorageStack StackOf(StackInput input);
    public static double? DeletedEstimate(double? fileSize, double? deletedRowCount, double? rowCount);
    public static double? SmallShareAtLeast(double? count, double? minimum, double? firstQuartile, double? median,
                                            double? thirdQuartile, double? maximum, double threshold);
    public static double? CommitsPerDay(double? commits, DateTimeOffset? oldestCommitAt, DateTimeOffset? newestCommitAt);   // commits: CommitsRead
    public static long? DaysBetween(DateTimeOffset? from, DateTimeOffset? to);
    public static double? Ratio(double? a, double? b);
}
public sealed class MeasureOptions { long SmallFileSize; long TinyFileSize; }
public sealed class FootprintMeasures
{
    IReadOnlyList<TableStorage> Tables; IReadOnlyList<ColumnStorage> Columns; CollectionMeasures Collection;
    bool FilesRead; IReadOnlyList<string> Warnings;
}
public sealed class TableStorage
{
    long? Id; int Row; string? Name; string Label; long? RowCount; long? FileCount; long? FileSize;
    StorageStack Stack; SmallFiles SmallFiles; SmallFiles TinyFiles; FileMeasures Files; TableMeasures Measures;
    MaintenanceMeasures Maintenance; PartitionMeasures Partitions;
    void WriteTo(Utf8JsonWriter writer);   // one table of the JSON of pqax tables --json, which shows some of the measures
}
public sealed class StorageStack
{
    double? Live, NonLive, Unreferenced, Deleted, Stale, Metadata, Footers;   // the seven components
    double? Waste, Overhead, Data, Total, WasteShare, OverheadShare; bool Listed; string? DeletedSource;   // "files" or "table"
    double? Component(string key);
    double? Share(string key);   // the share of a component: the component over Data
}
public sealed class SmallFiles { long Threshold; long? Count; double? ShareAtLeast; }   // SmallFileCount and SmallFileShare, or the tiny ones
public sealed class FileMeasures   // the file size figures of section 10.3, from the files or from FileSizes, and the V-Order shares
{
    long? Count; double? Sum, Minimum, FirstQuartile, Median, ThirdQuartile, Maximum; double? VOrderShare, VOrderBytesShare;
}
public sealed class TableMeasures
{
    double? CompressionRatio, DeletedRowShare, AverageFileSize, AverageRowGroupRows, FooterCoverage, StaleColumnShare,
            NonLiveShare, AveragePartitionSize, DictionaryFallbackShare;
}
public sealed class MaintenanceMeasures
{
    DateTimeOffset? LastCommitAt; double? CommitsPerDay; long? DaysSinceLastCommit;
    bool? HasVacuum; DateTimeOffset? LastVacuumAt; bool? HasOptimize; DateTimeOffset? LastOptimizeAt;
}
public sealed class PartitionMeasures { long? Count; long Kept; DateTimeOffset? OldestModifiedAt, NewestModifiedAt; }   // Partitions, PartitionsKept, ...
public sealed class ColumnStorage
{
    long? Id; int Row; long? Table; string? Name; string Label;   // Table: the id of its table, null when no table of the file has it
    double? NullShare, CompressionRatio, DictionaryShare, DictionaryFallbackShare, AverageValueLength, FilesWithoutColumnShare;
}
public sealed class CollectionMeasures
{
    long Sources, Tables, Columns; long? Files, FileSize, RowCount; double? Footprint, Waste, Overhead;
    IReadOnlyDictionary<string, long> Depths;   // the tables for each DetailDepth, "Unknown" for a table without one
}
```

The library offers every measure of section 10, as decision 7 of the [decisions](decisions.md) asks. For a table: the storage stack of 10.2 with the share of each component; the file size figures, SmallFileCount, SmallFileShare, TinyFileCount, TinyFileShare, VOrderShare, and VOrderBytesShare of 10.3; LastCommitAt, CommitsPerDay, DaysSinceLastCommit, HasVacuum, LastVacuumAt, HasOptimize, and LastOptimizeAt of 10.4; and the ratios and the partitions of 10.5. For a column, the six measures of 10.6, and for the collection, those of 10.7. The two measures over a set of columns of 10.6, ColumnNullShare and ColumnCompressionRatio, are left to the views. The names are those of section 10 in the casing of C#, grouped as the view model of 10.8 groups them: `Stack`, `Files`, `Maintenance`, `Measures`, and `Partitions` of a table, `ColumnStorage` for a column, and `CollectionMeasures` with the totals and `Depths`. VOrderShare and VOrderBytesShare are on `FileMeasures`, where the view model has the object `vOrder`. `pqax tables` shows some of the measures, and its JSON, which `TableStorage.WriteTo` writes, keeps the keys it had in version 0.2.0.

The comparison of two footprints of the same lakehouse, section 13 of the [object model](object-model.md), is in the static class `StorageDiff` of the same namespace, with the rules that section proposes: a table is the same table when its TableId is, and by its source, namespace, and name when one of the two has none; a column by its FieldId, then its name; a partition by its values, a file by its path, and a commit by its CommitId. Each kept table and column has the older value, the newer value, and the delta of each total of section 6 and of each measure of section 10, the delta null when either value is. `pqax diff OLDER NEWER [--part NAME] [--sort total|waste|rows|files|name] [--top N] [--json]` of `Pqax.Cli` writes it as a report or as one JSON object, the same text and the same JSON as `python -m pqax diff`.

```csharp
public static class StorageDiff
{
    public static IReadOnlyList<string> Orders { get; }   // "total", "waste", "rows", "files", "name"
    public static FootprintDiff Compare(PqaxDocument older, PqaxDocument newer, MeasureOptions? options = null);
    public static IReadOnlyList<TableChange> Rank(FootprintDiff diff, string order = "total", int? top = null);
}
public sealed class FootprintDiff
{
    FootprintMeasures Older, Newer; long SmallFileSize, TinyFileSize; IReadOnlyList<ValueChange> Collection;
    IReadOnlyList<TableEntry> Added, Removed;   // in the order of the newer file, and of the older file
    IReadOnlyList<KeptTable> Kept;              // in the order of the newer file
    int ChangedCount; ValueChange? CollectionValue(string name);
}
public sealed class ValueChange { string Name; object? Older, Newer; double? Delta; bool IsChanged; }   // the delta of a time in milliseconds
public sealed class TableEntry { TableSide Table; bool Recreated; }
public sealed class TableSide { long? Id; int Row; string Label; string? Name; IReadOnlyList<string>? Namespace; string? TableId, SnapshotId, DetailDepth;
                                long ColumnCount; long? FileCount, FileSize, RowCount; double? Total, Waste; }
public sealed class KeptTable { string MatchedBy; bool Renamed, IsChanged; TableSide Older, Newer; IReadOnlyList<ValueChange> Totals, Measures;
                                ColumnChanges Columns; DetailChanges? Files, Partitions, Commits; ValueChange? Total(string name); ValueChange? Measure(string name); }
public sealed class ColumnChanges { IReadOnlyList<ColumnSide> Added, Removed; IReadOnlyList<KeptColumn> Kept; }
public sealed class ColumnSide { long? Id; int Row; string Label; string? Name; long? FieldId; string? DataType; bool? IsNullable; long? CompressedSize; }
public sealed class KeptColumn { string MatchedBy; bool Renamed, IsChanged; ColumnSide Older, Newer; IReadOnlyList<ValueChange> Totals, Measures; }
public sealed class DetailChanges { long Added, Removed, Kept; long? Changed, AddedFileSize, RemovedFileSize; }   // the last three null for the commits
public sealed class TableChange { string Kind; TableEntry? Entry; KeptTable? Kept; string Label; }
```

### 4.4 TypeScript package `pqax`

ES module, no runtime dependency, runs in browsers and in Node 20 or later. Deflate goes through `DecompressionStream("deflate-raw")`. Node-only helpers live in a second entry point, `pqax/node`, so that the main entry imports nothing from Node.

```ts
export interface ByteSource { readonly size: number; read(offset: number, length: number): Promise<Uint8Array>; }
export type PqaxInput = Blob | ArrayBuffer | Uint8Array | ByteSource;
export function openPqax(input: PqaxInput): Promise<PqaxDocument>;
export function openEmbedded(input: PqaxInput, partName?: string): Promise<PqaxDocument>;   // default "Footprint.pqax"
export interface PqaxDocument {
  readonly manifest: Manifest;
  readonly summary: Summary;                   // typed columnar tables: summary.tables.fileCount is (number | null)[]
  rowCount(table: string): number;             // from the manifest
  readTable<K extends DetailTableName>(name: K): Promise<DetailTables[K]>;   // all parts, concatenated and cached
  readRaw(name: string): Promise<Record<string, unknown[]>>;
}
export function rows<T extends ColumnarTable>(table: T): Iterable<RowOf<T>>;   // one object per row
export function indexById(table: { id: number[] }): Map<number, number>;        // id to row position
export function validate(input: PqaxInput): Promise<ValidationResult>;         // the codes of section 4.1
export function embeddedSource(input: PqaxInput, partName?: string): Promise<ByteSource>;   // the footprint inside a VPAX, to validate it
// PqaxDocument also has embeddedPart: string | null and embeddedCompression: "stored" | "deflated" | null
// pqax/node: export function fromFile(path: string): Promise<ByteSource>;
```

A field that a part does not have is `undefined` on the table object. A missing value is `null`. A summary table that the file does not have is `undefined`. Types for every table and field are exported, and they match the schema of the kit.

The reader computes no measure. The derived measures of section 10 of the [object model](object-model.md) are in the package `@pqax/views`, from the same folder, as decided on 22 October 2026: `buildModel` computes every measure of a table, a column, and the collection, and `columnTree` the two measures over a set of columns. They are pure functions over what the reader returns. A measure that cannot be computed is `null`. The two thresholds of section 10.1 are the options `smallFileSize` and `tinyFileSize` of `buildModel`, 128 MiB and 8 MiB by default. Section 10.8 of the object model gives the name of each measure in the model, and the [README of the package](../src/typescript/views/README.md) says what the model adds for the views.

```ts
export function readFootprint(doc: PqaxDocument, options?: { files?: boolean; partitions?: boolean }): Promise<FootprintData>;
export function buildModel(data: FootprintData, options?: { smallFileSize?: number; tinyFileSize?: number }): StorageModel;
export function columnTree(model: StorageModel): TreeNode;
export function sortTables(tables: readonly TableModel[], order?: "waste" | "total" | "label"): TableModel[];
export function stackOf(input: StackInput): StackModel;   // and deletedEstimate, quartiles, smallShareAtLeast, commitsPerDay, daysBetween, ratio
```

### 4.5 Python package `pqax`

Standard library only. Python 3.10 or later.

```python
pqax.open(path_or_file) -> PqaxDocument
pqax.open_embedded(path_or_file, part_name="Footprint.pqax") -> PqaxDocument
doc.manifest -> dict
doc.summary -> dict[str, dict[str, list]]       # table name, then field name, then the values
doc.row_count(name) -> int                       # from the manifest
doc.embedded_part, doc.embedded_compression      # the part name and "stored" or "deflated" when opened inside a VPAX, else None
doc.table(name) -> dict[str, list]               # all parts, concatenated and cached; {} when the file has none
doc.rows(name) -> Iterator[dict]
pqax.validate(path_or_file) -> ValidationResult  # .is_valid, .checks, .issues: list of Issue(code, message, part, table, field)
pqax.validate_embedded(path_or_file, part_name="Footprint.pqax") -> ValidationResult
pqax.to_dataframe(table, library=None)           # pandas when installed, else polars; ImportError when neither is
python -m pqax check <file>                      # exit code 1 when invalid; one line per issue, with its code
python -m pqax info <file>
```

A writer, `pqax.Builder`, with the shape of the C# builder, is a should: the kit generator can use it later.

The derived measures of section 10 of the [object model](object-model.md) are in the module `pqax.measures`, as decided on 22 October 2026: pure functions over the tables that the reader returns, standard library only. A measure that cannot be computed is `None`. A result is a dict with the names of the JSON of `pqax tables --json`, which are those of the storage views, and a whole number in a float is returned as an `int`. The two thresholds are optional keyword arguments, 128 MiB and 8 MiB by default.

```python
pqax.measures.measure(doc, *, small_file_size=..., tiny_file_size=...) -> dict
                                     # {"filesRead", "warnings", "tables": [...], "columns": [...], "collection": {...}}
pqax.measures.measure_tables(summary, files=None, collection=None, partitions=None, *, small_file_size=...,
                             tiny_file_size=...) -> list[dict]
pqax.measures.measure_columns(summary) -> list[dict]
pqax.measures.measure_collection(summary, tables) -> dict   # tables: what measure_tables returned
pqax.measures.stack_of(file_size, deleted, footer_size, stale_column_size, non_live_file_size, metadata_size,
                       unreferenced_file_size=None, deleted_source=None) -> dict
pqax.measures.deleted_estimate(file_size, deleted_row_count, row_count)
pqax.measures.small_share_at_least(distribution, threshold)
pqax.measures.commits_per_day(commits, oldest_commit_at, newest_commit_at)   # commits: commitsRead
pqax.measures.days_between(start, end)
pqax.measures.ratio(a, b)
pqax.measures.DEFAULT_SMALL_FILE_SIZE, DEFAULT_TINY_FILE_SIZE, STACK_COMPONENTS, WASTE_COMPONENTS, OVERHEAD_COMPONENTS
python -m pqax tables <file> [--part NAME] [--sort waste|total|rows|files|name] [--top N] [--wide] [--json]
```

The module offers the measures that the C# library offers, as section 4.3 lists them: every measure of section 10 except the two over a set of columns. It has one function for each group: `measure_tables` for the tables, `measure_columns` for the columns, and `measure_collection` for the collection, and `measure` reads a document and calls the three. The dict of a table has `id`, `name`, `label`, `rowCount`, `fileCount`, `fileSize`; `stack` with the seven components, the sums, and `shares`, the share of each component; `smallFiles` and `tinyFiles`; `files` with the file size figures and `vOrder`, null or `{"share", "bytesShare"}`; `measures`; `maintenance`; and `partitions` with `count`, `kept`, `oldestModifiedAt`, and `newestModifiedAt`. The dict of a column has `id`, `table`, `name`, `label`, and the six measures of section 10.6, and that of the collection has `totals` and `depths`, as `collection` of the view model. A time is the text of the file. The JSON of `python -m pqax tables --json` keeps the keys it had in version 0.2.0, as in C#.

The export of section 10 of the [object model](object-model.md) and of the tables of a footprint, for a BI tool or a lakehouse, is in the module `pqax.export`. CSV needs the standard library only; Parquet needs pyarrow, the extra `pqax[parquet]`, and Delta needs deltalake and pyarrow, the extra `pqax[delta]`, and a missing library raises `ImportError` naming the extra.

```python
pqax.export.export(path_or_file, to, *, format="csv", part=None, if_exists="error", small_file_size=...,
                   tiny_file_size=...) -> dict   # {"footprintId", "collectedAt", "format", "folder", "skipped",
                                                 #  "tables": {name: rows}, "ignored": [...]}
pqax.export.COLUMNS, SCHEMAS, FORMATS, IF_EXISTS, MEASURE_TABLES, FOOTPRINTS
pqax.export.footprint_id(data) -> str            # the SHA-256 of the bytes of a PQAX file, lowercase hex
pqax.export.AlreadyExportedError                 # a PqaxError, code export.duplicate
python -m pqax export <file> --to FOLDER [--format csv|parquet|delta] [--part NAME] [--if-exists error|skip]
```

It writes one table for each table of the format that the file has, with every field of the format and the names of the file, and `tableMeasures`, `columnMeasures`, `collectionMeasures`, and `footprints`. Every row starts with `footprintId` and `collectedAt`. A measure has its name of section 10 with a lower-case first letter, the casing of rule 5 of section 3 for the keys of a table. The exports of several days append to the same tables, and a footprint that the folder already has is refused. The [Python README](../src/python/README.md#export-the-tables-day-after-day) lists the columns.

The comparison of two footprints, section 13 of the [object model](object-model.md), is in the module `pqax.diff`, with the rules of section 4.3 for `StorageDiff`. A result is a dict with the names of the JSON of `python -m pqax diff --json`, and a compared value is `{"older", "newer", "delta"}`. The command writes the same text and the same JSON as `pqax diff` in C#, apart from the line ends.

```python
pqax.diff.compare(older, newer, *, small_file_size=..., tiny_file_size=...) -> dict
        # {"older", "newer", "smallFileSize", "tinyFileSize", "counts", "collection", "added", "removed", "kept"}
pqax.diff.rank(result, order="total", top=None) -> list of (kind, entry)   # kind "added", "removed", or "kept"
pqax.diff.ORDERS                                                         # ("total", "waste", "rows", "files", "name")
python -m pqax diff <older> <newer> [--part NAME] [--sort total|waste|rows|files|name] [--top N] [--json]
```

The collector of section 4.6 is in the subpackage `pqax.collect`, as decided on 23 October 2026: the Delta log reader and the footer parser, which needs the standard library only, at levels 1 and 2. pyarrow reads the Delta checkpoints in Parquet, with the extra `pqax[collect]`, and azure-identity gives the identities of the Azure SDK, with the extra `pqax[azure]`; a missing library raises `ImportError` naming the extra, and the command exits with 2 and names it. The options, the defaults, the issue codes, the output lines, and the exit codes are those of `pqax collect` in C#. `--adls` reads a container of Azure Data Lake Storage Gen2, with the two forms of the URL, the refusals, and the messages of `pqax collect --adls`, reading the URL as `System.Uri` does, and `discover_folder` finds its Delta tables and its folders of Parquet files as `TableDiscovery.DiscoverFolderAsync` does, so that the two command lines name a storage account the same way and give the same footprint. `--mount` reads the folder of one item that holds `Tables`, such as the default lakehouse of a Fabric notebook, and `--endpoint`, with `--workspace`, another DFS endpoint in the OneLake layout. The messages that list the sources name `--mount` beside the sources of C#.

```python
pqax.collect.collect(storage, tables=None, *, items=None, level=2, depth="columnChunks", max_footers=0, max_commits=30,
                     parallelism=16, include_data_values=False, include_partition_values=False, options=None,
                     progress=None, checkpoint_reader="auto") -> pqax.Builder
pqax.collect.LocalStorage(root), LocalStorage.mount(folder, item=None)
pqax.collect.OneLakeStorage(workspace, token_provider, *, endpoint="https://onelake.dfs.fabric.microsoft.com", max_retries=4,
                            timeout=300.0, trace=None)
pqax.collect.list_items(storage), discover(storage, item) -> list[TableLocation]
pqax.collect.discover_folder(storage, folder, max_depth=DEFAULT_FOLDER_DEPTH) -> list[TableLocation]   # 4 by default
pqax.collect.TableLocation(..., format="Delta"), TableFormats.DELTA, TableFormats.PARQUET
pqax.collect.parse_footer(data, include_data_values=False) -> ParquetFooter      # FooterError(code, message) on bad bytes
pqax.collect.CollectorOptions, DetailDepth, CollectionProgress, IssueCodes, ISSUE_CODES, ArrowCheckpointReader
pqax.collect.identity.TokenCache(get_token), access_token(value), azure_cli(tenant=None), azure_credential(credential)
python -m pqax collect --local <folder> | --mount <folder> | --workspace <name or id> --item <name.Kind> ...
                       | --adls https://<account>.dfs.core.windows.net/<container>[/<folder>] --output <file>
                       [--endpoint <url>] [identity] [--depth ...] [--level 1|2] [--max-footers n] [--max-commits n]
                       [--parallelism n] [--verbose]
```

### 4.6 Collector

Namespace `Pqax.Collector`. The storage access, the Delta log, the tail read of a footer, and the allow-list of footer keys were first tried in a probe that preceded the collector, on a real Fabric lakehouse and warehouse; the collector supersedes it.

```csharp
public interface IStorage                          // read only
{
    Task<IReadOnlyList<StorageEntry>> ListAsync(string path, CancellationToken cancellation);
    Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellation);
}
public sealed class LocalStorage : IStorage;       // a folder with the OneLake layout, as conformance/fixtures/lakehouse
public sealed class OneLakeStorage : IStorage;     // the DFS endpoint, GET only, a token provider delegate, scope https://storage.azure.com/.default
public interface ICheckpointReader { Task<IReadOnlyList<DeltaAction>> ReadAsync(IStorage storage, string path, CancellationToken cancellation); }
public sealed class CollectorOptions { int Level = 2; DetailDepth Depth; int MaxFootersPerTable; int MaxCommitsPerTable; int Parallelism;
                                       bool IncludeDataValues = false; bool IncludePartitionValues = false; int FooterTailBytes = 32768; }
public sealed class PqaxCollector
{
    public PqaxCollector(IStorage storage, CollectorOptions options, ICheckpointReader? checkpoints = null);
    public Task<FootprintBuilder> CollectAsync(IReadOnlyList<TableLocation> tables, IProgress<CollectionProgress>? progress, CancellationToken cancellation);
}
```

The collector parses Parquet footers itself, from the Thrift compact protocol and `parquet.thrift`, and reads a footer with one request in the common case: the tail of the file first, then more when the footer is longer. Without an `ICheckpointReader`, a table whose log needs a checkpoint gets an issue, and its summary holds what the JSON commits say. `Pqax.Collector.ParquetNet` provides the checkpoint reader with Parquet.Net, with the definition-level handling that the probe needed. Data values are never kept unless the options ask for them, and footer key-value metadata is kept only for keys on the allow-list. A failure on one table or file becomes an issue, and the collection goes on.

At every collection level, the collector lists the folder of each table, at any depth, to count the unreferenced files of format 0.2: one request for each folder, or the pages of one recursive listing where the storage has one. A listing that fails becomes an issue, `AccessDenied` for a 403 and `DataFilesNotListed` otherwise, and the two unreferenced fields of the table are then absent.

The collector reads OneLake and Azure Data Lake Storage Gen2. A container of a storage account with hierarchical namespace serves the DFS API of OneLake, with the container where OneLake has the workspace, so `OneLakeStorage` reads it with the endpoint of the account, `https://<account>.dfs.core.windows.net`, and a token of the same scope. `TableDiscovery.DiscoverFolderAsync` finds the tables under a folder of it, to a bounded depth: a folder with `_delta_log` is a Delta table, and a folder of Parquet files with no subfolder is a table of the format `Parquet`, whose columns come from the footer of its most recent file, as section 6.5 of the [object model](object-model.md) says. Their source has the kind `Storage folder`, one of the kinds that section 6.3 lists.

Two collectors meet this contract: `Pqax.Collector` in C#, the reference, and `pqax.collect` in Python, a port of it with the same options, reads, totals, issue codes, and limits, as section 4.5 lists. Their footprints of `conformance/fixtures/lakehouse` hold the same values in every table and field, the requests and the bytes of the collection included, and differ only in the members of the collection that name the collector and the times, which `conformance/cross_check.py` checks. So do the footprints that `pqax collect --adls` and `python -m pqax collect --adls` take of the synthetic container of `conformance/container_check.py`, served by a fake DFS endpoint, with its Delta tables, its folders of Parquet files, its hidden and partitioned folders, and a footer with every type annotation; the two command lines print the same lines and fail and refuse with the same messages. The message of an issue that quotes the error of a JSON parser is worded by that parser, and the name of an exception in a message by its language; the codes do not differ.

## 5. Tests

The tests are written from the kit and from section 4, not from the code.

| Suite | Tool | What it checks |
|---|---|---|
| `tests/dotnet/Pqax.Tests` | xunit | Every sample of the kit opens and gives the expected results. Every invalid sample is rejected with at least the expected codes. A footprint built with `FootprintBuilder` is written, read back, and equal, and its file passes the validator. Unknown fields survive `From` and `Write`. A table of 600,000 rows is split in parts of 250,000 and read back whole. An embedded footprint opens from a stored and from a deflated part. |
| `tests/dotnet/Pqax.Collector.Tests` | xunit | The fixture lakehouse and warehouse are collected from `LocalStorage` at levels 1 and 2, and the result equals the probe dump of the same fixture in files, rows, row groups, column chunks, and sizes. The footer parser agrees with the dump on every footer. A missing checkpoint reader produces an issue, not an exception. Locations, every footer failure with its code and its effect on the totals, and the commit limit on synthetic logs. The unreferenced files and the removal times of the non-live files on the fixture, and distinct oldest and newest removal times on synthetic logs. |
| `tests/dotnet/Pqax.Cli.Tests` | xunit | `check` returns 0 on a valid sample and 1 on an invalid one, with the codes in the output. `info` prints the parts and the rows. |
| `tests/typescript` | vitest | The same conformance checks as the C# reader and validator, from `Uint8Array`, `Blob`, and a `ByteSource` that counts its reads: opening a sample must not read a detail part. The suite runs in Node and, through Playwright, in Chromium, Firefox, and WebKit. |
| `tests/python` | pytest | The same conformance checks as the C# reader and validator. The command line returns the right exit codes. |
| Cross-implementation | a script in `conformance/` | A file written by the C# writer is read by the TypeScript and Python readers, and every validator accepts it |

The expected results are the contract. When an implementation and the kit disagree, the kit is checked first against the serialization document, and the one that is wrong is fixed.

## 6. How version 0.1 was built

Version 0.1 was written in October 2026 in this order: the layout and the contracts of this document first, then the conformance kit and the three packages at the same time, then the tests against the kit, then the collector and its tests, and then the first collections from a real workspace. Each piece was integrated after its suites passed. The decision log of section 8 records what was decided on the way.

## 7. What comes after 0.1

The item-by-item list is the [backlog](backlog.md). The lines below are the direction.

- **Real tables.** Run the collector against more real workspaces and against tables written by other engines, compare the results with what a probe read, and fix the differences.
- **Format 1.0.** Freeze the format when the collector has run on tables written by at least three engines, and when the JSON Schema and the kit are complete. Until then, every change to the format bumps the kit and the three packages together.
- **Publishing.** The seven packages are published together from a GitHub release, as section 7.1 says, after the public API is reviewed once more against the needs of DAX Studio, Bravo, and Tabular Editor. A package states which format versions it reads and writes.
- **Derived measures and the view model.** Define them in the object model, then implement them in TypeScript for the application and once more in C# only if a tool asks.
- **VPAX integration.** The VPAX library learns to embed `Footprint.pqax` and the obfuscator learns to obfuscate it, with `FootprintBuilder.From` as the base.
- **Python writer.** Offer or contribute a PQAX export to semantic-link-labs once the format is 1.0.
- **Iceberg.** The model has the fields. The collector gets a metadata reader for Iceberg tables when a user asks.

Risks to watch: the format changes before 1.0 and the three packages drift, which the kit guards against; the footer parser meets a Thrift construct it does not expect, which fuzzing with files from several writers guards against; checkpoints come in forms the probe never saw, such as multi-part checkpoints and v2 checkpoints with sidecars, which the collector must report as issues rather than fail on; memory in .NET for a million column chunks as row objects, which a columnar storage behind the same API fixes later.

### 7.1 Versioning and releases

This is the policy of version 0.1. It is not a decision of the maintainers, and the decision log of section 8 does not list it.

1. The seven packages share one version number: `Pqax`, `Pqax.Cli`, `Pqax.Collector`, and `Pqax.Collector.ParquetNet` on NuGet, `@pqax/pqax` and `@pqax/views` on npm, and `pqax` on PyPI. `@pqax/views` joins from release 0.2.0, and depends on `@pqax/pqax` of its own release. Packages with the same number read and write the same format.
2. The major and minor numbers are those of the format version that the packages write, so format 0.1 gives packages 0.1.x. The patch number counts the releases that do not change the format.
3. A package states in its README and in its metadata which format versions it reads, every version with the same major number up to its own, and which it writes, its own. A reader still opens a file of a later version with the same major number and reads what it knows, as section 9 of the [serialization](serialization.md) requires.
4. A release is a GitHub release tagged `v<version>`, such as `v0.1.0`, and the packages are published from it. The publish workflow checks that the tag equals the version in `src/dotnet/Directory.Build.props`, `src/typescript/pqax/package.json`, `src/typescript/views/package.json`, `src/python/pyproject.toml`, and `src/python/pqax/__init__.py`, and that the views depend on `^<version>` of the reader, so that the three registries get the same version.
5. The format reaches 1.0 only as the [backlog](backlog.md) says, and the packages then become 1.0.0 together.

## 8. Decision log

| Date | Decision | By |
|---|---|---|
| 2026-10-06 | Names in lower camel case, `Footprint.pqax`, no views in the first version, no dictionary, at most 250,000 rows in a detail part | Marco Russo |
| 2026-10-07 | NuGet packages of the .NET reader and writer; libraries first, command line as a thin layer | Marco Russo |
| 2026-10-07 | The collector has its own footer parser and reads checkpoints through Parquet.Net behind an interface | Marco Russo |
| 2026-10-07 | Package names `Pqax`, `Pqax.Collector`, `Pqax.Collector.ParquetNet`, `Pqax.Cli`, npm and PyPI `pqax` | The maintainers |
| 2026-10-07 | MIT license | Marco Russo |
| 2026-10-07 | The public API of `Pqax` 0.1 stays as implemented; the eight points of its review are recorded in [decisions.md](decisions.md), and the later changes are in the [backlog](backlog.md) | Marco Russo |
| 2026-10-07 | The device code is the default sign-in of `pqax collect` | Marco Russo |
| 2026-10-21 | The npm package is `@pqax/pqax`: npm refuses the unscoped name `pqax` as too similar to the packages `rax` and `sax`. The three ecosystems move to 0.1.1 together, with no other change. | Marco Russo |
