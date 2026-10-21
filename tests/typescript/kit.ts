// What the TypeScript tests share: the files of the conformance kit and what it expects, the numbers of the expected
// shape, a ByteSource that counts its reads, and a small zip reader to look into the files. The zip writer is in
// zip.ts. The numbers are computed here from what the reader returns, as conformance/README.md defines them, so that
// the tests check the package and not a copy of its code. Nothing here imports the package or a module of Node, so
// that the tests run in browsers too.
import { files, root } from "virtual:pqax-kit";
import { inflateRaw, writeZip } from "./zip";

const decoder = new TextDecoder();
const encoder = new TextEncoder();

// The paths of the kit are relative to conformance/, with forward slashes. The module "virtual:pqax-kit", which
// vitest.config.ts makes when the tests start, holds the files.
export const SAMPLES = "samples";
export const INVALID = "samples/invalid";
export const EXPECTED = "expected";
/** The folder of the kit on disk, for the tests of @pqax/pqax/node, which read files. */
export const KIT_DIR: string = root;
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

const kitBytes = new Map<string, Uint8Array<ArrayBuffer>>();

function kitFile(path: string): Uint8Array<ArrayBuffer> {
  let bytes = kitBytes.get(path);
  if (bytes === undefined) {
    const base64 = files[path];
    if (base64 === undefined) {
      throw new Error(`The conformance kit has no file ${path}`);
    }
    const binary = atob(base64);
    bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    kitBytes.set(path, bytes);
  }
  return bytes;
}

/** Joins the parts of a path of the kit. */
export function join(...parts: string[]): string {
  return parts.join("/");
}

/** The bytes of a file of the kit, as a new Uint8Array that starts at offset 0 of its own buffer. */
export function bytesOf(path: string): Uint8Array<ArrayBuffer> {
  return kitFile(path).slice();
}

/** The text of a file of the kit. */
export function textOf(path: string): string {
  return decoder.decode(kitFile(path));
}

export function readJson<T>(path: string): T {
  return JSON.parse(textOf(path)) as T;
}

/** Every expected file, by its name without .json, such as "sample". */
export function expectedByName(): Record<string, Expected> {
  const result: Record<string, Expected> = {};
  const folder = `${EXPECTED}/`;
  const names = Object.keys(files).filter((path) => path.startsWith(folder)).map((path) => path.slice(folder.length));
  for (const file of names.filter((name) => name.endsWith(".json") && !name.includes("/")).sort()) {
    result[file.slice(0, -5)] = readJson<Expected>(join(EXPECTED, file));
  }
  return result;
}

export function invalidCases(): Record<string, InvalidCase> {
  return readJson<Record<string, InvalidCase>>(join(INVALID, "expected.json"));
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

/** What {@link documentShape} needs of an open document. */
export interface DocumentLike {
  readonly manifest: {
    format?: unknown;
    formatVersion?: unknown;
    parts: readonly { name: string; tables: Readonly<Record<string, number>> }[];
  };
  readonly summary: { readonly tables?: unknown; readonly physicalColumns?: unknown; readonly physicalSchemas?: unknown };
  rowCount(name: string): number;
  readTable(name: "columnChunks"): Promise<unknown>;
}

/** The numbers of an expected file, computed from an open document. */
export async function documentShape(doc: DocumentLike): Promise<Shape> {
  return computeShape(doc.manifest, (name) => doc.rowCount(name), {
    tables: doc.summary.tables,
    columnChunks: await doc.readTable("columnChunks"),
    physicalColumns: doc.summary.physicalColumns,
    physicalSchemas: doc.summary.physicalSchemas,
  });
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

// ---- a small zip reader ----

export interface ZipEntryInfo {
  name: string;
  method: number;
  flags: number;
  compressedSize: number;
  size: number;
  offset: number;
}

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
export async function readEntries(bytes: Uint8Array): Promise<Map<string, Uint8Array>> {
  const result = new Map<string, Uint8Array>();
  for (const entry of centralDirectory(bytes).entries) {
    const start = dataOffset(bytes, entry);
    const raw = bytes.subarray(start, start + entry.compressedSize);
    result.set(entry.name, entry.method === 0 ? raw.slice() : await inflateRaw(raw));
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

/** A copy of a zip archive whose entries go through change: new content, null to drop the entry, undefined to keep it. */
export async function rewriteZip(bytes: Uint8Array,
  change: (name: string, data: Uint8Array) => Uint8Array | string | null | undefined): Promise<Uint8Array> {
  const entries: [string, Uint8Array | string][] = [];
  for (const [name, data] of await readEntries(bytes)) {
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
