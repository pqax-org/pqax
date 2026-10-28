// The three container samples of the kit, crafted byte by byte from the entries of sample.pqax: an encrypted entry, and
// two uses of ZIP64 that a reader does not need. The validator reports their codes, the reader opens them, and their
// bytes are what the kit says (the contract of the hardening before 1.0, section 5). Also the members of a producer in
// the part descriptors of extensions.pqax, which the reader keeps (section 1).
import { describe, expect, it } from "vitest";
import { PqaxError, openPqax, validate } from "@pqax/pqax";
import {
  DETAIL_TABLES, INVALID, SAMPLES, bytesOf, centralDirectory, dataOffset, documentShape, expectedByName, invalidCases,
  join, readEntries, shapeOf,
} from "./kit";
import { inflateRaw } from "./zip";

const EXPECTED = expectedByName();
const CONTAINER: Readonly<Record<string, string>> = {
  "container.encrypted.pqax": "container.encrypted",
  "container.zip64.pqax": "container.zip64",
  "container.zip64.extraField.pqax": "container.zip64",
};
const ZIP64 = ["container.zip64.pqax", "container.zip64.extraField.pqax"];

const END_RECORD = 0x06054b50;
const ZIP64_END_RECORD = 0x06064b50;
const ZIP64_LOCATOR = 0x07064b50;
const ZIP64_EXTRA = 0x0001;

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

// ---- a small zip parser, for what kit.ts does not read ----

function viewOf(bytes: Uint8Array): DataView {
  return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
}

/** Where the end of central directory record is, and its fields. */
function endRecord(bytes: Uint8Array) {
  const view = viewOf(bytes);
  for (let offset = bytes.length - 22; offset >= 0; offset--) {
    if (view.getUint32(offset, true) === END_RECORD) {
      return {
        offset, disk: view.getUint16(offset + 4, true), directoryDisk: view.getUint16(offset + 6, true),
        diskEntries: view.getUint16(offset + 8, true), entries: view.getUint16(offset + 10, true),
        size: view.getUint32(offset + 12, true), start: view.getUint32(offset + 16, true),
      };
    }
  }
  throw new Error("no end of central directory record");
}

/** For each entry of the central directory, its extra fields as [header id, length of the data]. */
function extraFields(bytes: Uint8Array): Map<string, [number, number][]> {
  const view = viewOf(bytes);
  const end = endRecord(bytes);
  const result = new Map<string, [number, number][]>();
  let p = end.start;
  for (let i = 0; i < end.entries; i++) {
    expect(view.getUint32(p, true)).toBe(0x02014b50);
    const nameLength = view.getUint16(p + 28, true);
    const extraLength = view.getUint16(p + 30, true);
    const commentLength = view.getUint16(p + 32, true);
    const name = new TextDecoder().decode(bytes.subarray(p + 46, p + 46 + nameLength));
    const fields: [number, number][] = [];
    let q = p + 46 + nameLength;
    const stop = q + extraLength;
    while (q + 4 <= stop) {
      const length = view.getUint16(q + 2, true);
      fields.push([view.getUint16(q, true), length]);
      q += 4 + length;
    }
    expect(q, name).toBe(stop);
    result.set(name, fields);
    p += 46 + nameLength + extraLength + commentLength;
  }
  return result;
}

/** The general purpose flags of the local header at an offset. */
function localFlags(bytes: Uint8Array, offset: number): number {
  const view = viewOf(bytes);
  expect(view.getUint32(offset, true)).toBe(0x04034b50);
  return view.getUint16(offset + 6, true);
}

describe("the container samples", () => {
  it("are in the kit with their codes", () => {
    const cases = invalidCases();
    for (const [file, code] of Object.entries(CONTAINER)) {
      expect(cases[file]?.codes, file).toContain(code);
    }
  });

  it.each(Object.keys(CONTAINER))("%s: the validator reports its code", async (file) => {
    for (const input of [invalid(file), new Blob([invalid(file)])]) {
      const result = await validate(input);
      expect(result.issues.map((issue) => issue.code)).toContain(CONTAINER[file]);
      expect(result.isValid).toBe(false);
    }
  });

  it.each(Object.keys(CONTAINER))("%s: the reader opens it", async (file) => {
    for (const input of [invalid(file), new Blob([invalid(file)])]) {
      const doc = await openPqax(input);
      expect(doc.manifest.format).toBe("pqax");
      expect(doc.rowCount("commits")).toBe(6);
    }
  });

  it("container.encrypted.pqax: every table but commits reads as in sample.pqax, and commits fails with container.encrypted", async () => {
    const doc = await openPqax(invalid("container.encrypted.pqax"));
    const plain = await openPqax(sample());
    expect(doc.manifest).toEqual(plain.manifest);
    expect(doc.summary).toEqual(plain.summary);
    for (const name of DETAIL_TABLES.filter((table) => table !== "commits")) {
      expect(await doc.readTable(name), name).toEqual(await plain.readTable(name));
    }
    expect(doc.rowCount("commits")).toBe(plain.rowCount("commits"));
    expect(await codeOf(doc.readTable("commits"))).toBe("container.encrypted");
    expect(await codeOf(doc.readTable("commits"))).toBe("container.encrypted");
    expect(await codeOf(doc.readRaw("commits"))).toBe("container.encrypted");
  });

  it.each(ZIP64)("%s reads as sample.pqax: its parts, rows, and shape", async (file) => {
    const doc = await openPqax(invalid(file));
    const plain = await openPqax(sample());
    expect(await documentShape(doc)).toEqual(shapeOf(EXPECTED.sample!));
    expect(doc.manifest).toEqual(plain.manifest);
    expect(doc.summary).toEqual(plain.summary);
    for (const name of DETAIL_TABLES) {
      expect(await doc.readTable(name), name).toEqual(await plain.readTable(name));
    }
  });
});

describe("the bytes of the container samples", () => {
  it("container.encrypted.pqax sets bit 0 of the flags of commits.json, and of no other entry", () => {
    const bytes = invalid("container.encrypted.pqax");
    const { entries } = centralDirectory(bytes);
    expect(entries.map((entry) => entry.name).sort()).toEqual(centralDirectory(sample()).entries.map((e) => e.name).sort());
    for (const entry of entries) {
      const encrypted = entry.name === "commits.json";
      expect((entry.flags & 1) === 1, `${entry.name}: central directory`).toBe(encrypted);
      expect((localFlags(bytes, entry.offset) & 1) === 1, `${entry.name}: local header`).toBe(encrypted);
    }
    const commits = entries.find((entry) => entry.name === "commits.json")!;
    expect(commits.method).toBe(8);
    // The data is the 12-byte encryption header, then the encrypted deflated data.
    expect(commits.compressedSize).toBeGreaterThan(12);
  });

  it("container.encrypted.pqax has the entries of sample.pqax, commits.json aside", async () => {
    const bytes = invalid("container.encrypted.pqax");
    const plain = await readEntries(sample());
    for (const entry of centralDirectory(bytes).entries.filter((e) => e.name !== "commits.json")) {
      const start = dataOffset(bytes, entry);
      const raw = bytes.subarray(start, start + entry.compressedSize);
      const data = entry.method === 0 ? raw.slice() : await inflateRaw(raw);
      expect(data, entry.name).toEqual(plain.get(entry.name));
    }
  });

  it.each(ZIP64)("%s has the entries of sample.pqax", async (file) => {
    const contents = await readEntries(invalid(file));
    const plain = await readEntries(sample());
    expect([...contents.keys()]).toEqual([...plain.keys()]);
    for (const [name, data] of plain) {
      expect(contents.get(name), name).toEqual(data);
    }
  });

  it("container.zip64.pqax has a ZIP64 end record and its locator before the end record, which keeps the real values", () => {
    const bytes = invalid("container.zip64.pqax");
    const view = viewOf(bytes);
    const end = endRecord(bytes);
    expect([end.disk, end.directoryDisk]).toEqual([0, 0]);
    expect([end.diskEntries, end.entries]).toEqual([7, 7]);
    expect(end.size).not.toBe(0xffffffff);
    expect(end.start).not.toBe(0xffffffff);
    const locator = end.offset - 20;
    expect(view.getUint32(locator, true)).toBe(ZIP64_LOCATOR);
    expect(view.getUint32(locator + 4, true)).toBe(0);
    const recordOffset = Number(view.getBigUint64(locator + 8, true));
    expect(view.getUint32(locator + 16, true)).toBe(1);
    expect(view.getUint32(recordOffset, true)).toBe(ZIP64_END_RECORD);
    const recordSize = Number(view.getBigUint64(recordOffset + 4, true));
    // The record follows the central directory and ends where the locator starts.
    expect(recordOffset).toBe(end.start + end.size);
    expect(recordOffset + 12 + recordSize).toBe(locator);
    expect(recordSize).toBeGreaterThanOrEqual(44);
    expect([view.getUint32(recordOffset + 16, true), view.getUint32(recordOffset + 20, true)]).toEqual([0, 0]);
    expect([
      Number(view.getBigUint64(recordOffset + 24, true)), Number(view.getBigUint64(recordOffset + 32, true)),
      Number(view.getBigUint64(recordOffset + 40, true)), Number(view.getBigUint64(recordOffset + 48, true)),
    ]).toEqual([end.diskEntries, end.entries, end.size, end.start]);
  });

  it("container.zip64.extraField.pqax gives commits.json a ZIP64 extra field with no data in the central directory", () => {
    const bytes = invalid("container.zip64.extraField.pqax");
    const fields = extraFields(bytes);
    expect(fields.get("commits.json")).toContainEqual([ZIP64_EXTRA, 0]);
    for (const [name, list] of fields) {
      if (name !== "commits.json") {
        expect(list.map(([id]) => id), name).not.toContain(ZIP64_EXTRA);
      }
    }
    // Its sizes and offset fit in 32 bits, so the field has nothing to hold.
    const commits = centralDirectory(bytes).entries.find((entry) => entry.name === "commits.json")!;
    expect([commits.compressedSize, commits.size, commits.offset]).not.toContain(0xffffffff);
  });

  it.each(["container.encrypted.pqax", "container.zip64.extraField.pqax"])("%s has no ZIP64 locator", (file) => {
    const bytes = invalid(file);
    const end = endRecord(bytes);
    // A locator would be the 20 bytes right before the end record, after the central directory.
    expect(end.offset).toBe(end.start + end.size);
    expect(viewOf(bytes).getUint32(end.offset - 20, true)).not.toBe(ZIP64_LOCATOR);
  });
});

describe("the part descriptors of extensions.pqax", () => {
  it("keep the members of a producer: acme_checksum on summary.json and files.json, acme_version on acme_extra.json", async () => {
    const doc = await openPqax(bytesOf(join(SAMPLES, "extensions.pqax")));
    const parts = new Map(doc.manifest.parts.map((part) => [part.name, part as unknown as Record<string, unknown>]));
    expect(parts.get("summary.json")).toMatchObject({ name: "summary.json", acme_checksum: "c3a1" });
    expect(Object.keys(parts.get("summary.json")!).sort()).toEqual(["acme_checksum", "name", "tables"]);
    expect(parts.get("files.json")).toEqual({ name: "files.json", tables: { files: 5 }, acme_checksum: "5f0c" });
    expect(parts.get("acme_extra.json")).toEqual({ name: "acme_extra.json", tables: { acme_details: 3 }, acme_version: 2 });
    for (const name of ["partitions.json", "rowGroups.json", "columnChunks.json", "commits.json"]) {
      expect(Object.keys(parts.get(name)!).sort(), name).toEqual(["name", "tables"]);
    }
  });
});
