# Templates

Files to copy into another repository or workspace, to collect a footprint on a schedule without a person. They run `pqax collect` of [Pqax.Cli](../src/dotnet/Pqax.Cli/README.md) with one of its identities for unattended use, and need the first release of `Pqax.Cli` after 0.2.0, which has them.

| Folder | What it is | Sign-in | Status |
|---|---|---|---|
| [`github-actions`](github-actions/README.md) | A GitHub Actions workflow: a daily cron and a manual run, the footprint kept as an artifact and optionally committed | A service principal, with OpenID Connect federation through `azure/login`, or a client secret | Checked locally; not run on GitHub yet |
| [`fabric`](fabric/README.md) | A Fabric notebook that installs the .NET runtime and the tool, collects with the token of the notebook, and copies the footprint into a lakehouse; a pipeline schedules it | The identity that runs the notebook, through `notebookutils.credentials.getToken` | Not run in a workspace yet |

The templates read OneLake and write nothing there. A footprint holds the names of the workspace, the items, the tables, and the columns: keep it where those names may be read.
