// Measures what plain JavaScript needs to read the parts of one table of PQAX files:
// the time to inflate, the time to decode and parse, and the memory that the parsed result holds.
// Usage: node --expose-gc measure_parts.mjs <table> <file.pqax> [<file.pqax> ...]
import { readFileSync } from 'node:fs';
import { inflateRawSync } from 'node:zlib';
import { performance } from 'node:perf_hooks';

function openZip(file) {
  let end = file.length - 22;
  while (file.readUInt32LE(end) !== 0x06054b50) end--;
  const entries = new Map();
  let p = file.readUInt32LE(end + 16);
  for (let i = 0; i < file.readUInt16LE(end + 10); i++) {
    const nameLength = file.readUInt16LE(p + 28);
    entries.set(file.toString('utf8', p + 46, p + 46 + nameLength), {
      method: file.readUInt16LE(p + 10), compressedSize: file.readUInt32LE(p + 20), offset: file.readUInt32LE(p + 42),
    });
    p += 46 + nameLength + file.readUInt16LE(p + 30) + file.readUInt16LE(p + 32);
  }
  return (name) => {
    const entry = entries.get(name);
    const start = entry.offset + 30 + file.readUInt16LE(entry.offset + 26) + file.readUInt16LE(entry.offset + 28);
    const data = file.subarray(start, start + entry.compressedSize);
    return entry.method === 0 ? data : inflateRawSync(data);
  };
}

const megabytes = (bytes) => `${(bytes / 2 ** 20).toFixed(1)} MB`;
const tableName = process.argv[2];
for (const path of process.argv.slice(3)) {
  const file = readFileSync(path);
  const bytes = openZip(file);
  const manifest = JSON.parse(bytes('manifest.json').toString('utf8'));
  const parts = manifest.parts.filter((part) => part.tables[tableName]);
  const rows = parts.reduce((sum, part) => sum + part.tables[tableName], 0);
  let best = null;
  for (let run = 0; run < 5; run++) {
    globalThis.gc();
    const before = process.memoryUsage().heapUsed;
    const result = { inflate: 0, parse: 0, text: 0, largestText: 0, slowestPart: 0, kept: [] };
    for (const part of parts) {
      let start = performance.now();
      const data = bytes(part.name);
      result.inflate += performance.now() - start;
      start = performance.now();
      result.kept.push(JSON.parse(data.toString('utf8')));
      const elapsed = performance.now() - start;
      result.parse += elapsed;
      result.slowestPart = Math.max(result.slowestPart, elapsed);
      result.text += data.length;
      result.largestText = Math.max(result.largestText, data.length);
    }
    globalThis.gc();
    result.heap = process.memoryUsage().heapUsed - before;
    result.kept = null;
    if (best === null || result.inflate + result.parse < best.inflate + best.parse) best = result;
  }
  console.log(`${path}: ${megabytes(file.length)} file, ${rows.toLocaleString('en-US')} rows of ${tableName} in ${parts.length} part(s), ` +
    `text ${megabytes(best.text)} (largest part ${megabytes(best.largestText)}), inflate ${best.inflate.toFixed(0)} ms, ` +
    `decode and parse ${best.parse.toFixed(0)} ms (slowest part ${best.slowestPart.toFixed(0)} ms), heap ${megabytes(best.heap)}`);
}
