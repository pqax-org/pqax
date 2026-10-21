// The validator of the TypeScript package against the conformance kit (conformance/README.md, "A validator test", and
// plan 4.1 and 4.4). node.test.ts adds fromFile of @pqax/pqax/node.
import { describe, expect, it } from "vitest";
import { embeddedSource, type PqaxInput, validate } from "@pqax/pqax";
import {
  CountingSource, EMBEDDED_PART, INVALID, PLAN_CODES, SAMPLES, bytesOf, expectedByName, invalidCases, join, json,
  rewriteZip, textOf, toBytes,
} from "./kit";

const EXPECTED = expectedByName();
const CASES = invalidCases();

/** A sample to validate: a VPAX through the footprint inside it, which is the PQAX file. */
async function sourceOf(path: string): Promise<PqaxInput> {
  return path.endsWith(".vpax") ? embeddedSource(bytesOf(path), EMBEDDED_PART) : bytesOf(path);
}

function codes(result: { issues: readonly { code: string }[] }): Set<string> {
  return new Set(result.issues.map((issue) => issue.code));
}

describe("valid samples", () => {
  it.each(Object.values(EXPECTED).map((expected) => expected.file))("%s has no issue", async (file) => {
    const result = await validate(await sourceOf(join(SAMPLES, file)));
    expect(result.issues.map((issue) => `${issue.code}: ${issue.message}`)).toEqual([]);
    expect(result.isValid).toBe(true);
    expect(result.checks).toBeGreaterThan(0);
  });
});

describe("invalid samples", () => {
  it.each(Object.keys(CASES))("%s reports at least its codes", async (file) => {
    const result = await validate(await sourceOf(join(INVALID, file)));
    const reported = codes(result);
    for (const code of CASES[file]!.codes) {
      expect(reported, `${code} is missing; reported ${[...reported].join(", ")}`).toContain(code);
    }
    expect(result.isValid).toBe(false);
  });

  it.each(Object.keys(CASES))("%s reports only codes of the kit", async (file) => {
    const result = await validate(await sourceOf(join(INVALID, file)));
    for (const code of codes(result)) {
      expect(PLAN_CODES.has(code), code).toBe(true);
    }
  });
});

describe("the validator", () => {
  it("lists the codes of rules.md, which are the codes of the plan", () => {
    const text = textOf("rules.md");
    const section = text.split("## 1. The codes")[1]!.split("\n## ")[0]!;
    const listed = new Set([...section.matchAll(/^\| `([A-Za-z0-9]+\.[A-Za-z0-9]+)` \|/gm)].map((match) => match[1]!));
    expect([...listed].sort()).toEqual([...PLAN_CODES].sort());
  });

  it("gives every issue a code, a message, and a place when one applies", async () => {
    const result = await validate(bytesOf(join(INVALID, "part.rowCount.array.pqax")));
    expect(result.issues.length).toBeGreaterThan(0);
    for (const issue of result.issues) {
      expect(typeof issue.message).toBe("string");
      expect(issue.message.length).toBeGreaterThan(0);
      for (const place of [issue.part, issue.table, issue.field]) {
        expect(place === undefined || place === null || typeof place === "string").toBe(true);
      }
    }
    const issue = result.issues.find((candidate) => candidate.code === "part.rowCount")!;
    expect(issue.part).toBe("commits.json");
    expect(issue.table).toBe("commits");
  });

  it("gives the same result from every kind of input", async () => {
    const bytes = bytesOf(join(INVALID, "totals.fileSize.pqax"));
    const results = [
      await validate(bytes),
      await validate(new Blob([bytes])),
      await validate(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength)),
      await validate(new CountingSource(bytes)),
    ];
    for (const result of results) {
      expect([...codes(result)]).toEqual(["totals.fileSize"]);
      expect(result.checks).toBe(results[0]!.checks);
    }
  });

  it("reports every issue, not only the first", async () => {
    const bytes = await rewriteZip(bytesOf(join(SAMPLES, "sample.pqax")), (name, data) => {
      const content = json(data);
      if (name === "summary.json") {
        content.tables.fileCount[0] += 1;
        return toBytes(content);
      }
      if (name === "files.json") {
        content.files.modifiedAt[0] = "2023-11-14T22:15:00Z";
        return toBytes(content);
      }
      if (name === "commits.json") {
        content.commits.commitId[2] = null;
        return toBytes(content);
      }
      return undefined;
    });
    const reported = codes(await validate(bytes));
    for (const code of ["totals.fileCount", "json.timestamp", "table.required"]) {
      expect(reported).toContain(code);
    }
  });

  it("accepts a valid sample written again by another zip writer", async () => {
    const bytes = await rewriteZip(bytesOf(join(SAMPLES, "sample.pqax")), () => undefined);
    const result = await validate(bytes);
    expect(result.issues.map((issue) => issue.code)).toEqual([]);
  });

  it("reports a byte order mark and reads the part on", async () => {
    const reported = codes(await validate(bytesOf(join(INVALID, "json.bom.pqax"))));
    expect(reported).toContain("json.bom");
    expect(reported).not.toContain("json.invalid");
  });

  it("does not throw for a file that is not a zip archive", async () => {
    const result = await validate(new TextEncoder().encode("not a zip archive"));
    expect(result.isValid).toBe(false);
    expect(codes(result)).toContain("container.manifestMissing");
  });

  it("validates the footprint of a deflated part through embeddedSource", async () => {
    const result = await validate(await embeddedSource(bytesOf(join(SAMPLES, "embedded-deflated.vpax"))));
    expect(result.isValid).toBe(true);
  });
});

describe("the fields of format 0.2", () => {
  it.each(["sample.pqax", "format-0.1.pqax"])("%s, of format 0.2 and of format 0.1, is valid", async (file) => {
    const result = await validate(bytesOf(join(SAMPLES, file)));
    expect(result.issues.map((issue) => `${issue.code}: ${issue.message}`)).toEqual([]);
    expect(result.isValid).toBe(true);
  });

  it.each(["json.type.unreferencedFileCount.pqax", "json.timestamp.nonLiveOldestRemovedAt.pqax",
    "json.timestamp.partitionLastModifiedAt.pqax"])("the invalid sample %s reports its codes", async (file) => {
    expect(Object.keys(CASES)).toContain(file);
    const reported = codes(await validate(bytesOf(join(INVALID, file))));
    for (const code of CASES[file]!.codes) {
      expect(reported).toContain(code);
    }
  });

  const WRONG: [string, string, string, unknown, string][] = [
    ["summary.json", "tables", "unreferencedFileCount", "1", "json.type"],
    ["summary.json", "tables", "unreferencedFileCount", true, "json.type"],
    ["summary.json", "tables", "unreferencedFileSize", "737", "json.type"],
    ["summary.json", "tables", "unreferencedFileSize", [737], "json.type"],
    ["summary.json", "tables", "nonLiveOldestRemovedAt", 1700000050000, "json.type"],
    ["summary.json", "tables", "nonLiveNewestRemovedAt", { at: "2023-11-14T22:14:10.000Z" }, "json.type"],
    ["partitions.json", "partitions", "lastModifiedAt", 1700000100000, "json.type"],
    ["summary.json", "tables", "nonLiveOldestRemovedAt", "2023-11-14T22:14:10Z", "json.timestamp"],
    ["summary.json", "tables", "nonLiveNewestRemovedAt", "2023-11-14 22:14:10.000", "json.timestamp"],
    ["partitions.json", "partitions", "lastModifiedAt", "2023-11-14T22:15:00.000+00:00", "json.timestamp"],
  ];

  it.each(WRONG)("%s: %s.%s of %j gives %s with its table and field", async (part, table, field, value, code) => {
    const bytes = await rewriteZip(bytesOf(join(SAMPLES, "sample.pqax")), (name, data) => {
      if (name !== part) {
        return undefined;
      }
      const content = json(data);
      content[table][field][0] = value;
      return toBytes(content);
    });
    const result = await validate(bytes);
    expect(result.isValid).toBe(false);
    const matching = result.issues.filter((issue) => issue.code === code);
    expect(matching.length, result.issues.map((issue) => issue.code).join(", ")).toBeGreaterThan(0);
    expect(matching.some((issue) => issue.table === table && issue.field === field && issue.part === part),
      JSON.stringify(matching)).toBe(true);
  });
});
