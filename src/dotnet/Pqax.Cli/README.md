# pqax

The command line of PQAX. It checks a PQAX file against the rules of the format, shows what a PQAX file holds, and collects a footprint. It is a thin layer over the [Pqax](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax/README.md) and [Pqax.Collector](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Collector/README.md) libraries: everything it does, the libraries do too.

It is packed as a .NET tool, `Pqax.Cli`, with the command `pqax`. It needs .NET 8 or later. The package is not published yet.

## Commands

| Command | What it does |
|---|---|
| `pqax check <file>` | Validates the file and prints one line for each issue, starting with the code of its rule, then a line with the result |
| `pqax info <file>` | Prints the format and its version, the collection, the parts with the rows of each table, the rows of each table, and the totals of the summary |
| `pqax collect ...` | Collects the footprint of the tables of OneLake items, or of a folder with the OneLake layout, and writes a PQAX file. See below. |
| `pqax --version` | Prints the version |
| `pqax --help` | Prints the usage |

A file with the extension `.vpax` is read as a VPAX file that holds the footprint as its part `Footprint.pqax`. `--part <name>` names another part, and also makes any file read as a VPAX file.

## Exit codes

| Code | `check` | `info` |
|---|---|---|
| 0 | The file is valid | The file was read |
| 1 | The file breaks at least one rule | The reader refuses the file: it is not a PQAX file of a version that it reads |
| 2 | The command line is wrong, or the file cannot be read | The same |

## Output

`check` on a valid file:

```
sample.pqax: valid, 743 checks
```

`check` on an invalid file:

```
part.rowCount: commits.engine has 5 values, and the manifest states 6 rows of commits in this part. (part commits.json, table commits, field engine)
part.rowCount.array.pqax: invalid, 1 issue in 743 checks
```

The codes are those of the [rules of the conformance kit](https://github.com/pqax-org/pqax/blob/main/conformance/rules.md). An issue line starts with the code, followed by a colon. The last line starts with the file name.

`info`:

```
File               sample.pqax, 4183 bytes
Format             pqax 0.1
Collector          pqax-conformance-kit 0.1
Level              2
Collected          unknown to 2026-10-05 20:55:12 UTC
Data values        no
Partition values   no
Parts              6
  summary.json       sources 2, tables 2, columns 10, partitionSpecs 1, partitionFields 1, physicalSchemas 2, physicalColumns 8, writers 3, operations 5
  partitions.json    partitions 2
  files.json         files 5
  rowGroups.json     rowGroups 13
  columnChunks.json  columnChunks 52
  commits.json       commits 6
Tables             14
  sources                   2 rows
  ...
Summary            2 tables, 10 columns, 5 files, 199005 bytes, 21100 rows
```

Numbers are written without separators, so that a script can read them. For a VPAX file, a line such as `Embedded part      Footprint.pqax, deflated` gives the part and whether the package stores or deflates it.

## Collect

```
pqax collect --local <folder> --output <file.pqax> [--item <name.Kind>]... [options]
pqax collect --workspace <name or id> --item <name.Kind> [--item ...] --output <file.pqax> [--tenant <id>] [--browser] [options]
```

`--local` reads a folder with the layout of a OneLake workspace, `<item>.<Kind>/Tables/[<schema>/]<table>`, such as `conformance/fixtures/lakehouse`. Without `--item`, it collects every lakehouse, warehouse, mirrored database, and SQL database of the folder. `--workspace` reads OneLake after a sign-in with a device code, which the command prints, or in the browser with `--browser`; the token stays in memory. The device code is the default because a browser window can open where nobody sees it. The command runs the [collector](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Collector/README.md) with the checkpoint reader of [Pqax.Collector.ParquetNet](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Collector.ParquetNet/README.md), writes the file, and validates it.

| Option | Default | Meaning |
|---|---|---|
| `--depth summary\|files\|rowGroups\|columnChunks` | `columnChunks` | The detail kept for each table |
| `--level 1\|2` | 2 | 1 reads the Delta logs, 2 also the Parquet footers |
| `--max-footers <n>` | 0 | The footers read for each table, 0 for all |
| `--max-commits <n>` | 30 | The commit files read for the history of each table. The commits that the state needs are read anyway, so a table reads the greater of the limit and those. |
| `--parallelism <n>` | 16 | The storage requests at the same time |

It prints one line for each table that is done, with counts and never a name, then a last line:

```
fixture.pqax: 2 tables, 5 footers read, 0 issues, 15 requests, 142178 bytes read; valid
```

The exit code is 0 when the file is written and valid, 1 when the written file is invalid, and 2 for a wrong command line or a failure. `pqax collect --help` prints its usage.

## Use from code

`Pqax.Cli.Program.Run(args, output, error)` runs a command with the given writers and returns the exit code, so that a test or a host can run it in process.

## Pack

```bash
dotnet pack src/dotnet/Pqax.Cli -c Release -o artifacts
dotnet tool install --global --add-source artifacts Pqax.Cli
```
