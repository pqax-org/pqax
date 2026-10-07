# PQAX

PQAX, pronounced "pickax", is a file format for the footprint of Parquet tables: how much space the data takes and how it is arranged in files, row groups, and column chunks, without the data itself. A `.pqax` file lets someone analyze the compression and the design choices of a lakehouse without access to it, as a VPAX file does for a VertiPaq model. The file is a zip of JSON tables, and reading it needs no library beyond a zip reader and a JSON parser.

This repository holds the format, its conformance kit, and the reference implementations. The format is at version 0.1 and the implementations at 0.1.0. Nothing is published to a package registry yet.

| | |
|---|---|
| [docs/object-model.md](docs/object-model.md) | The logical content of a footprint: objects, fields, totals, and depth |
| [docs/serialization.md](docs/serialization.md) | How a file is written: the zip, the JSON, the tables, the manifest, and the rules for readers and writers |
| [docs/contracts.md](docs/contracts.md) | How the implementations are organized: layout, validation rule codes, the public API of each package, tests, roadmap |
| [docs/decisions.md](docs/decisions.md) | What was decided, and when |
| [docs/backlog.md](docs/backlog.md) | What comes after version 0.1 |
| [conformance/](conformance/README.md) | The kit: rules with codes, a JSON Schema, samples with expected results, invalid samples, a synthetic fixture lakehouse |
| [src/dotnet/Pqax](src/dotnet/Pqax/README.md) | C#: reads, writes, and validates. netstandard2.0 and net8.0, no dependency beyond `System.Text.Json`. |
| [src/dotnet/Pqax.Cli](src/dotnet/Pqax.Cli/README.md) | `pqax check`, `pqax info`, and `pqax collect` |
| [src/dotnet/Pqax.Collector](src/dotnet/Pqax.Collector/README.md) | C#: collects Delta tables from a folder or from OneLake, with its own Parquet footer parser |
| [src/typescript/pqax](src/typescript/pqax/README.md) | TypeScript: reads and validates, in browsers and in Node, no dependency |
| [src/python](src/python/README.md) | Python: reads, validates, and writes, with a command line, no dependency |
| [research/](research/) | The prototype and the encoding experiment that the serialization document cites |

## Build and test

Requirements: the .NET 10 SDK, Node 20 or later, Python 3.10 or later.

```bash
dotnet test Pqax.slnx
pip install -e src/python pytest jsonschema
python -m pytest tests/python -q
cd src/typescript/pqax && npm ci && npm test && cd ../../..
python conformance/cross_check.py
```

The last command writes a footprint with every writer and reads and validates it with every implementation, so that the three agree on every number.

## Status

A draft. The format may change until version 1.0, and every change updates the kit and the three implementations together. The [backlog](docs/backlog.md) says what is planned and the [decisions](docs/decisions.md) what is fixed.

## License

MIT. Maintained by [SQLBI](https://www.sqlbi.com). PQAX is a companion of [VertiPaq Analyzer](https://github.com/sql-bi/VertiPaq-Analyzer) and its VPAX format, which can embed a footprint as one part.
