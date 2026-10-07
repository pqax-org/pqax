# PQAX conformance kit

The kit keeps the readers, writers, and validators of PQAX consistent across languages. It holds the validation rules with their codes, a JSON Schema of the format, sample files with the results that a reader must get from them, invalid files with the codes that a validator must report, and the synthetic fixture that the collector tests use.

Format version 0.1. The [serialization](../docs/serialization.md) and the [object model](../docs/object-model.md) define the format, and section 4 of the [contracts](../docs/contracts.md) defines the contracts that the kit tests. When the kit and those documents disagree, the kit is checked against them first, and the one that is wrong is fixed.

Everything in the kit is synthetic. No name in it comes from a real lakehouse.

## Contents

| Path | Content |
|---|---|
| `rules.md` | The validation rules: the codes, the exact condition of each, the fields that each rule covers |
| `schema/manifest.schema.json` | JSON Schema, draft 2020-12, of `manifest.json` |
| `schema/part.schema.json` | JSON Schema of a part, with one entry in `$defs` for each of the 21 tables |
| `samples/*.pqax`, `samples/*.vpax` | Valid files. Every reader reads them, and every validator accepts them. |
| `samples/invalid/*.pqax` | Files that break one rule each. Every validator rejects them. |
| `samples/invalid/expected.json` | The codes that a validator must report for each invalid file |
| `expected/<sample>.json` | The results that a reader must get from each valid file |
| `fixtures/lakehouse` | A synthetic lakehouse and warehouse in the OneLake layout: Delta logs and Parquet files written by DuckDB |
| `fixtures/probe-dump` | The output of the probe that preceded the collector on the fixture. It is the input of `make_kit.py`, and what the collector must reproduce. |
| `make_kit.py` | Rebuilds `samples/` and `expected/` from `fixtures/probe-dump` |
| `check_schema.py` | Validates the samples against the schema |
| `.gitattributes` | Keeps git from changing the line ends of the fixtures, whose sizes the probe dump records |

## Valid samples

All are built from the fixture: a lakehouse table with 3 files, 10 row groups, a partition column, a deletion vector, and clustering, and a warehouse table with 2 files and 3 row groups. The parts of `sample.pqax` have the same bytes as the parts of [research/prototype/sample.pqax](../research/prototype/sample.pqax). Only the collection in the manifest differs.

| File | Content | What it tests |
|---|---|---|
| `sample.pqax` | Both tables at the `Column chunks` depth: 5 files, 13 row groups, 52 column chunks, 2 partitions, 6 commits | The base case |
| `summary-only.pqax` | `Summary` depth: `manifest.json` and `summary.json` only | A file with no detail |
| `files.pqax` | `Files` depth: partitions, files, and commits | A depth without row groups |
| `row-groups.pqax` | `Row groups` depth | A depth without column chunks |
| `multipart.pqax` | As `sample.pqax`, with the column chunks in 3 parts of 20, 20, and 12 rows, and the files in 2 parts of 3 and 2 rows | Concatenation of parts. The second part of `files` leaves out `deletionVectorSize` and `partition`, which have no value in its rows, so a reader must fill them with `null`. |
| `extensions.pqax` | As `sample.pqax`, with additions of a producer and of a later version, listed below | That a reader ignores what it does not know, and a validator does not report it |
| `later-depth.pqax` | As `sample.pqax`, with `detailDepth` `Pages` for both tables | A depth that this version does not define, which counts as deeper than `Column chunks` |
| `later-version.pqax` | As `sample.pqax`, with `formatVersion` `0.9` | A reader accepts any second number of the version |
| `embedded-stored.vpax` | A zip with `[Content_Types].xml`, `DaxModel.json`, and the bytes of `sample.pqax` as the entry `Footprint.pqax`, stored | Reading a footprint in place inside a VPAX |
| `embedded-deflated.vpax` | The same, with `Footprint.pqax` deflated | Reading a footprint that must be inflated first |

The two `.vpax` files are not real VPAX files. `DaxModel.json` is a placeholder, `{}`, and the package has no other part. They test only how a reader finds and opens the embedded footprint.

The additions in `extensions.pqax`:

| Addition | Where |
|---|---|
| A member of a producer in the manifest | `acme_build`, an object |
| A member of a producer in an element of `parts` | `acme_checksum` in the element for `files.json` |
| A table of a producer in `summary.json`, stated in the manifest | `acme_reviews`, 2 rows |
| A field of a producer in a summary table | `tables.acme_score`: `[7, 9]` |
| A field of a producer in a detail table | `files.acme_tier`: `"hot"` in all 5 rows |
| A member that is not an array, as a later version could write a field in another form | `columnChunks.encodings` is an object, so for a reader of this version `columnChunks` has no `encodings` |
| A member that this version does not define, inside a `Distribution` | `ninetiethPercentile` in the first `tables.fileSizes` |
| A part of a producer, listed in the manifest | `acme_extra.json`, with the table `acme_details`, 3 rows |
| An entry that the manifest does not list | `acme_readme.txt` |

## Invalid samples

Each file in `samples/invalid` is a valid sample with one change. It is named after the code that it breaks, sometimes with a suffix that tells apart two files of the same code. [rules.md](rules.md), section 7, lists them by code. Most come from `sample.pqax`; `depth.files.pqax` comes from `files.pqax`, and `depth.rowGroups.pqax` from `row-groups.pqax`.

`samples/invalid/expected.json` has one member for each file:

```json
{
  "json.bom.pqax": {"codes": ["json.bom"], "change": "summary.json starts with the byte order mark EF BB BF."}
}
```

| Key | Meaning |
|---|---|
| `codes` | The codes that a validator must report. It may report more: a change can break a rule that depends on the one it targets. |
| `change` | What differs from the valid file, for people |

No file tests `container.encrypted` or `container.zip64`: the `zipfile` module of Python, which writes the kit, cannot write an encrypted entry, or ZIP64 records in the central directory of a small archive.

## Expected results

`expected/<sample>.json` holds what a reader must get from `samples/<sample>.pqax` or `samples/<sample>.vpax`. This is `expected/sample.json`:

```json
{
  "file": "sample.pqax",
  "embeddedPart": null,
  "embeddedCompression": null,
  "format": "pqax",
  "formatVersion": "0.1",
  "parts": ["summary.json", "partitions.json", "files.json", "rowGroups.json", "columnChunks.json", "commits.json"],
  "rows": {"sources": 2, "tables": 2, "columns": 10, "partitionSpecs": 1, "partitionFields": 1, "physicalSchemas": 2,
           "physicalColumns": 8, "writers": 3, "operations": 5, "partitions": 2, "files": 5, "rowGroups": 13,
           "columnChunks": 52, "commits": 6},
  "tablesFileSize": 199005,
  "tablesRowCount": 21100,
  "columnChunksCompressedSize": 191505,
  "tables": [
    {"id": 1, "fileCount": 3, "columnChunkCount": 40, "columnChunkCompressedSize": 153328},
    {"id": 2, "fileCount": 2, "columnChunkCount": 12, "columnChunkCompressedSize": 38177}
  ],
  "largestColumns": [{"id": 4, "compressedSize": 128389}, {"id": 9, "compressedSize": 30997}, {"id": 1, "compressedSize": 19374}]
}
```

Every expected file has all these keys.

| Key | Type | Meaning |
|---|---|---|
| `file` | string | The name of the sample in `samples/` |
| `embeddedPart` | string or null | For a VPAX, the entry that holds the PQAX file: `Footprint.pqax`. `null` for a PQAX file. |
| `embeddedCompression` | string or null | For a VPAX, how that entry is written: `stored` or `deflated`. `null` for a PQAX file. |
| `format` | string | `format` in the manifest |
| `formatVersion` | string | `formatVersion` in the manifest |
| `parts` | array of strings | The `name` of every part, in the order of the manifest |
| `rows` | object | For every table that the manifest states, its number of rows: the sum over its parts of what the manifest states. It is also the length of every field of the table once its parts are concatenated. Tables of a producer are included, because a row count comes from the manifest. A table that is not a key has no rows. |
| `tablesFileSize` | integer | The sum of `fileSize` over the rows of `tables`, leaving out `null` |
| `tablesRowCount` | integer | The sum of `rowCount` over the rows of `tables`, leaving out `null` |
| `columnChunksCompressedSize` | integer | The sum of `compressedSize` over all the rows of `columnChunks`, in all its parts. 0 when the file has no column chunks. |
| `tables` | array | One object for each row of `tables`, in the order of the rows |
| `tables[].id` | integer | The `id` of the table |
| `tables[].fileCount` | integer or null | The `fileCount` of the table, as `summary.json` states it. It is not the number of files that the file keeps. |
| `tables[].columnChunkCount` | integer | The number of column chunks of the table, followed from `columnChunks.physicalColumn` to `physicalColumns.physicalSchema` to `physicalSchemas.table`. 0 when the file has none. |
| `tables[].columnChunkCompressedSize` | integer | The sum of `compressedSize` over those column chunks |
| `largestColumns` | array | The three columns with the largest sum of `compressedSize` over their column chunks, followed from `columnChunks.physicalColumn` to `physicalColumns.column`, the largest first. A tie goes to the smaller `id`. A column chunk whose physical column has no `column` counts for no column. Fewer than three when fewer columns have column chunks, and empty when the file has none. |
| `largestColumns[].id` | integer | The `id` of the column |
| `largestColumns[].compressedSize` | integer | The sum of `compressedSize` over its column chunks |

## How to use the kit

### A reader test

For each file in `expected/`:

1. Open `samples/<file>`. When `embeddedPart` is not `null`, open the footprint inside it with the entry point for embedded files and that part name.
2. Compare `format`, `formatVersion`, and `parts`, in order.
3. For every key of `rows`, the row count of the table equals the value, and every field of the table, read whole, has that many values. For a table of the format that is not a key, the row count is 0 and the table has no rows.
4. Compute the other values from the tables that the reader returns, and compare them with the file.

These facts are not in the expected files, and a test can check them too:

| Sample | Fact |
|---|---|
| `sample.pqax` | `commits.metrics` of the second commit is `{}`, a known empty map, not `null`. `tables.delta` of the second table has `readerFeatures` `[]`. |
| `multipart.pqax` | `files.deletionVectorSize` is `[40, null, null, null, null]` and `files.partition` is `[1, 1, 2, null, null]` |
| `extensions.pqax` | The same results as `sample.pqax`, apart from `parts` and `rows`. `columnChunks` has no `encodings`. Where the API returns unknown fields, `tables.acme_score` is `[7, 9]`. Where it returns unknown members of the manifest, `acme_build` is there. |
| `later-depth.pqax` | `detailDepth` is `Pages` for both tables, as written |
| `embedded-stored.vpax`, `embedded-deflated.vpax` | The entry `Footprint.pqax` has the same bytes as `sample.pqax` |
| Every sample | Opening a file reads `manifest.json` and `summary.json`, and no detail part |

A reader refuses three invalid files when it opens them: `container.manifestMissing.pqax`, `manifest.format.pqax`, and `manifest.versionUnknown.pqax`. It opens `json.bom.pqax` and gets the results of `sample.pqax`. It opens `json.invalid.pqax` and fails only when it reads `commits`.

### A validator test

1. Every valid sample gives no issue. For a VPAX, validate the footprint inside it, `Footprint.pqax`: the VPAX itself is not a PQAX file.
2. For every member of `samples/invalid/expected.json`, the validator reports every code in `codes` for that file. It may report more.

### The schema

The schema checks one document at a time. It has no `$id` yet, so a validator loads the two files by name.

| Document | Schema |
|---|---|
| `manifest.json` | `schema/manifest.schema.json` |
| `summary.json` | `schema/part.schema.json#/$defs/summaryPart` |
| Any other part | `schema/part.schema.json#/$defs/detailPart` |
| One table | `schema/part.schema.json#/$defs/<table>`, for example `#/$defs/columnChunks` |

A field of a table is an array with one element for each row. An element is `null` or a value of the type of the field, and a required field has no `null`. A member of a table that is not an array is not a field of this version, so the schema accepts any such member, and unknown tables, fields, and members are allowed everywhere.

What the schema checks: the JSON type of every field and of every member of the manifest, integers within 2^53 - 1, `id` values that are positive and distinct within one part, the form of timestamps, the nested objects, `format`, a `formatVersion` whose first number is 0, the names of parts, `summary.json` first, the kind of tables in each kind of part, one table in a detail part, and the required fields and members.

What it cannot check: the rules on the container and on the text of the JSON (`container.*` except names, `json.invalid`, `json.bom`, `json.duplicateMember`, and `1.0` written for `1`), and every rule that compares documents or rows: `part.tables`, `part.rowCount`, `part.tooLarge`, `manifest.duplicatePart`, `manifest.partMissing`, `id` values repeated across parts, `table.reference`, and the `depth.*` and `totals.*` rules.

The schema is stricter than the codes in one place: it rejects a detail part with no table, which 7.2 rule 6 of the serialization leaves out and no code reports. [rules.md](rules.md), section 8, lists the rules without a code.

`python check_schema.py` validates every valid sample, which must pass, and checks that every invalid sample whose rule the schema can express fails. It needs the `jsonschema` package: `pip install jsonschema`.

## How to rebuild

```bash
python conformance/make_kit.py
python conformance/check_schema.py
```

`make_kit.py` deletes the files in `samples/`, `samples/invalid/`, and `expected/`, and writes them again. Do not edit them by hand: change the script.

It reads `fixtures/probe-dump` and builds the tables with `build_pqax.py`, the builder of the [prototype](../research/prototype/README.md). That import is the one exception to the rule that the kit depends on nothing outside `conformance/`, `src/`, and `tests/`. When the Python package has a writer, `pqax.Builder`, the script will use it instead.

The output is deterministic: every zip entry has the date 2026-10-07 00:00 and the same attributes, and the entries are written in a fixed order. Two runs with the same Python give the same bytes. Another zlib can compress differently: the samples in the repository were written by Python 3.14 on Windows, whose zlib is zlib-ng 1.3.1. The content and the expected results do not depend on it.

When the format changes, change the documents first, then `rules.md`, the schema, and `make_kit.py`, rebuild, and run `check_schema.py`, the checker of the prototype on every valid sample, and the suites of the three packages.

## Cross-implementation check

`cross_check.py` checks that the packages agree with each other. Each writer rewrites three samples, and every validator and every reader checks every file that the writers produce, and the samples themselves as a reference.

```bash
python conformance/cross_check.py                  # prints one row for each file and one column for each validator and reader
python conformance/cross_check.py --keep out       # keeps the files it writes in out/
python conformance/cross_check.py --skip dotnet    # leaves out an implementation: python, dotnet, node
```

| Role | Python | C# | TypeScript |
|---|---|---|---|
| Writer | `Builder.from_document` | `FootprintBuilder.From`, through a runner that the .NET 10 SDK runs as a file-based app | None |
| Collector | | `pqax collect --local fixtures/lakehouse`, when the command line builds | |
| Validator | `python -m pqax check` | `pqax check`, or `PqaxValidator` when the command line does not build | `validate`, through `node` |
| Reader | `pqax.open` | `PqaxDocument.Open` | `openPqax`, through `node` |

The files are `sample.pqax`, `sample.pqax` with at most 20 rows in a detail part, and `extensions.pqax`, as each writer writes them, and the fixture as the collector writes it, which must give the results of `expected/sample.json`. Every validator must accept every file. Every reader must get the `rows`, the length of every table read whole, and `tablesFileSize`, `tablesRowCount`, `columnChunksCompressedSize`, `tables`, and `largestColumns` of the expected file of the sample. A written file has format version 0.1 and part names of its writer, so the parts are not compared.

The script needs only the standard library. It skips, with a message, what is not there: `dotnet` or the .NET 10 SDK, `node`, a build of the TypeScript package, which it runs with `npm run build` when `dist/` is older than the sources unless `--no-build` is given. The exit code is 0 when every check passes, 1 when one fails, and 2 when nothing can run.

## Limits

The fixture is small, and much of the format is not in it:

- Only Delta tables, written by DuckDB. No Iceberg table, so no `iceberg` details, `specId`, `orderId`, sort orders, or delete files.
- Only top-level columns of primitive types: one physical column for each column. No nested column, no column mapping, no field ids, and no schema change: one physical schema for each table, and `staleColumnSize` 0.
- No issues, no model bindings, and no obfuscation fields.
- No data values: no `minValue`, `maxValue`, partition values, or commit parameters, and `collection` says so.
- Every string is ASCII, so no sample tests the decoding of other UTF-8 text.
- No encrypted entry and no ZIP64 archive.
- The largest detail table has 52 rows. Only `part.tooLarge.pqax` has a large part: 1.5 MB of JSON text, which takes 1.5 KB in the zip.
- The expected results check counts, sums, and the joins between tables, not the value of every field.
