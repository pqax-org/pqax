// Node-only helpers: "pqax/node". The main entry stays free of Node modules so that it runs in browsers.
import { open, stat } from "node:fs/promises";
import type { ByteSource } from "./bytes.js";

/**
 * Opens a file on disk as a {@link ByteSource} that reads only the ranges that the reader asks for, never the whole
 * file. Each read opens the file, reads the range, and closes it, so the source holds no file handle and needs no
 * closing.
 * @param path The path of the file.
 * @returns A source whose size is the size of the file when this function ran.
 */
export async function fromFile(path: string): Promise<ByteSource> {
  const info = await stat(path);
  if (!info.isFile()) {
    throw new Error(`${path} is not a file`);
  }
  const size = info.size;
  return {
    size,
    async read(offset: number, length: number): Promise<Uint8Array> {
      if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(length) || offset < 0 || length < 0 || offset + length > size) {
        throw new RangeError(`The range of ${length} bytes at offset ${offset} is outside a file of ${size} bytes`);
      }
      const buffer = new Uint8Array(length);
      const handle = await open(path, "r");
      try {
        let done = 0;
        while (done < length) {
          const { bytesRead } = await handle.read(buffer, done, length - done, offset + done);
          if (bytesRead === 0) {
            throw new RangeError(`${path} ended before offset ${offset + length}`);
          }
          done += bytesRead;
        }
      } finally {
        await handle.close();
      }
      return buffer;
    },
  };
}
