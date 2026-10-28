// tryOpenEmbedded: the footprint that a package holds, or null exactly when the package has no part of that name, and
// every other failure thrown as openEmbedded throws it (the contract of the hardening before 1.0, section 3).
import { describe, expect, expectTypeOf, it } from "vitest";
import { PqaxError, openEmbedded, openPqax, tryOpenEmbedded, type PqaxDocument, type PqaxInput } from "@pqax/pqax";
import {
  CountingSource, DETAIL_TABLES, EMBEDDED_PART, INVALID, SAMPLES, bytesOf, documentShape, expectedByName, join,
  readEntries, rewriteZip, shapeOf,
} from "./kit";
import { writeZip } from "./zip";

const EXPECTED = expectedByName();
const PACKAGES = { "embedded-stored.vpax": "stored", "embedded-deflated.vpax": "deflated" } as const;
const INPUTS = ["Uint8Array", "ArrayBuffer", "Blob", "ByteSource"] as const;
type InputKind = (typeof INPUTS)[number];

function inputOf(bytes: Uint8Array<ArrayBuffer>, kind: InputKind): PqaxInput {
  switch (kind) {
    case "Uint8Array":
      return bytes;
    case "ArrayBuffer":
      return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
    case "Blob":
      return new Blob([bytes]);
    case "ByteSource":
      return new CountingSource(bytes);
  }
}

async function codeOf(promise: Promise<unknown>): Promise<string | undefined> {
  try {
    await promise;
  } catch (error) {
    expect(error).toBeInstanceOf(PqaxError);
    return (error as PqaxError).code;
  }
  throw new Error("the promise did not reject");
}

/** A VPAX-like package: the entries of embedded-stored.vpax, with these bytes as Footprint.pqax, or without it. */
async function packageWith(footprint: Uint8Array | null): Promise<Uint8Array<ArrayBuffer>> {
  const entries: [string, Uint8Array][] = [];
  for (const [name, data] of await readEntries(bytesOf(join(SAMPLES, "embedded-stored.vpax")))) {
    if (name !== EMBEDDED_PART) {
      entries.push([name, data]);
    } else if (footprint !== null) {
      entries.push([name, footprint]);
    }
  }
  return writeZip(entries, 0);
}

describe("tryOpenEmbedded", () => {
  it("is exported by the main entry, with the signature of the contract", () => {
    expect(typeof tryOpenEmbedded).toBe("function");
    expectTypeOf(tryOpenEmbedded).parameter(0).toEqualTypeOf<PqaxInput>();
    expectTypeOf(tryOpenEmbedded).parameter(1).toEqualTypeOf<string | undefined>();
    expectTypeOf(tryOpenEmbedded).returns.toEqualTypeOf<Promise<PqaxDocument | null>>();
  });

  describe.each(Object.keys(PACKAGES) as (keyof typeof PACKAGES)[])("on %s", (file) => {
    it.each(INPUTS)("gives the document of sample.pqax from a %s", async (kind) => {
      const doc = await tryOpenEmbedded(inputOf(bytesOf(join(SAMPLES, file)), kind));
      expect(doc).not.toBeNull();
      const plain = await openPqax(bytesOf(join(SAMPLES, "sample.pqax")));
      expect(doc!.embeddedPart).toBe(EMBEDDED_PART);
      expect(doc!.embeddedCompression).toBe(PACKAGES[file]);
      expect(doc!.manifest).toEqual(plain.manifest);
      expect(doc!.summary).toEqual(plain.summary);
      for (const name of DETAIL_TABLES) {
        expect(await doc!.readTable(name), name).toEqual(await plain.readTable(name));
      }
      expect(await documentShape(doc!)).toEqual(shapeOf(EXPECTED.sample!));
    });

    it.each(["Footprint.pqax", "/Footprint.pqax", "footprint.PQAX", "/FOOTPRINT.pqax"])(
      "finds the part named %s as openEmbedded finds it", async (partName) => {
        const bytes = bytesOf(join(SAMPLES, file));
        const doc = await tryOpenEmbedded(bytes, partName);
        expect(doc).not.toBeNull();
        expect(doc!.embeddedPart).toBe((await openEmbedded(bytes, partName)).embeddedPart);
        expect(doc!.embeddedPart).toBe(EMBEDDED_PART);
        expect(doc!.rowCount("columnChunks")).toBe(52);
      });

    it("gives null for a part name that the package does not have", async () => {
      for (const kind of INPUTS) {
        expect(await tryOpenEmbedded(inputOf(bytesOf(join(SAMPLES, file)), kind), "Nothing.pqax"), kind).toBeNull();
      }
    });
  });

  it.each(INPUTS)("gives null for a package without Footprint.pqax, from a %s", async (kind) => {
    const without = await packageWith(null);
    expect(await tryOpenEmbedded(inputOf(without, kind))).toBeNull();
    expect(await tryOpenEmbedded(inputOf(without, kind), EMBEDDED_PART)).toBeNull();
  });

  it("gives null for the package of the kit without its footprint", async () => {
    const vpax = bytesOf(join(SAMPLES, "embedded-deflated.vpax"));
    const without = await rewriteZip(vpax, (name) => (name === EMBEDDED_PART ? null : undefined));
    expect(await tryOpenEmbedded(without)).toBeNull();
  });

  it("leaves openEmbedded as it was: a package without the part is refused with embedded.partMissing", async () => {
    const without = await packageWith(null);
    expect(await codeOf(openEmbedded(without))).toBe("embedded.partMissing");
    expect(await codeOf(openEmbedded(bytesOf(join(SAMPLES, "embedded-stored.vpax")), "Nothing.pqax")))
      .toBe("embedded.partMissing");
  });

  it("throws container.manifestMissing for a package that is not a zip archive", async () => {
    const bytes = new TextEncoder().encode("not a zip archive");
    expect(await codeOf(tryOpenEmbedded(bytes))).toBe("container.manifestMissing");
    expect(await codeOf(tryOpenEmbedded(new Blob([bytes])))).toBe("container.manifestMissing");
    expect(await codeOf(openEmbedded(bytes))).toBe("container.manifestMissing");
  });

  it.each([
    ["a footprint that is not a zip archive", null, "container.manifestMissing"],
    ["a footprint without manifest.json", "container.manifestMissing.pqax", "container.manifestMissing"],
    ["a footprint whose format is vpax", "manifest.format.pqax", "manifest.format"],
    ["a footprint of version 9.0", "manifest.versionUnknown.pqax", "manifest.versionUnknown"],
  ] as const)("throws as openEmbedded throws for %s", async (_case, sample, code) => {
    const footprint = sample === null ? new TextEncoder().encode("not a zip archive") : bytesOf(join(INVALID, sample));
    const bytes = await packageWith(footprint);
    expect(await codeOf(openEmbedded(bytes))).toBe(code);
    for (const kind of INPUTS) {
      expect(await codeOf(tryOpenEmbedded(inputOf(bytes, kind))), kind).toBe(code);
    }
  });
});
