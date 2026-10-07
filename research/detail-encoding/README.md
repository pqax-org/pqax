# PQAX detail encoding

This experiment compares ways of storing a large detail table inside a PQAX package. It was written to check whether a format that depends only on zip and JSON can hold millions of rows.

## What it does

The program generates a synthetic table with one row per column chunk and ten fields: file, row group, column, codec, encodings, value count, null count, compressed size, uncompressed size, and dictionary page size. It writes the table in four encodings, each as the single entry of a zip file, and reads each one back.

| Encoding | Layout |
|---|---|
| Rows JSON | An array with one object per row. Names and enumeration values are spelled out in every row. |
| Columnar JSON | One array per field. Enumeration values are stored once, and rows refer to them by position. |
| CSV | A header and one line per row |
| Parquet | Written by Parquet.Net 5.6.1 with its default settings. Stored in the zip without further compression. |

The three text encodings are compressed with deflate in the zip. A read starts from the zip file and ends with the values in memory.

## How to run

Requirements: .NET 10 SDK, Node.js 20 or later, PowerShell 7.

```bash
pwsh ./run.ps1
```

The script writes its files to `data/`, which git ignores. `-Rows 1000000,5000000` sets the row counts.

## Results

Measured on 5 October 2026 with .NET SDK 10.0.401 and Node 24.19.0, on the same machine as the VPAX read benchmark.

1,000,000 column chunks (5,000 files, 4 row groups per file, 50 columns):

| Encoding | Raw size | Size in the zip | .NET read | JavaScript read |
|---|---|---|---|---|
| Rows JSON | 189.6 MB | 16.4 MB | 849 ms | 617 ms |
| Columnar JSON | 39.2 MB | 8.3 MB | 316 ms | 156 ms |
| CSV | 63.8 MB | 13.1 MB | 259 ms | 637 ms |
| Parquet | 16.7 MB | 16.7 MB | 84 ms | Not measured |

5,000,000 column chunks (25,000 files):

| Encoding | Raw size | Size in the zip | .NET read | JavaScript read |
|---|---|---|---|---|
| Rows JSON | 951.9 MB | 81.7 MB | 4,783 ms | Fails: the text exceeds the maximum string length |
| Columnar JSON | 199.8 MB | 41.6 MB | 1,505 ms | 820 ms |
| CSV | 322.4 MB | 65.7 MB | 1,274 ms | 3,216 ms |
| Parquet | 71.3 MB | 71.3 MB | 422 ms | Not measured |

## What the results say

- Columnar JSON needs only a zip library and a JSON parser, and it holds a million column chunks in 8 MB that plain JavaScript reads in 0.16 s.
- One object per row doubles the size, takes about four times longer in JavaScript, and cannot be parsed in one call at five million rows.
- CSV is close to columnar JSON in size, but JavaScript has no native parser for it, and it has no types.
- Parquet reads fastest in .NET, and it needs a Parquet library in every reader.
- A part should stay well under the string limit of JavaScript engines, which is about 512 MB of text in V8. Splitting detail by table keeps parts far below it.

## Limits

- The data is synthetic. Sizes are noisy on purpose, which is the hard case for compression.
- The Parquet file uses the defaults of Parquet.Net. A tuned Parquet file would be smaller, so the size comparison does not show that JSON is more compact than Parquet in general.
- JavaScript ran in Node, which uses the same V8 engine as Chrome and Edge.
- All numbers come from one machine and one run of two iterations.
