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
| 2026-10-23 | The Python collector for notebooks lives in the `pqax` package on PyPI, as the optional extra `pqax[collect]`, so that one version covers every package and the kit's cross check runs it. A thin wrapper that imports `pqax` is offered to semantic-link-labs after the planned waves of tools are complete, not before. |

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

The measures that a reader derives from the fields, defined in section 10 of the [object model](object-model.md). The first five decisions were taken by the maintainer on 21 October 2026 for the storage views, and the [views README](../src/typescript/views/README.md) records them under "Decisions" and "Non-live and unreferenced files". The others were proposed on 22 October 2026 with section 10 and decided by the maintainer the same day, the eighth with an amendment. The last two were proposed on 22 October 2026, when the three implementations were aligned with section 10, and were decided by the maintainer on 22 and 24 October 2026.

| Date | Decision | Status |
|---|---|---|
| 2026-10-21 | The waste of a table is the non-live files, the unreferenced files, the estimate of the deleted rows, and the stale columns: `nonLive + unreferenced + deleted + stale`. | Decided, 21 October 2026 |
| 2026-10-21 | The metadata and the footers are overhead. They are drawn, but they are outside the waste that orders the tables and outside the denominator of the shares. | Decided, 21 October 2026 |
| 2026-10-21 | Two small-file shares: the files below 8 MiB and the files below 128 MiB. | Decided, 21 October 2026 |
| 2026-10-21 | Commits per day are over the span of the commits read, from the oldest to the newest, with the days since the last commit beside them, counted to the collection. | Decided, 21 October 2026 |
| 2026-10-21 | The non-live and the unreferenced files are counted once. With a listing, the non-live component is `min(nonLiveFileSize, unreferencedFileSize)` and the unreferenced component the rest of `unreferencedFileSize`. Without one, the non-live component is `nonLiveFileSize`, an upper bound. | Decided, 21 October 2026 |
| 2026-10-22 | The derived measures are defined in section 10 of the object model. The views README no longer defines them and refers to the object model. | Decided, 22 October 2026 |
| 2026-10-22 | A measure has the name that the storage views give it, written with a capital first letter as a field is, and each language writes it in its own casing, as rule 5 of section 3 of the [contracts](contracts.md) says for fields. | Decided, 22 October 2026 |
| 2026-10-22 | A measure is null when a field that it needs is absent or its denominator is zero, never zero for an unknown value. Three exceptions: an absent stack component counts as zero in the waste, the overhead, and the total, which are then lower bounds and null only when every component is absent; the deleted estimate is zero when the deleted row count is zero; a sum over tables or columns adds those that have the value, and is null when none has it. The data of a stack, and so every share of the stack, is null when the live data is, and the share of a component is null when the component is absent. | Decided, 22 October 2026 |
| 2026-10-22 | Each measure states whether it is exact over the fields it reads, an estimate, or a bound. The deleted bytes are the one estimate in bytes; the non-live component and the non-live share are upper bounds; the small-file shares from the quartiles, and the V-Order share from the totals when not every footer was read, are lower bounds; the last commit from `LastModifiedAt` is an estimate. | Decided, 22 October 2026 |
| 2026-10-22 | The measures read the quartiles of a `Distribution` as taken by position, `floor(n/4)`, `floor(n/2)`, and `floor(3n/4)` from 0 in the sorted values, as the collector computes them. Section 4.3 of the object model states it in its next draft. | Decided, 22 October 2026 |
| 2026-10-22 | The two thresholds are 128 MiB and 8 MiB in binary units, a file is below one when its size is strictly less, and an implementation may let the caller change them. | Decided, 22 October 2026 |
| 2026-10-22 | Every implementation offers every measure of section 10 for tables, columns, and the collection, with the same names and the same nulls. The two measures over a set of columns, which the treemap uses, are required of the views only. | Decided, 22 October 2026 |
| 2026-10-22 | Where the measures live: in TypeScript, in `@pqax/views`, from `buildModel`; in C#, in the `Pqax` library, in the static class `StorageMeasures` of the namespace `Pqax`, which reverses for the derived measures point 8 of the review of the C# API above; in Python, in the module `pqax.measures`. The maintainer amended the proposal, which put the C# measures under a namespace `Pqax.Measures`: they stay where the code has them. Each is a set of pure functions over the rows that the reader returns, with no new dependency. | Decided, 22 October 2026 |
| 2026-10-22 | The deleted bytes keep the estimate of the views, over the whole file size, footers and stale columns included, rather than over the live data only. The difference is small next to the estimate itself, and the components of the stack still add up to the file size. | Decided, 22 October 2026 |
| 2026-10-22 | The delete files of Iceberg and the files of the Delta deletion vectors stay outside the stack in this draft. | Decided, 22 October 2026 |
| 2026-10-22 | Commits per day divide `CommitsRead` only, since `OldestCommitAt` is the oldest of the commits read. `CommitCount` counts commits older than that and would overstate the rate. | Decided, 22 October 2026 |
| 2026-10-22 | The share of the files without a column divides `FileCountWithoutColumn` by `FooterCount`, since both cover the files whose footer was read. | Decided, 22 October 2026 |
| 2026-10-22 | Whether a VACUUM or an OPTIMIZE ran is null for a table that has no operation, since its list of operations is empty when the commits carry no operation name; it is false only when the table has operations and none of them matches. | Decided, 22 October 2026 |
| 2026-10-22 | When the tables are ordered by Waste, then by Total, as section 10.2 of the object model says, a table whose Waste or Total is null comes after the tables that have it, which section 10.2 does not say. The storage views, `pqax tables` in C#, and `pqax tables` in Python order the tables so. | Decided, 24 October 2026 |
| 2026-10-22 | A time that a measure reads and that does not parse as a timestamp counts as absent, as a negative size does under rule 2 of section 10.1, which names only sizes and counts. So DaysSinceLastCommit counts to `StartedAt` when `CompletedAt` does not parse, and LastCommitAt skips a `SnapshotTimestamp` that does not parse. | Decided, 24 October 2026 |

## Comparing footprints

How two footprints of the same lakehouse taken at different times are compared, defined in section 13 of the [object model](object-model.md), which answers its open question 7. Proposed on 24 October 2026 with `pqax diff` in C# and Python, and decided by the maintainer the same day.

| Date | Decision | Status |
|---|---|---|
| 2026-10-24 | A table of the older footprint is the same table as one of the newer when both have a TableId and the two are equal. Two tables whose TableIds differ are never the same table, even with the same name: a table dropped and created again is removed and added, and both are marked as recreated. | Decided, 24 October 2026 |
| 2026-10-24 | When at least one of two tables has no TableId, they are the same table when they have the same source, Namespace, and Name: a folder of Parquet files has no TableId, nor has a table whose log was not read. A source is the same when its Kind and Name are, since its `Id` is local to its footprint and Location and Identifiers are not always present. Matching by name before TableId is rejected, since it would pair a table with the one that replaced it. | Decided, 24 October 2026 |
| 2026-10-24 | A column of a kept table is the same column when both have a FieldId and the two are equal, and otherwise, when at least one has none, when the Name is the same. A rename with column mapping keeps the column; a column dropped and added again gets a new FieldId and is removed and added. | Decided, 24 October 2026 |
| 2026-10-24 | A partition is identified by its Values, a file by its Path, and a commit by its CommitId, within a kept table. Partitions without Values, and files without a Path, are not compared. Names, paths, and values compare case-sensitively. | Decided, 24 October 2026 |
| 2026-10-24 | Pairing is one to one, in three passes for tables and columns: the same identifier and the same name, then the same identifier, then the fallback by name; each pass takes the older objects in the order of the file and pairs each with the first unpaired newer one. | Decided, 24 October 2026 |
| 2026-10-24 | The files of a kept table are compared when both footprints keep them, at the `Files` depth or deeper; the partitions and the commits when both footprints keep that list. The totals and the measures are compared whatever the depth, with a null delta where either footprint lacks the value. | Decided, 24 October 2026 |
| 2026-10-24 | Each compared value has its older value, its newer value, and the delta, the newer minus the older; a timestamp has its delta in milliseconds; a boolean, a text, or a list has none. | Decided, 24 October 2026 |
| 2026-10-24 | A kept table is changed when its name, its SnapshotId, a total, a column, a file, a partition, or a commit differs. A measure alone does not make it changed, since DaysSinceLastCommit moves with the time of the collection. | Decided, 24 October 2026 |
| 2026-10-24 | `pqax diff` orders the tables by the absolute change of the Total of the stack, a table added or removed by its Total, a null change last; `--sort` takes waste, total, rows, files, or name as `pqax tables` does, and `--top N` keeps the N largest changes. The text and the JSON are the same, byte for byte apart from the line ends, in C# and Python. | Decided, 24 October 2026 |

## The brand

The logo of PQAX, in [brand/](../brand/README.md). The drafts were made in five rounds on 21 and 22 October 2026 in the private repository of the application, and the choices were taken by the maintainer and his team.

| Date | Decision | Status |
|---|---|---|
| 2026-10-21 | The mark is Chunks: a Parquet file of three row groups of column chunks, with the footer below them and the folded corner of a file. Herringbone, Pick, Footprint, Monogram, Treemap, and Slice were dropped. | Decided, 21 October 2026 |
| 2026-10-21 | The lock-up puts the mark beside the wordmark: a square of 1.2 times the cap height, centered on the cap height, 0.5 cap height from the P, measured on the ink. | Decided, 21 October 2026 |
| 2026-10-21 | The colors of the mark are `#3C7AFE` for the chunks and `#EA580C` for the footer, the same in light and dark mode, so that one transparent bitmap serves both. The other palettes were dropped. | Decided, 21 October 2026 |
| 2026-10-23 | The wordmark is PQAX in Red Hat Display Bold, version 5.0.0 under the SIL Open Font License 1.1, with the kerning of the font plus 6% of the cap height between the letters, as paths, so that no font file travels with the logo. | Decided, 23 October 2026 |
| 2026-10-23 | The wordmark is slate 800 `#1E293B` on a light background and slate 200 `#E2E8F0` on a dark one. pqax.org and pqax.tools take the same two colors for their text, with the rest of their palette from the slate scale. | Decided, 23 October 2026 |
| 2026-10-23 | The files live in `brand/` of this repository: the SVG sources and the PNG exports. The sites use the mark as favicon and the lock-up in the header; the NuGet packages carry the mark as their icon; the README shows the lock-up of the reader's mode. | Decided, 23 October 2026 |

## The export and the Power BI template

Proposed on 24 October 2026 with `pqax export` and `templates/powerbi`, and decided by the maintainer the same day.

| Date | Decision | Status |
|---|---|---|
| 2026-10-24 | The Power BI template lives in this repository as a PBIP project, a TMDL model and a PBIR report in text, and is not published as a `.pbit` download. The text is reviewed and diffed with the code, a test checks its columns against the columns of `pqax export` in every build, and a download would have to be built by hand in Power BI Desktop at each release and could drift from the export. A user copies the folder, or takes it from the source archive of a release. | Decided, 24 October 2026 |
| 2026-10-24 | A footprint in an export is identified by `footprintId`, the SHA-256 of the bytes of its PQAX file in lowercase hex, of the embedded part for a VPAX. It needs nothing from the collector, gives the same id to the same file wherever it is copied or embedded, and lets the export refuse a footprint twice. A file rewritten with other bytes and the same content gets another id. | Decided, 24 October 2026 |
| 2026-10-24 | In the export, a measure has its name of section 10 of the object model with a lower-case first letter, and the stack component shares, which section 10 does not name, are `<component>DataShare`, such as `nonLiveDataShare`, since `nonLiveShare` is NonLiveShare of 10.5. The file size figures are `fileSizesCount` to `fileSizesMaximum`. | Decided, 24 October 2026 |
| 2026-10-24 | The measures of the template add, for a period, the tables of its last collection day, each table, by source name and label, from its latest collection of that day, since a size is a stock and adding the days would count it again. | Decided, 24 October 2026 |

## Footers of other writers and nested columns

Proposed on 28 October 2026, with the run of the footer parsers of the two collectors against the Parquet files of other writers and against mutated footers, and with the nested columns as physical columns under their top-level column. The code does what each row proposes; the alternative is what the maintainer may choose instead. They wait for the maintainer. [conformance/README.md](../conformance/README.md) describes the run.

| Date | Proposal | Alternative | Status |
|---|---|---|---|
| 2026-10-28 | Every structure, list, set, and map counts one level toward the depth limit of 64 of the parsers, whether the parser reads it or skips it, so the statistics of a column chunk are at level 7, and a deeper footer is `FooterInvalid`: "The footer nests values more than 64 levels deep." Only structures counted before: 10,000 nested lists overflowed the stack of the C# collector, which ended the process, and Python refused them from about 500 levels with another message. | A separate, higher limit for lists, sets, and maps, such as 1,000 levels, which C# still recurses through. | Proposed |
| 2026-10-28 | The columns of a folder of Parquet files get their type for every schema that the parsers read, 256 levels below the root at most: the limit of 64 levels of `TopLevelFields` goes. A schema of 65 to 256 levels made the table `TableNotRead`, with a message that named the exception type of each language, `ParquetFooterException` or `FooterError`. | Refuse in the parsers a schema of more than 64 levels as `FooterInvalid`, for the files of a Delta table too. | Proposed |
| 2026-10-28 | Every list and map of a footer holds at most 1,000,000 elements, the default limit of a Thrift container in the Parquet reader of Arrow C++, so that no file that pyarrow reads is refused for it; and the row groups of a footer hold at most 4,194,304 column chunks, and as many sorting columns, in all. Past either, the footer is `FooterTooLarge`, with the code of the footer over 64 MB. An empty column chunk takes one byte in a footer and some 300 bytes in the memory of either collector: an 8 MB footer of 8 million of them takes about 2.5 GB in C# and 2.9 GB in Python, and a footer of 64 MB would take some 20 GB. With the limits, a footer of empty column chunks stops at about 1.3 GB in C# and 1.5 GB in Python; the other lists take at most some 15 times their bytes. | 2,097,152 column chunks, which a footer of 64 MB from a writer, at about 40 bytes a chunk, still never reaches, and which halves the worst case; or no count limit, and the worst case of a hostile footer of 64 MB documented, some 20 GB. | Proposed |
| 2026-10-28 | With data values, a decimal bound has text only when its unscaled value takes at most 32 bytes, the largest decimal of Arrow, and its scale is at most 76; otherwise the bound is null. Python raised `ValueError` past 4,300 digits, which became `FooterNotRead` where C# read the footer, and a scale of 2,147,483,647 made Python build a string of 2 GB. | Such a bound in hexadecimal, `0x…`, as the bound of a binary column. | Proposed |
| 2026-10-28 | The keys of the allow-list and of V-Order, and the names of the columns that the leaves map to, are compared as .NET `OrdinalIgnoreCase` compares them, in Python too: by the upper case of each character, where a character outside ASCII never folds into ASCII, so `ſ` is not `s`, `ı` is not `i`, and the Kelvin sign is not `k`. Python compared the keys with `str.lower()`, where the Kelvin sign is `k`, and the names with a fold where `ſ` is `S`. | ASCII case only, in both languages, which changes C# for names and keys with letters outside ASCII, such as `é`. | Proposed |
| 2026-10-28 | A structure that appears twice in a footer, such as the metadata or the statistics of a column chunk, replaces the first whole, as Java and Rust read it; both parsers merged the two, as the Thrift library of C++ does. | Merge them, as C++ does, which keeps a field that only the first has. | Proposed |
| 2026-10-28 | An empty list of a footer may give any type to its elements, and a field header of type 0 ends its structure whatever its delta, as the Thrift libraries of C++, Java, and Rust read them. fastparquet writes the row groups of a file without rows as an empty list of type 0, which both parsers refused, and C# read a header of type 0 as a field of an unknown wire type where Python ended the structure. | Refuse both as `FooterInvalid`, which refuses files that the other readers read. | Proposed |
| 2026-10-28 | The totals of a column of a nested type are over the column chunks of all its leaves, which section 6.5 of the object model leaves open: `ValueCount` and `NullCount` add the values and the nulls of every leaf, so a structure of three fields counts three values a row and a list counts its elements, and `LargestChunkDistinctCount` is the largest over the chunks of all its leaves, so it is no lower bound of the distinct values of a list. The sizes, `ChunkCount`, and the dictionary totals add the leaves as the rule `totals.columnCompressedSize` needs. | `ValueCount`, `NullCount`, and `LargestChunkDistinctCount` absent for a column of more than one leaf or with a repeated leaf, and the other totals added over the leaves. | Proposed |
| 2026-10-28 | The `LogicalType`, the `Repetition`, and the `FieldId` of a physical column are those of its leaf: the element of a list of integers is `OPTIONAL` with no annotation, not `REPEATED` and `LIST`. Section 6.8 of the object model gives `LIST` and `MAP` as examples of `LogicalType`, which annotate groups and so no leaf: the examples would drop them. | Give a leaf under a repeated group the repetition `REPEATED`, and keep the examples. | Proposed |
