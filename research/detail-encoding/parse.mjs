// Times how long plain JavaScript takes to read each text encoding from its zip.
// Usage: node parse.mjs <folder> <rows>
import { readFileSync } from 'node:fs';
import { inflateRawSync } from 'node:zlib';
import { performance } from 'node:perf_hooks';
import { join } from 'node:path';

const [folder, rows] = process.argv.slice(2);

// Each zip holds a single entry, so the first local header is the one to read.
function readOnlyEntry(path) {
  const zip = readFileSync(path);
  const method = zip.readUInt16LE(8);
  const start = 30 + zip.readUInt16LE(26) + zip.readUInt16LE(28);
  let eocd = zip.length - 22;
  while (zip.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  const compressedSize = zip.readUInt32LE(zip.readUInt32LE(eocd + 16) + 20);
  const data = zip.subarray(start, start + compressedSize);
  return method === 0 ? data : inflateRawSync(data);
}

const readers = {
  'rows-json': (text) => JSON.parse(text).length,
  'columnar-json': (text) => JSON.parse(text).columns.compressedSize.length,
  csv: (text) => {
    const lines = text.split('\n');
    const sizes = new Float64Array(lines.length);
    let count = 0;
    for (let i = 1; i < lines.length; i++) {
      const line = lines[i];
      if (line.length === 0) continue;
      const quote = line.indexOf('"');
      const head = line.slice(0, quote - 1).split(',');
      const tail = line.slice(line.indexOf('"', quote + 1) + 2).split(',');
      sizes[count++] = Number(head[0]) + Number(head[1]) + Number(head[2]) +
        Number(tail[0]) + Number(tail[1]) + Number(tail[2]) + Number(tail[3]) + Number(tail[4]);
    }
    return count;
  },
};

for (const [name, read] of Object.entries(readers)) {
  let best = Infinity;
  let failure = null;
  for (let i = 0; i < 2 && failure === null; i++) {
    try {
      const start = performance.now();
      const bytes = readOnlyEntry(join(folder, `${name}-${rows}.zip`));
      const inflated = performance.now();
      const text = bytes.toString('utf8');
      const count = read(text);
      if (count !== Number(rows)) throw new Error(`row count mismatch: ${count}`);
      const done = performance.now();
      if (done - start < best) best = done - start;
      if (i === 1) console.log(`  ${name.padEnd(14)} JavaScript read ${best.toFixed(0).padStart(6)} ms (inflate ${(inflated - start).toFixed(0)} ms)`);
    } catch (error) {
      failure = error.code ?? error.message;
    }
  }
  if (failure !== null) console.log(`  ${name.padEnd(14)} JavaScript read fails: ${failure}`);
}
