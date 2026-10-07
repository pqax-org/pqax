// Builds columnar tables from the parts of a file, and reads them by row.
import { member, setMember } from "./json.js";
import type { ColumnarTable, RowOf } from "./types.js";

/** The piece of a table that one part holds. */
export interface TablePiece {
  /** The table object as the part holds it, or undefined when the part does not hold the table. */
  readonly table: Record<string, unknown> | undefined;
  /** The rows that the manifest states for the table in that part, when it states a valid count. */
  readonly declared: number | undefined;
}

/** The rows of a piece, and whether its arrays agree on them. */
export interface PieceRows {
  /** The rows of the piece: the length of its longest array. */
  readonly rows: number;
  /** Whether all the arrays of the piece have the same length. A piece with no array agrees. */
  readonly aligned: boolean;
}

/** The most rows that a piece with no array is taken to have: the most that a detail part may hold. */
const MAX_ROWS_WITHOUT_ARRAYS = 250_000;

/**
 * Counts the rows of a piece: the length of its longest array. In a valid file every array has the length that the
 * manifest states. The count of the manifest is used only for a piece with no array, whose rows have no value in any
 * field, and only up to 250,000, so that a manifest that states more rows than the part holds cannot make the reader
 * allocate them.
 */
export function pieceRows(piece: TablePiece): PieceRows {
  let common = -1;
  let longest = 0;
  let aligned = true;
  if (piece.table !== undefined) {
    for (const value of Object.values(piece.table)) {
      if (Array.isArray(value)) {
        if (common < 0) {
          common = value.length;
        } else if (value.length !== common) {
          aligned = false;
        }
        longest = Math.max(longest, value.length);
      }
    }
  }
  if (common >= 0) {
    return { rows: longest, aligned };
  }
  const declared = piece.declared ?? 0;
  return { rows: declared <= MAX_ROWS_WITHOUT_ARRAYS ? declared : 0, aligned };
}

/** The fields of a table joined across its parts. */
export interface JoinedTable {
  /** The number of rows. */
  readonly length: number;
  /** The arrays, in the order in which the fields first appear. */
  readonly fields: Map<string, unknown[]>;
  /** The members that are not arrays, as the first part that has them holds them. */
  readonly others: Map<string, unknown>;
}

/**
 * Joins the pieces of a table, in the order of the manifest. A field that a piece does not have gets one null for each
 * row of that piece.
 * @param pieces The pieces, in the order of the manifest.
 * @param keep Which fields to keep. All of them when undefined.
 * @param fit Whether to pad every array of a piece with nulls to the rows of the piece, so that the rows stay aligned.
 */
export function joinPieces(pieces: readonly TablePiece[], keep: ((field: string) => boolean) | undefined, fit: boolean): JoinedTable {
  const rows = pieces.map((piece) => pieceRows(piece).rows);
  const fields = new Map<string, unknown[]>();
  const others = new Map<string, unknown>();
  const names: string[] = [];
  for (const piece of pieces) {
    for (const [name, value] of Object.entries(piece.table ?? {})) {
      if (keep !== undefined && !keep(name)) {
        continue;
      }
      if (Array.isArray(value)) {
        if (!fields.has(name)) {
          fields.set(name, []);
          names.push(name);
        }
      } else if (!others.has(name)) {
        others.set(name, value);
      }
    }
  }
  for (const name of names) {
    const segments = pieces.map((piece, i) => {
      const value = member(piece.table, name);
      if (!Array.isArray(value)) {
        return nulls(rows[i]!);
      }
      return fit ? fitArray(value, rows[i]!) : value;
    });
    fields.set(name, segments.length === 1 ? segments[0]! : ([] as unknown[]).concat(...segments));
    others.delete(name);
  }
  return { length: rows.reduce((total, n) => total + n, 0), fields, others };
}

/** Builds a table object: `length`, then one array for each field. */
export function buildTable<T extends ColumnarTable>(length: number, fields: Map<string, unknown[]>): T {
  const table: Record<string, unknown> = { length };
  for (const [name, values] of fields) {
    setMember(table, name, values);
  }
  return table as unknown as T;
}

/** The table that {@link PqaxDocument.readTable} returns when the file has no rows of a table: `{ length: 0 }`. */
export function emptyTable<T extends ColumnarTable>(): T {
  return { length: 0 } as T;
}

/**
 * Reads a columnar table one row at a time.
 *
 * Each row is a new object with one member for each field that the table has, and the value of that field in the row.
 * A field that the table does not have is not on the row.
 * @param table A table from {@link PqaxDocument.summary} or {@link PqaxDocument.readTable}.
 */
export function* rows<T extends ColumnarTable>(table: T): Iterable<RowOf<T>> {
  const columns: [string, readonly unknown[]][] = [];
  for (const [name, values] of Object.entries(table)) {
    if (name !== "length" && Array.isArray(values)) {
      columns.push([name, values]);
    }
  }
  const length = typeof table.length === "number" ? table.length : Math.max(0, ...columns.map(([, values]) => values.length));
  for (let i = 0; i < length; i++) {
    const row: Record<string, unknown> = {};
    for (const [name, values] of columns) {
      setMember(row, name, i < values.length ? (values[i] ?? null) : null);
    }
    yield row as RowOf<T>;
  }
}

/**
 * Maps the `id` of each row to its position in the table, to follow references. When an `id` repeats, which a valid file
 * never has, the first row wins. Rows without an `id` are left out.
 * @param table A table with an `id` field. A table without one gives an empty map.
 */
export function indexById(table: { readonly id?: readonly (number | null)[] | undefined }): Map<number, number> {
  const index = new Map<number, number>();
  const ids = table.id;
  if (Array.isArray(ids)) {
    for (let i = 0; i < ids.length; i++) {
      const id = ids[i];
      if (typeof id === "number" && !index.has(id)) {
        index.set(id, i);
      }
    }
  }
  return index;
}

function nulls(count: number): null[] {
  return new Array<null>(count).fill(null);
}

function fitArray(values: unknown[], rows: number): unknown[] {
  return values.length >= rows ? values : values.concat(nulls(rows - values.length));
}
