# Changelog

The packages of a release share its version number, as section 7.1 of the [contracts](docs/contracts.md) says.

## Unreleased

### PyPI

`pqax export --format delta` writes into a mounted lakehouse: `/lakehouse/default/Tables` in a Fabric notebook, or `/dbfs` on Databricks. deltalake commits the Delta log by renaming a file, and the mount has no rename, so the export turns on its option `allow_unsafe_rename` there, which commits by a copy and a delete. The first run of the Fabric notebook found it on 24 October 2026.

### Templates

`fabric/pqax-footprint.ipynb` ran in a Fabric workspace on 24 October 2026, from the editor: the install, the collection of the default lakehouse over the mount, the check, and the copy into `Files` worked, and the export failed on the rename above until this fix. Two runs started by the job API, which Fabric treats as scheduled runs, failed in their first statement where the editor passed it: for a schedule or a pipeline, put `pqax[collect,delta]` in an Environment of the workspace and attach it to the notebook instead of the install cell, as its README now says.

## 0.2.1 - 2026-10-24

The tools of the plan, with no change to the format. `pqax collect` signs in without a person and reads Azure Data Lake Storage Gen2 with `--adls`, where a folder of Parquet files is a table; templates collect a footprint on a schedule from GitHub Actions or from a Fabric notebook; `pqax tables` shows the derived measures of the storage views, which section 10 of the object model defines and C#, Python, and TypeScript compute the same way; `pqax export` writes the tables of a footprint as CSV, Parquet, or Delta, with a Power BI template over them; `pqax diff` compares two footprints of the same lakehouse; and the Python package has the collector, `pqax.collect`, with the same footprint as the C# one. The format does not change.

### Format

| Piece | Version | Content |
|---|---|---|
| [Object model](docs/object-model.md) | Draft 0.6 | Section 10 defines the derived measures: the storage stack with its waste and overhead, the small files, the V-Order share, the history, and the ratios of tables, columns, and the collection. Section 4.3 states that the quartiles of a distribution are taken by position. The maintainer decided the measures on 22 October 2026. The format does not change. |
| [Object model](docs/object-model.md) | Draft 0.7 | Section 13 proposes how two footprints of the same lakehouse are compared, and answers open question 7: a table is the same table when its TableId is, and by its source, namespace, and name when one of the two has none; a column by its FieldId, then its name; a partition by its values, a file by its path, and a commit by its CommitId. The rules wait for the maintainer, in [decisions.md](docs/decisions.md). The format does not change. |

### .NET

| Package | What changes | Format |
|---|---|---|
| `Pqax` | `StorageMeasures` computes the derived measures of the tables as section 10 of the object model defines them, and as the storage views do: the stack with its waste and overhead, the deleted rows, the small files, the ratios, and the pace of the commits. The waste, the overhead, the data, and the total of a stack are `double?`: the data and the shares are null when the live data is, and the other three when every component they add is absent. The commits per day divide the commits read only. It offers every measure of section 10, with the names and the nulls of the storage views: besides the measures that `pqax tables` shows, the share of each component of the stack, the file size figures, the V-Order shares, VACUUM and OPTIMIZE, and the partitions of a table, the measures of each column, `StorageMeasures.ForColumns`, and those of the collection, `StorageMeasures.ForCollection`. `Compute` reads the `partitions` table too. Only the two measures over a set of columns stay in the views. `StorageDiff` compares two footprints of the same lakehouse, as section 13 of the object model proposes: the tables and the columns added, removed, and kept, with the older value, the newer value, and the delta of every total and measure, and the files, partitions, and commits added and removed. | Reads 0.1 and 0.2, writes 0.2 |
| `Pqax.Cli` | `pqax collect` signs in with `--client-id` and `--client-secret-env`, `--client-certificate`, `--managed-identity`, `--azure-cli`, `--access-token-env`, or `--env-credential`, besides the device code, still the default, and the browser. A secret is read from an environment variable, never from the command line, and is not printed or written. A refused sign-in or request is one line with its code and the likely cause. `pqax tables` prints one row per table with its rows, files, live bytes, waste, and waste share, or one JSON object with `--json`; a measure that is null is `n/a` in the table and `null` in the JSON. `pqax diff OLDER NEWER` compares two footprints, the largest change first, with `--sort`, `--top`, and `--json`, and writes the same text and JSON as `python -m pqax diff`. | Reads 0.1 and 0.2, writes 0.2 |
| `Pqax.Collector` | The README shows how to pass any `TokenCredential`, and the permissions of a service principal. Azure Data Lake Storage Gen2 and folders of Parquet files: see below. | Writes 0.2 |
| `Pqax.Collector.ParquetNet` | No change | Reads and writes no PQAX file |

Every package carries the PQAX icon, the mark of [brand/](brand/README.md).

`pqax collect --adls https://<account>.dfs.core.windows.net/<container>[/<folder>]` collects the tables of a container of Azure Data Lake Storage Gen2, with the identities of `--workspace` and the role Storage Blob Data Reader. `Pqax.Collector` reads the container with `OneLakeStorage` and the endpoint of the account, since Azure Storage serves the same DFS API, and `TableDiscovery.DiscoverFolderAsync` finds the tables under a folder, 4 levels deep by default: a folder with `_delta_log` is a Delta table, and a folder of Parquet files with no subfolder is a table of the format `Parquet`, whose columns come from the footer of its most recent file. Their source has the kind `Storage folder`, an `abfss://` location, and no identifiers. `TableLocation` has a `Format`, and `TableFormats` names the two. The OneLake behavior does not change. It ran against a storage account on 24 October 2026, on the two Delta tables of the kit and a folder with one Parquet file, which came out as a Parquet table; a URL carrying a SAS token was refused as designed.

### npm

`@pqax/views` computes the derived measures as section 10 of the object model defines them, and its README refers to that section instead of defining them. A measure that the fields do not give is null: the data of a stack and every share of it when the live data is, the waste, the overhead, and the total when every component they add is absent, the share of an absent component, and the footprint, waste, and overhead of the collection when no table has them. The commits per day divide the commits read only, the share of the files without a column divides by the footers read, VACUUM and OPTIMIZE are null for a table without operations, and the days since the last commit count to the first time of the collection that parses. The treemap names its ratio of a table, a source, or the whole the column compression ratio. `@pqax/pqax` does not change. Reads 0.1 and 0.2.

### PyPI

`pqax` has the module `measures`, the same measures as the storage views, computed as section 10 of the object model defines them, and the command `python -m pqax tables`, which prints `n/a` for a measure that is null. The module offers every measure of section 10 that C# offers: `measure_tables` for the tables, with the shares of the stack, `files`, VACUUM and OPTIMIZE, and `partitions`, `measure_columns` for the columns, and `measure_collection` for the collection, and `measure` returns the three. The JSON of `pqax tables --json`, in C# and in Python, keeps its keys. Reads 0.1 and 0.2, writes 0.2.

`python -m pqax export` and the module `pqax.export` write the tables of a footprint and its derived measures as CSV with the standard library, as Parquet with the new extra `pqax[parquet]` (pyarrow), or as Delta with the new extra `pqax[delta]` (deltalake). Every row carries `footprintId`, the SHA-256 of the PQAX file, and `collectedAt`, so that the exports of several days append to one table per name; a footprint already in the folder is refused, or skipped with `--if-exists skip`. The tables are those of the format with their fields, and `tableMeasures`, `columnMeasures`, `collectionMeasures`, and `footprints`. The export reads the footprint embedded in a VPAX too.

`pqax` has the module `diff`, which compares two footprints of the same lakehouse as `StorageDiff` does in C#, and the command `python -m pqax diff OLDER NEWER`, with the text and the JSON of `pqax diff`.

`pqax` has the collector, the subpackage `pqax.collect` and the command `python -m pqax collect`, a port of `Pqax.Collector` with the options, the issue codes, the output lines, and the exit codes of `pqax collect` in C#: the Delta log at level 1, with the JSON commits, the checkpoints, the state, the removed and the unreferenced files, and the history with the same commit limit, and the Parquet footers at level 2, parsed with the standard library. It reads a folder in the OneLake layout with `--local`, the mounted lakehouse of a Fabric notebook with `--mount`, and OneLake over the DFS API with a token provider, with `--endpoint` for another endpoint such as an ADLS Gen2 account. It signs in with `--access-token-env`, `--azure-cli`, or, with the new extra `pqax[azure]` (azure-identity), the identities of the Azure SDK, the device code by default. The new extra `pqax[collect]` (pyarrow) reads the Delta checkpoints in Parquet; without it the command exits with 2 naming the extra. Its footprint of the synthetic lakehouse of the kit has the same values as that of the C# collector in every table and field. It collected a Fabric workspace over OneLake from a desktop on 24 October 2026, with the same footprint as the C# collector.

### Templates

A new folder, `templates/`, with files to copy elsewhere. `github-actions/pqax-footprint.yml` collects a workspace every day as a service principal, signed in with OpenID Connect or a client secret, checks the footprint, and keeps it as an artifact and optionally in the repository. `fabric/pqax-footprint.ipynb` collects from a Fabric notebook with the token of the notebook. Neither has run against a real workspace yet: their READMEs say what is verified.

`powerbi/` is a Power BI project, a TMDL model and a PBIR report, over a folder of Parquet files written by `pqax export`: a Footprints table, Tables and Columns with their measures per collection, a Date table, and DAX measures for the last collection of a period and the change from the collection before. Its three pages show the storage stack per table over time, the non-live and unreferenced files, and the small files, with the definitions of section 10 in the descriptions. `make_sample.py` builds a synthetic export of fourteen days from a sample of the kit. The README says how to point the model at the Delta tables of a lakehouse in Direct Lake. It opened and refreshed in Power BI Desktop on that sample; Direct Lake has not run yet.

`fabric/pqax-footprint.ipynb` now installs `pqax[collect,delta]` with `%pip` instead of the .NET runtime and the tool, collects the default lakehouse where Fabric mounts it, or items of a workspace over OneLake with the token of the notebook, keeps the footprint in the `Files` area, and exports its tables with `python -m pqax export --format delta` into the `Tables` of the lakehouse, where the Power BI template reads them in Direct Lake. `github-actions/pqax-footprint.yml` has an export step: the variable `PQAX_EXPORT` names the format, `csv`, `parquet`, or `delta`; the export of the day is kept as an artifact, and with `PQAX_COMMIT` the commit job appends it to `exports/` in the repository with the footprint. Neither has run in Fabric or on GitHub yet; their READMEs say what ran locally.

### Conformance kit

`expected/measures/` holds the derived measures of every valid sample, as the storage views compute them, and `make_measures.py` rebuilds them. `cross_check.py` computes the measures with the three implementations and compares them with these files. The files hold every measure of section 10 that the three implementations share, for the tables, the columns, and the collection, and the comparison has no tolerance.

Two new valid samples, `diff-before.pqax` and `diff-after.pqax`, are two footprints of one synthetic lakehouse a week apart, written with `pqax.Builder` by `make_diff.py`, with tables and columns added, removed, renamed, and recreated, files added and removed, an OPTIMIZE and a VACUUM. `expected/diff/diff-before.diff-after.json` is their expected diff, built from the changes the script declares and from the measures of the views, and `cross_check.py` compares the diff of the C# and Python command lines with it, with no tolerance, and their outputs with each other.

`cross_check.py` also collects the fixture with `python -m pqax collect` when pyarrow is installed, checks the file with every validator, reader, and the measures as it checks the one of `pqax collect` in C#, and compares the two collections in every table and field, except the members of the collection that name the collector and the times.

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
