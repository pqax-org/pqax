# PQAX backlog

The changes to make after version 0.1, collected from the review of the C# API on 7 October 2026, whose outcome is in [decisions.md](decisions.md), from the [contracts](contracts.md), and from the first collections run against a real workspace. Started on 7 October 2026 when Marco kept the API of `Pqax` as implemented.

Each item has a stage: **Before publishing** means before the first NuGet, npm, and PyPI packages; **Before 1.0** means before the format is frozen; **Later** means when a user or a tool asks for it. A line through an item marks it done, with the date.

## 1. C# `Pqax`

| Item | Why | Stage |
|---|---|---|
| Async variants of `Open`, `OpenEmbedded`, `Read*`, `Write`, and `Validate`, beside the synchronous ones | Streams from the network, and hosts that forbid blocking such as .NET on WebAssembly | Before 1.0 |
| Columnar storage behind `PqaxTable<TRow>` | A row takes about 270 bytes; a million column chunks hold 270 MB | Before 1.0 |
| A navigation layer over the rows: `TableRow.Columns`, a `PhysicalColumnRow` behind `ColumnChunkRow.PhysicalColumn`, and so on | Tools that walk the model would rather follow objects than call `FindById` | Later |
| A way to ask for frozen rows when reading | Rows are mutable because the collector builds them step by step | Later |
| Keep the unknown members of the part descriptors of the manifest through `From` and `Write`, and keep the part names | A rewrite drops them today | Before 1.0 |
| Check the 2^53 limit of integers and the required fields in the writer | The validators check them, the writer does not | Before 1.0 |
| An option to check the CRC of the zip entries | A damaged file is caught by the JSON parser today, not by the CRC | Later |
| ~~Review the XML documentation and the README once more against the first users: DAX Studio, Bravo, Tabular Editor~~ Done on 20 October 2026 | First impressions of the package | Before publishing |
| A net462 target beside netstandard2.0, so that a .NET Framework host compiles against the `System.Text.Json` assembly that the package installs there | The review of 20 October 2026 found that the netstandard2.0 build fails with `FileLoadException` on .NET Framework until the host adds binding redirects. Proposed on 20 October 2026; deferred by the maintainer on 21 October 2026, after 0.1.0. | Before 1.0 |
| `TryOpenEmbedded`, to test a VPAX file for a footprint without catching `embedded.partMissing` | Most VPAX files have no footprint yet, and a tool that opens many of them would test each one | Before 1.0 |
| `string?` elements in `PartitionRow.Values`, which holds nulls by design | A nullable-aware caller gets no warning today | Before 1.0 |
| Stored entries on .NET Framework, where `ZipArchive` deflates the entries written with `NoCompression` | A stored part is read in place, a deflated one is inflated into memory | Later |
| A serializable `PqaxFormatException` | An exception that crosses an AppDomain on .NET Framework | Later |
| Parallel reads of two detail tables of one document, which share one lock today | Two threads that read two tables wait for each other | Later |

## 2. VPAX integration

| Item | Why | Stage |
|---|---|---|
| The VPAX library embeds `Footprint.pqax` through `System.IO.Packaging`, stored | Section 12 of the serialization | Before 1.0 |
| The VPAX obfuscator obfuscates the footprint, with `FootprintBuilder.From` as the base, instead of dropping the part | Section 10 of the serialization marks the fields to rewrite | Before 1.0 |
| Every tool that rewrites a VPAX preserves the part, or says that it drops it | A compatibility rule of VPAX | Later |

## 3. Collector

| Item | Why | Stage |
|---|---|---|
| Run against tables written by other engines: Spark outside Fabric, Databricks, DuckDB, Trino; fuzz the footer parser with their files | Only Fabric and DuckDB files have been read | Before 1.0 |
| Iceberg: a metadata reader, and the `IcebergDetails`, sort orders, and delete files that the model already has | The model covers it, the collector does not | Later |
| Nested columns as physical columns under their top-level column | Only top-level columns were tested | Before 1.0 |
| ~~Fill `Table.Location`, `Source.Location`, and `Source.Identifiers` from the discovery~~ Done on 20 October 2026; the collector README says what each storage fills | They are empty today | Before publishing |
| ~~State which code a 403 on a footer read gets, `AccessDenied` or `FooterNotRead`, and define `MaxCommitsPerTable` against the commits that the state needs~~ Done on 20 October 2026: `AccessDenied`, with the footer counted as not read, and the limit bounds only the history beyond the commits the state needs; both in the collector README | The tests could not pin them down | Before publishing |
| Await the first footer of a table before the other readers start, so that only one file is read with the full tail | The first 16 files of a table are still read with 32 KB | Later |
| Memory of the collected tables and of the builder rows for a million column chunks | Several hundred MB today | Before 1.0 |
| Read log compaction files and v2 checkpoint sidecars instead of counting them | They are counted and reported, not used | Later |
| Items by id as well as by name on OneLake, and a check of the API version `2018-11-09` against the current one | OneLake accepts both forms but not a mix | Later |
| Depth per table from a budget, as the open question 1 of the object model asks | One depth for the whole collection today | Before 1.0 |
| ~~Run the collector on a real workspace to check the listing of the table folders and the fields of format 0.2~~ Done on 21 October 2026, on a workspace of 78 tables: no issue, the listing cost 120 requests more than the 0.1 collector, and the unreferenced files exceeded the non-live files that the log tracks by two orders of magnitude on the tables that are rewritten daily without a VACUUM | The unreferenced files and the removal times were tested only on the fixture and on synthetic logs | Before 1.0 |

## 4. Conformance kit and format

| Item | Why | Stage |
|---|---|---|
| Samples for `container.encrypted` and `container.zip64`, crafted byte by byte | The Python standard library cannot write them, so no suite tests the two codes | Before 1.0 |
| Samples with Iceberg tables, sort orders, delete files, nested columns, column mapping, model bindings, data values, obfuscated names, and non-ASCII strings | The fixture has none of them | Before 1.0 |
| ~~A field reference of the probe dump in the kit README~~ Done on 20 October 2026: the section The probe dump of [conformance/README.md](../conformance/README.md) | The collector tests use it as their oracle and inferred its meaning | Before publishing |
| `make_kit.py` generates the samples with the Python `Builder` or with the collector, instead of the prototype builder | The exception noted in the kit README | Before 1.0 |
| A code for a string that is not valid Unicode, and codes for the totals that have none, or a decision that they stay without | Gaps noted in `rules.md` | Before 1.0 |
| ~~Unreferenced files and removal times of the non-live files in the format~~ Done on 21 October 2026, in format 0.2: four fields on `Table` and `LastModifiedAt` on `Partition`, in draft 0.5 of the [object model](object-model.md) | A prototype of storage views on a real lakehouse showed that the non-live files are only an upper bound of the space that a table wastes | Before 1.0 |
| Define the derived measures in the object model: compression ratio, dictionary fallback, small files, and the others of section 9 of the model | Defined once, computed the same everywhere | Before 1.0 |
| Decide the open questions of the object model that touch the file: file paths left out, the depth budget, the commit limit, the limit on issues | Sizes and completeness of real footprints | Before 1.0 |
| The `Pages` depth, and views, when a tool asks | Doors kept open by section 9 of the serialization | Later |
| Register a media type for `.pqax`, or settle on `application/zip` | The embedded part declares a content type | Later |
| Freeze the format at 1.0: files from at least three engines read, kit complete, then one version for the three packages | Section 7 of the contracts | Before 1.0 |

## 5. TypeScript and Python packages

| Item | Why | Stage |
|---|---|---|
| ~~Run the TypeScript suite in Firefox and Safari, and time a part of 250,000 rows there~~ Done on 20 October 2026, in Chromium, Firefox, and WebKit through Playwright, with `npm run test:browsers` in CI; the timings are in the package README | Only Node was tried; Chrome and Edge share its engine | Before publishing |
| A size limit on the parts that the TypeScript reader loads, and a validator that does not hold every part in memory | Protection against a hostile or huge file | Before 1.0 |
| The view model in TypeScript for the application and the web reader. Started on 21 October 2026 in `src/typescript/views`, whose README defines the derived values it computes until the object model defines them | The application spec assigns it to TypeScript; it needs the derived measures first | Before 1.0 |
| ~~Release `@pqax/views`: confirm the name, remove `"private": true`, and bring it into the versioning of section 7.1 of the [contracts](contracts.md)~~ Done on 21 October 2026: the name is kept, and the package is published from release 0.2.0 | Private at 0.0.0 since 21 October 2026, so that the API can change | Before 1.0 |
| A partition view: the partitions of a table by size, file count, and deleted rows, from `partitions` and `partitionSizes` | Deferred by the prototype of the storage views on 7 October 2026, which recommended the stack, the treemap, and the file strip first | Later |
| ~~The unreferenced files and the age of the non-live files in the storage stack, once the format has the fields and the reader reads them~~ Done on 21 October 2026, in `@pqax/views` 0.2.0 | The views read only the fields of format 0.1 | Before 1.0 |
| Set the trusted publisher of `@pqax/views` right before the next release, since npm drops a publisher that no publish uses within 48 hours, then remove the token step of the npm job once that release has validated it. The secret `NPM_TOKEN_VIEWS` goes away after the first publish of 21 October 2026, and the step is skipped without it | npm attaches a trusted publisher only to a package that exists | Before 1.0 |
| ~~Remove `"private": true` from the npm package, and publish the three packages~~ Done on 20 October 2026 for the flag; the publish is the item of section 6 | After the API review, which is done | Before publishing |
| A Python writer for notebooks, offered or contributed to semantic-link-labs | A strategic option recorded in [decisions.md](decisions.md) | Later |
| ~~Run the Python suite with polars installed, and on Python 3.10~~ Done on 20 October 2026, on Python 3.10 to 3.14 with and without pandas and polars, and CI runs that matrix | One test is skipped, and only 3.14 was tried | Before publishing |
| Check lone surrogates in strings in the Python and TypeScript validators once the code exists | See the kit item above | Before 1.0 |

## 6. Repository, publishing, and the application

| Item | Why | Stage |
|---|---|---|
| ~~Create the GitHub organization and move the format, the kit, the code, the tests, and the research into this repository~~ Done on 7 October 2026, with a fresh history | The code gets its own home | Before publishing |
| ~~Continuous integration: build, the five suites, and the cross-implementation check on every push~~ Done on 7 October 2026, with the workflow [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) | The checks run by hand today | Before publishing |
| ~~Publish `Pqax`, `Pqax.Cli`, `Pqax.Collector`, and `Pqax.Collector.ParquetNet` to NuGet, `pqax` to npm and PyPI, with release notes and the versioning policy of the plan~~ Done on 21 October 2026: 0.1.0 on NuGet and PyPI, then 0.1.1 on all three with the npm package as `@pqax/pqax`; the policy is section 7.1 of the [contracts](contracts.md) and the notes are in [CHANGELOG.md](../CHANGELOG.md) | Decided: the libraries are the product | Before publishing |
| Sites on pqax.org for the format and pqax.tools for the web reader. pqax.org has a one-page site since 21 October 2026, deployed from `site/`; pqax.tools serves the storage views since 21 October 2026, from its own repository pqax-org/pqax.tools | Domains registered on 7 October 2026 | Later |
| ~~pqax.tools as a site of its own for the web reader, with its own deploy, instead of the page at pqax.org/tools/~~ Done on 21 October 2026: the site installs the two packages from npm, and pqax.org/tools/ redirects to it | The storage views are at pqax.org/tools/ since 21 October 2026, deployed with `site/` | Later |
| VertiPaq Analyzer 3 collects through `Pqax.Collector` and shows the footprint next to the VertiPaq figures | The purpose of the format | Later |
| Compare two footprints of the same lakehouse taken at different times | Open question 7 of the object model | Later |
