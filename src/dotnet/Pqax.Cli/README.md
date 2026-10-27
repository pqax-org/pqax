# pqax

The command line of PQAX. It checks a PQAX file against the rules of the format, shows what a PQAX file holds, and collects a footprint. It is a thin layer over the [Pqax](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax/README.md) and [Pqax.Collector](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Collector/README.md) libraries: everything it does, the libraries do too.

It is packed as a .NET tool, `Pqax.Cli`, with the command `pqax`. It needs .NET 8 or later. The package is not published yet.

## Commands

| Command | What it does |
|---|---|
| `pqax check <file>` | Validates the file and prints one line for each issue, starting with the code of its rule, then a line with the result |
| `pqax info <file>` | Prints the format and its version, the collection, the parts with the rows of each table, the rows of each table, and the totals of the summary |
| `pqax tables <file>` | Prints one row per table with its rows, files, live bytes, waste, and waste share, as the storage views compute them. See below. |
| `pqax diff <older> <newer>` | Compares two footprints of the same lakehouse: the tables and the columns added, removed, and kept, the largest change first. See below. |
| `pqax collect ...` | Collects the footprint of the tables of OneLake items, of a folder with the OneLake layout, or of a folder of an Azure Data Lake Storage Gen2 container, and writes a PQAX file. See below. |
| `pqax --version` | Prints the version |
| `pqax --help` | Prints the usage |

A file with the extension `.vpax` is read as a VPAX file that holds the footprint as its part `Footprint.pqax`. `--part <name>` names another part, and also makes any file read as a VPAX file.

## Exit codes

| Code | `check` | `info`, `tables`, and `diff` |
|---|---|---|
| 0 | The file is valid | The file was read |
| 1 | The file breaks at least one rule | The reader refuses the file, or one of the two files of `diff`: it is not a PQAX file of a version that it reads |
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

## Diff

```
pqax diff <older> <newer> [--part <name>] [--sort total|waste|rows|files|name] [--top <n>] [--json]
```

Compares two footprints of the same lakehouse taken at different times, with `StorageDiff` of the `Pqax` library, by the rules that [section 13 of the object model](https://github.com/pqax-org/pqax/blob/main/docs/object-model.md#13-comparing-two-footprints) proposes: a table is the same table when its `TableId` is, and otherwise, when one of the two has none, when its source, namespace, and name are; a column when its `FieldId` is, and otherwise by name. A `.vpax` file is read as for `info`.

```
Older: diff-before.pqax, 7 tables, collected 2026-10-01T06:00:00.000Z
Newer: diff-after.pqax, 7 tables, collected 2026-10-08T06:00:00.000Z
Tables: 2 added, 2 removed, 5 kept, 4 changed; by total
Footprint: 634 KB to 383 KB, -252 KB; waste: 136 KB to 58.6 KB, -77.1 KB

Change     Table                    Total before  Total after  Total change  Waste change  Files change  Rows change  Files added  Files removed
removed    legacy                         177 KB            -       -177 KB           0 B            -2      -15,000            -              -
replaced   products                       108 KB            -       -108 KB      -29.3 KB            -3       -6,000            -              -
changed    orders                         138 KB      72.8 KB      -64.9 KB      -66.4 KB            -3            0            1              4
changed    sales                          122 KB       166 KB      +44.4 KB      +18.6 KB            +1       +3,000            2              1
added      events                              -      27.3 KB      +27.3 KB           0 B            +2       +5,000            -              -
recreated  products                            -      21.5 KB      +21.5 KB           0 B            +1       +2,000            -              -
changed    raw.staging                   9.96 KB      15.2 KB      +5.27 KB           0 B            +1         +540            1              0
unchanged  audit                         17.6 KB      17.6 KB           0 B           0 B             0            0          n/a            n/a
renamed    clients (was customers)       62.5 KB      62.5 KB           0 B           0 B             0            0          n/a            n/a

Table        Column       Change   Detail
orders       price        changed  type int to bigint; size +1,000 B
sales        discount     added    double
sales        discount     removed  double
sales        amount       renamed  was amt; size +6.84 KB
...
```

| Option | Default | Meaning |
|---|---|---|
| `--sort total\|waste\|rows\|files\|name` | `total` | The order: by the size of the change, the largest first, or by name. The change of a kept table is its delta; that of a table added or removed, its value. A null change goes last. Ties go by the name, then added before removed before kept, then the order of the file. |
| `--top <n>` | all | The first n changes of the order |
| `--json` | off | Writes one JSON object instead: the header of each file, the thresholds, the order, the counts, the change of the measures of the collection, and the lists `added`, `removed`, and `kept`, with the older value, the newer value, and the delta of every total and measure of each kept table and column, and the files, partitions, and commits added and removed |
| `--part <name>` | `Footprint.pqax` | The part of a VPAX file that holds the footprint, for both files |

`Change` is `added`, `removed`, `changed`, `unchanged`, or `renamed`; a table dropped and created again with a new `TableId` is `replaced` and `recreated`. `Files added` and `Files removed` count the files by their path, when both footprints keep the files of the table; `n/a` otherwise. The second table lists the columns added, removed, renamed, or changed in type, nullability, or size. Bytes and counts are written as `pqax tables` writes them, with a sign for a change. `python -m pqax diff` writes the same text and the same JSON, apart from the line ends.

## Collect

```
pqax collect --local <folder> --output <file.pqax> [--item <name.Kind>]... [options]
pqax collect --workspace <name or id> --item <name.Kind> [--item ...] --output <file.pqax> [identity] [options]
pqax collect --adls https://<account>.dfs.core.windows.net/<container>[/<folder>] --output <file.pqax> [identity] [options]
```

`--local` reads a folder with the layout of a OneLake workspace, `<item>.<Kind>/Tables/[<schema>/]<table>`, such as `conformance/fixtures/lakehouse`. Without `--item`, it collects every lakehouse, warehouse, mirrored database, and SQL database of the folder. `--workspace` reads OneLake after a sign-in with one of the identities below; the token stays in memory. The command runs the [collector](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Collector/README.md) with the checkpoint reader of [Pqax.Collector.ParquetNet](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Collector.ParquetNet/README.md), writes the file, and validates it.

`--adls` reads a container of a storage account with hierarchical namespace, Azure Data Lake Storage Gen2, after a sign-in with the same identities. The URL is the DFS endpoint of the account, the container, and optionally a folder: `https://<account>.dfs.core.windows.net/<container>/<folder>`. The form of the ABFS driver, `abfss://<container>@<account>.dfs.core.windows.net/<folder>`, is accepted too. The command finds the tables under the folder as the [collector](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Collector/README.md#folders-of-a-storage-account) says: a folder with `_delta_log` is a Delta table, and a folder of Parquet files with no subfolder is a Parquet table, down to 4 levels below the folder. Point the URL at a deeper folder to reach deeper tables. The command ran against a storage account on 24 October 2026, on Delta tables and a folder of Parquet files, with a person signed in through the Azure CLI and with a service principal holding Storage Blob Data Reader.

| `--adls` refuses | Why |
|---|---|
| `--local`, `--workspace`, or `--item` with it | One storage at a time; the tables are found, not named |
| A URL with a query, such as a SAS token | A secret on the command line stays in the shell history. The message does not quote the URL. |
| The blob endpoint, `<account>.blob.core.windows.net` | The command reads the DFS API |
| A OneLake host | OneLake is read with `--workspace` |
| A URL with no container, or a container name that is not 3 to 63 lowercase letters, digits, and dashes | The naming rule of Azure Storage |
| A folder with a `.` or `..` segment once decoded, such as `x%2F..%2Fy` | The folder would climb out of where the URL points |

The URL is read as `System.Uri` reads it before the command splits it into the endpoint, the container, and the folder. The host is taken in lower case and the port 443 is left out, so `https://PQAXTEST.dfs.core.windows.net:443/lake` is the account `pqaxtest.dfs.core.windows.net`; another port stays in the endpoint. A backslash in the path is taken for a slash. The `.` and `..` segments are resolved, also when written `%2E`, so `/lake/a/../b` is the folder `b` of the container `lake`, and `/lake/..` has no container. The segments and the container are then percent-decoded: `my%20data` is the folder `my data`, and `la%6Be` the container `lake`, while `a%2Fb` is two levels, `a/b`, and a `.` or `..` that such a `%2F` leaves, as in `x%2F..%2Fy`, is refused. A container that decodes to a name outside the rule above is refused, such as `lake%0A` with its final newline. Any host but the blob endpoint and the OneLake hosts is accepted, such as the DFS endpoint of another cloud or an emulator on `127.0.0.1`.

| Option | Default | Meaning |
|---|---|---|
| `--depth summary\|files\|rowGroups\|columnChunks` | `columnChunks` | The detail kept for each table |
| `--level 1\|2` | 2 | 1 reads the Delta logs, 2 also the Parquet footers |
| `--max-footers <n>` | 0 | The footers read for each table, 0 for all |
| `--max-commits <n>` | 30 | The commit files read for the history of each table. The commits that the state needs are read anyway, so a table reads the greater of the limit and those. |
| `--parallelism <n>` | 16 | The storage requests at the same time |
| `--verbose` | off | Prints the identity, then every request to OneLake or to the storage account with its status code, to the error output. The URLs name the items, the folders, and the tables. |

It prints one line for each table that is done, with counts and never a name, then a last line:

```
fixture.pqax: 2 tables, 5 footers read, 0 issues, 17 requests, 142178 bytes read; valid
```

The exit code is 0 when the file is written and valid, 1 when the written file is invalid, and 2 for a wrong command line or a failure. `pqax collect --help` prints its usage.

### Identities

The identities are the same for `--workspace` and `--adls`: they all request a token for `https://storage.azure.com`, which OneLake and Azure Storage both take. One identity at a time. Without an identity option, the command signs in a person with a device code, which it prints: a browser window can open where nobody sees it, so the device code is the default. The other identities run without a person, from a scheduled job, a pipeline, or a notebook.

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
| Viewer, plus the items shared with the principal with the permission to read their data in OneLake | Workspace, Manage access, and the sharing of each item | Verified on 24 October 2026: a principal with Viewer read the items shared with it |

The command turns a refused sign-in or a refused request into one line with the code and the likely cause:

| Error | Likely cause |
|---|---|
| `AADSTS7000215`, `AADSTS7000222` | The client secret is wrong or has expired |
| `AADSTS700016` | The client id is not an application of the tenant |
| `AADSTS90002` | The tenant is not found |
| `AADSTS700027` | The certificate is not one that the application has registered |
| `401` from OneLake | The tenant setting does not include the principal, or an access token is for another audience or has expired |
| `403` from OneLake | The principal has no workspace role that reads OneLake |
| `401` from a storage account | An access token is for another audience, from another tenant, or has expired |
| `403` from a storage account | The identity has no data role on the container, or a network rule of the account refuses the machine |
| The identity is not available | No managed identity on the machine, or the Azure CLI is not installed or not signed in |

### Permissions on a storage account

Any identity, a person included, reads a container of a storage account with a data role of Azure RBAC:

| Setting | Where | Status |
|---|---|---|
| The role Storage Blob Data Reader, on the container or on the storage account | Storage account, Access control (IAM), or the container, Access control (IAM) | Needed. It covers listing and reading, and nothing else. A role can take a few minutes to apply. |
| Reader, Contributor, or Owner of the account | The same | Not enough: these roles manage the account and do not read data |
| ACLs with read and execute on the folders and the files, instead of a role | Storage Explorer or `az storage fs access` | Works by the rules of ADLS Gen2, not verified with the command |
| The firewall of the account, when it allows selected networks only | Storage account, Networking | Must allow the machine that runs the command; otherwise every request answers 403 |

A shared key or a SAS token is not accepted: the command signs in with Microsoft Entra ID only.

## Use from code

`Pqax.Cli.Program.Run(args, output, error)` runs a command with the given writers and returns the exit code, so that a test or a host can run it in process. `CollectCommand.Run(args, output, error, host)` also takes a `CollectHost`: the environment variables, a credential to use instead of the one of the identity options, and the HTTP client and endpoint of OneLake. `--adls` takes its endpoint from its URL and its HTTP client from the host. `TokenCache` is the token delegate of the collect command: it keeps a token of a `TokenCredential` until five minutes before it expires.

## Pack

```bash
dotnet pack src/dotnet/Pqax.Cli -c Release -o artifacts
dotnet tool install --global --add-source artifacts Pqax.Cli
```
