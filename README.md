<picture>
  <source media="(prefers-color-scheme: dark)" srcset="brand/logo-dark.svg">
  <img src="brand/logo-light.svg" alt="PQAX" width="320">
</picture>

# PQAX

PQAX, pronounced "pickax", is a file format for the footprint of Parquet tables: how much space the data takes and how it is arranged in files, row groups, and column chunks, without the data itself. A `.pqax` file lets someone analyze the compression and the design choices of a lakehouse without access to it, as a VPAX file does for a VertiPaq model. The file is a zip of JSON tables, and reading it needs no library beyond a zip reader and a JSON parser.

This repository holds the format, its conformance kit, and the reference implementations. The format is at version 0.2 and the implementations at 0.2.2; the packages are on NuGet, npm, and PyPI since 0.1.

## Packages

The packages are published from GitHub releases, as [RELEASING.md](RELEASING.md) describes. The six first ones are published from release 0.1.0 on, `@pqax/pqax` from 0.1.1, and `@pqax/views` from 0.2.0, with one version number, as section 7.1 of the [contracts](docs/contracts.md) says. The [changelog](CHANGELOG.md) says what each release holds.

| Package | Registry | Install | README |
|---|---|---|---|
| `Pqax` | NuGet | `dotnet add package Pqax` | [src/dotnet/Pqax](src/dotnet/Pqax/README.md) |
| `Pqax.Cli` | NuGet, as a .NET tool | `dotnet tool install -g Pqax.Cli` | [src/dotnet/Pqax.Cli](src/dotnet/Pqax.Cli/README.md) |
| `Pqax.Collector` | NuGet | `dotnet add package Pqax.Collector` | [src/dotnet/Pqax.Collector](src/dotnet/Pqax.Collector/README.md) |
| `Pqax.Collector.ParquetNet` | NuGet | `dotnet add package Pqax.Collector.ParquetNet` | [src/dotnet/Pqax.Collector.ParquetNet](src/dotnet/Pqax.Collector.ParquetNet/README.md) |
| `@pqax/pqax` | npm | `npm install @pqax/pqax` | [src/typescript/pqax](src/typescript/pqax/README.md) |
| `@pqax/views` | npm | `npm install @pqax/views` | [src/typescript/views](src/typescript/views/README.md) |
| `pqax` | PyPI | `pip install pqax` | [src/python](src/python/README.md) |

## Contents

| | |
|---|---|
| [docs/object-model.md](docs/object-model.md) | The logical content of a footprint: objects, fields, totals, and depth |
| [docs/serialization.md](docs/serialization.md) | How a file is written: the zip, the JSON, the tables, the manifest, and the rules for readers and writers |
| [docs/contracts.md](docs/contracts.md) | How the implementations are organized: layout, validation rule codes, the public API of each package, tests, roadmap |
| [docs/decisions.md](docs/decisions.md) | What was decided, and when |
| [docs/backlog.md](docs/backlog.md) | What comes next, by area and by stage |
| [conformance/](conformance/README.md) | The kit: rules with codes, a JSON Schema, samples with expected results, invalid samples, a synthetic fixture lakehouse |
| [src/dotnet/Pqax](src/dotnet/Pqax/README.md) | C#: reads, writes, and validates. netstandard2.0 and net8.0, no dependency beyond `System.Text.Json`. |
| [src/dotnet/Pqax.Cli](src/dotnet/Pqax.Cli/README.md) | `pqax check`, `pqax info`, and `pqax collect` |
| [src/dotnet/Pqax.Collector](src/dotnet/Pqax.Collector/README.md) | C#: collects Delta tables from a folder or from OneLake, with its own Parquet footer parser |
| [src/typescript/pqax](src/typescript/pqax/README.md) | TypeScript: reads and validates, in browsers and in Node, no dependency |
| [src/typescript/views](src/typescript/views/README.md) | TypeScript: the view model and three storage views of a footprint, in plain DOM and SVG. The site [pqax.tools](https://pqax.tools/) uses it. |
| [src/python](src/python/README.md) | Python: reads, validates, and writes, with a command line, no dependency |
| [research/](research/) | The prototype and the encoding experiment that the serialization document cites |

## Build and test

Requirements: the .NET 10 SDK, Node 20 or later, Python 3.10 or later.

```bash
dotnet test Pqax.slnx
pip install -e src/python pytest jsonschema
python -m pytest tests/python -q
npm ci && npm run build && npm run typecheck && npm test
python conformance/cross_check.py
```

The last command writes a footprint with every writer and reads and validates it with every implementation, so that the three agree on every number.

## Status

A draft. The format may change until version 1.0, and every change updates the kit and the three implementations together. The [backlog](docs/backlog.md) says what is planned and the [decisions](docs/decisions.md) what is fixed.

## License

MIT. Maintained by [SQLBI](https://www.sqlbi.com). PQAX is a companion of [VertiPaq Analyzer](https://github.com/sql-bi/VertiPaq-Analyzer) and its VPAX format, which can embed a footprint as one part.
