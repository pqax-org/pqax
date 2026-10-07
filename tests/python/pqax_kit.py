"""What the Python tests share: where the conformance kit is, what it expects, and the numbers of the expected shape.

The numbers are computed here from the tables that a reader returns, as conformance/README.md defines them, so that
the tests check the reader and not a copy of its code. Nothing in this module imports pqax.
"""
from __future__ import annotations

import io
import json
import pathlib
import re
import zipfile
from collections import defaultdict
from typing import Any, Callable, Dict, Iterable, List, Mapping, Optional, Set, Tuple


def find_root(start: pathlib.Path) -> pathlib.Path:
    """The root of the repository: the first folder, from start upward, with conformance/ and Pqax.slnx."""
    for folder in (start, *start.parents):
        if (folder / "conformance").is_dir() and (folder / "Pqax.slnx").is_file():
            return folder
    raise RuntimeError(f"No folder with conformance/ and Pqax.slnx above {start}.")


ROOT = find_root(pathlib.Path(__file__).resolve().parent)
KIT = ROOT / "conformance"
SAMPLES = KIT / "samples"
INVALID = SAMPLES / "invalid"
EXPECTED = KIT / "expected"
SCHEMA = KIT / "schema"
EMBEDDED_PART = "Footprint.pqax"
ROWS_PER_PART = 250_000

SUMMARY_TABLES = ("sources", "tables", "columns", "partitionSpecs", "partitionFields", "sortOrders", "sortFields",
                  "physicalSchemas", "physicalColumns", "writers", "operations", "issues", "modelBindings",
                  "tableBindings", "columnBindings")
DETAIL_TABLES = ("partitions", "files", "rowGroups", "columnChunks", "deleteFiles", "commits")
FORMAT_TABLES = SUMMARY_TABLES + DETAIL_TABLES

# The three invalid samples that a reader refuses when it opens them, with the code of the refusal.
REFUSED = {
    "container.manifestMissing.pqax": "container.manifestMissing",
    "manifest.format.pqax": "manifest.format",
    "manifest.versionUnknown.pqax": "manifest.versionUnknown",
}

# The codes of section 4.1 of the contracts, which rules.md repeats.
PLAN_CODES = frozenset({
    "container.method", "container.encrypted", "container.zip64", "container.partName", "container.caseCollision",
    "container.manifestMissing", "json.invalid", "json.topLevel", "json.bom", "json.duplicateMember", "json.integer",
    "json.integerRange", "json.timestamp", "json.type", "manifest.required", "manifest.format", "manifest.formatVersion",
    "manifest.versionUnknown", "manifest.firstPart", "manifest.duplicatePart", "manifest.partMissing", "part.tables",
    "part.kind", "part.singleTable", "part.tooLarge", "part.rowCount", "table.required", "table.idUnique",
    "table.reference", "depth.files", "depth.rowGroups", "depth.columnChunks", "totals.fileCount", "totals.fileSize",
    "totals.rowGroupCount", "totals.compressedSize", "totals.columnCompressedSize", "embedded.partMissing",
})

# The keys of an expected file that a reader computes, as opposed to the facts about the sample file.
SHAPE_KEYS = ("format", "formatVersion", "parts", "rows", "tablesFileSize", "tablesRowCount",
              "columnChunksCompressedSize", "tables", "largestColumns")


def load_json(path: pathlib.Path) -> Any:
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def expected_paths() -> List[pathlib.Path]:
    """Every file of conformance/expected, sorted."""
    return sorted(EXPECTED.glob("*.json"))


def expected_by_name() -> Dict[str, Dict[str, Any]]:
    """The expected results, by the name of the expected file without .json, such as sample."""
    return {path.stem: load_json(path) for path in expected_paths()}


def invalid_cases() -> Dict[str, Dict[str, Any]]:
    """conformance/samples/invalid/expected.json: for each invalid file, the codes that a validator must report."""
    return load_json(INVALID / "expected.json")


def rule_codes() -> Set[str]:
    """The codes of section 1 of conformance/rules.md."""
    text = (KIT / "rules.md").read_text(encoding="utf-8")
    section = text.split("## 1. The codes", 1)[1].split("\n## ", 1)[0]
    codes = set(re.findall(r"^\| `([A-Za-z0-9]+\.[A-Za-z0-9]+)` \|", section, flags=re.MULTILINE))
    if len(codes) < 30:
        raise RuntimeError(f"Read only {len(codes)} codes from rules.md; its layout changed.")
    return codes


def code_pattern(code: str) -> "re.Pattern[str]":
    """A pattern that finds a code as a word of its own, and not inside a file name such as json.bom.pqax."""
    return re.compile(r"(?<![\w.])" + re.escape(code) + r"(?![\w.])")


# ---- the expected shape ----

def stated_rows(manifest: Mapping[str, Any]) -> Dict[str, int]:
    """For every table that the manifest states, the sum of its rows over the parts."""
    rows: Dict[str, int] = {}
    for part in manifest.get("parts", []):
        for name, count in part.get("tables", {}).items():
            rows[name] = rows.get(name, 0) + count
    return rows


def row_total(table: Mapping[str, List[Any]]) -> int:
    """The rows of a table returned as a dictionary of fields: the length of its fields, 0 for {}."""
    lengths = {len(values) for values in table.values()}
    if len(lengths) > 1:
        raise AssertionError(f"The fields of a table have different lengths: {sorted(lengths)}")
    return lengths.pop() if lengths else 0


def field(table: Mapping[str, List[Any]], name: str) -> List[Any]:
    """The values of a field, or None in every row when the table does not have the field."""
    values = table.get(name)
    return list(values) if values is not None else [None] * row_total(table)


def compute_shape(manifest: Mapping[str, Any], row_count: Callable[[str], int],
                  table: Callable[[str], Mapping[str, List[Any]]]) -> Dict[str, Any]:
    """The keys of SHAPE_KEYS, computed as conformance/README.md says, from what a reader returns.

    row_count(name) is the row count of the reader, and table(name) a table read whole, as a dictionary of fields.
    """
    tables = table("tables")
    chunks = table("columnChunks")
    physical_columns = table("physicalColumns")
    physical_schemas = table("physicalSchemas")

    schema_table = dict(zip(field(physical_schemas, "id"), field(physical_schemas, "table")))
    column_schema = dict(zip(field(physical_columns, "id"), field(physical_columns, "physicalSchema")))
    column_column = dict(zip(field(physical_columns, "id"), field(physical_columns, "column")))
    chunk_count: Dict[Any, int] = defaultdict(int)
    chunk_size: Dict[Any, int] = defaultdict(int)
    column_size: Dict[Any, int] = defaultdict(int)
    for physical_column, size in zip(field(chunks, "physicalColumn"), field(chunks, "compressedSize")):
        size = size or 0
        schema = column_schema.get(physical_column)
        owner = schema_table.get(schema) if schema is not None else None
        if owner is not None:
            chunk_count[owner] += 1
            chunk_size[owner] += size
        column = column_column.get(physical_column)
        if column is not None:
            column_size[column] += size
    largest = sorted(column_size.items(), key=lambda item: (-item[1], item[0]))[:3]
    return {
        "format": manifest.get("format"),
        "formatVersion": manifest.get("formatVersion"),
        "parts": [part.get("name") for part in manifest.get("parts", [])],
        "rows": {name: row_count(name) for name in stated_rows(manifest)},
        "tablesFileSize": sum(value for value in field(tables, "fileSize") if value is not None),
        "tablesRowCount": sum(value for value in field(tables, "rowCount") if value is not None),
        "columnChunksCompressedSize": sum(value for value in field(chunks, "compressedSize") if value is not None),
        "tables": [{"id": identifier, "fileCount": file_count, "columnChunkCount": chunk_count.get(identifier, 0),
                    "columnChunkCompressedSize": chunk_size.get(identifier, 0)}
                   for identifier, file_count in zip(field(tables, "id"), field(tables, "fileCount"))],
        "largestColumns": [{"id": column, "compressedSize": size} for column, size in largest],
    }


def shape_differences(actual: Mapping[str, Any], expected: Mapping[str, Any],
                      keys: Iterable[str] = SHAPE_KEYS) -> List[str]:
    """The keys whose values differ, with both values, for a readable assertion message."""
    return [f"{key}: got {actual.get(key)!r}, expected {expected.get(key)!r}"
            for key in keys if actual.get(key) != expected.get(key)]


# ---- a minimal reader with the standard library, to check the kit itself ----

def footprint_bytes(path: pathlib.Path, part_name: str = EMBEDDED_PART) -> bytes:
    """The bytes of the PQAX file: the file itself, or its part Footprint.pqax when it is a package."""
    data = path.read_bytes()
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        names = archive.namelist()
        if "manifest.json" not in names and part_name in names:
            return archive.read(part_name)
    return data


def concatenate(pieces: List[Tuple[Mapping[str, Any], int]]) -> Dict[str, List[Any]]:
    """The fields of a table from its parts, in order, with None where a part leaves a field out."""
    result: Dict[str, List[Any]] = {}
    total = 0
    for piece, count in pieces:
        arrays = {name: values for name, values in piece.items() if isinstance(values, list)}
        for name in arrays:
            if name not in result:
                result[name] = [None] * total
        for name, values in result.items():
            values.extend(arrays.get(name, [None] * count))
        total += count
    return result


def read_plain(data: bytes) -> Tuple[Dict[str, Any], Dict[str, Dict[str, List[Any]]]]:
    """The manifest and every table of a valid PQAX file, read with zipfile and json only."""
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        def load(name: str) -> Any:
            return json.loads(archive.read(name).decode("utf-8-sig"))

        manifest = load("manifest.json")
        pieces: Dict[str, List[Tuple[Mapping[str, Any], int]]] = defaultdict(list)
        for part in manifest["parts"]:
            content = load(part["name"])
            for name, count in part["tables"].items():
                pieces[name].append((content.get(name, {}), count))
    return manifest, {name: concatenate(parts) for name, parts in pieces.items()}


def entry_spans(data: bytes) -> Dict[str, Tuple[int, int]]:
    """For each entry of a zip archive, the bytes from its local header to the next header or the central directory."""
    end = data.rfind(b"PK\x05\x06")
    if end < 0:
        raise ValueError("not a zip archive")
    directory = int.from_bytes(data[end + 16:end + 20], "little")
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = sorted(archive.infolist(), key=lambda info: info.header_offset)
    spans: Dict[str, Tuple[int, int]] = {}
    for index, info in enumerate(entries):
        stop = entries[index + 1].header_offset if index + 1 < len(entries) else directory
        spans[info.filename] = (info.header_offset, stop)
    return spans


def touched_entries(spans: Mapping[str, Tuple[int, int]], ranges: Iterable[Tuple[int, int]]) -> Set[str]:
    """The entries whose bytes overlap one of the ranges read, each range as (start, stop)."""
    touched = set()
    for name, (low, high) in spans.items():
        if any(start < high and low < stop for start, stop in ranges):
            touched.add(name)
    return touched


class CountingReader(io.RawIOBase):
    """A seekable binary file over bytes that records every range that is read, as (start, stop)."""

    def __init__(self, data: bytes) -> None:
        super().__init__()
        self._data = data
        self._position = 0
        self.ranges: List[Tuple[int, int]] = []

    def readable(self) -> bool:
        return True

    def seekable(self) -> bool:
        return True

    def tell(self) -> int:
        return self._position

    def seek(self, offset: int, whence: int = io.SEEK_SET) -> int:
        if whence == io.SEEK_SET:
            position = offset
        elif whence == io.SEEK_CUR:
            position = self._position + offset
        elif whence == io.SEEK_END:
            position = len(self._data) + offset
        else:
            raise ValueError(f"whence {whence}")
        if position < 0:
            raise ValueError("negative position")
        self._position = position
        return position

    def readinto(self, buffer: Any) -> int:
        view = memoryview(buffer).cast("B")
        start = self._position
        chunk = self._data[start:start + len(view)]
        view[:len(chunk)] = chunk
        self._position = start + len(chunk)
        if chunk:
            self.ranges.append((start, start + len(chunk)))
        return len(chunk)


def rewrite_zip(source: pathlib.Path, target: pathlib.Path,
                change: Callable[[str, bytes], Optional[bytes]]) -> None:
    """Copies a zip archive entry by entry. change(name, data) returns new bytes, or None to keep the entry."""
    with zipfile.ZipFile(source) as original, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as copy:
        for info in original.infolist():
            data = original.read(info.filename)
            replaced = change(info.filename, data)
            copy.writestr(info.filename, data if replaced is None else replaced)


def compact(value: Any) -> bytes:
    """JSON with no whitespace, as a writer of the format should write it."""
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def drop_entry(source: pathlib.Path, target: pathlib.Path, name: str) -> None:
    """Copies a zip archive without one of its entries."""
    with zipfile.ZipFile(source) as original, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as copy:
        for info in original.infolist():
            if info.filename != name:
                copy.writestr(info.filename, original.read(info.filename))


def write_package_without_footprint(target: pathlib.Path) -> None:
    """A VPAX-like package with [Content_Types].xml and DaxModel.json, and no Footprint.pqax."""
    with zipfile.ZipFile(SAMPLES / "embedded-stored.vpax") as original, \
            zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as copy:
        for info in original.infolist():
            if info.filename != EMBEDDED_PART:
                copy.writestr(info.filename, original.read(info.filename))


# ---- the JSON Schema of the kit ----

def schema_validators() -> Optional[Dict[str, Any]]:
    """Validators for the manifest, summary.json, and the other parts, or None when jsonschema is not installed."""
    try:
        from jsonschema import Draft202012Validator
        from referencing import Registry, Resource
    except ImportError:
        return None
    registry = Registry()
    uris: Dict[str, str] = {}
    for name in ("manifest", "part"):
        path = SCHEMA / f"{name}.schema.json"
        schema = load_json(path)
        Draft202012Validator.check_schema(schema)
        uris[name] = path.as_uri()
        registry = registry.with_resource(uris[name], Resource.from_contents(schema))

    def make(reference: str) -> Any:
        return Draft202012Validator({"$ref": reference}, registry=registry)

    return {"manifest": make(uris["manifest"]), "summary": make(uris["part"] + "#/$defs/summaryPart"),
            "detail": make(uris["part"] + "#/$defs/detailPart")}


def schema_errors(data: bytes, validators: Mapping[str, Any]) -> List[str]:
    """Every error of the schema in the manifest and in the parts that it lists, as text."""
    errors: List[str] = []
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        def load(name: str) -> Any:
            return json.loads(archive.read(name).decode("utf-8"))

        manifest = load("manifest.json")
        errors += [f"manifest.json: {error.message}" for error in validators["manifest"].iter_errors(manifest)]
        for part in manifest.get("parts", []):
            validator = validators["summary"] if part["name"] == "summary.json" else validators["detail"]
            errors += [f"{part['name']}: {error.message}" for error in validator.iter_errors(load(part["name"]))]
    return errors
