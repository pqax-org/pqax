// The tables and fields that this version knows, from the object model and section 5.3 of the serialization. The
// reader keeps these fields in its typed tables, and the validator checks their values.
import type {
  ColumnarTable,
  DeltaDetails,
  DetailTableName,
  DetailTables,
  Distribution,
  IcebergDetails,
  SummaryTableName,
  SummaryTables,
  TableName,
} from "./types.js";

/**
 * The kind of a field: `id` for the identifier of a row, `ref:` and `refs:` for a reference and a list of references to
 * a table, `strings` for a list of strings, `nullableStrings` for the partition values, the only list whose elements can
 * be null, and `map` for a map of string to string.
 */
export type FieldKind =
  | "id"
  | "integer"
  | "string"
  | "boolean"
  | "timestamp"
  | "strings"
  | "nullableStrings"
  | "map"
  | "distribution"
  | "delta"
  | "iceberg"
  | `ref:${TableName}`
  | `refs:${TableName}`;

/** The names of the summary tables, in the order of the serialization. They are all in `summary.json`. */
export const summaryTableNames: readonly SummaryTableName[] = [
  "sources",
  "tables",
  "columns",
  "partitionSpecs",
  "partitionFields",
  "sortOrders",
  "sortFields",
  "physicalSchemas",
  "physicalColumns",
  "writers",
  "operations",
  "issues",
  "modelBindings",
  "tableBindings",
  "columnBindings",
];

/** The names of the detail tables, in the order of the serialization. Each has parts of its own. */
export const detailTableNames: readonly DetailTableName[] = ["partitions", "files", "rowGroups", "columnChunks", "deleteFiles", "commits"];

export const SUMMARY_TABLES: ReadonlySet<string> = new Set(summaryTableNames);
export const DETAIL_TABLES: ReadonlySet<string> = new Set(detailTableNames);

const I = "integer";
const S = "string";
const B = "boolean";
const T = "timestamp";

/** The fields of every table, with their kinds. */
export const FIELDS = {
  sources: { id: "id", kind: S, name: S, location: S, identifiers: "map" },
  tables: {
    id: "id",
    source: "ref:sources",
    namespace: "strings",
    name: S,
    location: S,
    format: S,
    tableId: S,
    description: S,
    createdAt: T,
    properties: "map",
    snapshotId: S,
    snapshotTimestamp: T,
    delta: "delta",
    iceberg: "iceberg",
    clusteringColumns: "refs:columns",
    collectedLevel: I,
    detailDepth: S,
    fileCount: I,
    fileSize: I,
    rowCount: I,
    deletedRowCount: I,
    lastModifiedAt: T,
    vOrderedFileCount: I,
    partitionCount: I,
    deleteFileCount: I,
    deleteFileSize: I,
    commitCount: I,
    commitsRead: I,
    oldestCommitAt: T,
    nonLiveFileCount: I,
    nonLiveFileSize: I,
    metadataFileCount: I,
    metadataSize: I,
    footerCount: I,
    rowGroupCount: I,
    compressedSize: I,
    uncompressedSize: I,
    footerSize: I,
    staleColumnSize: I,
    columnTotalsSource: S,
    fileSizes: "distribution",
    rowGroupRowCounts: "distribution",
    partitionSizes: "distribution",
  },
  columns: {
    id: "id",
    table: "ref:tables",
    fieldId: I,
    name: S,
    dataType: S,
    isNullable: B,
    physicalName: S,
    isPartitionColumn: B,
    description: S,
    compressedSize: I,
    uncompressedSize: I,
    valueCount: I,
    nullCount: I,
    chunkCount: I,
    dictionaryChunkCount: I,
    fallbackChunkCount: I,
    dictionaryPageSize: I,
    unencodedByteArrayDataBytes: I,
    codecs: "strings",
    encodings: "strings",
    fileCountWithoutColumn: I,
    largestChunkDistinctCount: I,
    distinctCount: I,
    distinctCountIsEstimate: B,
    distinctCountSource: S,
  },
  partitionSpecs: { id: "id", table: "ref:tables", specId: I, isDefault: B },
  partitionFields: { partitionSpec: "ref:partitionSpecs", position: I, name: S, sourceColumns: "refs:columns", transform: S },
  sortOrders: { id: "id", table: "ref:tables", orderId: I, isDefault: B },
  sortFields: { sortOrder: "ref:sortOrders", position: I, sourceColumn: "ref:columns", transform: S, direction: S, nullOrder: S },
  physicalSchemas: { id: "id", table: "ref:tables", fileCount: I },
  physicalColumns: {
    id: "id",
    physicalSchema: "ref:physicalSchemas",
    ordinal: I,
    path: "strings",
    physicalType: S,
    typeLength: I,
    logicalType: S,
    repetition: S,
    fieldId: I,
    column: "ref:columns",
    compressedSize: I,
    uncompressedSize: I,
    chunkCount: I,
  },
  writers: { id: "id", table: "ref:tables", createdBy: S, isVOrdered: B, vOrderLevel: S, fileCount: I, fileSize: I },
  operations: { table: "ref:tables", name: S, commitCount: I, lastAt: T },
  issues: { table: "ref:tables", path: S, code: S, message: S },
  modelBindings: { id: "id", modelName: S, serverName: S },
  tableBindings: { id: "id", modelBinding: "ref:modelBindings", modelTable: S, table: "ref:tables" },
  columnBindings: { tableBinding: "ref:tableBindings", modelColumn: S, column: "ref:columns" },
  partitions: {
    id: "id",
    table: "ref:tables",
    partitionSpec: "ref:partitionSpecs",
    values: "nullableStrings",
    fileCount: I,
    fileSize: I,
    rowCount: I,
    deletedRowCount: I,
  },
  files: {
    id: "id",
    table: "ref:tables",
    path: S,
    format: S,
    fileSize: I,
    modifiedAt: T,
    rowCount: I,
    deletedRowCount: I,
    deletionVectorSize: I,
    partition: "ref:partitions",
    sortOrder: "ref:sortOrders",
    clusteringProvider: S,
    isVOrdered: B,
    footerSize: I,
    physicalSchema: "ref:physicalSchemas",
    writer: "ref:writers",
    rowGroupCount: I,
    compressedSize: I,
    uncompressedSize: I,
  },
  rowGroups: { id: "id", file: "ref:files", ordinal: I, rowCount: I, uncompressedSize: I, compressedSize: I },
  columnChunks: {
    rowGroup: "ref:rowGroups",
    physicalColumn: "ref:physicalColumns",
    codec: S,
    encodings: "strings",
    valueCount: I,
    nullCount: I,
    compressedSize: I,
    uncompressedSize: I,
    hasDictionaryPage: B,
    dictionaryPageSize: I,
    dataPageCount: I,
    dictionaryEncodedPageCount: I,
    unencodedByteArrayDataBytes: I,
    bloomFilterSize: I,
    minValue: S,
    maxValue: S,
    distinctCount: I,
    dictionaryEntryCount: I,
  },
  deleteFiles: {
    id: "id",
    table: "ref:tables",
    kind: S,
    path: S,
    format: S,
    fileSize: I,
    recordCount: I,
    referencedFile: "ref:files",
    equalityColumns: "refs:columns",
    partition: "ref:partitions",
  },
  commits: {
    table: "ref:tables",
    commitId: S,
    parentId: S,
    timestamp: T,
    operation: S,
    engine: S,
    isBlindAppend: B,
    metrics: "map",
    parameters: "map",
  },
} as const satisfies Record<TableName, Record<string, FieldKind>>;

/** The members of the objects that a field can hold, with their kinds. */
export const NESTED_FIELDS = {
  distribution: { count: I, sum: I, minimum: I, maximum: I, firstQuartile: I, median: I, thirdQuartile: I },
  delta: {
    minReaderVersion: I,
    minWriterVersion: I,
    readerFeatures: "strings",
    writerFeatures: "strings",
    checkpointVersion: I,
    commitsAfterCheckpoint: I,
    domains: "strings",
  },
  iceberg: {
    formatVersion: I,
    currentSchemaId: I,
    schemaCount: I,
    snapshotCount: I,
    refCount: I,
    manifestCount: I,
    manifestSize: I,
    statisticsFileCount: I,
  },
} as const satisfies Record<"distribution" | "delta" | "iceberg", Record<string, FieldKind>>;

/** The members of the `collection` of the manifest, with their kinds. */
export const COLLECTION_FIELDS = {
  collectorApp: S,
  collectorAppVersion: S,
  collectorLib: S,
  collectorLibVersion: S,
  startedAt: T,
  completedAt: T,
  level: I,
  includesDataValues: B,
  includesPartitionValues: B,
  requestCount: I,
  bytesRead: I,
} as const satisfies Record<string, FieldKind>;

/**
 * The required fields of each table: the "Required" line of each object of the model, the `id` of the tables that others
 * refer to, and the reference to the parent and the `position` that section 5.3 of the serialization adds. The
 * reference to the parent is required in every table but `issues`.
 */
export const REQUIRED: Readonly<Record<TableName, readonly string[]>> = {
  sources: ["id", "kind"],
  tables: ["id", "name", "format", "detailDepth"],
  columns: ["id", "table", "name", "dataType"],
  partitionSpecs: ["id", "table"],
  partitionFields: ["partitionSpec", "position"],
  sortOrders: ["id", "table"],
  sortFields: ["sortOrder", "position", "sourceColumn", "direction"],
  physicalSchemas: ["id", "table"],
  physicalColumns: ["id", "physicalSchema", "ordinal", "path", "physicalType"],
  writers: ["id", "table"],
  operations: ["table", "name"],
  issues: ["code"],
  modelBindings: ["id", "modelName"],
  tableBindings: ["id", "modelBinding", "modelTable", "table"],
  columnBindings: ["tableBinding", "modelColumn", "column"],
  partitions: ["id", "table"],
  files: ["id", "table"],
  rowGroups: ["id", "file", "ordinal", "rowCount"],
  columnChunks: ["rowGroup", "physicalColumn", "compressedSize", "uncompressedSize"],
  deleteFiles: ["id", "table", "kind"],
  commits: ["table", "commitId"],
};

/** A reference from a field of a table to the rows of another table. */
export interface Reference {
  /** The table that holds the reference. */
  readonly table: TableName;
  /** The field that holds the reference. */
  readonly field: string;
  /** The table that the reference points to. */
  readonly target: TableName;
  /** Whether the field holds a list of references. */
  readonly list: boolean;
}

/** Every reference of the model, derived from {@link FIELDS}: the list of section 4.4 of the rules of the kit. */
export const REFERENCES: readonly Reference[] = Object.entries(FIELDS).flatMap(([table, fields]) =>
  Object.entries(fields as Record<string, FieldKind>).flatMap(([field, kind]): Reference[] => {
    const match = /^(refs?):(.+)$/.exec(kind);
    return match ? [{ table: table as TableName, field, target: match[2] as TableName, list: match[1] === "refs" }] : [];
  }),
);

/** The fields that this version knows in a table, or undefined for a table that it does not know. */
export function knownFields(table: string): Readonly<Record<string, FieldKind>> | undefined {
  return Object.prototype.hasOwnProperty.call(FIELDS, table) ? (FIELDS as Record<string, Record<string, FieldKind>>)[table] : undefined;
}

/** Whether a table name is one of the 21 tables of this version. */
export function isTableName(name: string): name is TableName {
  return SUMMARY_TABLES.has(name) || DETAIL_TABLES.has(name);
}

/**
 * Whether a value, not null, has the JSON type of a kind: a number for an integer, a reference, or an id, a string for
 * a string or a timestamp, an array of strings or of numbers for a list, an object of strings for a map, and an object
 * for a nested object, whose members are checked on their own. Only the partition values can have a null element. A
 * number with a fraction has the type of an integer: the integer rule, not the type rule, is about it.
 */
export function hasKindType(kind: FieldKind, value: unknown): boolean {
  switch (kind) {
    case "id":
    case "integer":
      return typeof value === "number";
    case "string":
    case "timestamp":
      return typeof value === "string";
    case "boolean":
      return typeof value === "boolean";
    case "strings":
      return Array.isArray(value) && value.every((item) => typeof item === "string");
    case "nullableStrings":
      return Array.isArray(value) && value.every((item) => typeof item === "string" || item === null);
    case "map":
      return isObject(value) && Object.values(value).every((item) => typeof item === "string");
    case "distribution":
    case "delta":
    case "iceberg":
      return isObject(value);
    default:
      return kind.startsWith("refs:") ? Array.isArray(value) && value.every((item) => typeof item === "number") : typeof value === "number";
  }
}

/** The kind of a field, as an error message says it. */
export function describeKind(kind: FieldKind): string {
  switch (kind) {
    case "id":
    case "integer":
      return "numbers";
    case "string":
    case "timestamp":
      return "strings";
    case "boolean":
      return "booleans";
    case "strings":
      return "lists of strings";
    case "nullableStrings":
      return "lists of strings or nulls";
    case "map":
      return "objects whose values are strings";
    case "distribution":
    case "delta":
    case "iceberg":
      return "objects";
    default:
      return kind.startsWith("refs:") ? "lists of numbers" : "numbers";
  }
}

/**
 * The values of a field as a reader gives them: a value whose JSON type is not the type of the field is null, and so is
 * a member of a nested object whose type is not the type of the member. The array is returned as it is when every value
 * has its type, and copied otherwise.
 */
export function withTypedValues(kind: FieldKind, values: unknown[]): unknown[] {
  let result = values;
  for (let i = 0; i < values.length; i++) {
    const value = values[i];
    if (value === null || value === undefined) {
      continue;
    }
    let typed: unknown = hasKindType(kind, value) ? value : null;
    if (typed !== null && (kind === "distribution" || kind === "delta" || kind === "iceberg")) {
      typed = withTypedMembers(NESTED_FIELDS[kind], typed as Record<string, unknown>);
    }
    if (typed !== value) {
      if (result === values) {
        result = values.slice();
      }
      result[i] = typed;
    }
  }
  return result;
}

/** A nested object with null in place of every member that this version knows and whose type is wrong. */
function withTypedMembers(members: Readonly<Record<string, FieldKind>>, value: Record<string, unknown>): Record<string, unknown> {
  let result = value;
  for (const [name, kind] of Object.entries(members)) {
    const member = Object.prototype.hasOwnProperty.call(value, name) ? value[name] : undefined;
    if (member !== null && member !== undefined && !hasKindType(kind, member)) {
      if (result === value) {
        result = { ...value };
      }
      result[name] = null;
    }
  }
  return result;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

// Compile-time checks that the interfaces of types.ts have exactly the fields of FIELDS, with the types of their kinds.
type KindType<K> = K extends "id" | "integer" | `ref:${string}`
  ? number
  : K extends "string" | "timestamp"
    ? string
    : K extends "boolean"
      ? boolean
      : K extends "strings"
        ? string[]
        : K extends "nullableStrings"
          ? (string | null)[]
          : K extends `refs:${string}`
          ? number[]
          : K extends "map"
            ? Record<string, string>
            : K extends "distribution"
              ? Distribution
              : K extends "delta"
                ? DeltaDetails
                : K extends "iceberg"
                  ? IcebergDetails
                  : never;
type Equal<X, Y> = (<V>() => V extends X ? 1 : 2) extends <V>() => V extends Y ? 1 : 2 ? true : false;
type FieldsOf<T extends ColumnarTable> = { -readonly [F in Exclude<keyof T, "length">]-?: NonNullable<T[F]> };
type FieldsFromKinds<S> = { -readonly [F in keyof S]-?: (KindType<S[F]> | null)[] };
type Matches<N extends TableName> = Equal<FieldsOf<(SummaryTables & DetailTables)[N]>, FieldsFromKinds<(typeof FIELDS)[N]>>;
type AllMatch = { [N in TableName]: Matches<N> }[TableName];
type Assert<T extends true> = T;
// If an interface of types.ts and FIELDS disagree on a table, AllMatch is false and this line does not compile.
type SchemaMatchesTypes = Assert<AllMatch>;
