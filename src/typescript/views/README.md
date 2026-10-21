# views

Storage views of a PQAX footprint, in a browser. A view model turns the tables of a footprint into plain data, and three views draw it in plain DOM and SVG: a storage stack per table, a treemap of the columns, and a strip of the file sizes. The package has one runtime dependency, the reader [`@pqax/pqax`](../pqax/README.md), and no framework.

Version 0.2.0, on npm as `@pqax/views` from this version. It shares the version number of the other packages, as section 7.1 of the [contracts](../../../docs/contracts.md) says, and depends on `@pqax/pqax` of the same release. It reads files of format 0.1 and 0.2, and writes none. The page at [pqax.org/tools](https://pqax.org/tools/) uses it.

## Install

```bash
npm install @pqax/views
```

npm installs the reader with it.

## What it does

| Export | What it does |
|---|---|
| `readFootprint(doc, options?)` | Reads what the model needs from an open document: the summary, and the `files` and `partitions` detail tables when the file has them |
| `buildModel(data, options?)` | The model of a footprint: the collection line, the sources, the tables, the columns, and the derived values |
| `renderTableStack(container, model, options?)` | The storage stack of each table, sorted by waste, in bytes or in shares |
| `renderColumnTreemap(container, model, options?)` | Sources, tables, and columns sized by compressed bytes and colored by the share of nulls, with drill-down |
| `renderFileStrip(container, model, options?)` | The distribution of the file sizes of each table on a log scale, with the V-Order share |
| `columnTree(model)`, `findPath(tree, key)` | The tree of the treemap, and the path to a node |
| `sortTables(tables, order)` | The tables by waste, by total, or by label |
| `deletedEstimate`, `stackOf`, `quartiles`, `smallShareAtLeast`, `commitsPerDay`, `daysBetween`, `ratio` | The derived values, one function each |
| `STACK_COMPONENTS`, `WASTE_COMPONENTS`, `OVERHEAD_COMPONENTS` | The components of a stack, and which make the waste and the overhead |
| `squarify(items, rect)` | The squarified treemap layout |
| `nullShareBin(share)` | The color class of a share of nulls |
| `formatBytes`, `formatCount`, `formatShare`, `formatNumber`, `formatTime` | How the views write numbers |
| `styles.css` | The styles, with the colors as custom properties, light and dark |

## Use

```js
import { openPqax } from "@pqax/pqax";
import { buildModel, readFootprint, renderColumnTreemap, renderFileStrip, renderTableStack } from "@pqax/views";

const doc = await openPqax(input.files[0]);            // or openEmbedded for a .vpax file
const model = buildModel(await readFootprint(doc));
const stack = renderTableStack(document.getElementById("stack"), model, { mode: "shares" });
const treemap = renderColumnTreemap(document.getElementById("treemap"), model);
const strip = renderFileStrip(document.getElementById("strip"), model);

stack.update(otherModel);                               // a new model, new options, or both
stack.dispose();                                        // removes the view and its listeners
```

The page links the stylesheet, `@pqax/views/styles.css`. Without a bundler, an import map finds the two packages:

```html
<link rel="stylesheet" href="lib/views/styles.css">
<script type="importmap">
{ "imports": { "@pqax/pqax": "./lib/pqax/index.js", "@pqax/views": "./lib/views/index.js" } }
</script>
```

The model runs in Node too. The views need a DOM.

## The views

Each render function takes a container, the model, and options, and returns a handle:

| Member | What it does |
|---|---|
| `element` | The root of the view: a `div` with the classes `pqax-view` and `pqax-stack`, `pqax-treemap`, or `pqax-strip` |
| `update(model?, options?)` | Draws the view again. The options merge with the current ones. |
| `dispose()` | Removes the view, its listeners, and its resize observer |

A view draws again when the width of its container changes. Every table, tile, or row is an item: it takes the keyboard focus, the arrow keys, Home, and End move between items, Enter or Space acts as a click, and a tooltip shows its figures on hover and on focus. The same figures are the accessible name of the item.

The options that the three share:

| Option | Default | What it does |
|---|---|---|
| `theme` | `auto` | `auto` follows the page; `light` and `dark` force one |
| `locale` | the browser's | The locale of the numbers |
| `label` | none | The accessible name of the view |
| `onSelect` | none | Called with the key of an item when the user clicks it or presses Enter on it: a table key `t:<id>`, or for the treemap the key of a column leaf |

### Storage stack

One bar per table, sorted by waste, the largest first. A bar has the live data, then the four segments of the waste: the non-live files, the unreferenced files, the deleted rows, and the stale columns. After a wider gap comes the overhead: the metadata and the footers together, which are not waste. A component that the file does not have gets a dashed mark after the bar, so that a missing figure does not read as zero: a file of format 0.1 has no unreferenced files. The tooltip of a table adds the files that the log and the listing count, when the non-live files were removed, the commits per day with the days since the last commit, the OPTIMIZE and VACUUM operations, and the partitions with the range of their last change. A segment narrower than 3 pixels is drawn without the gap that separates the others. Below 480 pixels of width, the label goes above the bar.

| Option | Default | What it does |
|---|---|---|
| `mode` | `bytes` | `bytes`: the bars to the scale of the largest table. `shares`: the live data and the waste of every table fill the same width, split by their shares of the data of the table, and the overhead goes beyond, since it is outside that denominator. A toggle switches it. |
| `order` | `waste` | `waste`, `total`, or `label` |
| `showToggle` | `true` | Whether to draw the bytes and shares toggle |
| `maxTables` | all | The most tables to draw, the first in the order |
| `onModeChange` | none | Called when the user changes the mode |

### Column treemap

The children of one node at a time: the sources at first, then the tables of a source, then the columns of a table. A tile that has children shows them inside it, small, when it is at least 72 by 60 pixels. A label is cut with an ellipsis to fit its tile. A click on such a tile, or Enter, opens it; the breadcrumb above goes back, and Backspace or Escape goes up one level. The color is the share of nulls, in six classes. A column without totals, such as a partition column that the files do not store, is a hatched tile at a small share of its parent. A table with stale columns has a leaf for them.

| Option | Default | What it does |
|---|---|---|
| `height` | 420 | The height of the plot, in pixels. Without it, below 480 pixels of width, the plot is as tall as it is wide, at most 60% of the height of the window. |
| `focus` | `root` | The key of the node whose children are shown |
| `absentShare` | 0.01 | The share of its parent that each tile without totals takes |
| `onFocusChange` | none | Called with the key of the new node when the user opens or leaves one |

The classes of the null share: 0, up to 5%, up to 20%, up to 50%, up to 80%, and over 80%, plus one for no totals.

### File strip

One row per table on a log scale shared by all rows: a line from the smallest to the largest file, a box from the first to the third quartile, and a mark at the median. When the footprint keeps the files of the table, a dot is a file and the figures come from the files; otherwise they come from the `fileSizes` distribution of the table. On the right, the share of V-Ordered files. Dashed lines mark the two small file thresholds of the model, 8 MiB and 128 MiB by default, when they fall inside the scale; the tooltip gives the files below each. The ticks are at powers of 1024, with their multiples by 10 and 100 when the range is narrow.

| Option | Default | What it does |
|---|---|---|
| `order` | `size` | `size`: by median, largest first. `count`: by number of files. `label`. |
| `showFiles` | `true` | Whether to draw a dot for each file |
| `maxDots` | 400 | The most dots for one table; beyond it, an even sample of the sorted sizes |
| `rowHeight` | 28 | The height of a row, in pixels |
| `showThreshold` | `true` | Whether to draw the two small file thresholds |

## The view model

`buildModel` is pure: it reads only its input, which `readFootprint` returns or a caller builds by hand. It tolerates a field that the file leaves out, a table that the file does not have, a value of the wrong type, duplicated ids, and a file of format 0.1. A value that cannot be computed is `null`; a stack component whose field is missing is marked `absent` and counts as 0. `readFootprint` reads the `files` and `partitions` tables only when the manifest lists rows for them, and a detail table that cannot be read becomes a warning in `model.warnings`, not an error. `buildModel` takes two options, `smallFileSize`, 128 MiB by default, and `tinyFileSize`, 8 MiB by default.

| Part of the model | Content |
|---|---|
| `collection` | Format version, collector, library, level, times, requests, bytes read, the embedded part, and totals: sources, tables, columns, files, data bytes, rows, footprint, waste, and the tables at each detail depth |
| `sources` | One per source, plus `No source` for the tables that refer to none |
| `tables` | One per table, with its stack, its file sizes, its maintenance, its partitions, its measures, and the fields of format 0.2 when the file has them |
| `columns` | One per column, with its totals and its measures |
| `hasFiles`, `warnings` | Whether the files were read, and what went wrong |

Keys: a table is `t:<id>`, a column `c:<id>`, a source `s:<id>`. A row without a valid or distinct id gets `t#<row>` or `c#<row>`, its position in the file.

### Derived values

| Value | Formula | Needs | Null or absent when |
|---|---|---|---|
| Deleted rows | `fileSize * deletedRowCount / rowCount`. With the files table, the sum of the same over the files of the table. 0 when `deletedRowCount` is 0. | `tables.fileSize`, `rowCount`, `deletedRowCount`; or `files.fileSize`, `rowCount`, `deletedRowCount` | A field is missing or `rowCount` is 0. The files are used only when every file of the table gives an estimate. |
| Live data | `max(0, fileSize - deleted - footerSize - staleColumnSize)`, a missing term counting as 0. The footers and the stale columns are bytes inside the data files. | `fileSize` | `fileSize` is missing |
| Non-live files | With the unreferenced files: `min(nonLiveFileSize, unreferencedFileSize)`. Without them: `nonLiveFileSize`, an upper bound. See below. | `nonLiveFileSize` | It is missing |
| Unreferenced files | `unreferencedFileSize` - the non-live files above | `unreferencedFileSize`, format 0.2 | It is missing: a file of format 0.1, or a listing that failed |
| Waste | non-live + unreferenced + deleted + `staleColumnSize` | | Never: a missing component counts as 0 |
| Overhead | `metadataSize + footerSize`, outside the waste | | Never |
| Data | live data + waste: the denominator of every share | | |
| Stack total | data + overhead, which is `fileSize + metadataSize` plus `unreferencedFileSize` (format 0.2) or `nonLiveFileSize` (format 0.1) when the live bytes are not clipped | | |
| Waste share | waste / data | | The data is 0 |
| Overhead share | overhead / data, shown beyond the data | | The data is 0 |
| File size quartiles | From the files: `sorted[n/4]`, `sorted[n/2]`, `sorted[3n/4]`, rounded down, as the collector computes `fileSizes`. Otherwise the `fileSizes` distribution. | `files.fileSize`, or `tables.fileSizes` | Neither is there |
| V-Order share | From the files: V-Ordered files / files with a known `isVOrdered`, and the same in bytes. Otherwise `vOrderedFileCount / fileCount`. | `files.isVOrdered`, or `tables.vOrderedFileCount` and `fileCount` | Neither is there |
| Small files, very small files | Two thresholds, 128 MiB and 8 MiB by default. From the files: the count below each, and its share. Otherwise a lower bound of the share from the quartiles: 1 when the maximum is below, 0.75 for the third quartile, 0.5 for the median, 0.25 for the first quartile, one file for the minimum. | `files.fileSize`, or `tables.fileSizes` | No quartile is known |
| Last commit | The newest commit read: `snapshotTimestamp`, else the latest `lastAt` of the operations of the table, else `lastModifiedAt` | | None is there |
| Commits per day | commits / max(1, days from `oldestCommitAt` to the last commit). The commits are `commitsRead`, since `oldestCommitAt` is the oldest of them, else `commitCount`. | `commitsRead` or `commitCount`, `oldestCommitAt`, the last commit | A value is missing |
| Days since the last commit | Whole days from the last commit to the end of the collection, else its start, at least 0 | The last commit, `manifest.collection.completedAt` or `startedAt` | A value is missing |
| Non-live files removed | From `nonLiveOldestRemovedAt` to `nonLiveNewestRemovedAt` | Format 0.2 | They are missing |
| Partitions | `partitionCount`, else the rows of the `partitions` table; their last change from the earliest to the latest `partitions.lastModifiedAt` | `partitionCount`, `partitions`, and from format 0.2 `lastModifiedAt` | Neither count is there |
| VACUUM, OPTIMIZE | Whether an operation of the table has the word in its name, and the latest `lastAt` | `operations` | The file has no `operations` table |
| Compression ratio | `uncompressedSize / compressedSize`, of a table or of a column | The two sizes | A size is missing or `compressedSize` is 0 |
| Deleted row share | `deletedRowCount / rowCount` | | |
| Average file size | `fileSize / fileCount` | | |
| Average row group | `rowGroupRowCounts.sum / rowGroupRowCounts.count`, else `rowCount / rowGroupCount` when `footerCount` equals `fileCount` | | |
| Footer coverage | `footerCount / fileCount`: how complete the totals of level 2 are | | |
| Stale column share | `staleColumnSize / compressedSize` | | |
| Non-live share | `nonLiveFileSize / (fileSize + nonLiveFileSize)` | | |
| Average partition | `partitionSizes.sum / partitionSizes.count`, else `fileSize / partitionCount` | | |
| Dictionary fallback | `fallbackChunkCount / dictionaryChunkCount`, of a column, or summed over the columns of a table | | |
| Null share | `nullCount / valueCount`, of a column, or summed over the columns of a node of the treemap | | `valueCount` is 0 or missing |
| Dictionary share | `dictionaryChunkCount / chunkCount` of a column | | |
| Average value length | `unencodedByteArrayDataBytes / (valueCount - nullCount)` of a column | | |
| Files without the column | `fileCountWithoutColumn / fileCount` of its table | | |

These values follow the prototype of the storage views of 7 October 2026 and the decisions of the maintainer of 21 October 2026. The object model does not define derived measures yet; the [backlog](../../../docs/backlog.md) plans it, and this table is the definition until then.

### Non-live and unreferenced files

The two sets overlap. The non-live files are those that left the table and that the log still tracks, whether a VACUUM deleted them or not. The unreferenced files are the data files that a listing of the storage finds and that the version described does not reference: the non-live files still stored, and files that no commit ever referenced or whose remove the log no longer holds. A summary does not say which files are in both.

The stack draws each byte once. With a file of format 0.2 whose listing worked, the two segments sum to `unreferencedFileSize`, what the storage holds: the non-live segment takes as much of it as the non-live files can account for, `min(nonLiveFileSize, unreferencedFileSize)`, and the unreferenced segment the rest. The non-live files beyond that are already deleted and are not drawn; the tooltip gives both counts. With a file of format 0.1, or when the listing failed, the non-live segment is `nonLiveFileSize`, an upper bound, and the unreferenced segment is marked as not in the file.

The model reads the fields of format 0.1 and 0.2. The reader leaves out the fields that it does not know, so a file of a later version gives the same views.

## Styling

Every color is a custom property on `.pqax-view`, with a light and a dark value. The dark values apply under `prefers-color-scheme: dark`, under an ancestor with `data-theme="dark"`, or with the option `theme: "dark"`; the light values under `data-theme="light"` or `theme: "light"`. A page overrides a property on `.pqax-view` or on an ancestor:

```css
.pqax-view { --pqax-gap: var(--page-background); --pqax-focus: var(--page-accent); }
```

| Property | Role |
|---|---|
| `--pqax-font` | The font, `inherit` by default |
| `--pqax-ink`, `--pqax-ink-2`, `--pqax-muted` | Text: primary, secondary, muted |
| `--pqax-grid`, `--pqax-axis`, `--pqax-border` | Gridlines, axes, borders |
| `--pqax-surface`, `--pqax-gap` | The tooltip background, and the gap between segments and tiles |
| `--pqax-focus` | The focus ring |
| `--pqax-live`, `--pqax-non-live`, `--pqax-unreferenced`, `--pqax-deleted`, `--pqax-stale`, `--pqax-overhead` | The segments of the stack |
| `--pqax-absent`, `--pqax-no-totals`, `--pqax-no-totals-ink` | Absent marks, and the tiles without totals |
| `--pqax-seq-0` to `--pqax-seq-5`, `--pqax-seq-ink-0` to `--pqax-seq-ink-5` | The classes of the null share, and the text on them |
| `--pqax-strip-range`, `--pqax-strip-box`, `--pqax-strip-median`, `--pqax-strip-dot` | The marks of the file strip |
| `--pqax-vorder`, `--pqax-vorder-track` | The V-Order bar |

The palette is the reference palette of the dataviz method used for the prototype: a gray for the live data, which is context, the first five categorical hues in their validated order for the four segments of the waste and the overhead, and one blue ramp for the null share. No color is set in the code.

The classes and attributes that a page or a test can rely on:

| View | Elements |
|---|---|
| All | `.pqax-item[data-key]` for each item, `.pqax-tooltip`, `.pqax-legend`, `.pqax-empty` for an empty model |
| Stack | `[data-mode]` on the root, `.pqax-toggle button[data-mode]`, `.pqax-row`, `.pqax-segment[data-component]` with `live`, `nonLive`, `unreferenced`, `deleted`, `stale`, `overhead`, `.pqax-overhead`, `.pqax-absent[data-component]`, `.pqax-value` |
| Treemap | `[data-focus]` on the root, `.pqax-tile[data-kind][data-bin]`, `.pqax-absent`, `nav.pqax-breadcrumb button[data-key]` |
| Strip | `.pqax-strip-row`, `.pqax-range`, `.pqax-box`, `.pqax-median`, `.pqax-dot`, `.pqax-threshold`, `.pqax-vorder-fill`, `.pqax-absent` |

## Decisions

The maintainer decided four questions on 21 October 2026:

| Question | Decision |
|---|---|
| Waste | The non-live files, the unreferenced files, the deleted rows, and the stale columns. The metadata and the footers are overhead: drawn, but outside the waste that sorts the tables and outside the denominator of the shares. |
| Small files | Two shares: below 8 MiB and below 128 MiB, both options of `buildModel` |
| Commits per day | Over the span of the commits read, which ends at the newest of them, with the days since the last commit beside it, counted to the collection |
| Treemap on a phone | As tall as it is wide, at most 60% of the height of the window, below the width where the views take their phone layout |

The others were taken while building the package, and are open to review:

| Question | Decision | Why |
|---|---|---|
| Bundling of the page | None. ES modules and an import map, the packages copied next to the page | Import maps work in Chrome and Edge 89, Firefox 108, and Safari 16.4, all older than what the reader needs |
| Treemap layout | Squarified, written here in about a hundred lines | No dependency |
| What the views take | The whole model, built once, for the three views | The page builds it once and updates the three |
| Metadata and footers | One segment in the stack, two components in the model | As the prototype; both are small next to the others |
| Non-live and unreferenced files | Drawn once: their sum is what the storage holds, the non-live part as large as it can be | A summary does not say which files are in both sets |
| Tiles without totals | 1% of their parent each, hatched | As the prototype; a tile of size 0 would not show |
| Null share colors | Six classes rather than a continuous scale | `color-mix` is not in Chrome 103, and classes give a readable legend |
| Tests | In `tests/typescript-views`, beside the tests of the reader | The vitest configuration of the reader collects every test file of `tests/typescript` |
| A focus on a column | The treemap shows its table | A leaf has no children to show |

## Limits

- The views draw every table and column of the footprint, and the strip a dot for each file up to `maxDots`. A footprint much larger than the one timed below was not tried.
- The treemap shows two levels at a time. A column of a nested type is one tile: the physical columns of the footprint are not drawn.
- The strip draws the quartiles that the file has. The quartiles of a large table are of all its files, even when the footprint keeps only the summary.
- The deleted rows are an estimate: it assumes that a deleted row takes the average size of a row of its file.
- The page reads the whole `files` table when the file has it, into memory.

## Browsers

The tests of the model run in Node and in the three browser engines, and the tests of the views in the browsers only, through Playwright and the browser mode of vitest.

| Engine | Build tested | Browsers on the engine |
|---|---|---|
| Chromium | Chrome Headless Shell 156.0.8078.4 | Chrome, Edge |
| Gecko | Firefox 157.0 | Firefox |
| WebKit | WebKit 27.2 | Safari |

These are the builds of Playwright 1.64.1, run on Windows 11 in October 2026. No test ran in Safari or in Edge themselves, nor on a phone.

The times to build the model and to draw each view, first layout included, on a synthetic footprint of 40 tables with 1,000 columns and 5,000 files, from `tests/typescript-views/performance.browser.test.ts`, on an Intel Core Ultra X7 358H. The test fails only when a view takes 2 seconds or more.

| Step | Chromium 156 | Firefox 157 | WebKit 27.2 |
|---|---|---|---|
| `buildModel` | 5 ms | 5 ms | 7 ms |
| Storage stack | 52 ms | 21 ms | 30 ms |
| Column treemap, at the root | 15 ms | 6 ms | 15 ms |
| Column treemap, on a table of 97 columns | 21 ms | 16 ms | 35 ms |
| File strip, 4,902 dots | 76 ms | 56 ms | 81 ms |

The page was checked in the browser pane of the Claude desktop app, a Chromium, with every valid sample of the kit, at a desktop width and at 375 pixels, light and dark.

## Build and test

The two npm packages are workspaces of the `package.json` at the root of the repository: `npm ci` there installs both and links this package to the reader of the same folder. Build the reader first, since the build of this package takes its declarations from `../pqax/dist`. The scripts run from the folder of the package:

```bash
npm ci                  # at the root of the repository
npm run build --workspace src/typescript/pqax
cd src/typescript/views
npm run typecheck
npm run build           # tsc, into dist/
npm test                # vitest in Node, on the tests in tests/typescript-views at the root of the repository
npm run test:browsers   # every test in Chromium, Firefox, and WebKit, headless, those that render included
npm run site            # after a build of both packages: the site with the page, in .artifacts/site
npm run serve           # serves .artifacts/site at http://127.0.0.1:8080/, and the kit samples under /_samples/
```

The tests import the package as `@pqax/views` and the reader as `@pqax/pqax`, both mapped to their TypeScript sources, so the tests need no build. They read the files of the conformance kit through the module that the configuration of the reader makes. The tests whose name ends with `.browser.test.ts` render the views and run in the browsers only.

The site pqax.tools, in the repository pqax-org/pqax.tools, installs the two packages from npm and deploys them with its own workflow; the page at pqax.org/tools/ redirects there.
