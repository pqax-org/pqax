// The view model of @pqax/views: readFootprint and buildModel on every valid sample of the conformance kit, the figures
// of the model against hand computations from the tables that the reader returns, the tree of the treemap, the
// tolerance of the model to fields, tables, and parts that are missing or wrong, the pure helpers with their edge cases,
// and the synthetic footprint of 1,000 columns and 5,000 files. The stack splits the data, the live bytes and the
// waste, from the overhead of the metadata and the footers; with the unreferenced files of format 0.2 the non-live
// component keeps only what can still be stored. It runs in Node and in the browsers.
import { describe, expect, it, vi } from "vitest";
import { type FilesTable, type PartitionsTable, type PqaxDocument, type Summary, openEmbedded, openPqax } from "@pqax/pqax";
import {
  DEFAULT_SMALL_FILE_SIZE, DEFAULT_TINY_FILE_SIZE, OVERHEAD_COMPONENTS, STACK_COMPONENTS, WASTE_COMPONENTS,
  type FootprintData, type StorageModel, type TableModel, type TreeNode, buildModel, columnTree, commitsPerDay,
  daysBetween, deletedEstimate, findPath, quartiles, ratio, readFootprint, smallShareAtLeast, sortTables, stackOf,
} from "@pqax/views";
import { type Expected, SAMPLES, bytesOf, expectedByName, join, rewriteZip } from "../typescript/kit";
import {
  COMPLETED_AT, EARLY_TABLE_FIELDS, FORMAT_02_TABLE_FIELDS, type Synthetic, distributionOf, syntheticFootprint,
} from "./synthetic";

const EXPECTED = expectedByName();
const EVERY = Object.values(EXPECTED).map((expected) => expected.file).sort();
/** The samples built from the fixture: every sample but the two of the comparison test, which have their own lakehouse. */
const ALL = EVERY.filter((file) => !file.startsWith("diff-"));
/** The samples of format 0.2: every sample but format-0.1.pqax. */
const FORMAT_02 = ALL.filter((file) => file !== "format-0.1.pqax");
const DAY_MS = 86_400_000;
const MIB = 1024 * 1024;

async function open(file: string): Promise<PqaxDocument> {
  const bytes = bytesOf(join(SAMPLES, file));
  return file.endsWith(".vpax") ? openEmbedded(bytes) : openPqax(bytes);
}

function expectedOf(file: string): Expected {
  return Object.values(EXPECTED).find((expected) => expected.file === file)!;
}

async function modelOf(file: string): Promise<{ doc: PqaxDocument; data: FootprintData; model: StorageModel }> {
  const doc = await open(file);
  const data = await readFootprint(doc);
  return { doc, data, model: buildModel(data) };
}

/** Equal within a relative tolerance, for sums of fractions. */
function expectClose(actual: number | null | undefined, expected: number, tolerance = 1e-9): void {
  expect(actual).not.toBeNull();
  expect(actual).toBeDefined();
  expect(Math.abs(actual! - expected)).toBeLessThanOrEqual(tolerance * Math.max(1, Math.abs(expected)));
}

function component(table: TableModel, key: string) {
  return table.stack.components.find((c) => c.key === key)!;
}

/** The deleted bytes of a table from the files table, computed here as conformance/README.md and the model define them. */
function deletedFromFiles(files: FilesTable, table: number): number {
  let sum = 0;
  for (let i = 0; i < files.length; i++) {
    if (files.table![i] !== table) {
      continue;
    }
    const size = files.fileSize![i]!;
    const deleted = files.deletedRowCount![i]!;
    const rows = files.rowCount![i]!;
    sum += deleted === 0 ? 0 : (size * Math.min(deleted, rows)) / rows;
  }
  return sum;
}

/** The invariants that every model holds: totals, stacks, shares, file sizes, sources, and columns. */
function expectInvariants(model: StorageModel): void {
  const sumKnown = (values: (number | null)[]) => values.reduce<number | null>((sum, v) => (v === null ? sum : (sum ?? 0) + v), null);
  const totals = model.collection.totals;
  expect(totals.files).toBe(sumKnown(model.tables.map((t) => t.fileCount)));
  expect(totals.fileSize).toBe(sumKnown(model.tables.map((t) => t.fileSize)));
  expect(totals.rowCount).toBe(sumKnown(model.tables.map((t) => t.rowCount)));
  // A sum over the tables adds those that have the value, and is null when none has it.
  const sumOf = (pick: (t: TableModel) => number | null) => sumKnown(model.tables.map(pick));
  const expectSum = (actual: number | null, expected: number | null) => (expected === null ? expect(actual).toBeNull() : expectClose(actual, expected));
  expectSum(totals.footprint, sumOf((t) => t.stack.total));
  expectSum(totals.waste, sumOf((t) => t.stack.waste));
  expectSum(totals.overhead, sumOf((t) => t.stack.overhead));

  for (const table of model.tables) {
    const { stack } = table;
    expect(stack.components.map((c) => c.key)).toEqual([...STACK_COMPONENTS]);
    // A sum of components counts an absent one as 0, and is null when every one it adds is absent.
    const present = (keys: readonly string[]) => sumKnown(keys.map((k) => (component(table, k).absent ? null : component(table, k).bytes)));
    expectSum(stack.total, present(STACK_COMPONENTS));
    const live = component(table, "live");
    // The waste is the four components between the live data and the overhead; the overhead, metadata and footers.
    expectSum(stack.waste, present(WASTE_COMPONENTS));
    expectSum(stack.overhead, present(OVERHEAD_COMPONENTS));
    expectSum(stack.data, live.absent ? null : live.bytes + (stack.waste ?? 0));
    if (stack.data !== null) {
      expectSum(stack.total, stack.data + (stack.overhead ?? 0));
    }
    if (stack.data !== null && stack.data > 0) {
      // The shares are of the data: those of the live data and the waste sum to 1, the overhead goes beyond. The share
      // of an absent component is null.
      for (const c of stack.components) {
        if (c.absent) {
          expect(c.share).toBeNull();
        } else {
          expectClose(c.share, c.bytes / stack.data);
        }
      }
      expectClose(["live", ...WASTE_COMPONENTS].reduce((sum, k) => sum + (component(table, k).share ?? 0), 0), 1);
      expectSum(stack.wasteShare, stack.waste === null ? null : stack.waste / stack.data);
      expectSum(stack.overheadShare, stack.overhead === null ? null : stack.overhead / stack.data);
    } else {
      expect(stack.components.every((c) => c.share === null)).toBe(true);
      expect(stack.wasteShare).toBeNull();
      expect(stack.overheadShare).toBeNull();
    }
    for (const c of stack.components) {
      expect(c.bytes).toBeGreaterThanOrEqual(0);
      if (c.absent) {
        expect(c.bytes).toBe(0);
      }
    }
    // The overlap rule: with the unreferenced files, the non-live component is at most what the storage holds.
    const nonLive = component(table, "nonLive");
    const unreferenced = component(table, "unreferenced");
    expect(stack.listed).toBe(table.unreferencedFileSize !== null);
    expect(nonLive.absent).toBe(table.nonLiveFileSize === null);
    expect(unreferenced.absent).toBe(!stack.listed);
    if (stack.listed) {
      expect(nonLive.bytes).toBe(table.nonLiveFileSize === null ? 0 : Math.min(table.nonLiveFileSize, table.unreferencedFileSize!));
      expectClose(nonLive.bytes + unreferenced.bytes, table.unreferencedFileSize!);
    } else {
      expect(nonLive.bytes).toBe(table.nonLiveFileSize ?? 0);
    }
    const unclipped = table.fileSize === null ? null
      : table.fileSize - component(table, "deleted").bytes - component(table, "footers").bytes - component(table, "stale").bytes;
    if (unclipped !== null && unclipped >= 0) {
      expectClose(live.bytes, unclipped);
      const stored = stack.listed ? table.unreferencedFileSize! : nonLive.bytes;
      expectClose(stack.total, table.fileSize! + stored + component(table, "metadata").bytes);
    }
    const m = table.maintenance;
    if (m.commitsPerDay !== null) {
      expect(m.lastCommitAt).not.toBeNull();
      expect(m.commitsPerDay).toBeGreaterThanOrEqual(0);
    }
    if (m.daysSinceLastCommit !== null) {
      expect(Number.isInteger(m.daysSinceLastCommit)).toBe(true);
      expect(m.daysSinceLastCommit).toBeGreaterThanOrEqual(0);
    }
    expect(table.partitions.kept).toBeGreaterThanOrEqual(0);
    if (table.partitions.oldestModifiedAt !== null) {
      expect(Date.parse(table.partitions.oldestModifiedAt)).toBeLessThanOrEqual(Date.parse(table.partitions.newestModifiedAt!));
    }

    const files = table.files;
    if (files.source === "files") {
      const sizes = files.sizes!;
      expect([...sizes].sort((a, b) => a - b)).toEqual(sizes);
      const d = distributionOf(sizes)!;
      expect(files.count).toBe(d.count);
      expect(files.sum).toBe(d.sum);
      expect([files.minimum, files.firstQuartile, files.median, files.thirdQuartile, files.maximum])
        .toEqual([d.minimum, d.firstQuartile, d.median, d.thirdQuartile, d.maximum]);
    }
    if (files.minimum !== null && files.maximum !== null && files.median !== null) {
      expect(files.minimum).toBeLessThanOrEqual(files.firstQuartile ?? files.minimum);
      expect(files.firstQuartile ?? files.median).toBeLessThanOrEqual(files.median);
      expect(files.median).toBeLessThanOrEqual(files.thirdQuartile ?? files.median);
      expect(files.thirdQuartile ?? files.maximum).toBeLessThanOrEqual(files.maximum);
    }
    if (files.vOrder !== null) {
      expect(files.vOrder.share).toBeGreaterThanOrEqual(0);
      expect(files.vOrder.share).toBeLessThanOrEqual(1);
    }
    for (const below of [files.tinyFiles, files.smallFiles]) {
      const share = below.shareAtLeast;
      if (share !== null) {
        expect(share).toBeGreaterThanOrEqual(0);
        expect(share).toBeLessThanOrEqual(1);
      }
      expect(below.count === null).toBe(files.source !== "files");
    }
    // Below the smaller threshold, never more files than below the larger one.
    if (files.tinyFiles.threshold <= files.smallFiles.threshold && files.tinyFiles.shareAtLeast !== null) {
      expect(files.tinyFiles.shareAtLeast).toBeLessThanOrEqual(files.smallFiles.shareAtLeast!);
      if (files.source === "files") {
        expect(files.tinyFiles.count!).toBeLessThanOrEqual(files.smallFiles.count!);
      }
    }
    expect(table.columnKeys).toEqual(model.columns.filter((c) => c.tableKey === table.key).map((c) => c.key));
  }

  // Every table is in exactly one source.
  const listed = model.sources.flatMap((s) => s.tableKeys);
  expect([...listed].sort()).toEqual(model.tables.map((t) => t.key).sort());
  expect(model.collection.totals.sources).toBe(model.sources.filter((s) => s.id !== null).length);
  expect(new Set(model.tables.map((t) => t.key)).size).toBe(model.tables.length);
  expect(new Set(model.columns.map((c) => c.key)).size).toBe(model.columns.length);
}

/** The invariants of the tree: an inner node is the sum of its children, and the children go largest first, absent last. */
function expectTree(node: TreeNode): void {
  const childSum = node.children.reduce((sum, child) => sum + child.size, 0);
  if (node.kind === "root" || node.kind === "source" || (node.kind === "table" && node.sizeSource === "columns")) {
    expectClose(node.size, childSum);
  }
  if (node.kind === "table" && node.sizeSource === "table") {
    expect(childSum).toBe(0);
    expect(node.size).toBe(node.table!.compressedSize);
  }
  for (let i = 1; i < node.children.length; i++) {
    const a = node.children[i - 1]!;
    const b = node.children[i]!;
    expect(Number(a.absent)).toBeLessThanOrEqual(Number(b.absent));
    if (a.absent === b.absent) {
      expect(a.size).toBeGreaterThanOrEqual(b.size);
    }
  }
  if (node.kind === "column") {
    expect(node.children).toEqual([]);
    expect(node.absent).toBe(node.column!.absent);
    expect(node.size).toBe(node.column!.compressedSize ?? 0);
  }
  for (const child of node.children) {
    expectTree(child);
  }
}

describe("the samples of the kit", () => {
  it("covers every valid sample, the two VPAX files included", () => {
    expect(ALL).toContain("sample.pqax");
    expect(ALL).toContain("embedded-stored.vpax");
    expect(ALL).toContain("embedded-deflated.vpax");
    expect(ALL).toContain("format-0.1.pqax");
    expect(ALL.length).toBe(11);
  });

  it.each(ALL)("%s gives the collection line of its manifest", async (file) => {
    const expected = expectedOf(file);
    const { doc, model } = await modelOf(file);
    const c = model.collection;
    expect(c.formatVersion).toBe(expected.formatVersion);
    // The kit names its collector after its own version, 0.2, whatever format version a sample declares;
    // format-0.1.pqax keeps the bytes of the 0.1 kit.
    expect(c.collector).toBe(`pqax-conformance-kit ${file === "format-0.1.pqax" ? "0.1" : "0.2"}`);
    expect(c.library).toBeNull();
    expect(c.level).toBe(2);
    expect(c.startedAt).toBeNull();
    expect(c.completedAt).toBe("2026-10-05T20:55:12.749Z");
    expect(c.requestCount).toBe(18);
    expect(c.bytesRead).toBe(195306);
    expect(c.embeddedPart).toBe(expected.embeddedPart);
    expect(c.totals.sources).toBe(expected.rows.sources);
    expect(c.totals.tables).toBe(expected.rows.tables);
    expect(c.totals.columns).toBe(expected.rows.columns);
    expect(c.totals.fileSize).toBe(expected.tablesFileSize);
    expect(c.totals.rowCount).toBe(expected.tablesRowCount);
    expect(c.totals.files).toBe(expected.tables.reduce((sum, t) => sum + (t.fileCount ?? 0), 0));
    expect(Object.values(c.depths).reduce((a, b) => a + b, 0)).toBe(expected.rows.tables);
    expect(Object.keys(c.depths)).toEqual([doc.summary.tables!.detailDepth![0]]);
    expect(model.hasFiles).toBe((expected.rows.files ?? 0) > 0);
    expect(model.warnings).toEqual([]);
    expect(model.tables.map((t) => t.id)).toEqual(expected.tables.map((t) => t.id));
    expect(model.tables.map((t) => t.fileCount)).toEqual(expected.tables.map((t) => t.fileCount));
  });

  it.each(EVERY)("%s holds the invariants of the model and of the tree", async (file) => {
    const { model } = await modelOf(file);
    expectInvariants(model);
    expectTree(columnTree(model));
  });

  it.each(FORMAT_02)("%s gives stacks whose total is fileSize + unreferencedFileSize + metadataSize", async (file) => {
    const { doc, model } = await modelOf(file);
    const tables = doc.summary.tables!;
    model.tables.forEach((table, row) => {
      const total = tables.fileSize![row]! + tables.unreferencedFileSize![row]! + tables.metadataSize![row]!;
      const overhead = tables.footerSize![row]! + tables.metadataSize![row]!;
      expect(table.stack.listed).toBe(true);
      expectClose(table.stack.total, total);
      expectClose(table.stack.overhead, overhead);
      expectClose(table.stack.data, total - overhead);
      expectClose(table.stack.waste, total - overhead - component(table, "live").bytes);
      expect(component(table, "footers").bytes).toBe(tables.footerSize![row]);
      expect(component(table, "metadata").bytes).toBe(tables.metadataSize![row]);
      const nonLive = Math.min(tables.nonLiveFileSize![row]!, tables.unreferencedFileSize![row]!);
      expect(component(table, "nonLive").bytes).toBe(nonLive);
      expect(component(table, "unreferenced").bytes).toBe(tables.unreferencedFileSize![row]! - nonLive);
      expect(component(table, "stale").bytes).toBe(0);
      expect(table.stack.components.some((c) => c.absent)).toBe(false);
    });
  });

  it("format-0.1.pqax gives stacks whose total is fileSize + nonLiveFileSize + metadataSize, without unreferenced files", async () => {
    const { doc, model } = await modelOf("format-0.1.pqax");
    const tables = doc.summary.tables!;
    expect(tables.unreferencedFileSize).toBeUndefined();
    model.tables.forEach((table, row) => {
      const total = tables.fileSize![row]! + tables.nonLiveFileSize![row]! + tables.metadataSize![row]!;
      expect(table.stack.listed).toBe(false);
      expectClose(table.stack.total, total);
      expect(component(table, "nonLive").bytes).toBe(tables.nonLiveFileSize![row]);
      expect(component(table, "unreferenced")).toMatchObject({ bytes: 0, absent: true });
      expect(table.stack.components.filter((c) => c.absent).map((c) => c.key)).toEqual(["unreferenced"]);
      expect(table.unreferencedFileCount).toBeNull();
      expect(table.unreferencedFileSize).toBeNull();
    });
  });

  it.each(ALL)("%s estimates the deleted bytes from the files when it has them, else from the table", async (file) => {
    const { doc, data, model } = await modelOf(file);
    const tables = doc.summary.tables!;
    model.tables.forEach((table, row) => {
      const deleted = component(table, "deleted");
      if (data.files) {
        expect(deleted.source).toBe("files");
        expectClose(deleted.bytes, deletedFromFiles(data.files, table.id!));
      } else {
        expect(deleted.source).toBe("table");
        expectClose(deleted.bytes, (tables.fileSize![row]! * tables.deletedRowCount![row]!) / tables.rowCount![row]!);
      }
    });
  });

  it.each(ALL)("%s gives the quartiles of the fileSizes distribution, from the files or from the summary", async (file) => {
    const { doc, data, model } = await modelOf(file);
    const tables = doc.summary.tables!;
    model.tables.forEach((table, row) => {
      const d = tables.fileSizes![row]!;
      const f = table.files;
      expect(f.source).toBe(data.files ? "files" : "table");
      expect([f.count, f.sum, f.minimum, f.firstQuartile, f.median, f.thirdQuartile, f.maximum])
        .toEqual([d.count, d.sum, d.minimum, d.firstQuartile, d.median, d.thirdQuartile, d.maximum]);
      if (data.files) {
        const sizes = data.files.fileSize!.filter((_, i) => data.files!.table![i] === table.id).map(Number).sort((a, b) => a - b);
        expect(f.sizes).toEqual(sizes);
      } else {
        expect(f.sizes).toBeNull();
      }
    });
  });

  it.each(ALL)("%s gives the V-Order share from the files, else from vOrderedFileCount", async (file) => {
    const { doc, data, model } = await modelOf(file);
    const tables = doc.summary.tables!;
    model.tables.forEach((table, row) => {
      const v = table.files.vOrder!;
      if (data.files) {
        const mine = [...Array(data.files.length).keys()].filter((i) => data.files!.table![i] === table.id);
        const ordered = mine.filter((i) => data.files!.isVOrdered![i] === true);
        const bytes = (rows: number[]) => rows.reduce((sum, i) => sum + data.files!.fileSize![i]!, 0);
        expect(v).toEqual({ files: ordered.length, knownFiles: mine.length, share: ordered.length / mine.length, bytesShare: bytes(ordered) / bytes(mine), source: "files" });
      } else {
        const count = tables.vOrderedFileCount![row]!;
        const files = tables.fileCount![row]!;
        expect(v).toEqual({ files: count, knownFiles: files, share: count / files, bytesShare: null, source: "table" });
      }
    });
  });

  it.each(ALL)("%s flags the operations, computes the commits per day up to the snapshot, and the days since it", async (file) => {
    const { doc, model } = await modelOf(file);
    const ops = doc.summary.operations!;
    const tables = doc.summary.tables!;
    const reference = Date.parse(doc.manifest.collection.completedAt!);
    model.tables.forEach((table, row) => {
      const names = ops.name!.filter((_, i) => ops.table![i] === table.id) as string[];
      const m = table.maintenance;
      expect(m.operations).toEqual(names);
      expect(m.hasOptimize).toBe(names.some((n) => n.includes("OPTIMIZE")));
      expect(m.hasVacuum).toBe(names.some((n) => n.includes("VACUUM")));
      expect(m.lastVacuumAt).toBeNull();
      expect(m.commitCount).toBe(tables.commitCount![row]);
      expect(m.commitsRead).toBe(tables.commitsRead![row]);
      expect(m.oldestCommitAt).toBe(tables.oldestCommitAt![row]);
      expect(m.lastCommitAt).toBe(tables.snapshotTimestamp![row]);
      // The commits read span less than a day before the snapshot: the span is one day.
      const span = (Date.parse(tables.snapshotTimestamp![row]!) - Date.parse(tables.oldestCommitAt![row]!)) / DAY_MS;
      expect(span).toBeLessThan(1);
      expect(m.commitsPerDay).toBe(tables.commitsRead![row]);
      const days = Math.floor((reference - Date.parse(tables.snapshotTimestamp![row]!)) / DAY_MS);
      expect(days).toBeGreaterThan(1000);
      expect(m.daysSinceLastCommit).toBe(days);
    });
    expect(model.tables[0]!.maintenance.lastOptimizeAt).toBe("2023-11-14T22:14:50.000Z");
    expect(model.tables[1]!.maintenance.lastOptimizeAt).toBeNull();
  });

  it.each(ALL)("%s gives column sizes that match the column chunks of the expected file", async (file) => {
    const expected = expectedOf(file);
    const { model } = await modelOf(file);
    const tree = columnTree(model);
    for (const t of expected.tables) {
      const node = findPath(tree, `t:${t.id}`)!.at(-1)!;
      if (t.columnChunkCount > 0) {
        expect(node.size).toBe(t.columnChunkCompressedSize);
      }
    }
    if (expected.columnChunksCompressedSize > 0) {
      expect(tree.size).toBe(expected.columnChunksCompressedSize);
      const largest = [...model.columns].filter((c) => !c.absent).sort((a, b) => b.compressedSize! - a.compressedSize! || a.id! - b.id!).slice(0, 3);
      expect(largest.map((c) => ({ id: c.id, compressedSize: c.compressedSize }))).toEqual(expected.largestColumns);
    }
  });
});

describe("the figures of sample.pqax against hand computations", () => {
  it("builds the stacks of both tables", async () => {
    const { model } = await modelOf("sample.pqax");
    const [sales, orders] = model.tables;
    // sales: one file of 47,820 bytes and 5,000 rows has 25 deleted rows. The log tracks one removed file of 12,345
    // bytes, and the storage holds one unreferenced file of 737 bytes: the non-live component is at most 737 bytes.
    const deleted = (47820 * 25) / 5000;
    expect(sales!.key).toBe("t:1");
    expect(sales!.label).toBe("sales");
    expect(sales!.sourceKey).toBe("s:1");
    expect(sales!.stack.components.map((c) => [c.key, c.bytes])).toEqual([
      ["live", 159121 - deleted - 3847], ["nonLive", 737], ["unreferenced", 0], ["deleted", deleted], ["stale", 0],
      ["metadata", 12469], ["footers", 3847],
    ]);
    expect(sales!.stack.listed).toBe(true);
    expect(sales!.stack.data).toBeCloseTo(159121 - 3847 + 737, 9);
    expect(sales!.stack.total).toBeCloseTo(159121 + 737 + 12469, 9);
    expect(sales!.stack.waste).toBeCloseTo(737 + deleted, 9);
    expect(sales!.stack.overhead).toBe(12469 + 3847);
    expect(sales!.stack.wasteShare).toBeCloseTo((737 + deleted) / (159121 - 3847 + 737), 12);
    expect(sales!.stack.overheadShare).toBeCloseTo((12469 + 3847) / (159121 - 3847 + 737), 12);
    expect(component(sales!, "metadata").share).toBeCloseTo(12469 / (159121 - 3847 + 737), 12);
    expect(orders!.key).toBe("t:2");
    expect(orders!.label).toBe("dbo.orders");
    expect(orders!.stack.components.map((c) => [c.key, c.bytes])).toEqual([
      ["live", 39884 - 1254], ["nonLive", 0], ["unreferenced", 0], ["deleted", 0], ["stale", 0], ["metadata", 1372], ["footers", 1254],
    ]);
    expect(orders!.stack.data).toBe(39884 - 1254);
    expect(orders!.stack.total).toBe(41256);
    expect(orders!.stack.waste).toBe(0);
    expect(orders!.stack.wasteShare).toBe(0);
    expect(orders!.stack.overhead).toBe(2626);
    expect(component(orders!, "live").share).toBe(1);
    expect(model.collection.totals.footprint).toBeCloseTo(172327 + 41256, 9);
    expect(model.collection.totals.waste).toBeCloseTo(737 + deleted, 9);
    expect(model.collection.totals.overhead).toBe(16316 + 2626);
    expect(sortTables(model.tables).map((t) => t.key)).toEqual(["t:1", "t:2"]);
  });

  it("builds the stacks of format-0.1.pqax from the non-live files that the log tracks", async () => {
    const { model } = await modelOf("format-0.1.pqax");
    const [sales, orders] = model.tables;
    const deleted = (47820 * 25) / 5000;
    expect(sales!.stack.components.map((c) => [c.key, c.bytes, c.absent])).toEqual([
      ["live", 159121 - deleted - 3847, false], ["nonLive", 12345, false], ["unreferenced", 0, true], ["deleted", deleted, false],
      ["stale", 0, false], ["metadata", 12469, false], ["footers", 3847, false],
    ]);
    expect(sales!.stack.listed).toBe(false);
    // Section 10.2: the share of an absent component is null, not 0.
    expect(component(sales!, "unreferenced").share).toBeNull();
    expect(sales!.stack.total).toBeCloseTo(183935, 9);
    expect(sales!.stack.waste).toBeCloseTo(12345 + deleted, 9);
    expect(orders!.stack.total).toBe(41256);
  });

  it("reads the fields of format 0.2 and the partitions of both tables", async () => {
    const { model } = await modelOf("sample.pqax");
    const [sales, orders] = model.tables;
    expect(sales).toMatchObject({
      nonLiveFileCount: 1, nonLiveFileSize: 12345, nonLiveOldestRemovedAt: "2023-11-14T22:14:10.000Z",
      nonLiveNewestRemovedAt: "2023-11-14T22:14:10.000Z", unreferencedFileCount: 1, unreferencedFileSize: 737, metadataFileCount: 6,
    });
    expect(sales!.partitions).toEqual({ count: 2, kept: 2, oldestModifiedAt: "2023-11-14T22:15:00.000Z", newestModifiedAt: "2023-11-14T22:15:10.000Z" });
    expect(orders).toMatchObject({
      nonLiveFileCount: 0, nonLiveFileSize: 0, nonLiveOldestRemovedAt: null, nonLiveNewestRemovedAt: null,
      unreferencedFileCount: 0, unreferencedFileSize: 0, metadataFileCount: 2,
    });
    expect(orders!.partitions).toEqual({ count: null, kept: 0, oldestModifiedAt: null, newestModifiedAt: null });
  });

  it("reads null for the fields of format 0.2 in format-0.1.pqax, and keeps the partitions without their times", async () => {
    const { data, model } = await modelOf("format-0.1.pqax");
    expect(data.partitions!.length).toBe(2);
    expect(data.partitions!.lastModifiedAt).toBeUndefined();
    const [sales] = model.tables;
    expect(sales).toMatchObject({
      nonLiveFileCount: 1, nonLiveFileSize: 12345, nonLiveOldestRemovedAt: null, nonLiveNewestRemovedAt: null,
      unreferencedFileCount: null, unreferencedFileSize: null, metadataFileCount: 6,
    });
    expect(sales!.partitions).toEqual({ count: 2, kept: 2, oldestModifiedAt: null, newestModifiedAt: null });
  });

  it("counts the partitions from partitionCount, else from the rows of the partitions table", async () => {
    const summaryOnly = (await modelOf("summary-only.pqax")).model;
    expect(summaryOnly.tables.map((t) => t.partitions)).toEqual([
      { count: 2, kept: 0, oldestModifiedAt: null, newestModifiedAt: null },
      { count: null, kept: 0, oldestModifiedAt: null, newestModifiedAt: null },
    ]);
    const { data } = await modelOf("sample.pqax");
    const { partitionCount: _p, ...tables } = data.summary.tables!;
    const model = buildModel({ ...data, summary: { ...data.summary, tables } });
    expect(model.tables.map((t) => [t.partitions.count, t.partitions.kept])).toEqual([[2, 2], [null, 0]]);
    // The average partition size still comes from partitionSizes.
    expect(model.tables[0]!.measures.averagePartitionSize).toBe(159121 / 2);
    // A partitions table whose rows say more than partitionCount: partitionCount wins, the rows are kept.
    const more = { ...data.partitions!, length: 3, id: [1, 2, 3], table: [1, 1, 1], lastModifiedAt: ["2023-11-14T22:15:00.000Z", null, "not a date"] } as PartitionsTable;
    const kept = buildModel({ ...data, partitions: more }).tables[0]!.partitions;
    expect(kept).toEqual({ count: 2, kept: 3, oldestModifiedAt: "2023-11-14T22:15:00.000Z", newestModifiedAt: "2023-11-14T22:15:00.000Z" });
  });

  it("reads the partitions only when readFootprint is told to", async () => {
    const doc = await open("sample.pqax");
    const data = await readFootprint(doc, { partitions: false });
    expect(data.partitions).toBeNull();
    expect(data.files!.length).toBe(5);
    expect(data.warnings).toEqual([]);
    const model = buildModel(data);
    expect(model.tables[0]!.partitions).toEqual({ count: 2, kept: 0, oldestModifiedAt: null, newestModifiedAt: null });
    expect(model.hasFiles).toBe(true);
    const both = await readFootprint(doc, { files: false, partitions: false });
    expect([both.files, both.partitions]).toEqual([null, null]);
    const read = await readFootprint(doc);
    expect(read.partitions!.length).toBe(2);
    expect(read.partitions!.lastModifiedAt).toEqual(["2023-11-14T22:15:00.000Z", "2023-11-14T22:15:10.000Z"]);
  });

  it("gives the measures of section 9 for both tables", async () => {
    const { model } = await modelOf("sample.pqax");
    expect(model.tables[0]!.measures).toEqual({
      compressionRatio: 307780 / 153328,
      deletedRowShare: 25 / 17000,
      averageFileSize: 159121 / 3,
      averageRowGroupRows: 17000 / 10,
      footerCoverage: 1,
      staleColumnShare: 0,
      nonLiveShare: 12345 / (159121 + 12345),
      averagePartitionSize: 159121 / 2,
      dictionaryFallbackShare: 0,
    });
    expect(model.tables[1]!.measures).toEqual({
      compressionRatio: 74888 / 38177,
      deletedRowShare: 0,
      averageFileSize: 39884 / 2,
      averageRowGroupRows: 4100 / 3,
      footerCoverage: 1,
      staleColumnShare: 0,
      nonLiveShare: 0,
      averagePartitionSize: null,
      dictionaryFallbackShare: 0,
    });
  });

  it("gives the measures of the columns", async () => {
    const { model } = await modelOf("sample.pqax");
    const byKey = new Map(model.columns.map((c) => [c.key, c]));
    expect(byKey.get("c:1")).toEqual({
      key: "c:1", id: 1, tableKey: "t:1", label: "id", dataType: "long", isPartitionColumn: false,
      compressedSize: 19374, uncompressedSize: 136277, valueCount: 17000, nullCount: 0, absent: false, nullShare: 0,
      compressionRatio: 136277 / 19374, dictionaryShare: 0, dictionaryFallbackShare: null, averageValueLength: null,
      filesWithoutColumnShare: 0,
    });
    expect(byKey.get("c:3")!.dictionaryShare).toBe(1);
    expect(byKey.get("c:3")!.dictionaryFallbackShare).toBe(0);
    expect(byKey.get("c:3")!.compressionRatio).toBe(25640 / 4132);
    expect(byKey.get("c:8")!.dictionaryShare).toBe(2 / 3);
    expect(byKey.get("c:5")).toMatchObject({
      tableKey: "t:1", label: "Year", isPartitionColumn: true, compressedSize: null, absent: true, nullShare: null,
      compressionRatio: null, dictionaryShare: null, filesWithoutColumnShare: null,
    });
    expect(byKey.get("c:10")).toMatchObject({ tableKey: "t:2", absent: true, filesWithoutColumnShare: 1 });
    expect(model.tables[0]!.columnKeys).toEqual(["c:1", "c:2", "c:3", "c:4", "c:5"]);
  });

  it("gives the sources with their tables", async () => {
    const { model } = await modelOf("sample.pqax");
    expect(model.sources).toEqual([
      { key: "s:1", id: 1, label: "Fix_Lakehouse", kind: "OneLake lakehouse", tableKeys: ["t:1"] },
      { key: "s:2", id: 2, label: "Fix_WH", kind: "OneLake warehouse", tableKeys: ["t:2"] },
    ]);
  });

  it("builds the tree: sources, tables, columns largest first, the partition column Year absent and last", async () => {
    const { model } = await modelOf("sample.pqax");
    const tree = columnTree(model);
    expect(tree.key).toBe("root");
    expect(tree.kind).toBe("root");
    expect(tree.size).toBe(153328 + 38177);
    expect(tree.children.map((n) => [n.key, n.kind, n.size])).toEqual([["s:1", "source", 153328], ["s:2", "source", 38177]]);
    const sales = tree.children[0]!.children[0]!;
    expect(sales.key).toBe("t:1");
    expect(sales.table!.key).toBe("t:1");
    expect(sales.children.map((n) => n.key)).toEqual(["c:4", "c:1", "c:3", "c:2", "c:5"]);
    expect(sales.children.map((n) => n.size)).toEqual([128389, 19374, 4132, 1433, 0]);
    const year = sales.children[4]!;
    expect(year.label).toBe("Year");
    expect(year.absent).toBe(true);
    expect(year.column!.isPartitionColumn).toBe(true);
    expect(sales.nullShare).toBe(0);
    expect(sales.valueCount).toBe(4 * 17000);
    expect(sales.compressionRatio).toBe(307780 / 153328);
    expect(sales.children.some((n) => n.kind === "stale")).toBe(false);
    expect(tree.children[1]!.children[0]!.children.at(-1)!.key).toBe("c:10");
  });

  it("finds the path of a node, and null for a key that no node has", async () => {
    const tree = columnTree((await modelOf("sample.pqax")).model);
    expect(findPath(tree, "c:4")!.map((n) => n.key)).toEqual(["root", "s:1", "t:1", "c:4"]);
    expect(findPath(tree, "t:2")!.map((n) => n.key)).toEqual(["root", "s:2", "t:2"]);
    expect(findPath(tree, "root")!.map((n) => n.key)).toEqual(["root"]);
    expect(findPath(tree, "c:99")).toBeNull();
  });

  it("counts the small files below the threshold of the options", async () => {
    const { data, model } = await modelOf("sample.pqax");
    expect(model.tables[0]!.files.smallFiles).toEqual({ threshold: DEFAULT_SMALL_FILE_SIZE, count: 3, shareAtLeast: 1 });
    const small = buildModel(data, { smallFileSize: 40000 });
    expect(small.tables[0]!.files.smallFiles).toEqual({ threshold: 40000, count: 1, shareAtLeast: 1 / 3 });
    expect(small.tables[1]!.files.smallFiles).toEqual({ threshold: 40000, count: 2, shareAtLeast: 1 });
    const summaryOnly = buildModel((await modelOf("summary-only.pqax")).data, { smallFileSize: 40000 });
    expect(summaryOnly.tables[0]!.files.smallFiles).toEqual({ threshold: 40000, count: null, shareAtLeast: 0.25 });
    expect(summaryOnly.tables[1]!.files.smallFiles).toEqual({ threshold: 40000, count: null, shareAtLeast: 1 });
    expect(buildModel(data, { smallFileSize: 0 }).tables[0]!.files.smallFiles.threshold).toBe(DEFAULT_SMALL_FILE_SIZE);
  });

  it("counts the tiny files below the second threshold of the options, from the files and from the quartiles", async () => {
    const { data, model } = await modelOf("sample.pqax");
    expect(DEFAULT_TINY_FILE_SIZE).toBe(8 * MIB);
    expect(DEFAULT_SMALL_FILE_SIZE).toBe(128 * MIB);
    expect(model.tables[0]!.files.tinyFiles).toEqual({ threshold: DEFAULT_TINY_FILE_SIZE, count: 3, shareAtLeast: 1 });
    // The sizes of sales are 28,252, 47,820, and 83,049; of orders 1,781 and 38,103.
    const both = buildModel(data, { tinyFileSize: 30000, smallFileSize: 50000 });
    expect(both.tables.map((t) => [t.files.tinyFiles, t.files.smallFiles])).toEqual([
      [{ threshold: 30000, count: 1, shareAtLeast: 1 / 3 }, { threshold: 50000, count: 2, shareAtLeast: 2 / 3 }],
      [{ threshold: 30000, count: 1, shareAtLeast: 1 / 2 }, { threshold: 50000, count: 2, shareAtLeast: 1 }],
    ]);
    // The option of one threshold leaves the other at its default.
    const tinyOnly = buildModel(data, { tinyFileSize: 30000 }).tables[0]!.files;
    expect([tinyOnly.tinyFiles.threshold, tinyOnly.smallFiles.threshold]).toEqual([30000, DEFAULT_SMALL_FILE_SIZE]);
    const smallOnly = buildModel(data, { smallFileSize: 50000 }).tables[0]!.files;
    expect([smallOnly.tinyFiles.threshold, smallOnly.smallFiles.threshold]).toEqual([DEFAULT_TINY_FILE_SIZE, 50000]);
    // Without the files, the bounds from the quartiles: sales has its first quartile below 30,000 and its median below
    // 50,000; orders, its first quartile below 30,000 and its maximum below 50,000.
    const summaryOnly = buildModel((await modelOf("summary-only.pqax")).data, { tinyFileSize: 30000, smallFileSize: 50000 });
    expect(summaryOnly.tables.map((t) => [t.files.tinyFiles, t.files.smallFiles])).toEqual([
      [{ threshold: 30000, count: null, shareAtLeast: 0.25 }, { threshold: 50000, count: null, shareAtLeast: 0.5 }],
      [{ threshold: 30000, count: null, shareAtLeast: 0.25 }, { threshold: 50000, count: null, shareAtLeast: 1 }],
    ]);
    expect(summaryOnly.tables[0]!.files.tinyFiles.count).toBeNull();
    for (const tinyFileSize of [0, -1, Number.NaN, Infinity]) {
      expect(buildModel(data, { tinyFileSize }).tables[0]!.files.tinyFiles.threshold).toBe(DEFAULT_TINY_FILE_SIZE);
    }
    // Below 1,781 bytes, no file.
    expect(buildModel(data, { tinyFileSize: 1781 }).tables[1]!.files.tinyFiles).toEqual({ threshold: 1781, count: 0, shareAtLeast: 0 });
  });

  it("estimates the deleted bytes of summary-only.pqax at the table level", async () => {
    const { model } = await modelOf("summary-only.pqax");
    const deleted = component(model.tables[0]!, "deleted");
    expect(deleted.source).toBe("table");
    expect(deleted.bytes).toBe((159121 * 25) / 17000);
    expect(model.hasFiles).toBe(false);
  });

  it("gives for multipart.pqax the same model as for sample.pqax", async () => {
    const multipart = await modelOf("multipart.pqax");
    const sample = await modelOf("sample.pqax");
    expect(multipart.model).toEqual(sample.model);
    expect(columnTree(multipart.model)).toEqual(columnTree(sample.model));
  });

  it("gives for the embedded footprints the model of sample.pqax, with the part", async () => {
    const sample = (await modelOf("sample.pqax")).model;
    for (const file of ["embedded-stored.vpax", "embedded-deflated.vpax"]) {
      const { model } = await modelOf(file);
      expect(model.collection.embeddedPart).toBe("Footprint.pqax");
      expect({ ...model, collection: { ...model.collection, embeddedPart: null } }).toEqual(sample);
    }
  });

  it("reads the table-level figures when readFootprint is told not to read the files", async () => {
    const doc = await open("sample.pqax");
    const data = await readFootprint(doc, { files: false });
    expect(data.files).toBeNull();
    expect(data.warnings).toEqual([]);
    const model = buildModel(data);
    expect(model.hasFiles).toBe(false);
    const summaryOnly = (await modelOf("summary-only.pqax")).model;
    expect(model.tables.map((t) => t.stack)).toEqual(summaryOnly.tables.map((t) => t.stack));
    expect(model.tables.map((t) => t.files)).toEqual(summaryOnly.tables.map((t) => t.files));
    expect(model.tables.every((t) => t.files.source === "table" && t.files.vOrder!.source === "table")).toBe(true);
    expect(component(model.tables[0]!, "deleted").source).toBe("table");
  });
});

describe("the tolerance of the model", () => {
  const sampleSummary = async () => (await open("sample.pqax")).summary;

  it("builds an empty model from {} as the summary, with no manifest", () => {
    for (const data of [{ summary: {} }, { summary: {}, manifest: null }, { summary: null as unknown as Summary }]) {
      const model = buildModel(data);
      expect(model.tables).toEqual([]);
      expect(model.columns).toEqual([]);
      expect(model.sources).toEqual([]);
      expect(model.hasFiles).toBe(false);
      expect(model.collection).toMatchObject({ formatVersion: null, collector: null, level: null, embeddedPart: null });
      expect(model.collection.totals).toEqual({ sources: 0, tables: 0, columns: 0, files: null, fileSize: null, rowCount: null, footprint: null, waste: null, overhead: null });
      const tree = columnTree(model);
      expect(tree.children).toEqual([]);
      expect(tree.size).toBe(0);
      expect(sortTables(model.tables)).toEqual([]);
    }
  });

  it("marks the components absent when the summary leaves out the fields of the stack", async () => {
    const summary = await sampleSummary();
    const tables = summary.tables!;
    const model = buildModel({ summary: { ...summary, tables: { length: tables.length, id: tables.id, name: tables.name, source: tables.source } } });
    expectInvariants(model);
    for (const table of model.tables) {
      expect(table.stack.components.every((c) => c.absent && c.bytes === 0)).toBe(true);
      // Section 10.2: with every component absent, the sums and the data are null.
      expect([table.stack.total, table.stack.waste, table.stack.overhead, table.stack.data]).toEqual([null, null, null, null]);
      expect(table.stack.listed).toBe(false);
      expect(table.stack.wasteShare).toBeNull();
      expect(table.stack.overheadShare).toBeNull();
      expect(table.files.source).toBeNull();
      expect(table.files.tinyFiles.shareAtLeast).toBeNull();
      // Without snapshotTimestamp, the newest commit is the latest operation; without a collection, no days since.
      expect(table.maintenance.lastCommitAt).toBe(table.id === 1 ? "2023-11-14T22:15:20.000Z" : "2023-11-14T22:18:20.000Z");
      expect(table.maintenance.daysSinceLastCommit).toBeNull();
      expect(table.partitions).toEqual({ count: null, kept: 0, oldestModifiedAt: null, newestModifiedAt: null });
      expect(table.files.vOrder).toBeNull();
      expect(table.files.smallFiles.shareAtLeast).toBeNull();
      expect(table.maintenance.commitsPerDay).toBeNull();
      expect(Object.values(table.measures).every((m) => m === null || m === 0)).toBe(true);
    }
    expectTree(columnTree(model));
  });

  it("keeps the other components when only some fields are missing", async () => {
    const summary = await sampleSummary();
    const { nonLiveFileSize: _a, metadataSize: _b, footerSize: _c, ...tables } = summary.tables!;
    const model = buildModel({ summary: { ...summary, tables } });
    expectInvariants(model);
    const sales = model.tables[0]!;
    expect(["nonLive", "metadata", "footers"].map((k) => component(sales, k).absent)).toEqual([true, true, true]);
    expect(component(sales, "live").bytes).toBe(159121 - (159121 * 25) / 17000);
    // Without nonLiveFileSize, the unreferenced component is the whole of unreferencedFileSize.
    expect(component(sales, "unreferenced")).toMatchObject({ bytes: 737, absent: false });
    expect(sales.stack.listed).toBe(true);
    expect(sales.stack.total).toBeCloseTo(159121 + 737, 9);
    // Section 10.2: with both of its components absent, the overhead and its share are null.
    expect(sales.stack.overhead).toBeNull();
    expect(sales.stack.overheadShare).toBeNull();
  });

  it("builds a model without the tables, the columns, the sources, or the operations", async () => {
    const summary = await sampleSummary();
    const { tables: _t, ...withoutTables } = summary;
    const a = buildModel({ summary: withoutTables });
    expect(a.tables).toEqual([]);
    expect(a.columns.every((c) => c.tableKey === "t:none")).toBe(true);
    expect(a.collection.totals.files).toBeNull();
    expectTree(columnTree(a));

    const { columns: _c, ...withoutColumns } = summary;
    const b = buildModel({ summary: withoutColumns });
    expect(b.columns).toEqual([]);
    expect(b.tables[0]!.measures.dictionaryFallbackShare).toBeNull();
    const tree = columnTree(b);
    expect(tree.children[0]!.children[0]!).toMatchObject({ key: "t:1", sizeSource: "table", absent: false, size: 153328 });
    expectTree(tree);

    const { sources: _s, ...withoutSources } = summary;
    const c = buildModel({ summary: withoutSources });
    expect(c.sources).toEqual([{ key: "s:none", id: null, label: "No source", kind: null, tableKeys: ["t:1", "t:2"] }]);
    expect(c.collection.totals.sources).toBe(0);
    expect(c.tables.every((t) => t.sourceKey === "s:none")).toBe(true);

    const { operations: _o, ...withoutOperations } = summary;
    const d = buildModel({ summary: withoutOperations });
    expect(d.tables.map((t) => [t.maintenance.hasVacuum, t.maintenance.hasOptimize, t.maintenance.operations])).toEqual([[null, null, []], [null, null, []]]);
  });

  it("reads null for values of the wrong type", () => {
    const summary = {
      sources: { length: 1, id: ["1"], name: [3] },
      tables: {
        id: [1, "2", 3.5], source: [1, 1, 1], name: [7, "table_002", null], namespace: ["ns", null, [1, "ns_b"]],
        fileSize: ["big", -5, Infinity], rowCount: [true, 10, null], deletedRowCount: [1, "x", 2], fileCount: [{}, 2, null],
        fileSizes: [5, { median: "x", minimum: 3, maximum: -1 }, [1, 2]], oldestCommitAt: [42, "not a date", null],
        commitCount: [3, 3, 3], footerSize: [null, null, "1"],
      },
      columns: { length: 2, id: [1, 2], table: [1, "1"], name: [null, 5], compressedSize: ["9", 10], nullCount: [1, 2], valueCount: [0, 1] },
      operations: { length: 2, table: [1, 1], name: [5, "VACUUM"], lastAt: [null, 12] },
    } as unknown as Summary;
    const model = buildModel({ summary, manifest: { formatVersion: 1, collection: { level: "2", completedAt: 5 } } as never });
    expectInvariants(model);
    expect(model.tables.map((t) => t.key)).toEqual(["t:1", "t#1", "t#2"]);
    expect(model.tables.map((t) => t.label)).toEqual(["Table 1", "table_002", "ns_b.Table at row 3"]);
    expect(model.tables.map((t) => t.fileSize)).toEqual([null, null, null]);
    expect(model.tables.map((t) => t.rowCount)).toEqual([null, 10, null]);
    expect(model.tables.map((t) => t.fileCount)).toEqual([null, 2, null]);
    expect(model.tables.map((t) => t.sourceKey)).toEqual(["s:none", "s:none", "s:none"]);
    expect(model.tables[0]!.files.source).toBeNull();
    expect(model.tables[1]!.files).toMatchObject({ source: "table", minimum: 3, median: null, maximum: null, count: 2 });
    expect(model.tables[2]!.files.source).toBeNull();
    expect(model.tables[0]!.maintenance).toMatchObject({ oldestCommitAt: null, commitsPerDay: null, hasVacuum: true, lastVacuumAt: null, operations: ["VACUUM"] });
    expect(model.tables[1]!.maintenance.commitsPerDay).toBeNull();
    expect(model.columns.map((c) => [c.label, c.tableKey, c.compressedSize, c.absent, c.nullShare])).toEqual([
      ["Column 1", "t:1", null, true, null], ["Column 2", "t:none", 10, false, 1],
    ]);
    expect(model.collection).toMatchObject({ formatVersion: null, level: null, completedAt: null });
    expect(model.collection.totals.sources).toBe(0);
    expectTree(columnTree(model));
  });

  it("reads tables whose arrays disagree in length, and a table without length", () => {
    const summary = { tables: { id: [1, 2, 3], fileSize: [10] } } as unknown as Summary;
    const model = buildModel({ summary });
    expect(model.tables.map((t) => [t.key, t.fileSize])).toEqual([["t:1", 10], ["t:2", null], ["t:3", null]]);
    expect(model.collection.totals.tables).toBe(3);
  });

  it("computes the commits per day without the collection, with a span of one day at least, and no days since", async () => {
    const summary = await sampleSummary();
    for (const manifest of [{ formatVersion: "0.1" }, null, undefined]) {
      const model = buildModel({ summary, manifest });
      expect(model.collection.collector).toBeNull();
      expect(model.collection.level).toBeNull();
      expect(model.collection.completedAt).toBeNull();
      // sales: 4 commits, the oldest 30 seconds before the snapshot: one day.
      expect(model.tables[0]!.maintenance.commitsPerDay).toBe(4);
      // orders: 2 commits, the oldest 100 seconds before the snapshot.
      expect(model.tables[1]!.maintenance.commitsPerDay).toBe(2);
      expect(model.tables.map((t) => t.maintenance.daysSinceLastCommit)).toEqual([null, null]);
      expect(model.tables.map((t) => t.maintenance.lastCommitAt)).toEqual(summary.tables!.snapshotTimestamp);
    }
    expect(buildModel({ summary, manifest: { formatVersion: "0.1" } }).collection.formatVersion).toBe("0.1");
  });

  it("counts the days since the last commit to startedAt when the collection has no completedAt", async () => {
    const summary = await sampleSummary();
    // Ten days and three minutes after the snapshot of sales, ten days after the snapshot of orders.
    const startedAt = "2023-11-24T22:18:20.000Z";
    const model = buildModel({ summary, manifest: { collection: { level: 2, startedAt } } });
    expect(model.tables.map((t) => t.maintenance.daysSinceLastCommit)).toEqual([10, 10]);
    expect(model.tables[0]!.maintenance.commitsPerDay).toBe(4);
    expect(model.collection.startedAt).toBe(startedAt);
    // A collection that ends before the last commit gives 0 days.
    const early = buildModel({ summary, manifest: { collection: { completedAt: "2023-11-14T22:15:00.000Z" } } });
    expect(early.tables.map((t) => t.maintenance.daysSinceLastCommit)).toEqual([0, 0]);
  });

  it("takes the newest commit from snapshotTimestamp, else the latest operation, else lastModifiedAt", () => {
    const collection = { completedAt: "2026-10-05T00:00:00.000Z" };
    const summary = {
      tables: {
        length: 5, id: [1, 2, 3, 4, 5], commitsRead: [20, 20, 20, 20, 20],
        oldestCommitAt: ["2026-09-01T00:00:00.000Z", "2026-09-01T00:00:00.000Z", "2026-09-01T00:00:00.000Z", "2026-09-01T00:00:00.000Z", "2026-09-01T00:00:00.000Z"],
        snapshotTimestamp: ["2026-09-21T00:00:00.000Z", null, "not a date", null, null],
        lastModifiedAt: ["2026-09-30T00:00:00.000Z", "2026-09-30T00:00:00.000Z", "2026-09-11T00:00:00.000Z", "2026-09-11T00:00:00.000Z", null],
      },
      operations: {
        length: 4, table: [1, 2, 2, 2], name: ["WRITE", "WRITE", "OPTIMIZE", "MERGE"],
        lastAt: ["2026-10-01T00:00:00.000Z", "2026-09-06T00:00:00.000Z", "2026-09-26T00:00:00.000Z", null],
      },
    } as unknown as Summary;
    const model = buildModel({ summary, manifest: { collection } as never });
    expect(model.tables.map((t) => t.maintenance.lastCommitAt)).toEqual([
      "2026-09-21T00:00:00.000Z", "2026-09-26T00:00:00.000Z", "2026-09-11T00:00:00.000Z", "2026-09-11T00:00:00.000Z", null,
    ]);
    // 20 commits over 20, 25, 10, and 10 days.
    expect(model.tables.map((t) => t.maintenance.commitsPerDay)).toEqual([1, 0.8, 2, 2, null]);
    expect(model.tables.map((t) => t.maintenance.daysSinceLastCommit)).toEqual([14, 9, 24, 24, null]);
    expect(model.tables[1]!.maintenance.lastOptimizeAt).toBe("2026-09-26T00:00:00.000Z");
  });

  it("keys duplicated ids by their row and keeps the first source of an id", () => {
    const summary = {
      sources: { length: 2, id: [1, 1], name: ["source_a", "source_b"] },
      tables: { length: 2, id: [1, 1], source: [1, 1], name: ["table_001", "table_002"], fileSize: [100, 200] },
      columns: { length: 3, id: [1, 1, 0], table: [1, 1, 1], name: ["col_0001", "col_0002", "col_0003"], compressedSize: [5, 6, 7] },
    } as unknown as Summary;
    const model = buildModel({ summary });
    expectInvariants(model);
    expect(model.tables.map((t) => [t.key, t.id])).toEqual([["t:1", 1], ["t#1", null]]);
    expect(model.columns.map((c) => [c.key, c.id, c.tableKey])).toEqual([["c:1", 1, "t:1"], ["c#1", null, "t:1"], ["c#2", null, "t:1"]]);
    expect(model.sources).toEqual([{ key: "s:1", id: 1, label: "source_a", kind: null, tableKeys: ["t:1", "t#1"] }]);
    const tree = columnTree(model);
    expectTree(tree);
    expect(findPath(tree, "t:1")!.at(-1)!.size).toBe(18);
    expect(findPath(tree, "t#1")!.at(-1)!).toMatchObject({ size: 0, absent: true, children: [] });
  });

  it("ignores the rows of files that point to no table or have no size", async () => {
    const summary = await sampleSummary();
    const files = {
      length: 4, id: [1, 2, 3, 4], table: [1, 99, null, 1], fileSize: [1000, 2000, 3000, null],
      rowCount: [10, 10, 10, 10], deletedRowCount: [0, 0, 0, 5], isVOrdered: [true, true, true, null],
    } as unknown as FilesTable;
    const model = buildModel({ summary, files });
    expectInvariants(model);
    expect(model.hasFiles).toBe(true);
    expect(model.tables[0]!.files).toMatchObject({ source: "files", count: 1, sizes: [1000] });
    expect(model.tables[0]!.files.vOrder).toMatchObject({ files: 1, knownFiles: 1, source: "files" });
    expect(model.tables[1]!.files.source).toBe("table");
  });

  it("leaves out a files table whose part is broken, with a warning, and does not throw", async () => {
    const real = await open("sample.pqax");
    const readTable = vi.fn(() => Promise.reject(Object.assign(new Error("broken part"), { code: "json.invalid" })));
    const fake: PqaxDocument = {
      manifest: real.manifest, summary: real.summary, embeddedPart: null, embeddedCompression: null,
      rowCount: (name) => real.rowCount(name), readTable: readTable as never, readRaw: () => Promise.reject(new Error("broken part")),
    };
    const data = await readFootprint(fake);
    expect(readTable).toHaveBeenCalledWith("files");
    expect(readTable).toHaveBeenCalledWith("partitions");
    expect(data.files).toBeNull();
    expect(data.partitions).toBeNull();
    // One warning for each detail table that could not be read.
    expect(data.warnings).toHaveLength(2);
    expect(data.warnings![0]).toContain("files");
    expect(data.warnings![0]).toContain("json.invalid");
    expect(data.warnings![1]).toContain("partitions");
    const model = buildModel(data);
    expect(model.warnings).toEqual(data.warnings);
    expect(model.hasFiles).toBe(false);
    expect(model.tables.every((t) => t.files.source === "table")).toBe(true);

    const plain: PqaxDocument = { ...fake, readTable: (() => Promise.reject(new Error("no code"))) as never };
    const second = await readFootprint(plain);
    expect(second.warnings).toHaveLength(2);
    expect(second.warnings!.every((w) => !w.includes("("))).toBe(true);
    const onlyFiles = await readFootprint(plain, { partitions: false });
    expect(onlyFiles.warnings).toHaveLength(1);
  });

  it("leaves out a partitions table whose part is broken, with a warning, and keeps the files", async () => {
    const real = await open("sample.pqax");
    const readTable = vi.fn((name: string) => (name === "partitions"
      ? Promise.reject(Object.assign(new Error("broken part"), { code: "part.rowCount" }))
      : real.readTable(name as "files")));
    const fake: PqaxDocument = {
      manifest: real.manifest, summary: real.summary, embeddedPart: null, embeddedCompression: null,
      rowCount: (name) => real.rowCount(name), readTable: readTable as never, readRaw: (name) => real.readRaw(name),
    };
    const data = await readFootprint(fake);
    expect(data.partitions).toBeNull();
    expect(data.files!.length).toBe(5);
    expect(data.warnings).toEqual(["The partitions table could not be read (part.rowCount). The views use the totals of the summary."]);
    const model = buildModel(data);
    expect(model.warnings).toEqual(data.warnings);
    expect(model.hasFiles).toBe(true);
    expect(model.tables[0]!.partitions).toEqual({ count: 2, kept: 0, oldestModifiedAt: null, newestModifiedAt: null });
    expectInvariants(model);
  });

  it("leaves out the partitions of a real file whose partitions part does not parse", async () => {
    const bytes = await rewriteZip(bytesOf(join(SAMPLES, "sample.pqax")), (name) => (name === "partitions.json" ? "{\"partitions\": [" : undefined));
    const data = await readFootprint(await openPqax(bytes));
    expect(data.partitions).toBeNull();
    expect(data.files).not.toBeNull();
    expect(data.warnings).toHaveLength(1);
    expect(data.warnings![0]).toContain("partitions");
    expect(data.warnings![0]).toContain("json.invalid");
    expectInvariants(buildModel(data));
  });

  it("leaves out the files of a real file whose files part does not parse", async () => {
    const bytes = await rewriteZip(bytesOf(join(SAMPLES, "sample.pqax")), (name) => (name === "files.json" ? "{\"files\": [" : undefined));
    const data = await readFootprint(await openPqax(bytes));
    expect(data.files).toBeNull();
    expect(data.warnings).toHaveLength(1);
    expect(data.warnings![0]).toContain("json.invalid");
    expectInvariants(buildModel(data));
  });

  it("does not read the files table when the manifest lists no rows for it", async () => {
    const real = await open("summary-only.pqax");
    const readTable = vi.fn(() => Promise.resolve({ length: 0 }));
    const data = await readFootprint({
      manifest: real.manifest, summary: real.summary, embeddedPart: null, embeddedCompression: null,
      rowCount: (name) => real.rowCount(name), readTable: readTable as never, readRaw: (name) => real.readRaw(name),
    });
    expect(readTable).not.toHaveBeenCalled();
    expect(data.files).toBeNull();
    expect(data.warnings).toEqual([]);
  });
});

describe("the helpers", () => {
  it("estimates the deleted bytes, 0 for no deleted row and null without rows", () => {
    expect(deletedEstimate(100, 5, 10)).toBe(50);
    expect(deletedEstimate(100, 0, 0)).toBe(0);
    expect(deletedEstimate(null, 0, null)).toBe(0);
    expect(deletedEstimate(100, 5, 0)).toBeNull();
    expect(deletedEstimate(100, 5, null)).toBeNull();
    expect(deletedEstimate(null, 5, 10)).toBeNull();
    expect(deletedEstimate(100, null, 10)).toBeNull();
    expect(deletedEstimate(100, 20, 10)).toBe(100);
  });

  it("takes the quartiles by position of 0, 1, 2, 4, and 5 values", () => {
    expect(quartiles([])).toBeNull();
    expect(quartiles([7])).toEqual({ minimum: 7, firstQuartile: 7, median: 7, thirdQuartile: 7, maximum: 7 });
    expect(quartiles([1, 2])).toEqual({ minimum: 1, firstQuartile: 1, median: 2, thirdQuartile: 2, maximum: 2 });
    expect(quartiles([1, 2, 3, 4])).toEqual({ minimum: 1, firstQuartile: 2, median: 3, thirdQuartile: 4, maximum: 4 });
    expect(quartiles([1, 2, 3, 4, 5])).toEqual({ minimum: 1, firstQuartile: 2, median: 3, thirdQuartile: 4, maximum: 5 });
    expect(quartiles([10, 20, 30])).toEqual({ minimum: 10, firstQuartile: 10, median: 20, thirdQuartile: 30, maximum: 30 });
  });

  it("bounds the share of the small files from the quartiles", () => {
    const d = { count: 8, minimum: 10, firstQuartile: 20, median: 30, thirdQuartile: 40, maximum: 50 };
    expect(smallShareAtLeast(d, 51)).toBe(1);
    expect(smallShareAtLeast(d, 50)).toBe(0.75);
    expect(smallShareAtLeast(d, 41)).toBe(0.75);
    expect(smallShareAtLeast(d, 31)).toBe(0.5);
    expect(smallShareAtLeast(d, 21)).toBe(0.25);
    expect(smallShareAtLeast(d, 11)).toBe(1 / 8);
    expect(smallShareAtLeast(d, 10)).toBe(0);
    expect(smallShareAtLeast({ ...d, count: null }, 11)).toBe(0);
    expect(smallShareAtLeast({ ...d, count: 0 }, 11)).toBe(0);
    expect(smallShareAtLeast({ count: 3, minimum: null, firstQuartile: null, median: null, thirdQuartile: null, maximum: null }, 11)).toBeNull();
    expect(smallShareAtLeast({ count: 3, minimum: null, firstQuartile: null, median: 5, thirdQuartile: null, maximum: null }, 11)).toBe(0.5);
  });

  it("computes the commits per day from the oldest commit to the newest, over one day at least", () => {
    const end = "2026-10-05T00:00:00.000Z";
    expect(commitsPerDay(30, "2026-09-05T00:00:00.000Z", "2026-09-20T00:00:00.000Z")).toBe(2);
    expect(commitsPerDay(10, "2026-10-04T23:00:00.000Z", "2026-10-05T23:00:00.000Z")).toBe(10);
    expect(commitsPerDay(10, "2026-09-25T00:00:00.000Z", end)).toBe(1);
    expect(commitsPerDay(10, "2026-10-04T12:00:00.000Z", end)).toBe(10);
    expect(commitsPerDay(10, end, end)).toBe(10);
    expect(commitsPerDay(10, "2026-10-06T00:00:00.000Z", end)).toBe(10);
    expect(commitsPerDay(0, "2026-09-25T00:00:00.000Z", end)).toBe(0);
    expect(commitsPerDay(null, "2026-09-25T00:00:00.000Z", end)).toBeNull();
    expect(commitsPerDay(10, null, end)).toBeNull();
    expect(commitsPerDay(10, "2026-09-25T00:00:00.000Z", null)).toBeNull();
    expect(commitsPerDay(10, "yesterday", end)).toBeNull();
  });

  it("counts the whole days between two times, 0 when the second comes first, null when one is missing", () => {
    const from = "2026-10-01T12:00:00.000Z";
    expect(daysBetween(from, "2026-10-05T12:00:00.000Z")).toBe(4);
    expect(daysBetween(from, "2026-10-05T11:59:59.999Z")).toBe(3);
    expect(daysBetween(from, from)).toBe(0);
    expect(daysBetween(from, "2026-10-02T11:00:00.000Z")).toBe(0);
    expect(daysBetween(from, "2026-09-01T00:00:00.000Z")).toBe(0);
    expect(daysBetween(null, from)).toBeNull();
    expect(daysBetween(from, null)).toBeNull();
    expect(daysBetween("yesterday", from)).toBeNull();
    expect(daysBetween(from, "")).toBeNull();
  });

  it("divides only by a positive known number", () => {
    expect(ratio(1, 4)).toBe(0.25);
    expect(ratio(1, 0)).toBeNull();
    expect(ratio(null, 4)).toBeNull();
    expect(ratio(1, null)).toBeNull();
  });

  it("builds a stack, clipping the live bytes at 0 and marking the null inputs absent", () => {
    const full = stackOf({ fileSize: 1000, deleted: 100, footerSize: 50, staleColumnSize: 25, nonLiveFileSize: 200, metadataSize: 30, deletedSource: "files" });
    expect(full.components.map((c) => c.key)).toEqual(["live", "nonLive", "unreferenced", "deleted", "stale", "metadata", "footers"]);
    expect(full.components.map((c) => c.bytes)).toEqual([825, 200, 0, 100, 25, 30, 50]);
    expect(full.components.map((c) => c.absent)).toEqual([false, false, true, false, false, false, false]);
    expect(full.data).toBe(1150);
    expect(full.total).toBe(1230);
    expect(full.waste).toBe(325);
    expect(full.overhead).toBe(80);
    expect(full.wasteShare).toBe(325 / 1150);
    expect(full.overheadShare).toBe(80 / 1150);
    expect(full.listed).toBe(false);
    expect(full.components.find((c) => c.key === "deleted")!.source).toBe("files");
    expect(full.components.find((c) => c.key === "live")!.source).toBeUndefined();

    const clipped = stackOf({ fileSize: 100, deleted: 80, footerSize: 30, staleColumnSize: 0, nonLiveFileSize: 0, metadataSize: 0 });
    expect(clipped.components[0]!.bytes).toBe(0);
    expect(clipped.data).toBe(80);
    expect(clipped.total).toBe(110);
    expect(clipped.waste).toBe(80);
    expect(clipped.wasteShare).toBe(1);

    const empty = stackOf({ fileSize: null, deleted: null, footerSize: null, staleColumnSize: null, nonLiveFileSize: null, metadataSize: null, deletedSource: "table" });
    expect(empty.components.every((c) => c.absent && c.bytes === 0 && c.share === null)).toBe(true);
    expect(empty.components.find((c) => c.key === "deleted")!.source).toBeUndefined();
    expect([empty.data, empty.total, empty.waste, empty.overhead]).toEqual([null, null, null, null]);
    expect(empty.wasteShare).toBeNull();
    expect(empty.overheadShare).toBeNull();

    // Only overhead: no data, so no share, and the overhead counts in the total.
    const overheadOnly = stackOf({ fileSize: 0, deleted: 0, footerSize: 0, staleColumnSize: 0, nonLiveFileSize: 0, metadataSize: 500 });
    expect([overheadOnly.data, overheadOnly.total, overheadOnly.overhead]).toEqual([0, 500, 500]);
    expect(overheadOnly.overheadShare).toBeNull();
    expect(overheadOnly.components.every((c) => c.share === null)).toBe(true);
  });

  it("gives the shares of the data, with the overhead beyond 1", () => {
    const stack = stackOf({ fileSize: 600, deleted: 100, footerSize: 100, staleColumnSize: 0, nonLiveFileSize: 300, unreferencedFileSize: 400, metadataSize: 700 });
    // live 400, non-live 300, unreferenced 100, deleted 100: data 900; overhead 800.
    expect(stack.components.map((c) => [c.key, c.bytes, c.share])).toEqual([
      ["live", 400, 400 / 900], ["nonLive", 300, 300 / 900], ["unreferenced", 100, 100 / 900], ["deleted", 100, 100 / 900],
      ["stale", 0, 0], ["metadata", 700, 700 / 900], ["footers", 100, 100 / 900],
    ]);
    expect(stack.data).toBe(900);
    expect(stack.total).toBe(1700);
    expect(stack.overheadShare).toBe(800 / 900);
    expect(stack.overheadShare! + 1).toBeCloseTo(stack.total / stack.data, 12);
    expect(stack.wasteShare).toBe(500 / 900);
  });

  describe("the overlap of the non-live and the unreferenced files", () => {
    const base = { fileSize: 1000, deleted: 0, footerSize: 40, staleColumnSize: 0, metadataSize: 60 };
    const parts = (nonLiveFileSize: number | null, unreferencedFileSize?: number | null) => {
      const stack = stackOf({ ...base, nonLiveFileSize, unreferencedFileSize });
      const get = (key: string) => stack.components.find((c) => c.key === key)!;
      return { stack, nonLive: get("nonLive"), unreferenced: get("unreferenced") };
    };

    it("keeps all the non-live files and adds the rest when the unreferenced files are larger", () => {
      const { stack, nonLive, unreferenced } = parts(200, 500);
      expect([nonLive.bytes, unreferenced.bytes]).toEqual([200, 300]);
      expect(stack.listed).toBe(true);
      expect(stack.waste).toBe(500);
      expect(stack.total).toBe(1000 + 500 + 60);
    });

    it("keeps only the unreferenced bytes as non-live when the unreferenced files are smaller", () => {
      const { stack, nonLive, unreferenced } = parts(200, 50);
      expect([nonLive.bytes, unreferenced.bytes]).toEqual([50, 0]);
      expect([nonLive.absent, unreferenced.absent]).toEqual([false, false]);
      expect(stack.total).toBe(1000 + 50 + 60);
    });

    it("gives all to the non-live files when the two sizes are equal", () => {
      const { stack, nonLive, unreferenced } = parts(200, 200);
      expect([nonLive.bytes, unreferenced.bytes]).toEqual([200, 0]);
      expect(stack.total).toBe(1000 + 200 + 60);
    });

    it("gives nothing to either when the storage holds no unreferenced file", () => {
      const { stack, nonLive, unreferenced } = parts(200, 0);
      expect([nonLive.bytes, unreferenced.bytes]).toEqual([0, 0]);
      expect([nonLive.absent, unreferenced.absent]).toEqual([false, false]);
      expect(stack.listed).toBe(true);
      expect(stack.waste).toBe(0);
      expect(stack.total).toBe(1000 + 60);
      const none = parts(0, 0);
      expect([none.nonLive.bytes, none.unreferenced.bytes]).toEqual([0, 0]);
    });

    it("gives the whole of the unreferenced files to their component when nonLiveFileSize is missing", () => {
      const { stack, nonLive, unreferenced } = parts(null, 300);
      expect(nonLive).toMatchObject({ bytes: 0, absent: true });
      expect(unreferenced).toMatchObject({ bytes: 300, absent: false });
      expect(stack.listed).toBe(true);
      expect(stack.total).toBe(1000 + 300 + 60);
      const unlisted = parts(null, null);
      expect([unlisted.nonLive.absent, unlisted.unreferenced.absent, unlisted.stack.listed]).toEqual([true, true, false]);
    });

    it("keeps nonLiveFileSize as an upper bound when unreferencedFileSize is missing", () => {
      for (const missing of [null, undefined]) {
        const { stack, nonLive, unreferenced } = parts(200, missing);
        expect(nonLive).toMatchObject({ bytes: 200, absent: false });
        expect(unreferenced).toMatchObject({ bytes: 0, absent: true, share: null });
        expect(stack.listed).toBe(false);
        expect(stack.total).toBe(1000 + 200 + 60);
      }
    });
  });

  it("sorts the tables by waste, by total, and by label, with ties by total, label, and row", () => {
    const summary = {
      tables: {
        length: 5, id: [1, 2, 3, 4, 5], name: ["table_b", "table_a", "table_c", "table_a", "table_d"],
        fileSize: [100, 100, 100, 100, 200], nonLiveFileSize: [10, 0, 10, 0, 10],
      },
    } as unknown as Summary;
    const model = buildModel({ summary });
    const before = model.tables.map((t) => t.key);
    expect(sortTables(model.tables).map((t) => t.key)).toEqual(["t:5", "t:1", "t:3", "t:2", "t:4"]);
    expect(sortTables(model.tables, "waste").map((t) => t.key)).toEqual(["t:5", "t:1", "t:3", "t:2", "t:4"]);
    expect(sortTables(model.tables, "total").map((t) => t.key)).toEqual(["t:5", "t:1", "t:3", "t:2", "t:4"]);
    expect(sortTables(model.tables, "label").map((t) => t.key)).toEqual(["t:2", "t:4", "t:1", "t:3", "t:5"]);
    expect(model.tables.map((t) => t.key)).toEqual(before);
    const wasteOrder = { ...summary, tables: { ...summary.tables!, nonLiveFileSize: [10, 0, 50, 0, 10] } } as Summary;
    expect(sortTables(buildModel({ summary: wasteOrder }).tables, "waste").map((t) => t.key)).toEqual(["t:3", "t:5", "t:1", "t:2", "t:4"]);
  });

  it("sorts by the waste without the overhead, which the metadata and the footers no longer join", () => {
    // table_a has little waste and a large metadata; table_b more waste and no overhead; table_c the largest footers.
    const summary = {
      tables: {
        length: 3, id: [1, 2, 3], name: ["table_a", "table_b", "table_c"], fileSize: [1000, 1000, 1000],
        deletedRowCount: [0, 0, 0], rowCount: [10, 10, 10], staleColumnSize: [0, 0, 0],
        nonLiveFileSize: [10, 100, 20], metadataSize: [5000, 0, 0], footerSize: [0, 0, 900],
      },
    } as unknown as Summary;
    const model = buildModel({ summary });
    expect(model.tables.map((t) => t.stack.waste)).toEqual([10, 100, 20]);
    expect(model.tables.map((t) => t.stack.overhead)).toEqual([5000, 0, 900]);
    // By the old waste, with the overhead, the order would be table_a, table_c, table_b.
    const oldWaste = (t: TableModel) => t.stack.waste + t.stack.overhead;
    expect([...model.tables].sort((a, b) => oldWaste(b) - oldWaste(a)).map((t) => t.key)).toEqual(["t:1", "t:3", "t:2"]);
    expect(sortTables(model.tables, "waste").map((t) => t.key)).toEqual(["t:2", "t:3", "t:1"]);
    expect(sortTables(model.tables, "total").map((t) => t.key)).toEqual(["t:1", "t:2", "t:3"]);
    expect(model.collection.totals).toMatchObject({ waste: 130, overhead: 5900, footprint: 3000 + 130 + 5000 });
    expect(model.tables[0]!.stack.wasteShare).toBe(10 / 1010);
    expect(model.tables[0]!.stack.overheadShare).toBe(5000 / 1010);
  });
});

describe("the synthetic footprint", () => {
  const synthetic: Synthetic = syntheticFootprint();
  const { data } = synthetic;

  it("is deterministic for a seed and changes with the seed", () => {
    expect(syntheticFootprint().data).toEqual(data);
    expect(syntheticFootprint({ seed: 7 }).data).not.toEqual(data);
    const small = syntheticFootprint({ tables: 4, columns: 20, files: 30, sources: 2 });
    expect(small.data.summary.tables!.length).toBe(4);
    expect(small.data.summary.columns!.length).toBe(20);
    expect(small.data.files!.length).toBe(30);
  });

  it("has 3 sources, 40 tables, 1,000 columns, and 5,000 files, whose sums are the totals of the tables", () => {
    const s = data.summary;
    expect([s.sources!.length, s.tables!.length, s.columns!.length, data.files!.length]).toEqual([3, 40, 1000, 5000]);
    for (const [row, table] of synthetic.tables.entries()) {
      const sizes = table.fileRows.map((i) => data.files!.fileSize![i]!);
      expect(s.tables!.fileCount![row]).toBe(sizes.length);
      expect(s.tables!.fileSize![row]).toBe(sizes.reduce((a, b) => a + b, 0));
      expect(s.tables!.fileSizes![row]).toEqual(distributionOf(sizes));
      expect(s.tables!.rowCount![row]).toBe(table.fileRows.reduce((sum, i) => sum + data.files!.rowCount![i]!, 0));
      const columns = table.columnRows.reduce((sum, i) => sum + (s.columns!.compressedSize![i] ?? 0), 0);
      expect(columns + table.staleColumnSize).toBe(s.tables!.compressedSize![row]);
    }
  });

  it("has a partitions table whose rows sum to the files of each partitioned table, in format 0.2", () => {
    const s = data.summary;
    const p = data.partitions!;
    const files = data.files!;
    expect(data.manifest!.formatVersion).toBe("0.2");
    expect(p.length).toBe(synthetic.tables.reduce((sum, t) => sum + t.partitionRows.length, 0));
    for (const [row, table] of synthetic.tables.entries()) {
      expect(table.partitionRows.length).toBe(table.partitioned ? s.tables!.partitionCount![row] : 0);
      if (!table.partitioned) {
        continue;
      }
      expect(table.partitionRows.every((i) => p.table![i] === table.id && p.fileCount![i]! > 0)).toBe(true);
      expect(table.partitionRows.reduce((sum, i) => sum + p.fileSize![i]!, 0)).toBe(s.tables!.fileSize![row]);
      expect(table.partitionRows.reduce((sum, i) => sum + p.fileCount![i]!, 0)).toBe(table.fileRows.length);
      expect(table.partitionRows.reduce((sum, i) => sum + p.rowCount![i]!, 0)).toBe(s.tables!.rowCount![row]);
      // lastModifiedAt of a partition is the latest modifiedAt of its files, whose path names the partition.
      table.partitionRows.forEach((i, k) => {
        const mine = table.fileRows.filter((f) => files.path![f]!.startsWith(`part_col=${k}/`));
        const latest = Math.max(...mine.map((f) => Date.parse(files.modifiedAt![f]!)));
        expect(p.lastModifiedAt![i]).toBe(new Date(latest).toISOString());
      });
    }
    // The unreferenced files meet every case of the overlap rule.
    const cases = synthetic.tables.map((t) => Math.sign(t.unreferencedFileSize - t.nonLiveFileSize));
    expect(cases).toContain(1);
    expect(cases).toContain(-1);
    expect(synthetic.tables.some((t) => t.unreferencedFileSize === t.nonLiveFileSize && t.nonLiveFileSize > 0)).toBe(true);
    expect(synthetic.tables.some((t) => t.unreferencedFileSize === 0 && t.nonLiveFileSize > 0)).toBe(true);
    expect(synthetic.tables.some((t) => t.unreferencedFileSize > 0 && t.nonLiveFileSize === 0)).toBe(true);
  });

  it("builds the stacks by the overlap rule, and the fields of 0.2 of every table", () => {
    const model = buildModel(data);
    const tables = data.summary.tables!;
    for (const [row, info] of synthetic.tables.entries()) {
      const table = model.tables[row]!;
      const nonLive = Math.min(info.nonLiveFileSize, info.unreferencedFileSize);
      expect(table.stack.listed).toBe(true);
      expect(component(table, "nonLive").bytes).toBe(nonLive);
      expect(component(table, "unreferenced").bytes).toBe(info.unreferencedFileSize - nonLive);
      expect(table.unreferencedFileCount).toBe(tables.unreferencedFileCount![row]);
      expect(table.unreferencedFileCount! > 0).toBe(info.unreferencedFileSize > 0);
      expect(table.nonLiveOldestRemovedAt).toBe(tables.nonLiveOldestRemovedAt![row]);
      expect(table.nonLiveNewestRemovedAt).toBe(tables.nonLiveNewestRemovedAt![row]);
      expect(table.nonLiveNewestRemovedAt === null).toBe(table.nonLiveFileCount === 0);
      if (table.nonLiveFileCount === 1) {
        expect(table.nonLiveOldestRemovedAt).toBe(table.nonLiveNewestRemovedAt);
      }
      expect(table.metadataFileCount).toBe(tables.metadataFileCount![row]);
      const times = info.partitionRows.map((i) => data.partitions!.lastModifiedAt![i]!).sort();
      expect(table.partitions).toEqual({
        count: info.partitioned ? info.partitionRows.length : null, kept: info.partitionRows.length,
        oldestModifiedAt: times[0] ?? null, newestModifiedAt: times.at(-1) ?? null,
      });
    }
    expect(model.tables.some((t) => component(t, "unreferenced").bytes > 0)).toBe(true);
  });

  it("gives a footprint of format 0.1 without the fields of 0.2, whose stacks take the non-live files of the log", () => {
    const early = syntheticFootprint({ formatVersion: "0.1" });
    expect(early.data.manifest!.formatVersion).toBe("0.1");
    for (const field of FORMAT_02_TABLE_FIELDS) {
      expect(early.data.summary.tables![field]).toBeUndefined();
    }
    expect(early.data.partitions!.lastModifiedAt).toBeUndefined();
    const model = buildModel(early.data);
    expectInvariants(model);
    for (const [row, info] of early.tables.entries()) {
      const table = model.tables[row]!;
      expect(table.stack.listed).toBe(false);
      expect(component(table, "nonLive").bytes).toBe(info.nonLiveFileSize);
      expect(component(table, "unreferenced").absent).toBe(true);
      expect([table.unreferencedFileSize, table.nonLiveOldestRemovedAt, table.partitions.newestModifiedAt]).toEqual([null, null, null]);
      expect(table.partitions.kept).toBe(info.partitionRows.length);
    }
    // The same data, apart from the fields of 0.2.
    expect(early.data.summary.tables!.fileSize).toEqual(data.summary.tables!.fileSize);
    expect(syntheticFootprint({ withPartitions: false }).data.partitions).toBeNull();
  });

  it("builds its model in less than a second, with every invariant", () => {
    const start = performance.now();
    const model = buildModel(data);
    const tree = columnTree(model);
    const elapsed = performance.now() - start;
    console.log(`buildModel and columnTree of 1,000 columns and 5,000 files: ${elapsed.toFixed(1)} ms`);
    expect(elapsed).toBeLessThan(1000);
    expectInvariants(model);
    expectTree(tree);
    expect(model.collection.totals).toMatchObject({ sources: 3, tables: 40, columns: 1000, files: 5000 });
    expect(model.collection).toMatchObject({ collector: "synthetic-collector 1.0", library: "synthetic-lib 2.0", level: 2, startedAt: "2026-10-05T20:50:00.000Z" });
    expect(model.hasFiles).toBe(true);
  });

  it("gives the figures of the files and of the maintenance of every table", () => {
    const model = buildModel(data);
    const files = data.files!;
    const reference = Date.parse(COMPLETED_AT);
    for (const [row, info] of synthetic.tables.entries()) {
      const table = model.tables[row]!;
      expect(table.key).toBe(`t:${info.id}`);
      expect(table.sourceKey).toBe(`s:${info.source}`);
      const deleted = component(table, "deleted");
      expect(deleted.source).toBe("files");
      expectClose(deleted.bytes, deletedFromFiles(files, info.id));
      expect(table.files.source).toBe("files");
      expect([table.files.minimum, table.files.firstQuartile, table.files.median, table.files.thirdQuartile, table.files.maximum])
        .toEqual(["minimum", "firstQuartile", "median", "thirdQuartile", "maximum"].map((k) => (data.summary.tables!.fileSizes![row] as Record<string, number>)[k]));
      const ordered = info.fileRows.filter((i) => files.isVOrdered![i]).length;
      expect(table.files.vOrder).toMatchObject({ files: ordered, knownFiles: info.fileRows.length, source: "files" });
      expect(table.files.vOrder!.share).toBe(ordered / info.fileRows.length);
      expect(table.maintenance.hasOptimize).toBe(info.optimized);
      expect(table.maintenance.hasVacuum).toBe(info.vacuumed);
      if (info.vacuumed) {
        expect(table.maintenance.lastVacuumAt).not.toBeNull();
      }
      // The snapshot is a day before the end of the collection: the span ends there.
      expect(table.maintenance.lastCommitAt).toBe(data.summary.tables!.snapshotTimestamp![row]);
      const days = (reference - DAY_MS - Date.parse(table.maintenance.oldestCommitAt!)) / DAY_MS;
      expectClose(table.maintenance.commitsPerDay, table.maintenance.commitsRead! / Math.max(1, days));
      expect(table.maintenance.daysSinceLastCommit).toBe(1);
      const stale = component(table, "stale");
      expect(stale.bytes).toBe(info.staleColumnSize);
      expect(table.measures.staleColumnShare).toBe(info.staleColumnSize / table.compressedSize!);
      expect(table.measures.footerCoverage).toBe(1);
    }
    expect(synthetic.tables.some((t) => t.optimized) && synthetic.tables.some((t) => !t.optimized)).toBe(true);
    expect(model.tables.some((t) => t.maintenance.commitsPerDay === t.maintenance.commitsRead)).toBe(true);
    expect(model.tables.some((t) => component(t, "deleted").bytes > 0)).toBe(true);
    expect(model.tables.some((t) => t.files.smallFiles.count! > 0)).toBe(true);
  });

  it("gives a stale leaf to the tables with stale columns, and absent leaves to the partition columns", () => {
    const model = buildModel(data);
    const tree = columnTree(model);
    for (const info of synthetic.tables) {
      const node = findPath(tree, `t:${info.id}`)!.at(-1)!;
      expect(node.size).toBe(data.summary.tables!.compressedSize![info.id - 1]);
      const stale = node.children.find((n) => n.kind === "stale");
      if (info.staleColumnSize > 0) {
        expect(stale).toMatchObject({ key: `t:${info.id}/stale`, size: info.staleColumnSize, absent: false, children: [] });
      } else {
        expect(stale).toBeUndefined();
      }
      const absent = node.children.filter((n) => n.absent);
      expect(absent.length).toBe(info.partitioned ? 1 : 0);
      if (info.partitioned) {
        expect(node.children.at(-1)!.column!.isPartitionColumn).toBe(true);
      }
      expect(node.children.filter((n) => n.kind === "column").length).toBe(info.columnRows.length);
    }
    expect(synthetic.tables.some((t) => t.staleColumnSize > 0)).toBe(true);
    expect(synthetic.tables.some((t) => t.partitioned)).toBe(true);
  });

  it("gives the same quartiles and V-Order shares from the totals when it has no files table", () => {
    const withFiles = buildModel(data);
    const without = syntheticFootprint({ withFiles: false });
    expect(without.data.files).toBeNull();
    const model = buildModel(without.data);
    expectInvariants(model);
    expect(model.hasFiles).toBe(false);
    model.tables.forEach((table, row) => {
      const other = withFiles.tables[row]!;
      expect(table.files.source).toBe("table");
      expect([table.files.count, table.files.sum, table.files.minimum, table.files.median, table.files.maximum])
        .toEqual([other.files.count, other.files.sum, other.files.minimum, other.files.median, other.files.maximum]);
      expect(table.files.vOrder).toMatchObject({ files: other.files.vOrder!.files, share: other.files.vOrder!.share, source: "table", bytesShare: null });
      expect(component(table, "deleted").source).toBe("table");
      expect(table.files.smallFiles.count).toBeNull();
      expect(table.files.tinyFiles.count).toBeNull();
      // The bound from the quartiles never exceeds the exact share from the files.
      expect(table.files.smallFiles.shareAtLeast!).toBeLessThanOrEqual(other.files.smallFiles.shareAtLeast! + 1e-12);
      expect(table.files.tinyFiles.shareAtLeast!).toBeLessThanOrEqual(other.files.tinyFiles.shareAtLeast! + 1e-12);
    });
  });

  it("counts the tiny and the small files of every table against its sizes, for both options", () => {
    for (const options of [{}, { tinyFileSize: 64 * 1024, smallFileSize: 4 * MIB }]) {
      const model = buildModel(data, options);
      for (const [row, info] of synthetic.tables.entries()) {
        const sizes = info.fileRows.map((i) => data.files!.fileSize![i]!);
        const f = model.tables[row]!.files;
        for (const below of [f.tinyFiles, f.smallFiles]) {
          const count = sizes.filter((size) => size < below.threshold).length;
          expect(below).toEqual({ threshold: below.threshold, count, shareAtLeast: count / sizes.length });
        }
      }
      const tiny = model.tables.map((t) => t.files.tinyFiles.threshold);
      expect(new Set(tiny)).toEqual(new Set([(options as { tinyFileSize?: number }).tinyFileSize ?? DEFAULT_TINY_FILE_SIZE]));
      expect(model.tables.some((t) => t.files.tinyFiles.count! > 0)).toBe(true);
      expect(model.tables.some((t) => t.files.tinyFiles.count! < t.files.smallFiles.count!)).toBe(true);
    }
  });

  it("marks absent what a collection at level 1 does not have", () => {
    const model = buildModel(syntheticFootprint({ level: 1 }).data);
    expectInvariants(model);
    const tree = columnTree(model);
    expectTree(tree);
    expect(model.columns.every((c) => c.absent)).toBe(true);
    expect(tree.size).toBe(0);
    expect(tree.children.every((s) => s.absent && s.children.every((t) => t.absent && t.sizeSource === "columns"))).toBe(true);
    for (const table of model.tables) {
      expect(component(table, "footers").absent).toBe(true);
      expect(component(table, "stale").absent).toBe(true);
      expect(component(table, "live").absent).toBe(false);
      expect(table.measures.compressionRatio).toBeNull();
      expect(table.measures.footerCoverage).toBeNull();
      expect(table.files.source).toBe("files");
    }
    expect(model.collection.level).toBe(1);
  });

  it("marks absent the fields that an early 0.1 producer leaves out", () => {
    const early = syntheticFootprint({ dropTableFields: EARLY_TABLE_FIELDS }).data;
    const model = buildModel(early);
    expectInvariants(model);
    for (const table of model.tables) {
      expect(["nonLive", "unreferenced", "metadata", "stale"].map((k) => component(table, k).absent)).toEqual([true, true, true, true]);
      expect(component(table, "footers").absent).toBe(false);
      expect(table.stack.listed).toBe(false);
      expect(table.stack.waste).toBe(component(table, "deleted").bytes);
      expect(table.stack.overhead).toBe(component(table, "footers").bytes);
      expect(table.metadataFileCount).toBeNull();
      expect(table.maintenance.commitsRead).toBeNull();
      expect(table.maintenance.commitsPerDay).toBeNull();
      expect(table.measures.nonLiveShare).toBeNull();
      expect(table.files.vOrder!.source).toBe("files");
    }
    const noFiles = buildModel(syntheticFootprint({ dropTableFields: EARLY_TABLE_FIELDS, withFiles: false }).data);
    expect(noFiles.tables.every((t) => t.files.vOrder === null)).toBe(true);
  });

  it("builds a model without the operations or the sources", () => {
    const noOps = buildModel(syntheticFootprint({ dropTables: ["operations"] }).data);
    expect(noOps.tables.every((t) => t.maintenance.hasVacuum === null && t.maintenance.hasOptimize === null)).toBe(true);
    const noSources = buildModel(syntheticFootprint({ dropTables: ["sources"] }).data);
    expect(noSources.sources.map((s) => s.key)).toEqual(["s:none"]);
    expectInvariants(noSources);
    expectTree(columnTree(noSources));
  });
});
