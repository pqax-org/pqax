// The view model of @pqax/views: readFootprint and buildModel on every valid sample of the conformance kit, the figures
// of the model against hand computations from the tables that the reader returns, the tree of the treemap, the
// tolerance of the model to fields, tables, and parts that are missing or wrong, the pure helpers with their edge cases,
// and the synthetic footprint of 1,000 columns and 5,000 files. It runs in Node and in the browsers.
import { describe, expect, it, vi } from "vitest";
import { type FilesTable, type PqaxDocument, type Summary, openEmbedded, openPqax } from "@pqax/pqax";
import {
  DEFAULT_SMALL_FILE_SIZE, STACK_COMPONENTS, type FootprintData, type StorageModel, type TableModel, type TreeNode,
  buildModel, columnTree, commitsPerDay, deletedEstimate, findPath, quartiles, ratio, readFootprint, smallShareAtLeast,
  sortTables, stackOf,
} from "@pqax/views";
import { type Expected, SAMPLES, bytesOf, expectedByName, join, rewriteZip } from "../typescript/kit";
import {
  COMPLETED_AT, EARLY_TABLE_FIELDS, type Synthetic, distributionOf, syntheticFootprint,
} from "./synthetic";

const EXPECTED = expectedByName();
const ALL = Object.values(EXPECTED).map((expected) => expected.file).sort();
const DAY_MS = 86_400_000;

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
  expectClose(totals.footprint, model.tables.reduce((sum, t) => sum + t.stack.total, 0));
  expectClose(totals.waste, model.tables.reduce((sum, t) => sum + t.stack.waste, 0));
  expectClose(totals.waste, totals.footprint - model.tables.reduce((sum, t) => sum + component(t, "live").bytes, 0), 1e-6);

  for (const table of model.tables) {
    const { stack } = table;
    expect(stack.components.map((c) => c.key)).toEqual([...STACK_COMPONENTS]);
    expectClose(stack.total, stack.components.reduce((sum, c) => sum + c.bytes, 0));
    const live = component(table, "live");
    expectClose(stack.waste, stack.total - live.bytes, 1e-9);
    if (stack.total > 0) {
      expectClose(stack.components.reduce((sum, c) => sum + c.share!, 0), 1);
      expectClose(stack.wasteShare, stack.waste / stack.total);
    } else {
      expect(stack.components.every((c) => c.share === null)).toBe(true);
      expect(stack.wasteShare).toBeNull();
    }
    for (const c of stack.components) {
      expect(c.bytes).toBeGreaterThanOrEqual(0);
      if (c.absent) {
        expect(c.bytes).toBe(0);
      }
    }
    const unclipped = table.fileSize === null ? null
      : table.fileSize - component(table, "deleted").bytes - component(table, "footers").bytes - component(table, "stale").bytes;
    if (unclipped !== null && unclipped >= 0) {
      expectClose(live.bytes, unclipped);
      expectClose(stack.total, table.fileSize! + component(table, "nonLive").bytes + component(table, "metadata").bytes);
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
    const share = files.smallFiles.shareAtLeast;
    if (share !== null) {
      expect(share).toBeGreaterThanOrEqual(0);
      expect(share).toBeLessThanOrEqual(1);
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
    expect(ALL.length).toBe(10);
  });

  it.each(ALL)("%s gives the collection line of its manifest", async (file) => {
    const expected = expectedOf(file);
    const { doc, model } = await modelOf(file);
    const c = model.collection;
    expect(c.formatVersion).toBe(expected.formatVersion);
    expect(c.collector).toBe("pqax-conformance-kit 0.1");
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

  it.each(ALL)("%s holds the invariants of the model and of the tree", async (file) => {
    const { model } = await modelOf(file);
    expectInvariants(model);
    expectTree(columnTree(model));
  });

  it.each(ALL)("%s gives stacks whose total is fileSize + nonLiveFileSize + metadataSize", async (file) => {
    const { doc, model } = await modelOf(file);
    const tables = doc.summary.tables!;
    model.tables.forEach((table, row) => {
      const total = tables.fileSize![row]! + tables.nonLiveFileSize![row]! + tables.metadataSize![row]!;
      expectClose(table.stack.total, total);
      expectClose(table.stack.waste, total - component(table, "live").bytes);
      expect(component(table, "footers").bytes).toBe(tables.footerSize![row]);
      expect(component(table, "metadata").bytes).toBe(tables.metadataSize![row]);
      expect(component(table, "nonLive").bytes).toBe(tables.nonLiveFileSize![row]);
      expect(component(table, "stale").bytes).toBe(0);
      expect(table.stack.components.some((c) => c.absent)).toBe(false);
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

  it.each(ALL)("%s flags the operations and computes the commits per day against the end of the collection", async (file) => {
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
      const days = (reference - Date.parse(tables.oldestCommitAt![row]!)) / DAY_MS;
      expect(days).toBeGreaterThan(1);
      expectClose(m.commitsPerDay, tables.commitsRead![row]! / days);
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
    // sales: one file of 47,820 bytes and 5,000 rows has 25 deleted rows.
    const deleted = (47820 * 25) / 5000;
    expect(sales!.key).toBe("t:1");
    expect(sales!.label).toBe("sales");
    expect(sales!.sourceKey).toBe("s:1");
    expect(sales!.stack.components.map((c) => [c.key, c.bytes])).toEqual([
      ["live", 159121 - deleted - 3847], ["nonLive", 12345], ["deleted", deleted], ["metadata", 12469], ["footers", 3847], ["stale", 0],
    ]);
    expect(sales!.stack.total).toBeCloseTo(183935, 9);
    expect(sales!.stack.waste).toBeCloseTo(12345 + deleted + 12469 + 3847, 9);
    expect(orders!.key).toBe("t:2");
    expect(orders!.label).toBe("dbo.orders");
    expect(orders!.stack.components.map((c) => [c.key, c.bytes])).toEqual([
      ["live", 39884 - 1254], ["nonLive", 0], ["deleted", 0], ["metadata", 1372], ["footers", 1254], ["stale", 0],
    ]);
    expect(orders!.stack.total).toBe(41256);
    expect(orders!.stack.waste).toBe(2626);
    expect(model.collection.totals.footprint).toBeCloseTo(183935 + 41256, 9);
    expect(sortTables(model.tables).map((t) => t.key)).toEqual(["t:1", "t:2"]);
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
      expect(model.collection.totals).toEqual({ sources: 0, tables: 0, columns: 0, files: null, fileSize: null, rowCount: null, footprint: 0, waste: 0 });
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
      expect(table.stack.total).toBe(0);
      expect(table.stack.wasteShare).toBeNull();
      expect(table.files.source).toBeNull();
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
    expect(sales.stack.total).toBeCloseTo(159121, 9);
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

  it("falls back on snapshotTimestamp when the manifest has no collection, with a span of one day at least", async () => {
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
    }
    expect(buildModel({ summary, manifest: { formatVersion: "0.1" } }).collection.formatVersion).toBe("0.1");
  });

  it("takes startedAt as the reference when the collection has no completedAt", async () => {
    const summary = await sampleSummary();
    const startedAt = "2023-11-24T22:14:50.000Z";
    const model = buildModel({ summary, manifest: { collection: { level: 2, startedAt } } });
    expect(model.tables[0]!.maintenance.commitsPerDay).toBe(4 / 10);
    expect(model.collection.startedAt).toBe(startedAt);
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
    expect(data.files).toBeNull();
    expect(data.warnings).toHaveLength(1);
    expect(data.warnings![0]).toContain("json.invalid");
    const model = buildModel(data);
    expect(model.warnings).toEqual(data.warnings);
    expect(model.hasFiles).toBe(false);
    expect(model.tables.every((t) => t.files.source === "table")).toBe(true);

    const plain: PqaxDocument = { ...fake, readTable: (() => Promise.reject(new Error("no code"))) as never };
    const second = await readFootprint(plain);
    expect(second.warnings).toHaveLength(1);
    expect(second.warnings![0]).not.toContain("(");
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

  it("computes the commits per day over one day at least", () => {
    const end = "2026-10-05T00:00:00.000Z";
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

  it("divides only by a positive known number", () => {
    expect(ratio(1, 4)).toBe(0.25);
    expect(ratio(1, 0)).toBeNull();
    expect(ratio(null, 4)).toBeNull();
    expect(ratio(1, null)).toBeNull();
  });

  it("builds a stack, clipping the live bytes at 0 and marking the null inputs absent", () => {
    const full = stackOf({ fileSize: 1000, deleted: 100, footerSize: 50, staleColumnSize: 25, nonLiveFileSize: 200, metadataSize: 30, deletedSource: "files" });
    expect(full.components.map((c) => c.bytes)).toEqual([825, 200, 100, 30, 50, 25]);
    expect(full.total).toBe(1230);
    expect(full.waste).toBe(405);
    expect(full.wasteShare).toBe(405 / 1230);
    expect(full.components.find((c) => c.key === "deleted")!.source).toBe("files");
    expect(full.components.find((c) => c.key === "live")!.source).toBeUndefined();

    const clipped = stackOf({ fileSize: 100, deleted: 80, footerSize: 30, staleColumnSize: 0, nonLiveFileSize: 0, metadataSize: 0 });
    expect(clipped.components[0]!.bytes).toBe(0);
    expect(clipped.total).toBe(110);
    expect(clipped.waste).toBe(110);

    const empty = stackOf({ fileSize: null, deleted: null, footerSize: null, staleColumnSize: null, nonLiveFileSize: null, metadataSize: null, deletedSource: "table" });
    expect(empty.components.every((c) => c.absent && c.bytes === 0 && c.share === null)).toBe(true);
    expect(empty.components.find((c) => c.key === "deleted")!.source).toBeUndefined();
    expect(empty.total).toBe(0);
    expect(empty.wasteShare).toBeNull();
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
      const days = (reference - Date.parse(table.maintenance.oldestCommitAt!)) / DAY_MS;
      expectClose(table.maintenance.commitsPerDay, table.maintenance.commitsRead! / Math.max(1, days));
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
      // The bound from the quartiles never exceeds the exact share from the files.
      expect(table.files.smallFiles.shareAtLeast!).toBeLessThanOrEqual(other.files.smallFiles.shareAtLeast! + 1e-12);
    });
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
      expect(["nonLive", "metadata", "stale"].map((k) => component(table, k).absent)).toEqual([true, true, true]);
      expect(component(table, "footers").absent).toBe(false);
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
