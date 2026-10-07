/**
 * A source of bytes that can be read at any position: a whole file, a piece of a file, or a buffer in memory.
 *
 * The reader asks only for the ranges it needs, so a source can read from a file, from a `Blob`, or from a server that
 * answers range requests.
 */
export interface ByteSource {
  /** The number of bytes in the source. */
  readonly size: number;
  /**
   * Reads bytes. The reader never asks for a range beyond {@link ByteSource.size}.
   * @param offset The position of the first byte.
   * @param length The number of bytes.
   * @returns Exactly `length` bytes. The reader does not change them.
   */
  read(offset: number, length: number): Promise<Uint8Array>;
}

/** What {@link openPqax}, {@link openEmbedded}, and {@link validate} accept. Any other `ArrayBufferView` works too. */
export type PqaxInput = Blob | ArrayBuffer | Uint8Array | ByteSource;

/** Turns any accepted input into a {@link ByteSource}. */
export function toByteSource(input: PqaxInput): ByteSource {
  if (ArrayBuffer.isView(input)) {
    return memorySource(unshared(new Uint8Array(input.buffer, input.byteOffset, input.byteLength)));
  }
  if (input instanceof ArrayBuffer || Object.prototype.toString.call(input) === "[object ArrayBuffer]") {
    return memorySource(new Uint8Array(input as ArrayBuffer));
  }
  if (isByteSource(input)) {
    return input;
  }
  if (isBlob(input)) {
    return blobSource(input);
  }
  throw new TypeError("The input is not a Blob, an ArrayBuffer, a Uint8Array, or a ByteSource");
}

/** A source over bytes in memory. A read returns a view of the same memory, with no copy. */
export function memorySource(bytes: Uint8Array): ByteSource {
  return {
    size: bytes.length,
    read(offset: number, length: number): Promise<Uint8Array> {
      checkRange(offset, length, bytes.length);
      return Promise.resolve(bytes.subarray(offset, offset + length));
    },
  };
}

/** A source over a range of another source: a stored part of a zip archive, read in place. */
export function windowSource(base: ByteSource, start: number, size: number): ByteSource {
  checkRange(start, size, base.size);
  return {
    size,
    read(offset: number, length: number): Promise<Uint8Array> {
      checkRange(offset, length, size);
      return base.read(start + offset, length);
    },
  };
}

/** Reads a range and checks that the source returned all of it. */
export async function readExact(source: ByteSource, offset: number, length: number): Promise<Uint8Array> {
  checkRange(offset, length, source.size);
  const bytes = await source.read(offset, length);
  if (!(bytes instanceof Uint8Array) || bytes.length < length) {
    throw new RangeError(`The source returned ${bytes?.length ?? 0} bytes at offset ${offset} instead of ${length}`);
  }
  return bytes.length === length ? bytes : bytes.subarray(0, length);
}

function checkRange(offset: number, length: number, size: number): void {
  if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(length) || offset < 0 || length < 0 || offset + length > size) {
    throw new RangeError(`The range of ${length} bytes at offset ${offset} is outside a source of ${size} bytes`);
  }
}

function blobSource(blob: Blob): ByteSource {
  return {
    size: blob.size,
    async read(offset: number, length: number): Promise<Uint8Array> {
      checkRange(offset, length, blob.size);
      return new Uint8Array(await blob.slice(offset, offset + length).arrayBuffer());
    },
  };
}

// Text decoding and streams refuse views of a SharedArrayBuffer, so such an input is copied once.
function unshared(bytes: Uint8Array): Uint8Array {
  return typeof SharedArrayBuffer !== "undefined" && bytes.buffer instanceof SharedArrayBuffer ? bytes.slice() : bytes;
}

function isByteSource(value: unknown): value is ByteSource {
  const candidate = value as Partial<ByteSource> | null;
  return typeof candidate === "object" && candidate !== null && typeof candidate.read === "function" && typeof candidate.size === "number";
}

function isBlob(value: unknown): value is Blob {
  if (typeof Blob !== "undefined" && value instanceof Blob) {
    return true;
  }
  const candidate = value as Partial<Blob> | null;
  return (
    typeof candidate === "object" &&
    candidate !== null &&
    typeof candidate.slice === "function" &&
    typeof candidate.arrayBuffer === "function" &&
    typeof candidate.size === "number"
  );
}
