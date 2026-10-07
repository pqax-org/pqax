// Smoke test of the built package (npm run build first). It prints identifiers and numbers, never a name of the data.
//
//   node scripts/smoke.mjs [file.pqax ...]   opens each file from disk and from memory, reads it, and validates it
//   node scripts/smoke.mjs --kit [folder]    runs the conformance kit, by default ../../../conformance
//
// With the kit, every valid sample must give its expected results and no issue, every invalid sample must report the
// codes of samples/invalid/expected.json, and opening a sample must read no detail part. The exit code is 1 on a failure.
import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { detailTableNames, embeddedSource, indexById, openEmbedded, openPqax, PqaxError, summaryTableNames, validate } from "pqax";
import { fromFile } from "pqax/node";
import { readZip } from "../dist/zip.js";

const args = process.argv.slice(2);
let failures = 0;
const fail = (message) => {
  failures++;
  console.log(`   FAIL ${message}`);
};
const sum = (values) => (values ?? []).reduce((total, value) => total + (typeof value === "number" ? value : 0), 0);

if (args[0] === "--kit") {
  await runKit(args[1] ?? fileURLToPath(new URL("../../../../conformance", import.meta.url)));
} else {
  for (const path of args) {
    await smoke(path);
  }
}
console.log(failures === 0 ? "smoke: all checks passed" : `smoke: ${failures} failures`);
process.exit(failures === 0 ? 0 : 1);

async function smoke(path) {
  console.log(`${path}`);
  for (const [how, input] of [["fromFile", await fromFile(path)], ["Uint8Array", new Uint8Array(await readFile(path))]]) {
    const doc = await openPqax(input);
    console.log(`   ${how}: ${doc.manifest.format} ${doc.manifest.formatVersion}, parts ${doc.manifest.parts.map((part) => part.name).join(", ")}`);
    const names = [...new Set(doc.manifest.parts.flatMap((part) => Object.keys(part.tables)))];
    console.log(`   rows: ${names.map((name) => `${name} ${doc.rowCount(name)}`).join(", ")}`);
    const chunks = await doc.readTable("columnChunks");
    console.log(`   columnChunks: ${chunks.length} rows, compressedSize ${sum(chunks.compressedSize)}, tables.compressedSize ${sum(doc.summary.tables?.compressedSize)}`);
  }
  const result = await validate(await fromFile(path));
  console.log(`   validate: ${result.isValid ? "valid" : "invalid"}, ${result.checks} checks, ${result.issues.length} issues`);
  for (const issue of result.issues) {
    console.log(`      ${issue.code}: ${issue.message}`);
  }
}

async function runKit(kit) {
  console.log(`kit ${kit}`);
  const samples = join(kit, "samples");
  const expectedFolder = join(kit, "expected");
  for (const name of (await readdir(expectedFolder)).filter((file) => file.endsWith(".json")).sort()) {
    const expected = JSON.parse(await readFile(join(expectedFolder, name), "utf8"));
    const path = join(samples, expected.file);
    const actual = await results(path, expected.embeddedPart);
    const differences = compare(expected, actual);
    const embedded = expected.embeddedPart === null ? "" : `, embedded ${actual.embeddedCompression}`;
    console.log(`${differences.length === 0 ? "ok  " : "FAIL"} ${expected.file}: ${actual.parts.length} parts, ${actual.rows.columnChunks ?? 0} column chunks, compressedSize ${actual.columnChunksCompressedSize}${embedded}`);
    for (const difference of differences) {
      fail(`${expected.file}: ${difference}`);
    }
    const source = expected.embeddedPart === null ? await fromFile(path) : await embeddedSource(await fromFile(path), expected.embeddedPart);
    const result = await validate(source);
    if (!result.isValid) {
      fail(`${expected.file} is valid, and the validator reports ${result.issues.map((issue) => issue.code).join(", ")}`);
    }
    if (expected.embeddedPart === null) {
      await checkOpenReads(path);
    }
  }
  await checkInvalid(join(samples, "invalid"));
  await checkReaderOnInvalid(join(samples, "invalid"), await results(join(samples, "sample.pqax"), null));
}

// What a reader must get from a sample, computed from the public API, in the shape of expected/<sample>.json.
async function results(path, embeddedPart) {
  const doc = embeddedPart === null ? await openPqax(await fromFile(path)) : await openEmbedded(await fromFile(path), embeddedPart);
  const names = [...new Set(doc.manifest.parts.flatMap((part) => Object.keys(part.tables)))];
  const rows = {};
  for (const name of names) {
    rows[name] = doc.rowCount(name);
    const raw = await doc.readRaw(name);
    for (const [field, values] of Object.entries(raw)) {
      if (Array.isArray(values) && values.length !== rows[name]) {
        fail(`${path}: ${name}.${field} has ${values.length} values, and the table has ${rows[name]} rows`);
      }
    }
    const typed = await doc.readTable(name);
    if (typed.length !== rows[name]) {
      fail(`${path}: readTable(${name}) has length ${typed.length}, and the manifest states ${rows[name]} rows`);
    }
  }
  for (const name of [...summaryTableNames, ...detailTableNames].filter((table) => !names.includes(table))) {
    const typed = await doc.readTable(name);
    if (doc.rowCount(name) !== 0 || typed.length !== 0 || Object.keys(typed).length !== 1 || doc.summary[name] !== undefined) {
      fail(`${path}: ${name} is not in the file, and the reader gives it rows or fields`);
    }
  }
  const tables = doc.summary.tables ?? { length: 0 };
  const chunks = await doc.readTable("columnChunks");
  const physical = doc.summary.physicalColumns ?? { length: 0 };
  const schemas = doc.summary.physicalSchemas ?? { length: 0 };
  const physicalIndex = indexById(physical);
  const schemaIndex = indexById(schemas);
  const byTable = new Map();
  const byColumn = new Map();
  for (let i = 0; i < chunks.length; i++) {
    const leaf = physicalIndex.get(chunks.physicalColumn?.[i]);
    if (leaf === undefined) {
      continue;
    }
    const size = chunks.compressedSize?.[i] ?? 0;
    const schema = schemaIndex.get(physical.physicalSchema?.[leaf]);
    const table = schema === undefined ? undefined : schemas.table?.[schema];
    if (table !== undefined && table !== null) {
      const totals = byTable.get(table) ?? { count: 0, size: 0 };
      byTable.set(table, { count: totals.count + 1, size: totals.size + size });
    }
    const column = physical.column?.[leaf];
    if (column !== undefined && column !== null) {
      byColumn.set(column, (byColumn.get(column) ?? 0) + size);
    }
  }
  const largestColumns = [...byColumn]
    .sort((a, b) => b[1] - a[1] || a[0] - b[0])
    .slice(0, 3)
    .map(([id, compressedSize]) => ({ id, compressedSize }));
  return {
    file: path.split(/[\\/]/).pop(),
    embeddedPart: doc.embeddedPart,
    embeddedCompression: doc.embeddedCompression,
    format: doc.manifest.format,
    formatVersion: doc.manifest.formatVersion,
    parts: doc.manifest.parts.map((part) => part.name),
    rows,
    tablesFileSize: sum(tables.fileSize),
    tablesRowCount: sum(tables.rowCount),
    columnChunksCompressedSize: sum(chunks.compressedSize),
    tables: Array.from({ length: tables.length }, (_, i) => ({
      id: tables.id[i],
      fileCount: tables.fileCount?.[i] ?? null,
      columnChunkCount: byTable.get(tables.id[i])?.count ?? 0,
      columnChunkCompressedSize: byTable.get(tables.id[i])?.size ?? 0,
    })),
    largestColumns,
  };
}

function compare(expected, actual, path = "") {
  if (typeof expected !== "object" || expected === null || typeof actual !== "object" || actual === null) {
    return Object.is(expected, actual) ? [] : [`${path || "value"}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`];
  }
  const keys = new Set([...Object.keys(expected), ...Object.keys(actual)]);
  return [...keys].flatMap((key) => compare(expected[key], actual[key], path ? `${path}.${key}` : key));
}

// Opening a sample reads the zip directory, manifest.json, and summary.json, and no byte of a detail part.
async function checkOpenReads(path) {
  const file = await fromFile(path);
  const reads = [];
  const counting = { size: file.size, read: (offset, length) => (reads.push([offset, offset + length]), file.read(offset, length)) };
  const doc = await openPqax(counting);
  const archive = await readZip(file);
  for (const part of doc.manifest.parts.filter((part) => part.name !== "summary.json")) {
    const entry = archive.find(part.name);
    const start = entry.localHeaderOffset;
    const end = start + entry.expectedHeaderLength + entry.compressedSize;
    if (reads.some(([from, to]) => from < end && to > start)) {
      fail(`${path}: opening the file reads ${part.name}`);
    }
  }
  console.log(`     opening ${path.split(/[\\/]/).pop()} takes ${reads.length} reads of ${reads.reduce((total, [from, to]) => total + to - from, 0)} bytes`);
}

async function checkInvalid(folder) {
  const expected = JSON.parse(await readFile(join(folder, "expected.json"), "utf8"));
  let passed = 0;
  for (const [file, { codes }] of Object.entries(expected)) {
    const result = await validate(await fromFile(join(folder, file)));
    const reported = [...new Set(result.issues.map((issue) => issue.code))];
    const missing = codes.filter((code) => !reported.includes(code));
    const extra = reported.filter((code) => !codes.includes(code));
    if (missing.length > 0) {
      fail(`${file}: the validator does not report ${missing.join(", ")}; it reports ${reported.join(", ") || "nothing"}`);
    } else {
      passed++;
    }
    console.log(`${missing.length === 0 ? "ok  " : "FAIL"} invalid/${file}: ${reported.join(", ")}${extra.length > 0 ? `  (more than expected: ${extra.join(", ")})` : ""}`);
  }
  console.log(`invalid samples: ${passed} of ${Object.keys(expected).length} report their codes`);
}

// The reader refuses three invalid files, reads json.bom.pqax as sample.pqax, and fails on json.invalid.pqax only when it
// reads commits. It opens the other invalid files.
async function checkReaderOnInvalid(folder, sampleResults) {
  const refused = ["container.manifestMissing.pqax", "manifest.format.pqax", "manifest.versionUnknown.pqax"];
  for (const file of (await readdir(folder)).filter((name) => name.endsWith(".pqax")).sort()) {
    const path = join(folder, file);
    try {
      await openPqax(await fromFile(path));
      if (refused.includes(file)) {
        fail(`the reader opens ${file}, which it must refuse`);
      }
    } catch (error) {
      const code = error instanceof PqaxError ? error.code : String(error);
      if (!refused.includes(file) || code !== file.replace(/\.pqax$/, "")) {
        fail(`the reader refuses ${file} with ${code}`);
      }
    }
  }
  const bom = await results(join(folder, "json.bom.pqax"), null);
  const differences = compare({ ...sampleResults, file: "json.bom.pqax" }, bom);
  if (differences.length > 0) {
    fail(`json.bom.pqax does not give the results of sample.pqax: ${differences.join("; ")}`);
  }
  const doc = await openPqax(await fromFile(join(folder, "json.invalid.pqax")));
  for (const table of ["partitions", "files", "rowGroups", "columnChunks"]) {
    await doc.readTable(table);
  }
  try {
    await doc.readTable("commits");
    fail("the reader reads commits from json.invalid.pqax");
  } catch (error) {
    if (!(error instanceof PqaxError) || error.code !== "json.invalid") {
      fail(`reading commits from json.invalid.pqax fails with ${error}`);
    }
  }
  console.log("reader on invalid samples: checked");
}
