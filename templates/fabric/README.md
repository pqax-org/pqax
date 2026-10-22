# Collection from a Fabric notebook

[`pqax-footprint.ipynb`](pqax-footprint.ipynb) collects the footprint of a workspace from a Fabric notebook, with the token of the identity that runs the notebook, and copies the file into the `Files` area of the default lakehouse of the notebook.

**This recipe has not been run in a Fabric workspace yet.** The steps were run outside Fabric, as the last section says. The first run in a workspace confirms what that section lists as not verified.

| Cell | What it does |
|---|---|
| Parameters | `WORKSPACE`, `ITEMS`, `PQAX_VERSION`, `OUTPUT_FOLDER`, `DOTNET_CHANNEL`, `EXTRA_OPTIONS`. It is the parameter cell of the notebook, so a pipeline can set them. |
| Install | Downloads `dotnet-install.sh` from `https://dot.net/v1/dotnet-install.sh` and installs the .NET runtime, not the SDK, in `~/.dotnet`. Downloads the package `Pqax.Cli` of `PQAX_VERSION` from nuget.org and unpacks it: a .NET tool holds its assemblies in `tools/<framework>/any`, so `dotnet Pqax.Cli.dll` runs it with the runtime alone. A second run in the same session skips both. |
| Collect | Takes a token with `notebookutils.credentials.getToken("storage")` and runs `pqax collect --workspace ... --item ... --access-token-env PQAX_ACCESS_TOKEN`, with the token in the environment of that process only. The footprint is written in `/tmp`, named after the workspace and the date. |
| Check and keep | `pqax check`, `pqax info`, and `pqax tables --top 20`, then copies the file to `OUTPUT_FOLDER`, by default `/lakehouse/default/Files/footprints` |

## Use it

1. Import the notebook into the workspace that will run it: Workspace, Import, Notebook, From this computer.
2. Attach a default lakehouse: the footprint goes into its `Files/footprints` folder.
3. Set `WORKSPACE`, `ITEMS`, and `PQAX_VERSION` in the parameter cell. `PQAX_VERSION` is the first release of `Pqax.Cli` after 0.2.0: 0.2.0 does not have `--access-token-env`.
4. Run all. The output of the last cell names the file.

The notebook runs as a PySpark notebook or as a Python notebook: it uses Python, `subprocess`, and `notebookutils`, and no Spark. The identity needs to read the items: a workspace role of Contributor or higher on the workspace collected. The environment of the notebook must reach `dot.net`, `builds.dotnet.microsoft.com`, and `api.nuget.org`; a workspace with outbound access protection blocks them.

## Schedule it

A notebook has its own schedule: in the notebook, Run, Schedule, with a daily repetition. To pass parameters, or to collect several workspaces, create a pipeline with a Notebook activity that runs this notebook, add the parameters under Base parameters with the names of the parameter cell, one activity per workspace, and give the pipeline a schedule trigger, such as every day at 04:00. A scheduled run uses the token of the identity that owns the schedule or the pipeline, so that identity needs the role on every workspace collected. The files of the lakehouse are not pruned: a later cell or a pipeline step can delete the footprints older than a number of days.

## What is verified

| Check | Status |
|---|---|
| The notebook is valid JSON of nbformat 4, and each code cell parses as Python | Done |
| The package layout: a package of `Pqax.Cli` packed from this repository has `tools/net8.0/any/Pqax.Cli.dll` and `tools/net10.0/any/Pqax.Cli.dll`, and the cells unpack it and run `pqax --version`, `check`, `info`, and `tables` with `dotnet Pqax.Cli.dll`, on Windows with a stub of `notebookutils` and `--local` on the synthetic fixture instead of `--workspace` | Done |
| `dotnet-install.sh --runtime dotnet` in the environment of a Fabric notebook, and its network access | Not verified |
| That `notebookutils.credentials.getToken("storage")` gives a token that OneLake accepts for the DFS endpoint, and how long it lasts | Not verified |
| That `/lakehouse/default/Files` is writable from the notebook with a default lakehouse attached | Not verified |
| A scheduled run, and a run from a pipeline with base parameters | Not verified |
