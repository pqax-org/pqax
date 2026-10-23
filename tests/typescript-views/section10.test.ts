// The derived measures of section 10 of docs/object-model.md, on footprints built by hand: the rules of the storage
// stack when a component is absent and the share of each component (10.2), the file size figures and the V-Order
// shares (10.3), the pace of the commits and the maintenance operations (10.4), the partitions (10.5), the compression
// ratio of a table against that of its columns (10.5, 10.6), the measures of a column (10.6), the measures of the
// collection (10.7), and the order of the tables by waste (10.2). The footprints of the second half are those of the
// tests of C# and Python. The expected values come from the text of section 10, not from the code. A measure that needs an absent field is
// null, never 0: the principle "Unknown is not zero" of section 2 and rule 2 of 10.1.
import { describe, expect, it } from "vitest";
import type { Summary } from "@pqax/pqax";
import { type StorageModel, type TableModel, buildModel, columnTree, findPath, sortTables, stackOf } from "@pqax/views";

/** Equal within a relative tolerance, for divisions. */
function expectClose(actual: number | null | undefined, expected: number, tolerance = 1e-12): void {
  expect(actual).not.toBeNull();
  expect(actual).toBeDefined();
  expect(Math.abs(actual! - expected)).toBeLessThanOrEqual(tolerance * Math.max(1, Math.abs(expected)));
}

function component(stack: ReturnType<typeof stackOf>, key: string) {
  return stack.components.find((c) => c.key === key)!;
}

/** The stack inputs, every one absent unless given. */
function stack(input: Partial<Parameters<typeof stackOf>[0]>) {
  return stackOf({
    fileSize: null, deleted: null, footerSize: null, staleColumnSize: null, nonLiveFileSize: null, unreferencedFileSize: null,
    metadataSize: null, ...input,
  });
}

function modelOf(summary: unknown, collection?: Record<string, unknown>): StorageModel {
  return buildModel({ summary: summary as Summary, manifest: collection === undefined ? null : ({ collection } as never) });
}

function byKey(model: StorageModel, key: string): TableModel {
  return model.tables.find((t) => t.key === key)!;
}

describe("10.2 the storage stack when Live is null", () => {
  it("10.2 Data is null when Live is null, and so is every share, while Waste, Overhead, and Total sum what is present", () => {
    // FileSize is absent, so Live is null. NonLive 7, Unreferenced absent, Deleted 10, Stale 2: Waste 19.
    // Metadata 3, Footers 5: Overhead 8. Total: the seven components, the absent ones as 0, 27.
    const s = stack({ deleted: 10, footerSize: 5, staleColumnSize: 2, nonLiveFileSize: 7, metadataSize: 3 });
    expect(component(s, "live").absent).toBe(true);
    expect(s.data).toBeNull();
    expect(s.waste).toBe(19);
    expect(s.overhead).toBe(8);
    expect(s.total).toBe(27);
    // 10.2 WasteShare = Waste / Data, OverheadShare = Overhead / Data, a component share = component / Data: all null.
    expect(s.wasteShare).toBeNull();
    expect(s.overheadShare).toBeNull();
    for (const c of s.components) {
      expect(c.share).toBeNull();
    }
  });

  it("10.2 Data is null when FileSize is absent from a table of the summary", () => {
    const model = modelOf({
      tables: {
        length: 1, id: [1], name: ["no_file_size"], rowCount: [100], deletedRowCount: [0], footerSize: [5], staleColumnSize: [2],
        nonLiveFileSize: [7], metadataSize: [3],
      },
    });
    const { stack: s } = model.tables[0]!;
    // Deleted is 0 since DeletedRowCount is 0, whatever the other fields.
    expect(component(s, "deleted")).toMatchObject({ absent: false, bytes: 0 });
    expect(component(s, "live").absent).toBe(true);
    expect(s.data).toBeNull();
    expect(s.waste).toBe(7 + 0 + 2);
    expect(s.overhead).toBe(3 + 5);
    expect(s.total).toBe(9 + 8);
    expect(s.wasteShare).toBeNull();
    expect(s.overheadShare).toBeNull();
    expect(s.components.every((c) => c.share === null)).toBe(true);
  });
});

describe("10.2 the sums of the stack when every component is absent", () => {
  it("10.2 Waste, Overhead, Total, and Data are null when all their components are absent", () => {
    const s = stack({});
    expect(s.components.every((c) => c.absent && c.bytes === 0 && c.share === null)).toBe(true);
    expect(s.waste).toBeNull();
    expect(s.overhead).toBeNull();
    expect(s.total).toBeNull();
    expect(s.data).toBeNull();
    expect(s.wasteShare).toBeNull();
    expect(s.overheadShare).toBeNull();
  });

  it("10.2 Total with only MetadataSize is MetadataSize, and Waste is null", () => {
    const s = stack({ metadataSize: 30 });
    expect(s.total).toBe(30);
    expect(s.overhead).toBe(30);
    expect(s.waste).toBeNull();
    expect(s.data).toBeNull();
  });

  it("10.2 Waste with only StaleColumnSize is StaleColumnSize, and Overhead is null", () => {
    const s = stack({ staleColumnSize: 20 });
    expect(s.waste).toBe(20);
    expect(s.overhead).toBeNull();
    expect(s.total).toBe(20);
    expect(s.data).toBeNull();
  });

  it("10.2 Overhead is the present one of Metadata and Footers, the absent one counting as 0", () => {
    expect(stack({ fileSize: 1000, footerSize: 40 }).overhead).toBe(40);
    expect(stack({ fileSize: 1000, metadataSize: 25 }).overhead).toBe(25);
    // A present zero is a value: the overhead is 0, not null.
    expect(stack({ fileSize: 1000, metadataSize: 0 }).overhead).toBe(0);
  });

  it("10.2 Waste is the sum of the present components, an absent one counting as 0", () => {
    // NonLive 100, Unreferenced absent (no listing), Deleted absent, Stale 20.
    const s = stack({ fileSize: 1000, staleColumnSize: 20, nonLiveFileSize: 100 });
    expect(s.waste).toBe(120);
    // Live = max(0, 1000 - 0 - 0 - 20) = 980, Data = 980 + 120.
    expect(s.data).toBe(1100);
    expect(s.total).toBe(1100);
    expect(s.overhead).toBeNull();
  });

  it("10.2 WasteShare and OverheadShare are null when Waste or Overhead is null, even when Data is known", () => {
    // Only FileSize: Live 1000, Waste and Overhead null. Data = Live + Waste is null only when Live is null.
    const s = stack({ fileSize: 1000 });
    expect(component(s, "live").bytes).toBe(1000);
    expect(s.waste).toBeNull();
    expect(s.overhead).toBeNull();
    expect(s.total).toBe(1000);
    expect(s.data).toBe(1000);
    // 10.1 rule 2: a measure is null when a field that it needs is absent.
    expect(s.wasteShare).toBeNull();
    expect(s.overheadShare).toBeNull();
  });
});

describe("10.2 the share of a component", () => {
  it("10.2 the share of an absent component is null, and that of a component present at 0 is 0", () => {
    // Live 1000, NonLive 100, Unreferenced absent, Deleted 0, Stale 0, Metadata 10, Footers absent. Data 1100.
    const s = stack({ fileSize: 1000, deleted: 0, staleColumnSize: 0, nonLiveFileSize: 100, metadataSize: 10 });
    expect(s.data).toBe(1100);
    expectClose(component(s, "live").share, 1000 / 1100);
    expectClose(component(s, "nonLive").share, 100 / 1100);
    expect(component(s, "unreferenced")).toMatchObject({ absent: true, bytes: 0, share: null });
    expect(component(s, "deleted")).toMatchObject({ absent: false, bytes: 0, share: 0 });
    expect(component(s, "stale")).toMatchObject({ absent: false, bytes: 0, share: 0 });
    expectClose(component(s, "metadata").share, 10 / 1100);
    expect(component(s, "footers")).toMatchObject({ absent: true, bytes: 0, share: null });
  });

  it("10.2 the shares are null when Data is 0", () => {
    const s = stack({ fileSize: 0, deleted: 0, footerSize: 0, staleColumnSize: 0, nonLiveFileSize: 0, unreferencedFileSize: 0, metadataSize: 30 });
    expect(s.data).toBe(0);
    expect(s.total).toBe(30);
    expect(s.wasteShare).toBeNull();
    expect(s.overheadShare).toBeNull();
    expect(s.components.every((c) => c.share === null)).toBe(true);
  });
});

describe("10.7 the sums of the collection", () => {
  // table_full: FileSize 1000, DeletedRowCount 0 so Deleted 0, FooterSize 50, StaleColumnSize 0, NonLiveFileSize 100,
  // MetadataSize 30. Live 950, Waste 100, Overhead 80, Total 1130.
  // table_bare: no field of the stack. Total, Waste, and Overhead null.
  // table_files: FileSize 500 only. Live 500, Total 500, Waste and Overhead null.
  const summary = {
    tables: {
      length: 3, id: [1, 2, 3], name: ["table_full", "table_bare", "table_files"],
      fileSize: [1000, null, 500], rowCount: [10, null, null], deletedRowCount: [0, null, null], footerSize: [50, null, null],
      staleColumnSize: [0, null, null], nonLiveFileSize: [100, null, null], metadataSize: [30, null, null],
    },
  };

  it("10.7 Footprint, Waste, and Overhead sum the tables whose value is not null", () => {
    const model = modelOf(summary);
    expect(model.tables.map((t) => t.stack.total)).toEqual([1130, null, 500]);
    expect(model.tables.map((t) => t.stack.waste)).toEqual([100, null, null]);
    expect(model.tables.map((t) => t.stack.overhead)).toEqual([80, null, null]);
    expect(model.collection.totals.footprint).toBe(1630);
    expect(model.collection.totals.waste).toBe(100);
    expect(model.collection.totals.overhead).toBe(80);
  });

  it("10.7 a sum is null when no table has the value", () => {
    const model = modelOf({ tables: { length: 2, id: [1, 2], name: ["table_bare", "table_rows"], rowCount: [null, 10] } });
    expect(model.collection.totals.footprint).toBeNull();
    expect(model.collection.totals.waste).toBeNull();
    expect(model.collection.totals.overhead).toBeNull();
    expect(model.collection.totals.fileSize).toBeNull();
    expect(model.collection.totals.rowCount).toBe(10);
  });

  it("10.7 a sum over no table is null", () => {
    const model = modelOf({});
    expect(model.collection.totals).toMatchObject({ tables: 0, footprint: null, waste: null, overhead: null });
  });
});

describe("10.6 FilesWithoutColumnShare", () => {
  it("10.6 FilesWithoutColumnShare divides FileCountWithoutColumn by the FooterCount of its table, not its FileCount", () => {
    // Table 1 read 4 footers of 10 files; table 2 has no FooterCount; table 3 read no footer.
    const model = modelOf({
      tables: { length: 3, id: [1, 2, 3], name: ["a", "b", "c"], fileCount: [10, 10, 10], footerCount: [4, null, 0] },
      columns: {
        length: 4, id: [1, 2, 3, 4], table: [1, 1, 2, 3], name: ["added_later", "everywhere", "b_col", "c_col"],
        fileCountWithoutColumn: [1, 0, 1, 0],
      },
    });
    expect(model.columns.map((c) => c.filesWithoutColumnShare)).toEqual([0.25, 0, null, null]);
  });
});

describe("10.4 the history of a table", () => {
  const collection = { startedAt: "2026-10-01T00:00:00.000Z", completedAt: "2026-10-11T12:00:00.000Z" };

  it("10.4 CommitsPerDay is CommitsRead over the days from OldestCommitAt to LastCommitAt, at least one", () => {
    const model = modelOf({
      tables: {
        length: 5, id: [1, 2, 3, 4, 5], name: ["ten_days", "count_only", "six_hours", "a_day_and_a_half", "no_oldest"],
        commitsRead: [20, null, 5, 6, 20], commitCount: [100, 100, 100, 100, 100],
        oldestCommitAt: ["2026-09-01T00:00:00.000Z", "2026-09-01T00:00:00.000Z", "2026-09-01T00:00:00.000Z", "2026-09-01T00:00:00.000Z", null],
        snapshotTimestamp: ["2026-09-11T00:00:00.000Z", "2026-09-11T00:00:00.000Z", "2026-09-01T06:00:00.000Z", "2026-09-02T12:00:00.000Z", "2026-09-11T00:00:00.000Z"],
      },
    }, collection);
    const rates = model.tables.map((t) => t.maintenance.commitsPerDay);
    // 20 over 10 days.
    expect(rates[0]).toBe(2);
    // No CommitsRead: null, although CommitCount is present.
    expect(rates[1]).toBeNull();
    // A span shorter than a day counts as one day: 5 commits in 6 hours give 5.
    expect(rates[2]).toBe(5);
    // The days as a fraction: 6 commits over 1.5 days.
    expect(rates[3]).toBe(4);
    // No OldestCommitAt: null.
    expect(rates[4]).toBeNull();
  });

  it("10.4 DaysSinceLastCommit counts to CompletedAt, else to StartedAt when CompletedAt is absent or does not parse", () => {
    const summary = { tables: { length: 1, id: [1], name: ["t"], snapshotTimestamp: ["2026-10-01T12:00:00.000Z"] } };
    const days = (c: Record<string, unknown> | undefined) => modelOf(summary, c).tables[0]!.maintenance.daysSinceLastCommit;
    // floor(10 days) to CompletedAt.
    expect(days({ startedAt: "2026-10-04T00:00:00.000Z", completedAt: "2026-10-11T12:00:00.000Z" })).toBe(10);
    // floor(2.5 days) to StartedAt.
    expect(days({ startedAt: "2026-10-04T00:00:00.000Z" })).toBe(2);
    expect(days({ startedAt: "2026-10-04T00:00:00.000Z", completedAt: "not a date" })).toBe(2);
    expect(days({ startedAt: "2026-10-04T00:00:00.000Z", completedAt: null })).toBe(2);
    // Neither: null.
    expect(days({ level: 1 })).toBeNull();
    expect(days({ completedAt: "not a date" })).toBeNull();
    expect(days(undefined)).toBeNull();
    // max(0, ...): a collection that ends before the last commit gives 0.
    expect(days({ completedAt: "2026-09-01T00:00:00.000Z" })).toBe(0);
  });

  it("10.4 HasVacuum and HasOptimize are null without an Operation, false when none matches, and match in any case", () => {
    const model = modelOf({
      tables: { length: 4, id: [1, 2, 3, 4], name: ["vacuumed", "written", "no_operation", "unnamed_operation"] },
      operations: {
        length: 6, table: [1, 1, 1, 2, 2, 4], name: ["WRITE", "vacuum end", "Optimize", "WRITE", "MERGE", null],
        commitCount: [3, 1, 1, 2, 1, 1],
        lastAt: ["2026-09-03T00:00:00.000Z", "2026-09-05T00:00:00.000Z", "2026-09-04T00:00:00.000Z", "2026-09-02T00:00:00.000Z", "2026-09-03T00:00:00.000Z", "2026-09-06T00:00:00.000Z"],
      },
    });
    const m = (key: string) => byKey(model, key).maintenance;
    expect([m("t:1").hasVacuum, m("t:1").hasOptimize]).toEqual([true, true]);
    expect(m("t:1").lastVacuumAt).toBe("2026-09-05T00:00:00.000Z");
    expect(m("t:1").lastOptimizeAt).toBe("2026-09-04T00:00:00.000Z");
    // Operations, none of them a VACUUM or an OPTIMIZE: false.
    expect([m("t:2").hasVacuum, m("t:2").hasOptimize]).toEqual([false, false]);
    expect([m("t:2").lastVacuumAt, m("t:2").lastOptimizeAt]).toEqual([null, null]);
    // No Operation for the table, although the operations table has rows: null.
    expect([m("t:3").hasVacuum, m("t:3").hasOptimize]).toEqual([null, null]);
    // An operation row without a Name is no Operation: null.
    expect([m("t:4").hasVacuum, m("t:4").hasOptimize]).toEqual([null, null]);
  });

  it("10.4 HasVacuum and HasOptimize are null when the footprint has no operations at all", () => {
    const model = modelOf({ tables: { length: 1, id: [1], name: ["t"] } });
    expect(model.tables[0]!.maintenance.hasVacuum).toBeNull();
    expect(model.tables[0]!.maintenance.hasOptimize).toBeNull();
  });
});

describe("10.5 and 10.6 CompressionRatio of a table and ColumnCompressionRatio of its node", () => {
  it("10.6 a node of columnTree has ColumnCompressionRatio; 10.5 the table has CompressionRatio from its totals; the two differ", () => {
    // Table 1: totals 3000 over 1000, CompressionRatio 3. Its columns: 1600 over 400, 200 over 100, a partition column
    // without sizes, and a column without an UncompressedSize. ColumnCompressionRatio: (1600 + 200) / (400 + 100) = 3.6.
    // Table 2: one column, 100 over 100. The source and the root: (1800 + 100) / (500 + 100).
    const model = modelOf({
      sources: { length: 1, id: [1], name: ["lake"] },
      tables: {
        length: 2, id: [1, 2], source: [1, 1], name: ["sales", "dates"], compressedSize: [1000, 100], uncompressedSize: [3000, 100],
        staleColumnSize: [400, 0],
      },
      columns: {
        length: 5, id: [1, 2, 3, 4, 5], table: [1, 1, 1, 1, 2], name: ["amount", "code", "year", "note", "date"],
        isPartitionColumn: [false, false, true, false, false],
        compressedSize: [400, 100, null, 50, 100], uncompressedSize: [1600, 200, null, null, 100],
      },
    });
    const sales = byKey(model, "t:1");
    expect(sales.measures.compressionRatio).toBe(3);
    const tree = columnTree(model);
    const tableNode = findPath(tree, "t:1")!.at(-1)!;
    expectClose(tableNode.compressionRatio, 3.6);
    expect(tableNode.compressionRatio).not.toBe(sales.measures.compressionRatio);
    // A column leaf has the CompressionRatio of its column.
    expect(findPath(tree, "c:1")!.at(-1)!.compressionRatio).toBe(4);
    expect(findPath(tree, "c:2")!.at(-1)!.compressionRatio).toBe(2);
    expect(findPath(tree, "c:3")!.at(-1)!.compressionRatio).toBeNull();
    expect(findPath(tree, "c:4")!.at(-1)!.compressionRatio).toBeNull();
    expect(model.columns.map((c) => c.compressionRatio)).toEqual([4, 2, null, null, 1]);
    // The source and the root add the columns of every table.
    expectClose(findPath(tree, "s:1")!.at(-1)!.compressionRatio, 1900 / 600);
    expectClose(tree.compressionRatio, 1900 / 600);
    expect(byKey(model, "t:2").measures.compressionRatio).toBe(1);
  });
});

describe("10.3 the source of the file size figures", () => {
  it("10.3 the figures come from FileSizes when it has any of the minimum, the quartiles, and the maximum", () => {
    const model = modelOf({
      tables: {
        length: 5, id: [1, 2, 3, 4, 5], name: ["quartiles", "median", "count_sum", "none", "maximum"], fileCount: [4, 4, 4, 4, 4],
        fileSizes: [
          { count: 4, firstQuartile: 10, thirdQuartile: 30 },
          { median: 20 },
          { count: 4, sum: 100 },
          null,
          { maximum: 50 },
        ],
      },
    });
    expect(model.tables.map((t) => t.files.source)).toEqual(["table", "table", null, null, "table"]);
    const quartiles = model.tables[0]!.files;
    expect([quartiles.minimum, quartiles.firstQuartile, quartiles.median, quartiles.thirdQuartile, quartiles.maximum]).toEqual([null, 10, null, 30, null]);
    // FileCount stands for the missing Count of FileSizes.
    expect(model.tables[1]!.files.count).toBe(4);
  });
});

describe("10.2 the order of the tables", () => {
  // waste_100: Waste 100, Total 1100. big_no_waste: FileSize 5000 only, Waste null, Total 5000. waste_10: Waste 10,
  // Total 1010. bare: nothing, Waste and Total null. mid_no_waste: Waste null, Total 2000. waste_0: Waste 0, Total 100.
  const summary = {
    tables: {
      length: 6, id: [1, 2, 3, 4, 5, 6], name: ["waste_100", "big_no_waste", "waste_10", "bare", "mid_no_waste", "waste_0"],
      fileSize: [1000, 5000, 1000, null, 2000, 100], nonLiveFileSize: [100, null, 10, null, null, 0],
    },
  };

  it("10.2 the tables go by Waste, the largest first, then by Total, then by name; a null Waste goes after the others", () => {
    const model = modelOf(summary);
    expect(model.tables.map((t) => t.stack.waste)).toEqual([100, null, 10, null, null, 0]);
    expect(model.tables.map((t) => t.stack.total)).toEqual([1100, 5000, 1010, null, 2000, 100]);
    expect(sortTables(model.tables, "waste").map((t) => t.key)).toEqual(["t:1", "t:3", "t:6", "t:2", "t:5", "t:4"]);
    expect(sortTables(model.tables).map((t) => t.key)).toEqual(["t:1", "t:3", "t:6", "t:2", "t:5", "t:4"]);
  });

  it("10.2 by Total, a null Total goes after the others", () => {
    const model = modelOf(summary);
    expect(sortTables(model.tables, "total").map((t) => t.key)).toEqual(["t:2", "t:5", "t:1", "t:3", "t:6", "t:4"]);
  });

  it("10.2 among the tables with a null Waste, a null Total goes last and equal totals go by name", () => {
    const model = modelOf({
      tables: { length: 3, id: [1, 2, 3], name: ["zeta", "bare", "alpha"], fileSize: [700, null, 700] },
    });
    expect(sortTables(model.tables, "waste").map((t) => t.key)).toEqual(["t:3", "t:1", "t:2"]);
  });
});

// ---- every measure of section 10, with the same footprints as the tests of C# and Python ----

/** A columnar table from rows, a missing field as null, with its length. */
function columnar(rows: readonly Record<string, unknown>[]): Record<string, unknown> {
  const keys: string[] = [];
  for (const row of rows) {
    keys.push(...Object.keys(row).filter((key) => !keys.includes(key)));
  }
  const table: Record<string, unknown> = { length: rows.length };
  for (const key of keys) {
    table[key] = rows.map((row) => (key in row ? row[key] : null));
  }
  return table;
}

function modelFrom(parts: { summary: unknown; files?: unknown; partitions?: unknown }): StorageModel {
  return buildModel({
    summary: parts.summary as Summary, manifest: null,
    files: (parts.files ?? null) as never, partitions: (parts.partitions ?? null) as never,
  });
}

describe("10.2 the share of each component, with a listing", () => {
  it("10.2 each of the seven shares is the component over Data", () => {
    // FileSize 1000, DeletedRowCount 0 so Deleted 0, FooterSize 40, StaleColumnSize 10, NonLiveFileSize 300,
    // UnreferencedFileSize 200, MetadataSize 50. Live = max(0, 1000 - 0 - 40 - 10) = 950. Listed: NonLive =
    // min(300, 200) = 200, Unreferenced = 200 - 200 = 0. Waste = 200 + 0 + 0 + 10 = 210. Data = 950 + 210 = 1160.
    const s = stack({ fileSize: 1000, deleted: 0, footerSize: 40, staleColumnSize: 10, nonLiveFileSize: 300, unreferencedFileSize: 200, metadataSize: 50 });
    expect(s.data).toBe(1160);
    expect(s.components.map((c) => c.share)).toEqual([950 / 1160, 200 / 1160, 0, 0, 10 / 1160, 50 / 1160, 40 / 1160]);
  });

  it("10.2 the shares of Metadata and Footers can exceed 1 and are not clipped", () => {
    // FileSize 100, MetadataSize 300: Live 100, Waste null, Data = 100 + 0 = 100. Metadata share 300 / 100 = 3.
    const s = stack({ fileSize: 100, metadataSize: 300 });
    expect(s.data).toBe(100);
    expect(component(s, "metadata").share).toBe(3);
    expect(component(s, "live").share).toBe(1);
  });
});

// The files of section 10.3. Table 1 keeps five files with a size, one without, and one with a negative size, which is
// absent; its FileSizes and VOrderedFileCount differ from the files, so that the form from the files shows. Table 2
// keeps four files, none with IsVOrdered. Tables 3 to 6 keep no file.
const FILE_TABLES = [
  {
    id: 1, name: "five_files", detailDepth: "Files", fileCount: 5, fileSize: 150, vOrderedFileCount: 5,
    fileSizes: { count: 99, sum: 9999, minimum: 1, firstQuartile: 2, median: 3, thirdQuartile: 4, maximum: 5 },
  },
  { id: 2, name: "four_files", detailDepth: "Files", fileCount: 4, fileSize: 1000, vOrderedFileCount: 3 },
  {
    id: 3, name: "totals", detailDepth: "Summary", fileCount: 12, fileSize: 90, vOrderedFileCount: 15,
    fileSizes: { minimum: 5, firstQuartile: 6, median: 7, thirdQuartile: 8, maximum: 9 },
  },
  { id: 4, name: "own_count", fileCount: 99, fileSize: 999, fileSizes: { count: 3, sum: 60, median: 20 } },
  { id: 5, name: "zero", fileCount: 0, fileSize: 0, vOrderedFileCount: 0 },
  { id: 6, name: "negative", fileCount: -3, fileSize: -10, vOrderedFileCount: 1, fileSizes: { count: -1, sum: -2, median: -5 } },
];

const FILE_ROWS = (
  [
    [1, 50, true], [1, 10, false], [1, 40, true], [1, 20, null], [1, 30, false], [1, null, null], [1, -7, null],
    [2, 300, null], [2, 100, null], [2, 400, null], [2, 200, null],
  ] as const
).map(([table, fileSize, isVOrdered], i) => ({ id: i + 1, table, fileSize, isVOrdered }));

describe("10.3 the file size figures and the V-Order shares", () => {
  const model = modelFrom({ summary: { tables: columnar(FILE_TABLES) }, files: columnar(FILE_ROWS) });
  const figures = (t: TableModel) => {
    const f = t.files;
    return [f.count, f.sum, f.minimum, f.firstQuartile, f.median, f.thirdQuartile, f.maximum];
  };

  it("10.3 from the files: the sizes sorted, the quartiles at the positions floor(n/4), floor(n/2), floor(3n/4)", () => {
    // Table 1: the sizes 10, 20, 30, 40, 50 (the file without a size and the one of -7 are left out). n = 5:
    // positions 1, 2, 3. Count 5, sum 150, and not the 99 and 9999 of FileSizes.
    expect(figures(model.tables[0]!)).toEqual([5, 150, 10, 20, 30, 40, 50]);
    // Table 2: 100, 200, 300, 400. n = 4: positions 1, 2, 3, so 200, 300, 400, not the interpolated 175, 250, 325.
    expect(figures(model.tables[1]!)).toEqual([4, 1000, 100, 200, 300, 400, 400]);
  });

  it("10.3 from FileSizes, FileCount standing for a missing Count and FileSize for a missing Sum", () => {
    // Table 3: no Count and no Sum in FileSizes: FileCount 12 and FileSize 90.
    expect(figures(model.tables[2]!)).toEqual([12, 90, 5, 6, 7, 8, 9]);
    // Table 4: Count 3 and Sum 60 of FileSizes, not FileCount 99 and FileSize 999.
    expect(figures(model.tables[3]!)).toEqual([3, 60, null, null, 20, null, null]);
    // Table 5: no FileSizes: FileCount 0 and FileSize 0 are values, the shape is null.
    expect(figures(model.tables[4]!)).toEqual([0, 0, null, null, null, null, null]);
    // Table 6: every value negative, so absent.
    expect(figures(model.tables[5]!)).toEqual([null, null, null, null, null, null, null]);
  });

  it("10.3 VOrderShare from the files when one has IsVOrdered, else min(1, VOrderedFileCount / FileCount)", () => {
    const vOrder = model.tables.map((t) => (t.files.vOrder === null ? null : [t.files.vOrder.share, t.files.vOrder.bytesShare]));
    expect(vOrder).toEqual([
      // Table 1: IsVOrdered on 4 files, true on 2: 2 / 4. The bytes: (50 + 40) / (50 + 10 + 40 + 30). Not 5 / 5.
      [0.5, 90 / 130],
      // Table 2: no file has IsVOrdered: 3 / 4 from the totals, and VOrderBytesShare null.
      [0.75, null],
      // Table 3: min(1, 15 / 12) = 1.
      [1, null],
      // Table 4: no VOrderedFileCount. Table 5: FileCount 0. Table 6: FileCount negative, so absent.
      null, null, null,
    ]);
  });

  it("10.3 a file with IsVOrdered and no FileSize counts in VOrderShare and adds no bytes", () => {
    // IsVOrdered is present on the three files, true on two: 2 / 3. The bytes: 100 / (100 + 300).
    const m = modelFrom({
      summary: { tables: columnar([{ id: 1, name: "t", detailDepth: "Files", fileCount: 3 }]) },
      files: columnar([
        { id: 1, table: 1, fileSize: 100, isVOrdered: true },
        { id: 2, table: 1, fileSize: null, isVOrdered: true },
        { id: 3, table: 1, fileSize: 300, isVOrdered: false },
      ]),
    });
    expect(m.tables[0]!.files.vOrder).toMatchObject({ share: 2 / 3, bytesShare: 0.25 });
    expect([m.tables[0]!.files.count, m.tables[0]!.files.sum]).toEqual([2, 400]);
  });
});

// The operations of section 10.4.
const HISTORY_TABLES = [
  { id: 1, name: "maintained" }, { id: 2, name: "written" }, { id: 3, name: "no_operation" },
  { id: 4, name: "unnamed_only" }, { id: 5, name: "vacuum_untimed" },
];

const HISTORY_OPERATIONS = (
  [
    [1, "WRITE", "2026-09-03T00:00:00.000Z"],
    [1, "VACUUM START", "2026-09-05T00:00:00.000Z"],
    [1, "vacuum end", "2026-09-07T00:00:00.000Z"],
    [1, "VACUUM END", "2026-09-06T00:00:00.000Z"],
    [1, "Optimize", "2026-09-04T00:00:00.000Z"],
    [1, "OPTIMIZE", null],
    [2, "WRITE", "2026-09-02T00:00:00.000Z"],
    [2, "MERGE", "2026-09-03T00:00:00.000Z"],
    [4, null, "2026-09-06T00:00:00.000Z"],
    [5, "VACUUM END", null],
  ] as const
).map(([table, name, lastAt]) => ({ table, name, commitCount: 1, lastAt }));

describe("10.4 HasVacuum, LastVacuumAt, HasOptimize, LastOptimizeAt", () => {
  it("10.4 the latest LastAt of the matching operations, in any case; null without an Operation; false when none matches", () => {
    const model = modelFrom({ summary: { tables: columnar(HISTORY_TABLES), operations: columnar(HISTORY_OPERATIONS) } });
    const m = model.tables.map((t) => t.maintenance);
    const of = (i: number) => [m[i]!.hasVacuum, m[i]!.lastVacuumAt, m[i]!.hasOptimize, m[i]!.lastOptimizeAt];
    // The latest of 09-05, 09-07 ("vacuum end", lower case), and 09-06, whatever their order. OPTIMIZE without a
    // LastAt counts for HasOptimize and leaves LastOptimizeAt at the 09-04 of "Optimize".
    expect(of(0)).toEqual([true, "2026-09-07T00:00:00.000Z", true, "2026-09-04T00:00:00.000Z"]);
    // Operations, none of them a VACUUM or an OPTIMIZE.
    expect(of(1)).toEqual([false, null, false, null]);
    // No Operation; an operation without a Name.
    expect(of(2)).toEqual([null, null, null, null]);
    expect(of(3)).toEqual([null, null, null, null]);
    // A VACUUM without a LastAt: true, and no time.
    expect(of(4)).toEqual([true, null, false, null]);
  });
});

// The partitions of section 10.5. The row of table 99 belongs to no table of the file.
const PARTITION_TABLES = [
  { id: 1, name: "counted_and_kept", partitionCount: 10 }, { id: 2, name: "kept_only" },
  { id: 3, name: "counted_only", partitionCount: 4 }, { id: 4, name: "neither" },
  { id: 5, name: "negative_count", partitionCount: -1 }, { id: 6, name: "zero_count", partitionCount: 0 },
];

const PARTITION_ROWS = (
  [
    [1, "2026-09-05T00:00:00.000Z"], [1, "2026-09-01T00:00:00.000Z"], [1, "2026-09-03T00:00:00.000Z"],
    [2, "2026-09-02T00:00:00.000Z"], [2, null], [5, null], [99, "2026-01-01T00:00:00.000Z"],
  ] as const
).map(([table, lastModifiedAt], i) => ({ id: i + 1, table, lastModifiedAt }));

describe("10.5 Partitions, PartitionsKept, PartitionsOldestModifiedAt, PartitionsNewestModifiedAt", () => {
  it("10.5 PartitionCount, else the partitions kept; null when neither; kept 0 without rows", () => {
    const model = modelFrom({ summary: { tables: columnar(PARTITION_TABLES) }, partitions: columnar(PARTITION_ROWS) });
    expect(model.tables.map((t) => t.partitions)).toEqual([
      // PartitionCount 10, three rows kept, from 09-01 to 09-05.
      { count: 10, kept: 3, oldestModifiedAt: "2026-09-01T00:00:00.000Z", newestModifiedAt: "2026-09-05T00:00:00.000Z" },
      // No PartitionCount: the two rows kept. One has no LastModifiedAt.
      { count: 2, kept: 2, oldestModifiedAt: "2026-09-02T00:00:00.000Z", newestModifiedAt: "2026-09-02T00:00:00.000Z" },
      { count: 4, kept: 0, oldestModifiedAt: null, newestModifiedAt: null },
      { count: null, kept: 0, oldestModifiedAt: null, newestModifiedAt: null },
      // A negative PartitionCount is absent: the one row kept.
      { count: 1, kept: 1, oldestModifiedAt: null, newestModifiedAt: null },
      // A PartitionCount of 0 is a value.
      { count: 0, kept: 0, oldestModifiedAt: null, newestModifiedAt: null },
    ]);
  });
});

// The columns of section 10.6. Table 1 read 4 footers, table 2 has no FooterCount, table 3 read none.
const COLUMN_TABLES = [{ id: 1, name: "a", footerCount: 4 }, { id: 2, name: "b" }, { id: 3, name: "c", footerCount: 0 }];

const COLUMN_ROWS = [
  {
    id: 1, table: 1, name: "full", valueCount: 100, nullCount: 25, compressedSize: 400, uncompressedSize: 1600, chunkCount: 8,
    dictionaryChunkCount: 6, fallbackChunkCount: 3, unencodedByteArrayDataBytes: 600, fileCountWithoutColumn: 1,
  },
  { id: 2, table: 1, name: "more_nulls", valueCount: 10, nullCount: 15, fileCountWithoutColumn: 0 },
  {
    id: 3, table: 1, name: "all_null", valueCount: 20, nullCount: 20, unencodedByteArrayDataBytes: 0, chunkCount: 0,
    dictionaryChunkCount: 0, fallbackChunkCount: 0, compressedSize: 0, uncompressedSize: 10,
  },
  { id: 4, table: 2, name: "no_footer_count", valueCount: 0, nullCount: 0, fileCountWithoutColumn: 1 },
  { id: 5, table: 3, name: "zero_footers", fileCountWithoutColumn: 0 },
  { id: 6, table: 99, name: "orphan", valueCount: 10, nullCount: 1, fileCountWithoutColumn: 1 },
  {
    id: 7, table: 1, name: "negative", valueCount: -10, nullCount: 2, compressedSize: -1, uncompressedSize: 5, chunkCount: 4,
    dictionaryChunkCount: -2, fileCountWithoutColumn: -1,
  },
  { id: 8, table: 1 },
  { id: 1, table: 1 },
];

describe("10.6 the measures of a column", () => {
  it("10.6 NullShare, CompressionRatio, DictionaryShare, DictionaryFallbackShare, AverageValueLength, FilesWithoutColumnShare", () => {
    const model = modelFrom({ summary: { tables: columnar(COLUMN_TABLES), columns: columnar(COLUMN_ROWS) } });
    const rows = model.columns.map((c) => [
      c.id, c.tableKey, c.label, c.nullShare, c.compressionRatio, c.dictionaryShare, c.dictionaryFallbackShare,
      c.averageValueLength, c.filesWithoutColumnShare,
    ]);
    expect(rows).toEqual([
      // 25 / 100, 1600 / 400, 6 / 8, 3 / 6, 600 / (100 - 25), 1 / 4.
      [1, "t:1", "full", 0.25, 4, 0.75, 0.5, 8, 0.25],
      // min(1, 15 / 10) = 1; 0 / 4 = 0.
      [2, "t:1", "more_nulls", 1, null, null, null, null, 0],
      // 20 / 20 = 1. CompressedSize 0, ChunkCount 0, DictionaryChunkCount 0, ValueCount - NullCount 0: null.
      [3, "t:1", "all_null", 1, null, null, null, null, null],
      // ValueCount 0: null. Its table has no FooterCount: null.
      [4, "t:2", "no_footer_count", null, null, null, null, null, null],
      // FooterCount 0: null.
      [5, "t:3", "zero_footers", null, null, null, null, null, null],
      // A column of no table of the file: 1 / 10, and no FooterCount to divide by.
      [6, "t:none", "orphan", 0.1, null, null, null, null, null],
      // Every negative value is absent.
      [7, "t:1", "negative", null, null, null, null, null, null],
      // No field: a partition column that the files do not store. The label names the id, then the row.
      [8, "t:1", "Column 8", null, null, null, null, null, null],
      [null, "t:1", "Column at row 9", null, null, null, null, null, null],
    ]);
  });
});

// The collection of section 10.7. Sources 1, 2, 2 again, and 0, which is no id.
// t1: Live 1000 - 0 - 50 - 0 = 950, Waste 100, Overhead 30 + 50 = 80, Total 1130. t2: Live 500, Total 500, Waste and
// Overhead null. t3: nothing, no DetailDepth. t4: FileCount and FileSize negative so absent, Live null, Overhead 20,
// Total 20, Waste null.
const COLLECTION_SUMMARY = {
  sources: columnar([{ id: 1, kind: "Lakehouse" }, { id: 2, kind: "Lakehouse" }, { id: 2, kind: "Lakehouse" }, { id: 0, kind: "Lakehouse" }]),
  tables: columnar([
    {
      id: 1, source: 1, name: "t1", detailDepth: "Files", fileCount: 3, fileSize: 1000, rowCount: 10, deletedRowCount: 0,
      footerSize: 50, staleColumnSize: 0, nonLiveFileSize: 100, metadataSize: 30,
    },
    { id: 2, source: 2, name: "t2", detailDepth: "Summary", fileCount: 2, fileSize: 500 },
    { id: 3, source: 1, name: "t3" },
    { id: 4, source: 2, name: "t4", detailDepth: "Summary", fileCount: -1, fileSize: -5, rowCount: 7, metadataSize: 20 },
  ]),
  columns: columnar([{ id: 1, table: 1, name: "x" }, { id: 2, table: 2, name: "y" }, { id: 3, table: 4, name: "z" }]),
};

describe("10.7 the measures of the collection", () => {
  it("10.7 Sources counts the distinct ids, the sums add the tables that have the value, Depths counts Unknown", () => {
    const model = modelFrom({ summary: COLLECTION_SUMMARY });
    expect(model.collection.totals).toEqual({
      sources: 2, tables: 4, columns: 3,
      // 3 + 2; 1000 + 500; 10 + 7.
      files: 5, fileSize: 1500, rowCount: 17,
      // 1130 + 500 + 20; 100; 80 + 20.
      footprint: 1650, waste: 100, overhead: 100,
    });
    expect(model.collection.depths).toEqual({ Files: 1, Summary: 2, Unknown: 1 });
  });

  it("10.7 every sum is null when no table has the value, and the counts are 0 without rows", () => {
    const bare = modelFrom({ summary: { tables: columnar([{ id: 1, name: "bare" }]) } });
    expect(bare.collection.totals).toEqual({
      sources: 0, tables: 1, columns: 0, files: null, fileSize: null, rowCount: null, footprint: null, waste: null, overhead: null,
    });
    expect(bare.collection.depths).toEqual({ Unknown: 1 });
    const empty = modelFrom({ summary: {} });
    expect(empty.collection.totals).toMatchObject({ sources: 0, tables: 0, columns: 0, files: null });
    expect(empty.collection.depths).toEqual({});
  });
});
