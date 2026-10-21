# Changelog

The packages of a release share its version number, as section 7.1 of the [contracts](docs/contracts.md) says.

## 0.2.0 - 2026-10-21

Format version 0.2 adds five optional fields. On `tables`, `unreferencedFileCount` and `unreferencedFileSize` count the data files in storage that the version described does not reference, and `nonLiveOldestRemovedAt` and `nonLiveNewestRemovedAt` say when its non-live files were removed. On `partitions`, `lastModifiedAt` says when the files of a partition last changed. Compared with `nonLiveFileCount` and `nonLiveFileSize`, they let a reader tell the non-live files still in storage from those that a VACUUM already deleted, and see how old the tombstones are against the retention of the table.

### Format

| Piece | Version | Content |
|---|---|---|
| [Object model](docs/object-model.md) | Draft 0.5 | The five fields, how the unreferenced files are found, and the one exception to the rule on absent totals |
| [Serialization](docs/serialization.md) | Draft 0.3 | The five fields in `tables` and `partitions`. The rules for versions do not change. |
| [Conformance kit](conformance/README.md) | Format 0.2 | The schema has the five fields. The samples are format 0.2, plus `format-0.1.pqax`, a sample of format 0.1. The fixture has a data file that no commit references, and the probe dump has the new fields, added by hand. |

A reader of format 0.1 ignores the new fields. A reader of format 0.2 opens a file of format 0.1.

### .NET

| Package | What changes | Format |
|---|---|---|
| `Pqax` | `TableRow` and `PartitionRow` have the five fields. `FootprintBuilder`, also through `From`, writes format 0.2. | Reads 0.1 and 0.2, writes 0.2 |
| `Pqax.Cli` | No change of its own | Reads 0.1 and 0.2, writes 0.2 |
| `Pqax.Collector` | Lists the folder of each table at every collection level to find the unreferenced files, and computes the removal times from the `remove` actions and `lastModifiedAt` of each partition. A listing that fails becomes the issue `AccessDenied` or `DataFilesNotListed`. | Writes 0.2 |
| `Pqax.Collector.ParquetNet` | No change | Reads and writes no PQAX file |

### npm

`@pqax/pqax` has the five fields in its types. Reads 0.1 and 0.2.

`@pqax/views` is a new package, published for the first time with this release: the storage views of a footprint in a browser, with no dependency but `@pqax/pqax` of the same release. A view model computes the derived values that its README defines, and three views draw it in plain DOM and SVG: the storage stack of each table sorted by waste, a treemap of the columns by compressed size colored by the share of nulls, and the distribution of the file sizes of each table with the V-Order share. It reads files of format 0.1 and 0.2: from 0.2 the stack has the unreferenced files, and the tooltips the removal times of the non-live files and the last change of the partitions. The page at [pqax.org/tools](https://pqax.org/tools/) uses it. Reads 0.1 and 0.2, writes nothing.

The two npm packages are workspaces of a `package.json` at the root of the repository. The first publish of `@pqax/views` uses the secret `NPM_TOKEN_VIEWS`, since npm attaches a trusted publisher only to a package that exists; the secret and its step leave the publish workflow after this release, as [RELEASING.md](RELEASING.md) says.

### PyPI

`pqax` has the five fields, and `Builder`, also through `from_document`, writes format 0.2. Reads 0.1 and 0.2, writes 0.2.

### Release text

Format version 0.2 adds five optional fields: the files in storage that a table no longer references, the removal times of its non-live files, and when each partition last changed. The packages read files of format 0.1 and 0.2 and write format 0.2. A new npm package, `@pqax/views`, draws the storage views of a footprint in a browser. `CHANGELOG.md` lists what changes in each package.

```
dotnet add package Pqax
dotnet tool install -g Pqax.Cli
npm install @pqax/pqax
pip install pqax
```

## 0.1.2 - 2026-10-21

No change in the packages. npm validates a new trusted publisher with the first publish that goes through it, within 48 hours of its creation, so this release publishes `@pqax/pqax` through the trusted publisher of the repository; NuGet and PyPI get the same number, so that the six packages keep one version. The publish workflow loses its token path.

### Release text

No change in the packages: this release publishes `@pqax/pqax` through the repository's trusted publisher on npm, which validates it, and gives NuGet and PyPI the same number.

## 0.1.1 - 2026-10-21

The npm package is `@pqax/pqax`. npm refused the unscoped name `pqax` as too similar to the existing packages `rax` and `sax`, so release 0.1.0 reached NuGet and PyPI and not npm. Nothing else changes: the four NuGet packages and the PyPI package get the same number, so that the six keep one version as section 7.1 of the contracts says.

### Release text

The npm package of PQAX is `@pqax/pqax`, published from this release; the registry refuses the unscoped name. NuGet and PyPI get the same version with no other change. The changelog has what 0.1.0 holds.

```
npm install @pqax/pqax
```

## 0.1.0 - 2026-10-21

The first release: format version 0.1, its conformance kit, and the reference implementations in C#, TypeScript, and Python.

### Format

| Piece | Version | Content |
|---|---|---|
| [Object model](docs/object-model.md) | Draft 0.4 | The objects of a footprint, their fields, the totals, and the depth |
| [Serialization](docs/serialization.md) | Draft 0.2 | The zip, the JSON tables, the manifest, and the rules for readers and writers |
| [Conformance kit](conformance/README.md) | Format 0.1 | The validation rules with their codes, a JSON Schema of the manifest and of every table, 10 valid samples with their expected results, 8 PQAX files and 2 VPAX files, 45 invalid samples with the codes that a validator must report, and a synthetic lakehouse and warehouse with the probe dump that the collector tests use |

A reader of format 0.1 also opens a file of a later version 0.x and reads what it knows, as section 9 of the serialization requires.

### .NET

On NuGet.

| Package | What it does | Format | Targets | Known limits |
|---|---|---|---|---|
| `Pqax` | Reads and validates PQAX files, also inside a VPAX file, and writes them | Reads 0.1, writes 0.1 | netstandard2.0, for .NET Framework hosts, and net8.0 | A part is read into memory whole, up to 1 GiB. A row is an object: a column chunk takes about 270 bytes. The reader does not check the CRC of an entry. The writer does not check that an integer is within 2^53 - 1. |
| `Pqax.Cli` | The .NET tool `pqax`: `check` validates a file, `info` shows what it holds, `collect` collects a footprint | Reads 0.1, writes 0.1 | .NET 8 or later | Those of `Pqax` and `Pqax.Collector` |
| `Pqax.Collector` | Collects the footprint of Delta tables from OneLake or from a folder with its layout, at collection levels 1 and 2, with its own Parquet footer parser | Writes 0.1 | netstandard2.0 and net8.0 | No Iceberg tables, no folders of Parquet files without a table format, and no collection levels 3 and 4. A checkpoint larger than 512 MB is not read. Log compaction files are counted, not used. The files of a table stay in memory until the footprint is built. |
| `Pqax.Collector.ParquetNet` | Reads Delta checkpoints for the collector, with Parquet.Net 5.6.1 | Reads and writes no PQAX file | net8.0 | net8.0 only, because Parquet.Net 5.6.1 ships net8.0 and net10.0 assemblies only, so a .NET Framework host collects without a checkpoint reader. A checkpoint column that does not line up with its rows fails the read, and the collector records `DeltaCheckpointNotRead`. |

### npm

| Package | What it does | Format | Runs on | Known limits |
|---|---|---|---|---|
| `@pqax/pqax` | Reads and validates PQAX files, also inside a VPAX file, in a browser and in Node, with no dependency | Reads 0.1 | Node 20.12 or later. Chrome and Edge 103, Firefox 113, Safari 16.4, or later. | Does not write. Tested in Node 24 and, through Playwright, in Chromium 156, Firefox 157, and WebKit 27.2. A part is held in memory whole while it is parsed, and `validate` holds every part. |

### PyPI

| Package | What it does | Format | Runs on | Known limits |
|---|---|---|---|---|
| `pqax` | Reads and validates PQAX files, also inside a VPAX file, and writes them, with a command line and data frames through pandas, or polars 1.0 or later | Reads 0.1, writes 0.1 | Python 3.10 or later, with the standard library only | No collector: it reads neither OneLake nor a folder of Delta tables. A table stays in memory once read. A part of more than 1 GB of JSON text is not read. Timestamps, lists, maps, and nested objects get no pandas or polars type. |

### Release text

This is the first release of PQAX, a file format for the storage footprint of Parquet and Delta tables: files, row groups, column chunks, and their sizes, without the data. It has format version 0.1, a draft that can change until 1.0, its conformance kit, and its implementations in C#, TypeScript, and Python.

```
dotnet add package Pqax
dotnet tool install -g Pqax.Cli
npm install pqax
pip install pqax
```

`Pqax.Collector` collects the footprint of Delta tables from OneLake or from a folder with its layout, with `Pqax.Collector.ParquetNet` for the checkpoints. `CHANGELOG.md` lists what each package does and its known limits.
