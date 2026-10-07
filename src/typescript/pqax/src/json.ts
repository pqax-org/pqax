// Decodes and parses the parts. JSON.parse keeps the last of two members with the same name and turns 1.0 into 1, so
// the validator also runs scanJson on the text: a small tokenizer that sees what JSON.parse loses. An integer beyond
// 2^53 - 1 needs no scan: JSON.parse rounds it to 2^53 or more, so the parsed value shows that it is out of range.

/** The text of a part. */
export interface DecodedText {
  /** The text, without the byte order mark. */
  readonly text: string;
  /** Whether the bytes started with a UTF-8 byte order mark. */
  readonly bom: boolean;
  /** Whether the bytes are valid UTF-8. When they are not, the text has replacement characters. */
  readonly validUtf8: boolean;
}

const lenientDecoder = new TextDecoder("utf-8", { ignoreBOM: true });
const strictDecoder = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });

/**
 * Decodes the bytes of a part as UTF-8 and skips a byte order mark.
 * @param bytes The bytes of the part.
 * @param checkUtf8 Whether to find out if the bytes are valid UTF-8, which costs a second decoding when they are not.
 */
export function decodeUtf8(bytes: Uint8Array, checkUtf8: boolean): DecodedText {
  const bom = bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf;
  const body = bom ? bytes.subarray(3) : bytes;
  if (checkUtf8) {
    try {
      return { text: strictDecoder.decode(body), bom, validUtf8: true };
    } catch {
      return { text: lenientDecoder.decode(body), bom, validUtf8: false };
    }
  }
  return { text: lenientDecoder.decode(body), bom, validUtf8: true };
}

/** The result of parsing a text. */
export type ParseResult = { readonly ok: true; readonly value: unknown } | { readonly ok: false; readonly message: string };

/** Parses a JSON text and returns the error message instead of throwing. */
export function parseJson(text: string): ParseResult {
  try {
    return { ok: true, value: JSON.parse(text) as unknown };
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : String(error) };
  }
}

/** Whether a value is a JSON object: not null, not an array. */
export function isJsonObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Reads a member of a parsed object only when it is its own, so that a name such as `constructor` finds nothing. */
export function member(object: Record<string, unknown> | undefined, name: string): unknown {
  return object !== undefined && Object.prototype.hasOwnProperty.call(object, name) ? object[name] : undefined;
}

/** Sets a member, also when its name is `__proto__`, which an assignment would take as the prototype. */
export function setMember(object: Record<string, unknown>, name: string, value: unknown): void {
  if (name === "__proto__") {
    Object.defineProperty(object, name, { value, enumerable: true, writable: true, configurable: true });
  } else {
    object[name] = value;
  }
}

/** A place in a JSON text, with the number of values found there. */
export interface PathCount {
  /** The member names from the top of the text. `[]` stands for any element of an array. */
  readonly path: readonly string[];
  /** How many values were found at that place. */
  count: number;
}

/** What {@link scanJson} finds. */
export interface ScanResult {
  /** The members that an object repeats: the path of the object and the repeated name. */
  readonly duplicates: { readonly path: readonly string[]; readonly name: string }[];
  /** The numbers written with a fraction or an exponent, by path. The key is the path joined with `/`. */
  readonly fractions: Map<string, PathCount>;
  /** The number of strings, member names included, that hold an escaped lone surrogate. */
  loneSurrogates: number;
}

/** The key of a path in the maps of a {@link ScanResult}. */
export function pathKey(path: readonly string[]): string {
  return path.join("/");
}

const LONE_SURROGATE = /\p{Cs}/u;

/**
 * Scans a JSON text that JSON.parse accepted, for what JSON.parse loses: repeated member names, numbers written with a
 * fraction or an exponent, and escaped lone surrogates.
 */
export function scanJson(text: string): ScanResult {
  const result: ScanResult = { duplicates: [], fractions: new Map(), loneSurrogates: 0 };
  // One frame for each open object or array. An object frame keeps its member names and the member being read.
  const isObject: boolean[] = [];
  const names: (Set<string> | null)[] = [];
  const current: string[] = [];
  let expectName = false;
  const path = (depth: number): string[] => {
    const segments: string[] = [];
    for (let d = 0; d < depth; d++) {
      segments.push(isObject[d] ? current[d]! : "[]");
    }
    return segments;
  };
  const length = text.length;
  let i = 0;
  while (i < length) {
    const c = text.charCodeAt(i);
    if (c === 0x22) {
      // A string: a member name or a value.
      const start = i + 1;
      let j = start;
      let escaped = false;
      for (;;) {
        const d = text.charCodeAt(j);
        if (d === 0x22 || j >= length) {
          break;
        }
        if (d === 0x5c) {
          escaped = true;
          j += 2;
        } else {
          j++;
        }
      }
      i = j + 1;
      let decoded: string | undefined;
      if (escaped) {
        decoded = JSON.parse(text.slice(start - 1, j + 1)) as string;
        if (LONE_SURROGATE.test(decoded)) {
          result.loneSurrogates++;
        }
      }
      if (expectName) {
        const name = decoded ?? text.slice(start, j);
        const top = isObject.length - 1;
        const seen = names[top]!;
        if (seen.has(name)) {
          result.duplicates.push({ path: path(top), name });
        } else {
          seen.add(name);
        }
        current[top] = name;
        expectName = false;
      }
    } else if (c === 0x7b) {
      isObject.push(true);
      names.push(new Set());
      current.push("");
      expectName = true;
      i++;
    } else if (c === 0x5b) {
      isObject.push(false);
      names.push(null);
      current.push("");
      expectName = false;
      i++;
    } else if (c === 0x7d || c === 0x5d) {
      isObject.pop();
      names.pop();
      current.pop();
      expectName = false;
      i++;
    } else if (c === 0x2c) {
      expectName = isObject.length > 0 && isObject[isObject.length - 1]!;
      i++;
    } else if (c === 0x2d || (c >= 0x30 && c <= 0x39)) {
      // A number. It is an integer when it has no fraction and no exponent.
      if (c === 0x2d) {
        i++;
      }
      while (i < length && isDigit(text.charCodeAt(i))) {
        i++;
      }
      let integer = true;
      if (text.charCodeAt(i) === 0x2e) {
        integer = false;
        i++;
        while (i < length && isDigit(text.charCodeAt(i))) {
          i++;
        }
      }
      const e = text.charCodeAt(i);
      if (e === 0x65 || e === 0x45) {
        integer = false;
        i++;
        const sign = text.charCodeAt(i);
        if (sign === 0x2b || sign === 0x2d) {
          i++;
        }
        while (i < length && isDigit(text.charCodeAt(i))) {
          i++;
        }
      }
      if (!integer) {
        count(result.fractions, path(isObject.length));
      }
    } else {
      // Whitespace, a colon, or the letters of true, false, and null.
      i++;
    }
  }
  return result;
}

function isDigit(c: number): boolean {
  return c >= 0x30 && c <= 0x39;
}

function count(map: Map<string, PathCount>, path: string[]): void {
  const key = pathKey(path);
  const found = map.get(key);
  if (found) {
    found.count++;
  } else {
    map.set(key, { path, count: 1 });
  }
}
