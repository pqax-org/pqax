# PQAX prototype

This prototype writes, reads, and checks PQAX files as the [serialization draft](../../docs/serialization.md) defines them. It exists to test the draft on real tables before anything is built on it, and its measurements are the evidence for the choices in that document.

## What is here

| File | What it does |
|---|---|
| `build_pqax.py` | Builds a PQAX file from the output of the probe that preceded the collector, in the form of `conformance/fixtures/probe-dump`, at a chosen depth. It can also write the layouts that the draft rejects, to compare them. |
| `check_pqax.py` | Checks a PQAX file against the rules of the draft: container, JSON values, arrays, identifiers, references, depth, and totals |
| `test_check.py` | Runs the checker on changed copies of a file, to show which changes it accepts and which it rejects |
| `read_pqax.mjs` | Opens a PQAX file in plain JavaScript and times each step. It also opens a PQAX file embedded in a VPAX. |
| `measure_parts.mjs` | Measures the time to inflate and to parse the parts of one table, and the memory that the result holds |
| `query_duckdb.py` | Loads the tables into DuckDB and joins them, to show that they work in SQL as they are |
| `embed-probe/` | Writes a package with `System.IO.Packaging`, as the VPAX library does, with a PQAX file as one part |
| `sample.pqax` | A small file built from the synthetic tables of the probe: 2 tables, 5 files, 13 row groups, and 52 column chunks. It is a zip, so any zip tool opens it. |

The builder, the checker, and the JavaScript reader use no package: only the standard library of Python and of Node.

## How to run

Requirements: Python 3 and Node.js, tested with Python 3.14 and Node 24. `query_duckdb.py` needs the `duckdb` package, and `embed-probe` needs the .NET 10 SDK.

```bash
python build_pqax.py data/out.pqax ../../conformance/fixtures/probe-dump --depth columnChunks
python check_pqax.py data/out.pqax
node read_pqax.mjs data/out.pqax
node --expose-gc measure_parts.mjs columnChunks data/out.pqax
python query_duckdb.py data/out.pqax
python test_check.py sample.pqax
dotnet run --project embed-probe -f net10.0 -- sample.pqax data/embedded.vpax
node read_pqax.mjs data/embedded.vpax
```

The input folder is the output of the probe. Files built from real tables hold real names, so they go to `data/`, which git ignores. Every script prints identifiers and numbers only.

Options of the builder that exist only for the measurements:

| Option | Effect |
|---|---|
| `--fill-footers` | Gives each file whose footer was not read a copy of a footer that was read, with its numbers changed at random. This estimates a complete file. |
| `--repeat N` | Writes every table N times |
| `--rows-per-part N` | Changes the limit of 250,000 rows in a detail part. A larger value writes a file that breaks the rule of the draft. |
| `--layout rows`, `--layout objects`, `--dictionary` | Write the tables in layouts that the draft rejects |

## Results

Measured on 5 and 6 October 2026 with Python 3.14, Node 24.19.0, and DuckDB 1.5.4.

The input is the probe output for a real lakehouse and a real warehouse: 78 tables, 813 columns, 5,236 files, 13.7 GB, and 929 million rows. The probe read the footer of 399 files.

### Size at each depth

| Build | Rows of detail | File |
|---|---|---|
| `--depth summary` | None | 61,723 bytes |
| `--depth files` | 5,236 files, 4,720 partitions, 1,112 commits | 346,130 bytes |
| `--depth rowGroups` | Also 400 row groups | 350,578 bytes |
| `--depth columnChunks` | Also 10,259 column chunks | 435,070 bytes |
| `--fill-footers` | 5,237 row groups and 225,484 column chunks | 2,054,208 bytes |
| `--fill-footers --repeat 5` | 26,180 files and 1,127,420 column chunks, in five parts | 9,960,467 bytes |
| The same with `--rows-per-part 5000000` | The column chunks in one part | 9,913,596 bytes |

The parts of the estimate with every footer:

| Part | Rows | JSON text | In the zip | Bytes for each row |
|---|---|---|---|---|
| `summary.json` | 1,894 in 9 tables | 280,786 | 61,709 | 76 for each column |
| `partitions.json` | 4,720 | 130,637 | 42,859 | 9 |
| `files.json` | 5,236 | 991,768 | 275,429 | 53 |
| `rowGroups.json` | 5,237 | 172,878 | 65,651 | 13 |
| `columnChunks.json` | 225,484 | 17,722,465 | 1,595,261 | 7 |
| `commits.json` | 1,112 | 187,948 | 12,271 | 11 |

Where the bytes go:

- In `files`, the path takes about 147 KB of the 275 KB. Paths are long and random.
- In `columns` and `physicalColumns`, the physical names of the warehouse take about half. Column mapping gives every column a generated name.
- In `columnChunks`, the sizes and the counts take nearly everything. The codec and the encodings, which repeat on every row, compress to almost nothing.

### Time to read

`read_pqax.mjs` and `measure_parts.mjs`, best of several runs:

| Step | Time |
|---|---|
| Open the file with 78 tables: zip directory, manifest, and summary | 2.8 to 3.6 ms |
| Open the file with 390 tables | 8.0 ms |
| Read 5,236 files | 4 ms |
| Read 225,484 column chunks in one part | 20 ms to inflate, 46 ms to decode and parse |
| Read 1,127,420 column chunks in five parts | 151 ms to inflate, 248 ms to parse, at most 73 ms for a part |
| Read 1,127,420 column chunks in one part | 102 ms to inflate, 324 ms to parse |
| Follow the references from 225,484 column chunks to their columns | 4 ms |

Other readers, on the estimate with 225,484 column chunks:

| Reader | Time |
|---|---|
| Python `json`, summary | 3 ms |
| Python `json`, column chunks | 0.16 s |
| DuckDB, load four tables and join column chunks to tables | 0.5 s |
| DuckDB, the same on 1,127,420 column chunks | 0.7 s |

`table` is a reserved word in SQL, so the reference of that name needs quotes there.

### Layouts of a table

All on the estimate with 225,484 column chunks. The last three columns are about the column chunk table alone.

| Layout | File | JSON text | Parse | Memory |
|---|---|---|---|---|
| One array for each field | 2,054,208 bytes | 16.9 MB | 46 ms | 43.5 MB |
| The same, with `--dictionary` | 2,032,223 bytes | 9.9 MB | 24 ms | 22.4 MB |
| `--layout rows`: one array for each row | 3,326,457 bytes | 17.3 MB | 49 ms | 55.5 MB |
| `--layout objects`: one object for each row | 3,936,243 bytes | 62.7 MB | 111 ms | 49.5 MB |

Where the memory of the first layout goes, measured by parsing the table without some fields:

| Fields | Memory |
|---|---|
| All 13 | 43.5 MB |
| Without `encodings` | 21.0 MB |
| Without `encodings` and `codec` | 18.9 MB |
| All 13, after sharing equal `encodings` lists | 22.8 MB |

- The `encodings` field holds a small list for each row, and takes half of the memory. The estimate has 4 distinct lists.
- Sharing equal lists after the parse takes 14 ms and gives the memory of the dictionary layout.
- `codec` adds 2.1 MB, about 9 bytes for each row, so the engine does not keep a separate string for each row.

### Embedding in a VPAX

`embed-probe` creates the part with `CompressionOption.NotCompressed`, on `System.IO.Packaging` 9.0.0.

| Writer | How the part is stored | Package against the PQAX file |
|---|---|---|
| .NET 10.0.12 | Stored, method 0 | 588 bytes larger |
| .NET Framework 4.8 | Stored, method 0 | 672 bytes larger |

- The package declares the content type of the part as `application/zip`, for the extension `pqax`.
- The reader opens the footprint inside the package in 3.8 to 4.5 ms, without copying it out.
- A part that is deflated instead is inflated in memory first, and opens in 7 ms.

### Checks

- `check_pqax.py` passes on every file built in the layout of the draft with the default options. On the largest it runs almost 12,000 checks. It rejects the file built with `--rows-per-part 5000000`, whose single part of column chunks is too large.
- `test_check.py` runs 13 cases on the sample file. The checker accepts the unchanged file, a file with the additions of a producer and of a later version, and a file with a depth that this version does not define. It rejects 10 files that break a rule each: a byte order mark, an array of another length, a reference to a missing row, a repeated identifier, a timestamp in another form, a number with a fraction, a wrong row count in the manifest, rows beyond the depth of a table, a total that differs from the detail, and a required field with no value.

## What the results say

- **A zip of JSON is enough.** A file with every footer of 5,236 files takes 2 MB, and one with 1.1 million column chunks takes 10 MB.
- **Opening does not depend on the detail.** The summary of 78 tables opens in 3 ms, whatever detail the file holds.
- **One array for each field is the layout to use.** Arrays of rows and arrays of objects are 62 and 92 percent larger.
- **A dictionary is not worth a second form of field.** It saves 1 percent of the file. A reader gets its memory saving with a short loop.
- **Parts of 250,000 rows cost nothing.** Splitting the column chunks in five parts adds 0.5 percent to the file, and no part then takes more than 73 ms to parse.
- **Enumeration values can be written in full.** The earlier [encoding experiment](../detail-encoding/README.md) stored them once and referred to them by position. Deflate makes that unnecessary.
- **The tables work in SQL as they are.** No view is needed to join them.
- **A VPAX can hold a PQAX file that a reader opens in place.**

## Limits

- The estimates repeat real footers with random changes. Real footers of the same files could compress better or worse.
- Times come from one machine and one JavaScript engine. No browser was tried.
- The input has only Delta tables written by Fabric, with top-level columns. Iceberg tables, delete files, sort orders, and model bindings are in the draft and not in any file built here.
- The builder computes the quartiles of a distribution by position, with no interpolation. The object model does not yet say how.
- There is no reader or writer in .NET yet.
