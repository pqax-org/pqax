# Collection from a Fabric notebook

[`pqax-footprint.ipynb`](pqax-footprint.ipynb) collects the footprint of the default lakehouse of a Fabric notebook, or of items of a workspace, with the collector of the Python package, `python -m pqax collect`, as the identity that runs the notebook. It keeps the file in the `Files` area of the default lakehouse, and exports its tables and their measures as Delta tables into the `Tables` of the lakehouse, where the [Power BI template](../powerbi/README.md) reads them in Direct Lake.

**This notebook has not run in a Fabric workspace yet.** Its cells ran outside Fabric, as the last section says. The first run in a workspace confirms what that section lists as not verified.

| Cell | What it does |
|---|---|
| Install | `%pip install "pqax[collect,delta]==0.2.1"`: the package, pyarrow for the Delta checkpoints, and deltalake for the export. It comes first, since `%pip` can restart the Python of the notebook. 0.2.1, released on 24 October 2026, is the first version with the collector: change it to a later one when there is one. |
| Parameters | `SOURCE`, `WORKSPACE`, `ITEMS`, `MOUNT`, `OUTPUT_FOLDER`, `EXPORT_TO`, `EXTRA_OPTIONS`. It is the parameter cell of the notebook, so a pipeline can set them. |
| Collect | With `SOURCE = "lakehouse"`, the default: `pqax collect --mount /lakehouse/default --item <lakehouse>.Lakehouse`, with the name of the default lakehouse from `notebookutils.runtime.context`. With `SOURCE = "workspace"`: `pqax collect --workspace ... --item ... --access-token-env PQAX_ACCESS_TOKEN`, over the DFS endpoint of OneLake, with the token of `notebookutils.credentials.getToken("storage")` in the environment of that process only; `WORKSPACE` empty is the workspace of the notebook. The footprint is written in the temporary folder, named after the lakehouse or the workspace and the date. |
| Check and keep | `pqax check`, `pqax info`, and `pqax tables --top 20`, then copies the file to `OUTPUT_FOLDER`, by default `/lakehouse/default/Files/footprints` |
| Export | `pqax export --format delta --if-exists skip` into `EXPORT_TO`, by default `/lakehouse/default/Tables`: one Delta table per name, `footprints`, `tableMeasures`, `columnMeasures`, `collectionMeasures`, and those of the format, such as `tables` and `files`, to which each run appends. `EXPORT_TO` empty skips it. |
| Run it every day | The paragraph on the schedule and the pipeline |

## Use it

1. Import the notebook into the workspace that will run it: Workspace, Import, Notebook, From this computer.
2. Attach a default lakehouse. With `SOURCE = "lakehouse"` it is the lakehouse collected; in every case the footprints go into its `Files/footprints` folder and the export into its tables.
3. Set the parameters, or keep the defaults to collect the default lakehouse. To collect other items, set `SOURCE = "workspace"` and `ITEMS`, such as `"Sales.Lakehouse, Finance.Warehouse"`, and `WORKSPACE` when it is not the workspace of the notebook. Name the workspace and the items both by name or both by id.
4. Run all. The output of the Check and keep cell names the file.

The notebook runs as a PySpark notebook or as a Python notebook: it uses Python, `subprocess`, and `notebookutils`, and no Spark. The identity needs to read the items: a workspace role of Contributor or higher on the workspace collected. The environment of the notebook must reach PyPI to install the package, unless the package is in an environment of the workspace. When the notebook collects its own default lakehouse and exports into it, the next footprint includes the tables of the export too.

## Run it every day

A notebook has its own schedule: in the notebook, Run, Schedule, with a daily repetition. To pass parameters, or to collect several lakehouses or workspaces, create a pipeline with one Notebook activity per run of the notebook, set the parameters under Base parameters with the names of the parameter cell, and give the pipeline a schedule trigger, such as every day at 04:00. Link the activities that export into the same tables with On success, so that they run one after the other: two exports that append to the same Delta table at the same time can conflict. A scheduled run uses the token of the identity that owns the schedule or the pipeline, so that identity needs the role on every workspace collected. The files of the lakehouse are not pruned: a later cell or a pipeline step can delete the footprints older than a number of days.

## What is verified

| Check | Status |
|---|---|
| The notebook is valid JSON of nbformat 4.5, with cell ids, and each code cell but `%pip` parses as Python | Done |
| Every code cell run in order outside Fabric, on Windows with Python 3.14, the package of this branch, pyarrow 26, and deltalake 1.6.6, with a stub of `notebookutils` and the parameters set as a pipeline sets them: `SOURCE = "lakehouse"` with a copy of the lakehouse of the synthetic fixture as `MOUNT`, and `SOURCE = "workspace"` against a local fake of the DFS endpoint over the fixture, through `--endpoint` in `EXTRA_OPTIONS`. Both wrote a valid footprint, checked, described, and exported it as 18 Delta tables. On the same fake endpoint, `pqax.collect` and the C# collector gave the same footprint field by field, the requests and the bytes included. | Done |
| `%pip install "pqax[collect,delta]==0.2.1"` in a Fabric notebook: 0.2.1 is on PyPI since 24 October 2026, and the notebook has not run in Fabric yet | Not verified |
| That `/lakehouse/default` lists the tables of the default lakehouse with their `_delta_log` folders, and that `notebookutils.runtime.context` has `defaultLakehouseName` and `currentWorkspaceName` | Not verified |
| That `notebookutils.credentials.getToken("storage")` gives a token that OneLake accepts for the DFS endpoint, and how long it lasts | Not verified |
| That deltalake writes Delta tables into `/lakehouse/default/Tables` that the lakehouse shows as tables, and that Direct Lake reads | Not verified |
| That `/lakehouse/default/Files` is writable from the notebook | Not verified |
| A scheduled run, and a run from a pipeline with base parameters | Not verified |
