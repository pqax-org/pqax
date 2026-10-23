# Power BI template

A Power BI project over the tables that `pqax export` writes, to follow the storage of a lakehouse day after day: the storage stack of each table over time, the non-live and unreferenced files, and the small files. The measures are those of section 10 of the [object model](../../docs/object-model.md#10-derived-measures), and their descriptions in the model quote its definitions.

| | |
|---|---|
| Format | PBIP: a TMDL semantic model and a PBIR report |
| Data | A folder of Parquet files written by `python -m pqax export --format parquet`, in Import mode; the same model can read the Delta tables of a lakehouse in Direct Lake |
| Needs | Power BI Desktop with the PBIP and PBIR formats, from 2025 on; the `pqax` Python package with `pqax[parquet]` for the export |
| Status | Opened and refreshed in Power BI Desktop 2.159 on the sample of `make_sample.py`; not published to a workspace yet |

| File | What it is |
|---|---|
| `PqaxFootprints.pbip` | The project: open it with Power BI Desktop |
| `PqaxFootprints.SemanticModel/` | The model, in TMDL |
| `PqaxFootprints.Report/` | The report, in PBIR: three pages |
| `make_sample.py` | Builds a sample export of fifteen footprints over fourteen days, synthetic, from a sample of the [conformance kit](../../conformance/README.md) |

## Export the footprints

Each day, export the footprint of the day into the same folder:

```bash
pip install "pqax[parquet]"
python -m pqax export footprint-2026-10-05.pqax --to C:\PQAX\export --format parquet
```

The folder gets one subfolder per table, with one Parquet file per footprint. A footprint already in the folder is refused, so running the export again does no harm; with `--if-exists skip` it exits with 0. The [Python README](../../src/python/README.md#export-the-tables-day-after-day) describes the tables, their columns, and the identifier of a footprint. The [GitHub Actions workflow](../github-actions/README.md) and the [Fabric notebook](../fabric/README.md) can run the export after the collection.

To try the template without footprints of your own, build the sample:

```bash
python templates/powerbi/make_sample.py      # writes templates/powerbi/sample, which git ignores
```

## Open the project

1. Open `PqaxFootprints.pbip` with Power BI Desktop.
2. Home, Transform data, Edit parameters: set `PqaxExportFolder` to the folder of the export, such as `C:\PQAX\export` or the full path of `templates/powerbi/sample`. The path, with the file names under it, must stay below 260 characters.
3. Refresh.

A visual shows an error until the first refresh, because the project holds no data.

## The model

| Table | Loads | One row for each |
|---|---|---|
| `Footprints` | `footprints` | Footprint: its id, `collectedAt`, `collectionDate`, the file, and the collection |
| `Tables` | `tableMeasures` | Table of a footprint, with its measures |
| `Columns` | `columnMeasures` | Column of a footprint, with its measures |
| `Collection` | `collectionMeasures` | Footprint, with the measures of the collection |
| `Date` | A calculated table | Day, from 1 January 2026 to the end of the next year, with `Month` and `Year` |
| `Storage` | A calculated table with no data | The DAX measures |

The columns keep the names of the export, and so of the format and of section 10. `Tables`, `Columns`, and `Collection` filter by `footprintId` from `Footprints`, and `Footprints` by `collectionDate` from `Date`. No column is summarized by default: a size of a table is a stock, not a flow, so adding it over the days says nothing.

The measures of `Storage` add the tables of the last collection day in the period, each table once, from its latest collection that day. A table is the pair of `sourceName` and `label`. So a month shows its last day, a day with two collections shows the later one, and a table that was dropped does not count after its last collection. A period whose last day has no collection of a source leaves that source out.

| Folder | Measures |
|---|---|
| Stack | `Live`, `Non-live`, `Unreferenced`, `Deleted`, `Stale`, `Metadata`, `Footers`, `Waste`, `Overhead`, `Data`, `Total` |
| Shares | `Waste Share`, `Overhead Share`, `Non-live Data Share`, `Unreferenced Data Share`, `Deleted Data Share`: the sum of the part over the sum of Data, over the tables that have Data |
| Files | `Table Count`, `Files`, `File Size`, `Rows`, `Average File Size`, `Median File Size` (one table only) |
| Small files | `Small Files At Least`, `Tiny Files At Least`, `Small File Share`, `Tiny File Share`, `Last Optimize At`. Over several tables, each table adds the files that its share proves, its share times the count of its file sizes. |
| Non-live and unreferenced | `Non-live Files`, `Unreferenced Files`, `Non-live Oldest Removed At`, `Non-live Newest Removed At`, `Last Vacuum At` |
| Trends | `Total Change`, `Waste Change`, `Non-live Change`, `Unreferenced Change`, `Files Change`: the last collection day of the period against the collection day before it |

`Footprints` has `Latest Collection At`, `Collections`, and `Collection Days`. The sizes are shown in units of 1,000 bytes, KB, MB, GB, and TB, since a format string of Power BI divides by 1,000; the storage views and `pqax tables` divide by 1,024.

## The report

| Page | Visuals |
|---|---|
| Storage | The total, the waste, the waste share, and the latest collection; the stack of the seven components by collection day; the tables with their total, live, waste, waste share, overhead, and the changes |
| Non-live and unreferenced | The non-live and unreferenced bytes by day, and their shares of the data; the tables with the files, the removal times, the last VACUUM, and the changes |
| Small files | The shares of the files below 128 MiB and 8 MiB by day, and the files; the tables with the average and median file sizes, the shares, and the last OPTIMIZE |

Each page has slicers for the source and the table, and a text with the definitions of section 10 that it shows.

## In a lakehouse, with Direct Lake

The same model reads the exported tables in a Fabric lakehouse in Direct Lake, with the names of the tables and the columns unchanged.

1. Export as Delta into the tables of the lakehouse. In a Fabric notebook with the lakehouse attached:

   ```python
   %pip install "pqax[delta]"
   !python -m pqax export /lakehouse/default/Files/footprints/footprint-2026-10-05.pqax --to /lakehouse/default/Tables --format delta
   ```

   The lakehouse gets the tables `footprints`, `tableMeasures`, `columnMeasures`, `collectionMeasures`, and those of the format, such as `tables` and `files`. Exporting as Parquet and loading the folders with Load to Tables gives the same tables.
2. In `expressions.tmdl`, replace the three expressions with the one that Direct Lake reads, with the ids of the workspace and of the lakehouse:

   ```
   expression DatabaseQuery =
   		let
   		    Source = AzureStorage.DataLake("https://onelake.dfs.fabric.microsoft.com/<workspace id>/<lakehouse id>", [HierarchicalNavigation = true])
   		in
   		    Source
   ```

3. In `Footprints.tmdl`, `Tables.tmdl`, `Columns.tmdl`, and `Collection.tmdl`, replace the `m` partition with an entity partition on the table of the same name:

   ```
   	partition Tables = entity
   		mode: directLake
   		source
   			entityName: tableMeasures
   			expressionSource: DatabaseQuery
   ```

   The four tables load `footprints`, `tableMeasures`, `columnMeasures`, and `collectionMeasures`. The columns stay as they are: each `sourceColumn` is the name of a column of the Delta table.
4. Keep `Date` and `Storage`. They are calculated tables that reference no table of the lakehouse, which Direct Lake allows, and the model has no calculated column on a table of the lakehouse.
5. Publish the project from Power BI Desktop, or deploy it with Fabric Git integration, and refresh the model once to frame it.

The Direct Lake steps follow the TMDL of a Direct Lake model on OneLake; they have not run in a workspace yet.

## Limits

- The export of each day must use the same folder and the same format. A CSV export is not read by this template.
- A day with footprints of the same table from two sources keeps both, since a table is its source name and its label; two sources with the same name and the same tables count once.
- The calendar starts on 1 January 2026, before which PQAX did not exist.
