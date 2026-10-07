# Pqax

Reads, writes, and validates PQAX files in .NET. A PQAX file holds the footprint of a set of Parquet tables: how much space the data takes and how it is arranged, without the data. It is a zip of JSON tables, defined by the PQAX serialization and the PQAX object model, which the [conformance kit](../../../conformance/README.md) turns into rules, a schema, and samples.

| | |
|---|---|
| Targets | netstandard2.0, so that .NET Framework hosts can use it, and net8.0 |
| Dependencies | None on net8.0. On netstandard2.0, the `System.Text.Json` package. |
| Reads | Format versions whose first number, the text before the first dot, is 0: `0.1`, `0.9`, and also `0` or `0.1.2` |
| Writes | Format version `0.1` |
| Validates | Every rule of the conformance kit, with its code |

The package is not published yet. Reference the project `src/dotnet/Pqax/Pqax.csproj`.

## Read

```csharp
using Pqax;

using PqaxDocument doc = PqaxDocument.Open("footprint.pqax");
Console.WriteLine($"{doc.Manifest.Format} {doc.Manifest.FormatVersion}, level {doc.Manifest.Collection.Level}");

foreach (TableRow table in doc.Summary.Tables)
{
    Console.WriteLine($"{table.Name}: {table.FileCount} files, {table.FileSize} bytes, depth {table.DetailDepth}");
}

// A detail table is read from its parts when it is asked for, and then kept.
PqaxTable<ColumnChunkRow> chunks = doc.ReadColumnChunks();
foreach (ColumnChunkRow chunk in chunks)
{
    PhysicalColumnRow? physical = doc.Summary.PhysicalColumns.FindById(chunk.PhysicalColumn);
    ColumnRow? column = doc.Summary.Columns.FindById(physical?.Column);
    // ...
}
```

| Member | What it does |
|---|---|
| `PqaxDocument.Open(path)`, `Open(stream, leaveOpen)` | Reads the central directory of the zip, `manifest.json`, and `summary.json`. Nothing else. |
| `PqaxDocument.OpenEmbedded(vpaxPath, partName)`, `OpenEmbedded(stream, partName, leaveOpen)` | Opens the PQAX file that a VPAX file holds as the part `Footprint.pqax`. A stored part is read in place, a deflated part is inflated in memory first. `EmbeddedPart` gives the name of the part and `EmbeddedCompression` gives `stored` or `deflated`; both are null for a PQAX file of its own. |
| `Manifest` | Format, version, collection, obfuscation fields, parts, and the members that this version does not know, in `Extensions` |
| `Summary` | The 15 summary tables, one property each: `Sources`, `Tables`, `Columns`, and so on |
| `RowCount(table)` | The rows of a table, from the manifest, without reading the table |
| `ReadPartitions()`, `ReadFiles()`, `ReadRowGroups()`, `ReadColumnChunks()`, `ReadDeleteFiles()`, `ReadCommits()` | A detail table, with all its parts concatenated in the order of the manifest. Empty when the file has none. |
| `ReadRaw(table)` | Any table, known or not, with every field as JSON values. Not kept. |

A table is a `PqaxTable<TRow>`: `Name`, `Count`, `Rows`, an indexer, `FindById(id)`, and `ExtraFields`, the fields that this version does not know, with one `JsonElement?` for each row. It is also an `IReadOnlyList<TRow>`.

A row is a class with one property for each field of the object model, named as in the file with a capital first letter: `ColumnRow.DataType` for `dataType`. The types:

| Type in the model | Type in C# |
|---|---|
| integer | `long?` |
| `id` | `long`, 0 when the file gives none |
| reference | `long?`, the `id` of the row it points to: `ColumnRow.Table`, `ColumnChunkRow.RowGroup` |
| list of references | `IReadOnlyList<long>?` |
| string | `string?` |
| boolean | `bool?` |
| timestamp | `DateTimeOffset?`, in UTC |
| list of string | `IReadOnlyList<string>?`. The format allows a null element only in `PartitionRow.Values`, for a null partition value. |
| map of string to string | `IReadOnlyDictionary<string, string>?` |
| `Distribution`, `DeltaDetails`, `IcebergDetails` | Classes of the same names. Their members that this version does not know are in `Extensions`. |

Rows, nested objects, and `CollectionInfo` have value equality: two rows are equal when every field is equal.

### What the reader tolerates

The reader is more tolerant than the validator.

| In the file | The reader |
|---|---|
| A byte order mark | Skips it |
| A field that a part leaves out | Gives null in the rows of that part |
| A value of another type than the model's, such as a string in an integer field | Gives null |
| `1.0` in an integer field | Gives 1 |
| A reference to a row that the file does not have | Keeps the number as written. `FindById` finds no row and gives null. |
| A field, table, entry, or member that this version does not know | Keeps it in `ExtraFields`, `ReadRaw`, or `Extensions`, and otherwise ignores it |
| A member of a table that is not an array | Ignores it, as a field of a later version |
| A manifest that states another number of rows than the arrays of a part hold | Gives the table the rows of its longest array in that part. The count of the manifest is never used to allocate rows. |
| A detail part that the manifest lists and the archive does not have | Fails the read of its table with `manifest.partMissing`. The other tables read. A missing `summary.json` gives empty summary tables. |

It refuses a file, with a `PqaxFormatException` whose `Code` is the code of the rule, only in these cases:

| Code | When |
|---|---|
| `container.manifestMissing` | The file is not a zip, or it has no `manifest.json` |
| `embedded.partMissing` | `OpenEmbedded`: the VPAX file has no part with the name of the footprint |
| `manifest.format` | The format is not `pqax` |
| `manifest.versionUnknown` | The first number of the format version is not 0 |
| `json.invalid` | A part that it reads is not valid JSON. For a detail part, the read of that table fails, not the open. |
| `manifest.partMissing` | A detail part that it reads is not in the archive. The read of that table fails, not the open. |
| `container.zip64`, `container.method`, `container.encrypted` | The archive needs ZIP64 to be read, or a part that it reads needs ZIP64, uses a compression other than deflate, or is encrypted |

## Write

```csharp
var builder = new FootprintBuilder(new CollectionInfo { CollectorApp = "MyTool", CollectorAppVersion = "1.0", Level = 2 });
long source = builder.AddSource(new SourceRow { Kind = "Storage folder", Name = "sales" });
long table = builder.AddTable(new TableRow { Source = source, Name = "orders", Format = "Delta", DetailDepth = "Files", FileCount = 1, FileSize = 1024 });
builder.AddColumn(new ColumnRow { Table = table, Name = "id", DataType = "long" });
builder.AddFile(new FileRow { Table = table, Path = "part-0.parquet", FileSize = 1024 });
builder.Write("orders.pqax");
```

There is one Add method for each of the 21 tables. For a table with ids, it keeps a positive id that the row brings, gives a row whose id is 0 the next free id, one more than the largest id so far, and returns the id. For a table without ids, it returns the position of the row, from 0. The caller puts the ids in the references of other rows. The builder keeps the row instance itself, so a property set after Add, such as the clustering columns of a table once its columns have ids, is written.

| The writer | Detail |
|---|---|
| Entries | `manifest.json` first, then `summary.json`, then one part for each detail table: `files.json`, or `columnChunks.1.json`, `columnChunks.2.json`, and so on |
| Parts | At most `Options.RowsPerPart` rows in a detail part, 250,000 by default and at most |
| JSON | Compact UTF-8 with no byte order mark. Non-ASCII text is written as is. |
| Fields | A field with no value in any row of a part is left out. An empty table is left out. `summary.json` is always there, `{}` when it has no rows. |
| Timestamps | `2026-10-05T08:15:30.000Z`, in UTC. Ticks below the millisecond are dropped. |
| Zip | Deflate at `Options.CompressionLevel`, `Optimal` by default. No ZIP64, no data descriptor. Every entry has the date of `Collection.CompletedAt`, so the same footprint gives the same bytes. |

`FootprintBuilder.From(document)` copies every table of a document, with its ids, the fields and tables that this version does not know, the members of the manifest and of nested objects that it does not know, the collection, and the obfuscation fields. A tool that rewrites a file starts there. The written file has format version 0.1, and its detail parts are named after their tables.

`Write` refuses a table with the same id in two rows, or with an id that is not positive: it throws `InvalidOperationException`, with the table and the id in the message, before it writes anything. Apart from the ids, the builder does not check what it writes. Validate the file when it matters.

## Validate

```csharp
ValidationResult result = PqaxValidator.Validate("footprint.pqax");
foreach (ValidationIssue issue in result.Issues)
{
    Console.WriteLine($"{issue.Code}: {issue.Message}");
}
```

| Member | What it does |
|---|---|
| `PqaxValidator.Validate(path)`, `Validate(stream)` | Checks a PQAX file |
| `PqaxValidator.ValidateEmbedded(vpaxPath, partName)`, `ValidateEmbedded(stream, partName)` | Checks the PQAX file inside a VPAX file |
| `ValidationResult` | `IsValid`, `Issues`, and `ChecksRun` |
| `ValidationIssue` | `Code`, `Message`, and when they apply `Part`, `Table`, and `Field` |

The codes are those of the [rules of the conformance kit](../../../conformance/rules.md): `container.*`, `json.*`, `manifest.*`, `part.*`, `table.*`, `depth.*`, and `totals.*`. The validator reports every issue instead of stopping at the first, with one issue for each rule, part, table, and field, and the count of the values in the message. It skips a rule that an earlier failure makes impossible to evaluate. A file that is not a zip is `container.manifestMissing`, and so is a VPAX file given to `Validate`. `ValidateEmbedded` reports `embedded.partMissing` when the VPAX file has no part with the name of the footprint. Messages hold numbers and names of the format, never a name or a value that comes from the data.

The validator throws only when the file cannot be read at all.

## Limits

| Limit | Detail |
|---|---|
| Size of a part | A part is read into memory whole, up to 1 GiB. A part of 250,000 column chunks is about 20 MB of JSON. |
| Memory | A row is an object. A column chunk takes about 270 bytes, so 600,000 column chunks hold about 155 MB. Equal strings and equal lists of strings, such as codecs and encodings, are shared. |
| Shared lists | Lists that the reader returns may be shared between rows. Do not cast them to arrays and change them. |
| CRC | The reader does not check the CRC of an entry. Deflate finds most damage, and a damaged stored part fails as JSON. |
| Integers | The writer does not check that an integer is within 2^53 - 1. The validator does. |
| Threads | The document and the builder are safe to use from several threads. Reads of the same document are serialized. |

## Time

Measured on 7 October 2026 on one Windows 11 machine, with a file of 600,000 column chunks in three parts, 30,000 row groups, and 10,000 files: 6.2 MB.

| Step | .NET 8 | .NET Framework 4.8 |
|---|---|---|
| Write with `FootprintBuilder` | 0.9 s | 1.7 s |
| Open: directory, manifest, and summary | 13 ms, 1.3 KB read | 57 ms |
| Read 600,000 column chunks, first time in the process | 0.7 to 0.9 s | 1.1 s |
| Read 600,000 column chunks, warm | 0.3 s | 1.2 s |
| Validate | 0.9 s | 2.2 s |

Of the 0.3 s of a warm read, 31 ms inflate the 55 MB of JSON, and the rest parses it into rows.
