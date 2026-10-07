# PQAX decisions

The decisions that the format and its implementations rest on, with their dates. They were taken by the maintainers at SQLBI. A statement marked **Decided** in the object model, the serialization, or the contracts refers to this list; everything else in those documents is a proposal.

## The format

| Date | Decision |
|---|---|
| 2026-10-04 | The Parquet information goes into a separate format, PQAX, that a VPAX file can embed as one part. VPAX itself does not change. |
| 2026-10-04 | The name is PQAX, pronounced "pickax", with the extension `.pqax`. A PQAX file holds the footprint of a set of Parquet tables: how much space the data takes and how it is arranged, without the data. |
| 2026-10-04 | Principles: independent of any library or platform; a diagnostic, not a duplicate of Parquet; valid at any depth of detail; room to grow without breaking the readers that exist. |
| 2026-10-05 | The format is defined before any implementation, and the logical model before the serialization. |
| 2026-10-05 | The first version describes top-level columns only, with a door open for nested fields. The root object is a `Footprint` and a lakehouse is a `Source`. |
| 2026-10-06 | Names in the file are in lower camel case. The part embedded in a VPAX is `Footprint.pqax`. The first version has no views. Values that repeat are written in full, with no dictionary. A detail part holds at most 250,000 rows. |
| 2026-10-07 | The text form of a logical type is its upper-case name with its parameters, such as `TIMESTAMP(MICROS, UTC)`, and the name of the converted type when a file has only that. |

## The implementations

| Date | Decision |
|---|---|
| 2026-10-05 | The collector lives in its own repository and package, outside the VPAX libraries. |
| 2026-10-07 | The .NET reader and writer are published as NuGet packages, so that other tools reuse them. The command line is a thin layer over the libraries, and nothing is possible only from the command line. |
| 2026-10-07 | One collector, in C#, with its own Parquet footer parser. Delta checkpoints are read through Parquet.Net behind an interface, in a separate package. |
| 2026-10-07 | A conformance kit, not shared code, keeps the implementations consistent: rules with codes, a JSON Schema, samples with expected results. |
| 2026-10-07 | Packages: `Pqax`, `Pqax.Cli`, `Pqax.Collector`, and `Pqax.Collector.ParquetNet` on NuGet; `pqax` on npm and on PyPI. |
| 2026-10-07 | The license is MIT. |
| 2026-10-07 | The device code is the default sign-in of `pqax collect`; `--browser` asks for the browser. |
| 2026-10-07 | A Python writer for notebooks is a strategic option, not a first step. |
| 2026-10-07 | The format, the kit, and the reference implementations live in this repository while the format is 0.x, so that one commit changes all of them together. |

## The C# API of `Pqax` 0.1

Reviewed on 7 October 2026 and kept as implemented. The later changes that some points call for are in the [backlog](backlog.md).

1. Rows mirror the file. A reference is a `long?`, resolved with `FindById`; there is no object graph. A navigation layer may come later without breaking anything.
2. Names: the `Row` suffix on the 21 row classes, `FootprintBuilder`, and the `Pqax` prefix on `PqaxDocument`, `PqaxTable`, `PqaxValidator`, and `PqaxFormatException`. `Id` is a `long` that is 0 when absent.
3. Rows are mutable classes with value equality, also when read from a file.
4. Rows are objects in memory, about 270 bytes each. A columnar storage behind the same `PqaxTable<TRow>` surface is planned.
5. Synchronous I/O only. Async variants may be added beside the existing methods.
6. One exception type, `PqaxFormatException`, with the rule codes of the conformance kit.
7. Unknown fields, tables, and manifest members survive a read and a rewrite with `FootprintBuilder.From`. Unknown members of the part descriptors do not; a rewrite names the parts after their tables and writes `formatVersion` 0.1.
8. Not in the package: obfuscation, the embedding of a footprint into a VPAX package, the derived measures of the view model, anything about Parquet or Delta, and the CRC check of the zip entries.
