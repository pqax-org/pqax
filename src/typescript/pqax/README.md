# pqax

Reads and validates PQAX files, in a browser and in Node. A PQAX file holds the footprint of a set of Parquet tables: a zip archive of JSON tables, as the [serialization](https://github.com/pqax-org/pqax/blob/main/docs/serialization.md) defines it. The package has no dependency.

Version 0.2.2. Reads 0.1 and 0.2, and any format version 0.x, whatever the second number. The five fields that 0.2 adds, four in `tables` and `lastModifiedAt` in `partitions`, are `undefined` on a 0.1 file. On npm it is `@pqax/pqax`: the registry refuses the unscoped name `pqax` as too similar to existing packages.

## What it does

| Function | What it does |
|---|---|
| `openPqax(input)` | Opens a PQAX file. It reads the zip directory, `manifest.json`, and `summary.json`, and nothing else. |
| `openEmbedded(input, partName?)` | Opens the PQAX file that a VPAX package holds as a part, by default `Footprint.pqax`. A stored part is read in place, and a deflated part is inflated in memory first. |
| `tryOpenEmbedded(input, partName?)` | The same as `openEmbedded`, or `null` when the package has no such part, as most VPAX files have none yet |
| `embeddedSource(input, partName?)` | The bytes of that part as a `ByteSource`, to validate it: `validate(await embeddedSource(vpax))` |
| `doc.manifest` | The manifest, with the members that this version does not know, in the manifest and in each element of `parts` |
| `doc.summary` | The 15 summary tables, read when the file opens |
| `doc.readTable(name)` | Reads a detail table: every part of it, joined, and cached |
| `doc.readRaw(name)` | Reads a table as the file holds it, with the fields and members that this version does not know |
| `doc.rowCount(name)` | The rows of a table, from the manifest |
| `doc.embeddedPart`, `doc.embeddedCompression` | For a file opened with `openEmbedded`, the part and `stored` or `deflated`. `null` otherwise. |
| `rows(table)` | Reads a table one row at a time |
| `indexById(table)` | Maps the `id` of each row to its position, to follow references |
| `validate(input)` | Checks a file against every rule, and returns every issue with its code |
| `fromFile(path)` | In `@pqax/pqax/node`: a file on disk that the reader reads by range, never whole |

An input is a `Blob` (a `File` is one), an `ArrayBuffer`, a `Uint8Array` or another `ArrayBufferView`, or a `ByteSource`: an object with a `size` and a `read(offset, length)` method that returns a promise of exactly those bytes. A `ByteSource` lets the reader work on a large file, or on a server that answers range requests, without loading it.

## Requirements

| Platform | Version |
|---|---|
| Node | 20.12 or later |
| Browsers | Chrome and Edge 103, Firefox 113, Safari 16.4, or later |

The reader inflates parts with `DecompressionStream("deflate-raw")`, which these versions have. The main entry imports nothing from Node. The Node helper is in the second entry, `@pqax/pqax/node`.

## Browsers

The tests run in Node and in the three browser engines, through Playwright and the browser mode of vitest. The browsers run every test file but `node.test.ts`, which tests `@pqax/pqax/node`.

| Engine | Build tested | Browsers on the engine |
|---|---|---|
| Chromium | Chrome Headless Shell 156.0.8078.4 | Chrome, Edge |
| Gecko | Firefox 157.0 | Firefox |
| WebKit | WebKit 27.2 | Safari |

These are the builds of Playwright 1.64.1, run on Windows 11 in October 2026. The WebKit build has the engine of Safari, JavaScriptCore included, but it is not Safari. No test ran in Safari or in Edge.

`readTable` on one detail part of 250,000 column chunks, the most that a part may hold, takes these times. The part has 14 fields of integers, strings, lists of strings, booleans, and nulls: 22.2 MB of JSON, 2.6 MB deflated. A time is the median of nine reads, from three runs of `tests/typescript/timing.test.ts`, on an Intel Core Ultra X7 358H. Every read is within 30% of its median.

| Platform | Stored | Deflated |
|---|---|---|
| Node 24.19 | 79 ms | 141 ms |
| Chromium 156 | 77 ms | 90 ms |
| Firefox 157 | 114 ms | 145 ms |
| WebKit 27.2 | 50 ms | 62 ms |

A time covers the read of the entry, the inflate of a deflated part, the decoding, `JSON.parse`, and the typing of the values. The test prints the times, and fails only when a read takes 10 seconds or more.

## Use

In a browser, with a file that the user picks:

```js
import { openPqax, rows } from "@pqax/pqax";

const doc = await openPqax(input.files[0]);
for (const table of rows(doc.summary.tables)) {
  console.log(table.id, table.detailDepth, table.fileCount, table.compressedSize);
}
```

In Node:

```js
import { embeddedSource, indexById, openPqax, tryOpenEmbedded, validate } from "@pqax/pqax";
import { fromFile } from "@pqax/pqax/node";

const doc = await openPqax(await fromFile("sample.pqax"));
const chunks = await doc.readTable("columnChunks");
const physical = indexById(doc.summary.physicalColumns);
let size = 0;
for (let i = 0; i < chunks.length; i++) {
  size += chunks.compressedSize[i] ?? 0;
  const leaf = physical.get(chunks.physicalColumn[i]);   // a position in doc.summary.physicalColumns
}

const footprint = await tryOpenEmbedded(await fromFile("model.vpax"));   // null when the VPAX file has no footprint

const result = await validate(await fromFile("sample.pqax"));
for (const issue of result.issues) console.log(issue.code, issue.message);
```

## Tables

A table is columnar, as in the file: one array for each field, with one value for each row, and `length`, the number of rows.

```js
doc.summary.tables
// { length: 2, id: [1, 2], name: [...], fileCount: [3, 2], partitionCount: [2, null], ... }
```

| Case | What the reader gives |
|---|---|
| A missing value | `null` |
| A field that the file leaves out | The field is `undefined` |
| A summary table that the file does not have | `doc.summary.<name>` is `undefined` |
| A table that the file does not have | `readTable` returns `{ length: 0 }`, with no field, and `readRaw` returns `{}` |
| A table in several parts | One table: the arrays of the parts joined in the order of the manifest. A field that a part leaves out gets one `null` for each row of that part. |
| A value whose JSON type is not the type of its field | `null` in `summary` and `readTable`, and as it is in `readRaw`. The same for a member of a `Distribution`, `DeltaDetails`, or `IcebergDetails`. |
| A field that this version does not know | Left out of `summary` and `readTable`, kept by `readRaw` |
| A member of a table that is not an array | Left out of `summary` and `readTable`, kept as it is by `readRaw` |
| A table that this version does not know | `readTable` and `readRaw` return every array it has |
| A member of the manifest, or of a nested object, that this version does not know | Kept on the object |
| A reference | The `id` of a row of the table it points to, as the file has it. `indexById` finds the row, and finds nothing for a reference to a missing row. |

The rows of a part are the length of its longest array, which in a valid file is what the manifest states. A shorter array, which only an invalid file has, is padded with `null`. The count of the manifest is used only for a part whose table has no array, and only up to 250,000 rows, so that a manifest cannot make the reader allocate rows that the part does not hold.

The types describe every table and field: `SourcesTable`, `TablesTable`, `FilesTable`, and the others, with `SourceRow`, `FileRow`, and the others for the rows that `rows` returns. Every field is optional in the types, because a table that the file does not have has no field. The doc comments say which fields the object model requires.

The tables are cached and shared between calls. Do not change them.

## Errors

The reader is tolerant. It skips a byte order mark, ignores what it does not know, and reads `null` for a value of the wrong type. `openPqax` refuses a file only in these cases, with a `PqaxError` whose `code` is the code of the rule:

| Code | When |
|---|---|
| `container.manifestMissing` | The file is not a zip archive, or has no `manifest.json` |
| `manifest.format` | `format` is not `pqax` |
| `manifest.versionUnknown` | `formatVersion` does not start with the number 0 |
| `json.invalid` | `manifest.json` or `summary.json` is not valid JSON |

It also throws a `PqaxError` when an entry that it must read cannot be read: `container.method`, `container.encrypted`, or `container.zip64` for the entry, and `json.invalid` for damaged bytes. A detail part is read only by `readTable` and `readRaw`, so a broken detail part fails only the read of its table, with `json.invalid`, or with `manifest.partMissing` when the archive does not have it. `openEmbedded` throws `embedded.partMissing` when the package has no such part, where `tryOpenEmbedded` returns `null`.

## Validation

`validate` reports the codes of section 4.1 of the [contracts](https://github.com/pqax-org/pqax/blob/main/docs/contracts.md), with the exact conditions of the [rules of the conformance kit](https://github.com/pqax-org/pqax/blob/main/conformance/rules.md). The result has `isValid`, `issues`, and `checks`, the number of checks that ran. An issue has a `code`, a `message`, and, when they apply, the `part`, the `table`, and the `field`. Messages hold names of the format, counts, rows, and ids, and no value from the data.

It reports every issue that it finds, and skips the rules that an earlier failure leaves without the data that they need:

| After | Skipped |
|---|---|
| A file that is not a zip archive, or a manifest that is missing or does not parse | Every rule but the container rules |
| `manifest.format` or `manifest.versionUnknown` | Every rule on the parts, as a reader refuses the file |
| No `parts` array | Every rule on the parts |
| A part descriptor without `tables` | `part.tables` for that part |
| A part that is missing, cannot be read, or does not parse | The rules on that part, and the reference, depth, and totals rules that need a table that it holds |
| Arrays of a table that disagree in length | Depth and totals that count that table |
| A required field without values, a dangling reference, or ids that are not distinct positive integers | Depth and totals that follow that field or those ids |

Notes on what it checks:

- `json.integer` and `json.integerRange` apply to the integers of the format: the integer fields, the ids, the references, `position`, the integer members of the nested objects, and in the manifest `collection.level`, `collection.requestCount`, `collection.bytesRead`, and the row counts of the parts. A number written as `1.0` or `1e3` there breaks `json.integer`. Fields that this version does not know are not checked.
- `json.type` applies to every field and nested member that the object model types, and to the members of the manifest. A list element that is `null` breaks it, except in `partitions.values`.
- `json.timestamp` checks the form `2026-10-05T08:15:30.000Z`, not the calendar.
- `container.zip64` covers a ZIP64 locator at the end of the archive, and ZIP64 values or extra fields in the central directory.
- `container.partName` applies to the names that the manifest lists. `container.caseCollision` applies to every entry, with ASCII letters compared without case.
- A part that is not valid UTF-8, or has a string with a lone surrogate, breaks `json.invalid`.
- A total that is `null` is not compared. `totals.columnCompressedSize` applies to the columns of a table at the `Column chunks` depth, or deeper, that has column chunks.

## Limits

- Stored and deflated entries only, and no ZIP64, as the format requires. A file that needs ZIP64 to be read is refused.
- The reader holds a whole part in memory while it parses it. The format bounds a detail part to 250,000 rows, about 20 MB of JSON text. The reader does not compare the sizes in the zip directory with a limit of its own before it reads a part.
- The reader does not replace a reference to a missing row with `null` in the arrays: checking a reference to a detail table would mean reading that table. `indexById` finds nothing for such a reference.
- Entry names are read as UTF-8.
- `validate` holds every part in memory, and scans the text of each part once more after `JSON.parse`, to find what `JSON.parse` loses: repeated member names and numbers written with a fraction or an exponent. On a file with 1,000,000 column chunks in four parts, `readTable` takes about 0.4 s and `validate` about 1.1 s, in Node 24.
- Tested in Node 24 and in the Playwright builds of Chromium, Firefox, and WebKit, on Windows. Not tested in Safari or Edge themselves, nor on a phone.

## Build and test

```bash
npm install
npm run build        # tsc, into dist/
npm run typecheck
npm test             # vitest in Node, on the tests in tests/typescript at the root of the repository
npx playwright install chromium firefox webkit   # once, before the next line: about 1.2 GB
npm run test:browsers   # the same tests in the three browsers, headless, but node.test.ts
npm run smoke        # after a build: the conformance kit, through the built package
node scripts/smoke.mjs file.pqax ...   # after a build: opens, reads, and validates the files
```

The tests import the package as `pqax` and `@pqax/pqax/node`. The vitest configuration maps both names to the TypeScript sources, so the tests need no build. It also turns the files of the conformance kit into a module, `virtual:pqax-kit`, so that the tests read the samples in the same way in Node and in a browser. `vitest.browser.config.ts` runs the tests in the browsers.

`npm publish` runs the build and `npm test` first. It publishes with provenance, which npm creates only in a CI service such as GitHub Actions.
