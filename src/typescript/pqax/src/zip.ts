// Reads a zip archive from its central directory, as section 3 of the serialization asks: the position and the size of
// an entry come from the directory at the end of the archive, and an entry is read only when someone asks for it.
// Stored and deflated entries only, and no ZIP64, which PQAX does not use.
import { type ByteSource, readExact } from "./bytes.js";
import { PqaxError } from "./errors.js";
import { inflateRaw } from "./inflate.js";

const END_SIGNATURE = 0x06054b50;
const ZIP64_LOCATOR_SIGNATURE = 0x07064b50;
const CENTRAL_SIGNATURE = 0x02014b50;
const LOCAL_SIGNATURE = 0x04034b50;
const END_LENGTH = 22;
const ZIP64_LOCATOR_LENGTH = 20;
const MAX_COMMENT_LENGTH = 0xffff;
const ZIP64_EXTRA_ID = 0x0001;

/** One entry of the central directory. */
export interface ZipEntry {
  /** The name of the entry. */
  readonly name: string;
  /** The compression method: 0 for stored, 8 for deflate. */
  readonly method: number;
  /** The general purpose flags. */
  readonly flags: number;
  /** Whether the entry is encrypted. */
  readonly encrypted: boolean;
  /** The size of the entry in the archive. */
  readonly compressedSize: number;
  /** The size of the entry once inflated. */
  readonly uncompressedSize: number;
  /** The position of the local header. */
  readonly localHeaderOffset: number;
  /** Whether the central directory uses ZIP64 for the entry: marker values or a ZIP64 extra field. */
  readonly zip64: boolean;
  /** Whether the sizes or the offset of the entry are only in a ZIP64 extra field, so that it cannot be read here. */
  readonly needsZip64: boolean;
  /** The length of the local header if it has the name and the extra field of the central directory. */
  readonly expectedHeaderLength: number;
}

/** The central directory of an archive. */
export interface ZipArchive {
  /** The bytes of the archive. */
  readonly source: ByteSource;
  /** Every entry, in the order of the central directory. */
  readonly entries: readonly ZipEntry[];
  /** Whether the end of the archive has a ZIP64 locator. */
  readonly zip64: boolean;
  /** The entry with this exact name. When two entries have the same name, the last one. */
  find(name: string): ZipEntry | undefined;
}

const nameDecoder = new TextDecoder("utf-8");

/**
 * Reads the central directory of an archive.
 * @throws {PqaxError} `container.manifestMissing` when the source is not a zip archive or its directory is damaged, and
 * `container.zip64` when the directory can only be found through ZIP64 records.
 */
export async function readZip(source: ByteSource): Promise<ZipArchive> {
  const size = source.size;
  if (size < END_LENGTH) {
    throw notZip("it is shorter than the end record of a zip archive");
  }
  // Most archives have no comment, so their end record is in the last 22 bytes. The 20 bytes before it hold the ZIP64
  // locator when there is one. When the record is not there, the archive has a comment, and the record is in the last
  // 64 KB.
  let tailStart = Math.max(0, size - END_LENGTH - ZIP64_LOCATOR_LENGTH);
  let tail = await readExact(source, tailStart, size - tailStart);
  let at = findEndRecord(tail);
  if (at < 0 && tailStart > 0) {
    tailStart = Math.max(0, size - END_LENGTH - MAX_COMMENT_LENGTH - ZIP64_LOCATOR_LENGTH);
    tail = await readExact(source, tailStart, size - tailStart);
    at = findEndRecord(tail);
  }
  if (at < 0) {
    throw notZip("it has no end of central directory record");
  }
  const end = view(tail);
  const disk = end.getUint16(at + 4, true);
  const directoryDisk = end.getUint16(at + 6, true);
  const entriesOnDisk = end.getUint16(at + 8, true);
  const entryCount = end.getUint16(at + 10, true);
  const directorySize = end.getUint32(at + 12, true);
  const directoryOffset = end.getUint32(at + 16, true);
  const zip64 = at >= ZIP64_LOCATOR_LENGTH && end.getUint32(at - ZIP64_LOCATOR_LENGTH, true) === ZIP64_LOCATOR_SIGNATURE;
  if (entryCount === 0xffff || entriesOnDisk === 0xffff || directorySize === 0xffffffff || directoryOffset === 0xffffffff) {
    throw new PqaxError("container.zip64", "The archive needs ZIP64 to locate its central directory");
  }
  if (disk !== 0 || directoryDisk !== 0 || entriesOnDisk !== entryCount) {
    throw notZip("it is split in volumes");
  }
  if (directoryOffset + directorySize > tailStart + at) {
    throw notZip("its central directory is beyond the end of the archive");
  }
  const directory = await readExact(source, directoryOffset, directorySize);
  const entries = parseDirectory(directory, entryCount);
  const byName = new Map<string, ZipEntry>();
  for (const entry of entries) {
    byName.set(entry.name, entry);
  }
  return { source, entries, zip64, find: (name: string) => byName.get(name) };
}

/**
 * Reads an entry and inflates it when it is deflated.
 * @throws {PqaxError} `container.zip64`, `container.encrypted`, or `container.method` when the entry cannot be read, and
 * `json.invalid` when its bytes are damaged.
 */
export async function readEntry(archive: ZipArchive, entry: ZipEntry): Promise<Uint8Array> {
  checkReadable(entry);
  const { source } = archive;
  const offset = entry.localHeaderOffset;
  if (offset + 30 > source.size) {
    throw damaged(entry, "its local header is beyond the end of the archive");
  }
  // The local header usually has the name and the extra field of the central directory, so one read gets the header
  // and the data, and nothing of the next entry. A second read follows when the local header is longer.
  const guess = Math.min(entry.expectedHeaderLength + entry.compressedSize, source.size - offset);
  const head = await readExact(source, offset, Math.max(30, guess));
  const header = view(head);
  if (header.getUint32(0, true) !== LOCAL_SIGNATURE) {
    throw damaged(entry, "its local header has no signature");
  }
  const headerLength = 30 + header.getUint16(26, true) + header.getUint16(28, true);
  const dataStart = offset + headerLength;
  if (dataStart + entry.compressedSize > source.size) {
    throw damaged(entry, "its data is beyond the end of the archive");
  }
  const data =
    headerLength + entry.compressedSize <= head.length
      ? head.subarray(headerLength, headerLength + entry.compressedSize)
      : await readExact(source, dataStart, entry.compressedSize);
  if (entry.method === 0) {
    return data;
  }
  try {
    return await inflateRaw(data, entry.uncompressedSize);
  } catch (error) {
    throw new PqaxError("json.invalid", `The entry ${entry.name} cannot be inflated`, entry.name, error);
  }
}

/**
 * Finds where the data of an entry starts, from its local header, without reading the data.
 * @throws {PqaxError} as {@link readEntry} does.
 */
export async function entryDataOffset(archive: ZipArchive, entry: ZipEntry): Promise<number> {
  checkReadable(entry);
  const { source } = archive;
  const offset = entry.localHeaderOffset;
  if (offset + 30 > source.size) {
    throw damaged(entry, "its local header is beyond the end of the archive");
  }
  const header = view(await readExact(source, offset, 30));
  if (header.getUint32(0, true) !== LOCAL_SIGNATURE) {
    throw damaged(entry, "its local header has no signature");
  }
  const dataStart = offset + 30 + header.getUint16(26, true) + header.getUint16(28, true);
  if (dataStart + entry.compressedSize > source.size) {
    throw damaged(entry, "its data is beyond the end of the archive");
  }
  return dataStart;
}

function checkReadable(entry: ZipEntry): void {
  if (entry.needsZip64) {
    throw new PqaxError("container.zip64", `The entry ${entry.name} needs ZIP64`, entry.name);
  }
  if (entry.encrypted) {
    throw new PqaxError("container.encrypted", `The entry ${entry.name} is encrypted`, entry.name);
  }
  if (entry.method !== 0 && entry.method !== 8) {
    throw new PqaxError("container.method", `The entry ${entry.name} uses compression method ${entry.method}`, entry.name);
  }
}

// Looks for the end record from the end of the tail. A record whose comment ends exactly at the end of the archive is
// preferred, so that a signature inside a comment is not taken for the record.
function findEndRecord(tail: Uint8Array): number {
  const data = view(tail);
  let fallback = -1;
  for (let at = tail.length - END_LENGTH; at >= 0; at--) {
    if (data.getUint32(at, true) === END_SIGNATURE) {
      if (at + END_LENGTH + data.getUint16(at + 20, true) === tail.length) {
        return at;
      }
      if (fallback < 0) {
        fallback = at;
      }
    }
  }
  return fallback;
}

function parseDirectory(directory: Uint8Array, entryCount: number): ZipEntry[] {
  const data = view(directory);
  const entries: ZipEntry[] = [];
  let p = 0;
  for (let i = 0; i < entryCount; i++) {
    if (p + 46 > directory.length || data.getUint32(p, true) !== CENTRAL_SIGNATURE) {
      throw notZip("its central directory is damaged");
    }
    const flags = data.getUint16(p + 8, true);
    const method = data.getUint16(p + 10, true);
    const compressedSize = data.getUint32(p + 20, true);
    const uncompressedSize = data.getUint32(p + 24, true);
    const nameLength = data.getUint16(p + 28, true);
    const extraLength = data.getUint16(p + 30, true);
    const commentLength = data.getUint16(p + 32, true);
    const diskStart = data.getUint16(p + 34, true);
    const localHeaderOffset = data.getUint32(p + 42, true);
    const nameEnd = p + 46 + nameLength;
    const next = nameEnd + extraLength + commentLength;
    if (next > directory.length) {
      throw notZip("its central directory is damaged");
    }
    const needsZip64 =
      compressedSize === 0xffffffff || uncompressedSize === 0xffffffff || localHeaderOffset === 0xffffffff || diskStart === 0xffff;
    entries.push({
      name: nameDecoder.decode(directory.subarray(p + 46, nameEnd)),
      method,
      flags,
      encrypted: (flags & 1) !== 0,
      compressedSize,
      uncompressedSize,
      localHeaderOffset,
      zip64: needsZip64 || hasExtraField(directory.subarray(nameEnd, nameEnd + extraLength), ZIP64_EXTRA_ID),
      needsZip64,
      expectedHeaderLength: 30 + nameLength + extraLength,
    });
    p = next;
  }
  return entries;
}

function hasExtraField(extra: Uint8Array, id: number): boolean {
  const data = view(extra);
  for (let p = 0; p + 4 <= extra.length; p += 4 + data.getUint16(p + 2, true)) {
    if (data.getUint16(p, true) === id) {
      return true;
    }
  }
  return false;
}

function view(bytes: Uint8Array): DataView {
  return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
}

function notZip(reason: string): PqaxError {
  return new PqaxError("container.manifestMissing", `The file is not a zip archive that can be read: ${reason}`);
}

function damaged(entry: ZipEntry, reason: string): PqaxError {
  return new PqaxError("json.invalid", `The entry ${entry.name} is damaged: ${reason}`, entry.name);
}
