// The values of a partition: one string for each field of its spec, or null for a null partition value. The type of
// PartitionsTable says so, and the reader returns the nulls (the contract of the hardening before 1.0, section 4). The
// checks of expectTypeOf are made by the TypeScript compiler, as with vitest --typecheck; at run time they pass.
import { describe, expect, expectTypeOf, it } from "vitest";
import { openPqax, rows, validate, type PartitionRow, type PartitionsTable } from "@pqax/pqax";
import { SAMPLES, bytesOf, join, json, rewriteZip, toBytes } from "./kit";

type Values = NonNullable<PartitionsTable["values"]>;

describe("partition values", () => {
  it("are typed as a list of string or null for each partition, at compile time", () => {
    expectTypeOf<Values>().toEqualTypeOf<((string | null)[] | null)[]>();
    expectTypeOf<Values[number]>().toEqualTypeOf<(string | null)[] | null>();
    expectTypeOf<NonNullable<Values[number]>[number]>().toEqualTypeOf<string | null>();
    expectTypeOf<NonNullable<PartitionRow["values"]>[number]>().toEqualTypeOf<string | null>();
    expectTypeOf<NonNullable<Values[number]>[number]>().not.toEqualTypeOf<string>();
  });

  it("read a null partition value as null", async () => {
    const bytes = await rewriteZip(bytesOf(join(SAMPLES, "sample.pqax")), (name, data) => {
      if (name !== "partitions.json") {
        return undefined;
      }
      const content = json(data);
      content.partitions.values = [["2024", null], null];
      return toBytes(content);
    });
    const doc = await openPqax(bytes);
    const partitions = await doc.readTable("partitions");
    const values: Values | undefined = partitions.values;
    expect(values).toEqual([["2024", null], null]);
    expect([...rows(partitions)].map((row) => row.values)).toEqual([["2024", null], null]);
    expect((await validate(bytes)).issues.map((issue) => `${issue.code}: ${issue.message}`)).toEqual([]);
  });
});
