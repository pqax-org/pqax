// The reader of the TypeScript package against the expected results of the conformance kit (conformance/README.md,
// "A reader test", and plan 4.4), from every kind of input that the package accepts.
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { openEmbedded, openPqax, type PqaxDocument, type PqaxInput } from "pqax";
import { fromFile } from "pqax/node";
import {
  CountingSource, DETAIL_TABLES, type Expected, FORMAT_TABLES, SAMPLES, SUMMARY_TABLES, bytesOf, computeShape,
  expectedByName, fieldsOf, shapeOf,
} from "./kit";

const EXPECTED = expectedByName();
const INPUTS = ["Uint8Array", "Uint8Array at an offset", "ArrayBuffer", "Blob", "ByteSource", "fromFile"] as const;
type InputKind = (typeof INPUTS)[number];

async function inputOf(path: string, kind: InputKind): Promise<PqaxInput> {
  const bytes = bytesOf(path);
  switch (kind) {
    case "Uint8Array":
      return bytes;
    case "Uint8Array at an offset": {
      const buffer = new ArrayBuffer(bytes.length + 13);
      const view = new Uint8Array(buffer, 7, bytes.length);
      view.set(bytes);
      return view;
    }
    case "ArrayBuffer":
      return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
    case "Blob":
      return new Blob([bytes]);
    case "ByteSource":
      return new CountingSource(bytes);
    case "fromFile":
      return fromFile(path);
  }
}

async function openSample(expected: Expected, kind: InputKind = "Uint8Array"): Promise<PqaxDocument> {
  const input = await inputOf(join(SAMPLES, expected.file), kind);
  return expected.embeddedPart === null ? openPqax(input) : openEmbedded(input, expected.embeddedPart);
}

/** A table read whole: a summary table from doc.summary, a detail table with readTable, another with readRaw. */
async function tableOf(doc: PqaxDocument, name: string): Promise<unknown> {
  if ((SUMMARY_TABLES as readonly string[]).includes(name)) {
    return (doc.summary as Record<string, unknown>)[name];
  }
  if ((DETAIL_TABLES as readonly string[]).includes(name)) {
    return doc.readTable(name as (typeof DETAIL_TABLES)[number]);
  }
  return doc.readRaw(name);
}

async function shapeOfDocument(doc: PqaxDocument) {
  return computeShape(doc.manifest, (name) => doc.rowCount(name), {
    tables: doc.summary.tables,
    columnChunks: await doc.readTable("columnChunks"),
    physicalColumns: doc.summary.physicalColumns,
    physicalSchemas: doc.summary.physicalSchemas,
  });
}

describe.each(Object.keys(EXPECTED))("sample %s", (name) => {
  const expected = EXPECTED[name]!;

  it.each(INPUTS)("gives the expected results from a %s", async (kind) => {
    const doc = await openSample(expected, kind);
    expect(await shapeOfDocument(doc)).toEqual(shapeOf(expected));
  });

  it("states the embedded part and its compression", async () => {
    const doc = await openSample(expected);
    expect(doc.embeddedPart).toBe(expected.embeddedPart);
    expect(doc.embeddedCompression).toBe(expected.embeddedCompression);
  });

  it("has every row of every table, and no row of the tables it does not have", async () => {
    const doc = await openSample(expected);
    for (const [table, count] of Object.entries(expected.rows)) {
      expect(doc.rowCount(table), table).toBe(count);
      const read = await tableOf(doc, table);
      const fields = fieldsOf(read);
      expect(Object.keys(fields).length, `${table} has no field`).toBeGreaterThan(0);
      for (const [field, values] of Object.entries(fields)) {
        expect(values.length, `${table}.${field}`).toBe(count);
      }
      if (FORMAT_TABLES.includes(table)) {
        expect((read as { length: number }).length, table).toBe(count);
      }
    }
    for (const table of FORMAT_TABLES) {
      if (table in expected.rows) {
        continue;
      }
      expect(doc.rowCount(table), table).toBe(0);
      if ((SUMMARY_TABLES as readonly string[]).includes(table)) {
        expect((doc.summary as Record<string, unknown>)[table], table).toBeUndefined();
      } else {
        expect(await doc.readTable(table as (typeof DETAIL_TABLES)[number]), table).toEqual({ length: 0 });
      }
    }
  });
});
