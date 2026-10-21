// A footprint of 600,000 column chunks in three parts of 250,000, 250,000, and 100,000 rows, read back whole and
// validated. The package has no writer, so the file is written here with the zip writer of the tests. It takes a few
// seconds.
import { describe, expect, it } from "vitest";
import { openPqax, validate } from "@pqax/pqax";
import { CountingSource, toBytes } from "./kit";
import { writeZip } from "./zip";

const FILES = 100;
const GROUPS_PER_FILE = 100;
const PHYSICAL = 60;
const GROUPS = FILES * GROUPS_PER_FILE;
const CHUNKS = GROUPS * PHYSICAL;
const PART = 250_000;

function range(count: number, map: (index: number) => unknown = (index) => index + 1): unknown[] {
  return Array.from({ length: count }, (_, index) => map(index));
}

async function largeFootprint(): Promise<{ bytes: Uint8Array; sizes: number[] }> {
  const sizes = Array.from({ length: CHUNKS }, (_, index) => 1 + ((index * 7919) % 1000));
  const fileSizes = range(FILES, (index) => 1000 + index) as number[];
  const summary = {
    sources: { id: [1], kind: ["Storage folder"] },
    tables: {
      id: [1], source: [1], name: ["large"], format: ["Parquet"], detailDepth: ["Column chunks"], fileCount: [FILES],
      fileSize: [fileSizes.reduce((a, b) => a + b, 0)], rowGroupCount: [GROUPS],
      compressedSize: [sizes.reduce((a, b) => a + b, 0)],
    },
    columns: { id: range(PHYSICAL), table: range(PHYSICAL, () => 1), name: range(PHYSICAL, (i) => `c${i + 1}`),
      dataType: range(PHYSICAL, () => "long") },
    physicalSchemas: { id: [1], table: [1], fileCount: [FILES] },
    physicalColumns: { id: range(PHYSICAL), physicalSchema: range(PHYSICAL, () => 1), ordinal: range(PHYSICAL, (i) => i),
      path: range(PHYSICAL, (i) => [`c${i + 1}`]), physicalType: range(PHYSICAL, () => "INT64"), column: range(PHYSICAL) },
  };
  const files = { files: { id: range(FILES), table: range(FILES, () => 1), fileSize: fileSizes } };
  const rowGroups = { rowGroups: { id: range(GROUPS), file: range(GROUPS, (i) => 1 + Math.floor(i / GROUPS_PER_FILE)),
    ordinal: range(GROUPS, (i) => i % GROUPS_PER_FILE), rowCount: range(GROUPS, () => 1000) } };
  const entries: [string, Uint8Array][] = [];
  const parts: { name: string; tables: Record<string, number> }[] = [
    { name: "summary.json", tables: { sources: 1, tables: 1, columns: PHYSICAL, physicalSchemas: 1, physicalColumns: PHYSICAL } },
    { name: "files.json", tables: { files: FILES } },
    { name: "rowGroups.json", tables: { rowGroups: GROUPS } },
  ];
  const chunkParts: [string, Uint8Array][] = [];
  for (let start = 0, number = 1; start < CHUNKS; start += PART, number++) {
    const stop = Math.min(CHUNKS, start + PART);
    const name = `columnChunks.${number}.json`;
    parts.push({ name, tables: { columnChunks: stop - start } });
    const slice = sizes.slice(start, stop);
    chunkParts.push([name, toBytes({ columnChunks: {
      rowGroup: range(stop - start, (i) => 1 + Math.floor((start + i) / PHYSICAL)),
      physicalColumn: range(stop - start, (i) => 1 + ((start + i) % PHYSICAL)),
      compressedSize: slice,
      uncompressedSize: slice.map((size) => 2 * size),
    } })]);
  }
  const manifest = { format: "pqax", formatVersion: "0.1", collection: { collectorApp: "pqax-tests", level: 2 }, parts };
  entries.push(["manifest.json", toBytes(manifest)], ["summary.json", toBytes(summary)], ["files.json", toBytes(files)],
    ["rowGroups.json", toBytes(rowGroups)], ...chunkParts);
  return { bytes: await writeZip(entries), sizes };
}

describe("a footprint of 600,000 column chunks", () => {
  it("is read back whole from parts of 250,000 rows, and is valid", { timeout: 120_000 }, async () => {
    const { bytes, sizes } = await largeFootprint();
    const source = new CountingSource(bytes);
    const doc = await openPqax(source);
    expect(doc.manifest.parts.filter((part) => "columnChunks" in part.tables).map((part) => part.tables.columnChunks))
      .toEqual([250_000, 250_000, 100_000]);
    expect(doc.rowCount("columnChunks")).toBe(CHUNKS);
    expect(source.bytesRead).toBeLessThan(bytes.length / 4);
    const chunks = await doc.readTable("columnChunks");
    expect(chunks.length).toBe(CHUNKS);
    expect(chunks.compressedSize!.length).toBe(CHUNKS);
    let mismatches = 0;
    for (let i = 0; i < CHUNKS; i++) {
      if (chunks.compressedSize![i] !== sizes[i] || chunks.physicalColumn![i] !== 1 + (i % PHYSICAL) ||
          chunks.rowGroup![i] !== 1 + Math.floor(i / PHYSICAL)) {
        mismatches++;
      }
    }
    expect(mismatches).toBe(0);
    const result = await validate(bytes);
    expect(result.issues.slice(0, 5).map((issue) => `${issue.code}: ${issue.message}`)).toEqual([]);
    expect(result.isValid).toBe(true);
  });
});
