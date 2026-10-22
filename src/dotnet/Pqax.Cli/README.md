# pqax

The command line of PQAX. It checks a PQAX file against the rules of the format, shows what a PQAX file holds, and collects a footprint. It is a thin layer over the [Pqax](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax/README.md) and [Pqax.Collector](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Collector/README.md) libraries: everything it does, the libraries do too.

It is packed as a .NET tool, `Pqax.Cli`, with the command `pqax`. It needs .NET 8 or later. The package is not published yet.

## Commands

| Command | What it does |
|---|---|
| `pqax check <file>` | Validates the file and prints one line for each issue, starting with the code of its rule, then a line with the result |
| `pqax info <file>` | Prints the format and its version, the collection, the parts with the rows of each table, the rows of each table, and the totals of the summary |
| `pqax tables <file>` | Prints one row per table with its rows, files, live bytes, waste, and waste share, as the storage views compute them. See below. |
| `pqax collect ...` | Collects the footprint of the tables of OneLake items, or of a folder with the OneLake layout, and writes a PQAX file. See below. |
| `pqax --version` | Prints the version |
| `pqax --help` | Prints the usage |

A file with the extension `.vpax` is read as a VPAX file that holds the footprint as its part `Footprint.pqax`. `--part <name>` names another part, and also makes any file read as a VPAX file.

## Exit codes

| Code | `check` | `info` and `tables` |
|---|---|---|
| 0 | The file is valid | The file was read |
| 1 | The file breaks at least one rule | The reader refuses the file: it is not a PQAX file of a version that it reads |
| 2 | The command line is wrong, or the file cannot be read | The same |

## Output

`check` on a valid file:

```
sample.pqax: valid, 767 checks
```

`check` on an invalid file:

```
part.rowCount: commits.engine has 5 values, and the manifest states 6 rows of commits in this part. (part commits.json, table commits, field engine)
part.rowCount.array.pqax: invalid, 1 issue in 767 checks
```

The codes are those of the [rules of the conformance kit](https://github.com/pqax-org/pqax/blob/main/conformance/rules.md). An issue line starts with the code, followed by a colon. The last line starts with the file name.

`info`:

```
File               sample.pqax, 4307 bytes
Format             pqax 0.2
Collector          pqax-conformance-kit 0.2
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

## Tables

```
pqax tables <file> [--part <name>] [--sort waste|total|rows|files|name] [--top <n>] [--wide] [--json]
```

One row per table with the derived measures of the [storage views](https://github.com/pqax-org/pqax/blob/main/src/typescript/views/README.md#derived-values), which the `Pqax` library computes with `StorageMeasures`: the same numbers as the views. A `.vpax` file is read as for `info`.

```
sample.pqax: 2 tables, by waste
Table         Rows  Files     Live  Non-live  Unreferenced  Deleted  Stale  Waste  Waste share
sales       17,000      3   151 KB     737 B           0 B    239 B    0 B  976 B         0.6%
dbo.orders   4,100      2  37.7 KB       0 B           0 B      0 B    0 B    0 B         0.0%
```

| Option | Default | Meaning |
|---|---|---|
| `--sort waste\|total\|rows\|files\|name` | `waste` | The order: the largest first, or by name. Ties go by the total of the stack, the largest first, then by the name, then by the order of the file. A table without rows or files goes last. |
| `--top <n>` | all | The first n tables of the order |
| `--wide` | off | Adds the share of the files under 8 MiB and under 128 MiB, and the metadata and the footers, which are overhead and not waste |
| `--json` | off | Writes one JSON object instead: the file, the format version, the collector, the time and level of the collection, the thresholds, the order, and the array of the tables with every measure, for a tool that reads it |
| `--part <name>` | `Footprint.pqax` | The part of a VPAX file that holds the footprint |

The columns are those of the storage stack of the views: the live data, the non-live files that the log still tracks, the unreferenced files that a listing found, the bytes of the deleted rows, estimated from their share of the rows, and the stale columns; the waste is their sum, and the waste share is the waste over the live data and the waste. A file of format 0.1 has no unreferenced files, and a value that the file does not have is written `n/a`, never 0. Bytes are in 1024-based units, as the views write them; counts have thousands separators, whatever the culture of the machine. With `--wide`, a share of small files from the quartiles of `fileSizes`, when the footprint does not keep the files, is a lower bound and is written `>=75.0%`.

The JSON of each table has the names of the views: `stack` with `live`, `nonLive`, `unreferenced`, `deleted`, `stale`, `metadata`, `footers`, `waste`, `overhead`, `data`, `total`, `wasteShare`, `overheadShare`; `smallFiles` and `tinyFiles`; `measures` with the ratios; and `maintenance` with the last commit, the commits per day, and the days since the last commit. `python -m pqax tables` writes the same.

## Collect

```
pqax collect --local <folder> --output <file.pqax> [--item <name.Kind>]... [options]
pqax collect --workspace <name or id> --item <name.Kind> [--item ...] --output <file.pqax> [identity] [options]
```

`--local` reads a folder with the layout of a OneLake workspace, `<item>.<Kind>/Tables/[<schema>/]<table>`, such as `conformance/fixtures/lakehouse`. Without `--item`, it collects every lakehouse, warehouse, mirrored database, and SQL database of the folder. `--workspace` reads OneLake after a sign-in with one of the identities below; the token stays in memory. The command runs the [collector](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Collector/README.md) with the checkpoint reader of [Pqax.Collector.ParquetNet](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Collector.ParquetNet/README.md), writes the file, and validates it.

| Option | Default | Meaning |
|---|---|---|
| `--depth summary\|files\|rowGroups\|columnChunks` | `columnChunks` | The detail kept for each table |
| `--level 1\|2` | 2 | 1 reads the Delta logs, 2 also the Parquet footers |
| `--max-footers <n>` | 0 | The footers read for each table, 0 for all |
| `--max-commits <n>` | 30 | The commit files read for the history of each table. The commits that the state needs are read anyway, so a table reads the greater of the limit and those. |
| `--parallelism <n>` | 16 | The storage requests at the same time |
| `--verbose` | off | Prints the identity, then every OneLake request with its status code, to the error output. The URLs name the items and the tables. |

It prints one line for each table that is done, with counts and never a name, then a last line:

```
fixture.pqax: 2 tables, 5 footers read, 0 issues, 15 requests, 142178 bytes read; valid
```

The exit code is 0 when the file is written and valid, 1 when the written file is invalid, and 2 for a wrong command line or a failure. `pqax collect --help` prints its usage.

### Identities

One identity at a time. Without an identity option, the command signs in a person with a device code, which it prints: a browser window can open where nobody sees it, so the device code is the default. The other identities run without a person, from a scheduled job, a pipeline, or a notebook.

| Identity | Options | Where it fits |
|---|---|---|
| A person, with a device code | none, or `--device-code`, with `--tenant <id>` optionally | A terminal |
| A person, in the browser | `--browser [--tenant <id>]` | A desktop |
| A service principal with a secret | `--client-id <id> --tenant <id> --client-secret-env <NAME>` | A scheduled job that keeps the secret in its store of secrets |
| A service principal with a certificate | `--client-id <id> --tenant <id> --client-certificate <file> [--client-certificate-password-env <NAME>]` | The same, with a PFX or a PEM file that holds the private key. The password is for a PFX. |
| A managed identity | `--managed-identity`, system-assigned, or `--managed-identity --client-id <id>`, user-assigned | An Azure virtual machine, container, or function |
| The Azure CLI | `--azure-cli [--tenant <id>]` | After `az login`, and in GitHub Actions after `azure/login`, which signs in with OpenID Connect federation and no secret |
| An access token | `--access-token-env <NAME>` | A notebook or a CI job that already holds a token for `https://storage.azure.com` |
| The variables of the Azure SDK | `--env-credential` | A host that sets the variables below |

A secret is never an option value: `--client-secret`, `--access-token`, and `--client-certificate-password` are refused, and the value given with them is not echoed. The `-env` options take the name of an environment variable, and the command reads the value from it. The value is not printed by `--verbose`, not written in an error, and not written to the footprint. The footprint does not record who collected: the manifest has no field for it.

`--env-credential` reads the variables that the Azure SDK reads for a service principal:

| Variable | Content |
|---|---|
| `AZURE_TENANT_ID` | The tenant |
| `AZURE_CLIENT_ID` | The client id of the application |
| `AZURE_CLIENT_SECRET` | The client secret. When it is set, the certificate is not read. |
| `AZURE_CLIENT_CERTIFICATE_PATH` | The path of a PFX or PEM certificate, when there is no secret |
| `AZURE_CLIENT_CERTIFICATE_PASSWORD` | The password of the certificate, when it has one |

An access token is used as it is, until it expires. When it is a JWT, the command reads its `exp` and `aud` claims, refuses a token that has expired, and warns when the audience is not `https://storage.azure.com`. A collection that lasts longer than the token fails with a 401.

### Permissions of a service principal

A service principal or a managed identity reads OneLake when two settings allow it:

| Setting | Where | Status |
|---|---|---|
| The tenant setting "Service principals can use Fabric APIs", enabled for the whole organization or for a security group that holds the principal | Fabric admin portal, Tenant settings, Developer settings | Needed: Fabric refuses a service principal otherwise |
| A workspace role: Contributor, Member, or Admin | Workspace, Manage access | Known to read OneLake. A collection reads only, but Viewer alone does not give access to OneLake. |
| Viewer, plus the items shared with the principal with the permission to read their data in OneLake | Workspace, Manage access, and the sharing of each item | Not verified yet |

The command turns a refused sign-in or a refused request into one line with the code and the likely cause:

| Error | Likely cause |
|---|---|
| `AADSTS7000215`, `AADSTS7000222` | The client secret is wrong or has expired |
| `AADSTS700016` | The client id is not an application of the tenant |
| `AADSTS90002` | The tenant is not found |
| `AADSTS700027` | The certificate is not one that the application has registered |
| `401` from OneLake | The tenant setting does not include the principal, or an access token is for another audience or has expired |
| `403` from OneLake | The principal has no workspace role that reads OneLake |
| The identity is not available | No managed identity on the machine, or the Azure CLI is not installed or not signed in |

## Use from code

`Pqax.Cli.Program.Run(args, output, error)` runs a command with the given writers and returns the exit code, so that a test or a host can run it in process. `CollectCommand.Run(args, output, error, host)` also takes a `CollectHost`: the environment variables, a credential to use instead of the one of the identity options, and the HTTP client and endpoint of OneLake. `TokenCache` is the token delegate of the collect command: it keeps a token of a `TokenCredential` until five minutes before it expires.

## Pack

```bash
dotnet pack src/dotnet/Pqax.Cli -c Release -o artifacts
dotnet tool install --global --add-source artifacts Pqax.Cli
```
