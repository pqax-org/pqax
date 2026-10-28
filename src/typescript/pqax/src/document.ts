// Opens a PQAX file as section 7.3 of the serialization says: the central directory, then manifest.json, then
// summary.json. A detail part is read only when someone asks for its table.
import { type ByteSource, type PqaxInput, memorySource, toByteSource, windowSource } from "./bytes.js";
import { PqaxError } from "./errors.js";
import { decodeUtf8, isJsonObject, member, parseJson, setMember } from "./json.js";
import { SUMMARY_TABLES, knownFields, summaryTableNames, withTypedValues } from "./schema.js";
import { type TablePiece, buildTable, emptyTable, joinPieces } from "./tables.js";
import type { ColumnarTable, DetailTableName, DetailTables, Manifest, PartInfo, Summary, SummaryTables } from "./types.js";
import { type ZipArchive, entryDataOffset, readEntry, readZip } from "./zip.js";

/** The first numbers of `formatVersion` that this package reads. */
export const KNOWN_MAJOR_VERSIONS: ReadonlySet<number> = new Set([0]);

/** An open PQAX file. */
export interface PqaxDocument {
  /** The manifest, with the members that this version does not know. */
  readonly manifest: Manifest;
  /** For a file opened with {@link openEmbedded}, the name of the part of the package that holds it. Otherwise null. */
  readonly embeddedPart: string | null;
  /** For a file opened with {@link openEmbedded}, how the package writes the part: `stored` or `deflated`. Otherwise null. */
  readonly embeddedCompression: "stored" | "deflated" | null;
  /**
   * The summary tables, read when the file opens. Each is a columnar table: `summary.tables.fileCount` is an array with
   * one value for each table. A table that the file does not have is undefined.
   */
  readonly summary: Summary;
  /**
   * The number of rows of a table, from the manifest: the sum over the parts that list it. 0 for a table that the file
   * does not have.
   */
  rowCount(table: string): number;
  /**
   * Reads a detail table: all its parts, in the order of the manifest, joined into one columnar table. The result is
   * cached, so a second call returns the same object. A table that the file does not have is `{ length: 0 }`.
   * @throws {PqaxError} when a part of the table is missing from the archive, cannot be read, or is not valid JSON.
   */
  readTable<K extends DetailTableName>(name: K): Promise<DetailTables[K]>;
  /**
   * Reads a table as the file holds it: every member, including the fields that this version does not know and the
   * members that are not arrays, which a later version may add. The arrays of several parts are joined. A table that the
   * file does not have is `{}`. The result is not cached.
   * @throws {PqaxError} as {@link PqaxDocument.readTable} does.
   */
  readRaw(name: string): Promise<Record<string, unknown[]>>;
}

/**
 * Opens a PQAX file. It reads the central directory, `manifest.json`, and `summary.json`, and nothing else.
 * @param input The file: a `Blob`, an `ArrayBuffer`, a `Uint8Array`, or a {@link ByteSource}.
 * @throws {PqaxError} `container.manifestMissing` when the file is not a zip archive or has no `manifest.json`,
 * `manifest.format` when the format is not `pqax`, `manifest.versionUnknown` when this package does not know the first
 * number of `formatVersion`, and `json.invalid` when the manifest or the summary is not valid JSON.
 */
export async function openPqax(input: PqaxInput): Promise<PqaxDocument> {
  return openSource(toByteSource(input));
}

/**
 * Opens the PQAX file that a VPAX package, or any zip archive, holds as one part. A stored part is read in place,
 * without a copy. A deflated part is inflated in memory first.
 * @param input The package.
 * @param partName The name of the part. A leading `/` is ignored, and the case is ignored when no part has the exact
 * name.
 * @throws {PqaxError} `embedded.partMissing` when the package has no such part, and the errors of {@link openPqax}.
 */
export async function openEmbedded(input: PqaxInput, partName = "Footprint.pqax"): Promise<PqaxDocument> {
  const embedded = await findEmbedded(input, partName);
  if (embedded === null) {
    throw partMissing(partName);
  }
  return openSource(embedded.source, embedded);
}

/**
 * Opens the PQAX file that a VPAX package holds as one part, as {@link openEmbedded} does, or returns null when the
 * package has no part with that name, as most VPAX files of today have none.
 * @param input The package.
 * @param partName The name of the part, as for {@link openEmbedded}.
 * @throws {PqaxError} the errors of {@link openEmbedded}, except `embedded.partMissing`.
 */
export async function tryOpenEmbedded(input: PqaxInput, partName = "Footprint.pqax"): Promise<PqaxDocument | null> {
  const embedded = await findEmbedded(input, partName);
  return embedded === null ? null : openSource(embedded.source, embedded);
}

/**
 * Finds the PQAX file that a VPAX package holds as one part, and returns its bytes as a {@link ByteSource}, to validate
 * it with {@link validate} or to open it with {@link openPqax}. A stored part is read in place, through a window on the
 * package. A deflated part is inflated in memory.
 * @param input The package.
 * @param partName The name of the part, as for {@link openEmbedded}.
 * @throws {PqaxError} `embedded.partMissing` when the package has no such part, `container.manifestMissing` when the
 * package is not a zip archive, and the errors of an entry that cannot be read.
 */
export async function embeddedSource(input: PqaxInput, partName = "Footprint.pqax"): Promise<ByteSource> {
  const embedded = await findEmbedded(input, partName);
  if (embedded === null) {
    throw partMissing(partName);
  }
  return embedded.source;
}

/** A PQAX file inside a package. */
interface EmbeddedPart {
  readonly source: ByteSource;
  readonly name: string;
  readonly compression: "stored" | "deflated";
}

// The part of a package, or null when the package has no part with that name.
async function findEmbedded(input: PqaxInput, partName: string): Promise<EmbeddedPart | null> {
  const outer = await readZip(toByteSource(input));
  const wanted = partName.replace(/^\/+/, "");
  const lower = wanted.toLowerCase();
  const entry = outer.find(wanted) ?? outer.entries.find((candidate) => candidate.name.toLowerCase() === lower);
  if (entry === undefined) {
    return null;
  }
  if (entry.method === 0 && !entry.encrypted && !entry.needsZip64) {
    const start = await entryDataOffset(outer, entry);
    return { source: windowSource(outer.source, start, entry.compressedSize), name: entry.name, compression: "stored" };
  }
  return { source: memorySource(await readEntry(outer, entry)), name: entry.name, compression: "deflated" };
}

function partMissing(partName: string): PqaxError {
  const wanted = partName.replace(/^\/+/, "");
  return new PqaxError("embedded.partMissing", `The package has no part named ${wanted}`, wanted);
}

/** A part as the manifest lists it, with the counts that are valid. */
interface ListedPart {
  readonly name: string;
  /** For each table that the part lists, its count when it is a non-negative integer. */
  readonly tables: ReadonlyMap<string, number | undefined>;
}

async function openSource(source: ByteSource, embedded?: EmbeddedPart): Promise<PqaxDocument> {
  const archive = await readZip(source);
  const manifestEntry = archive.find("manifest.json");
  if (manifestEntry === undefined) {
    throw new PqaxError("container.manifestMissing", "The file has no manifest.json entry");
  }
  const value = await readJson(archive, "manifest.json");
  if (!isJsonObject(value)) {
    throw new PqaxError("manifest.format", "manifest.json is not a JSON object, so it does not say that the format is pqax", "manifest.json");
  }
  if (member(value, "format") !== "pqax") {
    throw new PqaxError("manifest.format", "The manifest does not say that the format is pqax", "manifest.json");
  }
  // Any version whose first number is known, whatever the second: a 0.x draft can add what this package ignores.
  const major = majorVersion(member(value, "formatVersion"));
  if (major === undefined || !KNOWN_MAJOR_VERSIONS.has(major)) {
    throw new PqaxError("manifest.versionUnknown", "This package reads format version 0.x only", "manifest.json");
  }
  const { manifest, parts } = readManifest(value);
  const summaryPart = parts.find((part) => part.name === "summary.json");
  let summaryContent: Record<string, unknown> | undefined;
  if (summaryPart !== undefined && archive.find(summaryPart.name) !== undefined) {
    const content = await readJson(archive, summaryPart.name);
    summaryContent = isJsonObject(content) ? content : {};
  }
  return new Document(archive, manifest, parts, summaryContent, embedded?.name ?? null, embedded?.compression ?? null);
}

class Document implements PqaxDocument {
  readonly summary: Summary;
  private readonly cache = new Map<string, Promise<ColumnarTable>>();

  constructor(
    private readonly archive: ZipArchive,
    readonly manifest: Manifest,
    private readonly parts: readonly ListedPart[],
    private readonly summaryContent: Record<string, unknown> | undefined,
    readonly embeddedPart: string | null,
    readonly embeddedCompression: "stored" | "deflated" | null,
  ) {
    const declared = parts.find((part) => part.name === "summary.json")?.tables;
    const summary: Partial<Record<keyof SummaryTables, ColumnarTable>> = {};
    for (const name of summaryTableNames) {
      const table = member(summaryContent, name);
      if (isJsonObject(table)) {
        summary[name] = typedTable(name, [{ table, declared: declared?.get(name) }]);
      }
    }
    this.summary = summary as Summary;
  }

  rowCount(table: string): number {
    let rows = 0;
    for (const part of this.parts) {
      rows += part.tables.get(table) ?? 0;
    }
    return rows;
  }

  readTable<K extends DetailTableName>(name: K): Promise<DetailTables[K]> {
    // The summary tables are read when the file opens. JavaScript callers can ask for them here too.
    const summaryTable = (this.summary as Record<string, ColumnarTable | undefined>)[name];
    if (summaryTable !== undefined) {
      return Promise.resolve(summaryTable as DetailTables[K]);
    }
    let table = this.cache.get(name);
    if (table === undefined) {
      table = this.piecesOf(name).then((pieces) => (pieces.length === 0 ? emptyTable() : typedTable(name, pieces)));
      table.catch(() => this.cache.delete(name));
      this.cache.set(name, table);
    }
    return table as Promise<DetailTables[K]>;
  }

  async readRaw(name: string): Promise<Record<string, unknown[]>> {
    const joined = joinPieces(await this.piecesOf(name), undefined, false);
    const raw: Record<string, unknown> = {};
    for (const [field, values] of joined.fields) {
      setMember(raw, field, values);
    }
    for (const [field, value] of joined.others) {
      setMember(raw, field, value);
    }
    return raw as Record<string, unknown[]>;
  }

  // The pieces of a table, from every part that the manifest lists for it, in order. A summary table that summary.json
  // holds and the manifest does not list, which only an invalid file has, is read from summary.json.
  private async piecesOf(name: string): Promise<TablePiece[]> {
    const holders = this.parts.filter((part) => part.tables.has(name));
    const contents = await Promise.all(holders.map((part) => this.readPart(part.name)));
    const pieces = holders.map((part, i) => ({ table: asObject(member(contents[i], name)), declared: part.tables.get(name) }));
    const inSummary = SUMMARY_TABLES.has(name) ? asObject(member(this.summaryContent, name)) : undefined;
    return pieces.length === 0 && inSummary !== undefined ? [{ table: inSummary, declared: undefined }] : pieces;
  }

  private async readPart(name: string): Promise<Record<string, unknown>> {
    if (name === "summary.json" && this.summaryContent !== undefined) {
      return this.summaryContent;
    }
    if (this.archive.find(name) === undefined) {
      throw new PqaxError("manifest.partMissing", `The manifest lists ${name}, and the archive has no such entry`, name);
    }
    const content = await readJson(this.archive, name);
    return isJsonObject(content) ? content : {};
  }
}

// A typed table keeps the fields that this version knows, with null for a value whose JSON type is not the type of its
// field. A table that this version does not know keeps every array as it is.
function typedTable(name: string, pieces: readonly TablePiece[]): ColumnarTable {
  const known = knownFields(name);
  const keep = known === undefined ? undefined : (field: string) => Object.prototype.hasOwnProperty.call(known, field);
  const joined = joinPieces(pieces, keep, true);
  if (known !== undefined) {
    for (const [field, values] of joined.fields) {
      joined.fields.set(field, withTypedValues(known[field]!, values));
    }
  }
  return buildTable(joined.length, joined.fields);
}

async function readJson(archive: ZipArchive, name: string): Promise<unknown> {
  const bytes = await readEntry(archive, archive.find(name)!);
  const parsed = parseJson(decodeUtf8(bytes, false).text);
  if (!parsed.ok) {
    throw new PqaxError("json.invalid", `${name} is not valid JSON: ${parsed.message}`, name);
  }
  return parsed.value;
}

/** The first number of a version such as `0.1`, or undefined when the version does not start with a number. */
export function majorVersion(version: unknown): number | undefined {
  if (typeof version !== "string") {
    return undefined;
  }
  const match = /^([0-9]+)(?:\.|$)/.exec(version);
  return match ? Number(match[1]) : undefined;
}

// The manifest keeps every member as the file has it. Only parts are cleaned: a part with no name is left out, and the
// counts that are not non-negative integers are ignored when the reader counts rows.
function readManifest(value: Record<string, unknown>): { manifest: Manifest; parts: ListedPart[] } {
  const rawParts = Array.isArray(value.parts) ? (value.parts as unknown[]) : [];
  const kept: PartInfo[] = [];
  const parts: ListedPart[] = [];
  const seen = new Set<string>();
  for (const raw of rawParts) {
    if (!isJsonObject(raw) || typeof raw.name !== "string") {
      continue;
    }
    const tables = isJsonObject(raw.tables) ? raw.tables : {};
    kept.push((tables === raw.tables ? raw : { ...raw, tables }) as unknown as PartInfo);
    if (seen.has(raw.name)) {
      continue;
    }
    seen.add(raw.name);
    const counts = new Map<string, number | undefined>();
    for (const [table, rows] of Object.entries(tables)) {
      counts.set(table, Number.isSafeInteger(rows) && (rows as number) >= 0 ? (rows as number) : undefined);
    }
    parts.push({ name: raw.name, tables: counts });
  }
  const sameParts = kept.length === rawParts.length && kept.every((part, i) => part === rawParts[i]);
  const manifest = (sameParts ? value : { ...value, parts: kept }) as unknown as Manifest;
  return { manifest, parts };
}

function asObject(value: unknown): Record<string, unknown> | undefined {
  return isJsonObject(value) ? value : undefined;
}
