// fromFile of @pqax/pqax/node, which reads a file on disk by range: the checks of conformance.test.ts and validate.test.ts,
// from the files of the kit on disk. This file needs Node, so vitest.browser.config.ts leaves it out.
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { openEmbedded, openPqax, validate } from "@pqax/pqax";
import { fromFile } from "@pqax/pqax/node";
import { INVALID, KIT_DIR, SAMPLES, bytesOf, documentShape, expectedByName, shapeOf } from "./kit";

const EXPECTED = expectedByName();

describe("fromFile", () => {
  it.each(Object.keys(EXPECTED))("gives the expected results of sample %s", async (name) => {
    const expected = EXPECTED[name]!;
    const source = await fromFile(join(KIT_DIR, SAMPLES, expected.file));
    const doc = expected.embeddedPart === null ? await openPqax(source) : await openEmbedded(source, expected.embeddedPart);
    expect(await documentShape(doc)).toEqual(shapeOf(expected));
  });

  it("gives the validation result of the other inputs", async () => {
    const file = "totals.fileSize.pqax";
    const result = await validate(await fromFile(join(KIT_DIR, INVALID, file)));
    expect([...new Set(result.issues.map((issue) => issue.code))]).toEqual(["totals.fileSize"]);
    expect(result.checks).toBe((await validate(bytesOf(`${INVALID}/${file}`))).checks);
  });
});
