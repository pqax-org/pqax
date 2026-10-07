/**
 * The codes of the validation rules, as section 4.1 of the contracts lists them. Every PQAX validator reports
 * the same codes.
 */
export type ValidationCode =
  | "container.method"
  | "container.encrypted"
  | "container.zip64"
  | "container.partName"
  | "container.caseCollision"
  | "container.manifestMissing"
  | "json.invalid"
  | "json.topLevel"
  | "json.bom"
  | "json.duplicateMember"
  | "json.integer"
  | "json.integerRange"
  | "json.timestamp"
  | "json.type"
  | "manifest.required"
  | "manifest.format"
  | "manifest.formatVersion"
  | "manifest.versionUnknown"
  | "manifest.firstPart"
  | "manifest.duplicatePart"
  | "manifest.partMissing"
  | "part.tables"
  | "part.kind"
  | "part.singleTable"
  | "part.tooLarge"
  | "part.rowCount"
  | "table.required"
  | "table.idUnique"
  | "table.reference"
  | "depth.files"
  | "depth.rowGroups"
  | "depth.columnChunks"
  | "totals.fileCount"
  | "totals.fileSize"
  | "totals.rowGroupCount"
  | "totals.compressedSize"
  | "totals.columnCompressedSize";

/**
 * The code of a {@link PqaxError}: the code of the rule that the file breaks, or `embedded.partMissing` when a package
 * has no part with the name that {@link openEmbedded} looks for.
 */
export type PqaxErrorCode = ValidationCode | "embedded.partMissing";

/**
 * The error that the reader throws when it cannot open a file or read a part of it.
 *
 * Its code is the code of the rule that the file breaks. A file that is not a zip archive, or whose central directory
 * is damaged, has the code `container.manifestMissing`, because no `manifest.json` entry can be found in it. A part
 * whose bytes are damaged or cannot be inflated has the code `json.invalid`, because its content is not a JSON text.
 */
export class PqaxError extends Error {
  /** The code of the rule that the file breaks. */
  readonly code: PqaxErrorCode;
  /** The name of the part or entry concerned, when there is one. */
  readonly part: string | undefined;

  /**
   * Creates an error.
   * @param code The code of the rule that the file breaks.
   * @param message What went wrong.
   * @param part The name of the part or entry concerned.
   * @param cause The error that caused this one.
   */
  constructor(code: PqaxErrorCode, message: string, part?: string, cause?: unknown) {
    super(message, cause === undefined ? undefined : { cause });
    this.name = "PqaxError";
    this.code = code;
    this.part = part;
  }
}
