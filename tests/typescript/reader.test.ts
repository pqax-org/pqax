// The reader of the TypeScript package: what it reads and when, the facts of conformance/README.md that the expected
// files do not hold, the invalid samples, and rows and indexById (plan 4.4).
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { PqaxError, embeddedSource, indexById, openEmbedded, openPqax, rows, validate } from "pqax";
import {
  CountingSource, DETAIL_TABLES, EMBEDDED_PART, INVALID, REFUSED, SAMPLES, SUMMARY_TABLES, bytesOf, centralDirectory,
  computeShape, dataOffset, entrySpans, expectedByName, invalidCases, rewriteZip, shapeOf, touchedEntries,
} from "./kit";

const EXPECTED = expectedByName();
const PQAX_SAMPLES = Object.values(EXPECTED).filter((expected) => expected.embeddedPart === null).map((e) => e.file);

const sample = () => bytesOf(join(SAMPLES, "sample.pqax"));
const invalid = (name: string) => bytesOf(join(INVALID, name));

async function codeOf(promise: Promise<unknown>): Promise<string | undefined> {
  try {
    await promise;
  } catch (error) {
    expect(error).toBeInstanceOf(PqaxError);
    return (error as PqaxError).code;
  }
  throw new Error("the promise did not reject");
}

describe("what the reader reads", () => {
  it.each(PQAX_SAMPLES)("opening %s reads the manifest and the summary, and no detail part", async (file) => {
    const bytes = bytesOf(join(SAMPLES, file));
    const source = new CountingSource(bytes);
    await openPqax(source);
    expect([...touchedEntries(entrySpans(bytes), source.ranges)].sort()).toEqual(["manifest.json", "summary.json"]);
  });

  it("reads a detail table from its parts only, once", async () => {
    const bytes = bytesOf(join(SAMPLES, "multipart.pqax"));
    const source = new CountingSource(bytes);
    const doc = await openPqax(source);
    const spans = entrySpans(bytes);
    source.ranges.length = 0;
    expect((await doc.readTable("columnChunks")).length).toBe(52);
    expect([...touchedEntries(spans, source.ranges)].sort())
      .toEqual(["columnChunks.1.json", "columnChunks.2.json", "columnChunks.3.json"]);
    source.ranges.length = 0;
    expect((await doc.readTable("files")).length).toBe(5);
    expect([...touchedEntries(spans, source.ranges)].sort()).toEqual(["files.1.json", "files.2.json"]);
    source.ranges.length = 0;
    expect((await doc.readTable("columnChunks")).length).toBe(52);
    expect(source.ranges).toEqual([]);
  });

  it("opens a stored embedded footprint in place, reading no detail part of it", async () => {
    const bytes = bytesOf(join(SAMPLES, "embedded-stored.vpax"));
    const entry = centralDirectory(bytes).entries.find((e) => e.name === EMBEDDED_PART)!;
    expect(entry.method).toBe(0);
    const start = dataOffset(bytes, entry);
    const inner = bytes.subarray(start, start + entry.compressedSize);
    const source = new CountingSource(bytes);
    const doc = await openEmbedded(source);
    expect(doc.embeddedCompression).toBe("stored");
    const touched = touchedEntries(entrySpans(inner, start), source.ranges);
    expect([...touched].sort()).toEqual(["manifest.json", "summary.json"]);
  });
});

describe("the facts of conformance/README.md", () => {
  it("sample.pqax keeps known empty values", async () => {
    const doc = await openPqax(sample());
    expect((await doc.readTable("commits")).metrics![1]).toEqual({});
    expect(doc.summary.tables!.delta![1]!.readerFeatures).toEqual([]);
  });

  it("multipart.pqax joins the parts and fills the fields that a part leaves out with null", async () => {
    const doc = await openPqax(bytesOf(join(SAMPLES, "multipart.pqax")));
    const files = await doc.readTable("files");
    expect(files.deletionVectorSize).toEqual([40, null, null, null, null]);
    expect(files.partition).toEqual([1, 1, 2, null, null]);
    const whole = await openPqax(sample());
    for (const name of DETAIL_TABLES) {
      expect(await doc.readTable(name), name).toEqual(await whole.readTable(name));
    }
  });

  it("extensions.pqax: what this version does not know is left out of the typed tables and kept by readRaw", async () => {
    const doc = await openPqax(bytesOf(join(SAMPLES, "extensions.pqax")));
    const chunks = await doc.readTable("columnChunks");
    expect((chunks as unknown as Record<string, unknown>).encodings).toBeUndefined();
    const rawChunks = await doc.readRaw("columnChunks");
    expect(Array.isArray(rawChunks.encodings)).toBe(false);
    expect(rawChunks.encodings).toMatchObject({ dictionary: [["PLAIN"], ["PLAIN_DICTIONARY"]] });
    expect((doc.summary.tables as unknown as Record<string, unknown>).acme_score).toBeUndefined();
    expect((await doc.readRaw("tables")).acme_score).toEqual([7, 9]);
    expect((doc.manifest as unknown as Record<string, unknown>).acme_build).toEqual({ tool: "acme-writer", number: 7 });
    expect((await doc.readRaw("files")).acme_tier).toEqual(["hot", "hot", "hot", "hot", "hot"]);
    expect(doc.rowCount("acme_reviews")).toBe(2);
    expect((await doc.readRaw("acme_reviews")).score).toEqual([4, 3]);
    expect(doc.rowCount("acme_details")).toBe(3);
    expect((await doc.readRaw("acme_details")).label).toEqual(["a", "b", "c"]);
    const distribution = doc.summary.tables!.fileSizes![0]! as Record<string, unknown>;
    expect(distribution).toMatchObject({ count: 3, sum: 159121, median: 47820, ninetiethPercentile: 83049 });
    expect(doc.manifest.parts.map((part) => part.name)).not.toContain("acme_readme.txt");
    const plain = await openPqax(sample());
    for (const name of ["partitions", "rowGroups", "commits"] as const) {
      expect(await doc.readTable(name), name).toEqual(await plain.readTable(name));
    }
  });

  it("later-depth.pqax keeps a depth that this version does not define", async () => {
    const doc = await openPqax(bytesOf(join(SAMPLES, "later-depth.pqax")));
    expect(doc.summary.tables!.detailDepth).toEqual(["Pages", "Pages"]);
  });

  it("later-version.pqax is read: a later second number of the version", async () => {
    const doc = await openPqax(bytesOf(join(SAMPLES, "later-version.pqax")));
    expect(doc.manifest.formatVersion).toBe("0.9");
    expect((await doc.readTable("columnChunks")).length).toBe(52);
  });

  it.each(["embedded-stored.vpax", "embedded-deflated.vpax"])("%s holds the tables of sample.pqax", async (file) => {
    const doc = await openEmbedded(bytesOf(join(SAMPLES, file)));
    const plain = await openPqax(sample());
    expect(doc.embeddedPart).toBe(EMBEDDED_PART);
    expect(doc.manifest).toEqual(plain.manifest);
    expect(doc.summary).toEqual(plain.summary);
    for (const name of DETAIL_TABLES) {
      expect(await doc.readTable(name), name).toEqual(await plain.readTable(name));
    }
  });
});

describe("the invalid samples, read by a reader", () => {
  it.each(Object.entries(REFUSED))("refuses %s with %s", async (file, code) => {
    expect(await codeOf(openPqax(invalid(file)))).toBe(code);
  });

  it.each(Object.keys(invalidCases()).filter((file) => !(file in REFUSED)))("opens %s", async (file) => {
    const doc = await openPqax(invalid(file));
    expect(typeof doc.summary).toBe("object");
    expect(typeof doc.rowCount("tables")).toBe("number");
  });

  it("skips a byte order mark", async () => {
    const doc = await openPqax(invalid("json.bom.pqax"));
    const shape = computeShape(doc.manifest, (name) => doc.rowCount(name), {
      tables: doc.summary.tables, columnChunks: await doc.readTable("columnChunks"),
      physicalColumns: doc.summary.physicalColumns, physicalSchemas: doc.summary.physicalSchemas,
    });
    expect(shape).toEqual(shapeOf(EXPECTED.sample!));
  });

  it("fails only the read of a detail part that is not JSON", async () => {
    const doc = await openPqax(invalid("json.invalid.pqax"));
    expect(doc.summary.tables!.length).toBe(2);
    for (const name of ["partitions", "files", "rowGroups", "columnChunks"] as const) {
      expect((await doc.readTable(name)).length, name).toBe(EXPECTED.sample!.rows[name]);
    }
    expect(await codeOf(doc.readTable("commits"))).toBe("json.invalid");
  });

  it("fails only the read of a detail part that the archive does not have", async () => {
    const doc = await openPqax(invalid("manifest.partMissing.pqax"));
    expect((await doc.readTable("files")).length).toBe(5);
    expect(await codeOf(doc.readTable("commits"))).toBe("manifest.partMissing");
  });

  it("reads a value of the wrong JSON type as null, and readRaw keeps it", async () => {
    const doc = await openPqax(invalid("json.type.pqax"));
    expect((await doc.readTable("files")).footerSize![0]).toBeNull();
    expect((await doc.readRaw("files")).footerSize![0]).toBe("1139");
  });

  it("keeps a reference to a missing row as written, and indexById finds no row for it", async () => {
    const doc = await openPqax(invalid("table.reference.single.pqax"));
    const files = await doc.readTable("files");
    expect(files.writer).toEqual([99, 1, 2, 3, 3]);
    expect(indexById(doc.summary.writers as { id: number[] }).get(99)).toBeUndefined();
  });

  it("takes the rows of a part from its longest array", async () => {
    const stated = await openPqax(invalid("part.rowCount.manifest.pqax"));
    expect(stated.rowCount("commits")).toBe(7);
    expect((await stated.readTable("commits")).length).toBe(6);
    const short = await openPqax(invalid("part.rowCount.array.pqax"));
    const commits = await short.readTable("commits");
    expect(commits.length).toBe(6);
    expect(commits.engine!.length).toBe(6);
    expect(commits.engine![5]).toBeNull();
  });
});

describe("cases that the kit has no file for", () => {
  it("reads a listed summary.json that the archive does not have as empty summary tables", async () => {
    const bytes = rewriteZip(sample(), (name) => (name === "summary.json" ? null : undefined));
    const doc = await openPqax(bytes);
    for (const name of SUMMARY_TABLES) {
      const table = (doc.summary as Record<string, { length: number } | undefined>)[name];
      expect(table === undefined || table.length === 0, name).toBe(true);
    }
    expect((await doc.readTable("files")).length).toBe(5);
    const result = await validate(bytes);
    expect(result.issues.map((issue) => issue.code)).toContain("manifest.partMissing");
  });

  it("refuses a package without the footprint with embedded.partMissing", async () => {
    const vpax = bytesOf(join(SAMPLES, "embedded-stored.vpax"));
    const without = rewriteZip(vpax, (name) => (name === EMBEDDED_PART ? null : undefined));
    expect(await codeOf(openEmbedded(without))).toBe("embedded.partMissing");
    expect(await codeOf(embeddedSource(without))).toBe("embedded.partMissing");
    expect(await codeOf(openEmbedded(vpax, "Nothing.pqax"))).toBe("embedded.partMissing");
  });

  it("refuses a file that is not a zip archive with container.manifestMissing", async () => {
    expect(await codeOf(openPqax(new TextEncoder().encode("not a zip archive")))).toBe("container.manifestMissing");
  });
});

describe("rows and indexById", () => {
  it("rows gives one object for each row, with null for a missing value", async () => {
    const doc = await openPqax(bytesOf(join(SAMPLES, "multipart.pqax")));
    const tables = [...rows(doc.summary.tables!)];
    expect(tables.length).toBe(2);
    expect(tables[0]).toMatchObject({ id: 1, fileCount: 3, detailDepth: "Column chunks" });
    expect(tables[1]).toMatchObject({ id: 2, fileCount: 2, partitionCount: null });
    const files = [...rows(await doc.readTable("files"))];
    expect(files.map((file) => file.deletionVectorSize)).toEqual([40, null, null, null, null]);
    expect([...rows(await doc.readTable("deleteFiles"))]).toEqual([]);
  });

  it("indexById maps each id to its position", async () => {
    const doc = await openPqax(sample());
    const columns = indexById(doc.summary.columns as { id: number[] });
    expect(columns.size).toBe(10);
    expect(columns.get(1)).toBe(0);
    expect(columns.get(4)).toBe(3);
    expect(columns.get(99)).toBeUndefined();
    const chunks = await doc.readTable("columnChunks");
    const physical = indexById(doc.summary.physicalColumns as { id: number[] });
    for (const id of chunks.physicalColumn!) {
      expect(physical.has(id as number)).toBe(true);
    }
  });
});
