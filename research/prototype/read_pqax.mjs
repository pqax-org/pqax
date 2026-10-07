// Reads a PQAX file with plain JavaScript and times each step. It prints identifiers and numbers, never a name.
// Usage: node read_pqax.mjs <file.pqax>
//        node read_pqax.mjs <file.vpax>     reads the PQAX file that the VPAX embeds
import { openSync, readSync, fstatSync, closeSync } from 'node:fs';
import { inflateRawSync } from 'node:zlib';
import { performance } from 'node:perf_hooks';

// A range of bytes that holds a zip: a whole file, a piece of a file, or a buffer in memory.
function fileRange(fd, base, size) {
  return {
    size,
    read(offset, length) {
      const buffer = Buffer.alloc(length);
      readSync(fd, buffer, 0, length, base + offset);
      return buffer;
    },
  };
}
const bufferRange = (buffer) => ({ size: buffer.length, read: (offset, length) => buffer.subarray(offset, offset + length) });

// The directory at the end of a zip says where each entry is, so an entry is read only when it is needed.
function openZip(range) {
  const tailLength = Math.min(range.size, 65557);
  const tail = range.read(range.size - tailLength, tailLength);
  let end = tail.length - 22;
  while (tail.readUInt32LE(end) !== 0x06054b50) end--;
  const count = tail.readUInt16LE(end + 10);
  const directory = range.read(tail.readUInt32LE(end + 16), tail.readUInt32LE(end + 12));
  const entries = new Map();
  let p = 0;
  for (let i = 0; i < count; i++) {
    const nameLength = directory.readUInt16LE(p + 28);
    entries.set(directory.toString('utf8', p + 46, p + 46 + nameLength), {
      method: directory.readUInt16LE(p + 10),
      compressedSize: directory.readUInt32LE(p + 20),
      offset: directory.readUInt32LE(p + 42),
    });
    p += 46 + nameLength + directory.readUInt16LE(p + 30) + directory.readUInt16LE(p + 32);
  }
  // The bytes of an entry start after its local header.
  const dataStart = (entry) => {
    const header = range.read(entry.offset, 30);
    return entry.offset + 30 + header.readUInt16LE(26) + header.readUInt16LE(28);
  };
  const bytes = (name) => {
    const entry = entries.get(name);
    const data = range.read(dataStart(entry), entry.compressedSize);
    return entry.method === 0 ? data : inflateRawSync(data);
  };
  return { entries, dataStart, bytes, json: (name) => JSON.parse(bytes(name).toString('utf8')) };
}

const ms = (start) => `${(performance.now() - start).toFixed(1)} ms`;
const number = (value) => value.toLocaleString('en-US');
const sum = (values) => (values ?? []).reduce((a, b) => a + (b ?? 0), 0);

let start = performance.now();
const fd = openSync(process.argv[2], 'r');
let zip = openZip(fileRange(fd, 0, fstatSync(fd).size));
if (!zip.entries.has('manifest.json')) {
  // A VPAX: the PQAX file is one part. A stored part is a piece of the outer file, and a deflated part is inflated in memory.
  const name = [...zip.entries.keys()].find((n) => n.endsWith('.pqax'));
  const entry = zip.entries.get(name);
  const stored = entry.method === 0;
  zip = openZip(stored ? fileRange(fd, zip.dataStart(entry), entry.compressedSize) : bufferRange(zip.bytes(name)));
  console.log(`embedded part ${name}: ${stored ? 'stored, read in place' : 'deflated, inflated in memory'}`);
}
const manifest = zip.json('manifest.json');
if (manifest.format !== 'pqax' || !manifest.formatVersion.startsWith('0.')) throw new Error('Not a PQAX file of a version that this reader knows');
const summary = zip.json('summary.json');
console.log(`${manifest.format} ${manifest.formatVersion}: directory, manifest and summary read in ${ms(start)}`);
console.log(`summary tables: ${Object.entries(manifest.parts[0].tables).map(([name, rows]) => `${name} ${rows}`).join(', ')}`);
const tables = summary.tables;
console.log(`totals from the summary alone: ${tables.id.length} tables, ${number(sum(tables.fileCount))} files, ` +
  `${(sum(tables.fileSize) / 2 ** 30).toFixed(2)} GB, ${number(sum(tables.rowCount))} rows`);

// Loads a table from every part that holds rows of it, in the order of the manifest.
// A field is an array with one value per row. Anything else is ignored, and a field missing from a part has no values there.
function loadTable(name) {
  const pieces = manifest.parts.filter((part) => part.tables[name]).map((part) => [zip.json(part.name)[name], part.tables[name]]);
  const names = new Set(pieces.flatMap(([table]) => Object.keys(table).filter((field) => Array.isArray(table[field]))));
  const fields = {};
  for (const field of names) {
    fields[field] = [].concat(...pieces.map(([table, rows]) => (Array.isArray(table[field]) ? table[field] : new Array(rows).fill(null))));
  }
  return fields;
}

for (const name of new Set(manifest.parts.slice(1).flatMap((part) => Object.keys(part.tables)))) {
  start = performance.now();
  const table = loadTable(name);
  const parts = manifest.parts.filter((part) => part.tables[name]);
  console.log(`detail ${name}: ${number(sum(parts.map((part) => part.tables[name])))} rows in ${parts.length} part(s), ` +
    `${Object.keys(table).length} fields, read in ${ms(start)}`);

  if (name === 'columnChunks') {
    // Follow the references from each column chunk up to its column, to show that the tables join.
    start = performance.now();
    const columnOf = new Map(summary.physicalColumns.id.map((id, i) => [id, summary.physicalColumns.column?.[i] ?? null]));
    const sizeByColumn = new Map();
    for (let i = 0; i < table.physicalColumn.length; i++) {
      const column = columnOf.get(table.physicalColumn[i]);
      sizeByColumn.set(column, (sizeByColumn.get(column) ?? 0) + table.compressedSize[i]);
    }
    const top = [...sizeByColumn].sort((a, b) => b[1] - a[1]).slice(0, 3);
    console.log(`   size by column from the column chunks, in ${ms(start)}: ` +
      top.map(([column, size]) => `column ${column} ${(size / 2 ** 20).toFixed(0)} MB`).join(', '));
  }
}
closeSync(fd);
