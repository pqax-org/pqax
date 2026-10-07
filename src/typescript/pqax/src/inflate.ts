/**
 * Inflates a raw deflate stream, as a zip entry holds it, with the `DecompressionStream` of the platform. It works in
 * browsers and in Node, with no library.
 * @param data The compressed bytes.
 * @param expectedSize The size that the zip directory states, used to size the result. The result is correct when it
 * differs, and the size is trusted only up to 64 MB, so that a damaged directory cannot make the reader allocate more.
 */
export async function inflateRaw(data: Uint8Array, expectedSize: number): Promise<Uint8Array> {
  const input = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(data);
      controller.close();
    },
  });
  const decompression = new DecompressionStream("deflate-raw") as unknown as ReadableWritablePair<Uint8Array, Uint8Array>;
  const reader = input.pipeThrough(decompression).getReader();
  let result = new Uint8Array(Math.min(Math.max(0, expectedSize), 1 << 26));
  let length = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }
    if (length + value.length > result.length) {
      const larger = new Uint8Array(Math.max(result.length * 2, length + value.length));
      larger.set(result.subarray(0, length));
      result = larger;
    }
    result.set(value, length);
    length += value.length;
  }
  return length === result.length ? result : result.subarray(0, length);
}
