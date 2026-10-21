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
| 2026-10-21 | The npm package is `@pqax/pqax`, under the npm organization `pqax`: the registry refuses the unscoped name `pqax` as too similar to the packages `rax` and `sax`. The six packages move to 0.1.1 together, with no other change. |

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

Point 7 describes 0.1. From 0.2.0, a rewrite writes `formatVersion` 0.2.

## Format 0.2

Proposed on 21 October 2026, with the work on format 0.2, and decided by the maintainer the same day. The fields are defined in sections 6.4 and 6.6 of the [object model](object-model.md).

| Date | Decision | Status |
|---|---|---|
| 2026-10-21 | Format 0.2 adds five optional fields at collection level 1: `UnreferencedFileCount`, `UnreferencedFileSize`, `NonLiveOldestRemovedAt`, and `NonLiveNewestRemovedAt` on `Table`, and `LastModifiedAt` on `Partition`. No new table, no new required field, and no new validation code. | Decided, 21 October 2026 |
| 2026-10-21 | A data file, for the unreferenced files, is a file whose name ends with `.parquet`, in upper or lower case, outside the folders `_delta_log` and `_change_data`. Its path relative to the table location is compared with the decoded path of each live file, and the comparison is case-sensitive. | Decided, 21 October 2026 |
| 2026-10-21 | A file whose size the listing does not give counts in `UnreferencedFileCount` and not in `UnreferencedFileSize`. This is the one exception to rule 4 of section 4.2 of the object model. | Decided, 21 October 2026 |
| 2026-10-21 | The removal times cover the same tombstones that `NonLiveFileCount` counts. The latest remove action of a path wins, and when it has no timestamp, the timestamp of an earlier remove of the same path is kept. The oldest and the newest are taken over the tombstones that have a timestamp, so one without a timestamp does not make them absent; a strict reading of rule 4 of section 4.2 would. | Decided, 21 October 2026 |
| 2026-10-21 | The collector lists the folder of each table at every collection level, with no option to turn it off. A listing that fails becomes an issue, `AccessDenied` for a 403 and the new code `DataFilesNotListed` otherwise, and the two unreferenced fields are then absent. | Decided, 21 October 2026 |
| 2026-10-21 | The valid samples of the kit become format 0.2, and a new sample, `format-0.1.pqax`, keeps a file of format 0.1. The collector tests compare the collector with the samples field by field, so the samples must carry the new fields. | Decided, 21 October 2026 |
| 2026-10-21 | The probe dump of the kit is extended by hand with four fields, because the probe no longer runs: `unreferencedFileCount`, `unreferencedFileSize`, `delta.removedOldestTimestamp`, and `delta.removedNewestTimestamp`. | Decided, 21 October 2026 |
| 2026-10-21 | The tombstone already in the checkpoint of the fixture gives equal oldest and newest removal times. The synthetic logs of the collector tests cover distinct ones, so the fixture keeps its Delta logs. | Decided, 21 October 2026 |
| 2026-10-21 | `pqax info` does not change. | Decided, 21 October 2026 |
| 2026-10-21 | A rewrite with `FootprintBuilder.From` in C# or `Builder.from_document` in Python writes format 0.2. | Decided, 21 October 2026 |
