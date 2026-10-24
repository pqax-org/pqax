"""Exporting a footprint as tables that the exports of later days append to: CSV, Parquet, or Delta.

    from pqax import export

    result = export.export("footprint.pqax", "exports", format="parquet")
    print(result["footprintId"], result["tables"])

The export writes one table for each table of the format that the file has rows for, with its fields, and four more:
tableMeasures, columnMeasures, and collectionMeasures, with the derived measures of pqax.measures, and footprints,
with one row for the footprint. Every row starts with footprintId, the SHA-256 of the bytes of the PQAX file, and
collectedAt, the time of the collection, so that the exports of several days stay apart in one table per name. A
footprint already in the folder is refused. CSV needs the standard library only; Parquet needs pyarrow, the extra
pqax[parquet], and Delta needs deltalake and pyarrow, the extra pqax[delta].
"""
from __future__ import annotations

import datetime
import hashlib
import importlib
import io
import json
import os
import sys
from typing import Any, Dict, List, Mapping, Optional, Sequence, Tuple

from . import measures
from ._container import open_archive, read_embedded
from ._errors import PqaxError
from ._json import format_timestamp, parse_timestamp
from ._schema import (
    COLLECTION_FIELDS, EMBEDDED_PART, FIELDS, MANIFEST_PART, OBFUSCATION_MEMBERS, TABLES, TYPES,
)
from .reader import PqaxDocument

FORMATS = ("csv", "parquet", "delta")
"""The formats of an export."""

IF_EXISTS = ("error", "skip")
"""What the export does with a footprint that the folder already has: raise AlreadyExportedError, or write nothing."""

MEASURE_TABLES = ("tableMeasures", "columnMeasures", "collectionMeasures")
"""The tables of the derived measures, for the tables, the columns, and the collection."""

FOOTPRINTS = "footprints"
"""The table with one row for each footprint exported. It is written last, so a footprint in it is complete."""

_EXTRAS = {"parquet": ("pyarrow",), "delta": ("deltalake", "pyarrow")}
_MOUNTS_WITHOUT_RENAME = ("/lakehouse/", "/dbfs/")


def _delta_storage_options(location: Any) -> Optional[Dict[str, str]]:
    """The storage options of deltalake for a folder. deltalake commits the Delta log by renaming a file, and a mounted
    file system such as /lakehouse/default in a Fabric notebook or /dbfs on Databricks has no rename: its option
    allow_unsafe_rename commits by a copy and a delete there. A plain folder needs no option."""
    path = str(location).replace("\\", "/")
    return {"allow_unsafe_rename": "true"} if path.startswith(_MOUNTS_WITHOUT_RENAME) else None



# The kind of a column: string, integer, number, boolean, timestamp, date, or json (the text of a list, a map, or an
# object). The kind is fixed by the format, not by the values, so that the exports of several days have one schema.
_KIND_OF_TYPE = {"integer": "integer", "string": "string", "timestamp": "timestamp", "boolean": "boolean",
                 "strings": "json", "integers": "json", "map": "json", "object": "json"}
_KEY = (("footprintId", "string"), ("collectedAt", "timestamp"))

_TABLE_MEASURES: Tuple[Tuple[str, str, Tuple[str, ...]], ...] = (
    # (column, kind, path in the dict of measure_tables); a path that starts with "@" is a field of the table row.
    ("id", "integer", ("id",)), ("name", "string", ("name",)), ("label", "string", ("label",)),
    ("sourceName", "string", ("@sourceName",)), ("detailDepth", "string", ("@detailDepth",)),
    ("rowCount", "integer", ("rowCount",)), ("fileCount", "integer", ("fileCount",)),
    ("fileSize", "integer", ("fileSize",)),
    *((key, "number", ("stack", key)) for key in measures.STACK_COMPONENTS),
    ("deletedSource", "string", ("stack", "deletedSource")), ("listed", "boolean", ("stack", "listed")),
    *((key, "number", ("stack", key)) for key in ("waste", "overhead", "data", "total", "wasteShare", "overheadShare")),
    *((f"{key}DataShare", "number", ("stack", "shares", key)) for key in measures.STACK_COMPONENTS),
    ("nonLiveFileCount", "integer", ("@nonLiveFileCount",)),
    ("nonLiveOldestRemovedAt", "timestamp", ("@nonLiveOldestRemovedAt",)),
    ("nonLiveNewestRemovedAt", "timestamp", ("@nonLiveNewestRemovedAt",)),
    ("unreferencedFileCount", "integer", ("@unreferencedFileCount",)),
    ("smallFileSize", "integer", ("smallFiles", "threshold")), ("smallFileCount", "integer", ("smallFiles", "count")),
    ("smallFileShare", "number", ("smallFiles", "shareAtLeast")),
    ("tinyFileSize", "integer", ("tinyFiles", "threshold")), ("tinyFileCount", "integer", ("tinyFiles", "count")),
    ("tinyFileShare", "number", ("tinyFiles", "shareAtLeast")),
    *((f"fileSizes{key[0].upper()}{key[1:]}", "integer", ("files", key))
      for key in ("count", "sum", "minimum", "firstQuartile", "median", "thirdQuartile", "maximum")),
    ("vOrderShare", "number", ("files", "vOrder", "share")),
    ("vOrderBytesShare", "number", ("files", "vOrder", "bytesShare")),
    *((key, "number", ("measures", key)) for key in (
        "compressionRatio", "deletedRowShare", "averageFileSize", "averageRowGroupRows", "footerCoverage",
        "staleColumnShare", "nonLiveShare", "averagePartitionSize", "dictionaryFallbackShare")),
    ("lastCommitAt", "timestamp", ("maintenance", "lastCommitAt")),
    ("commitsPerDay", "number", ("maintenance", "commitsPerDay")),
    ("daysSinceLastCommit", "integer", ("maintenance", "daysSinceLastCommit")),
    ("hasVacuum", "boolean", ("maintenance", "hasVacuum")),
    ("lastVacuumAt", "timestamp", ("maintenance", "lastVacuumAt")),
    ("hasOptimize", "boolean", ("maintenance", "hasOptimize")),
    ("lastOptimizeAt", "timestamp", ("maintenance", "lastOptimizeAt")),
    ("partitions", "integer", ("partitions", "count")), ("partitionsKept", "integer", ("partitions", "kept")),
    ("partitionsOldestModifiedAt", "timestamp", ("partitions", "oldestModifiedAt")),
    ("partitionsNewestModifiedAt", "timestamp", ("partitions", "newestModifiedAt")),
)
_COLUMN_MEASURES = (
    ("id", "integer"), ("table", "integer"), ("tableLabel", "string"), ("name", "string"), ("label", "string"),
    ("nullShare", "number"), ("compressionRatio", "number"), ("dictionaryShare", "number"),
    ("dictionaryFallbackShare", "number"), ("averageValueLength", "number"), ("filesWithoutColumnShare", "number"),
)
_COLLECTION_MEASURES = (
    ("sources", "integer"), ("tables", "integer"), ("columns", "integer"), ("files", "integer"),
    ("fileSize", "integer"), ("rowCount", "integer"), ("footprint", "number"), ("waste", "number"),
    ("overhead", "number"), ("depths", "json"),
)
_COLLECTION_KINDS = {"collectorApp": "string", "collectorAppVersion": "string", "collectorLib": "string",
                     "collectorLibVersion": "string", "startedAt": "timestamp", "completedAt": "timestamp",
                     "level": "integer", "includesDataValues": "boolean", "includesPartitionValues": "boolean",
                     "requestCount": "integer", "bytesRead": "integer"}
_FOOTPRINTS = (
    ("collectionDate", "date"), ("file", "string"), ("embeddedPart", "string"), ("format", "string"),
    ("formatVersion", "string"), *((name, _COLLECTION_KINDS[name]) for name in COLLECTION_FIELDS),
    *((name, "string") for name in OBFUSCATION_MEMBERS),
)

SCHEMAS: Dict[str, Tuple[Tuple[str, str], ...]] = {
    **{name: _KEY + tuple((field, _KIND_OF_TYPE[TYPES[name][field]]) for field in FIELDS[name]) for name in TABLES},
    "tableMeasures": _KEY + tuple((column, kind) for column, kind, _ in _TABLE_MEASURES),
    "columnMeasures": _KEY + _COLUMN_MEASURES,
    "collectionMeasures": _KEY + _COLLECTION_MEASURES,
    FOOTPRINTS: _KEY + _FOOTPRINTS,
}
"""The columns of every output table with their kind: string, integer, number, boolean, timestamp, date, or json."""

COLUMNS: Dict[str, Tuple[str, ...]] = {name: tuple(column for column, _ in schema) for name, schema in SCHEMAS.items()}
"""The columns of every output table, in order."""

__all__ = ["AlreadyExportedError", "COLUMNS", "FOOTPRINTS", "FORMATS", "IF_EXISTS", "MEASURE_TABLES", "SCHEMAS",
           "export", "footprint_id"]


class AlreadyExportedError(PqaxError):
    """The footprints table of the folder already has the footprint, so the export would write its rows twice."""

    code = "export.duplicate"


def footprint_id(data: bytes) -> str:
    """The identifier of a footprint in an export: the SHA-256 of the bytes of its PQAX file, in lowercase hex."""
    return hashlib.sha256(data).hexdigest()


# ---- reading the footprint ----

def _read_bytes(source: Any) -> Tuple[bytes, Optional[str]]:
    """The bytes of the source and its base name: a path, bytes, or a binary file object."""
    if isinstance(source, (bytes, bytearray, memoryview)):
        return bytes(source), None
    if isinstance(source, (str, os.PathLike)):
        with open(source, "rb") as fp:
            return fp.read(), os.path.basename(os.fspath(source))
    if hasattr(source, "read"):
        if hasattr(source, "seek"):
            source.seek(0)
        name = getattr(source, "name", None)
        return source.read(), os.path.basename(name) if isinstance(name, str) else None
    raise TypeError(f"Expected a path, bytes, or a binary file object, not {type(source).__name__}")


def _open(data: bytes, part: Optional[str]) -> Tuple[bytes, PqaxDocument]:
    """The bytes of the PQAX file and the document: the file itself, or the part of a package that embeds it."""
    if part is None:
        archive, fp, owned = open_archive(data)
        try:
            names = archive.namelist()
        finally:
            archive.close()
            if owned:
                fp.close()
        if MANIFEST_PART in names or EMBEDDED_PART not in names:
            archive, fp, owned = open_archive(data)
            return data, PqaxDocument(archive, fp, owned)
        part = EMBEDDED_PART
    inner, entry, compression = read_embedded(data, part)
    archive, fp, owned = open_archive(inner)
    return inner, PqaxDocument(archive, fp, owned, embedded_part=entry, embedded_compression=compression)


def _collected_at(collection: Mapping[str, Any]) -> Optional[datetime.datetime]:
    """completedAt, else startedAt: the first that parses as a timestamp."""
    for name in ("completedAt", "startedAt"):
        value = _timestamp(collection.get(name))
        if value is not None:
            return value
    return None


# ---- values in the kind of their column ----

def _timestamp(value: Any) -> Optional[datetime.datetime]:
    if isinstance(value, datetime.datetime):
        return value
    if not isinstance(value, str):
        return None
    try:
        return parse_timestamp(value)
    except (ValueError, TypeError, OverflowError):
        return None


def _value(kind: str, value: Any) -> Any:
    """A value of the reader or of the measures as the Python value of a column of that kind, None when it has none."""
    if value is None:
        return None
    if kind == "string":
        return value if isinstance(value, str) else None
    if kind == "integer":
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return None
        return int(value) if float(value).is_integer() else None
    if kind == "number":
        return float(value) if isinstance(value, (int, float)) and not isinstance(value, bool) else None
    if kind == "boolean":
        return value if isinstance(value, bool) else None
    if kind == "timestamp":
        return _timestamp(value)
    if kind == "date":
        return value if isinstance(value, datetime.date) else None
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _path(item: Mapping[str, Any], path: Sequence[str]) -> Any:
    value: Any = item
    for key in path:
        if not isinstance(value, Mapping):
            return None
        value = value.get(key)
    return value


class _Table:
    """An output table: its name, the kind of each column, and the values, column by column."""

    def __init__(self, name: str, columns: Dict[str, List[Any]], rows: int) -> None:
        self.name = name
        self.schema = SCHEMAS[name]
        self.columns = columns
        self.rows = rows


def _table(name: str, rows: int, values: Mapping[str, Sequence[Any]], key: Tuple[str, Optional[datetime.datetime]]
           ) -> _Table:
    """A table of the given rows, with the key on each row and None for a column that values lacks."""
    columns: Dict[str, List[Any]] = {"footprintId": [key[0]] * rows, "collectedAt": [key[1]] * rows}
    for column, kind in SCHEMAS[name][2:]:
        given = values.get(column)
        columns[column] = ([_value(kind, given[row] if row < len(given) else None) for row in range(rows)]
                           if isinstance(given, list) else [None] * rows)
    return _Table(name, columns, rows)


def _tables(document: PqaxDocument, file_name: Optional[str], key: Tuple[str, Optional[datetime.datetime]],
            small_file_size: float, tiny_file_size: float) -> Tuple[List[_Table], List[str]]:
    """The output tables of a document, footprints last, and the names of its tables that the format does not have."""
    result: List[_Table] = []
    ignored = [name for name in document.table_names if name not in FIELDS]
    for name in TABLES:
        table = document.table(name)
        rows = max((len(values) for values in table.values() if isinstance(values, list)), default=0)
        if rows:
            result.append(_table(name, rows, table, key))

    measured = measures.measure(document, small_file_size=small_file_size, tiny_file_size=tiny_file_size)
    summary = document.summary
    table_fields = summary.get("tables", {})
    source_fields = summary.get("sources", {})
    source_names: Dict[int, Optional[str]] = {}
    for row, identifier in enumerate(source_fields.get("id", [])):
        if isinstance(identifier, int) and identifier not in source_names:
            names = source_fields.get("name", [])
            source_names[identifier] = names[row] if row < len(names) else None

    def field(name: str, row: int) -> Any:
        values = table_fields.get(name, [])
        return values[row] if row < len(values) else None

    table_rows = measured["tables"]
    by_column: Dict[str, List[Any]] = {column: [] for column, _, _ in _TABLE_MEASURES}
    for row, item in enumerate(table_rows):
        source = field("source", row)
        row_fields = {"sourceName": source_names.get(source) if isinstance(source, int) else None,
                      **{name: field(name, row) for name in ("detailDepth", "nonLiveFileCount",
                                                             "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt",
                                                             "unreferencedFileCount")}}
        for column, _, path in _TABLE_MEASURES:
            by_column[column].append(row_fields.get(path[0][1:]) if path[0].startswith("@") else _path(item, path))
    if table_rows:
        result.append(_table("tableMeasures", len(table_rows), by_column, key))

    labels = {item["id"]: item["label"] for item in table_rows if item["id"] is not None}
    column_rows = measured["columns"]
    if column_rows:
        values = {column: [item.get(column) for item in column_rows] for column, _ in _COLUMN_MEASURES}
        values["tableLabel"] = [labels.get(item["table"]) if item["table"] is not None else None
                                for item in column_rows]
        result.append(_table("columnMeasures", len(column_rows), values, key))

    totals = measured["collection"]["totals"]
    collection_values: Dict[str, List[Any]] = {column: [totals.get(column)] for column, _ in _COLLECTION_MEASURES}
    collection_values["depths"] = [dict(sorted(measured["collection"]["depths"].items()))]
    result.append(_table("collectionMeasures", 1, collection_values, key))

    manifest, collection = document.manifest, document.collection
    collected_at = key[1]
    footprint: Dict[str, List[Any]] = {
        "collectionDate": [collected_at.astimezone(datetime.timezone.utc).date() if collected_at else None],
        "file": [file_name], "embeddedPart": [document.embedded_part], "format": [manifest.get("format")],
        "formatVersion": [manifest.get("formatVersion")],
        **{name: [collection.get(name)] for name in COLLECTION_FIELDS},
        **{name: [manifest.get(name)] for name in OBFUSCATION_MEMBERS},
    }
    result.append(_table(FOOTPRINTS, 1, footprint, key))
    return result, ignored


# ---- writers ----

def _import(format: str) -> Dict[str, Any]:
    """The modules that a format needs, or ImportError naming the extra that installs them."""
    modules: Dict[str, Any] = {}
    for library in _EXTRAS.get(format, ()):
        try:
            modules[library] = importlib.import_module(library)
            if library == "pyarrow":
                modules["pyarrow.parquet"] = importlib.import_module("pyarrow.parquet")
        except ImportError as error:
            raise ImportError(f"export(format={format!r}) needs {library}: pip install \"pqax[{format}]\"") from error
    return modules


def _csv_field(kind: str, value: Any) -> str:
    if value is None:
        return ""
    if kind == "boolean":
        return "true" if value else "false"
    if kind == "integer":
        return str(value)
    if kind == "number":
        return str(int(value)) if value.is_integer() else repr(value)
    if kind == "timestamp":
        return format_timestamp(value)
    if kind == "date":
        return value.isoformat()
    if value == "" or any(character in value for character in ',"\r\n'):
        return '"' + value.replace('"', '""') + '"'
    return value


def _csv_header(path: str) -> Optional[List[str]]:
    """The header of a CSV file, None when the file is missing or empty."""
    if not os.path.exists(path):
        return None
    import csv
    with open(path, "r", encoding="utf-8", newline="") as fp:
        return next(csv.reader(fp), None)


def _csv_column(path: str, column: str) -> List[str]:
    import csv
    with open(path, "r", encoding="utf-8", newline="") as fp:
        reader = csv.reader(fp)
        header = next(reader, None)
        if not header or column not in header:
            return []
        index = header.index(column)
        return [row[index] for row in reader if len(row) > index]


def _arrow_type(pa: Any, kind: str) -> Any:
    return {"string": pa.string(), "json": pa.string(), "integer": pa.int64(), "number": pa.float64(),
            "boolean": pa.bool_(), "timestamp": pa.timestamp("us", tz="UTC"), "date": pa.date32()}[kind]


def _arrow(pa: Any, table: _Table) -> Any:
    fields = [pa.field(column, _arrow_type(pa, kind)) for column, kind in table.schema]
    arrays = [pa.array(table.columns[column], type=_arrow_type(pa, kind)) for column, kind in table.schema]
    return pa.Table.from_arrays(arrays, schema=pa.schema(fields))


def _is_delta(folder: str) -> bool:
    return os.path.isdir(os.path.join(folder, "_delta_log"))


def _has_parquet(folder: str) -> bool:
    return os.path.isdir(folder) and any(name.endswith(".parquet") for name in os.listdir(folder))


class _Writer:
    """Writes the output tables of one format into a folder, and reads the footprints already there."""

    def __init__(self, format: str, folder: str, modules: Dict[str, Any]) -> None:
        self.format = format
        self.folder = folder
        self.modules = modules

    def location(self, name: str) -> str:
        return os.path.join(self.folder, f"{name}.csv" if self.format == "csv" else name)

    def exported(self) -> List[str]:
        """The footprint ids of the footprints table of the folder."""
        location = self.location(FOOTPRINTS)
        if self.format == "csv":
            return _csv_column(location, "footprintId") if os.path.exists(location) else []
        if self.format == "parquet":
            if not _has_parquet(location) or _is_delta(location):
                return []
            pq = self.modules["pyarrow.parquet"]
            ids: List[str] = []
            for name in sorted(os.listdir(location)):
                if name.endswith(".parquet"):
                    ids += pq.read_table(os.path.join(location, name), columns=["footprintId"])["footprintId"].to_pylist()
            return ids
        if not _is_delta(location):
            return []
        table = self.modules["deltalake"].DeltaTable(location, storage_options=_delta_storage_options(location))
        table = table.to_pyarrow_table(columns=["footprintId"])
        return table["footprintId"].to_pylist()

    def check(self, table: _Table) -> None:
        """Raises ValueError when the table cannot be appended to what the folder holds."""
        location = self.location(table.name)
        if self.format == "csv":
            header = _csv_header(location)
            if header is not None and header != list(COLUMNS[table.name]):
                raise ValueError(f"{location} has other columns than the {table.name} table of this version of pqax, "
                                 f"so the rows cannot be appended to it. Export into another folder.")
        elif self.format == "parquet" and _is_delta(location):
            raise ValueError(f"{location} is a Delta table, not a folder of Parquet files: export with --format delta, "
                             f"or into another folder.")
        elif self.format == "delta" and not _is_delta(location) and _has_parquet(location):
            raise ValueError(f"{location} holds Parquet files and is not a Delta table: export with --format parquet, "
                             f"or into another folder.")

    def write(self, table: _Table, footprint: str) -> None:
        location = self.location(table.name)
        if self.format == "csv":
            new = _csv_header(location) is None
            kinds = [kind for _, kind in table.schema]
            names = [column for column, _ in table.schema]
            buffer = io.StringIO()
            if new:
                buffer.write(",".join(names) + "\r\n")
            for row in range(table.rows):
                buffer.write(",".join(_csv_field(kind, table.columns[name][row])
                                      for name, kind in zip(names, kinds)) + "\r\n")
            with open(location, "a", encoding="utf-8", newline="") as fp:
                fp.write(buffer.getvalue())
            return
        data = _arrow(self.modules["pyarrow"], table)
        if self.format == "parquet":
            os.makedirs(location, exist_ok=True)
            target = os.path.join(location, f"{footprint}.parquet")
            temporary = os.path.join(location, f".{footprint}.parquet.tmp")
            self.modules["pyarrow.parquet"].write_table(data, temporary)
            os.replace(temporary, target)
            return
        self.modules["deltalake"].write_deltalake(location, data, mode="append", schema_mode="merge",
                                                  storage_options=_delta_storage_options(location))


def export(path_or_file: Any, to: Any, *, format: str = "csv", part: Optional[str] = None, if_exists: str = "error",
           small_file_size: float = measures.DEFAULT_SMALL_FILE_SIZE,
           tiny_file_size: float = measures.DEFAULT_TINY_FILE_SIZE) -> Dict[str, Any]:
    """Exports a footprint into a folder, appending to the tables that the folder already has.

    path_or_file is a PQAX file, or a package such as a VPAX that embeds one as Footprint.pqax, or as part. to is the
    folder, created when missing. format is csv, parquet (pyarrow), or delta (deltalake and pyarrow). A footprint
    whose id the footprints table of the folder already has raises AlreadyExportedError, or with if_exists="skip" is
    left out; either way nothing is written. Returns {"footprintId", "collectedAt", "format", "folder", "skipped",
    "tables": {name: rows written}, "ignored": [tables of the file that the format does not have]}.
    """
    if format not in FORMATS:
        raise ValueError(f"format is one of {', '.join(FORMATS)}, not {format!r}")
    if if_exists not in IF_EXISTS:
        raise ValueError(f"if_exists is one of {', '.join(IF_EXISTS)}, not {if_exists!r}")
    modules = _import(format)
    data, file_name = _read_bytes(path_or_file)
    inner, document = _open(data, part)
    identifier = footprint_id(inner)
    folder = os.fspath(to)
    with document:
        collected_at = _collected_at(document.collection)
        tables, ignored = _tables(document, file_name, (identifier, collected_at), small_file_size, tiny_file_size)
    result: Dict[str, Any] = {
        "footprintId": identifier, "collectedAt": format_timestamp(collected_at) if collected_at else None,
        "format": format, "folder": folder, "skipped": False, "tables": {}, "ignored": ignored,
    }
    writer = _Writer(format, folder, modules)
    if os.path.isdir(folder) and identifier in writer.exported():
        if if_exists == "skip":
            result["skipped"] = True
            return result
        raise AlreadyExportedError(f"The footprint {identifier}, collected "
                                   f"{result['collectedAt'] or 'at an unknown time'}, is already in {folder}. Nothing "
                                   f"was written.")
    for table in tables:
        writer.check(table)
    os.makedirs(folder, exist_ok=True)
    for table in tables:
        writer.write(table, identifier)
        result["tables"][table.name] = table.rows
    return result


def command(file: str, to: str, format: str, part: Optional[str], if_exists: str,
            out: Any = None, err: Any = None) -> int:
    """python -m pqax export: 0 when written or skipped, 2 when a library is missing or the folder refuses the rows.
    A footprint already in the folder raises AlreadyExportedError, which the command line turns into exit code 1."""
    out = out if out is not None else sys.stdout
    err = err if err is not None else sys.stderr
    try:
        result = export(file, to, format=format, part=part, if_exists=if_exists)
    except (ImportError, ValueError) as error:
        print(f"pqax export: {error}", file=err)
        return 2
    name = os.path.basename(file)
    if result["skipped"]:
        print(f"{name}: the footprint {result['footprintId']} is already in {to}; skipped", file=out)
        return 0
    for table, rows in result["tables"].items():
        print(f"{table}: {rows:,} {'row' if rows == 1 else 'rows'}", file=out)
    if result["ignored"]:
        print(f"{name}: not exported, not tables of the format: {', '.join(result['ignored'])}", file=err)
    if result["collectedAt"] is None:
        print(f"{name}: the collection has no completedAt or startedAt, so its rows have no collectedAt", file=err)
    print(f"{name}: footprint {result['footprintId']}, collected {result['collectedAt'] or 'at an unknown time'}, "
          f"exported as {format} to {to}", file=out)
    return 0

