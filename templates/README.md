# Templates

Files to copy into another repository or workspace, to collect a footprint on a schedule without a person. The workflow runs `pqax collect` of [Pqax.Cli](../src/dotnet/Pqax.Cli/README.md) with one of its identities for unattended use, and the notebook `python -m pqax collect` of the [Python package](../src/python/README.md#collect-a-footprint). Both need the first release after 0.2.0, which has them.

| Folder | What it is | Sign-in | Status |
|---|---|---|---|
| [`github-actions`](github-actions/README.md) | A GitHub Actions workflow: a daily cron and a manual run, the footprint kept as an artifact and optionally committed, with an optional export of its tables as CSV, Parquet, or Delta | A service principal, with OpenID Connect federation through `azure/login`, or a client secret | Checked locally; not run on GitHub yet |
| [`fabric`](fabric/README.md) | A Fabric notebook that installs `pqax[collect,delta]`, collects its default lakehouse where Fabric mounts it, or items of a workspace with the token of the notebook, keeps the footprint in the lakehouse, and exports its tables as Delta tables for Direct Lake; a pipeline schedules it | The identity that runs the notebook, through the mount or `notebookutils.credentials.getToken` | Run outside Fabric with a stub of `notebookutils`; not run in a workspace yet |
| [`powerbi`](powerbi/README.md) | A Power BI project, TMDL and PBIR, over the tables that `pqax export` appends day after day: the storage stack per table over time, the non-live and unreferenced files, and the small files; Import from a folder of Parquet files, or Direct Lake over the Delta tables of a lakehouse | None: it reads the export | Opened and refreshed in Power BI Desktop on a synthetic sample; not published yet |

The templates read OneLake and write nothing there, except the notebook, which writes the footprint and the export into its own default lakehouse. A footprint holds the names of the workspace, the items, the tables, and the columns: keep it where those names may be read.
