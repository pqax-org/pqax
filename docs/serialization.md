# PQAX serialization

Draft 0.2, 6 October 2026.

This document defines how a PQAX file is written. The [object model](object-model.md) defines the content: the objects and their fields. This document defines the container, the JSON, how the objects are laid out, and what a reader and a writer do.

Everything here is **Proposed** unless it is marked **Decided**. The decisions it builds on are in [decisions.md](decisions.md).

Draft 0.2 records five decisions that the maintainers made on 6 October 2026: names are in lower camel case, the part embedded in a VPAX is named `Footprint.pqax`, the first version has no views, values that repeat are written in full with no dictionary, and a detail part holds at most 250,000 rows.

The [prototype](../research/prototype/README.md) writes, reads, and checks files in this format. It includes a [sample file](../research/prototype/sample.pqax), and section 13 has its measurements.

A sentence in the present tense is a rule. A sentence with "should" is a recommendation.

## 1. Requirements

| The serialization must | Because |
|---|---|
| Need a zip library and a JSON parser, and nothing else | The format is independent. **Decided.** |
| Be valid at any depth, so that a reader never needs something that the writer left out | Any depth is valid. **Decided.** |
| Accept new objects and fields without breaking the readers and the files that exist | Room to grow. **Decided.** |
| Stay small enough to attach to a message | A diagnostic, not a duplicate. **Decided.** |
| Be the same bytes as a file of its own and as a part of a VPAX | A VPAX can embed a PQAX. **Decided.** |
| Be readable in a browser | The web reader is in scope. **Decided.** |
| Show every table and its totals before any detail is read | Opening a file must be fast |
| Let an obfuscator change values without changing the structure | VPAX files are obfuscated today |

## 2. The format in brief

A PQAX file is a zip archive of JSON documents.

```
sample.pqax
├── manifest.json        what the file is, how it was collected, and which parts it has
├── summary.json         every table and column, with totals: all that a reader shows first
├── partitions.json      optional detail, one row for each partition
├── files.json           optional detail, one row for each data file
├── rowGroups.json       optional detail, one row for each row group
├── columnChunks.json    optional detail, one row for each column chunk
└── commits.json         optional detail, one row for each commit
```

Each kind of object of the model is a table. A table is a JSON object with one array for each field, and every array has one element for each row. This is the complete `rowGroups.json` of the sample file:

```json
{
  "rowGroups": {
    "id":               [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13],
    "file":             [1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 4, 4, 5],
    "ordinal":          [0, 1, 2, 0, 1, 2, 3, 4, 0, 1, 0, 1, 0],
    "rowCount":         [2048, 2048, 904, 2048, 2048, 2048, 2048, 808, 2048, 952, 2048, 1952, 100],
    "uncompressedSize": [36772, 36772, 17048, 36772, 36772, 36772, 36772, 15512, 36772, 17816, 36772, 35236, 2880],
    "compressedSize":   [19562, 18170, 8364, 18173, 18178, 18188, 18179, 7551, 18180, 8783, 19550, 17354, 1273]
  }
}
```

Rows refer to each other by number. The first three row groups belong to the row of `files` whose `id` is 1.

Three choices shape the format:

- **Tables, not a tree.** VPAX writes its model as nested objects with `$id` and `$ref`, and adds flat views for the tools that cannot follow them. PQAX writes flat tables only, so the full model and the views are the same thing.
- **One array for each field.** It is the smallest of the layouts measured, and it loads directly into SQL, a data frame, or typed arrays.
- **One part for each depth.** A reader opens the manifest and the summary, and reads a detail part only when someone looks at it.

## 3. Container

1. A PQAX file is a zip archive with the extension `.pqax`. **Decided.**
2. An entry is either stored or compressed with deflate. The archive is not encrypted, not signed, and not split in volumes. These are the restrictions of ISO/IEC 21320-1, the zip profile for document containers.
3. The archive does not use ZIP64. A PQAX file is therefore smaller than 4 GB and has fewer than 65,535 entries.
4. The name of a part uses ASCII letters, digits, `.`, `-`, and `_`. No two entries have names that differ only in case.
5. The entry `manifest.json` is always present. The order of the entries has no meaning.
6. A reader takes the position and the size of an entry from the central directory at the end of the archive.
7. A reader ignores the entries that the manifest does not list.

The container is a plain zip and not an Open Packaging Conventions package, as VPAX is. Nothing in PQAX needs content types or relationships, and every platform reads and writes a plain zip with what it already has.

## 4. JSON

1. A part is a JSON text in UTF-8 whose top level is an object. The rules of this section apply to the manifest as well.
2. A writer writes no byte order mark. A reader skips one if it finds it.
3. A part follows the I-JSON profile, RFC 7493: member names are not repeated in an object, strings are valid Unicode, and numbers fit a double.
4. A writer should write no whitespace between tokens.

| Type in the object model | JSON value |
|---|---|
| string | A string |
| integer | A number with no fraction and no exponent, between -(2^53 - 1) and 2^53 - 1 |
| boolean | `true` or `false` |
| timestamp | A string such as `2026-10-05T08:15:30.000Z`: UTC, with exactly three decimals |
| list | An array |
| map of string to string | An object whose values are strings |
| `Distribution`, `DeltaDetails`, `IcebergDetails` | An object with the fields of that object |
| reference | The `id` of the row it points to. See section 6. |
| No value | `null`, or the field is left out |

- An identifier that can exceed 2^53 is a string. The object model already gives that type to snapshot and commit identifiers.
- Timestamps have one form, so that they sort as text. A time known to the second ends in `.000Z`. The form follows the recommendation of RFC 7493, and it is what `toISOString` returns in JavaScript.
- "Unknown is not zero" becomes a difference that JSON can express: `null` is unknown, and `0`, `false`, `""`, `[]`, and `{}` are known values.

## 5. Tables

### 5.1 Layout

1. A table is an object. Each member is a field: its name, and an array with one value for each row.
2. In one part, all the arrays of a table have the same length.
3. Row *n* of the table is element *n* of each array.
4. A writer leaves out a field that has no value in any row of the part. A reader treats a missing field as `null` in every row. Every table has at least one required field, so a table with rows always has at least one array, and a reader may take the number of rows of a part from its longest array.
5. A member whose value is not an array is not a field of this version. A reader ignores it, as it ignores a field that it does not know. This keeps one door open: a later version can give a field another form, and a reader of this version loses that field and nothing else.
6. The order of the fields has no meaning, and neither has the order of the rows. A writer should keep the rows of the same parent together, because they compress better.
7. A value is written in full in every row. A value that repeats is not replaced by an index into a list of values. **Decided.** Section 14 has the reasons.

### 5.2 Names

The names of tables and fields are in lower camel case. **Decided.**

- A table has the name of its object in the plural: `physicalColumns`.
- A field has its name in the object model with a lower-case first letter: `FileCount` is `fileCount`, and `IsVOrdered` is `isVOrdered`.
- A value that the model defines is written as the model spells it: `Delta`, `Row groups`, `Footers`.
- No name that the format defines has an underscore. Section 9 gives those names to producers.

### 5.3 From objects to tables

1. Each object that the model keeps in a list is a table. The children of an object are not nested inside it. They are rows of their own table, and each has a reference to its parent, named after the parent: `table`, `file`, `rowGroup`.
2. The children that the model keeps in order, which are the fields of a partition spec and of a sort order, also have a `position`, from 0.
3. An object that the model keeps in a single field is a JSON object in that field: a `Distribution`, the `delta` details, the `iceberg` details. These objects hold no name and no reference.
4. The `Footprint` and its `Collection` are not tables. They are in the manifest.

| Table | One row for each | `id` | References |
|---|---|---|---|
| `sources` | Source | Yes | |
| `tables` | Table | Yes | `source`, `clusteringColumns` to `columns` |
| `columns` | Column of a table | Yes | `table` |
| `partitionSpecs` | Partition spec of a table | Yes | `table` |
| `partitionFields` | Field of a partition spec | | `partitionSpec`, `sourceColumns` to `columns` |
| `sortOrders` | Sort order of a table | Yes | `table` |
| `sortFields` | Field of a sort order | | `sortOrder`, `sourceColumn` to `columns` |
| `physicalSchemas` | Distinct Parquet schema of a table | Yes | `table` |
| `physicalColumns` | Leaf column of a physical schema | Yes | `physicalSchema`, `column` |
| `writers` | Writer found in a table | Yes | `table` |
| `operations` | Kind of operation of a table | | `table` |
| `issues` | Problem met | | `table`, absent when the issue concerns no single table |
| `modelBindings` | Semantic model | Yes | |
| `tableBindings` | Table of a semantic model | Yes | `modelBinding`, `table` |
| `columnBindings` | Column of a semantic model | | `tableBinding`, `column` |
| `partitions` | Partition that holds data | Yes | `table`, `partitionSpec` |
| `files` | Data file | Yes | `table`, `partition`, `sortOrder`, `physicalSchema`, `writer` |
| `rowGroups` | Row group | Yes | `file` |
| `columnChunks` | Column chunk | | `rowGroup`, `physicalColumn` |
| `deleteFiles` | Delete file | Yes | `table`, `referencedFile` to `files`, `equalityColumns` to `columns`, `partition` |
| `commits` | Commit that was read | | `table` |

The first fifteen are the summary tables, and the last six are the detail tables. Section 7 says where each goes.

A table has the fields of its object in the [object model](object-model.md), which also says which ones are required. It also has the reference to its parent, and the `position` of rule 2. Both are required, except the reference in `issues`.

## 6. Identifiers and references

1. A table that another table refers to has an `id` field. An `id` is a positive integer, and it is unique in its table across the whole file.
2. A writer should number the rows of a table from 1, in their order. A reader does not rely on it.
3. A reference is the `id` of a row of the table it points to. A list of references is an array of them.
4. A reference always points to a row that is in the file. A writer that leaves out a row also leaves out the references to it.
5. A reader that follows a reference to a missing row finds nothing, as if the reference were `null`. It does not have to check references when it reads, because the target may be in a part it has not read.

Identifiers are explicit, and not positions in the arrays, so that a tool can filter or sort a table without breaking the references to it.

## 7. Manifest and parts

### 7.1 Manifest

`manifest.json` says what the file is and lists its parts. This is the manifest of the sample file:

```json
{
  "format": "pqax",
  "formatVersion": "0.1",
  "collection": {
    "collectorApp": "pqax-prototype",
    "collectorAppVersion": "0.1",
    "level": 2,
    "includesDataValues": false,
    "includesPartitionValues": false
  },
  "parts": [
    { "name": "summary.json", "tables": { "sources": 2, "tables": 2, "columns": 10, "partitionSpecs": 1, "partitionFields": 1,
                                          "physicalSchemas": 2, "physicalColumns": 8, "writers": 3, "operations": 5 } },
    { "name": "partitions.json", "tables": { "partitions": 2 } },
    { "name": "files.json", "tables": { "files": 5 } },
    { "name": "rowGroups.json", "tables": { "rowGroups": 13 } },
    { "name": "columnChunks.json", "tables": { "columnChunks": 52 } },
    { "name": "commits.json", "tables": { "commits": 6 } }
  ]
}
```

| Member | Content |
|---|---|
| `format` | Always `pqax` |
| `formatVersion` | The version of the format. See section 9. |
| `collection` | The `Collection` object of the model |
| `obfuscatorDictionaryId`, `obfuscatorLib`, `obfuscatorLibVersion` | The obfuscation fields of the `Footprint`, present when names were obfuscated |
| `parts` | The parts of the file. Each has the `name` of its entry and its `tables`: the number of rows that the part holds for each table. |

Required: `format`, `formatVersion`, `collection`, `parts`.

### 7.2 Parts

1. A part is an object with one member for each table that it holds. Every member of a part is a table, and the manifest lists it.
2. The first part of the list is `summary.json`. It holds every row of the summary tables, and no other part holds any.
3. Every other part holds rows of one detail table.
4. A detail table with many rows continues in more parts. Its rows are the rows of its parts, in the order of the manifest. A detail part holds at most 250,000 rows. **Decided.**
5. A writer should name a detail part after its table, as in `files.json`, and number the parts of a table that has several, as in `columnChunks.1.json`. A reader finds the parts in the manifest and gives no meaning to their names.
6. A table with no rows is left out, and a detail part with no table is left out. `summary.json` is always present, as an empty object when the footprint has no summary rows.
7. The number of rows that the manifest states for a table in a part equals the length of its arrays in that part.

Rule 4 bounds what a reader does in one step. A part with 250,000 column chunks is about 20 MB of JSON text, and JavaScript parses it in about 70 ms.

### 7.3 Opening a file

1. The reader reads the central directory of the zip.
2. It reads `manifest.json`. It stops if `format` is not `pqax`, or if it does not know the first number of `formatVersion`.
3. It reads `summary.json`.

It can now show every source, table, and column with its totals. It reads a detail part only when something needs it.

A PQAX file can come from anyone. Before it reads a part, a reader should compare the size in the directory and the rows in the manifest with what it is ready to load.

## 8. Depth

The object model lets a collector choose how deep the detail of each table goes. In the file, the depth of a table is the set of rows that the detail tables have for it.

| `detailDepth` of the table | Rows in the file |
|---|---|
| `Summary` | Its rows in the summary tables |
| `Files` | Also all its data files, in `files` |
| `Row groups` | Also the row groups of the files whose footer was read, in `rowGroups` |
| `Column chunks` | Also the column chunks of those row groups, in `columnChunks` |

1. The depth is set for each table, so one file can hold tables at different depths.
2. A detail is complete or absent. When the file has files for a table, it has all of them, and `fileCount` equals their number.
3. `partitions`, `deleteFiles`, and `commits` are optional at any depth.
4. A file with only `manifest.json` and `summary.json` is complete at the `Summary` depth.
5. Missing rows mean that the detail was not kept. The totals of the table say how many files and row groups exist.
6. A reader treats a value of `detailDepth` that it does not know as deeper than `Column chunks`.

## 9. Versions and extensions

These rules apply section 7 of the object model to the file.

1. A reader ignores what it does not know: an entry that the manifest does not list, a member of the manifest, a table, a field, a member of a nested object, and a member of a table that is not an array.
2. A reader accepts a value that it does not know where the model has an open list of values, and shows it as it is.
3. A later version adds tables, fields, and values. It does not rename or remove what exists, change its meaning or its type, or move a table between the summary and the detail.
4. `formatVersion` has two numbers, as in `1.0`. A change that follows rule 3 raises the second number. A reader accepts a file whose first number it knows, whatever the second. The first number is the text before the first dot.
5. A name that a producer adds on its own starts with a prefix that the producer chooses, followed by an underscore, as in `acme_reviewNote`. This applies to tables, fields, members of the manifest, and entries. The format never defines a name with an underscore, so the two cannot collide.
6. While the format is a draft, the version is `0.x`. A new draft may break the files of the draft before it. A reader still accepts any `0.x` file and reads what it knows.

How the extensions that the object model foresees would reach the file:

| Extension | In the file |
|---|---|
| Nested fields of a column | A new summary table with a reference to `columns`, and a new optional reference on `physicalColumns` |
| Pages | A new detail table with references to `rowGroups` and `physicalColumns`, and a new value of `detailDepth` |
| Statistics for each file and column | A new detail table with references to `files` and `columns` |
| Another table format | A new value of `format` and a new nested object on `tables` |
| Views | New parts with their own tables. See section 11. |
| A more compact form for a field | A member that is an object and not an array. See rule 5 of section 5.1. |

## 10. Names, data values, and obfuscation

1. A name that comes from the source appears only as a value: in the fields that the object model marks as `Name`, and in the keys and values of maps. No entry name, table name, or field name comes from the source.
2. An obfuscator therefore replaces values and nothing else. The entries, the tables, the fields, and the lengths of the arrays stay as they are.
3. When names are obfuscated, the manifest says so with the obfuscation fields, as a VPAX does.
4. A writer leaves out the fields that the object model marks as `Data`, unless `collection` states that the file includes data values or partition values.

## 11. Views

VPAX carries its content twice: the model as nested objects, and flat views for the Excel workbook. The object model left open whether PQAX does the same.

The first version has one representation and no views. **Decided.** The tables of section 5 are already flat, and a view would only put a name where a table has a reference, which is one join. The prototype supports this:

- Python reads a table with its standard library and no helper code.
- DuckDB loads the tables as they are, and joins 225,484 column chunks to their tables through three references in 0.5 s.
- The JavaScript reader follows the same references with a lookup for each row.

A later version can still add views as new parts with their own tables. A reader of this version ignores them, and the three rules for views in the object model apply.

## 12. Embedding in a VPAX

1. A VPAX holds the footprint of the tables that its model reads as one part. The part is a complete PQAX file, with the same bytes that the file has on its own. **Decided.**
2. The part is named `Footprint.pqax`. **Decided.** A footprint can cover several lakehouses and warehouses, which is why the name replaces the earlier placeholder, `Lakehouse.pqax`.
3. The content type of the part is `application/zip`.
4. A writer should store the part without compression. Its content is already compressed, and a stored part is one continuous range of the VPAX file, so a reader opens the PQAX file inside it without a copy.
5. A reader also accepts a deflated part, which it inflates in memory first.
6. The `modelBindings`, `tableBindings`, and `columnBindings` tables link the tables and columns of the model to those of the footprint.

Checked with the prototype:

- `System.IO.Packaging` 9.0.0 stores a part created with `CompressionOption.NotCompressed`, on .NET 10 and on .NET Framework 4.8. A test package with the part and an empty model is about 600 bytes larger than the PQAX file that it holds.
- The JavaScript reader opens a footprint inside a package in 4 ms, against 3 ms for the same file on its own.
- The VPAX library ignores a part that it does not know, as a test of the library showed.

The VPAX obfuscator deletes every part except `DaxModel.json`. An obfuscated VPAX therefore loses its footprint until the obfuscator learns to obfuscate one.

## 13. Measurements

The numbers come from the [prototype](../research/prototype/README.md), run on 5 and 6 October 2026 with Python 3.14 and Node 24.

The input is what a probe read from a real Fabric lakehouse and warehouse: 78 tables, 813 columns, 5,236 files, 13.7 GB, and 929 million rows. The probe read the footer of 399 files. The estimates give the other files a copy of a footer that was read, with its numbers changed at random.

### 13.1 Size

| Depth of every table | Detail rows | Size of the file |
|---|---|---|
| Summary | None | 62 KB |
| Files | 5,236 files, 4,720 partitions, and 1,112 commits | 346 KB |
| Row groups | Also 400 row groups | 351 KB |
| Column chunks | Also 10,259 column chunks | 435 KB |
| Column chunks, estimated with every footer read | 5,237 row groups and 225,484 column chunks | 2.1 MB |
| The same estimate, written five times | 26,180 files and 1,127,420 column chunks | 10.0 MB |

| Row | Bytes in the zip |
|---|---|
| Column, with its share of every summary table | 76 |
| File | 53, of which 28 are its path |
| Row group | 13 |
| Partition | 9 |
| Column chunk | 7 |

`summary.json` is a single part, so it grows with the number of columns: about 350 bytes of JSON text for each column, before compression.

### 13.2 Time

Plain JavaScript, with no library:

| Step | Time |
|---|---|
| Open a file with 78 tables and 813 columns: directory, manifest, and summary | 3 ms |
| Open a file with 390 tables and 4,065 columns | 8 ms |
| Read 5,236 files | 4 ms |
| Read 225,484 column chunks in one part | 20 ms to inflate and 46 ms to parse |
| Read 1,127,420 column chunks in five parts | 151 ms to inflate and 248 ms to parse, with at most 73 ms for one part |

Other readers, on the estimate with 225,484 column chunks:

- Python parses the summary in 3 ms and the column chunks in 0.16 s, with its standard library.
- DuckDB loads four tables and joins them in 0.5 s. On the estimate that is five times larger it takes 0.7 s.

### 13.3 Layouts of a table

On the same estimate:

| Layout | Size of the file | Time to parse the column chunks | Memory they hold in JavaScript |
|---|---|---|---|
| One array for each field, as proposed | 2.05 MB | 46 ms | 43.5 MB |
| The same, with repeated values in a dictionary | 2.03 MB | 24 ms | 22.4 MB |
| One array for each row | 3.33 MB | 49 ms | 55.5 MB |
| One object for each row | 3.94 MB | 111 ms | 49.5 MB |

Half of the 43.5 MB is the `encodings` field, which holds a small list for each row. The whole estimate has 4 distinct lists. A reader that shares equal lists after parsing brings the memory down to 22.8 MB, with a loop that takes 14 ms.

### 13.4 Rules that a program can check

The prototype has a checker for the rules of sections 3 to 9: the container, the JSON values, the lengths of the arrays, the identifiers, the references, the depth of each table, and the totals against the detail.

- Every file that the prototype built in this format passes, up to the one with 1.1 million column chunks in five parts. The copy of that file with all the column chunks in one part is rejected, because the part is too large.
- A test runs the checker on 12 changed copies of the sample file. The checker accepts the 2 changes that the rules allow, which are the additions of a producer and a depth that a later version adds. It rejects the 10 that break a rule.

### 13.5 Not measured

- A reader or a writer in .NET, and a reader in a browser. Node runs the same JavaScript engine as Chrome and Edge, so the time to parse a part of 250,000 rows is still unknown for Firefox and Safari.
- Iceberg tables, nested columns, delete files, and model bindings, which the input does not have.
- Files with data values or partition values.

## 14. Alternatives considered

| Alternative | Why not |
|---|---|
| One JSON document of nested objects, as in `DaxModel.json` | A reader parses everything to show anything, and shared objects need `$id` and `$ref`, which belong to one serializer. With one object for each row, the file is 92 percent larger and takes 2.4 times longer to parse. |
| One array for each row, with the names of the fields stated once | The file is 62 percent larger, and it is no faster to parse |
| A dictionary for repeated values | See below |
| Positions in the arrays in place of `id` | Smaller, but a tool that filters or sorts a table breaks the references without any sign of it |
| Numbers for timestamps | Smaller, but unreadable, and SQL tools and spreadsheets recognize the text form |
| Parts in Parquet, Arrow, or CSV | Parquet and Arrow put a library in every reader. CSV has no types and no native parser in JavaScript. In the [encoding experiment](../research/detail-encoding/README.md), a million column chunks took 16.7 MB as Parquet, 13.1 MB as CSV, and 8.3 MB as JSON arrays. |
| A SQLite file | It has tables and joins built in, but every reader needs a database engine, also in the browser |
| An Open Packaging Conventions package | It adds an entry and naming rules that PQAX has no use for |
| PascalCase names, as in VPAX | Lower camel case is the convention of JSON, of the Delta log, and of JavaScript, where the web reader lives. **Decided** for lower camel case. |

**The dictionary.** This form writes a field that has few distinct values as the list of those values and one index for each row. It halves the time to parse the column chunks and the memory they hold. It saves only 1 percent of the file, because deflate already removes the repetition. Its cost is that every field would have two forms, every reader would handle both, and a table would no longer load as it is into SQL or a data frame. A reader gets the same memory by sharing equal lists after it parses a part, and the limit on the rows of a part bounds what it holds before that. The dictionary is left out. **Decided.** Rule 5 of section 5.1 lets a later version add it without breaking a reader of this one.

## 15. Open questions

1. **JSON Schema.** ~~The format spec asks for a schema of every part. The names are now confirmed, so it can be written. The checker of the prototype is a first step toward a conformance test.~~ Answered in version 0.1: the [conformance kit](../conformance/README.md) has a JSON Schema, draft 2020-12, of the manifest and of every table, in `conformance/schema`.
2. **Media type.** Whether to register one for `.pqax`. Until then the embedded part uses `application/zip`. Tracked in section 4 of the [backlog](backlog.md), for later.
3. **File paths.** They take half of the `files` table. The object model already asks whether a collection may leave them out. Tracked in section 4 of the [backlog](backlog.md), by the item on the open questions that touch the file, before 1.0.
4. **Issues.** They are in the summary. A collection that fails on every file would make the summary large, so a collector needs a limit. Tracked in section 4 of the [backlog](backlog.md), by the item on the open questions that touch the file, before 1.0. Until then the collector keeps 100 issues for each table, and one more that counts the rest.
5. **.NET.** ~~A reader and a writer in C# are still to be written, and the reader must also run on .NET Framework.~~ Answered in version 0.1: the reader and the writer are in [src/dotnet/Pqax](../src/dotnet/Pqax/README.md), and the reader runs on .NET Framework through the netstandard2.0 target.
