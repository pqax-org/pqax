// Checks a PQAX file against the rules of the serialization and the object model, with the codes of section 4.1 of the
// contracts and the exact conditions of the rules of the conformance kit. It reports every issue it finds,
// and skips a rule that an earlier failure leaves without the data it needs. Messages hold codes, names of the format,
// counts, rows, and ids: never a value of the data.
import { type ByteSource, type PqaxInput, toByteSource } from "./bytes.js";
import { KNOWN_MAJOR_VERSIONS, majorVersion } from "./document.js";
import { PqaxError, type ValidationCode } from "./errors.js";
import { type PathCount, decodeUtf8, isJsonObject, member, parseJson, pathKey, scanJson } from "./json.js";
import {
  COLLECTION_FIELDS,
  DETAIL_TABLES,
  type FieldKind,
  NESTED_FIELDS,
  REFERENCES,
  REQUIRED,
  SUMMARY_TABLES,
  describeKind,
  hasKindType,
  isTableName,
  knownFields,
} from "./schema.js";
import { type JoinedTable, type TablePiece, joinPieces, pieceRows } from "./tables.js";
import type { TableName } from "./types.js";
import { type ZipArchive, type ZipEntry, readEntry, readZip } from "./zip.js";

/** A rule that a file breaks. */
export interface ValidationIssue {
  /** The code of the rule, from section 4.1 of the contracts. */
  readonly code: ValidationCode;
  /** What is wrong. */
  readonly message: string;
  /** The part or the zip entry concerned, when there is one. */
  readonly part?: string;
  /** The table concerned, when there is one. */
  readonly table?: string;
  /** The field concerned, when there is one. In the manifest, a path such as `collection.level`. */
  readonly field?: string;
}

/** What {@link validate} found. */
export interface ValidationResult {
  /** Whether the file breaks no rule. */
  readonly isValid: boolean;
  /** Every issue found, in the order of the checks: container, manifest, parts, identifiers, references, depth, totals. */
  readonly issues: readonly ValidationIssue[];
  /** The number of checks that ran. */
  readonly checks: number;
}

/**
 * Validates a PQAX file. It reads what a reader reads, reports every issue it finds instead of stopping at the first,
 * and never throws for a problem of the file. It throws only when the input itself cannot be read.
 *
 * To validate the footprint inside a VPAX package, pass `await embeddedSource(input)`.
 * @param input The file: a `Blob`, an `ArrayBuffer`, a `Uint8Array`, or a {@link ByteSource}.
 */
export async function validate(input: PqaxInput): Promise<ValidationResult> {
  const validator = new Validator(toByteSource(input));
  await validator.run();
  return { isValid: validator.issues.length === 0, issues: validator.issues, checks: validator.checks };
}

const ROWS_PER_PART = 250_000;
const PART_NAME = /^[A-Za-z0-9._-]+$/;
const FORMAT_VERSION = /^[0-9]+\.[0-9]+$/;
const TIMESTAMP = /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$/;
const DEPTHS = ["Summary", "Files", "Row groups", "Column chunks"];
const MANIFEST = "manifest.json";
const MAX_DUPLICATES = 20;

interface Where {
  part?: string;
  table?: string;
  field?: string;
}

/** A document that parsed, with the numbers that its text writes with a fraction or an exponent. */
interface ParsedPart {
  readonly value: Record<string, unknown>;
  readonly fractions: ReadonlyMap<string, PathCount>;
}

/** A part as the manifest lists it, with a name and tables. */
interface ListedPart {
  readonly name: string;
  /** For each table that the manifest states for the part, its count when it is a non-negative integer. */
  readonly tables: ReadonlyMap<string, number | undefined>;
}

class Validator {
  readonly issues: ValidationIssue[] = [];
  checks = 0;
  /** The pieces of each table, from the parts that could be read, in the order of the manifest. */
  private readonly pieces = new Map<string, TablePiece[]>();
  /** The tables with rows in a part that could not be read: the rules that need all their rows are skipped. */
  private readonly incomplete = new Set<string>();
  /** The tables whose arrays disagree on the number of rows in a part: their rows cannot be lined up. */
  private readonly misaligned = new Set<string>();
  /** The tables whose ids are not all distinct positive integers: rows cannot be found by id. */
  private readonly badIds = new Set<string>();
  /** The fields, as `table.field`, that a rule found broken: a required field without values, or a dangling reference. */
  private readonly brokenFields = new Set<string>();

  constructor(private readonly source: ByteSource) {}

  async run(): Promise<void> {
    let archive: ZipArchive;
    try {
      archive = await readZip(this.source);
    } catch (error) {
      if (!(error instanceof PqaxError)) {
        throw error;
      }
      this.check(false, error.code === "container.zip64" ? "container.zip64" : "container.manifestMissing", error.message);
      return;
    }
    this.checkContainer(archive);
    const manifestEntry = archive.find(MANIFEST);
    if (!this.check(manifestEntry !== undefined, "container.manifestMissing", "The archive has no manifest.json entry")) {
      return;
    }
    const manifest = await this.readDocument(archive, manifestEntry!, MANIFEST);
    if (manifest === undefined) {
      return;
    }
    const parts = this.checkManifest(manifest, archive);
    if (parts === undefined) {
      return;
    }
    for (const part of parts) {
      const entry = archive.find(part.name);
      const parsed = entry === undefined ? undefined : await this.readDocument(archive, entry, part.name);
      if (parsed === undefined) {
        for (const table of part.tables.keys()) {
          this.incomplete.add(table);
        }
        continue;
      }
      this.checkPart(part, parsed);
    }
    const ids = this.checkIdentifiers();
    this.checkReferences(ids);
    this.checkDepthAndTotals();
  }

  private check(ok: boolean, code: ValidationCode, message: string | (() => string), where: Where = {}): boolean {
    this.checks++;
    if (!ok) {
      this.report(code, typeof message === "string" ? message : message(), where);
    }
    return ok;
  }

  private report(code: ValidationCode, message: string, where: Where): void {
    const issue: { code: ValidationCode; message: string; part?: string; table?: string; field?: string } = { code, message };
    if (where.part !== undefined) {
      issue.part = where.part;
    }
    if (where.table !== undefined) {
      issue.table = where.table;
    }
    if (where.field !== undefined) {
      issue.field = where.field;
    }
    this.issues.push(issue);
  }

  // ---- The container: section 3 ----

  private checkContainer(archive: ZipArchive): void {
    this.check(!archive.zip64, "container.zip64", "The archive has a ZIP64 end of central directory locator");
    for (const entry of archive.entries) {
      const where = { part: entry.name };
      this.check(entry.method === 0 || entry.method === 8, "container.method", () => `${entry.name} is neither stored nor deflated: its method is ${entry.method}`, where);
      this.check(!entry.encrypted, "container.encrypted", () => `${entry.name} is encrypted`, where);
      this.check(!entry.zip64, "container.zip64", () => `${entry.name} uses ZIP64 in the central directory`, where);
    }
    // Names are compared with ASCII letters folded to lower case, and the same name twice collides too.
    const byFoldedName = new Map<string, string[]>();
    for (const entry of archive.entries) {
      const key = entry.name.replace(/[A-Z]/g, (letter) => letter.toLowerCase());
      const names = byFoldedName.get(key);
      if (names === undefined) {
        byFoldedName.set(key, [entry.name]);
      } else {
        names.push(entry.name);
      }
    }
    for (const names of byFoldedName.values()) {
      const distinct = [...new Set(names)];
      this.check(
        names.length === 1,
        "container.caseCollision",
        () => (distinct.length === 1 ? `The archive has ${names.length} entries named ${names[0]}` : `The entry names ${distinct.join(", ")} differ only in case`),
        { part: names[0]! },
      );
    }
  }

  // ---- A document, the manifest or a part: section 4 ----

  private async readDocument(archive: ZipArchive, entry: ZipEntry, name: string): Promise<ParsedPart | undefined> {
    const where = { part: name };
    let bytes: Uint8Array;
    try {
      bytes = await readEntry(archive, entry);
    } catch (error) {
      if (!(error instanceof PqaxError)) {
        throw error;
      }
      // The method, the encryption, and ZIP64 are reported with the container. A damaged entry is reported here.
      if (error.code === "json.invalid") {
        this.check(false, "json.invalid", error.message, where);
      }
      return undefined;
    }
    const decoded = decodeUtf8(bytes, true);
    this.check(!decoded.bom, "json.bom", `${name} starts with a byte order mark`, where);
    this.check(decoded.validUtf8, "json.invalid", `${name} is not valid UTF-8`, where);
    const parsed = parseJson(decoded.text);
    if (!parsed.ok) {
      this.check(false, "json.invalid", `${name} is not valid JSON: ${parsed.message}`, where);
      return undefined;
    }
    if (!this.check(isJsonObject(parsed.value), "json.topLevel", `The top level of ${name} is not an object`, where)) {
      return undefined;
    }
    const scan = scanJson(decoded.text);
    this.checks++;
    for (const duplicate of scan.duplicates.slice(0, MAX_DUPLICATES)) {
      // A name inside a row can be a key of a map, which can come from the data, so only the names of the format are shown.
      const shown = duplicate.path.includes("[]") ? "a member name" : `the member ${JSON.stringify(duplicate.name)}`;
      this.report("json.duplicateMember", `${name} repeats ${shown} in ${describe(duplicate.path)}`, locate(name, duplicate.path, duplicate.name));
    }
    if (scan.duplicates.length > MAX_DUPLICATES) {
      this.report("json.duplicateMember", `${name} repeats ${scan.duplicates.length - MAX_DUPLICATES} more member names`, where);
    }
    this.check(scan.loneSurrogates === 0, "json.invalid", () => `${name} has ${scan.loneSurrogates} strings with a lone surrogate, which is not valid Unicode`, where);
    return { value: parsed.value as Record<string, unknown>, fractions: scan.fractions };
  }

  // ---- The manifest: sections 7.1 and 9.4 ----

  private checkManifest(parsed: ParsedPart, archive: ZipArchive): ListedPart[] | undefined {
    const manifest = parsed.value;
    const at = (field: string): Where => ({ part: MANIFEST, field });
    for (const name of ["format", "formatVersion", "collection", "parts"]) {
      const value = member(manifest, name);
      this.check(value !== undefined && value !== null, "manifest.required", `The manifest has no ${name}`, at(name));
    }
    const format = member(manifest, "format");
    let knownFormat = true;
    if (format !== undefined && format !== null) {
      this.check(typeof format === "string", "json.type", "format is not a string", at("format"));
      knownFormat = this.check(format === "pqax", "manifest.format", "The manifest does not say that the format is pqax", at("format"));
    }
    const version = member(manifest, "formatVersion");
    let knownVersion = true;
    if (version !== undefined && version !== null) {
      this.check(typeof version === "string", "json.type", "formatVersion is not a string", at("formatVersion"));
      const wellFormed = this.check(typeof version === "string" && FORMAT_VERSION.test(version), "manifest.formatVersion", "formatVersion is not two numbers separated by a dot", at("formatVersion"));
      if (wellFormed) {
        knownVersion = this.check(KNOWN_MAJOR_VERSIONS.has(majorVersion(version)!), "manifest.versionUnknown", `This validator reads format version 0.x, and the file has ${String(version)}`, at("formatVersion"));
      }
    }
    // A reader stops at a file that is not PQAX or whose version it does not know, and so does the validator.
    if (!knownFormat || !knownVersion) {
      return undefined;
    }

    for (const name of ["obfuscatorDictionaryId", "obfuscatorLib", "obfuscatorLibVersion"]) {
      const value = member(manifest, name);
      if (value !== undefined && value !== null) {
        this.check(typeof value === "string", "json.type", `${name} is not a string`, at(name));
      }
    }
    const collection = member(manifest, "collection");
    if (collection !== undefined && collection !== null && this.check(isJsonObject(collection), "json.type", "collection is not an object", at("collection"))) {
      const info = collection as Record<string, unknown>;
      const level = member(info, "level");
      this.check(level !== undefined && level !== null, "manifest.required", "collection has no level", at("collection.level"));
      for (const [name, kind] of Object.entries(COLLECTION_FIELDS)) {
        const value = member(info, name);
        if (value !== undefined && value !== null) {
          this.checkSingle(at(`collection.${name}`), `collection.${name}`, kind, value, parsed.fractions.has(pathKey(["collection", name])));
        }
      }
    }

    const rawParts = member(manifest, "parts");
    if (rawParts === undefined || rawParts === null || !this.check(Array.isArray(rawParts), "json.type", "parts is not an array", at("parts"))) {
      return undefined;
    }
    // An element of parts without a name or tables is left out of the rules on parts.
    const listed: ListedPart[] = [];
    const counts = new Map<string, number>();
    (rawParts as unknown[]).forEach((raw, i) => {
      if (!this.check(isJsonObject(raw), "json.type", `Element ${i} of parts is not an object`, at(`parts[${i}]`))) {
        return;
      }
      const descriptor = raw as Record<string, unknown>;
      const name = member(descriptor, "name");
      if (
        !this.check(name !== undefined && name !== null, "manifest.required", `Element ${i} of parts has no name`, at(`parts[${i}].name`)) ||
        !this.check(typeof name === "string", "json.type", `The name of element ${i} of parts is not a string`, at(`parts[${i}].name`))
      ) {
        return;
      }
      const partName = name as string;
      const rawTables = member(descriptor, "tables");
      const where = { part: partName, field: "tables" };
      if (
        !this.check(rawTables !== undefined && rawTables !== null, "manifest.required", `The manifest states no tables for ${partName}`, where) ||
        !this.check(isJsonObject(rawTables), "json.type", `The tables of ${partName} in the manifest are not an object`, where)
      ) {
        return;
      }
      const tables = new Map<string, number | undefined>();
      for (const [table, rows] of Object.entries(rawTables as Record<string, unknown>)) {
        tables.set(table, this.checkRowCountOfManifest(partName, table, rows, parsed.fractions.has(pathKey(["parts", "[]", "tables", table]))));
      }
      const seen = counts.get(partName) ?? 0;
      counts.set(partName, seen + 1);
      if (seen === 0) {
        listed.push({ name: partName, tables });
      }
    });
    const first = (rawParts as unknown[])[0];
    this.check(isJsonObject(first) && member(first, "name") === "summary.json", "manifest.firstPart", "The first part of the manifest is not summary.json", { part: MANIFEST });
    for (const [name, count] of counts) {
      this.check(count === 1, "manifest.duplicatePart", `The manifest lists ${name} ${count} times`, { part: name });
      this.check(PART_NAME.test(name), "container.partName", `The part name ${JSON.stringify(name)} has a character other than ASCII letters, digits, ".", "-", and "_"`, {
        part: name,
      });
      this.check(archive.find(name) !== undefined, "manifest.partMissing", `The manifest lists ${name}, and the archive has no such entry`, { part: name });
    }
    return listed;
  }

  // Checks a number of rows that the manifest states, and returns it when it is a non-negative integer.
  private checkRowCountOfManifest(part: string, table: string, rows: unknown, written: boolean): number | undefined {
    const where = { part, table };
    const label = `The number of rows of ${table} in ${part} in the manifest`;
    if (!this.check(typeof rows === "number", "json.type", `${label} is not a number`, where)) {
      return undefined;
    }
    const count = rows as number;
    const integer = this.check(Number.isInteger(count) && !written, "json.integer", `${label} is not an integer`, where);
    const inRange = this.check(Math.abs(count) <= Number.MAX_SAFE_INTEGER, "json.integerRange", `${label} is beyond 2^53 - 1`, where);
    if (!integer || !inRange) {
      return undefined;
    }
    return this.check(count >= 0, "part.rowCount", `${label} is negative`, where) ? count : undefined;
  }

  // The type, integer, and timestamp rules for one value of the manifest.
  private checkSingle(where: Where, label: string, kind: FieldKind, value: unknown, written: boolean): void {
    if (!this.check(hasKindType(kind, value), "json.type", `${label} is not one of the ${describeKind(kind)}`, where)) {
      return;
    }
    if (kind === "integer") {
      this.check(Number.isInteger(value) && !written, "json.integer", `${label} is not an integer`, where);
      this.check(Math.abs(value as number) <= Number.MAX_SAFE_INTEGER, "json.integerRange", `${label} is beyond 2^53 - 1 in absolute value`, where);
    } else if (kind === "timestamp") {
      this.check(TIMESTAMP.test(value as string), "json.timestamp", `${label} is not a timestamp in the form 2026-10-05T08:15:30.000Z`, where);
    }
  }

  // ---- The tables of a part: sections 5.1 and 7.2 ----

  private checkPart(part: ListedPart, parsed: ParsedPart): void {
    const content = parsed.value;
    const name = part.name;
    const isSummary = name === "summary.json";
    const held = Object.keys(content);
    for (const table of held) {
      this.check(part.tables.has(table), "part.tables", `${name} holds ${table}, and the manifest does not state it for that part`, { part: name, table });
    }
    for (const table of part.tables.keys()) {
      if (!this.check(Object.prototype.hasOwnProperty.call(content, table), "part.tables", `The manifest states ${table} for ${name}, and the part does not hold it`, { part: name, table })) {
        this.incomplete.add(table);
      }
    }
    for (const table of new Set([...held, ...part.tables.keys()])) {
      if (isSummary) {
        this.check(!DETAIL_TABLES.has(table), "part.kind", `${table} is a detail table, and it is in summary.json`, { part: name, table });
      } else {
        this.check(!SUMMARY_TABLES.has(table), "part.kind", `${table} is a summary table, and it is in ${name}`, { part: name, table });
      }
    }
    if (!isSummary) {
      this.check(held.length <= 1, "part.singleTable", `${name} is a detail part, and it holds ${held.length} tables`, { part: name });
      let declared = 0;
      for (const count of part.tables.values()) {
        declared += count ?? 0;
      }
      this.check(declared <= ROWS_PER_PART, "part.tooLarge", `${name} is a detail part, and the manifest states ${declared} rows for it, more than ${ROWS_PER_PART}`, { part: name });
    }
    for (const table of held) {
      const value = content[table];
      if (!this.check(isJsonObject(value), "json.type", `${table} in ${name} is not an object`, { part: name, table })) {
        this.incomplete.add(table);
        continue;
      }
      const piece: TablePiece = { table: value as Record<string, unknown>, declared: part.tables.get(table) };
      this.checkRowCount(name, table, piece, part.tables.has(table));
      const pieces = this.pieces.get(table);
      if (pieces === undefined) {
        this.pieces.set(table, [piece]);
      } else {
        pieces.push(piece);
      }
      if (isTableName(table)) {
        this.checkTable(name, table, piece.table!, parsed.fractions);
      }
    }
  }

  private checkRowCount(part: string, table: string, piece: TablePiece, listed: boolean): void {
    const arrays = Object.entries(piece.table ?? {}).filter((entry): entry is [string, unknown[]] => Array.isArray(entry[1]));
    const { aligned } = pieceRows(piece);
    if (!aligned) {
      this.misaligned.add(table);
    }
    const declared = piece.declared;
    if (listed && declared !== undefined) {
      const wrong = arrays.filter(([, values]) => values.length !== declared);
      if (aligned && wrong.length > 0) {
        this.check(false, "part.rowCount", `${table} has ${wrong[0]![1].length} rows in ${part}, and the manifest states ${declared}`, { part, table });
      } else {
        for (const [field, values] of arrays) {
          this.check(values.length === declared, "part.rowCount", () => `${table}.${field} has ${values.length} values in ${part}, and the manifest states ${declared} rows`, {
            part,
            table,
            field,
          });
        }
      }
    } else if (!aligned) {
      this.check(false, "part.rowCount", `The arrays of ${table} in ${part} have different lengths`, { part, table });
    }
  }

  // The required fields of a table of the format, and the values of its fields, in one part.
  private checkTable(part: string, table: TableName, content: Record<string, unknown>, fractions: ReadonlyMap<string, PathCount>): void {
    for (const field of REQUIRED[table]) {
      const values = member(content, field);
      const where = { part, table, field };
      const missing = Array.isArray(values) ? values.filter((value) => value === null || value === undefined).length : -1;
      const ok = this.check(missing === 0, "table.required", () =>
        missing < 0 ? `${table}.${field} is required, and ${part} does not have it` : `${table}.${field} is required, and it has no value in ${missing} rows of ${part}`,
      where);
      if (!ok) {
        this.brokenFields.add(`${table}.${field}`);
      }
    }
    for (const [field, kind] of Object.entries(knownFields(table)!)) {
      const values = member(content, field);
      if (Array.isArray(values)) {
        this.checkValues(part, table, field, kind, values, fractions);
      }
    }
  }

  // The type, integer, and timestamp rules of section 4 for the values of one field in one part.
  private checkValues(part: string, table: string, field: string, kind: FieldKind, values: readonly unknown[], fractions: ReadonlyMap<string, PathCount>): void {
    const where = { part, table, field };
    const label = `${table}.${field}`;
    const path = [table, field, "[]"];
    const wrong = tally(values, (value) => hasKindType(kind, value));
    this.check(wrong.count === 0, "json.type", () => `${label} has ${wrong.count} values in ${part} that are not ${describeKind(kind)}, the first in row ${wrong.first}`, where);
    if (kind === "id" || kind === "integer" || kind.startsWith("ref:")) {
      this.checkIntegers(where, label, values, false, fractions.has(pathKey(path)));
    } else if (kind.startsWith("refs:")) {
      this.checkIntegers(where, label, values, true, fractions.has(pathKey([...path, "[]"])) || fractions.has(pathKey(path)));
    } else if (kind === "timestamp") {
      const bad = tally(values, (value) => typeof value !== "string" || TIMESTAMP.test(value));
      this.check(bad.count === 0, "json.timestamp", () => `${label} has ${bad.count} values in ${part} that are not in the form 2026-10-05T08:15:30.000Z, the first in row ${bad.first}`, where);
    } else if (kind === "distribution" || kind === "delta" || kind === "iceberg") {
      for (const [name, memberKind] of Object.entries(NESTED_FIELDS[kind] as Record<string, FieldKind>)) {
        const members = values.map((value) => (isJsonObject(value) ? member(value, name) : undefined));
        const memberLabel = `${label}.${name}`;
        const wrongMember = tally(members, (value) => hasKindType(memberKind, value));
        this.check(wrongMember.count === 0, "json.type", () => `${memberLabel} has ${wrongMember.count} values in ${part} that are not ${describeKind(memberKind)}, the first in row ${wrongMember.first}`, where);
        if (memberKind === "integer") {
          this.checkIntegers(where, memberLabel, members, false, fractions.has(pathKey([...path, name])));
        }
      }
    }
  }

  // The integer rules on the numbers of a field: a fraction or an exponent, and the range. Values that are not numbers
  // break the type rule instead.
  private checkIntegers(where: Where, label: string, values: readonly unknown[], lists: boolean, written: boolean): void {
    const each = (test: (value: number) => boolean) => (value: unknown) => {
      const items = lists && Array.isArray(value) ? value : [value];
      return items.every((item) => typeof item !== "number" || test(item));
    };
    const fraction = tally(values, each(Number.isInteger));
    this.check(fraction.count === 0 && !written, "json.integer", () =>
      fraction.count > 0
        ? `${label} has ${fraction.count} values in ${where.part} that are not integers, the first in row ${fraction.first}`
        : `${label} has numbers in ${where.part} written with a fraction or an exponent`,
    where);
    const range = tally(values, each((value) => Math.abs(value) <= Number.MAX_SAFE_INTEGER));
    this.check(range.count === 0, "json.integerRange", () => `${label} has ${range.count} values in ${where.part} beyond 2^53 - 1 in absolute value, the first in row ${range.first}`, where);
  }

  // ---- Identifiers and references: section 6 ----

  private checkIdentifiers(): Map<string, Set<number>> {
    const ids = new Map<string, Set<number>>();
    for (const [table, pieces] of this.pieces) {
      if (knownFields(table)?.id !== "id") {
        continue;
      }
      const found = new Set<number>();
      const repeated = new Set<number>();
      let hasField = false;
      let notPositive = 0;
      for (const piece of pieces) {
        const values = member(piece.table, "id");
        if (!Array.isArray(values)) {
          continue; // table.required reports it
        }
        hasField = true;
        for (const id of values) {
          if (!isId(id)) {
            notPositive++;
          } else if (found.has(id)) {
            repeated.add(id);
          } else {
            found.add(id);
          }
        }
      }
      if (!hasField) {
        continue;
      }
      ids.set(table, found);
      const where = { table, field: "id" };
      const positive = this.check(notPositive === 0, "table.idUnique", () => `${notPositive} ids of ${table} are not integers greater than 0`, where);
      const unique = this.check(repeated.size === 0, "table.idUnique", () => `${repeated.size} ids of ${table} are repeated, such as ${repeated.values().next().value}`, where);
      if (!positive || !unique) {
        this.badIds.add(table);
      }
    }
    return ids;
  }

  private checkReferences(ids: Map<string, Set<number>>): void {
    for (const reference of REFERENCES) {
      const pieces = this.pieces.get(reference.table);
      if (pieces === undefined) {
        continue;
      }
      // A reference to a table whose parts could not all be read, or whose rows have no id field, cannot be checked.
      const targets = ids.get(reference.target);
      if (this.incomplete.has(reference.target) || (targets === undefined && this.pieces.has(reference.target))) {
        this.brokenFields.add(`${reference.table}.${reference.field}`);
        continue;
      }
      let present = false;
      let dangling = 0;
      let first: unknown;
      for (const piece of pieces) {
        const values = member(piece.table, reference.field);
        if (!Array.isArray(values)) {
          continue;
        }
        present = true;
        for (const value of values) {
          for (const target of reference.list && Array.isArray(value) ? value : [value]) {
            if (target !== null && target !== undefined && !(typeof target === "number" && targets?.has(target))) {
              if (dangling++ === 0) {
                first = target;
              }
            }
          }
        }
      }
      if (present) {
        const ok = this.check(
          dangling === 0,
          "table.reference",
          () =>
            `${dangling} values of ${reference.table}.${reference.field} refer to rows that ${reference.target} does not have` +
            (typeof first === "number" ? `, such as ${first}` : ""),
          { table: reference.table, field: reference.field },
        );
        if (!ok) {
          this.brokenFields.add(`${reference.table}.${reference.field}`);
        }
      }
    }
  }

  // ---- Depth and totals: section 8, and section 4.2 of the object model ----

  // A table joined across its parts with only the fields needed, or undefined when its rows cannot be trusted: a part of
  // it could not be read, its arrays disagree, or a reference that leads from it to its table is broken.
  private joined(table: string, fields: readonly string[], links: readonly string[] = []): JoinedTable | undefined {
    if (this.incomplete.has(table) || this.misaligned.has(table) || links.some((field) => this.brokenFields.has(`${table}.${field}`))) {
      return undefined;
    }
    return joinPieces(this.pieces.get(table) ?? [], (field) => fields.includes(field), true);
  }

  // A rule that counts the detail of a table follows references from the detail up to the table. When one of them is
  // broken, or when the ids it follows repeat, the counts are wrong for a reason already reported, and the rule is skipped.
  // A total that is null, or not a number, is not compared.
  private checkDepthAndTotals(): void {
    if (this.badIds.has("tables")) {
      return;
    }
    const tables = this.joined("tables", ["id", "detailDepth", "fileCount", "fileSize", "rowGroupCount", "compressedSize"]);
    if (tables === undefined || tables.length === 0) {
      return;
    }
    const tableIds = column(tables, "id");
    const known = new Set(tableIds.filter(isId));

    // Files, and the table of each file.
    const files = this.joined("files", ["id", "table", "fileSize"], ["table"]);
    const fileTable = new Map<number, number>();
    const fileCount = new Totals();
    const fileSize = new Totals();
    if (files !== undefined) {
      const fileIds = column(files, "id");
      const owners = column(files, "table");
      const sizes = column(files, "fileSize");
      for (let i = 0; i < files.length; i++) {
        const owner = owners[i];
        if (!isId(owner) || !known.has(owner)) {
          continue;
        }
        fileCount.add(owner, 1);
        fileSize.add(owner, sizes[i]);
        const id = fileIds[i];
        if (isId(id) && !fileTable.has(id)) {
          fileTable.set(id, owner);
        }
      }
    }

    // Row groups, through their file.
    const rowGroups = files === undefined || this.badIds.has("files") ? undefined : this.joined("rowGroups", ["file"], ["file"]);
    const groupCount = new Totals();
    if (rowGroups !== undefined) {
      const groupFiles = column(rowGroups, "file");
      for (let i = 0; i < rowGroups.length; i++) {
        const file = groupFiles[i];
        const owner = isId(file) ? fileTable.get(file) : undefined;
        if (owner !== undefined) {
          groupCount.add(owner, 1);
        }
      }
    }

    // Column chunks, through their physical column and its physical schema.
    const linked = !this.badIds.has("physicalSchemas") && !this.badIds.has("physicalColumns");
    const schemas = linked ? this.joined("physicalSchemas", ["id", "table"], ["table"]) : undefined;
    const physical = linked ? this.joined("physicalColumns", ["id", "physicalSchema", "column"], ["physicalSchema"]) : undefined;
    const chunks =
      schemas === undefined || physical === undefined ? undefined : this.joined("columnChunks", ["physicalColumn", "compressedSize"], ["physicalColumn"]);
    const chunkCount = new Totals();
    const chunkSize = new Totals();
    const columnSize = new Totals();
    if (chunks !== undefined && schemas !== undefined && physical !== undefined) {
      const schemaTable = mapIds(schemas, "table");
      const physicalSchema = mapIds(physical, "physicalSchema");
      const physicalColumn = mapIds(physical, "column");
      const chunkPhysical = column(chunks, "physicalColumn");
      const sizes = column(chunks, "compressedSize");
      for (let i = 0; i < chunks.length; i++) {
        const leaf = chunkPhysical[i];
        if (!isId(leaf)) {
          continue;
        }
        const schema = physicalSchema.get(leaf);
        const owner = schema === undefined ? undefined : schemaTable.get(schema);
        if (owner !== undefined) {
          chunkCount.add(owner, 1);
          chunkSize.add(owner, sizes[i]);
        }
        const top = physicalColumn.get(leaf);
        if (top !== undefined) {
          columnSize.add(top, sizes[i]);
        }
      }
    }

    const depths = column(tables, "detailDepth");
    const fileCounts = column(tables, "fileCount");
    const fileSizes = column(tables, "fileSize");
    const rowGroupCounts = column(tables, "rowGroupCount");
    const compressedSizes = column(tables, "compressedSize");
    const deepTables = new Set<number>();
    for (let i = 0; i < tables.length; i++) {
      const id = tableIds[i];
      const depth = depths[i];
      if (!isId(id) || typeof depth !== "string") {
        continue;
      }
      // A depth that this version does not know is deeper than Column chunks. The comparison is exact, with case.
      const level = DEPTHS.includes(depth) ? DEPTHS.indexOf(depth) : DEPTHS.length;
      const row = `The table with id ${id}`;
      const at = (field: string): Where => ({ table: "tables", field });
      if (files !== undefined) {
        const kept = fileCount.get(id);
        if (level === 0) {
          this.check(kept === 0, "depth.files", () => `${row} is at the Summary depth, and the file has ${kept} of its files`, at("detailDepth"));
        } else {
          if (typeof fileCounts[i] === "number") {
            this.check(fileCounts[i] === kept, "totals.fileCount", () => `${row} has fileCount ${String(fileCounts[i])}, and the file has ${kept} of its files`, at("fileCount"));
          }
          if (typeof fileSizes[i] === "number" && fileSize.known(id)) {
            const sum = fileSize.get(id);
            this.check(fileSizes[i] === sum, "totals.fileSize", () => `${row} has fileSize ${String(fileSizes[i])}, and its files add up to ${sum}`, at("fileSize"));
          }
        }
      }
      if (rowGroups !== undefined) {
        const kept = groupCount.get(id);
        if (level < 2) {
          this.check(kept === 0, "depth.rowGroups", () => `${row} is at the ${depth} depth, and the file has ${kept} of its row groups`, at("detailDepth"));
        } else if (typeof rowGroupCounts[i] === "number") {
          this.check(rowGroupCounts[i] === kept, "totals.rowGroupCount", () => `${row} has rowGroupCount ${String(rowGroupCounts[i])}, and the file has ${kept} of its row groups`, at("rowGroupCount"));
        }
      }
      if (chunks !== undefined) {
        const kept = chunkCount.get(id);
        if (level < 3) {
          this.check(kept === 0, "depth.columnChunks", () => `${row} is at the ${depth} depth, and the file has ${kept} of its column chunks`, at("detailDepth"));
        } else {
          if (typeof compressedSizes[i] === "number" && chunkSize.known(id)) {
            const sum = chunkSize.get(id);
            this.check(compressedSizes[i] === sum, "totals.compressedSize", () => `${row} has compressedSize ${String(compressedSizes[i])}, and its column chunks add up to ${sum}`, at("compressedSize"));
          }
          // The totals of the columns of a table with no column chunk come from the table format, not from the chunks.
          if (kept > 0) {
            deepTables.add(id);
          }
        }
      }
    }

    const columnsLinked = chunks !== undefined && !this.badIds.has("columns") && !this.brokenFields.has("physicalColumns.column");
    const columns = columnsLinked ? this.joined("columns", ["id", "table", "compressedSize"], ["table"]) : undefined;
    if (columns !== undefined) {
      const columnIds = column(columns, "id");
      const owners = column(columns, "table");
      const sizes = column(columns, "compressedSize");
      for (let i = 0; i < columns.length; i++) {
        const id = columnIds[i];
        const owner = owners[i];
        const size = sizes[i];
        if (!isId(id) || !isId(owner) || !deepTables.has(owner) || typeof size !== "number" || !columnSize.known(id)) {
          continue;
        }
        const sum = columnSize.get(id);
        this.check(size === sum, "totals.columnCompressedSize", () => `The column with id ${id} has compressedSize ${size}, and its column chunks add up to ${sum}`, {
          table: "columns",
          field: "compressedSize",
        });
      }
    }
  }
}

/** Sums by id. A value that is not a number makes the sum of its id unknown. */
class Totals {
  private readonly sums = new Map<number, number>();
  private readonly unknown = new Set<number>();

  add(id: number, value: unknown): void {
    if (typeof value === "number") {
      this.sums.set(id, (this.sums.get(id) ?? 0) + value);
    } else {
      this.unknown.add(id);
    }
  }

  get(id: number): number {
    return this.sums.get(id) ?? 0;
  }

  known(id: number): boolean {
    return !this.unknown.has(id);
  }
}

function column(table: JoinedTable, field: string): unknown[] {
  return table.fields.get(field) ?? [];
}

// Maps the id of each row to the value of a reference field of the row.
function mapIds(table: JoinedTable, field: string): Map<number, number> {
  const ids = column(table, "id");
  const values = column(table, field);
  const map = new Map<number, number>();
  for (let i = 0; i < table.length; i++) {
    const id = ids[i];
    const value = values[i];
    if (isId(id) && isId(value) && !map.has(id)) {
      map.set(id, value);
    }
  }
  return map;
}

/** Whether a value is an id: an integer greater than 0. */
function isId(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value > 0;
}

// Counts the values, nulls left out, that fail a test, and the row of the first.
function tally(values: readonly unknown[], ok: (value: unknown) => boolean): { count: number; first: number } {
  let count = 0;
  let first = -1;
  for (let i = 0; i < values.length; i++) {
    const value = values[i];
    if (value !== null && value !== undefined && !ok(value)) {
      if (count++ === 0) {
        first = i;
      }
    }
  }
  return { count, first };
}

function describe(path: readonly string[]): string {
  const segments = path.filter((segment) => segment !== "[]");
  return segments.length === 0 ? "the top level" : segments.join(".");
}

// Where an issue found at a path of a document is: the table and the field for a part, a dotted path for the manifest.
function locate(part: string, path: readonly string[], name?: string): Where {
  const segments = (name === undefined ? [...path] : [...path, name]).filter((segment) => segment !== "[]");
  if (part === MANIFEST) {
    return segments.length === 0 ? { part } : { part, field: segments.join(".") };
  }
  const where: Where = { part };
  if (segments.length > 0) {
    where.table = segments[0]!;
  }
  if (segments.length > 1) {
    where.field = segments[1]!;
  }
  return where;
}
