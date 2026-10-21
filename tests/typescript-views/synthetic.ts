// A synthetic footprint for the tests of the views: a deterministic builder, from a seeded generator, of the data that
// the view model reads. By default 3 sources, 40 tables, 1,000 columns, and 5,000 files in format 0.2, with every field
// that the model reads, a `files` table whose sums are the totals of the tables, and a `partitions` table for the
// partitioned tables. The unreferenced files of a table are more than its non-live files, fewer, as many, or none, so
// that the overlap rule of the model meets every case. Options drop fields or tables, to look like a file of format 0.1,
// of an early 0.1 producer, or of a collection at level 1. Every name is made up: source_a, table_017, col_0042. Nothing
// here imports a module of Node, so that the tests run in browsers too.
import type { FilesTable, Manifest, PartitionsTable, Summary } from "@pqax/pqax";
import type { FootprintData } from "@pqax/views";

/** The reference time of the synthetic collection: when it completed. */
export const COMPLETED_AT = "2026-10-05T20:55:12.749Z";
/** When the synthetic collection started. */
export const STARTED_AT = "2026-10-05T20:50:00.000Z";

/** The fields of `tables` that format 0.2 added: the removal times of the non-live files and the unreferenced files. */
export const FORMAT_02_TABLE_FIELDS = [
  "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt", "unreferencedFileCount", "unreferencedFileSize",
] as const;

/** The fields of `partitions` that format 0.2 added. */
export const FORMAT_02_PARTITION_FIELDS = ["lastModifiedAt"] as const;

/** The fields of `tables` that the model reads and that an early 0.1 producer could leave out, the fields of 0.2 included. */
export const EARLY_TABLE_FIELDS = [
  "nonLiveFileCount", "nonLiveFileSize", "metadataFileCount", "metadataSize", "staleColumnSize", "vOrderedFileCount",
  "commitsRead", "oldestCommitAt", ...FORMAT_02_TABLE_FIELDS,
] as const;

/** The fields of `tables` that only the footers give, left out at level 1. */
export const FOOTER_TABLE_FIELDS = [
  "footerCount", "rowGroupCount", "compressedSize", "uncompressedSize", "footerSize", "staleColumnSize",
  "columnTotalsSource", "rowGroupRowCounts",
] as const;

/** The fields of `columns` that only the footers give, left out at level 1. */
export const FOOTER_COLUMN_FIELDS = [
  "compressedSize", "uncompressedSize", "valueCount", "nullCount", "chunkCount", "dictionaryChunkCount",
  "fallbackChunkCount", "dictionaryPageSize", "unencodedByteArrayDataBytes", "codecs", "encodings",
  "fileCountWithoutColumn",
] as const;

/** The fields of `files` that only the footers give, left out at level 1. */
export const FOOTER_FILE_FIELDS = ["footerSize", "rowGroupCount", "compressedSize", "uncompressedSize"] as const;

/** The options of {@link syntheticFootprint}. */
export interface SyntheticOptions {
  /** The seed of the generator. Default 42. */
  readonly seed?: number;
  /** The sources. Default 3. */
  readonly sources?: number;
  /** The tables, spread over the sources in turn. Default 40. */
  readonly tables?: number;
  /** The columns, two per table at least. Default 1,000. */
  readonly columns?: number;
  /** The files, one per table at least. Default 5,000. */
  readonly files?: number;
  /** 2 gives the totals of the footers; 1 leaves out every field that only the footers give. Default 2. */
  readonly level?: 1 | 2;
  /** Whether the result has the `files` table. Default true. */
  readonly withFiles?: boolean;
  /** Whether the result has the `partitions` table. Default true. */
  readonly withPartitions?: boolean;
  /** "0.1" leaves out the fields that format 0.2 added, in `tables` and in `partitions`. Default "0.2". */
  readonly formatVersion?: "0.1" | "0.2";
  /** The smallest and the largest file size: the sizes are spread evenly on a log scale between them. Default each table around a size of its own, from 4 KB to 1 GB. */
  readonly sizeRange?: readonly [number, number];
  /** Fields of `tables` to leave out, such as {@link EARLY_TABLE_FIELDS}. */
  readonly dropTableFields?: readonly string[];
  /** Fields of `columns` to leave out. */
  readonly dropColumnFields?: readonly string[];
  /** Fields of `files` to leave out. */
  readonly dropFileFields?: readonly string[];
  /** Summary tables to leave out. */
  readonly dropTables?: readonly ("sources" | "tables" | "columns" | "operations")[];
}

/** What the builder knows about each table, for the tests to check against. */
export interface SyntheticTable {
  readonly id: number;
  readonly name: string;
  readonly source: number;
  /** The rows of `files` of the table, in order. */
  readonly fileRows: readonly number[];
  /** The rows of `columns` of the table, in order. */
  readonly columnRows: readonly number[];
  /** Whether it has a partition column, which has no totals. */
  readonly partitioned: boolean;
  /** Its stale bytes: 0 for most tables. */
  readonly staleColumnSize: number;
  /** Whether its operations include OPTIMIZE and VACUUM. */
  readonly optimized: boolean;
  readonly vacuumed: boolean;
  /** `nonLiveFileSize` of the table. */
  readonly nonLiveFileSize: number;
  /** `unreferencedFileSize` of the table, in format 0.2. */
  readonly unreferencedFileSize: number;
  /** The rows of `partitions` of the table, in order: none when it is not partitioned. */
  readonly partitionRows: readonly number[];
}

/** The synthetic footprint and what the builder knows of it. */
export interface Synthetic {
  readonly data: FootprintData;
  readonly tables: readonly SyntheticTable[];
}

/** A seeded generator of numbers in [0, 1): mulberry32. */
export function generator(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** The distribution of a set of values as the collector writes it: quartiles by position, `sorted[floor(n/4)]`. */
export function distributionOf(values: readonly number[]): { count: number; sum: number; minimum: number; firstQuartile: number; median: number; thirdQuartile: number; maximum: number } | null {
  if (values.length === 0) {
    return null;
  }
  const sorted = [...values].sort((a, b) => a - b);
  const n = sorted.length;
  return {
    count: n,
    sum: sorted.reduce((a, b) => a + b, 0),
    minimum: sorted[0]!,
    firstQuartile: sorted[Math.floor(n / 4)]!,
    median: sorted[Math.floor(n / 2)]!,
    thirdQuartile: sorted[Math.floor((3 * n) / 4)]!,
    maximum: sorted[n - 1]!,
  };
}

const pad = (value: number, width: number) => String(value).padStart(width, "0");
const DAY_MS = 86_400_000;
const KINDS = ["OneLake lakehouse", "OneLake warehouse", "Storage folder"];
const TYPES = ["long", "integer", "string", "double", "date", "timestamp", "boolean", "decimal(18,2)"];

/** Spreads `total` items over `buckets`, `minimum` each at least, more of them to the first buckets. */
function spread(total: number, buckets: number, minimum: number, random: () => number): number[] {
  const counts = new Array<number>(buckets).fill(minimum);
  for (let i = buckets * minimum; i < total; i++) {
    counts[Math.min(buckets - 1, Math.floor(random() ** 1.6 * buckets))]! += 1;
  }
  return counts;
}

/** Splits `total` into integer shares proportional to the weights, which sum to `total` exactly. */
function split(total: number, weights: readonly number[]): number[] {
  const sum = weights.reduce((a, b) => a + b, 0);
  const parts = weights.map((w) => Math.floor((total * w) / sum));
  parts[0]! += total - parts.reduce((a, b) => a + b, 0);
  return parts;
}

/**
 * Builds a synthetic footprint. The same options give the same data.
 * @param options The sizes, the seed, and what to leave out.
 */
export function syntheticFootprint(options: SyntheticOptions = {}): Synthetic {
  const random = generator(options.seed ?? 42);
  const sourceCount = options.sources ?? 3;
  const tableCount = options.tables ?? 40;
  const columnCount = Math.max(options.columns ?? 1000, 2 * tableCount);
  const fileCount = Math.max(options.files ?? 5000, tableCount);
  const level = options.level ?? 2;
  const completed = Date.parse(COMPLETED_AT);

  const sources: Record<string, unknown[]> = { id: [], kind: [], name: [] };
  for (let s = 0; s < sourceCount; s++) {
    sources.id!.push(s + 1);
    sources.kind!.push(KINDS[s % KINDS.length]);
    sources.name!.push(`source_${String.fromCharCode(97 + (s % 26))}${s >= 26 ? s : ""}`);
  }

  const T: Record<string, unknown[]> = {};
  const C: Record<string, unknown[]> = {};
  const F: Record<string, unknown[]> = {};
  const P: Record<string, unknown[]> = {};
  const O: Record<string, unknown[]> = { table: [], name: [], commitCount: [], lastAt: [] };
  const push = (table: Record<string, unknown[]>, field: string, value: unknown) => (table[field] ??= []).push(value);

  const filesPerTable = spread(fileCount, tableCount, 1, random);
  const columnsPerTable = spread(columnCount, tableCount, 2, random);
  const info: SyntheticTable[] = [];
  let fileId = 0;
  let columnId = 0;
  let partitionId = 0;

  for (let t = 0; t < tableCount; t++) {
    const id = t + 1;
    const source = (t % sourceCount) + 1;
    const name = `table_${pad(id, 3)}`;
    const partitioned = t % 5 === 0;
    const optimized = t % 3 === 0;
    const vacuumed = t % 4 === 0;
    const hasDeletionVectors = t % 3 === 1;
    const vOrderShare = [0, 1, 0.5, random()][t % 4]!;
    const center = Math.exp(Math.log(64 * 1024) + random() * (Math.log(256 * 1024 * 1024) - Math.log(64 * 1024)));
    const bytesPerRow = 20 + random() * 180;
    const compression = 1.5 + random() * 2.5;
    const nColumns = columnsPerTable[t]!;

    // The files of the table.
    const fileRows: number[] = [];
    const sizes: number[] = [];
    const rowGroupRows: number[] = [];
    let footerSum = 0;
    let compressedSum = 0;
    let uncompressedSum = 0;
    let rowSum = 0;
    let deletedSum = 0;
    let rowGroupSum = 0;
    let vOrdered = 0;
    // Every partition holds a file at least, as the rows of `partitions` do.
    const partitionCount = partitioned ? Math.min(filesPerTable[t]!, 2 + Math.floor(random() * 9)) : null;
    const partitionBytes = new Array<number>(partitionCount ?? 0).fill(0);
    const partitionFiles = new Array<number>(partitionCount ?? 0).fill(0);
    const partitionRowCounts = new Array<number>(partitionCount ?? 0).fill(0);
    const partitionDeleted = new Array<number>(partitionCount ?? 0).fill(0);
    const partitionModified = new Array<number>(partitionCount ?? 0).fill(-Infinity);
    const [low, high] = options.sizeRange ?? [0, 0];
    for (let f = 0; f < filesPerTable[t]!; f++) {
      const fileSize = options.sizeRange !== undefined
        ? Math.round(Math.exp(Math.log(low) + random() * (Math.log(high) - Math.log(low))))
        : Math.round(Math.min(1024 ** 3, Math.max(4096, center * Math.exp((random() - 0.5) * 3))));
      const footerSize = Math.max(100, Math.min(Math.floor(fileSize * 0.05), 200 + nColumns * 60 + Math.floor(random() * 500)));
      const compressedSize = fileSize - footerSize - 12;
      const uncompressedSize = Math.round(compressedSize * compression);
      const rowCount = Math.max(1, Math.floor(compressedSize / bytesPerRow));
      const deletedRowCount = hasDeletionVectors && random() < 0.4 ? Math.floor(rowCount * random() * 0.3) : 0;
      const rowGroupCount = Math.max(1, Math.ceil(rowCount / 1_048_576));
      const isVOrdered = random() < vOrderShare;
      fileId++;
      fileRows.push(fileId - 1);
      sizes.push(fileSize);
      for (let g = 0; g < rowGroupCount; g++) {
        rowGroupRows.push(g < rowGroupCount - 1 ? 1_048_576 : rowCount - 1_048_576 * (rowGroupCount - 1));
      }
      footerSum += footerSize;
      compressedSum += compressedSize;
      uncompressedSum += uncompressedSize;
      rowSum += rowCount;
      deletedSum += deletedRowCount;
      rowGroupSum += rowGroupCount;
      vOrdered += isVOrdered ? 1 : 0;
      const partition = partitionCount === null ? null : f % partitionCount;
      const modifiedAt = completed - Math.floor(random() * 300) * DAY_MS;
      if (partition !== null) {
        partitionBytes[partition]! += fileSize;
        partitionFiles[partition]! += 1;
        partitionRowCounts[partition]! += rowCount;
        partitionDeleted[partition]! += deletedRowCount;
        partitionModified[partition] = Math.max(partitionModified[partition]!, modifiedAt);
      }
      push(F, "id", fileId);
      push(F, "table", id);
      push(F, "path", `${partition === null ? "" : `part_col=${partition}/`}part-${pad(fileId, 5)}.parquet`);
      push(F, "format", "Parquet");
      push(F, "fileSize", fileSize);
      push(F, "modifiedAt", new Date(modifiedAt).toISOString());
      push(F, "rowCount", rowCount);
      push(F, "deletedRowCount", deletedRowCount);
      push(F, "deletionVectorSize", deletedRowCount > 0 ? 40 + Math.floor(deletedRowCount / 8) : null);
      push(F, "isVOrdered", isVOrdered);
      push(F, "footerSize", footerSize);
      push(F, "rowGroupCount", rowGroupCount);
      push(F, "compressedSize", compressedSize);
      push(F, "uncompressedSize", uncompressedSize);
    }
    const fileSize = sizes.reduce((a, b) => a + b, 0);
    const partitionRows: number[] = [];
    for (let p = 0; p < (partitionCount ?? 0); p++) {
      partitionId++;
      partitionRows.push(partitionId - 1);
      push(P, "id", partitionId);
      push(P, "table", id);
      push(P, "partitionSpec", null);
      push(P, "fileCount", partitionFiles[p]);
      push(P, "fileSize", partitionBytes[p]);
      push(P, "rowCount", partitionRowCounts[p]);
      push(P, "deletedRowCount", partitionDeleted[p]);
      push(P, "lastModifiedAt", new Date(partitionModified[p]!).toISOString());
    }
    const staleColumnSize = t % 7 === 3 ? Math.floor(compressedSum * 0.1) : 0;

    // The columns of the table: the partition column, the last one, has no totals.
    const columnRows: number[] = [];
    const present = partitioned ? nColumns - 1 : nColumns;
    const columnSizes = split(compressedSum - staleColumnSize, Array.from({ length: present }, () => 0.05 + random() ** 3));
    for (let c = 0; c < nColumns; c++) {
      columnId++;
      columnRows.push(columnId - 1);
      const isPartition = partitioned && c === nColumns - 1;
      const dataType = isPartition ? "integer" : TYPES[Math.floor(random() * TYPES.length)]!;
      const nullBin = Math.floor(random() * 7);
      const nullShare = [0, 0, 0.01 + random() * 0.04, 0.1 + random() * 0.1, 0.3 + random() * 0.2, 0.6 + random() * 0.2, 0.85 + random() * 0.15][nullBin]!;
      const nullCount = Math.floor(rowSum * nullShare);
      const dictionary = Math.floor(random() * (rowGroupSum + 1));
      push(C, "id", columnId);
      push(C, "table", id);
      push(C, "name", `col_${pad(columnId, 4)}`);
      push(C, "dataType", dataType);
      push(C, "isNullable", c > 0);
      push(C, "isPartitionColumn", isPartition);
      if (isPartition) {
        for (const field of FOOTER_COLUMN_FIELDS) {
          push(C, field, null);
        }
        continue;
      }
      const compressedSize = columnSizes[c]!;
      push(C, "compressedSize", compressedSize);
      push(C, "uncompressedSize", Math.round(compressedSize * compression));
      push(C, "valueCount", rowSum);
      push(C, "nullCount", nullCount);
      push(C, "chunkCount", rowGroupSum);
      push(C, "dictionaryChunkCount", dictionary);
      push(C, "fallbackChunkCount", Math.floor(dictionary * random() * 0.3));
      push(C, "dictionaryPageSize", dictionary * 512);
      push(C, "unencodedByteArrayDataBytes", dataType === "string" ? (rowSum - nullCount) * (4 + Math.floor(random() * 40)) : null);
      push(C, "codecs", ["ZSTD"]);
      push(C, "encodings", dictionary > 0 ? ["PLAIN", "RLE_DICTIONARY"] : ["PLAIN"]);
      push(C, "fileCountWithoutColumn", c % 11 === 10 ? Math.floor(filesPerTable[t]! * 0.3) : 0);
    }

    // The operations of the table.
    const operations: string[] = ["WRITE"];
    if (t % 5 === 1) {
      operations.push("MERGE");
    }
    if (optimized) {
      operations.push("OPTIMIZE");
    }
    if (vacuumed) {
      operations.push("VACUUM END", "VACUUM START");
    }
    for (const op of operations) {
      O.table!.push(id);
      O.name!.push(op);
      O.commitCount!.push(1 + Math.floor(random() * 20));
      O.lastAt!.push(new Date(completed - Math.floor(random() * 100 * DAY_MS)).toISOString());
    }

    const commitCount = 10 + Math.floor(random() * 500);
    const oldestDays = t % 6 === 5 ? random() * 0.9 : 0.5 + random() * 400;
    const nonLiveFileCount = t % 5 < 3 ? 1 + Math.floor(random() * 50) : 0;
    const nonLiveFileSize = nonLiveFileCount === 0 ? 0 : Math.floor(fileSize * random() * 0.5);
    // The unreferenced files: more than the non-live files, fewer, as many, or none.
    const unreferencedFileSize = [
      nonLiveFileSize + 1000 + Math.floor(fileSize * random() * 0.2), Math.floor(nonLiveFileSize * random() * 0.8), 0, nonLiveFileSize,
    ][t % 4]!;
    const unreferencedFileCount = unreferencedFileSize === 0 ? 0 : 1 + Math.floor(random() * 20);
    // The removal times of the non-live files: one time for a single file.
    const newestRemoved = nonLiveFileCount === 0 ? null : completed - Math.floor(random() * 30 * DAY_MS);
    const oldestRemoved = newestRemoved === null ? null : nonLiveFileCount === 1 ? newestRemoved : newestRemoved - Math.floor(random() * 60 * DAY_MS);
    const iso = (value: number | null) => (value === null ? null : new Date(value).toISOString());
    const distribution = distributionOf(sizes)!;
    const rowGroupDistribution = distributionOf(rowGroupRows);
    const partitionDistribution = partitionCount === null ? null : distributionOf(partitionBytes);
    const values: Record<string, unknown> = {
      id, source, namespace: source % 2 === 0 ? ["dbo"] : [], name, location: `source_${source}/Tables/${name}`,
      format: "Delta", tableId: `00000000-0000-0000-0000-${pad(id, 12)}`,
      snapshotId: String(commitCount - 1), snapshotTimestamp: new Date(completed - DAY_MS).toISOString(),
      collectedLevel: level, detailDepth: options.withFiles === false ? "Summary" : "Files",
      fileCount: sizes.length, fileSize, rowCount: rowSum, deletedRowCount: deletedSum,
      lastModifiedAt: new Date(completed - DAY_MS).toISOString(), vOrderedFileCount: vOrdered,
      partitionCount, commitCount, commitsRead: Math.min(commitCount, 100),
      oldestCommitAt: new Date(completed - oldestDays * DAY_MS).toISOString(),
      nonLiveFileCount, nonLiveFileSize, nonLiveOldestRemovedAt: iso(oldestRemoved), nonLiveNewestRemovedAt: iso(newestRemoved),
      unreferencedFileCount, unreferencedFileSize,
      metadataFileCount: 2 + Math.min(commitCount, 100), metadataSize: 1000 + Math.min(commitCount, 100) * 800,
      footerCount: sizes.length, rowGroupCount: rowGroupSum, compressedSize: compressedSum,
      uncompressedSize: uncompressedSum, footerSize: footerSum, staleColumnSize, columnTotalsSource: "Footers",
      fileSizes: distribution, rowGroupRowCounts: rowGroupDistribution, partitionSizes: partitionDistribution,
    };
    for (const [field, value] of Object.entries(values)) {
      push(T, field, value);
    }
    info.push({
      id, name, source, fileRows, columnRows, partitioned, staleColumnSize, optimized, vacuumed, nonLiveFileSize,
      unreferencedFileSize, partitionRows,
    });
  }

  if (level === 1) {
    for (const field of FOOTER_TABLE_FIELDS) delete T[field];
    for (const field of FOOTER_COLUMN_FIELDS) delete C[field];
    for (const field of FOOTER_FILE_FIELDS) delete F[field];
  }
  if (options.formatVersion === "0.1") {
    for (const field of FORMAT_02_TABLE_FIELDS) delete T[field];
    for (const field of FORMAT_02_PARTITION_FIELDS) delete P[field];
  }
  for (const field of options.dropTableFields ?? []) delete T[field];
  for (const field of options.dropColumnFields ?? []) delete C[field];
  for (const field of options.dropFileFields ?? []) delete F[field];

  const summary: Record<string, unknown> = {
    sources: { length: sourceCount, ...sources },
    tables: { length: tableCount, ...T },
    columns: { length: columnId, ...C },
    operations: { length: O.table!.length, ...O },
  };
  for (const name of options.dropTables ?? []) {
    delete summary[name];
  }
  const files = options.withFiles === false ? null : ({ length: fileId, ...F } as unknown as FilesTable);
  const partitions = options.withPartitions === false || partitionId === 0 ? null : ({ length: partitionId, ...P } as unknown as PartitionsTable);
  const manifest: Partial<Manifest> = {
    format: "pqax",
    formatVersion: options.formatVersion ?? "0.2",
    collection: {
      collectorApp: "synthetic-collector", collectorAppVersion: "1.0", collectorLib: "synthetic-lib",
      collectorLibVersion: "2.0", startedAt: STARTED_AT, completedAt: COMPLETED_AT, level, requestCount: 1234,
      bytesRead: 5_678_900,
    },
    parts: [],
  };
  return { data: { manifest, summary: summary as Summary, files, partitions, embeddedPart: null, warnings: [] }, tables: info };
}
