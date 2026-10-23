# pqax for Python

`pqax` reads, validates, and writes PQAX files: the footprint of Parquet tables, as a zip of JSON tables. The format is defined by the [serialization](https://github.com/pqax-org/pqax/blob/main/docs/serialization.md) and the [object model](https://github.com/pqax-org/pqax/blob/main/docs/object-model.md), and the validation rules by the [conformance kit](https://github.com/pqax-org/pqax/blob/main/conformance/rules.md).

| | |
|---|---|
| Version | 0.2.0 |
| Format | Reads 0.1 and 0.2, writes 0.2. Reads every other version 0.x. |
| Python | 3.10 or later |
| Dependencies | None. `to_dataframe` uses pandas, or polars 1.0 or later, when one is installed. `export` writes Parquet with pyarrow and Delta with deltalake, when they are installed. |

## Install

```bash
pip install pqax                       # the package and the pqax command
pip install "pqax[pandas]"             # with pandas, for to_dataframe
pip install "pqax[polars]"             # with polars, for to_dataframe
pip install "pqax[parquet]"            # with pyarrow, for export to Parquet
pip install "pqax[delta]"              # with deltalake and pyarrow, for export to Delta
```

From the root of the repository:

```bash
pip install -e src/python              # the package and the pqax command
pip install -e "src/python[pandas]"    # with pandas, for to_dataframe
pip install -e "src/python[dev]"       # with pytest and jsonschema, for the tests
```

Python versions: 3.10 or later. The tests run on 3.10 and 3.14, each without extras and with both pandas and polars. The package has no dependency: the `pandas` and `polars` extras only install the library that `to_dataframe` uses, and the `parquet` and `delta` extras the libraries that `export` uses. The tests run with pyarrow and deltalake where they run with pandas and polars.

## Read a file

```python
import pqax

with pqax.open("footprint.pqax") as doc:
    print(doc.manifest["formatVersion"], doc.part_names)
    tables = doc.summary["tables"]               # read when the file opens
    for table_id, size in zip(tables["id"], tables.get("fileSize", [])):
        print(table_id, size)
    chunks = doc.table("columnChunks")           # read now, from all its parts, and kept
    print(doc.row_count("columnChunks"), sum(chunks["compressedSize"]))
    for row in doc.rows("files"):                # one dictionary for each row
        print(row["id"], row["fileSize"])
```

A table is a dictionary of fields, and a field is a list with one value for each row, as in the file. The keys keep the names of the file, such as `fileCount`. A value that the file does not have is `None`.

- Opening a file reads the zip directory, `manifest.json`, and `summary.json`. A detail part is read the first time a table that it holds is asked for.
- `doc.table(name)` concatenates the parts of a table in the order of the manifest. A field that a part leaves out is `None` in the rows of that part. A table that the file does not have is `{}`.
- A field that has no value in any row is not in the dictionary: use `table.get("field")`.
- A value whose JSON type is not the type of its field, such as a string where the model says integer, reads as `None`. `doc.raw(name)` returns the table as parsed, with that value and with the members of a table that are not arrays.
- Fields and tables that this version does not define are returned like the others. Members of the manifest that it does not define stay in `doc.manifest`.

A PQAX file embedded in a VPAX file:

```python
with pqax.open_embedded("model.vpax") as doc:    # the part Footprint.pqax, stored or deflated
    print(doc.embedded_part, doc.embedded_compression)
```

The part is read into memory, and the VPAX file is closed before `open_embedded` returns.

## Validate a file

```python
result = pqax.validate("footprint.pqax")         # or pqax.validate_embedded("model.vpax")
print(result.is_valid, result.checks)
for issue in result.issues:
    print(issue.code, issue.message, issue.part, issue.table, issue.field)
```

The validator reports every issue that it finds, with the codes of section 4.1 of the [contracts](https://github.com/pqax-org/pqax/blob/main/docs/contracts.md): `container.*`, `json.*`, `manifest.*`, `part.*`, `table.*`, `depth.*`, `totals.*`, and, for `validate_embedded`, `embedded.partMissing`. A rule that cannot be evaluated because an earlier one failed is skipped. A file that cannot be opened raises `OSError`. A file that is not a zip archive is an issue, `container.manifestMissing`, and so is a VPAX file given to `validate`.

## Data frames

```python
frame = pqax.to_dataframe(doc.table("columnChunks"))             # pandas when installed, else polars
frame = pqax.to_dataframe(doc.summary["tables"], library="polars")
```

With pandas, an integer or boolean column with `None` values gets a nullable type, so it does not turn into floats. Lists and objects stay as Python objects. Timestamps stay strings; `pqax.parse_timestamp` turns one into a `datetime`. The rows of `doc.rows(name)` give the same frame as `doc.table(name)`.

`to_dataframe` needs polars 1.0 or later, and raises `ImportError` with an earlier version, which loses the values of a column that mixes types.

## Write a file

```python
from datetime import datetime, timezone

builder = pqax.Builder({"collectorApp": "my-tool", "level": 2, "startedAt": datetime.now(timezone.utc)})
source = builder.add_source(kind="Storage folder")
table = builder.add_table(source=source, name="sales", format="Delta", detailDepth="Files")
file_id = builder.add_file(table=table, path="part-0001.parquet", fileSize=47820)
builder.update("tables", table, fileCount=1, fileSize=47820)
builder.write("footprint.pqax")
```

| Method | What it does |
|---|---|
| `add_source(**fields)`, `add_table`, `add_column`, and one for each of the 21 tables | Adds a row. For a table with an `id`, it keeps a positive id that the row brings, assigns the next free id when the row has none (greater than every id so far in the table, so from 1), and returns the id. For a table without, it returns the position of the row, from 0. The plural names, such as `add_files`, are the same methods. |
| `add(table, **fields)` | Adds a row to any table, also one that this version does not define, with any field |
| `extend(table, columns, detail=None)` | Adds many rows from a columnar table. `detail` places a table that the format does not define. |
| `update(table, id_or_position, **fields)` | Sets fields of a row already added, such as totals that are known after the detail |
| `write(path_or_file)` | Writes the file. It raises `ValueError`, naming the table and the id, when a table has an id in more than one row. |
| `Builder.from_document(doc)` | A builder with every table of a document, to rewrite a file |

The fields are named as in the file. An `add_` method refuses a field that its table does not have, unless the name has a producer's prefix and an underscore, as in `acme_note`. A field set to `None` has no value. A `datetime` becomes a timestamp in UTC with three decimals; a naive `datetime` is taken as UTC.

`write` lays out the file as the serialization says: `manifest.json` first, then `summary.json`, always present, then one part for each detail table, named after it, as `files.json`. A table with more than `rows_per_part` rows, 250,000 by default and at most, continues in parts numbered from 1, as `columnChunks.1.json`. A field with no value in any row of a part is left out of that part, and so is a table with no rows. The JSON is compact UTF-8 with no byte order mark, the entries are deflated at level 9 by default (`compression_level`), and every entry has the same date, so the same footprint gives the same bytes. A path is written through a temporary file.

`from_document` keeps the ids, the collection, the obfuscation fields, the other members of the manifest, and the fields and tables that this version does not define. It loses what the reader does not keep: the entries that the manifest does not list, the members of a table that are not arrays, and the members of an element of `parts` other than `name` and `tables`. It writes format version 0.2, and its own part names.

## Measure the tables, the columns, and the collection

`pqax.measures` computes every derived measure of [section 10 of the object model](https://github.com/pqax-org/pqax/blob/main/docs/object-model.md#10-derived-measures) for the tables, the columns, and the collection, with the names and the numbers of the [storage views](https://github.com/pqax-org/pqax/blob/main/src/typescript/views/README.md): the storage stack with its waste, its overhead, and the share of each component, the file sizes, the small files, and the V-Order shares, the history with VACUUM and OPTIMIZE, the ratios, the partitions, the measures of each column, and the totals of the collection. Only the two measures over a set of columns, which the treemap of the views draws, are not here.

```python
import pqax

with pqax.open("footprint.pqax") as doc:
    result = pqax.measures.measure(doc)          # reads the files and partitions tables when the file has them
for table in sorted(result["tables"], key=lambda t: -(t["stack"]["waste"] or 0)):
    print(table["label"], table["stack"]["waste"], table["stack"]["wasteShare"],
          table["files"]["median"], table["maintenance"]["lastVacuumAt"], table["partitions"]["count"])
print([column["label"] for column in result["columns"] if (column["nullShare"] or 0) > 0.5])
print(result["collection"]["totals"]["footprint"], result["collection"]["depths"])
```

| Name | What it is |
|---|---|
| `measure(doc, small_file_size=..., tiny_file_size=...)` | `{"filesRead": ..., "warnings": [...], "tables": [...], "columns": [...], "collection": {...}}`. A `files` or `partitions` table that cannot be read becomes a warning, and the measures use the totals of the summary. |
| `measure_tables(summary, files=None, collection=None, partitions=None, ...)` | The list of the tables, from tables already read |
| `measure_columns(summary)`, `measure_collection(summary, tables)` | The list of the columns, and the collection from the tables that `measure_tables` returned |
| `stack_of`, `deleted_estimate`, `small_share_at_least`, `commits_per_day`, `days_between`, `ratio` | The derived values, one function each, as `stackOf`, `deletedEstimate`, `smallShareAtLeast`, `commitsPerDay`, `daysBetween`, and `ratio` of the views |
| `STACK_COMPONENTS`, `WASTE_COMPONENTS`, `OVERHEAD_COMPONENTS` | The names of the components |
| `DEFAULT_SMALL_FILE_SIZE`, `DEFAULT_TINY_FILE_SIZE` | 128 MiB and 8 MiB |

The keys are the names of the view model. The dict of a table has `id`, `name`, `label`, `rowCount`, `fileCount`, `fileSize`, `stack` with `shares`, `smallFiles`, `tinyFiles`, `files` with `vOrder`, `measures`, `maintenance`, and `partitions`; `python -m pqax tables --json` prints the keys it printed in version 0.2.0. A column has `id`, `table`, `name`, `label`, and its six measures, and the collection `totals` and `depths`. A value that cannot be computed is `None`, never 0; the waste, the overhead, and the total count a missing component as 0, as the views do, and are `None` only when every component they add is missing. An integral float is an `int`, and a time is the text of the file. The conformance kit holds the measures of the views for every sample, in `conformance/expected/measures`, and the tests compare these with them.

## Command line

```bash
python -m pqax check footprint.pqax      # exit code 0 when valid, 1 when not, 2 on a usage or I/O error
python -m pqax info footprint.pqax       # format, version, collection, parts with their tables and rows
python -m pqax info model.vpax           # a VPAX file: the PQAX file that it embeds as Footprint.pqax
python -m pqax tables footprint.pqax     # one row per table: rows, files, live bytes, waste, and waste share
python -m pqax export footprint.pqax --to exports   # the tables and their measures as CSV, Parquet, or Delta
```

`check` prints one line for each issue, its code first, then a summary line. The commands take `--part NAME` for another embedded part. `tables` takes `--sort waste|total|rows|files|name`, `--top N`, `--wide` for the small files and the overhead, and `--json` for one JSON object; it writes what `pqax tables` of [Pqax.Cli](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Cli/README.md#tables) writes. The installed package also has a `pqax` command.

## Export the tables, day after day

`pqax.export` writes the tables of a footprint and its derived measures as CSV, Parquet, or Delta, so that a BI tool or a lakehouse can load them. The exports of several days go into the same folder and append to the same tables, and each row says which footprint it comes from. The [Power BI template](https://github.com/pqax-org/pqax/blob/main/templates/powerbi/README.md) reads them.

```bash
python -m pqax export footprint.pqax --to exports                    # CSV, with the standard library only
python -m pqax export footprint.pqax --to exports --format parquet   # needs pqax[parquet]
python -m pqax export model.vpax --to Tables --format delta          # needs pqax[delta]; the embedded footprint
```

```python
from pqax import export

result = export.export("footprint.pqax", "exports", format="parquet")
print(result["footprintId"], result["collectedAt"], result["tables"])
```

| Extra | Installs | For |
|---|---|---|
| `pqax[parquet]` | pyarrow 16 or later | `--format parquet` |
| `pqax[delta]` | deltalake 1.0 or later, and pyarrow | `--format delta` |

Without the library, the export raises `ImportError` naming the extra, and the command exits with 2, before anything is written.

The output tables:

| Table | One row for each |
|---|---|
| `sources`, `tables`, `columns`, ..., `files`, `rowGroups`, `columnChunks`, `commits` | Row of that table of the format that the file has: every field of `pqax.FIELDS[name]`, with the names of the file, also those the file lacks |
| `tableMeasures` | Table, with the measures of `measure_tables` |
| `columnMeasures` | Column, with the measures of `measure_columns` |
| `collectionMeasures` | Footprint, with the measures of `measure_collection` |
| `footprints` | Footprint: the file, the format version, and the members of `collection` and of the obfuscation |

Every row starts with `footprintId` and `collectedAt`. `export.COLUMNS` lists the columns of each table, and `export.SCHEMAS` their kinds.

- **footprintId** is the SHA-256 of the bytes of the PQAX file, in lowercase hex; for a VPAX, of the PQAX file that it embeds, which has the same bytes as the file on its own. The id comes from the content and from nothing else: no collector has to assign one, the same footprint has the same id wherever it is copied or embedded, and a different collection always has another id, since its manifest holds its times. That is what lets the export refuse a footprint twice.
- **collectedAt** is `completedAt` of the collection, else `startedAt`, as `collectedAt` of `pqax tables --json`. `footprints` also has `collectionDate`, its date in UTC.
- **Names.** A table of the format and its fields keep the names of the file. A measure takes its name in section 10 of the object model, with a lower-case first letter, as rule 5 of section 3 of the contracts says for the keys of a table: `live`, `wasteShare`, `smallFileShare`, `vOrderShare`, `lastCommitAt`, `partitionsKept`. Section 10 does not name the share of each stack component, and NonLiveShare is already a measure, so those shares are `liveDataShare`, `nonLiveDataShare`, and so on: the component over Data. The file size figures are `fileSizesCount`, `fileSizesSum`, `fileSizesMinimum`, `fileSizesFirstQuartile`, `fileSizesMedian`, `fileSizesThirdQuartile`, and `fileSizesMaximum`. `tableMeasures` also has `sourceName` and some fields of its table, shown beside the stack: `detailDepth`, `nonLiveFileCount`, `nonLiveOldestRemovedAt`, `nonLiveNewestRemovedAt`, `unreferencedFileCount`. `columnMeasures` has `tableLabel`.
- **Types.** The type of a column comes from the format, not from the values, so that the files of every day have the same schema. Integers are int64, the measures float64, the booleans bool, the timestamps UTC timestamps in microseconds, and `collectionDate` a date. A list, a map, or a nested object, such as `namespace`, `properties`, or `fileSizes`, is its compact JSON text. A timestamp that does not parse is null.
- **Left out.** The tables and the fields that the format does not define, such as those of a producer, `acme_note`, are not exported; the result lists the tables in `ignored`, and the command prints them. A table with no rows is not written.

The folder:

| Format | Each table | A second export |
|---|---|---|
| CSV | `<folder>/<table>.csv`: UTF-8, a header, commas, `\r\n`; null is an empty field, an empty string `""`, a boolean `true` or `false`, a timestamp `2026-10-05T08:15:30.000Z` | Appends the rows, with no second header. A file whose header differs, from another version, is refused before anything is written. |
| Parquet | `<folder>/<table>/<footprintId>.parquet` | Adds one file per table |
| Delta | `<folder>/<table>/`, a Delta table written with deltalake | Appends one commit per table, merging a new column of a later version |

A footprint whose id the `footprints` table of the folder already has is refused with `AlreadyExportedError`, code `export.duplicate`, and exit code 1, and nothing is written; `if_exists="skip"`, or `--if-exists skip`, writes nothing and returns, with exit code 0. `footprints` is written last, so a footprint in it was exported whole. An export that fails half way, on a full disk for example, can leave rows of the footprint in the CSV files or the Delta tables written before; the Parquet files of a footprint are replaced by the next export of it.

In a Fabric notebook, `--to /lakehouse/default/Tables --format delta` writes the tables straight into the lakehouse attached to the notebook.

## API

| Name | What it is |
|---|---|
| `open(path_or_file)` | A `PqaxDocument`. The source is a path, the bytes of a file, or a binary file object, which is left open. |
| `open_embedded(path_or_file, part_name="Footprint.pqax")` | A `PqaxDocument` of the PQAX file that a package holds as a part |
| `PqaxDocument` | `manifest`, `summary`, `row_count(name)`, `table(name)`, `rows(name)`, `raw(name, part=None)`, `table_names`, `part_names`, `format_version`, `collection`, `embedded_part`, `embedded_compression`, `close()`, and the `with` statement |
| `validate(path_or_file)`, `validate_embedded(path_or_file, part_name)` | A `ValidationResult`: `is_valid`, `checks`, `issues`, `codes` |
| `Issue` | `code`, `message`, `part`, `table`, `field` |
| `to_dataframe(table, library=None)` | A pandas or polars data frame |
| `measures` | The derived measures of the tables, the columns, and the collection. See above. |
| `export` | `export(path_or_file, to, format="csv", part=None, if_exists="error")`, `COLUMNS`, `SCHEMAS`, `FORMATS`, `footprint_id(data)`, `AlreadyExportedError`. See above. |
| `Builder`, `BuilderOptions` | The writer |
| `format_timestamp(datetime)`, `parse_timestamp(text)` | Timestamps in the form of the format, `2026-10-05T08:15:30.000Z` |
| `TABLES`, `SUMMARY_TABLES`, `DETAIL_TABLES`, `FIELDS`, `REQUIRED`, `REFERENCES` | The names of the format |

Errors:

| Error | When |
|---|---|
| `ManifestMissingError` | The archive has no `manifest.json` |
| `FormatError` | `format` is not `pqax` |
| `UnknownVersionError` | The first number of `formatVersion` is not 0 |
| `JsonError` | A part that is read is not valid JSON |
| `PartMissingError` | A detail part that the manifest lists is not in the archive, when its table is read (`manifest.partMissing`), or the embedded part is not in the package (`embedded.partMissing`) |
| `ContainerError` | The file is not a zip archive, or an entry cannot be read: encrypted, compressed with another method, or corrupt |

All derive from `PqaxError`, whose `code` is the validation code that the error matches, when there is one. A reader refuses a file for the first four, when it opens it, and `open_embedded` also for a missing embedded part. Opening also fails when the manifest or the summary cannot be read from the zip, for example because the entry is encrypted. A `summary.json` that the manifest lists and the archive does not have gives empty summary tables, and `validate` reports it. A detail part that is broken fails only the read of its tables.

## Limits

- The reader keeps a table in memory once it is read. A part of 250,000 column chunks takes about 0.15 s to parse.
- A part that holds more than 1 GB of JSON text is not read, and is reported or raised as such.
- The rows of a part are the length of its longest array, not what the manifest states, so that a file that states more rows than it holds cannot make the reader allocate them. In a valid file the two agree.
- The reader replaces a value of another JSON type with `None` only at the level of the field. An element of a list, or a member of a nested object, is returned as the file has it.
- `container.zip64` looks at the central directory and its locator, as the kit says. A ZIP64 field in a local header alone is not reported.
- No pandas or polars type is chosen for timestamps, lists, maps, or nested objects.
- On Python 3.10, `parse_timestamp` reads fewer of the other forms of ISO 8601, as `datetime.fromisoformat` does there: a fraction of seconds with other than three or six digits, a comma before the fraction, an offset without a colon, and the basic form, such as `20261005T081530Z`, raise `ValueError`. The form of the format reads on every version.
