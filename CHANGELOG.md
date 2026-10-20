# Changelog

The packages of a release share its version number, as section 7.1 of the [contracts](docs/contracts.md) says.

## 0.1.0 - unreleased

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
| `pqax` | Reads and validates PQAX files, also inside a VPAX file, in a browser and in Node, with no dependency | Reads 0.1 | Node 20.12 or later. Chrome and Edge 103, Firefox 113, Safari 16.4, or later. | Does not write. Tested in Node 24 and, through Playwright, in Chromium 156, Firefox 157, and WebKit 27.2. A part is held in memory whole while it is parsed, and `validate` holds every part. |

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
