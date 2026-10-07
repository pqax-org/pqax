# PQAX contracts

How the implementations of PQAX are organized, what each one must offer, and what comes next. Started on 7 October 2026. When this document disagrees with the [serialization](serialization.md) or the [object model](object-model.md), those two win.

## 1. Version 0.1

What the first version delivers:

| Piece | Content |
|---|---|
| C# `Pqax` | Reads, writes, and validates PQAX files. Targets netstandard2.0 and net8.0. No dependency beyond zip and JSON. Published later as a NuGet package. |
| C# `Pqax.Cli` | `pqax check <file>` and `pqax info <file>`, over the library |
| C# `Pqax.Collector` | Collects Delta tables from a local folder or from OneLake, at collection levels 1 and 2, and writes a PQAX file. Its own Parquet footer parser. No Parquet library. |
| C# `Pqax.Collector.ParquetNet` | Reads Delta checkpoints, which are Parquet files with nested data, with Parquet.Net. It plugs into the collector through an interface. |
| TypeScript `pqax` | Reads and validates PQAX files in a browser and in Node, with no dependency |
| Python `pqax` | Reads and validates PQAX files with the standard library, loads tables into data frames when pandas or polars is installed, and has a command line |
| Conformance kit | A JSON Schema of every part, sample files with expected results, the list of validation rules with their codes, and the fixture that the collector tests use |
| Tests | For every piece, written from the conformance kit and the contracts of section 4, not from the code |

Not in 0.1: the view model and the derived measures, Iceberg tables, obfuscation, a Python writer, publishing to any package registry, collection level 3 and 4.

## 2. Layout

```
README.md
LICENSE                           MIT
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
src/python/pqax                   the Python package
tests/dotnet/Pqax.Tests           xunit
tests/dotnet/Pqax.Collector.Tests
tests/dotnet/Pqax.Cli.Tests
tests/typescript                  vitest
tests/python                      pytest
research/prototype                the prototype that tested the serialization
research/detail-encoding          the experiment that compared encodings of a large table
.github/workflows/ci.yml          builds and runs every suite
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
8. Nothing is published from a working copy. Releases follow the versioning of section 7.

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

Namespace `Pqax`. One class per table of the file, named after its object with the suffix `Row`: `SourceRow`, `TableRow`, `ColumnRow`, `PartitionSpecRow`, `PartitionFieldRow`, `SortOrderRow`, `SortFieldRow`, `PhysicalSchemaRow`, `PhysicalColumnRow`, `WriterRow`, `OperationRow`, `IssueRow`, `ModelBindingRow`, `TableBindingRow`, `ColumnBindingRow`, `PartitionRow`, `FileRow`, `RowGroupRow`, `ColumnChunkRow`, `DeleteFileRow`, `CommitRow`. A row has one property per field of the object model, named as in the file with a capital first letter: `long?` for integers, `string?`, `bool?`, `DateTimeOffset?` for timestamps, `IReadOnlyList<string>?` for lists, `IReadOnlyDictionary<string, string>?` for maps, `Distribution?`, `DeltaDetails?`, `IcebergDetails?`, and `long?` for a reference, with the name of the reference field: `ColumnRow.Table`, `ColumnChunkRow.RowGroup`. An `id` is `long`.

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

## 5. Tests

The tests are written from the kit and from section 4, not from the code.

| Suite | Tool | What it checks |
|---|---|---|
| `tests/dotnet/Pqax.Tests` | xunit | Every sample of the kit opens and gives the expected results. Every invalid sample is rejected with at least the expected codes. A footprint built with `FootprintBuilder` is written, read back, and equal, and its file passes the validator. Unknown fields survive `From` and `Write`. A table of 600,000 rows is split in parts of 250,000 and read back whole. An embedded footprint opens from a stored and from a deflated part. |
| `tests/dotnet/Pqax.Collector.Tests` | xunit | The fixture lakehouse and warehouse are collected from `LocalStorage` at levels 1 and 2, and the result equals the probe dump of the same fixture in files, rows, row groups, column chunks, and sizes. The footer parser agrees with the dump on every footer. A missing checkpoint reader produces an issue, not an exception. |
| `tests/dotnet/Pqax.Cli.Tests` | xunit | `check` returns 0 on a valid sample and 1 on an invalid one, with the codes in the output. `info` prints the parts and the rows. |
| `tests/typescript` | vitest | The same conformance checks as the C# reader and validator, from `Uint8Array`, `Blob`, and a `ByteSource` that counts its reads: opening a sample must not read a detail part. |
| `tests/python` | pytest | The same conformance checks as the C# reader and validator. The command line returns the right exit codes. |
| Cross-implementation | a script in `conformance/` | A file written by the C# writer is read by the TypeScript and Python readers, and every validator accepts it |

The expected results are the contract. When an implementation and the kit disagree, the kit is checked first against the serialization document, and the one that is wrong is fixed.

## 6. How version 0.1 was built

Version 0.1 was written in October 2026 in this order: the layout and the contracts of this document first, then the conformance kit and the three packages at the same time, then the tests against the kit, then the collector and its tests, and then the first collections from a real workspace. Each piece was integrated after its suites passed. The decision log of section 8 records what was decided on the way.

## 7. What comes after 0.1

The item-by-item list is the [backlog](backlog.md). The lines below are the direction.

- **Real tables.** Run the collector against more real workspaces and against tables written by other engines, compare the results with what a probe read, and fix the differences.
- **Format 1.0.** Freeze the format when the collector has run on tables written by at least three engines, and when the JSON Schema and the kit are complete. Until then, every change to the format bumps the kit and the three packages together.
- **Publishing.** NuGet first, with the public API reviewed once more against the needs of DAX Studio, Bravo, and Tabular Editor. npm and PyPI follow. A package states which format versions it reads and writes.
- **Derived measures and the view model.** Define them in the object model, then implement them in TypeScript for the application and once more in C# only if a tool asks.
- **VPAX integration.** The VPAX library learns to embed `Footprint.pqax` and the obfuscator learns to obfuscate it, with `FootprintBuilder.From` as the base.
- **Python writer.** Offer or contribute a PQAX export to semantic-link-labs once the format is 1.0.
- **Iceberg.** The model has the fields. The collector gets a metadata reader for Iceberg tables when a user asks.

Risks to watch: the format changes before 1.0 and the three packages drift, which the kit guards against; the footer parser meets a Thrift construct it does not expect, which fuzzing with files from several writers guards against; checkpoints come in forms the probe never saw, such as multi-part checkpoints and v2 checkpoints with sidecars, which the collector must report as issues rather than fail on; memory in .NET for a million column chunks as row objects, which a columnar storage behind the same API fixes later.

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
