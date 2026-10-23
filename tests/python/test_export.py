"""pqax export: the tables of a footprint and their derived measures as CSV, Parquet, or Delta, so that the exports of
several days append.

The tests follow the interface of the export (24 October 2026), section 10 of docs/object-model.md, and the kit. The
expected rows come from the document as the reader returns it and from conformance/expected/measures, through the
column mapping of the interface; nothing here comes from the code of the export. The CSV tests need the standard
library only: they read the files with the csv module and with a reader of the rules of the interface, which tells a
null (an empty field) from an empty string (""). The Parquet and Delta tests are skipped when pyarrow or deltalake is
not installed. The command runs in the process, through pqax.__main__.main, with one run in a process of its own.
"""
from __future__ import annotations

import codecs
import csv
import hashlib
import importlib
import json
import os
import re
import subprocess
import sys
import zipfile
from collections import Counter
from datetime import date, datetime, timezone

import pytest

import pqax
from pqax.__main__ import main
from pqax._schema import COLLECTION_FIELDS, FIELDS, TYPES
from pqax_kit import (EMBEDDED_PART, EXPECTED, INVALID, ROOT, SAMPLES, expected_by_name, field, footprint_bytes,
                      load_json, write_package_without_footprint)

KIT = expected_by_name()
NAMES = sorted(KIT)  # the 9 .pqax samples and the 2 .vpax
MEASURES = EXPECTED / "measures"
FORMATS = ["csv", "parquet", "delta"]
COLLECTED_AT = "2026-10-05T20:55:12.749Z"  # collection.completedAt of every sample of the kit
NEXT_COLLECTED_AT = "2026-10-06T20:55:12.749Z"
SAMPLE = SAMPLES / "sample.pqax"
SAMPLE_ID = hashlib.sha256(SAMPLE.read_bytes()).hexdigest()
TIMESTAMP_TEXT = re.compile(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z")
DATE_TEXT = re.compile(r"[0-9]{4}-[0-9]{2}-[0-9]{2}")

# ---- the columns of the interface ----

# The type of a field of the format, TYPES of pqax._schema, as a type of the export.
KIND_OF_TYPE = {"integer": "integer", "string": "string", "timestamp": "timestamp", "boolean": "boolean",
                "strings": "json", "integers": "json", "map": "json", "object": "json"}

STACK = ("live", "nonLive", "unreferenced", "deleted", "stale", "metadata", "footers")
TABLE_MEASURES = ("compressionRatio", "deletedRowShare", "averageFileSize", "averageRowGroupRows", "footerCoverage",
                  "staleColumnShare", "nonLiveShare", "averagePartitionSize", "dictionaryFallbackShare")
FIGURES = ("count", "sum", "minimum", "firstQuartile", "median", "thirdQuartile", "maximum")

# tableMeasures after footprintId and collectedAt, in the order of the interface, with the type of each column.
TABLE_MEASURE_COLUMNS = (
    ("id", "integer"), ("name", "string"), ("label", "string"), ("sourceName", "string"), ("detailDepth", "string"),
    ("rowCount", "integer"), ("fileCount", "integer"), ("fileSize", "integer"),
    ("live", "number"), ("nonLive", "number"), ("unreferenced", "number"), ("deleted", "number"), ("stale", "number"),
    ("metadata", "number"), ("footers", "number"), ("deletedSource", "string"), ("listed", "boolean"),
    ("waste", "number"), ("overhead", "number"), ("data", "number"), ("total", "number"), ("wasteShare", "number"),
    ("overheadShare", "number"),
    ("liveDataShare", "number"), ("nonLiveDataShare", "number"), ("unreferencedDataShare", "number"),
    ("deletedDataShare", "number"), ("staleDataShare", "number"), ("metadataDataShare", "number"),
    ("footersDataShare", "number"),
    ("nonLiveFileCount", "integer"), ("nonLiveOldestRemovedAt", "timestamp"), ("nonLiveNewestRemovedAt", "timestamp"),
    ("unreferencedFileCount", "integer"),
    ("smallFileSize", "integer"), ("smallFileCount", "integer"), ("smallFileShare", "number"),
    ("tinyFileSize", "integer"), ("tinyFileCount", "integer"), ("tinyFileShare", "number"),
    ("fileSizesCount", "integer"), ("fileSizesSum", "integer"), ("fileSizesMinimum", "integer"),
    ("fileSizesFirstQuartile", "integer"), ("fileSizesMedian", "integer"), ("fileSizesThirdQuartile", "integer"),
    ("fileSizesMaximum", "integer"),
    ("vOrderShare", "number"), ("vOrderBytesShare", "number"),
    ("compressionRatio", "number"), ("deletedRowShare", "number"), ("averageFileSize", "number"),
    ("averageRowGroupRows", "number"), ("footerCoverage", "number"), ("staleColumnShare", "number"),
    ("nonLiveShare", "number"), ("averagePartitionSize", "number"), ("dictionaryFallbackShare", "number"),
    ("lastCommitAt", "timestamp"), ("commitsPerDay", "number"), ("daysSinceLastCommit", "integer"),
    ("hasVacuum", "boolean"), ("lastVacuumAt", "timestamp"), ("hasOptimize", "boolean"),
    ("lastOptimizeAt", "timestamp"),
    ("partitions", "integer"), ("partitionsKept", "integer"), ("partitionsOldestModifiedAt", "timestamp"),
    ("partitionsNewestModifiedAt", "timestamp"),
)
COLUMN_MEASURE_COLUMNS = (
    ("id", "integer"), ("table", "integer"), ("tableLabel", "string"), ("name", "string"), ("label", "string"),
    ("nullShare", "number"), ("compressionRatio", "number"), ("dictionaryShare", "number"),
    ("dictionaryFallbackShare", "number"), ("averageValueLength", "number"), ("filesWithoutColumnShare", "number"),
)
COLLECTION_MEASURE_COLUMNS = (
    ("sources", "integer"), ("tables", "integer"), ("columns", "integer"), ("files", "integer"),
    ("fileSize", "integer"), ("rowCount", "integer"), ("footprint", "number"), ("waste", "number"),
    ("overhead", "number"), ("depths", "json"),
)
FOOTPRINT_COLUMNS = (
    ("collectionDate", "date"), ("file", "string"), ("embeddedPart", "string"), ("format", "string"),
    ("formatVersion", "string"),
    ("collectorApp", "string"), ("collectorAppVersion", "string"), ("collectorLib", "string"),
    ("collectorLibVersion", "string"), ("startedAt", "timestamp"), ("completedAt", "timestamp"),
    ("level", "integer"), ("includesDataValues", "boolean"), ("includesPartitionValues", "boolean"),
    ("requestCount", "integer"), ("bytesRead", "integer"),
    ("obfuscatorDictionaryId", "string"), ("obfuscatorLib", "string"), ("obfuscatorLibVersion", "string"),
)
DERIVED_COLUMNS = {"tableMeasures": TABLE_MEASURE_COLUMNS, "columnMeasures": COLUMN_MEASURE_COLUMNS,
                   "collectionMeasures": COLLECTION_MEASURE_COLUMNS, "footprints": FOOTPRINT_COLUMNS}
OUTPUT_TABLES = (*pqax.TABLES, *DERIVED_COLUMNS)


def kinds(table):
    """{column: type} of an output table, in the order of its columns."""
    lead = {"footprintId": "string", "collectedAt": "timestamp"}
    if table in FIELDS:
        return {**lead, **{name: KIND_OF_TYPE[TYPES[table][name]] for name in FIELDS[table]}}
    return {**lead, **dict(DERIVED_COLUMNS[table])}


def columns_of(table):
    return tuple(kinds(table))


# ---- values ----

def moment(text):
    """The instant of a timestamp text, in UTC, or None when the text does not parse as a timestamp."""
    if not isinstance(text, str):
        return None
    try:
        value = datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError:
        return None
    return value.astimezone(timezone.utc) if value.tzinfo is not None else None


def typed(value, kind):
    """A value of the file or of the measures as the export writes it: a moment for a timestamp, the compact JSON
    text for a list, a map, or an object, and the value itself otherwise."""
    if value is None:
        return None
    if kind == "timestamp":
        return moment(value)
    if kind == "json":
        return json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    return value


def number_text(value):
    """The CSV form of a number: the digits of an integral value, else the repr of the float."""
    return str(int(value)) if value.is_integer() else repr(value)


def timestamp_text(value):
    return value.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.") + f"{value.microsecond // 1000:03d}Z"


def collected_at(collection):
    """completedAt, else startedAt: the first that parses as a timestamp, or None."""
    for member in ("completedAt", "startedAt"):
        value = moment(collection.get(member))
        if value is not None:
            return value
    return None


def same(actual, expected):
    """Equality that does not take True for 1 or False for 0."""
    if isinstance(actual, bool) or isinstance(expected, bool):
        return type(actual) is type(expected) and actual == expected
    if expected is None or actual is None:
        return actual is None and expected is None
    return actual == expected


def assert_rows(actual, expected, columns, where):
    assert len(actual) == len(expected), f"{where}: {len(actual)} rows, expected {len(expected)}"
    for index, (got, want) in enumerate(zip(actual, expected)):
        differences = [f"{column}: got {g!r}, expected {w!r}" for column, g, w in zip(columns, got, want)
                       if not same(g, w)]
        assert not differences, f"{where}, row {index}: " + "; ".join(differences)


# ---- the expected rows ----

def sample_path(name):
    return SAMPLES / KIT[name]["file"]


def open_sample(name):
    part = KIT[name]["embeddedPart"]
    path = sample_path(name)
    return pqax.open_embedded(path, part) if part else pqax.open(path)


def format_rows(doc, table, footprint, at):
    """The rows of an output table of the format: those of doc.table(name), every field of FIELDS[name]."""
    count = doc.row_count(table)
    values = doc.table(table)
    table_kinds = kinds(table)
    fields = [field(values, name) if values else [None] * count for name in FIELDS[table]]
    for name, column in zip(FIELDS[table], fields):
        assert len(column) == count, f"{table}.{name}: the reader gives {len(column)} rows, the manifest {count}"
    return [[footprint, at] + [typed(column[row], table_kinds[name]) for name, column in zip(FIELDS[table], fields)]
            for row in range(count)]


def table_measure_values(t, row, source_names):
    """The columns of tableMeasures from a table dict of the measures and the row of the table, as the interface maps
    them."""
    stack = t["stack"]
    files = t["files"]
    v_order = files["vOrder"] or {}
    maintenance = t["maintenance"]
    partitions = t["partitions"]
    values = {key: t[key] for key in ("id", "name", "label", "rowCount", "fileCount", "fileSize")}
    values["sourceName"] = source_names.get(row["source"]) if row["source"] is not None else None
    values["detailDepth"] = row["detailDepth"]
    values.update({key: stack[key] for key in STACK + ("deletedSource", "listed", "waste", "overhead", "data", "total",
                                                       "wasteShare", "overheadShare")})
    values.update({f"{key}DataShare": stack["shares"][key] for key in STACK})
    values.update({key: row[key] for key in ("nonLiveFileCount", "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt",
                                             "unreferencedFileCount")})
    for prefix, key in (("small", "smallFiles"), ("tiny", "tinyFiles")):
        values[f"{prefix}FileSize"] = t[key]["threshold"]
        values[f"{prefix}FileCount"] = t[key]["count"]
        values[f"{prefix}FileShare"] = t[key]["shareAtLeast"]
    values.update({"fileSizes" + key[0].upper() + key[1:]: files[key] for key in FIGURES})
    values["vOrderShare"] = v_order.get("share")
    values["vOrderBytesShare"] = v_order.get("bytesShare")
    values.update({key: t["measures"][key] for key in TABLE_MEASURES})
    values.update({key: maintenance[key] for key in ("lastCommitAt", "commitsPerDay", "daysSinceLastCommit",
                                                     "hasVacuum", "lastVacuumAt", "hasOptimize", "lastOptimizeAt")})
    values["partitions"] = partitions["count"]
    values["partitionsKept"] = partitions["kept"]
    values["partitionsOldestModifiedAt"] = partitions["oldestModifiedAt"]
    values["partitionsNewestModifiedAt"] = partitions["newestModifiedAt"]
    return values


def derived_rows(doc, measured, footprint, at, file_name):
    """The rows of tableMeasures, columnMeasures, collectionMeasures, and footprints. measured has the shape of
    pqax.measures.measure: the expected measures of the kit."""
    tables = doc.table("tables")
    sources = doc.table("sources")
    source_names = dict(zip(field(sources, "id"), field(sources, "name")))
    table_rows = []
    for index, t in enumerate(measured["tables"]):
        row = {name: field(tables, name)[index] for name in FIELDS["tables"]}
        assert row["id"] == t["id"], "the measures of the tables follow the rows of tables"
        values = table_measure_values(t, row, source_names)
        table_rows.append([footprint, at] + [typed(values[name], kind) for name, kind in TABLE_MEASURE_COLUMNS])
    labels = {t["id"]: t["label"] for t in measured["tables"]}
    column_rows = []
    for c in measured["columns"]:
        values = dict(c, tableLabel=labels.get(c["table"]) if c["table"] is not None else None)
        column_rows.append([footprint, at] + [typed(values[name], kind) for name, kind in COLUMN_MEASURE_COLUMNS])
    totals = measured["collection"]["totals"]
    depths = json.dumps(measured["collection"]["depths"], sort_keys=True, ensure_ascii=False, separators=(",", ":"))
    collection_row = [footprint, at] + [totals[name] for name, _ in COLLECTION_MEASURE_COLUMNS[:-1]] + [depths]
    manifest = doc.manifest
    collection = manifest.get("collection") or {}
    footprint_row = {"collectionDate": at.date() if at is not None else None, "file": file_name,
                     "embeddedPart": doc.embedded_part, "format": manifest.get("format"),
                     "formatVersion": manifest.get("formatVersion")}
    footprint_row.update({name: collection.get(name) for name in COLLECTION_FIELDS})
    footprint_row.update({name: manifest.get(name)
                          for name in ("obfuscatorDictionaryId", "obfuscatorLib", "obfuscatorLibVersion")})
    return {"tableMeasures": table_rows, "columnMeasures": column_rows, "collectionMeasures": [collection_row],
            "footprints": [[footprint, at] + [typed(footprint_row[name], kind) for name, kind in FOOTPRINT_COLUMNS]]}


def expected_output(name):
    """{output table: rows} of a sample of the kit, for the tables that have rows."""
    path = sample_path(name)
    footprint = hashlib.sha256(footprint_bytes(path)).hexdigest()
    with open_sample(name) as doc:
        at = collected_at(doc.collection)
        result = {table: format_rows(doc, table, footprint, at) for table in pqax.TABLES if doc.row_count(table) > 0}
        result.update(derived_rows(doc, load_json(MEASURES / f"{name}.json"), footprint, at, path.name))
    return {table: rows for table, rows in result.items() if rows}


def ignored_tables(name):
    with open_sample(name) as doc:
        return sorted(table for table in doc.table_names if table not in pqax.TABLES)


# ---- reading the export ----

UNQUOTED = re.compile(r'[^,\r\n"]*')


def csv_records(text):
    """The records of a CSV text as the interface writes it, each a list of (text, quoted): fields separated by
    commas, each record ended by \\r\\n, a quoted field with its quotes doubled inside."""
    records, record, position, end = [], [], 0, len(text)
    while position < end:
        if text[position] == '"':
            pieces = []
            position += 1
            while True:
                close = text.find('"', position)
                assert close >= 0, "a quoted field is not closed"
                pieces.append(text[position:close])
                if text.startswith('""', close):
                    pieces.append('"')
                    position = close + 2
                else:
                    position = close + 1
                    break
            record.append(("".join(pieces), True))
        else:
            match = UNQUOTED.match(text, position)
            record.append((match.group(), False))
            position = match.end()
        if text.startswith(",", position):
            position += 1
        elif text.startswith("\r\n", position):
            records.append(record)
            record = []
            position += 2
        else:
            raise AssertionError(f"{text[position:position + 20]!r} at {position}: a field ends with a comma or \\r\\n")
    assert not record, "the last record does not end with \\r\\n"
    return records


def read_csv(path):
    """The header and the rows of a CSV file of the export, None for an empty field and "" for a quoted empty one.

    It checks the layout of the interface: UTF-8 without BOM, \\r\\n, a field quoted only when it holds a comma, a
    quote, a line break, or is the empty string, and the same texts from the csv module."""
    data = path.read_bytes()
    assert not data.startswith(codecs.BOM_UTF8), f"{path.name} starts with a BOM"
    records = csv_records(data.decode("utf-8"))
    with open(path, encoding="utf-8", newline="") as handle:
        assert list(csv.reader(handle)) == [[text for text, _ in record] for record in records]
    for record in records:
        assert len(record) == len(records[0]), f"{path.name}: a record has {len(record)} fields"
        for text, quoted in record:
            if quoted:
                assert text == "" or any(c in text for c in ',"\r\n'), f"{path.name}: {text!r} is quoted"
    header = [text for text, _ in records[0]]
    rows = [[None if text == "" and not quoted else text for text, quoted in record] for record in records[1:]]
    return header, rows


def decode(text, kind):
    """A CSV field as a value, checking its form: digits, the form of a number, true or false, the timestamp form."""
    if text is None or kind in ("string", "json"):
        return text
    if kind == "integer":
        assert re.fullmatch(r"-?[0-9]+", text), f"{text!r} is not an integer"
        return int(text)
    if kind == "number":
        value = float(text)
        assert text == number_text(value), f"{text!r} is not the form of the number {value!r}"
        return value
    if kind == "boolean":
        assert text in ("true", "false"), f"{text!r} is not a boolean"
        return text == "true"
    if kind == "timestamp":
        assert TIMESTAMP_TEXT.fullmatch(text), f"{text!r} is not in the form of a timestamp"
        return moment(text)
    assert kind == "date" and DATE_TEXT.fullmatch(text), f"{text!r} is not a date"
    return date.fromisoformat(text)


def arrow_type_is(arrow_type, kind):
    import pyarrow as pa

    if kind == "integer":
        return arrow_type == pa.int64()
    if kind == "number":
        return arrow_type == pa.float64()
    if kind == "boolean":
        return arrow_type == pa.bool_()
    if kind == "timestamp":
        return pa.types.is_timestamp(arrow_type) and arrow_type.unit == "us" and arrow_type.tz in ("UTC", "+00:00")
    if kind == "date":
        return arrow_type == pa.date32()
    return pa.types.is_string(arrow_type) or pa.types.is_large_string(arrow_type)


DELTA_TYPES = {"integer": "long", "number": "double", "boolean": "boolean", "timestamp": "timestamp", "date": "date",
               "string": "string", "json": "string"}


def read_arrow(folder, fmt, table):
    """The Arrow table of an output table: the Parquet files of the folder in the order of their names, or the Delta
    table."""
    if fmt == "parquet":
        import pyarrow as pa
        import pyarrow.parquet as pq

        files = sorted((folder / table).glob("*.parquet"))
        assert files, f"{table} has no Parquet file"
        return pa.concat_tables([pq.read_table(path) for path in files])
    from deltalake import DeltaTable

    return DeltaTable(str(folder / table)).to_pyarrow_table()


def read_output(folder, fmt, table):
    """The column names and the rows of an output table, each value an int, float, str, bool, datetime, date, or
    None. For Parquet and Delta it also checks the type of each column."""
    table_kinds = kinds(table)
    if fmt == "csv":
        header, rows = read_csv(folder / f"{table}.csv")
        return header, [[decode(text, table_kinds.get(column, "string")) for text, column in zip(row, header)]
                        for row in rows]
    arrow = read_arrow(folder, fmt, table)
    wrong = [f"{item.name}: {item.type}" for item in arrow.schema
             if item.name in table_kinds and not arrow_type_is(item.type, table_kinds[item.name])]
    assert not wrong, f"{table} has columns of another type: {wrong}"
    if fmt == "delta":
        from deltalake import DeltaTable

        schema = DeltaTable(str(folder / table)).schema()
        wrong = [f"{item.name}: {item.type}" for item in schema.fields
                 if item.name in table_kinds and getattr(item.type, "type", None) != DELTA_TYPES[table_kinds[item.name]]]
        assert not wrong, f"{table} has Delta columns of another type: {wrong}"
    return arrow.column_names, [[row[name] for name in arrow.column_names] for row in arrow.to_pylist()]


def written_tables(folder, fmt):
    if fmt == "csv":
        return {path.stem for path in folder.glob("*.csv")}
    return {path.name for path in folder.iterdir() if path.is_dir()}


def snapshot(folder):
    """Every file under the folder with its bytes."""
    if not folder.exists():
        return {}
    return {path.relative_to(folder).as_posix(): path.read_bytes() for path in sorted(folder.rglob("*"))
            if path.is_file()}


def need(fmt):
    if fmt in ("parquet", "delta"):
        pytest.importorskip("pyarrow")
    if fmt == "delta":
        pytest.importorskip("deltalake")


def hide(monkeypatch, *libraries):
    """Makes an import of the libraries fail, as when they are not installed."""
    for name in list(sys.modules):
        if name.split(".")[0] in libraries:
            monkeypatch.delitem(sys.modules, name)
    for name in libraries:
        monkeypatch.setitem(sys.modules, name, None)


@pytest.fixture
def px():
    """The module pqax.export."""
    return importlib.import_module("pqax.export")


@pytest.fixture
def next_footprint(tmp_path):
    """sample.pqax rewritten with the builder and collected one day later: another footprint with the same tables."""
    with pqax.open(SAMPLE) as doc:
        builder = pqax.Builder.from_document(doc)
    builder.collection["completedAt"] = NEXT_COLLECTED_AT
    path = tmp_path / "next.pqax"
    builder.write(path)
    return path


@pytest.fixture
def awkward(tmp_path):
    """A footprint with the values that CSV has to quote or to tell apart: a comma, a quote, a line break, an empty
    string, letters outside ASCII, an empty list, a map, a timestamp that does not parse, and a completedAt that does
    not parse, so that collectedAt is startedAt."""
    builder = pqax.Builder({"collectorApp": "pqax-tests", "collectorAppVersion": "0.0",
                            "startedAt": "2026-10-11T00:00:00.000Z", "completedAt": "not a time", "level": 2,
                            "includesDataValues": False})
    source = builder.add_source(kind="Storage folder", name='a "quoted", name', location="",
                                identifiers={"k": "v,w"})
    table = builder.add_table(source=source, name="Größe\nzwei", format="Delta", detailDepth="Summary", namespace=[],
                              properties={"clé": "été"}, description=",", createdAt="not a time",
                              lastModifiedAt="2026-10-10T08:15:30.000Z", fileCount=1, fileSize=10, rowCount=5)
    builder.add_column(table=table, name="", dataType="string", isNullable=False, codecs=["ZSTD", "SNAPPY"])
    builder.add_issue(code="test.awkward", message="line one\r\nline two")
    path = tmp_path / "awkward.pqax"
    builder.write(path)
    return path


# ---- the module ----

def test_the_module_has_the_names_of_the_interface(px):
    from pqax import export as from_package

    assert from_package is px
    assert px.FORMATS == ("csv", "parquet", "delta")
    assert px.MEASURE_TABLES == ("tableMeasures", "columnMeasures", "collectionMeasures")
    assert issubclass(px.AlreadyExportedError, pqax.PqaxError)
    assert px.AlreadyExportedError.code == "export.duplicate"


def test_the_footprint_id_is_the_lowercase_sha256_of_the_bytes(px):
    data = SAMPLE.read_bytes()
    assert px.footprint_id(data) == hashlib.sha256(data).hexdigest() == SAMPLE_ID
    assert re.fullmatch("[0-9a-f]{64}", SAMPLE_ID)
    assert px.footprint_id(footprint_bytes(SAMPLES / "embedded-stored.vpax")) == SAMPLE_ID
    assert px.footprint_id(b"") == hashlib.sha256(b"").hexdigest()


def test_columns_has_every_output_table_with_the_columns_of_the_interface(px):
    assert set(px.COLUMNS) == set(OUTPUT_TABLES)
    for table in OUTPUT_TABLES:
        assert tuple(px.COLUMNS[table]) == columns_of(table), table
    for table in pqax.TABLES:
        assert tuple(px.COLUMNS[table]) == ("footprintId", "collectedAt", *pqax.FIELDS[table])


def test_the_footprints_table_has_the_members_of_the_collection_in_their_order():
    names = [name for name, _ in FOOTPRINT_COLUMNS]
    assert tuple(names[5:16]) == COLLECTION_FIELDS
    assert len(columns_of("tableMeasures")) == 2 + 69


# ---- every sample of the kit ----

@pytest.mark.parametrize("name", NAMES)
@pytest.mark.parametrize("fmt", FORMATS)
def test_the_result_and_the_folder_of_every_sample(fmt, name, px, tmp_path):
    need(fmt)
    path = sample_path(name)
    to = tmp_path / "out"
    result = px.export(path, to, format=fmt)
    expected = expected_output(name)
    footprint = hashlib.sha256(footprint_bytes(path)).hexdigest()
    assert set(result) == {"footprintId", "collectedAt", "format", "folder", "skipped", "tables", "ignored"}
    assert result["footprintId"] == footprint
    assert result["collectedAt"] == COLLECTED_AT
    assert result["format"] == fmt
    assert isinstance(result["folder"], str) and os.path.samefile(result["folder"], to)
    assert result["skipped"] is False
    assert result["tables"] == {table: len(rows) for table, rows in expected.items()}
    assert sorted(result["ignored"]) == ignored_tables(name)
    with open_sample(name) as doc:
        for table in pqax.TABLES:
            assert (table in result["tables"]) == (doc.row_count(table) > 0), table
            if table in result["tables"]:
                assert result["tables"][table] == doc.row_count(table), table
    assert written_tables(to, fmt) == set(expected)
    for table in expected:
        if fmt == "csv":
            assert (to / f"{table}.csv").is_file()
        elif fmt == "parquet":
            assert sorted(path.name for path in (to / table).iterdir()) == [f"{footprint}.parquet"]
        else:
            assert (to / table / "_delta_log").is_dir()


@pytest.mark.parametrize("name", NAMES)
@pytest.mark.parametrize("fmt", FORMATS)
def test_the_tables_of_the_format_have_every_field_and_the_rows_of_the_document(fmt, name, px, tmp_path):
    need(fmt)
    to = tmp_path / "out"
    px.export(sample_path(name), to, format=fmt)
    expected = expected_output(name)
    for table in pqax.TABLES:
        if table not in expected:
            continue
        columns, rows = read_output(to, fmt, table)
        assert tuple(columns) == columns_of(table), table
        assert_rows(rows, expected[table], columns, f"{name}, {table}")


@pytest.mark.parametrize("name", NAMES)
@pytest.mark.parametrize("fmt", FORMATS)
def test_the_measures_and_the_footprint_match_the_expected_measures_of_the_kit(fmt, name, px, tmp_path):
    need(fmt)
    to = tmp_path / "out"
    px.export(sample_path(name), to, format=fmt)
    expected = expected_output(name)
    for table in DERIVED_COLUMNS:
        columns, rows = read_output(to, fmt, table)
        assert tuple(columns) == columns_of(table), table
        assert_rows(rows, expected[table], columns, f"{name}, {table}")
    columns, rows = read_output(to, fmt, "footprints")
    footprint = dict(zip(columns, rows[0]))
    assert footprint["file"] == KIT[name]["file"]
    assert footprint["embeddedPart"] == KIT[name]["embeddedPart"]
    assert footprint["collectionDate"] == date(2026, 10, 5)


def test_the_extension_tables_are_ignored_and_the_extension_fields_are_not_columns(px, tmp_path):
    to = tmp_path / "out"
    result = px.export(SAMPLES / "extensions.pqax", to)
    assert sorted(result["ignored"]) == ["acme_details", "acme_reviews"]
    assert not list(to.glob("acme_*"))
    for table in written_tables(to, "csv"):
        header, _ = read_csv(to / f"{table}.csv")
        assert not [column for column in header if column.startswith("acme_")], table
    assert read_csv(to / "tables.csv")[0] == list(columns_of("tables"))
    assert read_csv(to / "files.csv")[0] == list(columns_of("files"))
    assert px.export(SAMPLE, tmp_path / "plain")["ignored"] == []


def test_a_table_that_the_file_lacks_is_not_written(px, tmp_path):
    to = tmp_path / "out"
    result = px.export(SAMPLES / "summary-only.pqax", to)
    for table in pqax.DETAIL_TABLES:
        assert table not in result["tables"]
        assert not (to / f"{table}.csv").exists()
    assert {"footprints", "collectionMeasures", "tableMeasures", "columnMeasures", "tables"} <= set(result["tables"])
    assert (result["tables"]["footprints"], result["tables"]["collectionMeasures"]) == (1, 1)
    result = px.export(SAMPLES / "files.pqax", tmp_path / "files")
    assert "files" in result["tables"] and "rowGroups" not in result["tables"] and "columnChunks" not in result["tables"]


def test_a_field_that_the_file_lacks_is_a_column_with_null_in_every_row(px, tmp_path):
    to = tmp_path / "out"
    px.export(SAMPLE, to)
    checked = 0
    with pqax.open(SAMPLE) as doc:
        for table in pqax.TABLES:
            if doc.row_count(table) == 0:
                continue
            header, rows = read_csv(to / f"{table}.csv")
            for name in FIELDS[table]:
                if name not in doc.table(table):
                    assert name in header, f"{table}.{name}"
                    assert all(row[header.index(name)] is None for row in rows), f"{table}.{name}"
                    checked += 1
    assert checked > 10


# ---- the forms of CSV ----

def test_csv_quotes_a_comma_a_quote_a_line_break_and_the_empty_string(px, awkward, tmp_path):
    to = tmp_path / "out"
    result = px.export(awkward, to)
    footprint = hashlib.sha256(awkward.read_bytes()).hexdigest()
    assert result["collectedAt"] == "2026-10-11T00:00:00.000Z"  # completedAt does not parse: startedAt
    assert (to / "sources.csv").read_bytes() == (
        "footprintId,collectedAt,id,kind,name,location,identifiers\r\n"
        f'{footprint},2026-10-11T00:00:00.000Z,1,Storage folder,"a ""quoted"", name","","{{""k"":""v,w""}}"\r\n'
    ).encode("utf-8")
    header, rows = read_csv(to / "tables.csv")
    table = dict(zip(header, rows[0]))
    assert table["name"] == "Größe\nzwei"
    assert '"Größe\nzwei"'.encode("utf-8") in (to / "tables.csv").read_bytes()
    assert table["description"] == ","
    assert table["namespace"] == "[]"  # a known empty list is [], not null
    assert table["properties"] == '{"clé":"été"}'
    assert table["createdAt"] is None  # a timestamp that does not parse is null
    assert table["lastModifiedAt"] == "2026-10-10T08:15:30.000Z"
    assert (table["fileCount"], table["fileSize"], table["rowCount"]) == ("1", "10", "5")
    assert table["location"] is None and table["tableId"] is None
    header, rows = read_csv(to / "columns.csv")
    column = dict(zip(header, rows[0]))
    assert column["name"] == ""  # an empty string, not null
    assert column["description"] is None
    assert column["isNullable"] == "false"
    assert column["codecs"] == '["ZSTD","SNAPPY"]'
    header, rows = read_csv(to / "issues.csv")
    assert dict(zip(header, rows[0]))["message"] == "line one\r\nline two"
    header, rows = read_csv(to / "footprints.csv")
    footprints = dict(zip(header, rows[0]))
    assert footprints["collectedAt"] == "2026-10-11T00:00:00.000Z"
    assert footprints["collectionDate"] == "2026-10-11"
    assert footprints["startedAt"] == "2026-10-11T00:00:00.000Z"
    assert footprints["completedAt"] is None
    assert (footprints["level"], footprints["includesDataValues"]) == ("2", "false")
    assert footprints["includesPartitionValues"] is None and footprints["collectorLib"] is None
    assert (footprints["file"], footprints["embeddedPart"]) == ("awkward.pqax", None)
    assert (footprints["format"], footprints["formatVersion"]) == ("pqax", "0.2")
    header, rows = read_csv(to / "tableMeasures.csv")
    measures = dict(zip(header, rows[0]))
    assert (measures["sourceName"], measures["detailDepth"]) == ('a "quoted", name', "Summary")


def test_the_measures_are_written_in_the_text_forms_of_the_interface(px, tmp_path):
    to = tmp_path / "out"
    px.export(SAMPLE, to)
    expected = expected_output("sample")
    for table in DERIVED_COLUMNS:
        header, rows = read_csv(to / f"{table}.csv")
        table_kinds = kinds(table)
        for got, want in zip(rows, expected[table]):
            for column, text, value in zip(header, got, want):
                kind = table_kinds[column]
                if value is None:
                    form = None
                elif kind == "number":
                    form = number_text(float(value))
                elif kind == "boolean":
                    form = "true" if value else "false"
                elif kind == "timestamp":
                    form = timestamp_text(value)
                else:
                    form = str(value)
                assert text == form, f"{table}.{column}: {text!r}, expected {form!r}"
    header, rows = read_csv(to / "tableMeasures.csv")
    sales = dict(zip(header, rows[0]))
    assert (sales["live"], sales["nonLive"], sales["listed"]) == ("155034.9", "737", "true")
    assert sales["wasteShare"] == "0.006256610110825519"
    assert sales["lastVacuumAt"] is None
    assert sales["lastOptimizeAt"] == "2023-11-14T22:14:50.000Z"
    header, rows = read_csv(to / "collectionMeasures.csv")
    assert dict(zip(header, rows[0]))["depths"] == '{"Column chunks":2}'


def test_without_a_parseable_time_collected_at_is_null(px, tmp_path):
    builder = pqax.Builder({"collectorApp": "pqax-tests", "level": 2, "completedAt": "never", "startedAt": "soon"})
    builder.add_table(source=builder.add_source(kind="Storage folder"), name="t", format="Delta",
                      detailDepth="Summary")
    path = tmp_path / "timeless.pqax"
    builder.write(path)
    to = tmp_path / "out"
    result = px.export(path, to)
    assert result["collectedAt"] is None
    for table in result["tables"]:
        header, rows = read_csv(to / f"{table}.csv")
        assert rows and all(row[header.index("collectedAt")] is None for row in rows), table
    header, rows = read_csv(to / "footprints.csv")
    footprints = dict(zip(header, rows[0]))
    assert footprints["collectionDate"] is None
    assert footprints["startedAt"] is None and footprints["completedAt"] is None


# ---- the input ----

def test_bytes_and_a_file_object_give_the_same_footprint_and_the_file_name_when_known(px, tmp_path):
    result = px.export(SAMPLE.read_bytes(), tmp_path / "bytes")
    assert result["footprintId"] == SAMPLE_ID
    header, rows = read_csv(tmp_path / "bytes" / "footprints.csv")
    assert dict(zip(header, rows[0]))["file"] is None
    with open(SAMPLE, "rb") as handle:
        result = px.export(handle, tmp_path / "object")
    assert result["footprintId"] == SAMPLE_ID
    header, rows = read_csv(tmp_path / "object" / "footprints.csv")
    assert dict(zip(header, rows[0]))["file"] == "sample.pqax"
    result = px.export((SAMPLES / "embedded-deflated.vpax").read_bytes(), tmp_path / "vpax")
    assert result["footprintId"] == SAMPLE_ID
    header, rows = read_csv(tmp_path / "vpax" / "footprints.csv")
    assert dict(zip(header, rows[0]))["embeddedPart"] == EMBEDDED_PART


def test_the_footprint_of_a_vpax_is_that_of_sample_pqax(px, tmp_path):
    for name in ("embedded-stored.vpax", "embedded-deflated.vpax"):
        result = px.export(SAMPLES / name, tmp_path / name, part=EMBEDDED_PART)
        assert result["footprintId"] == SAMPLE_ID, name
        assert result["collectedAt"] == COLLECTED_AT


def test_part_names_another_embedded_part(px, tmp_path):
    package = tmp_path / "renamed.vpax"
    with zipfile.ZipFile(SAMPLES / "embedded-stored.vpax") as source, zipfile.ZipFile(package, "w") as target:
        for info in source.infolist():
            name = "Other.pqax" if info.filename == EMBEDDED_PART else info.filename
            target.writestr(name, source.read(info.filename), compress_type=zipfile.ZIP_STORED)
    with pytest.raises(pqax.PqaxError):
        px.export(package, tmp_path / "default")
    result = px.export(package, tmp_path / "out", part="Other.pqax")
    assert result["footprintId"] == SAMPLE_ID
    header, rows = read_csv(tmp_path / "out" / "footprints.csv")
    assert dict(zip(header, rows[0]))["embeddedPart"] == "Other.pqax"


@pytest.mark.parametrize("make", ["manifest.format", "manifest.versionUnknown", "no footprint"])
def test_a_file_that_is_not_a_footprint_raises_pqax_error_and_writes_nothing(make, px, tmp_path):
    if make == "no footprint":
        path = tmp_path / "empty.vpax"
        write_package_without_footprint(path)
    else:
        path = INVALID / f"{make}.pqax"
    to = tmp_path / "out"
    with pytest.raises(pqax.PqaxError):
        px.export(path, to)
    assert snapshot(to) == {}


@pytest.mark.parametrize("options", [{"format": "xlsx"}, {"format": "json"}, {"if_exists": "overwrite"},
                                     {"if_exists": "replace"}])
def test_another_format_or_if_exists_raises_value_error(options, px, tmp_path):
    to = tmp_path / "out"
    with pytest.raises(ValueError):
        px.export(SAMPLE, to, **options)
    assert snapshot(to) == {}


@pytest.mark.parametrize("fmt, hidden, extra", [
    ("parquet", ("pyarrow",), "pqax[parquet]"),
    ("delta", ("deltalake",), "pqax[delta]"),
    ("delta", ("pyarrow",), "pqax[delta]"),
], ids=["parquet without pyarrow", "delta without deltalake", "delta without pyarrow"])
def test_a_missing_library_raises_import_error_naming_the_extra_before_writing(fmt, hidden, extra, px, tmp_path,
                                                                               monkeypatch):
    hide(monkeypatch, *hidden)
    to = tmp_path / "out"
    with pytest.raises(ImportError) as caught:
        px.export(SAMPLE, to, format=fmt)
    assert extra in str(caught.value)
    assert snapshot(to) == {}


# ---- appending ----

@pytest.mark.parametrize("fmt", FORMATS)
def test_a_second_footprint_appends_its_rows(fmt, px, tmp_path, next_footprint):
    need(fmt)
    to = tmp_path / "out"
    first = px.export(SAMPLE, to, format=fmt)
    versions = {}
    if fmt == "delta":
        from deltalake import DeltaTable

        versions = {table: DeltaTable(str(to / table)).version() for table in first["tables"]}
    second = px.export(next_footprint, to, format=fmt)
    old, new = first["footprintId"], second["footprintId"]
    assert new == hashlib.sha256(next_footprint.read_bytes()).hexdigest() != old
    assert second["collectedAt"] == NEXT_COLLECTED_AT and second["skipped"] is False
    assert second["tables"] == first["tables"]
    for table, count in first["tables"].items():
        columns, rows = read_output(to, fmt, table)
        assert tuple(columns) == columns_of(table)
        identifiers = [row[0] for row in rows]
        times = [row[1] for row in rows]
        assert Counter(identifiers) == {old: count, new: count}, table
        assert {time for identifier, time in zip(identifiers, times) if identifier == new} == {
            moment(NEXT_COLLECTED_AT)}, table
        if fmt == "csv":
            assert identifiers == [old] * count + [new] * count, table
            text = (to / f"{table}.csv").read_bytes().decode("utf-8")
            assert text.startswith(",".join(columns) + "\r\n")
            assert text.count("footprintId,collectedAt") == 1, f"{table}.csv has a second header"
        elif fmt == "parquet":
            assert sorted(path.name for path in (to / table).iterdir()) == sorted([f"{old}.parquet", f"{new}.parquet"])
        else:
            from deltalake import DeltaTable

            assert DeltaTable(str(to / table)).version() == versions[table] + 1, table


@pytest.mark.parametrize("fmt", FORMATS)
def test_the_same_footprint_again_is_refused_and_nothing_is_written(fmt, px, tmp_path):
    need(fmt)
    to = tmp_path / "out"
    px.export(SAMPLE, to, format=fmt)
    before = snapshot(to)
    with pytest.raises(px.AlreadyExportedError) as caught:
        px.export(SAMPLE, to, format=fmt)
    assert caught.value.code == "export.duplicate"
    assert isinstance(caught.value, pqax.PqaxError)
    assert snapshot(to) == before
    with pytest.raises(px.AlreadyExportedError):
        px.export(SAMPLE, to, format=fmt, if_exists="error")
    assert snapshot(to) == before


@pytest.mark.parametrize("fmt", FORMATS)
def test_a_vpax_that_embeds_an_exported_footprint_is_refused(fmt, px, tmp_path):
    need(fmt)
    to = tmp_path / "out"
    px.export(SAMPLE, to, format=fmt)
    before = snapshot(to)
    for name in ("embedded-stored.vpax", "embedded-deflated.vpax"):
        with pytest.raises(px.AlreadyExportedError) as caught:
            px.export(SAMPLES / name, to, format=fmt)
        assert caught.value.code == "export.duplicate"
        assert snapshot(to) == before, name


@pytest.mark.parametrize("fmt", FORMATS)
def test_skip_writes_nothing_for_a_footprint_already_exported(fmt, px, tmp_path):
    need(fmt)
    to = tmp_path / "out"
    first = px.export(SAMPLE, to, format=fmt, if_exists="skip")
    assert first["skipped"] is False and first["tables"]
    before = snapshot(to)
    result = px.export(SAMPLES / "embedded-stored.vpax", to, format=fmt, if_exists="skip")
    assert result["skipped"] is True
    assert result["tables"] == {}
    assert result["footprintId"] == SAMPLE_ID
    assert result["format"] == fmt
    assert snapshot(to) == before


@pytest.mark.parametrize("table, header", [
    ("commits", "footprintId,collectedAt,table,commitId\r\n"),
    ("sources", ",".join(reversed(("footprintId", "collectedAt", *FIELDS["sources"])))+ "\r\n"),
], ids=["a column less", "another order"])
def test_a_csv_file_with_another_header_raises_value_error_before_anything_is_written(table, header, px, tmp_path):
    to = tmp_path / "out"
    to.mkdir()
    (to / f"{table}.csv").write_bytes(header.encode("utf-8"))
    before = snapshot(to)
    with pytest.raises(ValueError, match=re.escape(f"{table}.csv")):
        px.export(SAMPLE, to)
    assert snapshot(to) == before


@pytest.mark.parametrize("first, second", [("delta", "parquet"), ("parquet", "delta")])
def test_parquet_and_delta_do_not_mix_in_one_folder(first, second, px, tmp_path, next_footprint):
    need("delta")
    to = tmp_path / "out"
    px.export(SAMPLE, to, format=first)
    before = snapshot(to)
    with pytest.raises(ValueError):
        px.export(next_footprint, to, format=second)
    assert snapshot(to) == before


# ---- the command line ----

@pytest.fixture
def run(capsys):
    """Runs pqax export in the process and returns the exit code, the output, and the error output."""
    def run(*arguments):
        try:
            code = main(["export", *map(str, arguments)])
        except SystemExit as error:
            code = error.code
        out, err = capsys.readouterr()
        return code, out, err
    return run


def assert_output_lines(out, result):
    """One line for each table written, with its name and its rows, then a last line with the footprint id."""
    lines = out.rstrip("\n").splitlines()
    assert len(lines) == len(result["tables"]) + 1, out
    assert result["footprintId"] in lines[-1], out
    seen = {}
    for line in lines[:-1]:
        names = set(re.findall(r"[A-Za-z_]+", line)) & set(result["tables"])
        assert len(names) == 1, f"{line!r} names {sorted(names)}"
        [name] = names
        rows = result["tables"][name]
        assert {str(rows), f"{rows:,}"} & set(re.findall(r"[0-9]+(?:,[0-9]{3})*", line)), line
        seen[name] = line
    assert set(seen) == set(result["tables"])


@pytest.mark.parametrize("name", NAMES)
def test_the_command_writes_the_files_of_the_function_and_a_line_for_each_table(name, px, run, tmp_path):
    path = sample_path(name)
    code, out, err = run(path, "--to", tmp_path / "command")
    assert code == 0, out + err
    result = px.export(path, tmp_path / "function")
    assert snapshot(tmp_path / "command") == snapshot(tmp_path / "function")
    assert_output_lines(out, result)


@pytest.mark.parametrize("fmt", ["parquet", "delta"])
def test_the_command_writes_parquet_and_delta(fmt, run, tmp_path):
    need(fmt)
    to = tmp_path / "out"
    code, out, err = run(SAMPLE, "--to", to, "--format", fmt)
    assert code == 0, out + err
    assert SAMPLE_ID in out.rstrip("\n").splitlines()[-1]
    if fmt == "parquet":
        assert (to / "footprints" / f"{SAMPLE_ID}.parquet").is_file()
    else:
        assert (to / "footprints" / "_delta_log").is_dir()


def test_the_command_takes_part_for_a_vpax(run, tmp_path):
    code, out, err = run(SAMPLES / "embedded-stored.vpax", "--to", tmp_path / "out", "--part", EMBEDDED_PART)
    assert code == 0, out + err
    assert SAMPLE_ID in out.rstrip("\n").splitlines()[-1]


def test_the_command_exits_with_1_for_a_footprint_already_exported_and_0_when_skipped(run, tmp_path):
    to = tmp_path / "out"
    assert run(SAMPLE, "--to", to)[0] == 0
    before = snapshot(to)
    for path in (SAMPLE, SAMPLES / "embedded-stored.vpax"):
        code, out, err = run(path, "--to", to)
        assert code == 1, out + err
        assert err.strip()
    code, out, err = run(SAMPLE, "--to", to, "--if-exists", "skip")
    assert code == 0, out + err
    assert "skip" in (out + err).lower()
    assert snapshot(to) == before


@pytest.mark.parametrize("make", ["manifest.format", "container.manifestMissing", "no footprint"])
def test_the_command_exits_with_1_for_a_file_that_is_not_a_footprint(make, run, tmp_path):
    if make == "no footprint":
        path = tmp_path / "empty.vpax"
        write_package_without_footprint(path)
    else:
        path = INVALID / f"{make}.pqax"
    code, out, err = run(path, "--to", tmp_path / "out")
    assert code == 1, out + err
    assert err.strip()
    assert snapshot(tmp_path / "out") == {}


@pytest.mark.parametrize("fmt, hidden, extra", [("parquet", ("pyarrow",), "pqax[parquet]"),
                                                ("delta", ("deltalake",), "pqax[delta]")])
def test_the_command_exits_with_2_when_a_library_is_missing(fmt, hidden, extra, run, tmp_path, monkeypatch):
    hide(monkeypatch, *hidden)
    code, out, err = run(SAMPLE, "--to", tmp_path / "out", "--format", fmt)
    assert code == 2, out + err
    assert extra in err
    assert snapshot(tmp_path / "out") == {}


@pytest.mark.parametrize("arguments", [
    [], ["{sample}"], ["{sample}", "--to"], ["--to", "{to}"], ["{sample}", "--to", "{to}", "--format", "xlsx"],
    ["{sample}", "--to", "{to}", "--format"], ["{sample}", "--to", "{to}", "--if-exists", "overwrite"],
    ["{sample}", "--to", "{to}", "--no-such-option"], ["{sample}", "{sample}", "--to", "{to}"],
], ids=["nothing", "no folder", "to without value", "no file", "unknown format", "format without value",
        "unknown if-exists", "unknown option", "two files"])
def test_a_wrong_command_line_exits_with_2(arguments, run, tmp_path):
    to = tmp_path / "out"
    resolved = [str(SAMPLE) if argument == "{sample}" else str(to) if argument == "{to}" else argument
                for argument in arguments]
    code, out, err = run(*resolved)
    assert code == 2, out + err
    assert err.strip()
    assert snapshot(to) == {}


def test_an_io_error_exits_with_2(run, tmp_path):
    code, out, err = run(tmp_path / "missing.pqax", "--to", tmp_path / "out")
    assert code == 2, out + err
    assert err.strip()
    blocker = tmp_path / "a file"
    blocker.write_bytes(b"not a folder")
    code, out, err = run(SAMPLE, "--to", blocker)
    assert code == 2, out + err
    assert err.strip()
    assert blocker.read_bytes() == b"not a folder"


def test_the_command_runs_in_a_process_of_its_own(tmp_path):
    to = tmp_path / "out"
    completed = subprocess.run([sys.executable, "-m", "pqax", "export", str(SAMPLE), "--to", str(to)], cwd=ROOT,
                               capture_output=True, text=True, encoding="utf-8", timeout=120)
    assert completed.returncode == 0, completed.stdout + completed.stderr
    assert SAMPLE_ID in completed.stdout.rstrip("\n").splitlines()[-1]
    assert read_csv(to / "footprints.csv")[1][0][0] == SAMPLE_ID
