// A small zip writer for the tests: the cases that the kit has no file for, and the large footprints that the tests
// build in memory. It runs in Node and in browsers alike. The CRC-32 is computed here, and deflate and inflate go
// through the CompressionStream and DecompressionStream of the platform. Nothing here imports the package.

const encoder = new TextEncoder();

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    table[n] = c >>> 0;
  }
  return table;
})();

/** The CRC-32 of the bytes, as the zip format stores it. */
export function crc32(bytes: Uint8Array): number {
  let crc = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) {
    crc = CRC_TABLE[(crc ^ bytes[i]!) & 0xff]! ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

/** Whether the platform can write a deflated entry: whether it has CompressionStream with `deflate-raw`. */
export const canDeflate: boolean = (() => {
  try {
    new CompressionStream("deflate-raw");
    return true;
  } catch {
    return false;
  }
})();

async function transform(data: Uint8Array, stream: CompressionStream | DecompressionStream): Promise<Uint8Array<ArrayBuffer>> {
  const pair = stream as unknown as ReadableWritablePair<Uint8Array, Uint8Array>;
  const output = new Blob([data as Uint8Array<ArrayBuffer>]).stream().pipeThrough(pair);
  return new Uint8Array(await new Response(output).arrayBuffer());
}

/** Compresses bytes into a raw deflate stream, as a zip entry holds it. */
export function deflateRaw(data: Uint8Array): Promise<Uint8Array<ArrayBuffer>> {
  return transform(data, new CompressionStream("deflate-raw"));
}

/** Inflates a raw deflate stream. */
export function inflateRaw(data: Uint8Array): Promise<Uint8Array<ArrayBuffer>> {
  return transform(data, new DecompressionStream("deflate-raw"));
}

/**
 * A zip archive with these entries, in this order, all dated 2026-10-07: a local header and the data for each entry,
 * then the central directory and its end record.
 * @param entries The name and the content of each entry. A string is written in UTF-8.
 * @param method 8 to deflate every entry, 0 to store them.
 */
export async function writeZip(entries: readonly [string, Uint8Array | string][],
  method: 0 | 8 = 8): Promise<Uint8Array<ArrayBuffer>> {
  const chunks: Uint8Array[] = [];
  const directory: Uint8Array[] = [];
  let offset = 0;
  const date = ((2026 - 1980) << 9) | (10 << 5) | 7;
  for (const [name, content] of entries) {
    const data = typeof content === "string" ? encoder.encode(content) : content;
    const stored = method === 0 ? data : await deflateRaw(data);
    const nameBytes = encoder.encode(name);
    const crc = crc32(data);
    const local = new Uint8Array(30 + nameBytes.length);
    const lv = new DataView(local.buffer);
    lv.setUint32(0, 0x04034b50, true);
    lv.setUint16(4, 20, true);
    lv.setUint16(8, method, true);
    lv.setUint16(12, date, true);
    lv.setUint32(14, crc, true);
    lv.setUint32(18, stored.length, true);
    lv.setUint32(22, data.length, true);
    lv.setUint16(26, nameBytes.length, true);
    local.set(nameBytes, 30);
    const central = new Uint8Array(46 + nameBytes.length);
    const cv = new DataView(central.buffer);
    cv.setUint32(0, 0x02014b50, true);
    cv.setUint16(4, 20, true);
    cv.setUint16(6, 20, true);
    cv.setUint16(10, method, true);
    cv.setUint16(14, date, true);
    cv.setUint32(16, crc, true);
    cv.setUint32(20, stored.length, true);
    cv.setUint32(24, data.length, true);
    cv.setUint16(28, nameBytes.length, true);
    cv.setUint32(42, offset, true);
    central.set(nameBytes, 46);
    chunks.push(local, stored);
    directory.push(central);
    offset += local.length + stored.length;
  }
  const directorySize = directory.reduce((sum, chunk) => sum + chunk.length, 0);
  const end = new Uint8Array(22);
  const ev = new DataView(end.buffer);
  ev.setUint32(0, 0x06054b50, true);
  ev.setUint16(8, entries.length, true);
  ev.setUint16(10, entries.length, true);
  ev.setUint32(12, directorySize, true);
  ev.setUint32(16, offset, true);
  const all = [...chunks, ...directory, end];
  const result = new Uint8Array(all.reduce((sum, chunk) => sum + chunk.length, 0));
  let position = 0;
  for (const chunk of all) {
    result.set(chunk, position);
    position += chunk.length;
  }
  return result;
}
