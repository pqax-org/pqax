# Scheduled collection with GitHub Actions

[`pqax-footprint.yml`](pqax-footprint.yml) collects the footprint of a Fabric workspace every day at 04:23 UTC, and on demand. It runs `pqax collect` as a service principal, checks the file, and keeps it as a workflow artifact and, optionally, in the folder `footprints/` of the repository. It reads OneLake and writes nothing there.

| Step | What it does |
|---|---|
| Check the variables | Stops with an error that names the variables that are not set |
| Name the footprint | The workspace in lowercase letters, digits, and hyphens, then the date in UTC: `sales-analytics-2026-10-22.pqax` |
| `actions/setup-dotnet@v6` | Installs the .NET 8 SDK |
| Install pqax | `dotnet tool install Pqax.Cli --version $PQAX_VERSION` into a folder of the runner |
| `azure/login@v3` | Signs in the service principal with OpenID Connect, with no secret, when `PQAX_SIGN_IN` is `oidc` |
| Collect | `pqax collect --workspace ... --item ...` with `--azure-cli`, or with `--client-secret-env` when `PQAX_SIGN_IN` is `secret` |
| Check and describe | `pqax check`, `pqax info`, and `pqax tables --top 20`, as a smoke test: the job fails when the file is not valid |
| `actions/upload-artifact@v7` | Keeps the file as an artifact named after it, for `PQAX_RETENTION_DAYS` days |
| Commit, a second job | When `PQAX_COMMIT` is `true`: adds the file to `footprints/`, removes the footprints whose date is older than `PQAX_KEEP_DAYS` days, commits, and pushes |

The collect job has the permissions `contents: read` and `id-token: write`; only the commit job has `contents: write`.

The template needs a release of `Pqax.Cli` that has the identity options of `pqax collect` and the command `pqax tables`: the first release after 0.2.0. Set `PQAX_VERSION` to it.

## Set it up

1. Use a private repository. A footprint holds the names of the workspace, the items, the tables, and the columns.
2. Copy `pqax-footprint.yml` to `.github/workflows/` of the repository.
3. Register an application in Microsoft Entra ID, or take an existing one: its service principal is the identity that collects. Note its client id and the tenant id.
4. In Fabric, let it read the workspace: see [Permissions](#permissions).
5. Choose the sign-in: a federated credential, recommended, or a client secret.
6. Set the repository variables below, in Settings, Secrets and variables, Actions, Variables.
7. Run the workflow once from the Actions tab, with Run workflow, and read the log of the Check and describe step.

### Sign-in with OpenID Connect, recommended

GitHub gives the job a token that Entra ID exchanges for a token of the service principal, so the repository holds no secret. Add a federated credential to the application, in Certificates and secrets, Federated credentials, with the scenario "GitHub Actions deploying Azure resources", or with the Azure CLI:

```bash
az ad app federated-credential create --id <client id> --parameters '{"name": "pqax-footprint", "issuer": "https://token.actions.githubusercontent.com", "subject": "repo:<owner>/<repository>:ref:refs/heads/main", "audiences": ["api://AzureADTokenExchange"]}'
```

| Field | Value |
|---|---|
| Issuer | `https://token.actions.githubusercontent.com` |
| Audience | `api://AzureADTokenExchange` |
| Subject, for a branch | `repo:<owner>/<repository>:ref:refs/heads/<branch>`. A scheduled run runs on the default branch, so the branch is the default branch, such as `main`. A run started by hand from another branch has another subject, and Entra ID refuses it. |
| Subject, for an environment | `repo:<owner>/<repository>:environment:<name>`, when the line `environment:` of the collect job is uncommented. An environment can require a reviewer or limit the branches that use it. |

`azure/login` runs with `allow-no-subscriptions: true`: the service principal needs no Azure subscription. Then `pqax collect --azure-cli` takes the token from the Azure CLI that `azure/login` signed in.

### Sign-in with a client secret

Set `PQAX_SIGN_IN` to `secret`, create a client secret for the application, and store it as the repository secret `PQAX_CLIENT_SECRET`, in Settings, Secrets and variables, Actions, Secrets. The workflow passes it to `pqax collect --client-secret-env PQAX_CLIENT_SECRET` in the environment, never on the command line. A secret expires: note the date and renew it before.

## Variables

| Variable | Required | Default | Content |
|---|---|---|---|
| `PQAX_VERSION` | Yes | | The version of `Pqax.Cli` to install, such as the first release after 0.2.0. A pinned version, so that a new release changes nothing until the variable changes. |
| `PQAX_WORKSPACE` | Yes | | The name or the id of the workspace |
| `PQAX_ITEMS` | Yes | | The items, separated by commas, as OneLake names them: `Sales.Lakehouse, Finance.Warehouse`. Name the workspace and the items both by name or both by id. |
| `AZURE_CLIENT_ID` | Yes | | The client id of the application |
| `AZURE_TENANT_ID` | Yes | | The tenant id |
| `PQAX_SIGN_IN` | No | `oidc` | `oidc` for the federated credential, `secret` for the client secret |
| `PQAX_OPTIONS` | No | none | More options of `pqax collect`, separated by spaces, such as `--level 1 --depth files` |
| `PQAX_RETENTION_DAYS` | No | 30 | The days that GitHub keeps the artifact, within the limit of the repository |
| `PQAX_COMMIT` | No | `false` | `true` to commit the footprint to `footprints/` |
| `PQAX_KEEP_DAYS` | No | 90 | With `PQAX_COMMIT`, the footprints whose date in the name is older than this many days are removed from `footprints/` |

| Secret | Required | Content |
|---|---|---|
| `PQAX_CLIENT_SECRET` | With `PQAX_SIGN_IN` = `secret` | The client secret of the application |

## Permissions

| Where | What | Status |
|---|---|---|
| Fabric admin portal, Tenant settings, Developer settings | "Service principals can use Fabric APIs", enabled for the organization or for a security group that holds the service principal | Needed |
| The workspace, Manage access | The service principal, or a group that holds it, as Contributor, Member, or Admin | Contributor is known to read OneLake; Viewer alone does not. Viewer plus the items shared with the permission to read their data is not verified. |
| The repository, with `PQAX_COMMIT` | The commit job asks for `contents: write`. An organization that limits the token of the workflows to read refuses it, and a rule of the default branch that requires a pull request or a signed commit refuses the push. | |

`pqax collect` turns a refused sign-in into one line with the likely cause: the [Pqax.Cli README](../../src/dotnet/Pqax.Cli/README.md#identities) lists them.

## What is verified

| Check | Status |
|---|---|
| The YAML parses, and every action exists at the version it names: `actions/setup-dotnet@v6`, `azure/login@v3`, `actions/upload-artifact@v7`, `actions/checkout@v7`, `actions/download-artifact@v8`, checked on 22 October 2026 | Done |
| The inputs given to each action exist in its `action.yml` | Done |
| The shell of every step run locally with bash: install from a local package source, collect with `--local` on the synthetic fixture instead of `--workspace`, check, info, tables, and the prune and commit in a scratch repository | Done |
| A run on GitHub, the OpenID Connect sign-in, the client secret, and a real workspace | Not yet: it needs a repository, an application, and a workspace of the maintainer |
