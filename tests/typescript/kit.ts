// What the TypeScript tests share: where the conformance kit is, what it expects, the numbers of the expected shape,
// a ByteSource that counts its reads, and a small zip reader and writer to build the cases that the kit has no file for.
// The numbers are computed here from what the reader returns, as conformance/README.md defines them, so that the
// tests check the package and not a copy of its code. Nothing here imports the package.
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { crc32, deflateRawSync, inflateRawSync } from "node:zlib";

function findRoot(start: string): string {
  let folder = start;
  for (;;) {
    if (existsSync(join(folder, "conformance")) && statSync(join(folder, "conformance")).isDirectory() &&
        existsSync(join(folder, "Pqax.slnx"))) {
      return folder;
    }
    const parent = dirname(folder);
    if (parent === folder) {
      throw new Error(`No folder with conformance/ and Pqax.slnx above ${start}`);
    }
    folder = parent;
  }
}

export const ROOT = findRoot(dirname(fileURLToPath(import.meta.url)));
export const KIT = join(ROOT, "conformance");
export const SAMPLES = join(KIT, "samples");
export const INVALID = join(SAMPLES, "invalid");
export const EXPECTED = join(KIT, "expected");
export const EMBEDDED_PART = "Footprint.pqax";

export const SUMMARY_TABLES = ["sources", "tables", "columns", "partitionSpecs", "partitionFields", "sortOrders",
  "sortFields", "physicalSchemas", "physicalColumns", "writers", "operations", "issues", "modelBindings",
  "tableBindings", "columnBindings"] as const;
export const DETAIL_TABLES = ["partitions", "files", "rowGroups", "columnChunks", "deleteFiles", "commits"] as const;
export const FORMAT_TABLES: readonly string[] = [...SUMMARY_TABLES, ...DETAIL_TABLES];

/** The three invalid samples that a reader refuses when it opens them, with the code of the refusal. */
export const REFUSED: Readonly<Record<string, string>> = {
  "container.manifestMissing.pqax": "container.manifestMissing",
  "manifest.format.pqax": "manifest.format",
  "manifest.versionUnknown.pqax": "manifest.versionUnknown",
};

/** The codes of section 4.1 of the contracts. */
export const PLAN_CODES: ReadonlySet<string> = new Set([
  "container.method", "container.encrypted", "container.zip64", "container.partName", "container.caseCollision",
  "container.manifestMissing", "json.invalid", "json.topLevel", "json.bom", "json.duplicateMember", "json.integer",
  "json.integerRange", "json.timestamp", "json.type", "manifest.required", "manifest.format", "manifest.formatVersion",
  "manifest.versionUnknown", "manifest.firstPart", "manifest.duplicatePart", "manifest.partMissing", "part.tables",
  "part.kind", "part.singleTable", "part.tooLarge", "part.rowCount", "table.required", "table.idUnique",
  "table.reference", "depth.files", "depth.rowGroups", "depth.columnChunks", "totals.fileCount", "totals.fileSize",
  "totals.rowGroupCount", "totals.compressedSize", "totals.columnCompressedSize", "embedded.partMissing",
]);

export interface ExpectedTable {
  id: number;
  fileCount: number | null;
  columnChunkCount: number;
  columnChunkCompressedSize: number;
}

export interface Shape {
  format: unknown;
  formatVersion: unknown;
  parts: unknown[];
  rows: Record<string, number>;
  tablesFileSize: number;
  tablesRowCount: number;
  columnChunksCompressedSize: number;
  tables: ExpectedTable[];
  largestColumns: { id: number; compressedSize: number }[];
}

export interface Expected extends Shape {
  file: string;
  embeddedPart: string | null;
  embeddedCompression: "stored" | "deflated" | null;
}

export interface InvalidCase {
  codes: string[];
  change: string;
}

export function readJson<T>(path: string): T {
  return JSON.parse(readFileSync(path, "utf8")) as T;
}

/** Every expected file, by its name without .json, such as "sample". */
export function expectedByName(): Record<string, Expected> {
  const result: Record<string, Expected> = {};
  for (const file of readdirSync(EXPECTED).filter((name) => name.endsWith(".json")).sort()) {
    result[file.slice(0, -5)] = readJson<Expected>(join(EXPECTED, file));
  }
  return result;
}

export function invalidCases(): Record<string, InvalidCase> {
  return readJson<Record<string, InvalidCase>>(join(INVALID, "expected.json"));
}

/** The bytes of a file as a plain Uint8Array that starts at offset 0 of its own buffer. */
export function bytesOf(path: string): Uint8Array<ArrayBuffer> {
  return new Uint8Array(readFileSync(path));
}

/** The keys of a columnar table that hold values: every member that is an array. */
export function fieldsOf(table: unknown): Record<string, unknown[]> {
  const result: Record<string, unknown[]> = {};
  if (table && typeof table === "object") {
    for (const [name, values] of Object.entries(table as Record<string, unknown>)) {
      if (Array.isArray(values)) {
        result[name] = values;
      }
    }
  }
  return result;
}

/** For every table that the manifest states, the sum of its rows over the parts. */
export function statedRows(manifest: { parts: readonly { tables: Readonly<Record<string, number>> }[] }): Record<string, number> {
  const rows: Record<string, number> = {};
  for (const part of manifest.parts) {
    for (const [name, count] of Object.entries(part.tables)) {
      rows[name] = (rows[name] ?? 0) + count;
    }
  }
  return rows;
}

type Columns = Record<string, unknown[]>;

function field(table: Columns, name: string): unknown[] {
  const values = table[name];
  if (values) {
    return values;
  }
  const length = Math.max(0, ...Object.values(table).map((array) => array.length));
  return new Array(length).fill(null);
}

function total(values: unknown[]): number {
  return values.reduce<number>((sum, value) => sum + (typeof value === "number" ? value : 0), 0);
}

/**
 * The numbers of an expected file, computed as conformance/README.md says, from the four tables that the reader
 * returned and from its manifest and row counts.
 */
export function computeShape(manifest: { format?: unknown; formatVersion?: unknown; parts: readonly { name: string; tables: Readonly<Record<string, number>> }[] },
  rowCount: (name: string) => number, tables: { tables: unknown; columnChunks: unknown; physicalColumns: unknown; physicalSchemas: unknown }): Shape {
  const summaryTables = fieldsOf(tables.tables);
  const chunks = fieldsOf(tables.columnChunks);
  const physicalColumns = fieldsOf(tables.physicalColumns);
  const physicalSchemas = fieldsOf(tables.physicalSchemas);
  const schemaTable = new Map<unknown, unknown>();
  field(physicalSchemas, "id").forEach((id, i) => schemaTable.set(id, field(physicalSchemas, "table")[i]));
  const columnSchema = new Map<unknown, unknown>();
  const columnColumn = new Map<unknown, unknown>();
  field(physicalColumns, "id").forEach((id, i) => {
    columnSchema.set(id, field(physicalColumns, "physicalSchema")[i]);
    columnColumn.set(id, field(physicalColumns, "column")[i]);
  });
  const count = new Map<unknown, number>();
  const size = new Map<unknown, number>();
  const byColumn = new Map<number, number>();
  const sizes = field(chunks, "compressedSize");
  field(chunks, "physicalColumn").forEach((physical, i) => {
    const compressed = typeof sizes[i] === "number" ? (sizes[i] as number) : 0;
    const schema = columnSchema.get(physical);
    const owner = schema === undefined || schema === null ? undefined : schemaTable.get(schema);
    if (owner !== undefined && owner !== null) {
      count.set(owner, (count.get(owner) ?? 0) + 1);
      size.set(owner, (size.get(owner) ?? 0) + compressed);
    }
    const column = columnColumn.get(physical);
    if (typeof column === "number") {
      byColumn.set(column, (byColumn.get(column) ?? 0) + compressed);
    }
  });
  const largest = [...byColumn.entries()].sort((a, b) => b[1] - a[1] || a[0] - b[0]).slice(0, 3);
  const rows: Record<string, number> = {};
  for (const name of Object.keys(statedRows(manifest))) {
    rows[name] = rowCount(name);
  }
  const fileCounts = field(summaryTables, "fileCount");
  return {
    format: manifest.format,
    formatVersion: manifest.formatVersion,
    parts: manifest.parts.map((part) => part.name),
    rows,
    tablesFileSize: total(field(summaryTables, "fileSize")),
    tablesRowCount: total(field(summaryTables, "rowCount")),
    columnChunksCompressedSize: total(sizes),
    tables: field(summaryTables, "id").map((id, i) => ({
      id: id as number,
      fileCount: (fileCounts[i] ?? null) as number | null,
      columnChunkCount: count.get(id) ?? 0,
      columnChunkCompressedSize: size.get(id) ?? 0,
    })),
    largestColumns: largest.map(([id, compressedSize]) => ({ id, compressedSize })),
  };
}

/** The keys of an expected file that a reader computes. */
export function shapeOf(expected: Expected): Shape {
  const { file: _file, embeddedPart: _part, embeddedCompression: _compression, ...shape } = expected;
  return shape;
}

// ---- a ByteSource that counts its reads ----

export class CountingSource {
  readonly ranges: [number, number][] = [];

  constructor(private readonly bytes: Uint8Array) {}

  get size(): number {
    return this.bytes.length;
  }

  async read(offset: number, length: number): Promise<Uint8Array> {
    if (offset < 0 || length < 0 || offset + length > this.bytes.length) {
      throw new RangeError(`read of ${length} bytes at ${offset} outside ${this.bytes.length}`);
    }
    this.ranges.push([offset, offset + length]);
    return this.bytes.slice(offset, offset + length);
  }

  get bytesRead(): number {
    return this.ranges.reduce((sum, [start, stop]) => sum + stop - start, 0);
  }
}

// ---- a small zip reader and writer ----

export interface ZipEntryInfo {
  name: string;
  method: number;
  flags: number;
  compressedSize: number;
  size: number;
  offset: number;
}

const decoder = new TextDecoder();
const encoder = new TextEncoder();

/** The entries of the central directory, and where the directory starts. */
export function centralDirectory(bytes: Uint8Array): { entries: ZipEntryInfo[]; directoryOffset: number } {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let end = -1;
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 22 - 65535); i--) {
    if (view.getUint32(i, true) === 0x06054b50) {
      end = i;
      break;
    }
  }
  if (end < 0) {
    throw new Error("not a zip archive");
  }
  const count = view.getUint16(end + 10, true);
  const directoryOffset = view.getUint32(end + 16, true);
  const entries: ZipEntryInfo[] = [];
  let p = directoryOffset;
  for (let i = 0; i < count; i++) {
    if (view.getUint32(p, true) !== 0x02014b50) {
      throw new Error("bad central directory");
    }
    const nameLength = view.getUint16(p + 28, true);
    const extraLength = view.getUint16(p + 30, true);
    const commentLength = view.getUint16(p + 32, true);
    entries.push({
      name: decoder.decode(bytes.subarray(p + 46, p + 46 + nameLength)),
      flags: view.getUint16(p + 8, true),
      method: view.getUint16(p + 10, true),
      compressedSize: view.getUint32(p + 20, true),
      size: view.getUint32(p + 24, true),
      offset: view.getUint32(p + 42, true),
    });
    p += 46 + nameLength + extraLength + commentLength;
  }
  return { entries, directoryOffset };
}

/** Where the data of an entry starts: after its local header. */
export function dataOffset(bytes: Uint8Array, entry: ZipEntryInfo): number {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  return entry.offset + 30 + view.getUint16(entry.offset + 26, true) + view.getUint16(entry.offset + 28, true);
}

/** The content of every entry, stored or deflated, by name. */
export function readEntries(bytes: Uint8Array): Map<string, Uint8Array> {
  const result = new Map<string, Uint8Array>();
  for (const entry of centralDirectory(bytes).entries) {
    const start = dataOffset(bytes, entry);
    const raw = bytes.subarray(start, start + entry.compressedSize);
    result.set(entry.name, entry.method === 0 ? raw.slice() : new Uint8Array(inflateRawSync(raw)));
  }
  return result;
}

/** For each entry, the bytes from its local header to the next header or to the central directory. */
export function entrySpans(bytes: Uint8Array, base = 0): Map<string, [number, number]> {
  const { entries, directoryOffset } = centralDirectory(bytes);
  const sorted = [...entries].sort((a, b) => a.offset - b.offset);
  const spans = new Map<string, [number, number]>();
  sorted.forEach((entry, i) => {
    const stop = i + 1 < sorted.length ? sorted[i + 1]!.offset : directoryOffset;
    spans.set(entry.name, [base + entry.offset, base + stop]);
  });
  return spans;
}

/** The entries whose bytes overlap one of the ranges read. */
export function touchedEntries(spans: Map<string, [number, number]>, ranges: readonly [number, number][]): Set<string> {
  const touched = new Set<string>();
  for (const [name, [low, high]] of spans) {
    if (ranges.some(([start, stop]) => start < high && low < stop)) {
      touched.add(name);
    }
  }
  return touched;
}

/** A zip archive with these entries, in this order, deflated or stored, all dated 2026-10-07. */
export function writeZip(entries: readonly [string, Uint8Array | string][], method: 0 | 8 = 8): Uint8Array {
  const chunks: Uint8Array[] = [];
  const directory: Uint8Array[] = [];
  let offset = 0;
  const date = ((2026 - 1980) << 9) | (10 << 5) | 7;
  for (const [name, content] of entries) {
    const data = typeof content === "string" ? encoder.encode(content) : content;
    const stored = method === 0 ? data : new Uint8Array(deflateRawSync(data));
    const nameBytes = encoder.encode(name);
    const crc = crc32(data) >>> 0;
    const local = new Uint8Array(30 + nameBytes.length);
    const lv = new DataView(local.buffer);
    lv.setUint32(0, 0x04034b50, true);
    lv.setUint16(4, 20, true);
    lv.setUint16(6, 0, true);
    lv.setUint16(8, method, true);
    lv.setUint16(10, 0, true);
    lv.setUint16(12, date, true);
    lv.setUint32(14, crc, true);
    lv.setUint32(18, stored.length, true);
    lv.setUint32(22, data.length, true);
    lv.setUint16(26, nameBytes.length, true);
    lv.setUint16(28, 0, true);
    local.set(nameBytes, 30);
    const central = new Uint8Array(46 + nameBytes.length);
    const cv = new DataView(central.buffer);
    cv.setUint32(0, 0x02014b50, true);
    cv.setUint16(4, 20, true);
    cv.setUint16(6, 20, true);
    cv.setUint16(8, 0, true);
    cv.setUint16(10, method, true);
    cv.setUint16(12, 0, true);
    cv.setUint16(14, date, true);
    cv.setUint32(16, crc, true);
    cv.setUint32(20, stored.length, true);
    cv.setUint32(24, data.length, true);
    cv.setUint16(28, nameBytes.length, true);
    cv.setUint32(42, offset, true);
    central.set(nameBytes, 46);
    chunks.push(local, stored);
    directory.push(central);
    offset += local.length + stored.length;
  }
  const directorySize = directory.reduce((sum, chunk) => sum + chunk.length, 0);
  const end = new Uint8Array(22);
  const ev = new DataView(end.buffer);
  ev.setUint32(0, 0x06054b50, true);
  ev.setUint16(8, entries.length, true);
  ev.setUint16(10, entries.length, true);
  ev.setUint32(12, directorySize, true);
  ev.setUint32(16, offset, true);
  const all = [...chunks, ...directory, end];
  const result = new Uint8Array(all.reduce((sum, chunk) => sum + chunk.length, 0));
  let position = 0;
  for (const chunk of all) {
    result.set(chunk, position);
    position += chunk.length;
  }
  return result;
}

/** A copy of a zip archive whose entries go through change: new content, null to drop the entry, undefined to keep it. */
export function rewriteZip(bytes: Uint8Array, change: (name: string, data: Uint8Array) => Uint8Array | string | null | undefined): Uint8Array {
  const entries: [string, Uint8Array | string][] = [];
  for (const [name, data] of readEntries(bytes)) {
    const replaced = change(name, data);
    if (replaced === null) {
      continue;
    }
    entries.push([name, replaced === undefined ? data : replaced]);
  }
  return writeZip(entries);
}

export function json(data: Uint8Array): any {
  return JSON.parse(decoder.decode(data));
}

export function toBytes(value: unknown): Uint8Array {
  return encoder.encode(JSON.stringify(value));
}
