# Pqax

Reads, writes, and validates PQAX files in .NET. A PQAX file holds the footprint of a set of Parquet tables: how much space the data takes and how it is arranged, without the data. It is a zip of JSON tables, defined by the [serialization](https://github.com/pqax-org/pqax/blob/main/docs/serialization.md) and the [object model](https://github.com/pqax-org/pqax/blob/main/docs/object-model.md) of PQAX, which the [conformance kit](https://github.com/pqax-org/pqax/blob/main/conformance/README.md) turns into rules, a schema, and samples.

| | |
|---|---|
| Targets | netstandard2.0, so that .NET Framework hosts can use it, and net8.0 |
| Dependencies | None on net8.0. On netstandard2.0, `System.Text.Json` 8.0.5 and the packages that it brings. |
| Reads | Format versions whose first number, the text before the first dot, is 0: `0.1` and `0.2`, and also `0.9`, `0`, or `0.1.2` |
| Writes | Format version `0.2` |
| Validates | Every rule of the conformance kit, with its code |

## Install

```
dotnet add package Pqax
```

The namespace is `Pqax`. From a clone of the repository, reference `src/dotnet/Pqax/Pqax.csproj` instead. A .NET Framework application needs binding redirects: read the section .NET Framework below.

## Open a file

```csharp
using Pqax;

// A PQAX file of its own.
using (PqaxDocument doc = PqaxDocument.Open("footprint.pqax"))
{
    Console.WriteLine($"PQAX {doc.Manifest.FormatVersion}, level {doc.Manifest.Collection.Level}, {doc.Summary.Tables.Count} tables");
}

// The footprint that a VPAX file holds as the part Footprint.pqax.
try
{
    using (PqaxDocument doc = PqaxDocument.OpenEmbedded("model.vpax"))
    {
        Console.WriteLine($"{doc.EmbeddedPart}, {doc.EmbeddedCompression}");
    }
}
catch (PqaxFormatException e) when (e.Code == "embedded.partMissing")
{
    Console.WriteLine("The VPAX file has no footprint.");
}
```

| Method | What it reads |
|---|---|
| `PqaxDocument.Open(path)`, `Open(stream, leaveOpen)` | The central directory of the zip, `manifest.json`, and `summary.json`. Nothing else. A stream that cannot seek is copied into memory first. |
| `PqaxDocument.OpenEmbedded(vpaxPath, partName)`, `OpenEmbedded(stream, partName, leaveOpen)` | The same, for the PQAX file that a VPAX file holds as the part `Footprint.pqax`. A stored part is read in place, a deflated part is inflated in memory first. `EmbeddedPart` gives the name of the part and `EmbeddedCompression` gives `stored` or `deflated`; both are null for a PQAX file of its own. |

## Read the tables

```csharp
using System.Linq;

using (PqaxDocument doc = PqaxDocument.OpenEmbedded("model.vpax"))
{
    foreach (TableRow table in doc.Summary.Tables)
    {
        Console.WriteLine($"{table.Name}: {table.Format}, {table.FileCount} files, {table.FileSize} bytes, depth {table.DetailDepth}");
        foreach (ColumnRow column in doc.Summary.Columns.Where(c => c.Table == table.Id))
        {
            Console.WriteLine($"  {column.Name} {column.DataType}: {column.CompressedSize} bytes");
        }
    }

    // RowCount reads nothing: it adds up the rows that the manifest states.
    if (doc.RowCount("columnChunks") > 0)
    {
        // A detail table is read from its parts on the first call, and then kept.
        PqaxTable<ColumnChunkRow> chunks = doc.ReadColumnChunks();
        Console.WriteLine($"{chunks.Count} column chunks, {chunks.Sum(c => c.CompressedSize ?? 0)} bytes");
    }
}
```

| Member of `PqaxDocument` | Tables | Read |
|---|---|---|
| `Summary` | The 15 summary tables, one property each: `Sources`, `Tables`, `Columns`, `PartitionSpecs`, `PartitionFields`, `SortOrders`, `SortFields`, `PhysicalSchemas`, `PhysicalColumns`, `Writers`, `Operations`, `Issues`, `ModelBindings`, `TableBindings`, `ColumnBindings` | When the document opens |
| `ReadPartitions()`, `ReadFiles()`, `ReadRowGroups()`, `ReadColumnChunks()`, `ReadDeleteFiles()`, `ReadCommits()` | The 6 detail tables, each with all its parts concatenated in the order of the manifest | On the first call, then kept |
| `ReadRaw(name)` | Any table, known or not, with every field as JSON values | On every call |
| `RowCount(name)` | The rows of any table, as the manifest states them | Never |
| `Manifest` | Format, version, collection, obfuscation fields, parts, and the members that this version does not know, in `Extensions` | When the document opens |

A table is a `PqaxTable<TRow>`, which is also an `IReadOnlyList<TRow>`: `Name`, `Count`, `Rows`, an indexer, `FindById(id)`, and `ExtraFields`, the fields that this version does not know, with one `JsonElement?` for each row. A table that the file does not have is empty.

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

Rows, nested objects, and `CollectionInfo` have value equality: two rows are equal when every field is equal. Lists that the reader returns may be shared between rows: do not cast them to arrays and change them.

The reader is more tolerant than the validator. A field that a part leaves out, or a value of another JSON type than the model's, reads as null. What this version does not know is kept in `ExtraFields`, `ReadRaw`, and `Extensions`, or ignored. The documentation of `PqaxDocument` has the details.

## Follow a reference

A reference holds the `id` of a row of another table. `FindById` finds that row, and gives null when the reference is null or the file has no such row. A table builds the index of its ids on the first call.

```csharp
using (PqaxDocument doc = PqaxDocument.Open("footprint.pqax"))
{
    PqaxTable<FileRow> files = doc.ReadFiles();
    PqaxTable<RowGroupRow> rowGroups = doc.ReadRowGroups();
    foreach (ColumnChunkRow chunk in doc.ReadColumnChunks())
    {
        var physical = doc.Summary.PhysicalColumns.FindById(chunk.PhysicalColumn);
        var column = doc.Summary.Columns.FindById(physical?.Column);
        var file = files.FindById(rowGroups.FindById(chunk.RowGroup)?.File);
        Console.WriteLine($"{file?.Path} {column?.Name}: {chunk.CompressedSize} bytes");
    }
}
```

There is no index in the other direction: the columns of a table are `doc.Summary.Columns.Where(c => c.Table == table.Id)`, as in the previous sample.

## Validate

```csharp
ValidationResult result = PqaxValidator.ValidateEmbedded("model.vpax");
Console.WriteLine(result);   // "valid, N checks" or "invalid, N issues in M checks"
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
| `ValidationIssue` | `Code`, `Message`, and when they apply `Part`, `Table`, and `Field`. `ToString()` gives them on one line. |

The codes are those of the [rules of the conformance kit](https://github.com/pqax-org/pqax/blob/main/conformance/rules.md): `container.*`, `json.*`, `manifest.*`, `part.*`, `table.*`, `depth.*`, and `totals.*`, and `embedded.partMissing` when a VPAX file has no part with the name of the footprint. The validator reports every issue instead of stopping at the first, with one issue for each rule, part, table, and field, and the count of the values in the message. It skips a rule that an earlier failure makes impossible to evaluate. A file that is not a zip is `container.manifestMissing`, and so is a VPAX file given to `Validate`. Messages hold numbers and names of the format, never a name or a value that comes from the data.

The validator never throws `PqaxFormatException`: it throws only for a null argument, and when the file cannot be read at all. A file does not have to be valid to be read: the reader opens most invalid files.

## Measure the tables, the columns, and the collection

`StorageMeasures` computes every derived measure of [section 10 of the object model](https://github.com/pqax-org/pqax/blob/main/docs/object-model.md#10-derived-measures) for the tables, the columns, and the collection, with the names and the numbers of the [storage views](https://github.com/pqax-org/pqax/blob/main/src/typescript/views/README.md): the storage stack with its waste, its overhead, and the share of each component, the file sizes, the small files, and the V-Order shares, the history with VACUUM and OPTIMIZE, the ratios, the partitions, the measures of each column, and the totals of the collection. Only the two measures over a set of columns, which the treemap of the views draws, are not here.

```csharp
using PqaxDocument doc = PqaxDocument.Open("footprint.pqax");
FootprintMeasures measures = StorageMeasures.Compute(doc);       // reads the files and partitions tables when the file has them
foreach (TableStorage table in measures.Tables.OrderByDescending(t => t.Stack.Waste))
{
    Console.WriteLine($"{table.Label}: {table.Stack.Waste:N0} bytes of waste, {table.Stack.WasteShare:P1} of the data, " +
        $"median file {table.Files.Median:N0} bytes, V-Ordered {table.Files.VOrderShare:P0}, last VACUUM {table.Maintenance.LastVacuumAt}");
}
foreach (ColumnStorage column in measures.Columns.Where(c => c.NullShare > 0.5))
{
    Console.WriteLine($"{column.Label}: {column.NullShare:P0} null");
}
Console.WriteLine($"{measures.Collection.Tables} tables, {measures.Collection.Footprint:N0} bytes stored");
```

| Member | What it is |
|---|---|
| `StorageMeasures.Compute(document, options)` | The measures of every table, column, and of the collection, from the summary and the `files` and `partitions` tables. A detail table that cannot be read becomes a warning, and the measures use the totals of the summary. |
| `StorageMeasures.ForTables(summary, files, collection, options, partitions)` | The measures of the tables, for a caller that read the tables |
| `StorageMeasures.ForColumns(summary)`, `ForCollection(summary, tables)` | The measures of the columns, and those of the collection from the tables that `ForTables` returned |
| `StorageMeasures.StackOf`, `DeletedEstimate`, `SmallShareAtLeast`, `CommitsPerDay`, `DaysBetween`, `Ratio` | The derived values, one function each, as `stackOf`, `deletedEstimate`, `smallShareAtLeast`, `commitsPerDay`, `daysBetween`, and `ratio` of the views |
| `StorageMeasures.StackComponents`, `WasteComponents`, `OverheadComponents` | The names of the components: `live`, `nonLive`, `unreferenced`, `deleted`, `stale`, `metadata`, `footers`; the first four after `live` are the waste, the last two the overhead |
| `FootprintMeasures` | `Tables`, `Columns`, `Collection`, `FilesRead`, `Warnings` |
| `TableStorage` | `Id`, `Row`, `Name`, `Label`, `RowCount`, `FileCount`, `FileSize`, `Stack`, `SmallFiles`, `TinyFiles`, `Files`, `Measures`, `Maintenance`, `Partitions`, and `WriteTo(Utf8JsonWriter)`, which writes the JSON of `pqax tables --json` for one table |
| `StorageStack` | The seven components, `DeletedSource`, `Listed`, `Data`, `Total`, `Waste`, `Overhead`, `WasteShare`, `OverheadShare`, `Component(key)`, and `Share(key)`, the share of a component |
| `SmallFiles` | `Threshold`, `Count` from the `files` table, `ShareAtLeast` |
| `FileMeasures` | `Count`, `Sum`, `Minimum`, `FirstQuartile`, `Median`, `ThirdQuartile`, `Maximum`, `VOrderShare`, `VOrderBytesShare` |
| `TableMeasures` | `CompressionRatio`, `DeletedRowShare`, `AverageFileSize`, `AverageRowGroupRows`, `FooterCoverage`, `StaleColumnShare`, `NonLiveShare`, `AveragePartitionSize`, `DictionaryFallbackShare` |
| `MaintenanceMeasures` | `LastCommitAt`, `CommitsPerDay`, `DaysSinceLastCommit`, `HasVacuum`, `LastVacuumAt`, `HasOptimize`, `LastOptimizeAt` |
| `PartitionMeasures` | `Count`, `Kept`, `OldestModifiedAt`, `NewestModifiedAt` |
| `ColumnStorage` | `Id`, `Row`, `Table`, `Name`, `Label`, `NullShare`, `CompressionRatio`, `DictionaryShare`, `DictionaryFallbackShare`, `AverageValueLength`, `FilesWithoutColumnShare` |
| `CollectionMeasures` | `Sources`, `Tables`, `Columns`, `Files`, `FileSize`, `RowCount`, `Footprint`, `Waste`, `Overhead`, `Depths` |
| `MeasureOptions` | `SmallFileSize`, 128 MiB by default, and `TinyFileSize`, 8 MiB |

A value that cannot be computed is `null`, never 0: a stack component whose field the file lacks, such as the unreferenced files of a file of format 0.1, is `null`. The waste, the overhead, and the total count such a component as 0, as the views do, and are `null` only when every component they add is absent. The conformance kit holds the measures of the views for every sample, in `conformance/expected/measures`, and the tests compare these with them.

## Compare two footprints

`StorageDiff` compares two footprints of the same lakehouse taken at different times, as [section 13 of the object model](https://github.com/pqax-org/pqax/blob/main/docs/object-model.md#13-comparing-two-footprints) proposes: the tables and the columns added, removed, and kept, and for every kept one the older value, the newer value, and the delta of each total of section 6 and of each measure of section 10. A table is the same table when its `TableId` is, and otherwise, when one of the two has none, when its source, namespace, and name are; a column when its `FieldId` is, and otherwise by name; a partition by its values, a file by its path, and a commit by its `CommitId`.

```csharp
using PqaxDocument older = PqaxDocument.Open("monday.pqax");
using PqaxDocument newer = PqaxDocument.Open("friday.pqax");
FootprintDiff diff = StorageDiff.Compare(older, newer);
foreach (TableChange change in StorageDiff.Rank(diff, "total", top: 10))
{
    if (change.Kept is { } kept)
    {
        Console.WriteLine($"{kept.Newer.Label}: {kept.Measure("stack.total")!.Delta:N0} bytes, {kept.Files?.Added} files added, {kept.Columns.Added.Count} columns added");
    }
    else
    {
        Console.WriteLine($"{change.Label} {change.Kind}{(change.Entry!.Recreated ? ", recreated" : string.Empty)}: {change.Entry.Table.Total:N0} bytes");
    }
}
```

| Member | What it is |
|---|---|
| `StorageDiff.Compare(older, newer, options)` | The comparison. It computes the measures of both documents with `StorageMeasures.Compute`, and reads their `files`, `partitions`, and `commits` tables when they have rows. |
| `StorageDiff.Rank(diff, order, top)` | The changes in one order, as `TableChange`: `total`, `waste`, `rows`, or `files` by the size of the change, the largest first, or `name` |
| `StorageDiff.Orders` | `total`, `waste`, `rows`, `files`, `name` |
| `FootprintDiff` | `Older` and `Newer`, the measures of each document; `SmallFileSize`, `TinyFileSize`; `Collection`, the change of the measures of the collection and of the tables at each depth; `Added`, `Removed`, `Kept`; `ChangedCount`; `CollectionValue(name)` |
| `ValueChange` | `Name`, `Older`, `Newer`, `Delta`, `IsChanged`. A value is a `long`, a `double`, a `bool`, a `string`, a `DateTimeOffset`, a list of strings, or null. The delta of a time is in milliseconds; a boolean, a text, and a list have none, and the delta is null when either value is. |
| `TableEntry` | A table added or removed: `Table` and `Recreated`, true when a table with the same source, namespace, and name was removed and added with another `TableId` |
| `TableSide` | `Id`, `Row`, `Label`, `Name`, `Namespace`, `TableId`, `SnapshotId`, `DetailDepth`, `ColumnCount`, `FileCount`, `FileSize`, `RowCount`, and the `Total` and `Waste` of its stack |
| `KeptTable` | `MatchedBy` (`tableId` or `name`), `Renamed`, `IsChanged`, `Older`, `Newer`, `Totals`, `Measures`, `Columns`, and `Files`, `Partitions`, `Commits`, null when the two footprints do not both keep them; `Total(name)` and `Measure(name)` |
| `ColumnChanges` | `Added` and `Removed`, as `ColumnSide`, and `Kept`, as `KeptColumn` |
| `ColumnSide`, `KeptColumn` | `Id`, `Row`, `Label`, `Name`, `FieldId`, `DataType`, `IsNullable`, `CompressedSize`; and `MatchedBy` (`fieldId` or `name`), `Renamed`, `IsChanged`, `Older`, `Newer`, `Totals`, `Measures` |
| `DetailChanges` | `Added`, `Removed`, `Kept`, and for the files and the partitions `Changed`, `AddedFileSize`, and `RemovedFileSize` |
| `TableChange` | `Kind` (`added`, `removed`, `kept`), `Entry` or `Kept`, and `Label` |

A kept table is changed when its name, its `SnapshotId`, a total, a column, a file, a partition, or a commit differs; a measure alone does not make it changed, since the days since the last commit move with the time of the collection. The totals keep the names of the fields, with the members of a distribution as `fileSizes.median`, and the measures the paths of the JSON of the measures, such as `stack.waste`. `pqax diff` of [Pqax.Cli](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Cli/README.md#diff) writes the comparison as a report or as JSON. The conformance kit has two footprints with known differences and their expected diff, in `conformance/expected/diff`, and the tests compare the comparison with it.

## Write

```csharp
var builder = new FootprintBuilder(new CollectionInfo { CollectorApp = "MyTool", CollectorAppVersion = "1.0", Level = 2, CompletedAt = DateTimeOffset.UtcNow });
long source = builder.AddSource(new SourceRow { Kind = "Storage folder", Name = "sales" });
long table = builder.AddTable(new TableRow { Source = source, Name = "orders", Format = "Delta", DetailDepth = "Files", FileCount = 1, FileSize = 1024 });
builder.AddColumn(new ColumnRow { Table = table, Name = "id", DataType = "long" });
builder.AddFile(new FileRow { Table = table, Path = "part-0.parquet", FileSize = 1024 });
builder.Write("orders.pqax");
```

There is one Add method for each of the 21 tables. For a table with ids, it keeps a positive id that the row brings, gives a row whose id is 0 the next free id, one more than the largest id so far, and returns the id. For a table without ids, it returns the position of the row, from 0. The caller puts the ids in the references of other rows. The builder keeps the row instance itself, so a property set after Add, such as the clustering columns of a table once its columns have ids, is written.

`Write` refuses a table with the same id in two rows, or with an id that is not positive: it throws `InvalidOperationException`, with the table and the id in the message, before it writes anything. Apart from the ids, the builder does not check what it writes. Validate the file when it matters.

The file has `manifest.json`, then `summary.json`, then the parts of the detail tables, with at most `Options.RowsPerPart` rows in a part: 250,000, the most that the format allows, by default. The JSON is compact UTF-8, deflated at `Options.CompressionLevel`, `Optimal` by default. Every entry has the date of `Collection.CompletedAt`, so the same footprint gives the same bytes on the same .NET runtime. The documentation of `FootprintBuilder` has the details.

`FootprintBuilder.From(document)` copies every table of a document, with its ids, the fields and tables that this version does not know, the members of the manifest and of nested objects that it does not know, the collection, and the obfuscation fields. A tool that rewrites a file starts there. The written file has format version 0.2, also when the document is a 0.1 file, and its detail parts are named after their tables.

## Embed a footprint in a VPAX file

The package does not write VPAX files. A VPAX file is an Open Packaging Conventions package, and `System.IO.Packaging` adds the footprint to it as the part `/Footprint.pqax`, with the content type `application/zip`. Store the part without compression, so that a reader opens it in place.

```csharp
using System.IO.Packaging;

static void AddFootprint(string vpaxPath, FootprintBuilder builder)
{
    var uri = new Uri("/" + PqaxDocument.FootprintPartName, UriKind.Relative);
    using (Package package = Package.Open(vpaxPath, FileMode.Open, FileAccess.ReadWrite))
    {
        if (package.PartExists(uri))
        {
            package.DeletePart(uri);
        }

        PackagePart part = package.CreatePart(uri, "application/zip", CompressionOption.NotCompressed);
        using (Stream stream = part.GetStream())
        {
            builder.Write(stream);
        }
    }
}
```

The part holds the same bytes as a `.pqax` file, so copying a file into the part works too. `System.IO.Packaging` is a NuGet package on .NET 8, and part of `WindowsBase` on .NET Framework.

## Threads and disposal

| Object | Threads | Lifetime |
|---|---|---|
| `PqaxDocument` | Every member is safe from several threads. The reads of one document are serialized, so two threads that read two detail tables wait for each other. | `Dispose` closes the file, or the stream unless `leaveOpen` is true. `Manifest`, `Summary`, `RowCount`, and the detail tables already read stay usable. A detail table not read yet, and `ReadRaw`, then throw `ObjectDisposedException`. |
| `PqaxTable<TRow>` and rows | A table is safe to read from several threads, `FindById` included. Rows are plain mutable objects, without locks. | A table outlives its document. |
| `FootprintBuilder` | The Add methods and `Write` are safe from several threads. Do not change a row while `Write` runs. | Nothing to dispose. `Write` can run more than once. |
| `PqaxValidator` | Static, with no shared state | It closes the file that it opens, and leaves a stream open. |

A document opened from a path keeps the file open, shared with readers only, until it is disposed: dispose it before the file is written again. A footprint stored in a VPAX file keeps the VPAX file open the same way; a deflated one is inflated, and the VPAX file is closed at once. A document opened from a stream moves the position of the stream when it reads, so do not use that stream elsewhere while the document is open.

The I/O is synchronous. In an application with a user interface, open and read on a background thread: opening takes milliseconds, and reading 600,000 column chunks about a second, as the section Time shows.

## .NET Framework

A .NET Framework application uses the netstandard2.0 build. The section Time has figures measured on .NET Framework 4.8.

| Topic | Detail |
|---|---|
| Dependencies | `System.Text.Json` 8.0.5, which brings `Microsoft.Bcl.AsyncInterfaces`, `System.Buffers`, `System.Memory`, `System.Numerics.Vectors`, `System.Runtime.CompilerServices.Unsafe`, `System.Text.Encodings.Web`, `System.Threading.Tasks.Extensions`, and `System.ValueTuple`. They are copied next to the application. |
| Binding redirects | Required. `Pqax` references `System.Text.Json` 8.0.0.0, and the package installs 8.0.0.5 on .NET Framework. `System.Memory` references an older `System.Runtime.CompilerServices.Unsafe` than the one installed. Without the redirects, `Pqax` fails with `FileLoadException` as soon as it needs `System.Text.Json`. |
| API | No `Span` or `Memory`: strings, streams, nullable numbers, read-only lists and dictionaries, and `JsonElement` only for what this version does not know |
| C# | The samples of this page compile with C# 7.3, the default for .NET Framework |

An SDK-style project that builds an application generates the redirects, because `AutoGenerateBindingRedirects` is on by default for it. Another project adds them to its `app.config`. With no other package that brings later versions of these assemblies, they are:

```xml
<dependentAssembly>
  <assemblyIdentity name="System.Text.Json" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-8.0.0.5" newVersion="8.0.0.5" />
</dependentAssembly>
<dependentAssembly>
  <assemblyIdentity name="System.Runtime.CompilerServices.Unsafe" publicKeyToken="b03f5f7f11d50a3a" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-6.0.0.0" newVersion="6.0.0.0" />
</dependentAssembly>
```

A library that another application loads, such as a plug-in, runs with the redirects of that application.

## Errors

| Exception | When |
|---|---|
| `PqaxFormatException` | The reader refuses a file, or a part of it. `Code` is the code of the rule, as in the next table. |
| `ArgumentNullException` | An argument is null |
| `IOException`, such as `FileNotFoundException`, and `UnauthorizedAccessException` | A file or a stream cannot be opened, read, or written |
| `ObjectDisposedException` | A read, after `Dispose`, of a table that the document had not read before |
| `InvalidOperationException` | `Write` finds an id that repeats in a table, or that is not positive |
| `ArgumentException` | A path is empty, or `Write` gets a stream that cannot write |
| `ArgumentOutOfRangeException` | `Options.RowsPerPart` outside 1 to 250,000, or an index outside a table |

The reader refuses a file with a `PqaxFormatException` only in these cases:

| Code | When |
|---|---|
| `container.manifestMissing` | The file is not a zip, or it has no `manifest.json`. For `OpenEmbedded`, also a VPAX file that is not a zip, or a part that cannot be inflated. |
| `embedded.partMissing` | `OpenEmbedded`: the VPAX file has no part with the name of the footprint |
| `manifest.format` | The format is not `pqax` |
| `manifest.versionUnknown` | The first number of the format version is not 0 |
| `json.invalid` | A part that it reads is damaged or is not valid JSON. For a detail part, the read of that table fails, not the open. |
| `manifest.partMissing` | A detail part that it reads is not in the archive. The read of that table fails, not the open. |
| `container.zip64`, `container.method`, `container.encrypted` | The archive needs ZIP64 to be read, or a part that it reads needs ZIP64, uses a compression other than deflate, or is encrypted |

## What the package does not do

| Not in the package | Where it is |
|---|---|
| Collect a footprint from a lakehouse or a folder, or read Parquet and Delta files | The package `Pqax.Collector` |
| The tree of the treemap, and the two measures over a set of columns that it draws | The view model of [`@pqax/views`](https://github.com/pqax-org/pqax/blob/main/src/typescript/views/README.md). `StorageMeasures` has every other measure. |
| Write a VPAX file, or embed a footprint in one | `System.IO.Packaging`, as above, and later the VPAX library |
| Obfuscate names | Later, in the VPAX obfuscator, from `FootprintBuilder.From` |
| Asynchronous I/O | Not in 0.2 |
| Check the CRC of the zip entries | Not done. Deflate finds most damage, and a damaged stored part fails as JSON. |
| A command line | The package `Pqax.Cli`: `pqax check`, `pqax info`, and `pqax tables` |

## Limits

| Limit | Detail |
|---|---|
| Size of a part | A part is read into memory whole, up to 1 GiB. A larger part fails with `json.invalid`. A part of 250,000 column chunks is about 20 MB of JSON. |
| Memory | A row is an object. A column chunk takes about 270 bytes, so 600,000 column chunks hold about 155 MB. Equal strings and equal lists of strings, such as codecs and encodings, are shared. |
| Integers | The writer does not check that an integer is within 2^53 - 1. The validator does. |

## Time

Measured on 7 October 2026 on one Windows 11 machine, with a file of 600,000 column chunks in three parts, 30,000 row groups, and 10,000 files: 6.2 MB.

| Step | .NET 8 | .NET Framework 4.8 |
|---|---|---|
| Write with `FootprintBuilder` | 0.9 s | 1.7 s |
| Open: directory, manifest, and summary | 13 ms, 1.3 KB read | 57 ms |
| Read 600,000 column chunks, first time in the process | 0.7 to 0.9 s | 1.1 s |
| Read 600,000 column chunks, warm | 0.3 s | 1.2 s |
| Validate | 0.9 s | 2.2 s |
