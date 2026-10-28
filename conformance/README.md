# PQAX conformance kit

The kit keeps the readers, writers, and validators of PQAX consistent across languages. It holds the validation rules with their codes, a JSON Schema of the format, sample files with the results that a reader must get from them, invalid files with the codes that a validator must report, and the synthetic fixture that the collector tests use.

Format version 0.2. The [serialization](../docs/serialization.md) and the [object model](../docs/object-model.md) define the format, and section 4 of the [contracts](../docs/contracts.md) defines the contracts that the kit tests. When the kit and those documents disagree, the kit is checked against them first, and the one that is wrong is fixed.

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
| `expected/measures/<sample>.json` | The derived measures of the tables, the columns, and the collection of each valid file, as the storage views compute them |
| `expected/diff/diff-before.diff-after.json` | The comparison of the two samples of the comparison test, as `pqax diff --json` writes it |
| `fixtures/lakehouse` | A synthetic lakehouse and warehouse in the OneLake layout: Delta logs and Parquet files written by DuckDB, and one data file that no commit references |
| `fixtures/probe-dump` | The output of the probe that preceded the collector on the fixture. It is the input of `make_kit.py`, and the oracle of the collector tests. The section on the probe dump describes every field. |
| `make_kit.py` | Rebuilds `samples/` and `expected/` from `fixtures/probe-dump` |
| `make_measures.py` | Rebuilds `expected/measures/` with the view model of `@pqax/views` |
| `make_diff.py` | Rebuilds the two samples of the comparison test with `pqax.Builder`, and `expected/diff/` |
| `check_schema.py` | Validates the samples against the schema |
| `.gitattributes` | Keeps git from changing the line ends of the fixtures, whose sizes the probe dump records |

## Valid samples

All are built from the fixture: a lakehouse table with 3 files, 10 row groups, a partition column, a deletion vector, and clustering, and a warehouse table with 2 files and 3 row groups. The folder of the lakehouse table also holds `Year=2024/part-0004.parquet`, an aborted write that no commit references, so the table has one unreferenced file. Its checkpoint keeps one removed file, `part-0000.parquet`, that a VACUUM already deleted.

| File | Content | What it tests |
|---|---|---|
| `sample.pqax` | Both tables at the `Column chunks` depth: 5 files, 13 row groups, 52 column chunks, 2 partitions, 6 commits | The base case |
| `format-0.1.pqax` | As `sample.pqax`, in format 0.1, without the five fields that 0.2 added. Its parts have the same bytes as the parts of [research/prototype/sample.pqax](../research/prototype/sample.pqax). Only the collection in the manifest differs. | A reader of 0.2 opens a file of 0.1 |
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

The comparison test has two more valid samples, which are not built from the fixture. `make_diff.py` writes them with `pqax.Builder`, the writer of the Python package: two footprints of one synthetic lakehouse, `Diff_Lakehouse`, collected a week apart, with 7 tables each at the `Files` and `Summary` depths, partitions with their values, and the 8 most recent commits of each table. Between the two, the lakehouse changed in the ways below, which `SCENARIO` in the script declares.

| File | Content | What it tests |
|---|---|---|
| `diff-before.pqax` | The older footprint, collected on 1 October 2026: `sales`, `orders`, `customers`, `products`, `legacy`, `raw.staging`, `audit` | That `pqax diff` reports exactly the changes made, against `expected/diff/` |
| `diff-after.pqax` | The newer footprint, collected on 8 October 2026: `events`, `sales`, `clients`, `orders`, `products`, `raw.staging`, `audit` | The same |

| Table | What changed | What the diff must report |
|---|---|---|
| `sales` | Grown: a partition deleted (1 file removed), 2 files appended, one of them in a new partition, and the deletion vector of a kept file grew. Column mapping: `amt` renamed `amount` with the same FieldId, `note` dropped, `discount` dropped and added again with a new FieldId, `region` added. 5 commits more, and 2 out of the 8 kept. | Kept, matched by TableId, changed; files 2 added, 1 removed, 3 kept, 1 changed; partitions 1 added, 1 removed, 2 kept, 2 changed; commits 5 added, 2 removed, 3 kept; columns `discount` and `region` added, `discount` and `note` removed, `amt` kept and renamed |
| `orders` | Shrunk: `price` widened from `int` to `bigint`, an OPTIMIZE that rewrote 4 files as 1, then a VACUUM that deleted the unreferenced files older than the retention. No field ids. | Kept, changed; files 1 added, 4 removed; columns matched by name, `price` changed in type; `maintenance.hasVacuum` false to true |
| `customers` | Renamed `clients`, with the same TableId and the same data | Kept and renamed |
| `products` | Dropped and created again with a new TableId | Removed and added, both recreated |
| `legacy` | Dropped | Removed |
| `events` | Created | Added |
| `raw.staging` | A folder of Parquet files with no table format and no TableId; one file and one column added | Kept, matched by name |
| `audit` | The same data, kept at the `Files` depth before and at the `Summary` depth after | Kept and unchanged; its files are not compared |

The additions in `extensions.pqax`:

| Addition | Where |
|---|---|
| A member of a producer in the manifest | `acme_build`, an object |
| Members of a producer in elements of `parts` | `acme_checksum` in the elements for `summary.json` and `files.json`, and `acme_version` in the element for `acme_extra.json` |
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

The `zipfile` module of Python, which writes the kit, cannot write an encrypted entry, or ZIP64 records in a small archive, so `make_kit.py` crafts the three samples of `container.encrypted` and `container.zip64` byte by byte from the entries of `sample.pqax`, and reads each back with `zipfile` to check it:

| File | The structure that it adds |
|---|---|
| `container.encrypted.pqax` | `commits.json` is encrypted with the traditional PKWARE encryption of section 6.1 of the zip specification, with the password `pqax`: bit 0 of its flags is set, and its data is the 12-byte encryption header followed by the encrypted deflated data |
| `container.zip64.pqax` | A ZIP64 end of central directory record and its locator precede the end record, which keeps the real counts, size, and offset |
| `container.zip64.extraField.pqax` | The central directory gives `commits.json` a ZIP64 extra field, header 0x0001, with no data, since its sizes and offset fit in 32 bits |

None of the three needs ZIP64 or a password to open: the manifest, the summary, and the other parts are as in `sample.pqax`.

## Expected results

`expected/<sample>.json` holds what a reader must get from `samples/<sample>.pqax` or `samples/<sample>.vpax`. This is `expected/sample.json`:

```json
{
  "file": "sample.pqax",
  "embeddedPart": null,
  "embeddedCompression": null,
  "format": "pqax",
  "formatVersion": "0.2",
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

### Expected measures

`expected/measures/<sample>.json` holds the derived measures of section 10 of the [object model](../docs/object-model.md) for every table, every column, and the collection of a valid sample, as the view model of the storage views computes them: `buildModel(await readFootprint(doc))` of [`@pqax/views`](../src/typescript/views/README.md). It holds every measure of section 10 except ColumnNullShare and ColumnCompressionRatio, the two over a set of columns, which only the views offer. The C# library (`StorageMeasures`) and the Python package (`pqax.measures`) implement the same measures and must give the same values. In the files of the kit they are equal to the last bit: the three languages add and divide in the same order.

```json
{"file": "sample.pqax", "embeddedPart": null, "smallFileSize": 134217728, "tinyFileSize": 8388608, "filesRead": true, "warnings": [],
 "tables": [{"id": 1, "name": "sales", "label": "sales", "rowCount": 17000, "fileCount": 3, "fileSize": 159121,
             "stack": {"live": 155034.9, "nonLive": 737, "unreferenced": 0, "deleted": 239.1, "stale": 0, "metadata": 12469, "footers": 3847,
                       "deletedSource": "files", "listed": true, "data": 156011, "total": 172327, "waste": 976.1, "overhead": 16316,
                       "wasteShare": 0.006256610110825519, "overheadShare": 0.10458236919191595,
                       "shares": {"live": 0.9937433898891744, "...": "..."}},
             "smallFiles": {"threshold": 134217728, "count": 3, "shareAtLeast": 1}, "tinyFiles": {"...": "..."},
             "files": {"count": 3, "sum": 159121, "minimum": 28252, "firstQuartile": 28252, "median": 47820, "thirdQuartile": 83049,
                       "maximum": 83049, "vOrder": {"share": 0.3333333333333333, "bytesShare": 0.17755041760672696}},
             "measures": {"compressionRatio": 2.0073306897631222, "...": "..."},
             "maintenance": {"lastCommitAt": "2023-11-14T22:15:20.000Z", "commitsPerDay": 4, "daysSinceLastCommit": 1055,
                             "hasVacuum": false, "lastVacuumAt": null, "hasOptimize": true, "lastOptimizeAt": "2023-11-14T22:14:50.000Z"},
             "partitions": {"count": 2, "kept": 2, "oldestModifiedAt": "2023-11-14T22:15:00.000Z", "...": "..."}}],
 "columns": [{"id": 1, "table": 1, "name": "id", "label": "id", "nullShare": 0, "compressionRatio": 7.034014658821101,
              "dictionaryShare": 0, "dictionaryFallbackShare": null, "averageValueLength": null, "filesWithoutColumnShare": 0}],
 "collection": {"totals": {"sources": 2, "tables": 2, "columns": 10, "files": 5, "fileSize": 199005, "rowCount": 21100,
                           "footprint": 213583, "waste": 976.1, "overhead": 18942},
                "depths": {"Column chunks": 2}}}
```

| Key | Content |
|---|---|
| `file`, `embeddedPart` | The sample, and for a VPAX the part that holds the footprint |
| `smallFileSize`, `tinyFileSize` | The thresholds of the small files: the defaults of the views, 128 MiB and 8 MiB |
| `filesRead` | Whether the `files` table was read and has rows: then the deleted rows and the small files come from the files |
| `warnings` | What the views could not read |
| `tables` | One object per row of `tables`, in the order of the file. It has the keys of `pqax tables --json`: `id`, `name`, `label`, `rowCount`, `fileCount`, `fileSize`, `stack` with the seven components (`null` when absent) and the sums, `smallFiles` and `tinyFiles`, `measures` with the ratios of section 10.5, and `maintenance` with `lastCommitAt`, `commitsPerDay`, and `daysSinceLastCommit`. It has the measures that the command does not show too: `stack.shares`, the share of each component; `files`, the file size figures and `vOrder`, `null` or the V-Order shares `share` and `bytesShare`; `hasVacuum`, `lastVacuumAt`, `hasOptimize`, and `lastOptimizeAt` in `maintenance`; and `partitions`, with `count`, `kept`, `oldestModifiedAt`, and `newestModifiedAt`. |
| `columns` | One object per row of `columns`, in the order of the file: `id`, `table`, the id of its table or `null` when no table of the file has it, `name`, `label`, and the six measures of section 10.6, `nullShare`, `compressionRatio`, `dictionaryShare`, `dictionaryFallbackShare`, `averageValueLength`, and `filesWithoutColumnShare` |
| `collection` | The measures of section 10.7: `totals` with `sources`, `tables`, `columns`, `files`, `fileSize`, `rowCount`, `footprint`, `waste`, and `overhead`, and `depths`, the number of tables for each `detailDepth` |

The names are those of the view model, which section 10.8 of the object model maps. A missing value is `null`. A test compares the numbers exactly, with no tolerance, and a time, a key that ends in `At`, as a moment, since C# writes it with three decimals and the views and Python keep the text of the file.

### Expected diff

`expected/diff/diff-before.diff-after.json` holds what `pqax diff diff-before.pqax diff-after.pqax --json` writes, in C# and in Python, as section 13 of the [object model](../docs/object-model.md) proposes. `make_diff.py` writes it without an implementation of the diff: the pairs of tables and columns, the tables recreated, and the counts of the files, partitions, and commits come from `SCENARIO`, which the script checks against the data it writes; the totals come from the rows it writes; and the measures come from the storage views, as `expected/measures` does.

| Key | Content |
|---|---|
| `older`, `newer` | The header of each file: `file`, `embeddedPart`, `format`, `formatVersion`, `collector`, `library`, `collectedAt`, `level`, `tableCount`, `filesRead`, `warnings` |
| `smallFileSize`, `tinyFileSize`, `sort`, `top` | The thresholds of the measures, and the order and the cut of the command: `total` and `null` |
| `counts` | The tables `added`, `removed`, `kept`, and the kept ones `changed` |
| `collection` | The measures of section 10.7 and the tables at each depth, as `depths.Files`, each a compared value |
| `added`, `removed` | The tables, in the order of the command: `id`, `label`, `name`, `namespace`, `tableId`, `snapshotId`, `detailDepth`, `columnCount`, `fileCount`, `fileSize`, `rowCount`, `total` and `waste` of the stack, and `recreated` |
| `kept` | The kept tables, in the order of the command: `status`, `matchedBy`, `renamed`, `older` and `newer` with the keys of an added table, `totals` and `measures` of compared values, `columns` with `added`, `removed`, and `kept`, and `files`, `partitions`, and `commits` |

A compared value is `{"older": ..., "newer": ..., "delta": ...}`, the delta the newer minus the older, in milliseconds for a time, `null` for a boolean, a text, or a list, and when either value is `null`. The totals of a table are its fields of section 6.4, with the members of its distributions as `fileSizes.median`; its measures have the paths of `expected/measures`, such as `stack.waste`, with `files.vOrder.share` and `files.vOrder.bytesShare` for the V-Order shares. A kept column has the same keys, with the totals of section 6.5 and the six measures of section 10.6. A time is written with three decimals in `Z`. A test compares the numbers exactly, with no tolerance.

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
| `sample.pqax` | `tables.unreferencedFileCount` is `[1, 0]` and `tables.unreferencedFileSize` is `[737, 0]`. `tables.nonLiveOldestRemovedAt` and `tables.nonLiveNewestRemovedAt` are both `["2023-11-14T22:14:10.000Z", null]`. `partitions.lastModifiedAt` is `["2023-11-14T22:15:00.000Z", "2023-11-14T22:15:10.000Z"]`. |
| `format-0.1.pqax` | The same results as `sample.pqax`, apart from `formatVersion` `0.1`. `tables` has no `unreferencedFileCount`, `unreferencedFileSize`, `nonLiveOldestRemovedAt`, or `nonLiveNewestRemovedAt`, and `partitions` has no `lastModifiedAt`: a reader reads them as a missing field: `null` in C# and `None` in Python, and the field is `undefined` on the table in TypeScript. |
| `multipart.pqax` | `files.deletionVectorSize` is `[40, null, null, null, null]` and `files.partition` is `[1, 1, 2, null, null]` |
| `extensions.pqax` | The same results as `sample.pqax`, apart from `parts` and `rows`. `columnChunks` has no `encodings`. Where the API returns unknown fields, `tables.acme_score` is `[7, 9]`. Where it returns unknown members of the manifest, `acme_build` is there. |
| `later-depth.pqax` | `detailDepth` is `Pages` for both tables, as written |
| `embedded-stored.vpax`, `embedded-deflated.vpax` | The entry `Footprint.pqax` has the same bytes as `sample.pqax` |
| Every sample | Opening a file reads `manifest.json` and `summary.json`, and no detail part |

A reader refuses three invalid files when it opens them: `container.manifestMissing.pqax`, `manifest.format.pqax`, and `manifest.versionUnknown.pqax`. It opens `json.bom.pqax` and gets the results of `sample.pqax`. It opens `json.invalid.pqax` and fails only when it reads `commits`. It opens `container.zip64.pqax` and `container.zip64.extraField.pqax` and gets the results of `sample.pqax`, since neither needs ZIP64 to be read, and it opens `container.encrypted.pqax` and fails only when it reads `commits`, with the code `container.encrypted`.

### A validator test

1. Every valid sample gives no issue. For a VPAX, validate the footprint inside it, `Footprint.pqax`: the VPAX itself is not a PQAX file.
2. For every member of `samples/invalid/expected.json`, the validator reports every code in `codes` for that file. It may report more.

### A collector test

A collector meets section 4.6 of the [contracts](../docs/contracts.md) when its collection of `fixtures/lakehouse`, read from the folder with the default options, gives:

1. Every table and field of `samples/sample.pqax`, value for value, except `sources.location` and `tables.location`, which are the full paths of the item and table folders on the machine, and the collection of the manifest. With `--depth summary`, `files`, and `rowGroups`, the tables of `summary-only.pqax`, `files.pqax`, and `row-groups.pqax`.
2. The facts of the table of the reader test above, such as the unreferenced file of 737 bytes and the removal time of the non-live file.
3. The values of the C# collector, `pqax collect --local fixtures/lakehouse`, in every table and field, and in the collection except the members that name the collector and the times. With the defaults, the collection states 17 requests and 142,178 bytes read, and 12 requests and 13,841 bytes at level 1.
4. The footers of the probe dump, each found by the size of its file, when the collector has a footer parser of its own.

`tests/python/test_collect_fixture.py` and `test_collect_footer.py`, and the C# collector tests, check the first two and the last, and `cross_check.py` the third.

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

It reads `fixtures/probe-dump` and builds the tables with `build_pqax.py`, the builder of the [prototype](../research/prototype/README.md). It writes the zip archives with the `zipfile` module, and crafts the three samples of `container.encrypted` and `container.zip64` byte by byte, as the section on the invalid samples says. It builds `format-0.1.pqax` with the format version 0.1 of the builder, which leaves out the fields that 0.2 added. That import is the one exception to the rule that the kit depends on nothing outside `conformance/`, `src/`, and `tests/`. The Python package now has a writer, `pqax.Builder`, and the [backlog](../docs/backlog.md) has the item that makes the script use it.

The output is deterministic: every zip entry has the date 2026-10-07 00:00 and the same attributes, and the entries are written in a fixed order. Two runs with the same Python give the same bytes. Another zlib can compress differently: the samples in the repository were written by Python 3.14 on Windows, whose zlib is zlib-ng 1.3.1. The content and the expected results do not depend on it.

When the format changes, change the documents first, then `rules.md`, the schema, and `make_kit.py`, rebuild, and run `check_schema.py`, the checker of the prototype on every valid sample, and the suites of the three packages.

The expected measures come from the storage views, so they are rebuilt after the samples, and after a change of the view model of `@pqax/views`:

```bash
npm ci && npm run build                          # at the root: the reader and the views, in dist/
python conformance/make_measures.py              # rewrites expected/measures/ from the views
python conformance/make_measures.py --check      # writes nothing; exit code 1 when a file is not up to date
```

The expected diff comes from the views too, so it is rebuilt last:

```bash
python conformance/make_diff.py                  # rewrites the two samples of the comparison test and expected/diff/
python conformance/make_diff.py --check          # writes nothing; exit code 1 when a file is not up to date
```

`make_kit.py` writes the two samples of the comparison test and their `expected/<sample>.json` too, with `make_diff.py`, since it deletes the files of `samples/`; it does not write the expected diff, which needs `node`. `make_diff.py` writes the samples with `pqax.Builder` from `src/python`, and changes to the lakehouse go to its `SCENARIO` and its two lists of tables.

`make_measures.py` runs a small script through `node` on every sample, with the built packages; it runs `npm run build` itself when a `dist/` folder is older than its sources, unless `--no-build` is given. It needs only the standard library of Python. The script takes from the model of the views every measure that the files hold: for each table, the stack with the `share` of each component, the small files, `files` with `vOrder`, `measures`, `maintenance`, and `partitions`; for each column, its six measures, with its `name` from the `columns` table of the file and its `table` from the key of its table; and `collection.totals` and `collection.depths`. A view flag, such as the `source` of the file sizes, is left out. A change of the measures goes to the views first, then to `StorageMeasures` in C# and to `pqax.measures` in Python, until the three agree.

## The probe dump

`fixtures/probe-dump` is the output of the probe that preceded the collector, run on `fixtures/lakehouse`. It has three files, each one JSON object: `index.json` describes the run and lists the tables, and each table has a file named after its id, `L001.json` for the lakehouse table and `W001.json` for the warehouse table.

The dump has two roles:

- It is the input of `make_kit.py`. The script takes the collection of the manifest from `index.json`, and `build_pqax.py` builds every table of the samples from the table files.
- It is the oracle of the collector tests. They collect `fixtures/lakehouse` through `LocalStorage` and compare the result with the samples, every table and field at every depth, and with the dump itself. They also compare the footer parser, the checkpoint reader, and the listing of a Delta log with the dump. A test finds the dump of a data file by its size, so the data files of the fixture must keep sizes that differ.

Conventions of the probe:

- A field without a value is left out, except in the arrays of a row group, which have `null` for a column chunk without the value.
- A time from the Delta log is an integer of milliseconds since 1970-01-01 UTC, as the log writes it. The samples have it as a timestamp of the format.
- The dump holds no data value. A partition value becomes a number: each partition column numbers its values from 0 in the order in which they first appear, and a null is a value of its own. A path keeps its folders with the number in place of the value, as in `Year=#1/part-0003.parquet`. The collector does the same unless `IncludePartitionValues` is set.
- The probe kept no user name or user id of a commit, no value of an operation parameter, and no bound of the statistics. A footer key has its value only when the key is on the allow-list of the probe, as the V-Order keys are.

In the tables below, the column Maps to names the tables and fields of the samples that `make_kit.py` builds from a field, as the file names them. The column Collector says what the tests hold the collector to:

| Collector | Meaning |
|---|---|
| Footprint | The collector writes the same value into its footprint, or the value that the kit derives from it |
| Parser | The footer parser, the checkpoint reader, or the storage listing returns the same value. The footprint does not hold it. |
| Input | Input to the kit or to the tests only. The collector writes its own value, or none. |
| None | Recorded by the probe and read by nothing |

### index.json

| Field | Type | Meaning | Maps to | Collector |
|---|---|---|---|---|
| `collectedAtUtc` | string | When the probe finished, in UTC with seven decimals: `2026-10-05T20:55:12.7492636Z` | `collection.completedAt` of the manifest, cut to three decimals | Input |
| `maxFooters` | integer | The most footers that the probe read for one table | Nothing | None |
| `maxHistory` | integer | The most commits that the probe read for the history of one table | Nothing | None |
| `tailKb` | integer | The kilobytes that the probe read from the end of a file to get its footer | Nothing | None |
| `requests` | integer | The storage requests of the probe | `collection.requestCount` | Input |
| `bytesRead` | integer | The bytes that those requests read | `collection.bytesRead` | Input |
| `seconds` | number | How long the probe ran, in seconds | Nothing | None |
| `tables` | array of objects | One element for each table, in the order of the probe | Nothing: `make_kit.py` reads the table files | Input: the tests take the tables and their order from here |
| `tables[].id` | string | The id of the table in the dump, which names its file: `L001` | Nothing | Input: the tests open `<id>.json` |
| `tables[].item` | string | The OneLake item that holds the table | The same value as `item` in the table file | Footprint |
| `tables[].itemKind` | string | The kind of the item: `Lakehouse` or `Warehouse` | The same value as `itemKind` in the table file | Footprint |
| `tables[].namespace` | array of strings | The schema of the table, as `["dbo"]`, empty when the item has none | The same value as `namespace` in the table file | Footprint |
| `tables[].name` | string | The name of the table | The same value as `name` in the table file | Footprint |

The folder of a table in `fixtures/lakehouse` is `<item>.<itemKind>/Tables/<namespace>/<name>`, without the namespace folder when the namespace is empty. The tests find each table there. The other members of `collection` in the samples are constants of `make_kit.py`: `collectorApp`, `collectorAppVersion`, `level`, `includesDataValues`, and `includesPartitionValues`.

### L001.json and W001.json

A table file describes one table at its current version. The two files have the same fields, and `W001.json` leaves out those that its table has no value for, such as the checkpoint, the partitions, the deletion vectors, and the removal times.

`unreferencedFileCount`, `unreferencedFileSize`, `delta.removedOldestTimestamp`, and `delta.removedNewestTimestamp` were added by hand for format 0.2, because the probe no longer runs. The values are those of the fixture: in `sales`, `Year=2024/part-0004.parquet`, 737 bytes, is the one unreferenced file, and the tombstone of `part-0000.parquet` in the checkpoint has the removal time 1700000050000. `orders` has no unreferenced file and no tombstone.

In the table, `rowGroups[]` is short for `files[].footer.rowGroups[]`. In a row group, `numRows`, `totalByteSize`, and `totalCompressedSize` describe the row group. Every other field is an array with one element for each column chunk, in the order of the leaf columns of the schema of the file: element i is the chunk of `schemas[s].columns[i]`, where s is `footer.schema`. Each chunk is one row of `columnChunks` at the `Column chunks` depth.

| Field | Type | Meaning | Maps to | Collector |
|---|---|---|---|---|
| `id` | string | The id of the table in the dump, which names the file | The order of the tables: the kit sorts the files by `id`, and `tables.id` counts from 1 in that order | Input |
| `item` | string | The OneLake item that holds the table | `sources.name`, with one source for each item and kind | Footprint |
| `itemKind` | string | `Lakehouse` or `Warehouse` | `sources.kind`: `OneLake lakehouse` or `OneLake warehouse` | Footprint |
| `namespace` | array of strings | The schema of the table, empty when the item has none | `tables.namespace` | Footprint |
| `name` | string | The name of the table | `tables.name` | Footprint |
| `delta` | object | What the Delta log says about the current version | The `delta.` fields below | See below |
| `delta.version` | integer | The current version: that of the newest commit | `tables.snapshotId`, as text. The commit of that version gives `tables.snapshotTimestamp`. | Footprint |
| `delta.checkpointVersion` | integer | The version of the last checkpoint. Absent when the log has none. | `tables.delta.checkpointVersion`, and with `delta.version`, `tables.delta.commitsAfterCheckpoint` | Footprint |
| `delta.checkpointKind` | string | The kind of that checkpoint, as `classic`. Absent when the log has none. | Nothing | None |
| `delta.checkpointSize` | integer | The bytes of that checkpoint, 0 when the log has none | Nothing | None |
| `delta.logFileCount` | integer | The files in `_delta_log`: commits, checkpoints, and `_last_checkpoint` | `tables.metadataFileCount` | Footprint |
| `delta.logSize` | integer | Their bytes | `tables.metadataSize` | Footprint |
| `delta.commitFileCount` | integer | The commit files in `_delta_log` | `tables.commitCount` | Footprint |
| `delta.oldestCommitVersion` | integer | The version of the oldest of them | Nothing | None |
| `delta.tableId` | string | `metaData.id` | `tables.tableId` | Footprint |
| `delta.tableName` | string | `metaData.name`. Absent when the log has none. | Nothing | None |
| `delta.hasDescription` | boolean | Whether `metaData.description` is set. The probe kept no text. | Nothing | None |
| `delta.provider` | string | `metaData.format.provider`, as `parquet` | Nothing | Parser |
| `delta.schemaString` | string | `metaData.schemaString`: the schema as JSON text | `columns`, one row for each field of the schema: `name`, `dataType`, `isNullable`, and `isPartitionColumn` for a name in `partitionColumns`. The physical names map the leaves of `schemas` to columns. | Footprint |
| `delta.partitionColumns` | array of strings | `metaData.partitionColumns` | `columns.isPartitionColumn`. When not empty, one row of `partitionSpecs`, and one row of `partitionFields` for each column, with the transform `identity`. | Footprint |
| `delta.configuration` | map of strings | `metaData.configuration`: the table properties | `tables.properties` | Footprint |
| `delta.createdTime` | integer | `metaData.createdTime` | `tables.createdAt` | Footprint |
| `delta.minReaderVersion`, `delta.minWriterVersion` | integer | `protocol.minReaderVersion` and `protocol.minWriterVersion` | `tables.delta.minReaderVersion` and `tables.delta.minWriterVersion` | Footprint |
| `delta.readerFeatures`, `delta.writerFeatures` | array of strings | The table features of `protocol`, empty when it has none | `tables.delta.readerFeatures` and `tables.delta.writerFeatures` | Footprint |
| `delta.domains` | map of strings | The `domainMetadata` actions: for each domain, its configuration as JSON text | `tables.delta.domains`, the names sorted. The `clusteringColumns` of `delta.clustering` give `tables.clusteringColumns`. | Footprint |
| `delta.removedFileCount`, `delta.removedFileSize` | integer | The files that `remove` actions took out of the table and that the log still tracks, and the sum of their sizes | `tables.nonLiveFileCount` and `tables.nonLiveFileSize` | Footprint |
| `delta.removedOldestTimestamp` | integer | The earliest `remove.deletionTimestamp` of those files. Absent when none has one. | `tables.nonLiveOldestRemovedAt` | Footprint |
| `delta.removedNewestTimestamp` | integer | The latest `remove.deletionTimestamp` of those files. Absent when none has one. | `tables.nonLiveNewestRemovedAt` | Footprint |
| `delta.partitionValueCounts` | map of integers | The distinct values of each partition column | Nothing: the kit counts the partitions from `files[].partition` | None |
| `unreferencedFileCount` | integer | The `.parquet` files in the folder of the table, outside `_delta_log` and `_change_data`, that the current version does not reference | `tables.unreferencedFileCount` | Footprint |
| `unreferencedFileSize` | integer | The sum of their sizes in the storage listing | `tables.unreferencedFileSize` | Footprint |
| `files` | array of objects | One element for each data file of the current version: the `add` actions that the state keeps | One row of `files` each, at the `Files` depth and deeper. The totals of `tables` count every file at every depth. | Footprint |
| `files[].path` | string | `add.path`, with partition values replaced by numbers | `files.path` | Footprint |
| `files[].size` | integer | `add.size` | `files.fileSize`. Summed in `tables.fileSize`, `partitions.fileSize`, and `writers.fileSize`, and in the distribution `tables.fileSizes`. | Footprint |
| `files[].modificationTime` | integer | `add.modificationTime` | `files.modifiedAt`. The latest is `tables.lastModifiedAt`, and the latest of the files of a partition is `partitions.lastModifiedAt`. | Footprint |
| `files[].numRecords` | integer | `numRecords` in the statistics of `add`. Absent when the action has no statistics. | `files.rowCount`, or `footer.numRows` when absent. Summed in `tables.rowCount` and `partitions.rowCount`. | Footprint |
| `files[].statsColumnCount` | integer | The columns that those statistics cover. Absent with them. | Nothing | None |
| `files[].partition` | map of integers | For each partition column, the number of the value of the file. Absent when the table has no partition columns. | `files.partition`: one row of `partitions` for each distinct map, at the `Files` depth and deeper, with its totals. Also `tables.partitionCount` and `tables.partitionSizes`. | Footprint |
| `files[].deletionVectorStorage` | string | `deletionVector.storageType`, as `u` | Nothing | None |
| `files[].deletionVectorSize` | integer | `deletionVector.sizeInBytes` | `files.deletionVectorSize` | Footprint |
| `files[].deletedRows` | integer | `deletionVector.cardinality`: the rows marked as deleted | `files.deletedRowCount`, 0 when absent. Summed in `tables.deletedRowCount` and `partitions.deletedRowCount`. | Footprint |
| `files[].tags` | map of strings | `add.tags`. Absent when the action has none. The fixture has the keys `VORDER` and `INSERTION_TIME`. | `VORDER` set to `true` makes the file V-Ordered: `files.isVOrdered`, `tables.vOrderedFileCount`, and the writer of the file. `INSERTION_TIME` is not read. | Footprint, for `VORDER` |
| `files[].footer` | object | The footer of the file, as the probe decoded it. Absent when the probe did not read it. | The `footer.` and `rowGroups[]` fields below. Also `files.rowGroupCount`, `files.compressedSize`, and `files.uncompressedSize`, over the row groups and column chunks of the file. | Footprint |
| `files[].footer.footerSize` | integer | The length of the footer: the bytes of the `FileMetaData`, without the length and the magic that close the file | `files.footerSize`, summed in `tables.footerSize` | Footprint |
| `files[].footer.requests` | integer | The requests that the probe made to read the footer | Nothing | None |
| `files[].footer.version` | integer | `version` of the `FileMetaData` | Nothing | Parser |
| `files[].footer.createdBy` | string | `created_by`: the writer of the file | `writers.createdBy`, with one writer for each combination of `createdBy`, V-Order, and V-Order level, and `files.writer` | Footprint |
| `files[].footer.numRows` | integer | `num_rows` of the file | `files.rowCount` when `numRecords` is absent | Footprint |
| `files[].footer.keyValues` | array of objects | The `key_value_metadata` of the footer, in order | A key that ends with `vorder.enabled`, with the value `true`, makes the file V-Ordered. A key that ends with `vorder.level` gives `writers.vOrderLevel`. | Footprint |
| `files[].footer.keyValues[].key` | string | The key | See `keyValues` | Parser |
| `files[].footer.keyValues[].valueLength` | integer | The length of the value | Nothing | Parser |
| `files[].footer.keyValues[].value` | string | The value, for a key on the allow-list | See `keyValues` | Parser |
| `files[].footer.schema` | integer | The position of the schema of the file in `schemas`, from 0 | `files.physicalSchema`, and `physicalSchemas.fileCount` | Footprint |
| `files[].footer.rowGroups` | array of objects | The row groups of the file, in order | One row of `rowGroups` each, at the `Row groups` depth and deeper, with its position as `ordinal`. Counted in `files.rowGroupCount` and `tables.rowGroupCount`. | Footprint |
| `rowGroups[].numRows` | integer | `num_rows` of the row group | `rowGroups.rowCount`, and the distribution `tables.rowGroupRowCounts` | Footprint |
| `rowGroups[].totalByteSize` | integer | `total_byte_size` of the row group | `rowGroups.uncompressedSize` | Footprint |
| `rowGroups[].totalCompressedSize` | integer | `total_compressed_size` of the row group | `rowGroups.compressedSize`, or the sum of `compressed` when absent | Footprint |
| `rowGroups[].codec` | array of strings | `codec` of each chunk | `columnChunks.codec`, and `columns.codecs` | Footprint |
| `rowGroups[].encodings` | array of strings | `encodings` of each chunk, in one string separated by commas | `columnChunks.encodings`, split and sorted, and `columns.encodings` | Footprint |
| `rowGroups[].numValues` | array of integers | `num_values` of each chunk | `columnChunks.valueCount`, summed in `columns.valueCount` | Footprint |
| `rowGroups[].compressed` | array of integers | `total_compressed_size` of each chunk | `columnChunks.compressedSize`. Summed as `compressedSize` in `columns`, `physicalColumns`, `files`, and `tables`, and in `tables.staleColumnSize` for a leaf that maps to no column. | Footprint |
| `rowGroups[].uncompressed` | array of integers | `total_uncompressed_size` of each chunk | `columnChunks.uncompressedSize`, summed as `uncompressedSize` in the same tables | Footprint |
| `rowGroups[].dataPageOffset` | array of integers | `data_page_offset` of each chunk | With `dictionaryPageOffset`, `columnChunks.dictionaryPageSize` | Footprint |
| `rowGroups[].dictionaryPageOffset` | array of integer or null | `dictionary_page_offset` of each chunk, `null` when it has no dictionary page | `columnChunks.hasDictionaryPage`. `dataPageOffset` minus it is `columnChunks.dictionaryPageSize`. Also `columns.dictionaryChunkCount` and `columns.dictionaryPageSize`. | Footprint |
| `rowGroups[].nullCount` | array of integer or null | `null_count` of the statistics of each chunk | `columnChunks.nullCount`, summed in `columns.nullCount` | Footprint |
| `rowGroups[].distinctCount` | array of integer or null | `distinct_count` of the statistics of each chunk | `columnChunks.distinctCount`. The largest is `columns.largestChunkDistinctCount`. | Footprint |
| `rowGroups[].hasMinMax` | array of booleans | Whether the statistics of each chunk have bounds | Nothing | Parser |
| `rowGroups[].encodingStats` | array of string or null | `encoding_stats` of each chunk, as items `PAGE_TYPE:ENCODING:COUNT` separated by semicolons. `null` in the fixture. | `columnChunks.dataPageCount` and `columnChunks.dictionaryEncodedPageCount`, and `columns.fallbackChunkCount` | Footprint |
| `rowGroups[].bloomFilterLength` | array of integer or null | `bloom_filter_length` of each chunk | `columnChunks.bloomFilterSize` | Footprint |
| `rowGroups[].unencodedByteArrayBytes` | array of integer or null | `unencoded_byte_array_data_bytes` of the size statistics of each chunk. `null` in the fixture. | Nothing: `make_kit.py` does not read it. The collector writes it as `columnChunks.unencodedByteArrayDataBytes`. | Footprint |
| `rowGroups[].columnIndexLength` | array of integer or null | `column_index_length` of each chunk. `null` in the fixture. | Nothing | Parser |
| `rowGroups[].offsetIndexLength` | array of integer or null | `offset_index_length` of each chunk. `null` in the fixture. | Nothing | Parser |
| `schemas` | array of objects | The distinct Parquet schemas of the footers that the probe read | One row of `physicalSchemas` each | Footprint |
| `schemas[].columns` | array of objects | The leaf columns of the schema, in the order in which a row group stores them | One row of `physicalColumns` each, with its position as `ordinal` | Footprint |
| `schemas[].columns[].path` | array of strings | `path_in_schema` of the leaf | `physicalColumns.path`. Its first name, matched with the physical name or the name of a column, gives `physicalColumns.column`. | Footprint |
| `schemas[].columns[].type` | string | The physical type, as `INT64` | `physicalColumns.physicalType` | Footprint |
| `schemas[].columns[].repetition` | string | `repetition_type`, as `OPTIONAL` | `physicalColumns.repetition` | Footprint |
| `schemas[].columns[].converted` | string | `converted_type`, as `UTF8`. Absent when the leaf has none. | `physicalColumns.logicalType`, when the leaf has no logical type | Footprint |
| `commits` | array of objects | The most recent commits, up to `maxHistory`, in the order of their versions | One row of `commits` each, at the `Files` depth and deeper. Counted in `tables.commitsRead`. | Footprint |
| `commits[].version` | integer | The version of the commit, which names its file | `commits.commitId`, as text | Footprint |
| `commits[].timestamp` | integer | `commitInfo.timestamp` | `commits.timestamp` and `operations.lastAt`. The oldest is `tables.oldestCommitAt`. | Footprint |
| `commits[].operation` | string | `commitInfo.operation` | `commits.operation`, and one row of `operations` for each operation, with `commitCount` | Footprint |
| `commits[].engine` | string | `commitInfo.engineInfo` | `commits.engine` | Footprint |
| `commits[].isBlindAppend` | boolean | `commitInfo.isBlindAppend`. Absent when the commit has none. | `commits.isBlindAppend` | Footprint |
| `commits[].metrics` | map of strings | `commitInfo.operationMetrics`, empty when the commit has none | `commits.metrics` | Footprint |
| `commits[].parameterKeys` | array of strings | The names of `commitInfo.operationParameters`, without the values | Nothing, as the samples have no data values | Footprint, with data values only: the keys of `commits.parameters` |
| `issues` | array of strings | The problems that the probe met, each as `Code: message`. Empty in the fixture. | One row of `issues` each, with `code` and `message` | Footprint: the collector records no issue for the fixture |

The kit fills the other fields of the samples without a field of the dump: `tables.format` is `Delta`, `tables.detailDepth` is the depth of the sample, `tables.collectedLevel` is 2 when a footer was read and 1 otherwise, `tables.footerCount` counts the files with a footer, `tables.columnTotalsSource` is `Footers`, and `columns.fileCountWithoutColumn` counts, for a column that is not a partition column, the files whose schema has no leaf for it.

`build_pqax.py` also reads fields that the fixture does not have: `files[].clusteringProvider`, for `files.clusteringProvider`; `logical`, `typeLength`, and `fieldId` of a leaf in `schemas[].columns[]`, for `physicalColumns.logicalType`, which takes `logical` before `converted`, `physicalColumns.typeLength`, and `physicalColumns.fieldId`; and the keys `delta.columnMapping.id`, `delta.columnMapping.physicalName`, and `comment` in the metadata of a field of `schemaString`, for `columns.fieldId`, `columns.physicalName`, and `columns.description`.

## Cross-implementation check

`cross_check.py` checks that the packages agree with each other. Each writer rewrites four samples, and every validator and every reader checks every file that the writers produce, and the samples themselves as a reference.

```bash
python conformance/cross_check.py                  # prints one row for each file and one column for each validator and reader
python conformance/cross_check.py --keep out       # keeps the files it writes in out/
python conformance/cross_check.py --skip dotnet    # leaves out an implementation: python, dotnet, node
```

| Role | Python | C# | TypeScript |
|---|---|---|---|
| Writer | `Builder.from_document` | `FootprintBuilder.From`, through a runner that the .NET 10 SDK runs as a file-based app | None |
| Collector | `python -m pqax collect --local fixtures/lakehouse`, when pyarrow is installed: `pqax[collect]`; and `--adls` on the container of `container_check.py` | `pqax collect --local fixtures/lakehouse`, when the command line builds; and `--adls` on the container, through a runner that the .NET 10 SDK runs as a file-based app | |
| Validator | `python -m pqax check` | `pqax check`, or `PqaxValidator` when the command line does not build | `validate`, through `node` |
| Reader | `pqax.open` | `PqaxDocument.Open` | `openPqax`, through `node` |
| Measures | `pqax.measures.measure` | `StorageMeasures.Compute`, through the runner | `buildModel` of `@pqax/views`, through `node` and `make_measures.py` |

The files are `sample.pqax`, `sample.pqax` with at most 20 rows in a detail part, `extensions.pqax`, and `multipart.pqax`, as each writer writes them, the fixture as each collector writes it, which must give the results of `expected/sample.json`, and the samples `sample.pqax`, `multipart.pqax`, `extensions.pqax`, and `format-0.1.pqax` of the kit. Every validator must accept every file. Every reader must get the `rows`, the length of every table read whole, and `tablesFileSize`, `tablesRowCount`, `columnChunksCompressedSize`, `tables`, and `largestColumns` of the expected file of the sample. A written file has format version 0.2. A rewrite with the default rows per part keeps the parts of its sample, so every reader must find the descriptors of the parts of the sample as they are, in their order: the names, the rows of each table, and the members of a producer, such as `acme_checksum` and `acme_version` in `extensions.pqax`. The file with 20 rows in a part has the part names of its writer for `columnChunks`, and only `summary.json` first is checked there.

Every validator must also report at least the codes of `samples/invalid/expected.json` for every invalid sample, and every reader must read the three samples of the container rules as the reader test says: `container.zip64.pqax` and `container.zip64.extraField.pqax` with the results of `expected/sample.json`, and `container.encrypted.pqax` until `commits`, whose read fails with the code `container.encrypted`. The measures of every file, by the three implementations, must equal `expected/measures/` of its sample value for value, for the tables, the columns, and the collection; for the collected fixture, the days since the last commit are left out, since they count to the time of the collection.

When both collectors run, their two files of the fixture are read whole with the Python reader and compared in every table and every field, a missing field counting as nulls, and in the collection of the manifest without `collectorApp`, `collectorAppVersion`, `collectorLib`, `collectorLibVersion`, `startedAt`, and `completedAt`: the requests and the bytes read must be equal too. Without pyarrow, the Python collector is skipped with a message, and with one collector the comparison is skipped.

When both collectors run, `container_check.py` also builds a synthetic container of Azure Data Lake Storage Gen2 in the work folder, from the lakehouse table of the fixture, synthetic Delta logs, and synthetic footers: Delta tables at depths 1, 2, 4, and 5, a folder of Parquet files as Spark leaves it, with `_SUCCESS`, `_temporary`, and hidden files, a folder of Parquet files with a subfolder, an extension in upper case, a footer with every physical type and annotation, lists, maps, structures, and a variant, a file that is no Parquet file, an empty folder, a folder of other files, a partitioned folder, folders whose names start with `_` and `.`, and a folder with a space in its name. A fake DFS endpoint on 127.0.0.1 serves it as the filesystem `lake` of the account `pqaxtest`, with the listings in pages of three entries, and answers 403 and 401 for two other filesystems. Both command lines collect it with `--adls` and the URLs of the account, `https://pqaxtest.dfs.core.windows.net/lake/...` and `abfss://lake@pqaxtest.dfs.core.windows.net/...`, and an access token from a variable; their HTTPS requests go to the fake endpoint, through the HTTP client of `CollectHost` in C# and through `http.client.HTTPSConnection`, replaced in the process, in Python. Each of five collections, with other folders and options, must give the same footprint in the two languages, compared as the collections of the fixture, and the same lines apart from the order of the lines of the tables done; and seventeen command lines that fail after the sign-in or are refused before it must give the same exit code and the same first line of the error output.

`python -m pqax diff` and `pqax diff` in C#, when the command line builds, compare `diff-before.pqax` with `diff-after.pqax`. The JSON of each must equal `expected/diff/diff-before.diff-after.json` value for value, and the text and the JSON of the two, with the default options and with `--sort` and `--top`, must be the same text, apart from the line ends. The TypeScript package has no diff.

The script needs only the standard library. It skips, with a message, what is not there: `dotnet` or the .NET 10 SDK, `node`, a build of the TypeScript package, which it runs with `npm run build` when `dist/` is older than the sources unless `--no-build` is given. The exit code is 0 when every check passes, 1 when one fails, and 2 when nothing can run.

## Limits

The fixture is small, and much of the format is not in it:

- Only Delta tables, written by DuckDB. No Iceberg table, so no `iceberg` details, `specId`, `orderId`, sort orders, or delete files.
- Only top-level columns of primitive types: one physical column for each column. No nested column, no column mapping, no field ids, and no schema change: one physical schema for each table, and `staleColumnSize` 0.
- No issues, no model bindings, and no obfuscation fields.
- No data values: no `minValue`, `maxValue`, partition values, or commit parameters, and `collection` says so.
- Every string is ASCII, so no sample tests the decoding of other UTF-8 text.
- No valid sample has an encrypted entry or ZIP64 records: only the three invalid samples of the container rules, crafted byte by byte, do.
- The largest detail table has 52 rows. Only `part.tooLarge.pqax` has a large part: 1.5 MB of JSON text, which takes 1.5 KB in the zip.
- The expected results check counts, sums, and the joins between tables, not the value of every field.
