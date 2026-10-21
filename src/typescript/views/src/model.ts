// The view model: pure functions from the tables of a footprint to plain data for the views. Every function tolerates a
// field that the file leaves out, a table that it does not have, a value of the wrong type, and a 0.1 file. A value that
// cannot be computed is null, and a stack component whose field is missing is marked absent.
import type {
  Distribution, FilesTable, Manifest, PqaxDocument, Summary, TablesTable,
} from "@pqax/pqax";

/** What the view model reads: the manifest, the summary tables, and the `files` detail table when it was read. */
export interface FootprintData {
  /** The manifest. Only `formatVersion` and `collection` are read. */
  readonly manifest?: Partial<Manifest> | null;
  /** The summary tables, as `doc.summary` holds them. */
  readonly summary: Summary;
  /** The `files` detail table, when the file has it and it was read. */
  readonly files?: FilesTable | null;
  /** For a footprint read from a VPAX file, the part that holds it. */
  readonly embeddedPart?: string | null;
  /** Problems met while reading, such as a detail part that could not be read. */
  readonly warnings?: readonly string[];
}

/** Options of {@link readFootprint}. */
export interface ReadOptions {
  /** Whether to read the `files` detail table when the file has it. Default true. */
  readonly files?: boolean;
}

/**
 * Reads what the view model needs from an open document: the summary, already read when the file opened, and the
 * `files` detail table when the manifest lists rows for it. A `files` table that cannot be read is left out, with a
 * warning, so that the views still show what the summary has.
 * @param doc A document from `openPqax` or `openEmbedded`.
 * @param options Whether to read the files.
 */
export async function readFootprint(doc: PqaxDocument, options: ReadOptions = {}): Promise<FootprintData> {
  const warnings: string[] = [];
  let files: FilesTable | null = null;
  if (options.files !== false && doc.rowCount("files") > 0) {
    try {
      files = await doc.readTable("files");
    } catch (error) {
      const code = (error as { code?: unknown }).code;
      warnings.push(`The files table could not be read${typeof code === "string" ? ` (${code})` : ""}. The views use the totals of the summary.`);
    }
  }
  return { manifest: doc.manifest, summary: doc.summary, files, embeddedPart: doc.embeddedPart, warnings };
}

// ---- the output ----

/** The whole model of a footprint, which the three views take. */
export interface StorageModel {
  /** The collection line: who collected, when, at which level, and the totals. */
  readonly collection: CollectionModel;
  /** The sources, in the order of the file, plus one for the tables without a source when there are any. */
  readonly sources: readonly SourceModel[];
  /** The tables, in the order of the file. */
  readonly tables: readonly TableModel[];
  /** The columns, in the order of the file. */
  readonly columns: readonly ColumnModel[];
  /** Whether the `files` detail table was read and has rows. */
  readonly hasFiles: boolean;
  /** Problems met while reading. */
  readonly warnings: readonly string[];
}

/** The collection line. */
export interface CollectionModel {
  /** `formatVersion` of the manifest. */
  readonly formatVersion: string | null;
  /** The application that collected, with its version. */
  readonly collector: string | null;
  /** The library that collected, with its version. */
  readonly library: string | null;
  /** The collection level requested, 1 to 4. */
  readonly level: number | null;
  /** When the collection started. */
  readonly startedAt: string | null;
  /** When the collection completed. */
  readonly completedAt: string | null;
  /** The storage requests made. */
  readonly requestCount: number | null;
  /** The bytes read from storage. */
  readonly bytesRead: number | null;
  /** For a footprint read from a VPAX file, the part that holds it. */
  readonly embeddedPart: string | null;
  /** The totals over the tables. */
  readonly totals: CollectionTotals;
  /** The number of tables at each detail depth, by the value of `detailDepth`. */
  readonly depths: Readonly<Record<string, number>>;
}

/** The totals of the collection line. A sum is null when no table has the field. */
export interface CollectionTotals {
  /** The rows of `sources`. */
  readonly sources: number;
  /** The rows of `tables`. */
  readonly tables: number;
  /** The rows of `columns`. */
  readonly columns: number;
  /** The sum of `tables.fileCount`. */
  readonly files: number | null;
  /** The sum of `tables.fileSize`. */
  readonly fileSize: number | null;
  /** The sum of `tables.rowCount`. */
  readonly rowCount: number | null;
  /** The sum of the stacks: data files, non-live files, and metadata. */
  readonly footprint: number;
  /** The sum of the waste of the stacks. */
  readonly waste: number;
}

/** A source: a lakehouse, warehouse, catalog, or folder. */
export interface SourceModel {
  /** `s:<id>`, or `s:none` for the tables without a source. */
  readonly key: string;
  /** The `id` of the source, null for the tables without a source. */
  readonly id: number | null;
  /** The name, else the kind, else a label made from the id. */
  readonly label: string;
  /** `kind` of the source. */
  readonly kind: string | null;
  /** The keys of its tables, in the order of the file. */
  readonly tableKeys: readonly string[];
}

/** The components of a storage stack, in the order the stack draws them. */
export const STACK_COMPONENTS = ["live", "nonLive", "deleted", "metadata", "footers", "stale"] as const;

/** The key of a component of a storage stack. */
export type StackComponentKey = (typeof STACK_COMPONENTS)[number];

/** One component of a storage stack. */
export interface StackComponent {
  /** Which component. */
  readonly key: StackComponentKey;
  /** Its bytes. 0 when absent. */
  readonly bytes: number;
  /** Its share of the stack total, null when the total is 0. */
  readonly share: number | null;
  /** Whether the file lacks the field it needs, so that the bytes are unknown and drawn as 0. */
  readonly absent: boolean;
  /** For the deleted rows: whether the estimate comes from the `files` table or from the table totals. */
  readonly source?: "files" | "table";
}

/** The storage stack of a table: where its bytes go. */
export interface StackModel {
  /** The six components, in the order of {@link STACK_COMPONENTS}. */
  readonly components: readonly StackComponent[];
  /** Their sum: `fileSize + nonLiveFileSize + metadataSize` when the live bytes are not clipped at 0. */
  readonly total: number;
  /** Everything but the live bytes. */
  readonly waste: number;
  /** `waste / total`, null when the total is 0. */
  readonly wasteShare: number | null;
}

/** The file sizes of a table, for the strip. */
export interface FileSizesModel {
  /** Where the figures come from: the `files` table, the `fileSizes` distribution, or nowhere. */
  readonly source: "files" | "table" | null;
  /** The number of files. */
  readonly count: number | null;
  /** The sum of their sizes. */
  readonly sum: number | null;
  /** The smallest file. */
  readonly minimum: number | null;
  /** The first quartile. */
  readonly firstQuartile: number | null;
  /** The median. */
  readonly median: number | null;
  /** The third quartile. */
  readonly thirdQuartile: number | null;
  /** The largest file. */
  readonly maximum: number | null;
  /** Every file size, sorted, when the `files` table has the files of the table. */
  readonly sizes: readonly number[] | null;
  /** The V-Order share, null when the file cannot say. */
  readonly vOrder: VOrderModel | null;
  /** The files smaller than the threshold of the options. */
  readonly smallFiles: SmallFilesModel;
}

/** How many files of a table are V-Ordered. */
export interface VOrderModel {
  /** The V-Ordered files. */
  readonly files: number;
  /** The files whose V-Order is known: the denominator of the share. */
  readonly knownFiles: number;
  /** `files / knownFiles`. */
  readonly share: number;
  /** The share of the bytes in V-Ordered files, from the `files` table only. */
  readonly bytesShare: number | null;
  /** Whether the figures come from the `files` table or from `vOrderedFileCount` and `fileCount`. */
  readonly source: "files" | "table";
}

/** The files below a size. */
export interface SmallFilesModel {
  /** The threshold in bytes. */
  readonly threshold: number;
  /** The exact count, from the `files` table only. */
  readonly count: number | null;
  /** A lower bound of the share, from the quartiles when the `files` table is not there, exact otherwise. */
  readonly shareAtLeast: number | null;
}

/** What the history of a table says about its maintenance. */
export interface MaintenanceModel {
  /** `commitCount`: the commits that the table format keeps. */
  readonly commitCount: number | null;
  /** `commitsRead`: the most recent commits that the collector read. */
  readonly commitsRead: number | null;
  /** `oldestCommitAt`: the oldest of the commits read. */
  readonly oldestCommitAt: string | null;
  /** Commits per day between `oldestCommitAt` and the collection, with a span of one day at least. */
  readonly commitsPerDay: number | null;
  /** Whether a `VACUUM` operation appears. Null when the file has no `operations` table. */
  readonly hasVacuum: boolean | null;
  /** The time of the most recent `VACUUM`. */
  readonly lastVacuumAt: string | null;
  /** Whether an `OPTIMIZE` operation appears. Null when the file has no `operations` table. */
  readonly hasOptimize: boolean | null;
  /** The time of the most recent `OPTIMIZE`. */
  readonly lastOptimizeAt: string | null;
  /** The names of the operations of the table, in the order of the file. */
  readonly operations: readonly string[];
}

/** The derived measures of a table that section 9 of the object model names. Null when a field is missing. */
export interface TableMeasures {
  /** `uncompressedSize / compressedSize`. */
  readonly compressionRatio: number | null;
  /** `deletedRowCount / rowCount`. */
  readonly deletedRowShare: number | null;
  /** `fileSize / fileCount`. */
  readonly averageFileSize: number | null;
  /** `rowGroupRowCounts.sum / rowGroupRowCounts.count`, else `rowCount / rowGroupCount` when every footer was read. */
  readonly averageRowGroupRows: number | null;
  /** `footerCount / fileCount`: how complete the totals of level 2 are. */
  readonly footerCoverage: number | null;
  /** `staleColumnSize / compressedSize`. */
  readonly staleColumnShare: number | null;
  /** `nonLiveFileSize / (fileSize + nonLiveFileSize)`. */
  readonly nonLiveShare: number | null;
  /** `partitionSizes.sum / partitionSizes.count`, else `fileSize / partitionCount`. */
  readonly averagePartitionSize: number | null;
  /** The sum of `fallbackChunkCount` over the sum of `dictionaryChunkCount`, over the columns of the table. */
  readonly dictionaryFallbackShare: number | null;
}

/** A table, with its stack, its file sizes, its maintenance, and its measures. */
export interface TableModel {
  /** `t:<id>`, or `t#<row>` for a row without a valid id. */
  readonly key: string;
  /** The `id` of the table. */
  readonly id: number | null;
  /** The position in `tables`. */
  readonly row: number;
  /** The namespace and the name, joined by dots. */
  readonly label: string;
  /** `name` of the table. */
  readonly name: string | null;
  /** The key of its source. */
  readonly sourceKey: string;
  /** `format`: `Delta`, `Iceberg`, or `Parquet`. */
  readonly format: string | null;
  /** `detailDepth`. */
  readonly detailDepth: string | null;
  /** `collectedLevel`. */
  readonly collectedLevel: number | null;
  /** `fileCount`. */
  readonly fileCount: number | null;
  /** `fileSize`. */
  readonly fileSize: number | null;
  /** `rowCount`. */
  readonly rowCount: number | null;
  /** `deletedRowCount`. */
  readonly deletedRowCount: number | null;
  /** `compressedSize`. */
  readonly compressedSize: number | null;
  /** `uncompressedSize`. */
  readonly uncompressedSize: number | null;
  /** The storage stack. */
  readonly stack: StackModel;
  /** The file sizes and the V-Order share. */
  readonly files: FileSizesModel;
  /** Commits and maintenance operations. */
  readonly maintenance: MaintenanceModel;
  /** The derived measures. */
  readonly measures: TableMeasures;
  /** The keys of its columns, in the order of the file. */
  readonly columnKeys: readonly string[];
}

/** A column, with its totals and its measures. */
export interface ColumnModel {
  /** `c:<id>`, or `c#<row>` for a row without a valid id. */
  readonly key: string;
  /** The `id` of the column. */
  readonly id: number | null;
  /** The key of its table, `t:none` when it refers to no table of the file. */
  readonly tableKey: string;
  /** `name`, else a label made from the id. */
  readonly label: string;
  /** `dataType`. */
  readonly dataType: string | null;
  /** `isPartitionColumn`. */
  readonly isPartitionColumn: boolean | null;
  /** `compressedSize`. */
  readonly compressedSize: number | null;
  /** `uncompressedSize`. */
  readonly uncompressedSize: number | null;
  /** `valueCount`. */
  readonly valueCount: number | null;
  /** `nullCount`. */
  readonly nullCount: number | null;
  /** Whether the column has no `compressedSize`: a partition column, or a collection at level 1. */
  readonly absent: boolean;
  /** `nullCount / valueCount`. */
  readonly nullShare: number | null;
  /** `uncompressedSize / compressedSize`. */
  readonly compressionRatio: number | null;
  /** `dictionaryChunkCount / chunkCount`. */
  readonly dictionaryShare: number | null;
  /** `fallbackChunkCount / dictionaryChunkCount`. */
  readonly dictionaryFallbackShare: number | null;
  /** `unencodedByteArrayDataBytes / (valueCount - nullCount)`: the average length of a string. */
  readonly averageValueLength: number | null;
  /** `fileCountWithoutColumn / fileCount` of the table. */
  readonly filesWithoutColumnShare: number | null;
}

/** Options of {@link buildModel}. */
export interface ModelOptions {
  /** The size below which a file is small, in bytes. Default 128 MiB. */
  readonly smallFileSize?: number;
}

/** The default of {@link ModelOptions.smallFileSize}: 128 MiB. */
export const DEFAULT_SMALL_FILE_SIZE = 128 * 1024 * 1024;

const DAY_MS = 86_400_000;

// ---- reading values defensively ----

type Field = readonly unknown[] | undefined | null;

function at(field: Field, row: number): unknown {
  return Array.isArray(field) ? field[row] : undefined;
}

/** A finite number, else null. */
function num(field: Field, row: number): number | null {
  const value = at(field, row);
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

/** A finite number that is not negative, else null: sizes and counts. */
function size(field: Field, row: number): number | null {
  const value = num(field, row);
  return value !== null && value >= 0 ? value : null;
}

function str(field: Field, row: number): string | null {
  const value = at(field, row);
  return typeof value === "string" ? value : null;
}

function bool(field: Field, row: number): boolean | null {
  const value = at(field, row);
  return typeof value === "boolean" ? value : null;
}

function strings(field: Field, row: number): string[] {
  const value = at(field, row);
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

function distribution(field: Field, row: number): Distribution | null {
  const value = at(field, row);
  return value !== null && typeof value === "object" && !Array.isArray(value) ? (value as Distribution) : null;
}

function member(value: Distribution | null, name: keyof Distribution): number | null {
  const item = value?.[name];
  return typeof item === "number" && Number.isFinite(item) && item >= 0 ? item : null;
}

/** The number of rows of a table: its `length`, else the longest array. */
function lengthOf(table: unknown): number {
  if (table === null || typeof table !== "object") {
    return 0;
  }
  const length = (table as { length?: unknown }).length;
  if (typeof length === "number" && Number.isInteger(length) && length >= 0) {
    return length;
  }
  let longest = 0;
  for (const value of Object.values(table)) {
    if (Array.isArray(value)) {
      longest = Math.max(longest, value.length);
    }
  }
  return longest;
}

function isId(value: number | null): value is number {
  return value !== null && Number.isInteger(value) && value > 0;
}

/** `a / b` when both are known and b is positive, else null. */
export function ratio(a: number | null, b: number | null): number | null {
  return a !== null && b !== null && b > 0 ? a / b : null;
}

function sumKnown(values: readonly (number | null)[]): number | null {
  let total: number | null = null;
  for (const value of values) {
    if (value !== null) {
      total = (total ?? 0) + value;
    }
  }
  return total;
}

function time(value: string | null | undefined): number | null {
  if (typeof value !== "string") {
    return null;
  }
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function later(a: string | null, b: string | null): string | null {
  if (a === null) {
    return b;
  }
  if (b === null) {
    return a;
  }
  return (time(b) ?? -Infinity) > (time(a) ?? -Infinity) ? b : a;
}

// ---- derived values, one function each ----

/**
 * The bytes that the deleted rows of a table hold, estimated from the share of the rows: `fileSize * deletedRowCount /
 * rowCount`. 0 when `deletedRowCount` is 0, null when a field is missing or `rowCount` is not positive.
 */
export function deletedEstimate(fileSize: number | null, deletedRowCount: number | null, rowCount: number | null): number | null {
  if (deletedRowCount === 0) {
    return 0;
  }
  if (fileSize === null || deletedRowCount === null || rowCount === null || rowCount <= 0) {
    return null;
  }
  return (fileSize * Math.min(deletedRowCount, rowCount)) / rowCount;
}

/**
 * The quartiles of a set of sizes taken by position, as the collector computes `fileSizes`: `sorted[n/4]`,
 * `sorted[n/2]`, `sorted[3n/4]`, rounded down. Null for no value.
 * @param sorted The values, sorted in ascending order.
 */
export function quartiles(sorted: readonly number[]): { minimum: number; firstQuartile: number; median: number; thirdQuartile: number; maximum: number } | null {
  const n = sorted.length;
  if (n === 0) {
    return null;
  }
  return {
    minimum: sorted[0]!,
    firstQuartile: sorted[Math.floor(n / 4)]!,
    median: sorted[Math.floor(n / 2)]!,
    thirdQuartile: sorted[Math.floor((3 * n) / 4)]!,
    maximum: sorted[n - 1]!,
  };
}

/**
 * A lower bound of the share of the files below a threshold, from a distribution: 1 when the maximum is below it, 0.75
 * when the third quartile is, 0.5 for the median, 0.25 for the first quartile, a share of one file for the minimum, and
 * 0 otherwise. Null when the distribution has no quartile.
 */
export function smallShareAtLeast(dist: { count: number | null; minimum: number | null; firstQuartile: number | null; median: number | null; thirdQuartile: number | null; maximum: number | null }, threshold: number): number | null {
  const { count, minimum, firstQuartile, median, thirdQuartile, maximum } = dist;
  if (maximum !== null && maximum < threshold) {
    return 1;
  }
  if (thirdQuartile !== null && thirdQuartile < threshold) {
    return 0.75;
  }
  if (median !== null && median < threshold) {
    return 0.5;
  }
  if (firstQuartile !== null && firstQuartile < threshold) {
    return 0.25;
  }
  if (minimum !== null && minimum < threshold) {
    return count !== null && count > 0 ? 1 / count : 0;
  }
  return minimum === null && firstQuartile === null && median === null && thirdQuartile === null && maximum === null ? null : 0;
}

/**
 * Commits per day: the commits read, else `commitCount`, over the days from `oldestCommitAt` to the reference time, at
 * least one day. The reference is the end of the collection, else its start, else `snapshotTimestamp`. Null when a
 * value is missing.
 */
export function commitsPerDay(commits: number | null, oldestCommitAt: string | null, reference: string | null): number | null {
  const oldest = time(oldestCommitAt);
  const end = time(reference);
  if (commits === null || oldest === null || end === null) {
    return null;
  }
  return commits / Math.max(1, (end - oldest) / DAY_MS);
}

/** A stack from its six inputs, as the README defines it. A null input is an absent component. */
export function stackOf(input: {
  fileSize: number | null;
  deleted: number | null;
  footerSize: number | null;
  staleColumnSize: number | null;
  nonLiveFileSize: number | null;
  metadataSize: number | null;
  deletedSource?: "files" | "table";
}): StackModel {
  const { fileSize, deleted, footerSize, staleColumnSize, nonLiveFileSize, metadataSize } = input;
  const live = fileSize === null ? null : Math.max(0, fileSize - (deleted ?? 0) - (footerSize ?? 0) - (staleColumnSize ?? 0));
  const values: Record<StackComponentKey, number | null> = {
    live, nonLive: nonLiveFileSize, deleted, metadata: metadataSize, footers: footerSize, stale: staleColumnSize,
  };
  let total = 0;
  for (const key of STACK_COMPONENTS) {
    total += values[key] ?? 0;
  }
  const components = STACK_COMPONENTS.map((key): StackComponent => {
    const bytes = values[key] ?? 0;
    const component: StackComponent = { key, bytes, share: total > 0 ? bytes / total : null, absent: values[key] === null };
    return key === "deleted" && input.deletedSource !== undefined && values[key] !== null ? { ...component, source: input.deletedSource } : component;
  });
  const waste = total - (live ?? 0);
  return { components, total, waste, wasteShare: total > 0 ? waste / total : null };
}

// ---- the model ----

/** What the `files` table says about one table. */
interface FilesOfTable {
  sizes: number[];
  vOrdered: number;
  vOrderKnown: number;
  vOrderedBytes: number;
  vOrderKnownBytes: number;
  deleted: number;
  deletedKnown: number;
}

function filesByTable(files: FilesTable | null | undefined): Map<number, FilesOfTable> {
  const result = new Map<number, FilesOfTable>();
  if (!files) {
    return result;
  }
  const rows = lengthOf(files);
  for (let i = 0; i < rows; i++) {
    const table = num(files.table, i);
    if (!isId(table)) {
      continue;
    }
    let entry = result.get(table);
    if (entry === undefined) {
      entry = { sizes: [], vOrdered: 0, vOrderKnown: 0, vOrderedBytes: 0, vOrderKnownBytes: 0, deleted: 0, deletedKnown: 0 };
      result.set(table, entry);
    }
    const fileSize = size(files.fileSize, i);
    if (fileSize !== null) {
      entry.sizes.push(fileSize);
    }
    const vOrdered = bool(files.isVOrdered, i);
    if (vOrdered !== null) {
      entry.vOrderKnown++;
      entry.vOrderKnownBytes += fileSize ?? 0;
      if (vOrdered) {
        entry.vOrdered++;
        entry.vOrderedBytes += fileSize ?? 0;
      }
    }
    // Only a file with a size counts, so that the files that give an estimate are among the sizes.
    const estimate = fileSize === null ? null : deletedEstimate(fileSize, size(files.deletedRowCount, i), size(files.rowCount, i));
    if (estimate !== null) {
      entry.deleted += estimate;
      entry.deletedKnown++;
    }
  }
  for (const entry of result.values()) {
    entry.sizes.sort((a, b) => a - b);
  }
  return result;
}

function tableLabel(tables: TablesTable, row: number, id: number | null): string {
  const name = str(tables.name, row);
  const parts = [...strings(tables.namespace, row), name ?? (id !== null ? `Table ${id}` : `Table at row ${row + 1}`)];
  return parts.join(".");
}

/**
 * Builds the model of a footprint: the collection line, the sources, the tables with their stacks, file sizes,
 * maintenance, and measures, and the columns. Pure: it reads only its input.
 * @param data What {@link readFootprint} returns, or the same built by hand.
 * @param options The small file threshold.
 */
export function buildModel(data: FootprintData, options: ModelOptions = {}): StorageModel {
  const threshold = options.smallFileSize !== undefined && options.smallFileSize > 0 ? options.smallFileSize : DEFAULT_SMALL_FILE_SIZE;
  const summary: Summary = data.summary ?? {};
  const collectionInfo = (data.manifest?.collection ?? null) as Partial<Manifest["collection"]> | null;
  const tables = summary.tables ?? ({ length: 0 } as TablesTable);
  const columnsTable = summary.columns ?? { length: 0 };
  const sourcesTable = summary.sources ?? { length: 0 };
  const operations = summary.operations;
  const byTable = filesByTable(data.files);
  const reference = typeof collectionInfo?.completedAt === "string" ? collectionInfo.completedAt
    : typeof collectionInfo?.startedAt === "string" ? collectionInfo.startedAt : null;

  // Operations by table id.
  const opsByTable = new Map<number, { name: string; lastAt: string | null }[]>();
  for (let i = 0; i < lengthOf(operations); i++) {
    const table = num(operations?.table, i);
    const name = str(operations?.name, i);
    if (isId(table) && name !== null) {
      const list = opsByTable.get(table) ?? [];
      list.push({ name, lastAt: str(operations?.lastAt, i) });
      opsByTable.set(table, list);
    }
  }

  // Columns, first by table id, for the measures of the tables.
  const columnRows = lengthOf(columnsTable);
  const columnsByTable = new Map<number, number[]>();
  for (let i = 0; i < columnRows; i++) {
    const table = num(columnsTable.table, i);
    if (isId(table)) {
      const list = columnsByTable.get(table) ?? [];
      list.push(i);
      columnsByTable.set(table, list);
    }
  }

  const tableRows = lengthOf(tables);
  const tableKeyById = new Map<number, string>();
  const tableModels: TableModel[] = [];
  const fileCountById = new Map<number, number | null>();
  const sourceIds = new Set<number>();
  for (let i = 0; i < lengthOf(sourcesTable); i++) {
    const id = num(sourcesTable.id, i);
    if (isId(id)) {
      sourceIds.add(id);
    }
  }

  for (let row = 0; row < tableRows; row++) {
    const id = num(tables.id, row);
    const validId = isId(id) && !tableKeyById.has(id) ? id : null;
    const key = validId !== null ? `t:${validId}` : `t#${row}`;
    if (validId !== null) {
      tableKeyById.set(validId, key);
    }
    const source = num(tables.source, row);
    const sourceKey = isId(source) && sourceIds.has(source) ? `s:${source}` : "s:none";
    const fileCount = size(tables.fileCount, row);
    const fileSize = size(tables.fileSize, row);
    const rowCount = size(tables.rowCount, row);
    const deletedRowCount = size(tables.deletedRowCount, row);
    const compressedSize = size(tables.compressedSize, row);
    const uncompressedSize = size(tables.uncompressedSize, row);
    const footerSize = size(tables.footerSize, row);
    const staleColumnSize = size(tables.staleColumnSize, row);
    const nonLiveFileSize = size(tables.nonLiveFileSize, row);
    const metadataSize = size(tables.metadataSize, row);
    if (validId !== null) {
      fileCountById.set(validId, fileCount);
    }
    const fromFiles = validId !== null ? byTable.get(validId) : undefined;

    // The deleted rows: from the files when every file of the table that has rows gives an estimate.
    let deleted = deletedEstimate(fileSize, deletedRowCount, rowCount);
    let deletedSource: "files" | "table" | undefined = deleted !== null ? "table" : undefined;
    if (fromFiles !== undefined && fromFiles.deletedKnown > 0 && fromFiles.deletedKnown === fromFiles.sizes.length) {
      deleted = fromFiles.deleted;
      deletedSource = "files";
    }
    const stack = stackOf({ fileSize, deleted, footerSize, staleColumnSize, nonLiveFileSize, metadataSize, deletedSource });

    // The file sizes.
    let files: FileSizesModel;
    if (fromFiles !== undefined && fromFiles.sizes.length > 0) {
      const q = quartiles(fromFiles.sizes)!;
      const small = fromFiles.sizes.filter((value) => value < threshold).length;
      files = {
        source: "files", count: fromFiles.sizes.length, sum: fromFiles.sizes.reduce((a, b) => a + b, 0), ...q,
        sizes: fromFiles.sizes, vOrder: null,
        smallFiles: { threshold, count: small, shareAtLeast: small / fromFiles.sizes.length },
      };
    } else {
      const dist = distribution(tables.fileSizes, row);
      const figures = {
        count: member(dist, "count") ?? fileCount, sum: member(dist, "sum") ?? fileSize,
        minimum: member(dist, "minimum"), firstQuartile: member(dist, "firstQuartile"), median: member(dist, "median"),
        thirdQuartile: member(dist, "thirdQuartile"), maximum: member(dist, "maximum"),
      };
      const hasShape = figures.minimum !== null || figures.median !== null || figures.maximum !== null;
      files = {
        source: hasShape ? "table" : null, ...figures, sizes: null, vOrder: null,
        smallFiles: { threshold, count: null, shareAtLeast: smallShareAtLeast(figures, threshold) },
      };
    }
    let vOrder: VOrderModel | null = null;
    if (fromFiles !== undefined && fromFiles.vOrderKnown > 0) {
      vOrder = {
        files: fromFiles.vOrdered, knownFiles: fromFiles.vOrderKnown, share: fromFiles.vOrdered / fromFiles.vOrderKnown,
        bytesShare: ratio(fromFiles.vOrderedBytes, fromFiles.vOrderKnownBytes), source: "files",
      };
    } else {
      const vOrdered = size(tables.vOrderedFileCount, row);
      if (vOrdered !== null && fileCount !== null && fileCount > 0) {
        vOrder = { files: vOrdered, knownFiles: fileCount, share: Math.min(1, vOrdered / fileCount), bytesShare: null, source: "table" };
      }
    }
    files = { ...files, vOrder };

    // Maintenance.
    const ops = validId !== null ? opsByTable.get(validId) ?? [] : [];
    const hasOps = operations !== undefined;
    let lastVacuumAt: string | null = null;
    let lastOptimizeAt: string | null = null;
    let hasVacuum = false;
    let hasOptimize = false;
    for (const op of ops) {
      const name = op.name.toUpperCase();
      if (name.includes("VACUUM")) {
        hasVacuum = true;
        lastVacuumAt = later(lastVacuumAt, op.lastAt);
      }
      if (name.includes("OPTIMIZE")) {
        hasOptimize = true;
        lastOptimizeAt = later(lastOptimizeAt, op.lastAt);
      }
    }
    const commitCount = size(tables.commitCount, row);
    const commitsRead = size(tables.commitsRead, row);
    const oldestCommitAt = str(tables.oldestCommitAt, row);
    const maintenance: MaintenanceModel = {
      commitCount, commitsRead, oldestCommitAt,
      commitsPerDay: commitsPerDay(commitsRead ?? commitCount, oldestCommitAt, reference ?? str(tables.snapshotTimestamp, row)),
      hasVacuum: hasOps ? hasVacuum : null, lastVacuumAt,
      hasOptimize: hasOps ? hasOptimize : null, lastOptimizeAt,
      operations: ops.map((op) => op.name),
    };

    // Measures.
    const rowGroups = distribution(tables.rowGroupRowCounts, row);
    const footerCount = size(tables.footerCount, row);
    const rowGroupCount = size(tables.rowGroupCount, row);
    const partitions = distribution(tables.partitionSizes, row);
    let fallback: number | null = null;
    let dictionary: number | null = null;
    for (const c of validId !== null ? columnsByTable.get(validId) ?? [] : []) {
      const f = size(columnsTable.fallbackChunkCount, c);
      const d = size(columnsTable.dictionaryChunkCount, c);
      if (f !== null && d !== null) {
        fallback = (fallback ?? 0) + f;
        dictionary = (dictionary ?? 0) + d;
      }
    }
    const measures: TableMeasures = {
      compressionRatio: ratio(uncompressedSize, compressedSize),
      deletedRowShare: ratio(deletedRowCount, rowCount),
      averageFileSize: ratio(fileSize, fileCount),
      averageRowGroupRows: ratio(member(rowGroups, "sum"), member(rowGroups, "count"))
        ?? (footerCount !== null && footerCount === fileCount ? ratio(rowCount, rowGroupCount) : null),
      footerCoverage: ratio(footerCount, fileCount),
      staleColumnShare: ratio(staleColumnSize, compressedSize),
      nonLiveShare: fileSize !== null && nonLiveFileSize !== null ? ratio(nonLiveFileSize, fileSize + nonLiveFileSize) : null,
      averagePartitionSize: ratio(member(partitions, "sum"), member(partitions, "count")) ?? ratio(fileSize, size(tables.partitionCount, row)),
      dictionaryFallbackShare: ratio(fallback, dictionary),
    };

    tableModels.push({
      key, id: validId, row, label: tableLabel(tables, row, validId), name: str(tables.name, row), sourceKey,
      format: str(tables.format, row), detailDepth: str(tables.detailDepth, row), collectedLevel: num(tables.collectedLevel, row),
      fileCount, fileSize, rowCount, deletedRowCount, compressedSize, uncompressedSize,
      stack, files, maintenance, measures, columnKeys: [],
    });
  }

  // Columns.
  const columnModels: ColumnModel[] = [];
  const columnKeysByTable = new Map<string, string[]>();
  const seenColumns = new Set<number>();
  for (let row = 0; row < columnRows; row++) {
    const id = num(columnsTable.id, row);
    const validId = isId(id) && !seenColumns.has(id) ? id : null;
    if (validId !== null) {
      seenColumns.add(validId);
    }
    const key = validId !== null ? `c:${validId}` : `c#${row}`;
    const table = num(columnsTable.table, row);
    const tableKey = isId(table) ? tableKeyById.get(table) ?? "t:none" : "t:none";
    const compressedSize = size(columnsTable.compressedSize, row);
    const uncompressedSize = size(columnsTable.uncompressedSize, row);
    const valueCount = size(columnsTable.valueCount, row);
    const nullCount = size(columnsTable.nullCount, row);
    const dictionaryChunkCount = size(columnsTable.dictionaryChunkCount, row);
    const nonNull = valueCount !== null && nullCount !== null ? valueCount - nullCount : null;
    columnModels.push({
      key, id: validId, tableKey,
      label: str(columnsTable.name, row) ?? (validId !== null ? `Column ${validId}` : `Column at row ${row + 1}`),
      dataType: str(columnsTable.dataType, row), isPartitionColumn: bool(columnsTable.isPartitionColumn, row),
      compressedSize, uncompressedSize, valueCount, nullCount, absent: compressedSize === null,
      nullShare: nullCount !== null && valueCount !== null && valueCount > 0 ? Math.min(1, nullCount / valueCount) : null,
      compressionRatio: ratio(uncompressedSize, compressedSize),
      dictionaryShare: ratio(dictionaryChunkCount, size(columnsTable.chunkCount, row)),
      dictionaryFallbackShare: ratio(size(columnsTable.fallbackChunkCount, row), dictionaryChunkCount),
      averageValueLength: ratio(size(columnsTable.unencodedByteArrayDataBytes, row), nonNull),
      filesWithoutColumnShare: isId(table) ? ratio(size(columnsTable.fileCountWithoutColumn, row), fileCountById.get(table) ?? null) : null,
    });
    const list = columnKeysByTable.get(tableKey) ?? [];
    list.push(key);
    columnKeysByTable.set(tableKey, list);
  }
  const tablesWithColumns = tableModels.map((t) => ({ ...t, columnKeys: columnKeysByTable.get(t.key) ?? [] }));

  // Sources, then one for the tables without a source.
  const sources: SourceModel[] = [];
  const seenSources = new Set<number>();
  for (let row = 0; row < lengthOf(sourcesTable); row++) {
    const id = num(sourcesTable.id, row);
    if (!isId(id) || seenSources.has(id)) {
      continue;
    }
    seenSources.add(id);
    const key = `s:${id}`;
    sources.push({
      key, id, label: str(sourcesTable.name, row) ?? str(sourcesTable.kind, row) ?? `Source ${id}`,
      kind: str(sourcesTable.kind, row), tableKeys: tablesWithColumns.filter((t) => t.sourceKey === key).map((t) => t.key),
    });
  }
  const orphans = tablesWithColumns.filter((t) => t.sourceKey === "s:none").map((t) => t.key);
  if (orphans.length > 0) {
    sources.push({ key: "s:none", id: null, label: "No source", kind: null, tableKeys: orphans });
  }

  const depths: Record<string, number> = {};
  for (const t of tablesWithColumns) {
    const depth = t.detailDepth ?? "Unknown";
    depths[depth] = (depths[depth] ?? 0) + 1;
  }
  const app = typeof collectionInfo?.collectorApp === "string" ? collectionInfo.collectorApp : null;
  const appVersion = typeof collectionInfo?.collectorAppVersion === "string" ? collectionInfo.collectorAppVersion : null;
  const lib = typeof collectionInfo?.collectorLib === "string" ? collectionInfo.collectorLib : null;
  const libVersion = typeof collectionInfo?.collectorLibVersion === "string" ? collectionInfo.collectorLibVersion : null;
  const numberOf = (value: unknown): number | null => (typeof value === "number" && Number.isFinite(value) ? value : null);
  const collection: CollectionModel = {
    formatVersion: typeof data.manifest?.formatVersion === "string" ? data.manifest.formatVersion : null,
    collector: app !== null ? (appVersion !== null ? `${app} ${appVersion}` : app) : null,
    library: lib !== null ? (libVersion !== null ? `${lib} ${libVersion}` : lib) : null,
    level: numberOf(collectionInfo?.level),
    startedAt: typeof collectionInfo?.startedAt === "string" ? collectionInfo.startedAt : null,
    completedAt: typeof collectionInfo?.completedAt === "string" ? collectionInfo.completedAt : null,
    requestCount: numberOf(collectionInfo?.requestCount),
    bytesRead: numberOf(collectionInfo?.bytesRead),
    embeddedPart: data.embeddedPart ?? null,
    totals: {
      sources: sources.filter((s) => s.id !== null).length,
      tables: tableRows,
      columns: columnRows,
      files: sumKnown(tablesWithColumns.map((t) => t.fileCount)),
      fileSize: sumKnown(tablesWithColumns.map((t) => t.fileSize)),
      rowCount: sumKnown(tablesWithColumns.map((t) => t.rowCount)),
      footprint: tablesWithColumns.reduce((sum, t) => sum + t.stack.total, 0),
      waste: tablesWithColumns.reduce((sum, t) => sum + t.stack.waste, 0),
    },
    depths,
  };

  return {
    collection, sources, tables: tablesWithColumns, columns: columnModels,
    hasFiles: lengthOf(data.files) > 0, warnings: [...(data.warnings ?? [])],
  };
}

// ---- orders and the tree of the treemap ----

/** How {@link sortTables} orders the tables. */
export type TableOrder = "waste" | "total" | "label";

/**
 * The tables in a new array, sorted: by waste, by stack total, or by label. Ties go by total, then by label, then by
 * the order of the file, so that the order is stable.
 */
export function sortTables(tables: readonly TableModel[], order: TableOrder = "waste"): TableModel[] {
  const byLabel = (a: TableModel, b: TableModel) => (a.label < b.label ? -1 : a.label > b.label ? 1 : 0);
  return [...tables].sort((a, b) => {
    if (order === "label") {
      return byLabel(a, b) || a.row - b.row;
    }
    const primary = order === "waste" ? b.stack.waste - a.stack.waste : 0;
    return primary || b.stack.total - a.stack.total || byLabel(a, b) || a.row - b.row;
  });
}

/** The kind of a node of {@link columnTree}. */
export type TreeNodeKind = "root" | "source" | "table" | "column" | "stale";

/** A node of the tree of the treemap: sources, tables, columns. */
export interface TreeNode {
  /** `root`, or the key of the source, table, or column. A stale leaf is `<table key>/stale`. */
  readonly key: string;
  /** What the node is. */
  readonly kind: TreeNodeKind;
  /** What the view writes on the node. */
  readonly label: string;
  /** The compressed bytes: the sum of the children for an inner node. 0 when absent. */
  readonly size: number;
  /** Whether the node has no totals: a column without `compressedSize`, or a table or source whose columns have none. */
  readonly absent: boolean;
  /** For a table without column totals, whether its size comes from `tables.compressedSize`. */
  readonly sizeSource: "columns" | "table";
  /** The null values over the values, summed over the columns that have both counts. */
  readonly nullShare: number | null;
  /** The sum of `nullCount` over those columns. */
  readonly nullCount: number | null;
  /** The sum of `valueCount` over those columns. */
  readonly valueCount: number | null;
  /** Uncompressed over compressed, over the columns that have both sizes. */
  readonly compressionRatio: number | null;
  /** The children, largest first, absent last. */
  readonly children: readonly TreeNode[];
  /** For a column leaf, its model. */
  readonly column?: ColumnModel;
  /** For a table node, its model. */
  readonly table?: TableModel;
  /** For a source node, its model. */
  readonly source?: SourceModel;
}

interface Totals {
  size: number;
  nullCount: number | null;
  valueCount: number | null;
  uncompressed: number;
  compressed: number;
}

function addTotals(totals: Totals, node: { size: number; nullCount: number | null; valueCount: number | null }, uncompressed: number | null, compressed: number | null): void {
  totals.size += node.size;
  if (node.nullCount !== null && node.valueCount !== null) {
    totals.nullCount = (totals.nullCount ?? 0) + node.nullCount;
    totals.valueCount = (totals.valueCount ?? 0) + node.valueCount;
  }
  if (uncompressed !== null && compressed !== null) {
    totals.uncompressed += uncompressed;
    totals.compressed += compressed;
  }
}

function bySize(a: TreeNode, b: TreeNode): number {
  return Number(a.absent) - Number(b.absent) || b.size - a.size || (a.label < b.label ? -1 : a.label > b.label ? 1 : 0);
}

function node(key: string, kind: TreeNodeKind, label: string, totals: Totals, children: TreeNode[], extra: Partial<TreeNode> = {}): TreeNode {
  return {
    key, kind, label, size: totals.size, absent: totals.size <= 0, sizeSource: "columns",
    nullShare: totals.nullCount !== null && totals.valueCount !== null && totals.valueCount > 0 ? totals.nullCount / totals.valueCount : null,
    nullCount: totals.nullCount, valueCount: totals.valueCount,
    compressionRatio: totals.compressed > 0 ? totals.uncompressed / totals.compressed : null,
    children: children.sort(bySize), ...extra,
  };
}

/**
 * The tree of the treemap: the root, the sources, their tables, and the columns of each table, sized by compressed
 * bytes. A table with stale columns gets one more leaf for `staleColumnSize`. A table whose columns have no totals takes
 * `tables.compressedSize` when the file has it, and is absent otherwise. A source with one table or more is kept even
 * when all are absent.
 */
export function columnTree(model: StorageModel): TreeNode {
  const columnsByTable = new Map<string, ColumnModel[]>();
  for (const column of model.columns) {
    const list = columnsByTable.get(column.tableKey) ?? [];
    list.push(column);
    columnsByTable.set(column.tableKey, list);
  }
  const tableByKey = new Map(model.tables.map((t) => [t.key, t]));
  const rootTotals: Totals = { size: 0, nullCount: null, valueCount: null, uncompressed: 0, compressed: 0 };
  const sourceNodes: TreeNode[] = [];
  for (const source of model.sources) {
    const sourceTotals: Totals = { size: 0, nullCount: null, valueCount: null, uncompressed: 0, compressed: 0 };
    const tableNodes: TreeNode[] = [];
    for (const tableKey of source.tableKeys) {
      const table = tableByKey.get(tableKey);
      if (table === undefined) {
        continue;
      }
      const totals: Totals = { size: 0, nullCount: null, valueCount: null, uncompressed: 0, compressed: 0 };
      const leaves: TreeNode[] = [];
      for (const column of columnsByTable.get(tableKey) ?? []) {
        const leafTotals: Totals = { size: column.compressedSize ?? 0, nullCount: column.nullCount, valueCount: column.valueCount, uncompressed: column.uncompressedSize ?? 0, compressed: column.compressedSize ?? 0 };
        const leaf = node(column.key, "column", column.label, leafTotals, [], {
          absent: column.absent, column,
          compressionRatio: column.compressionRatio,
          nullShare: column.nullShare,
        });
        leaves.push(leaf);
        addTotals(totals, leaf, column.uncompressedSize, column.compressedSize);
      }
      const stale = table.stack.components.find((c) => c.key === "stale");
      if (stale !== undefined && !stale.absent && stale.bytes > 0) {
        const staleNode = node(`${tableKey}/stale`, "stale", "Stale columns", { size: stale.bytes, nullCount: null, valueCount: null, uncompressed: 0, compressed: 0 }, []);
        leaves.push(staleNode);
        totals.size += stale.bytes;
      }
      let extra: Partial<TreeNode> = { table };
      if (totals.size <= 0 && table.compressedSize !== null && table.compressedSize > 0) {
        totals.size = table.compressedSize;
        extra = { ...extra, sizeSource: "table", absent: false };
      }
      const tableNode = node(tableKey, "table", table.label, totals, leaves, extra);
      tableNodes.push(tableNode);
      addTotals(sourceTotals, tableNode, null, null);
      sourceTotals.uncompressed += totals.uncompressed;
      sourceTotals.compressed += totals.compressed;
    }
    const sourceNode = node(source.key, "source", source.label, sourceTotals, tableNodes, { source });
    sourceNodes.push(sourceNode);
    addTotals(rootTotals, sourceNode, null, null);
    rootTotals.uncompressed += sourceTotals.uncompressed;
    rootTotals.compressed += sourceTotals.compressed;
  }
  return node("root", "root", "All sources", rootTotals, sourceNodes);
}

/** Finds a node of a tree by its key, with the path from the root to it. Null when no node has the key. */
export function findPath(root: TreeNode, key: string): TreeNode[] | null {
  if (root.key === key) {
    return [root];
  }
  for (const child of root.children) {
    const path = findPath(child, key);
    if (path !== null) {
      return [root, ...path];
    }
  }
  return null;
}
