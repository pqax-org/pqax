# PQAX validation rules

Format version 0.2. Every validator reports the codes of this document. The codes and their one-line rules come from section 4.1 of the [contracts](../docs/contracts.md). This document adds what the plan leaves to the kit: the exact condition of each code, the fields that each rule covers, and the rules that a validator skips when an earlier one fails.

The [serialization](../docs/serialization.md) and the [object model](../docs/object-model.md) are the source of truth. When this document disagrees with them, this document is wrong and is fixed.

## 1. The codes

This is the table of section 4.1 of the contracts, unchanged. The section numbers refer to the serialization; "model" refers to the object model.

| Code | The rule that is broken | Where |
|---|---|---|
| `container.method` | An entry is neither stored nor deflated | 3.2 |
| `container.encrypted` | An entry is encrypted | 3.2 |
| `container.zip64` | The archive uses ZIP64 | 3.3 |
| `container.partName` | The name of a part has a character other than ASCII letters, digits, `.`, `-`, `_` | 3.4 |
| `container.caseCollision` | Two entry names differ only in case | 3.4 |
| `container.manifestMissing` | There is no `manifest.json` entry | 3.5 |
| `json.invalid` | A part is not valid JSON | 4.1 |
| `json.topLevel` | The top level of a part is not an object | 4.1 |
| `json.bom` | A part starts with a byte order mark | 4.2 |
| `json.duplicateMember` | An object repeats a member name | 4.3 |
| `json.integer` | A number is not an integer | 4 |
| `json.integerRange` | An integer is beyond 2^53 - 1 in absolute value | 4 |
| `json.timestamp` | A timestamp is not in the form `2026-10-05T08:15:30.000Z` | 4 |
| `json.type` | A value has not the JSON type of its field: a string where the model says integer, an array where it says map, and so on | 4 |
| `manifest.required` | `format`, `formatVersion`, `collection`, `collection.level`, or `parts` is missing, or a part descriptor has no `name` or `tables` | 7.1 |
| `manifest.format` | `format` is not `pqax` | 7.1 |
| `manifest.formatVersion` | `formatVersion` is not two numbers separated by a dot | 7.1 |
| `manifest.versionUnknown` | The first number of `formatVersion` is not one the validator knows | 9.4 |
| `manifest.firstPart` | The first part is not `summary.json` | 7.2 rule 2 |
| `manifest.duplicatePart` | A part is listed twice | 7.2 |
| `manifest.partMissing` | A listed part has no entry in the archive | 7.1 |
| `part.tables` | The tables in a part differ from the tables that the manifest states for it | 7.2 rules 1 and 7 |
| `part.kind` | A detail table is in `summary.json`, or a summary table is in another part | 7.2 rules 2 and 3 |
| `part.singleTable` | A detail part holds more than one table | 7.2 rule 3 |
| `part.tooLarge` | A detail part holds more than 250,000 rows | 7.2 rule 4 |
| `part.rowCount` | An array of a table has not the length that the manifest states for that part | 5.1 rule 2, 7.2 rule 7 |
| `table.required` | A required field is missing, or has `null` in a row. The required fields are those of the object model, plus the parent reference and `position` of serialization 5.3 | model, 5.3 |
| `table.idUnique` | An `id` is not a positive integer, or is repeated in its table | 6 rule 1 |
| `table.reference` | A reference points to a row that the file does not have | 6 rule 4 |
| `depth.files` | A table at the `Summary` depth has rows in `files` | 8 |
| `depth.rowGroups` | A table below the `Row groups` depth has rows in `rowGroups` | 8 |
| `depth.columnChunks` | A table below the `Column chunks` depth has rows in `columnChunks` | 8 |
| `totals.fileCount` | `fileCount` of a table differs from the number of its files kept | 8 rule 2 |
| `totals.fileSize` | `fileSize` of a table differs from the sum over its files kept | 8 rule 2 |
| `totals.rowGroupCount` | `rowGroupCount` of a table differs from the number of its row groups kept | model 4.2 |
| `totals.compressedSize` | `compressedSize` of a table differs from the sum over its column chunks kept | model 4.2 |
| `totals.columnCompressedSize` | `compressedSize` of a column differs from the sum over its column chunks kept | model 4.2 |
| `embedded.partMissing` | A VPAX has no part with the name of the footprint. Reported by a reader that opens an embedded footprint and by a validator asked to validate one, never for a standalone file | 12 |

The plan adds that the `json.*` rules apply to the manifest as well as to the parts, and that a total that is `null` is not compared. An issue has a code, a message, and, when they apply, the part, the table, and the field. The kit asserts the codes only.

## 2. Terms

| Term | Meaning |
|---|---|
| Entry | A file in the zip archive |
| Part | An entry that the manifest lists in `parts`. An entry that the manifest does not list is not a part: a reader ignores it, and only `container.caseCollision` looks at its name. |
| Document | `manifest.json` or a part. The `json.*` rules apply to every document, the manifest included (serialization 4 rule 1). |
| Summary part | The part named `summary.json`, wherever the manifest lists it. It is always present, as an empty object when the footprint has no summary rows (7.2 rule 6). |
| Detail part | Every part other than the summary part |
| Summary table | `sources`, `tables`, `columns`, `partitionSpecs`, `partitionFields`, `sortOrders`, `sortFields`, `physicalSchemas`, `physicalColumns`, `writers`, `operations`, `issues`, `modelBindings`, `tableBindings`, `columnBindings` |
| Detail table | `partitions`, `files`, `rowGroups`, `columnChunks`, `deleteFiles`, `commits` |
| Table of the format | One of these 21 tables. Every member of a part is a table, and the manifest lists it (7.2 rule 1). A member with another name is a table of a producer or of a later version: the `part.*` rules apply to it, and so do the `json.*` rules on the text of a document, but no rule on values or rows. |
| Field | A member of a table whose value is an array. A member whose value is not an array is not a field of this version (5.1 rule 5), and no rule looks at it. |
| Rows of a table in a part | The number that the manifest states for the table in that part |
| Rows of a table | The rows of the table in all its parts, in the order of the manifest. A field that a part leaves out has `null` in the rows of that part (5.1 rule 4). |
| Level of a table | From its `detailDepth`: `Summary` is 0, `Files` 1, `Row groups` 2, `Column chunks` 3, and any other string is deeper than 3 (8 rule 6). The comparison is exact, with case. A table keeps files from level 1, row groups from level 2, and column chunks from level 3. |
| Files of a table | The rows of `files` whose `table` is the `id` of the table |
| Row groups of a table | The rows of `rowGroups` whose `file` is the `id` of a file of the table |
| Column chunks of a table | The rows of `columnChunks` whose `physicalColumn` is the `id` of a physical column whose `physicalSchema` is the `id` of a physical schema whose `table` is the `id` of the table |
| Column chunks of a column | The rows of `columnChunks` whose `physicalColumn` is the `id` of a physical column whose `column` is the `id` of the column |

## 3. Conditions

Each code is reported when its condition holds. "Source" gives the section of the serialization, or of the object model, that the rule comes from.

| Code | Condition | Source |
|---|---|---|
| `container.method` | An entry of the archive has a compression method other than 0, stored, and 8, deflate. | 3 rule 2 |
| `container.encrypted` | An entry of the archive has bit 0 of its general purpose flags set. | 3 rule 2 |
| `container.zip64` | The archive has a ZIP64 end of central directory locator, or an entry of the central directory has a ZIP64 extra field, header 0x0001. | 3 rule 3 |
| `container.partName` | The name of a part is empty, or has a character other than `A` to `Z`, `a` to `z`, `0` to `9`, `.`, `-`, and `_`. | 3 rule 4 |
| `container.caseCollision` | Two entries of the archive, parts or not, have names that are equal when ASCII letters are compared without case. Two entries with the same name also break the rule. | 3 rule 4 |
| `container.manifestMissing` | The archive has no entry named exactly `manifest.json`. | 3 rule 5 |
| `json.invalid` | A document is not a JSON text in UTF-8: it does not parse, its bytes are not valid UTF-8, or it uses a literal that JSON does not have, such as `NaN` or `Infinity`. | 4 rule 1 |
| `json.topLevel` | The top-level value of a document is not an object. | 4 rule 1 |
| `json.bom` | A document starts with the bytes EF BB BF. The validator reports it, skips the three bytes, and reads on. | 4 rule 2 |
| `json.duplicateMember` | An object anywhere in a document has two members with the same name. | 4 rule 3 |
| `json.integer` | An integer of the format (section 4.1 of this document) is a number with a fraction or an exponent. | 4, the table of types |
| `json.integerRange` | An integer of the format (section 4.1 of this document) is greater than 2^53 - 1 or less than -(2^53 - 1). | 4, the table of types |
| `json.timestamp` | A timestamp of the format (section 4.2 of this document) is a string that does not match `^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$`. | 4, the table of types |
| `json.type` | A value whose type the format defines is not `null` and has another JSON type than section 4.3 of this document gives, or a list other than `partitions.values` has a `null` element, or a map has a value that is not a string. | 4, the table of types; model 6.6 |
| `manifest.required` | `format`, `formatVersion`, `collection`, `collection.level`, or `parts` is missing from the manifest or is `null`, or an element of `parts` has no `name` or no `tables`, or `null` there. | 7.1; model 6.2 |
| `manifest.format` | `format` is present and is not the string `pqax`. | 7.1; 7.3 rule 2 |
| `manifest.formatVersion` | `formatVersion` is present and is not a string that matches `^[0-9]+\.[0-9]+$`. | 9 rule 4 |
| `manifest.versionUnknown` | `formatVersion` matches that pattern, and its first number is not one that the validator reads. A validator of this version reads 0. | 9 rule 4; 7.3 rule 2 |
| `manifest.firstPart` | `parts` is an array, and it is empty or its first element does not have the `name` `summary.json`. | 7.2 rule 2 |
| `manifest.duplicatePart` | Two elements of `parts` have the same `name`. | 7.2 rule 4: the rows of a table are the rows of its parts, in the order of the manifest |
| `manifest.partMissing` | The archive has no entry whose name is exactly the `name` of a part. | 7.1: each part has the name of its entry |
| `part.tables` | The names of the members of a part differ from the names of the tables that the manifest states for that part: a table is stated and absent, or a member is present and not stated. | 7.2 rules 1 and 7 |
| `part.kind` | The summary part has a member named after a detail table, or a detail part has a member named after a summary table. | 7.2 rules 2 and 3 |
| `part.singleTable` | A detail part has more than one member. | 7.2 rule 3 |
| `part.tooLarge` | The rows that the manifest states for a detail part add up to more than 250,000. | 7.2 rule 4 |
| `part.rowCount` | In a part, a field of a table has a length other than the rows that the manifest states for that table in that part. | 5.1 rule 2; 7.2 rule 7 |
| `table.required` | In a part that holds a table of the format, a required field of that table (section 4.4 of this document) is missing, or has `null` in a row. | model, the "Required" line of each object; 5.3; 6 rule 1 |
| `table.idUnique` | In a table with an `id`, a value of `id` is not an integer greater than 0, or appears in two rows of the table, in the same part or in two parts. | 6 rule 1 |
| `table.reference` | A reference, or an element of a list of references (section 4.5 of this document), is not `null` and is not the `id` of a row of the table it points to. | 6 rules 3 and 4 |
| `depth.files` | A table at level 0 has files. | 8, the table of depths |
| `depth.rowGroups` | A table at level 0 or 1 has row groups. | 8, the table of depths |
| `depth.columnChunks` | A table at level 0, 1, or 2 has column chunks. | 8, the table of depths |
| `totals.fileCount` | A table at level 1 or deeper has a `fileCount` that is not `null` and differs from the number of its files. | 8 rule 2 |
| `totals.fileSize` | A table at level 1 or deeper has a `fileSize` that is not `null`, every one of its files has a `fileSize`, and their sum differs from it. | 8 rule 2; model 4.2 rules 2 and 4 |
| `totals.rowGroupCount` | A table at level 2 or deeper has a `rowGroupCount` that is not `null` and differs from the number of its row groups. | model 4.2 rule 2 |
| `totals.compressedSize` | A table at level 3 or deeper has a `compressedSize` that is not `null` and differs from the sum of `compressedSize` over its column chunks. | model 4.2 rule 2; model 6.4, `CompressedSize` |
| `totals.columnCompressedSize` | A column of a table at level 3 or deeper, whose table has at least one column chunk, has a `compressedSize` that is not `null` and differs from the sum of `compressedSize` over the column chunks of the column. | model 4.2 rule 2; model 6.5 |

Notes on the conditions:

- `json.type`, `json.integer`, `json.integerRange`, and `json.timestamp` do not overlap on a value. A string in an integer field breaks `json.type`, not `json.integer`. A number in a timestamp field breaks `json.type`, not `json.timestamp`.
- A value can break `json.type` and a rule on the same value: an `id` that is a string also breaks `table.idUnique`, a reference that is a string also breaks `table.reference`, and a `format` or `formatVersion` that is not a string also breaks `manifest.format` or `manifest.formatVersion`.
- A `null` total is not compared (contracts 4.1). A total is absent when a value it needs is unknown (model 4.2 rule 4), and a table can keep a depth that its collection level did not reach.
- `totals.columnCompressedSize` needs the table to have column chunks because the totals of a column come from the table format when no footer was read (model 6.5), and they are then not a sum over column chunks.
- `json.integer`: the kit tests a number that is not whole, `2048.5`. The text forms `1.0` and `1e3` also break the rule, but `JSON.parse` cannot see them, so no sample has them.
- `json.timestamp` checks the form, not the calendar: `2026-02-31T00:00:00.000Z` matches.
- `manifest.formatVersion`: "two numbers separated by a dot" is the pattern above. `manifest.versionUnknown` is not evaluated when `formatVersion` breaks it.

## 4. What the rules cover

### 4.1 Integers of the format

`json.integer` and `json.integerRange` look at these values when they are numbers, and at no other number:

- every field that the object model types as integer, which [part.schema.json](schema/part.schema.json) gives as `integerField` or `requiredIntegerField`;
- every `id`, every reference, and every element of a list of references;
- `position` in `partitionFields` and `sortFields`;
- every member of a `Distribution`, `DeltaDetails`, or `IcebergDetails` that the object model types as integer;
- in the manifest: `collection.level`, `collection.requestCount`, `collection.bytesRead`, and the row counts in `parts[].tables`.

A field or a member that the format does not define is not checked, whatever its numbers.

### 4.2 Timestamps of the format

`tables.createdAt`, `tables.snapshotTimestamp`, `tables.lastModifiedAt`, `tables.oldestCommitAt`, `tables.nonLiveOldestRemovedAt`, `tables.nonLiveNewestRemovedAt`, `operations.lastAt`, `partitions.lastModifiedAt`, `files.modifiedAt`, `commits.timestamp`, and in the manifest `collection.startedAt` and `collection.completedAt`.

### 4.3 JSON types

`json.type` compares the JSON type of a value with the type that the format gives to it (serialization 4). It looks at every element of a field of the 21 tables, at the members of the nested objects that the object model defines, and at the members of the manifest, of `collection`, and of the elements of `parts`. [part.schema.json](schema/part.schema.json) and [manifest.schema.json](schema/manifest.schema.json) give the type of each of them.

| Type of the value | JSON type |
|---|---|
| string, timestamp | string |
| integer, `id`, reference, `position` | number |
| boolean | `true` or `false` |
| list of strings | array of strings |
| list of references | array of numbers |
| `partitions.values` | array of strings and `null` (model 6.6) |
| map of string to string | object whose values are strings |
| `Distribution`, `DeltaDetails`, `IcebergDetails` | object, whose members have their own types or are `null` |
| `collection` in the manifest | object |
| `parts` in the manifest | array of objects |
| `tables` in an element of `parts` | object whose values are numbers |

`null` means no value, and `json.type` does not report it, with two exceptions: in a list, only `partitions.values` accepts `null` as an element, and in a map, every value is a string. A required field with `null` breaks `table.required`. A member of a table that is not an array is not a field, and `json.type` does not look at it. In Python, `True` and `False` are instances of `int`, so a validator tests for a boolean before it tests for a number.

### 4.4 Required fields

From the "Required" line of each object in the object model, plus the reference to the parent and the `position` that section 5.3 of the serialization adds, required in every table except the reference of `issues`, and the `id` of section 6 rule 1.

| Table | Required fields |
|---|---|
| `sources` | `id`, `kind` |
| `tables` | `id`, `name`, `format`, `detailDepth` |
| `columns` | `id`, `table`, `name`, `dataType` |
| `partitionSpecs` | `id`, `table` |
| `partitionFields` | `partitionSpec`, `position` |
| `sortOrders` | `id`, `table` |
| `sortFields` | `sortOrder`, `position`, `sourceColumn`, `direction` |
| `physicalSchemas` | `id`, `table` |
| `physicalColumns` | `id`, `physicalSchema`, `ordinal`, `path`, `physicalType` |
| `writers` | `id`, `table` |
| `operations` | `table`, `name` |
| `issues` | `code` |
| `modelBindings` | `id`, `modelName` |
| `tableBindings` | `id`, `modelBinding`, `modelTable`, `table` |
| `columnBindings` | `tableBinding`, `modelColumn`, `column` |
| `partitions` | `id`, `table` |
| `files` | `id`, `table` |
| `rowGroups` | `id`, `file`, `ordinal`, `rowCount` |
| `columnChunks` | `rowGroup`, `physicalColumn`, `compressedSize`, `uncompressedSize` |
| `deleteFiles` | `id`, `table`, `kind` |
| `commits` | `table`, `commitId` |

A required field written as a member that is not an array is missing. A `null` `id` breaks `table.required` and `table.idUnique`.

### 4.5 References

The fields marked "list" hold a list of references.

| Table | Field | Points to |
|---|---|---|
| `tables` | `source` | `sources` |
| `tables` | `clusteringColumns`, list | `columns` |
| `columns` | `table` | `tables` |
| `partitionSpecs` | `table` | `tables` |
| `partitionFields` | `partitionSpec` | `partitionSpecs` |
| `partitionFields` | `sourceColumns`, list | `columns` |
| `sortOrders` | `table` | `tables` |
| `sortFields` | `sortOrder` | `sortOrders` |
| `sortFields` | `sourceColumn` | `columns` |
| `physicalSchemas` | `table` | `tables` |
| `physicalColumns` | `physicalSchema` | `physicalSchemas` |
| `physicalColumns` | `column` | `columns` |
| `writers` | `table` | `tables` |
| `operations` | `table` | `tables` |
| `issues` | `table` | `tables` |
| `tableBindings` | `modelBinding` | `modelBindings` |
| `tableBindings` | `table` | `tables` |
| `columnBindings` | `tableBinding` | `tableBindings` |
| `columnBindings` | `column` | `columns` |
| `partitions` | `table` | `tables` |
| `partitions` | `partitionSpec` | `partitionSpecs` |
| `files` | `table` | `tables` |
| `files` | `partition` | `partitions` |
| `files` | `sortOrder` | `sortOrders` |
| `files` | `physicalSchema` | `physicalSchemas` |
| `files` | `writer` | `writers` |
| `rowGroups` | `file` | `files` |
| `columnChunks` | `rowGroup` | `rowGroups` |
| `columnChunks` | `physicalColumn` | `physicalColumns` |
| `deleteFiles` | `table` | `tables` |
| `deleteFiles` | `referencedFile` | `files` |
| `deleteFiles` | `equalityColumns`, list | `columns` |
| `deleteFiles` | `partition` | `partitions` |
| `commits` | `table` | `tables` |

The tables with an `id` are the 14 tables that a reference points to: `sources`, `tables`, `columns`, `partitionSpecs`, `sortOrders`, `physicalSchemas`, `physicalColumns`, `writers`, `modelBindings`, `tableBindings`, `partitions`, `files`, `rowGroups`, and `deleteFiles`.

## 5. Rules that a validator skips

A validator reports every issue it finds. A rule that it cannot evaluate because an earlier rule failed is skipped, not reported (contracts 4.1).

- Without a readable manifest (`container.manifestMissing`, or `json.invalid` or `json.topLevel` on `manifest.json`), only the container rules run.
- After `manifest.format` or `manifest.versionUnknown`, a reader stops, and so does the validator: it reads no part.
- Without `parts` (`manifest.required`), no rule on parts runs. An element of `parts` without `name` or `tables` is left out.
- A part that cannot be read is left out: a part with `manifest.partMissing`, `json.invalid`, or `json.topLevel`, or one compressed with a method that the validator cannot decompress. The rules on its tables are skipped, and so are the reference, depth, and totals rules that need a table that it holds.
- A rule that needs a field that `table.required` reports as missing is skipped for the rows concerned. For example, a table without `detailDepth` gets no depth and no totals rule.
- A value that breaks `json.type` is unknown to every other rule, except the rules on the same value that section 3 lists. For example, a `fileSize` written as a string leaves `totals.fileSize` unevaluated for its table.

Some changes can produce more codes than the one they target, for example a validator that fills the rows that the manifest states with `null` reports `table.required` next to `part.rowCount`. That is allowed: [samples/invalid/expected.json](samples/invalid/expected.json) lists the codes that a validator must report at least.

## 6. Readers

A reader is more tolerant than a validator (contracts 4.1). It skips a byte order mark, ignores what it does not know, finds nothing when it follows a reference to a missing row, reads a listed `summary.json` that is missing as empty summary tables, fails the read of a listed detail part that is missing with `manifest.partMissing`, and refuses a file only for `manifest.format`, `manifest.versionUnknown`, `container.manifestMissing`, `embedded.partMissing`, and JSON that does not parse.

## 7. Coverage of the kit

| Code | Invalid samples |
|---|---|
| `container.method` | `container.method.pqax` |
| `container.encrypted` | None. The `zipfile` module of Python cannot write an encrypted entry. |
| `container.zip64` | None. The `zipfile` module of Python writes ZIP64 records in the central directory only for an archive that needs them, over 4 GB or 65,535 entries. |
| `container.partName` | `container.partName.pqax` |
| `container.caseCollision` | `container.caseCollision.pqax` |
| `container.manifestMissing` | `container.manifestMissing.pqax` |
| `json.invalid` | `json.invalid.pqax` |
| `json.topLevel` | `json.topLevel.pqax` |
| `json.bom` | `json.bom.pqax` |
| `json.duplicateMember` | `json.duplicateMember.pqax` |
| `json.integer` | `json.integer.pqax` |
| `json.integerRange` | `json.integerRange.pqax` |
| `json.timestamp` | `json.timestamp.pqax`, `json.timestamp.nonLiveOldestRemovedAt.pqax`, `json.timestamp.partitionLastModifiedAt.pqax` |
| `json.type` | `json.type.pqax`, `json.type.map.pqax`, `json.type.unreferencedFileCount.pqax` |
| `manifest.required` | `manifest.required.pqax`, `manifest.required.level.pqax`, `manifest.required.partTables.pqax` |
| `manifest.format` | `manifest.format.pqax` |
| `manifest.formatVersion` | `manifest.formatVersion.pqax` |
| `manifest.versionUnknown` | `manifest.versionUnknown.pqax` |
| `manifest.firstPart` | `manifest.firstPart.pqax` |
| `manifest.duplicatePart` | `manifest.duplicatePart.pqax` |
| `manifest.partMissing` | `manifest.partMissing.pqax` |
| `part.tables` | `part.tables.pqax` |
| `part.kind` | `part.kind.detail-in-summary.pqax`, `part.kind.summary-in-detail.pqax` |
| `part.singleTable` | `part.singleTable.pqax` |
| `part.tooLarge` | `part.tooLarge.pqax` |
| `part.rowCount` | `part.rowCount.manifest.pqax`, `part.rowCount.array.pqax` |
| `table.required` | `table.required.null.pqax`, `table.required.missing.pqax`, `table.required.position.pqax`, `table.required.operationName.pqax` |
| `table.idUnique` | `table.idUnique.repeated.pqax`, `table.idUnique.nonPositive.pqax` |
| `table.reference` | `table.reference.single.pqax`, `table.reference.list.pqax` |
| `depth.files` | `depth.files.pqax` |
| `depth.rowGroups` | `depth.rowGroups.pqax` |
| `depth.columnChunks` | `depth.columnChunks.pqax` |
| `totals.fileCount` | `totals.fileCount.pqax` |
| `totals.fileSize` | `totals.fileSize.pqax` |
| `totals.rowGroupCount` | `totals.rowGroupCount.pqax` |
| `totals.compressedSize` | `totals.compressedSize.pqax` |
| `totals.columnCompressedSize` | `totals.columnCompressedSize.pqax` |

The valid samples test what must not be reported: a producer's additions and a later form of a field (`extensions.pqax`), a depth that this version does not define (`later-depth.pqax`), a later second number of the version (`later-version.pqax`), and fields that a part leaves out (`multipart.pqax`).

## 8. Open points

What the documents still leave without a code.

1. **Totals without a code.** The object model says that every total equals the sum over the detail when the detail is present (4.2 rule 2). Only five totals have a code. These have none: `rowCount` of a table against its files, `rowGroupCount`, `compressedSize`, and `uncompressedSize` of a file, `compressedSize` of a row group, and the totals of `physicalColumns`, `partitions`, `writers`, and `physicalSchemas`.
2. **Rules without a code.** A table with no rows and a detail part with no table are left out (7.2 rule 6). Data values are left out unless `collection` says otherwise (10 rule 4). The JSON Schema rejects a detail part with no table; no code reports either rule.
