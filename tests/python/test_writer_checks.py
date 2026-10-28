"""The checks of Builder.write: a footprint whose integers, required fields, ids, or collection level break a rule of
the format is refused with pqax.WriteError and the code of the rule, before anything is written (the contract of the
hardening before 1.0, section 2)."""
from __future__ import annotations

import io
import pathlib
import tempfile
from typing import Any, Callable, NamedTuple, Optional

import pytest

import pqax
from pqax_kit import INVALID, invalid_cases

LIMIT = 2 ** 53 - 1  # the largest integer of the format, in absolute value
COLLECTION = {"collectorApp": "pqax-tests", "level": 1}


def footprint(collection=None, table=None, file=None):
    """A small valid footprint: a source, a table at the Files depth, and its file, with the fields given added to
    the table and to the file. collection replaces the whole collection."""
    builder = pqax.Builder(COLLECTION if collection is None else collection)
    builder.add_source(kind="Storage folder")
    builder.add_table(**{"source": 1, "name": "a", "format": "Parquet", "detailDepth": "Files", "fileCount": 1,
                         "fileSize": 10, **(table or {})})
    builder.add_file(**{"table": 1, "fileSize": 10, **(file or {})})
    return builder


def refused(builder, tmp_path):
    """The WriteError of a refused write, the same to a file object, to a path, and to a path that has a file, after
    checking that nothing was written: the file object is empty, and no file is created or changed."""
    folder = pathlib.Path(tempfile.mkdtemp(dir=tmp_path))
    errors = []
    buffer = io.BytesIO()
    with pytest.raises(pqax.WriteError) as raised:
        builder.write(buffer)
    assert buffer.getvalue() == b""
    errors.append(raised.value)
    target = folder / "refused.pqax"
    with pytest.raises(pqax.WriteError) as raised:
        builder.write(str(target))
    assert not target.exists()
    errors.append(raised.value)
    existing = folder / "existing.pqax"
    existing.write_bytes(b"left as it was")
    with pytest.raises(pqax.WriteError) as raised:
        builder.write(existing)
    assert existing.read_bytes() == b"left as it was"
    errors.append(raised.value)
    assert sorted(folder.iterdir()) == [existing], "a temporary file is left"
    for error in errors:
        assert isinstance(error, pqax.PqaxError) and isinstance(error, ValueError)
        assert (error.code, error.table, error.field) == (errors[0].code, errors[0].table, errors[0].field)
        assert str(error)
    return errors[0]


def written(builder):
    """The bytes of a write that succeeds."""
    buffer = io.BytesIO()
    builder.write(buffer)
    return buffer.getvalue()


def test_write_error_is_a_pqax_error_and_a_value_error_exported_by_the_package():
    assert issubclass(pqax.WriteError, pqax.PqaxError)
    assert issubclass(pqax.WriteError, ValueError)
    assert "WriteError" in pqax.__all__


def test_a_valid_footprint_is_written():
    with pqax.open(written(footprint())) as doc:
        assert doc.row_count("files") == 1
    assert pqax.validate(written(footprint())).is_valid


# ---- integers ----

class Integer(NamedTuple):
    """A kind of integer of the format: a footprint with a value there, where the error points, and the value read
    back. signed is False for an id, which the add methods refuse when it is not positive."""

    make: Callable[[Any], Any]
    table: Optional[str]
    field: str
    read: Callable[[Any], Any]
    signed: bool = True


def with_source(value):
    builder = footprint()
    builder.add_source(id=value, kind="Storage folder")
    return builder


def with_partition_field(value):
    builder = footprint()
    spec = builder.add_partition_spec(table=1)
    builder.add_partition_field(partitionSpec=spec, position=value)
    return builder


def summary(table, field, index=0):
    return lambda doc: doc.summary[table][field][index]


INTEGERS = {
    "an integer field": Integer(lambda value: footprint(table={"rowCount": value}), "tables", "rowCount",
                                summary("tables", "rowCount")),
    "an integer field of a detail table": Integer(lambda value: footprint(file={"rowCount": value}), "files", "rowCount",
                                                  lambda doc: doc.table("files")["rowCount"][0]),
    "an id": Integer(with_source, "sources", "id", summary("sources", "id", 1), signed=False),
    "a reference": Integer(lambda value: footprint(table={"source": value}), "tables", "source",
                           summary("tables", "source")),
    "an element of a list of references": Integer(lambda value: footprint(table={"clusteringColumns": [1, value]}),
                                                  "tables", "clusteringColumns",
                                                  lambda doc: doc.summary["tables"]["clusteringColumns"][0][1]),
    "a member of a Distribution": Integer(lambda value: footprint(table={"fileSizes": {"count": 1, "maximum": value}}),
                                          "tables", "fileSizes",
                                          lambda doc: doc.summary["tables"]["fileSizes"][0]["maximum"]),
    "a member of DeltaDetails": Integer(
        lambda value: footprint(table={"delta": {"minReaderVersion": 1, "checkpointVersion": value}}),
        "tables", "delta", lambda doc: doc.summary["tables"]["delta"][0]["checkpointVersion"]),
    "a member of IcebergDetails": Integer(
        lambda value: footprint(table={"iceberg": {"formatVersion": 2, "snapshotCount": value}}),
        "tables", "iceberg", lambda doc: doc.summary["tables"]["iceberg"][0]["snapshotCount"]),
    "a position": Integer(with_partition_field, "partitionFields", "position", summary("partitionFields", "position")),
    "collection.level": Integer(lambda value: footprint(collection={**COLLECTION, "level": value}), None,
                                "collection.level", lambda doc: doc.manifest["collection"]["level"]),
    "collection.requestCount": Integer(lambda value: footprint(collection={**COLLECTION, "requestCount": value}), None,
                                       "collection.requestCount",
                                       lambda doc: doc.manifest["collection"]["requestCount"]),
    "collection.bytesRead": Integer(lambda value: footprint(collection={**COLLECTION, "bytesRead": value}), None,
                                    "collection.bytesRead", lambda doc: doc.manifest["collection"]["bytesRead"]),
}


def cases(values):
    return [pytest.param(kind, value, id=f"{kind}, {value}") for kind in INTEGERS for value in values
            if value > 0 or INTEGERS[kind].signed]


@pytest.mark.parametrize("kind,value", cases([LIMIT, -LIMIT]))
def test_an_integer_of_2_53_minus_1_in_absolute_value_is_written(kind, value):
    integer = INTEGERS[kind]
    with pqax.open(written(integer.make(value))) as doc:
        read = integer.read(doc)
    assert read == value
    assert type(read) is int


@pytest.mark.parametrize("kind,value", cases([LIMIT + 1, -(LIMIT + 1), 2 ** 64]))
def test_an_integer_beyond_2_53_minus_1_is_refused_with_json_integerRange(kind, value, tmp_path):
    integer = INTEGERS[kind]
    error = refused(integer.make(value), tmp_path)
    assert (error.code, error.table, error.field) == ("json.integerRange", integer.table, integer.field)


@pytest.mark.parametrize("kind,value", [pytest.param(kind, value, id=f"{kind}, {value}") for kind in INTEGERS
                                        if INTEGERS[kind].signed for value in (2.0, 2.5)])
def test_a_float_in_an_integer_of_the_format_is_refused_with_json_integer(kind, value, tmp_path):
    integer = INTEGERS[kind]
    error = refused(integer.make(value), tmp_path)
    assert (error.code, error.table, error.field) == ("json.integer", integer.table, integer.field)


def test_the_numbers_of_a_producer_are_not_checked():
    huge = 2 ** 60
    builder = footprint(collection={**COLLECTION, "acme_requests": huge},
                        table={"acme_score": huge, "acme_ratio": 0.5, "fileSizes": {"count": 1, "acme_p99": huge}},
                        file={"acme_offset": -huge})
    builder.add("acme_reviews", table=1, score=huge, ratio=1.5)
    builder.extend("acme_events", {"table": [1, 1], "size": [huge, 2.5]}, detail=True)
    builder.manifest_extensions["acme_build"] = {"number": huge, "ratio": 0.25}
    with pqax.open(written(builder)) as doc:
        assert doc.manifest["collection"]["acme_requests"] == huge
        assert doc.manifest["acme_build"] == {"number": huge, "ratio": 0.25}
        assert doc.summary["tables"]["acme_score"] == [huge]
        assert doc.summary["tables"]["acme_ratio"] == [0.5]
        assert doc.summary["tables"]["fileSizes"][0]["acme_p99"] == huge
        assert doc.table("files")["acme_offset"] == [-huge]
        assert doc.summary["acme_reviews"] == {"table": [1], "score": [huge], "ratio": [1.5]}
        assert doc.table("acme_events") == {"table": [1, 1], "size": [huge, 2.5]}


# ---- required fields ----

def add_rows(add):
    """The footprint with the rows that add(builder) adds."""
    builder = footprint()
    add(builder)
    return builder


def second_table_without_format(builder):
    builder.add_table(source=1, name="b", detailDepth="Summary")


def partition_field_without_position(builder):
    builder.add_partition_spec(table=1)
    builder.add_partition_field(partitionSpec=1, name="p")


def sort_field_without_direction(builder):
    builder.add_column(table=1, name="c", dataType="long")
    builder.add_sort_order(table=1)
    builder.add_sort_field(sortOrder=1, position=0, sourceColumn=1)


def column_chunk_without_uncompressed_size(builder):
    builder.add_physical_schema(table=1)
    builder.add_physical_column(physicalSchema=1, ordinal=0, path=["c"], physicalType="INT64")
    builder.add_row_group(file=1, ordinal=0, rowCount=1)
    builder.add_column_chunk(rowGroup=1, physicalColumn=1, compressedSize=1)


# Each footprint misses one required field of rules.md 4.4: absent from every row, or null in one row of a field
# that other rows have.
REQUIRED = {
    "tables.format, absent from every row": (lambda: footprint(table={"format": None}), "tables", "format"),
    "tables.format, null in one row": (lambda: add_rows(second_table_without_format), "tables", "format"),
    "columns.table, the reference to the parent": (
        lambda: add_rows(lambda builder: builder.add_column(name="c", dataType="long")), "columns", "table"),
    "partitionFields.position": (lambda: add_rows(partition_field_without_position), "partitionFields", "position"),
    "sortFields.direction": (lambda: add_rows(sort_field_without_direction), "sortFields", "direction"),
    "issues.code": (lambda: add_rows(lambda builder: builder.add_issue(message="no code")), "issues", "code"),
    "files.table, null in one row of a detail table": (
        lambda: add_rows(lambda builder: builder.add_file(fileSize=5)), "files", "table"),
    "columnChunks.uncompressedSize": (lambda: add_rows(column_chunk_without_uncompressed_size), "columnChunks",
                                      "uncompressedSize"),
    "commits.commitId": (lambda: add_rows(lambda builder: builder.add_commit(table=1)), "commits", "commitId"),
}


@pytest.mark.parametrize("case", sorted(REQUIRED))
def test_a_required_field_without_a_value_is_refused_with_table_required(case, tmp_path):
    make, table, field = REQUIRED[case]
    error = refused(make(), tmp_path)
    assert (error.code, error.table, error.field) == ("table.required", table, field)


WITHOUT_LEVEL = {
    "level missing": lambda: footprint(collection={"collectorApp": "pqax-tests"}),
    "level null": lambda: footprint(collection={"collectorApp": "pqax-tests", "level": None}),
    "an empty builder with an empty collection": lambda: pqax.Builder({}),
}


@pytest.mark.parametrize("case", sorted(WITHOUT_LEVEL))
def test_a_collection_without_level_is_refused_with_manifest_required(case, tmp_path):
    error = refused(WITHOUT_LEVEL[case](), tmp_path)
    assert (error.code, error.table, error.field) == ("manifest.required", None, "collection.level")


# ---- ids ----

def test_a_repeated_id_is_refused_with_table_idUnique(tmp_path):
    builder = footprint()
    builder.add_source(id=5, kind="Storage folder")
    builder.add_source(id=5, kind="Storage folder")
    error = refused(builder, tmp_path)
    assert (error.code, error.table, error.field) == ("table.idUnique", "sources", "id")


def test_a_repeated_id_of_a_detail_table_is_refused_with_table_idUnique(tmp_path):
    builder = footprint(table={"fileCount": 2, "fileSize": 20})
    builder.extend("files", {"id": [1], "table": [1], "fileSize": [10]})
    error = refused(builder, tmp_path)
    assert (error.code, error.table, error.field) == ("table.idUnique", "files", "id")


# ---- rewrites of the invalid samples ----

# The invalid samples whose rule a writer checks: Builder.from_document keeps what the reader returns, and the write
# is refused with the code that the kit gives to the sample, where the change is.
REWRITES = {
    "json.integer.pqax": ("json.integer", "rowGroups", "rowCount"),
    "json.integerRange.pqax": ("json.integerRange", "rowGroups", "uncompressedSize"),
    "manifest.required.level.pqax": ("manifest.required", None, "collection.level"),
    "table.idUnique.repeated.pqax": ("table.idUnique", "sources", "id"),
    "table.required.missing.pqax": ("table.required", "columns", "dataType"),
    "table.required.null.pqax": ("table.required", "columns", "name"),
    "table.required.operationName.pqax": ("table.required", "operations", "name"),
    "table.required.position.pqax": ("table.required", "partitionFields", "position"),
}


@pytest.mark.parametrize("name", sorted(REWRITES))
def test_the_rewrite_of_an_invalid_sample_is_refused_with_its_code(name, tmp_path):
    expected = REWRITES[name]
    assert expected[0] in invalid_cases()[name]["codes"]
    with pqax.open(str(INVALID / name)) as doc:
        error = refused(pqax.Builder.from_document(doc), tmp_path)
    assert (error.code, error.table, error.field) == expected
