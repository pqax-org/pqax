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

## Derived measures

The measures that a reader derives from the fields, defined in section 10 of the [object model](object-model.md). The first five decisions were taken by the maintainer on 21 October 2026 for the storage views, and the [views README](../src/typescript/views/README.md) records them under "Decisions" and "Non-live and unreferenced files". The others were proposed on 22 October 2026 with section 10, and wait for the maintainer.

| Date | Decision | Status |
|---|---|---|
| 2026-10-21 | The waste of a table is the non-live files, the unreferenced files, the estimate of the deleted rows, and the stale columns: `nonLive + unreferenced + deleted + stale`. | Decided, 21 October 2026 |
| 2026-10-21 | The metadata and the footers are overhead. They are drawn, but they are outside the waste that orders the tables and outside the denominator of the shares. | Decided, 21 October 2026 |
| 2026-10-21 | Two small-file shares: the files below 8 MiB and the files below 128 MiB. | Decided, 21 October 2026 |
| 2026-10-21 | Commits per day are over the span of the commits read, from the oldest to the newest, with the days since the last commit beside them, counted to the collection. | Decided, 21 October 2026 |
| 2026-10-21 | The non-live and the unreferenced files are counted once. With a listing, the non-live component is `min(nonLiveFileSize, unreferencedFileSize)` and the unreferenced component the rest of `unreferencedFileSize`. Without one, the non-live component is `nonLiveFileSize`, an upper bound. | Decided, 21 October 2026 |
| 2026-10-22 | The derived measures are defined in section 10 of the object model. The views README no longer defines them and refers to the object model. | Proposed |
| 2026-10-22 | A measure has the name that the storage views give it, written with a capital first letter as a field is, and each language writes it in its own casing, as rule 5 of section 3 of the [contracts](contracts.md) says for fields. | Proposed |
| 2026-10-22 | A measure is null when a field that it needs is absent or its denominator is zero, never zero for an unknown value. Three exceptions: an absent stack component counts as zero in the waste, the overhead, and the total, which are then lower bounds and null only when every component is absent; the deleted estimate is zero when the deleted row count is zero; a sum over tables or columns adds those that have the value, and is null when none has it. The data of a stack, and so every share of the stack, is null when the live data is, and the share of a component is null when the component is absent. | Proposed |
| 2026-10-22 | Each measure states whether it is exact over the fields it reads, an estimate, or a bound. The deleted bytes are the one estimate in bytes; the non-live component and the non-live share are upper bounds; the small-file shares from the quartiles, and the V-Order share from the totals when not every footer was read, are lower bounds; the last commit from `LastModifiedAt` is an estimate. | Proposed |
| 2026-10-22 | The measures read the quartiles of a `Distribution` as taken by position, `floor(n/4)`, `floor(n/2)`, and `floor(3n/4)` from 0 in the sorted values, as the collector computes them. Section 4.3 of the object model states it in its next draft. | Proposed |
| 2026-10-22 | The two thresholds are 128 MiB and 8 MiB in binary units, a file is below one when its size is strictly less, and an implementation may let the caller change them. | Proposed |
| 2026-10-22 | Every implementation offers every measure of section 10 for tables, columns, and the collection, with the same names and the same nulls. The two measures over a set of columns, which the treemap uses, are required of the views only. | Proposed |
| 2026-10-22 | Where the measures live: in TypeScript, in `@pqax/views`, from `buildModel`; in C#, in the `Pqax` library under the namespace `Pqax.Measures`, which reverses for the derived measures point 8 of the review of the C# API above; in Python, in the module `pqax.measures`. Each is a set of pure functions over the rows that the reader returns, with no new dependency. | Proposed |
| 2026-10-22 | The deleted bytes keep the estimate of the views, over the whole file size, footers and stale columns included, rather than over the live data only. The difference is small next to the estimate itself, and the components of the stack still add up to the file size. | Proposed |
| 2026-10-22 | The delete files of Iceberg and the files of the Delta deletion vectors stay outside the stack in this draft. | Proposed |
| 2026-10-22 | Commits per day divide `CommitsRead` only, since `OldestCommitAt` is the oldest of the commits read. `CommitCount` counts commits older than that and would overstate the rate. | Proposed |
| 2026-10-22 | The share of the files without a column divides `FileCountWithoutColumn` by `FooterCount`, since both cover the files whose footer was read. | Proposed |
| 2026-10-22 | Whether a VACUUM or an OPTIMIZE ran is null for a table that has no operation, since its list of operations is empty when the commits carry no operation name; it is false only when the table has operations and none of them matches. | Proposed |
