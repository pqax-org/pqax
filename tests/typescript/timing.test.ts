// The time that readTable takes on a detail part of 250,000 rows, the most that a part may hold (section 7.2 of the
// serialization). The part is a columnChunks table with the value types of a real footprint: integers, strings, lists
// of strings, booleans, and nulls, about 22 MB of JSON. The test writes it to a stored archive and, when the platform
// has CompressionStream("deflate-raw"), to a deflated one, reads it three times from each, and prints the times. It
// asserts only that every read takes less than 10 seconds. The README of the package reports the times.
import { describe, expect, it } from "vitest";
import { openPqax } from "pqax";
import { toBytes } from "./kit";
import { canDeflate, writeZip } from "./zip";

const ROWS = 250_000;
const COLUMNS = 25;
const GROUPS = ROWS / COLUMNS;
const RUNS = 3;
const TEXT = ["PLAIN", "RLE", "RLE_DICTIONARY"];
const NUMBER = ["PLAIN", "RLE"];

/** The entries of the file, and the sum of the compressed sizes of its chunks, to check what the reader returns. */
function footprint(): { entries: [string, Uint8Array][]; partSize: number; sizeSum: number } {
  const chunks: Record<string, unknown[]> = {
    rowGroup: [], physicalColumn: [], codec: [], encodings: [], valueCount: [], nullCount: [], compressedSize: [],
    uncompressedSize: [], hasDictionaryPage: [], dictionaryPageSize: [], dataPageCount: [],
    dictionaryEncodedPageCount: [], unencodedByteArrayDataBytes: [], dictionaryEntryCount: [],
  };
  const add = (field: string, value: unknown) => chunks[field]!.push(value);
  let sizeSum = 0;
  for (let i = 0; i < ROWS; i++) {
    const group = Math.floor(i / COLUMNS);
    const column = i % COLUMNS;
    const text = column % 5 === 0;
    const size = 2_000 + ((i * 7919) % 4_000_000);
    const pages = 1 + (i % 12);
    sizeSum += size;
    add("rowGroup", group + 1);
    add("physicalColumn", column + 1);
    add("codec", group % 10 === 0 ? "SNAPPY" : "ZSTD");
    add("encodings", text ? TEXT : NUMBER);
    add("valueCount", 1_048_576);
    add("nullCount", i % 7 === 0 ? null : (i * 31) % 5_000);
    add("compressedSize", size);
    add("uncompressedSize", size * (2 + (column % 3)));
    add("hasDictionaryPage", text);
    add("dictionaryPageSize", text ? 1_000 + (i % 50_000) : null);
    add("dataPageCount", pages);
    add("dictionaryEncodedPageCount", text ? pages : 0);
    add("unencodedByteArrayDataBytes", text ? size * 3 : null);
    add("dictionaryEntryCount", text ? 1 + (i % 20_000) : null);
  }
  const range = (count: number, map: (index: number) => unknown) => Array.from({ length: count }, (_, index) => map(index));
  const summary = {
    sources: { id: [1], kind: ["Storage folder"] },
    tables: { id: [1], source: [1], name: ["large"], format: ["Delta"], detailDepth: ["Column chunks"], fileCount: [1_000],
      rowGroupCount: [GROUPS], compressedSize: [sizeSum] },
    columns: { id: range(COLUMNS, (i) => i + 1), table: range(COLUMNS, () => 1), name: range(COLUMNS, (i) => `c${i + 1}`),
      dataType: range(COLUMNS, (i) => (i % 5 === 0 ? "string" : "long")) },
    physicalSchemas: { id: [1], table: [1], fileCount: [1_000] },
    physicalColumns: { id: range(COLUMNS, (i) => i + 1), physicalSchema: range(COLUMNS, () => 1),
      ordinal: range(COLUMNS, (i) => i), path: range(COLUMNS, (i) => [`c${i + 1}`]),
      physicalType: range(COLUMNS, (i) => (i % 5 === 0 ? "BYTE_ARRAY" : "INT64")), column: range(COLUMNS, (i) => i + 1) },
  };
  const manifest = {
    format: "pqax", formatVersion: "0.1", collection: { collectorApp: "pqax-tests", level: 2 },
    parts: [
      { name: "summary.json", tables: { sources: 1, tables: 1, columns: COLUMNS, physicalSchemas: 1, physicalColumns: COLUMNS } },
      { name: "columnChunks.json", tables: { columnChunks: ROWS } },
    ],
  };
  const part = toBytes({ columnChunks: chunks });
  return {
    entries: [["manifest.json", toBytes(manifest)], ["summary.json", toBytes(summary)], ["columnChunks.json", part]],
    partSize: part.length,
    sizeSum,
  };
}

let built: ReturnType<typeof footprint> | undefined;

function platform(): string {
  const node = (globalThis as { process?: { versions?: { node?: string } } }).process?.versions?.node;
  return node === undefined ? navigator.userAgent : `Node ${node}`;
}

async function timeReads(form: string, method: 0 | 8): Promise<void> {
  built ??= footprint();
  const { entries, partSize, sizeSum } = built;
  const bytes = await writeZip(entries, method);
  const times: number[] = [];
  for (let run = 0; run < RUNS; run++) {
    const doc = await openPqax(bytes);
    const start = performance.now();
    const chunks = await doc.readTable("columnChunks");
    times.push(performance.now() - start);
    expect(chunks.length).toBe(ROWS);
    expect(chunks.compressedSize!.reduce<number>((sum, size) => sum + (size ?? 0), 0)).toBe(sizeSum);
    expect(chunks.rowGroup![ROWS - 1]).toBe(GROUPS);
    expect([chunks.nullCount![0], chunks.nullCount![1]]).toEqual([null, 31]);
    expect([chunks.encodings![0], chunks.hasDictionaryPage![1], chunks.dictionaryPageSize![1]]).toEqual([TEXT, false, null]);
  }
  const mb = (count: number) => `${(count / 1_000_000).toFixed(1)} MB`;
  console.log(`readTable on ${ROWS.toLocaleString("en-US")} column chunks, ${form} (part ${mb(partSize)}, archive ` +
    `${mb(bytes.length)}): ${times.map((time) => `${Math.round(time)} ms`).join(", ")}; ${platform()}`);
  for (const time of times) {
    expect(time).toBeLessThan(10_000);
  }
}

describe("a detail part of 250,000 rows", () => {
  it("is read in less than 10 seconds from a stored archive", { timeout: 120_000 }, async () => {
    await timeReads("stored", 0);
  });

  it.skipIf(!canDeflate)("is read in less than 10 seconds from a deflated archive", { timeout: 120_000 }, async () => {
    await timeReads("deflated", 8);
  });
});
