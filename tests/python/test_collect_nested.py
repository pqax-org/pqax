"""The physical columns of nested columns, from the rules N1 to N6 of conformance/footers.md: one physical column for
each leaf, with every name of its path and the types of the leaf itself; its column, by field id or by name; the totals
of a column over all its leaves; and the types of the nested columns of a folder of Parquet files.

The files are written by pyarrow at test time (those tests are skipped without it), with the names of the
specification and, with use_compliant_nested_type=False, the legacy list/item; pyarrow.parquet.ParquetFile(...).metadata
is the oracle of the paths and of the totals. The other files are the synthetic footers of collect_kit.schema_footer,
which NestedColumnTests.cs of the C# tests writes byte for byte, so that the two suites expect the same rows for the
same bytes (N6).
"""
from __future__ import annotations

import datetime
import json
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Sequence, Tuple

import pytest

from collect_kit import (HAS_PYARROW, NEWER, OLDER, CONVERTED_TYPES, PHYSICAL_TYPES, REPETITIONS, Container, Thrift,
                         _write_logical, add, assert_valid, collect_bytes, commit_info, group, leaf, metadata,
                         metadata_bytes, of_table, open_bytes, parquet_file, protocol, rows, schema_footer)
from pqax.collect import LocalStorage, TableFormats, discover_folder, parse_footer

needs_pyarrow = pytest.mark.skipif(not HAS_PYARROW, reason="pyarrow writes the files of the test, pqax[collect]")

FOLDER = "data/nested"
OLDEST, MIDDLE, NEWEST = "part-0001.parquet", "part-0002.parquet", "part-0003.parquet"


# ---- collecting a folder ----

def collect(storage: Any, folder: str, **options: Any):
    [location] = discover_folder(storage, folder)
    data, _ = collect_bytes(storage, [location], checkpoint_reader=None, **options)
    assert_valid(data)
    doc = open_bytes(data)
    [table] = rows(doc, "tables")
    return doc, table


def physical_columns(doc, table, file: Optional[str] = None) -> List[Dict[str, Any]]:
    """The physical columns of a table, or of the schema of one of its files, in the order of their ordinals."""
    if file is None:
        schemas = {schema["id"] for schema in of_table(doc, "physicalSchemas", table["id"])}
    else:
        [row] = [row for row in of_table(doc, "files", table["id"]) if row["path"] == file]
        schemas = {row["physicalSchema"]}
    return sorted((row for row in rows(doc, "physicalColumns") if row["physicalSchema"] in schemas),
                  key=lambda row: (row["physicalSchema"], row["ordinal"]))


def path_of(column: Dict[str, Any]) -> str:
    return ".".join(column["path"])


def name_of(columns: List[Dict[str, Any]], physical: Dict[str, Any]) -> Optional[str]:
    if physical.get("column") is None:
        return None
    [column] = [column for column in columns if column["id"] == physical["column"]]
    return column["name"]


# ---- the files of pyarrow ----

def arrow_schema(pa):
    leaf3 = pa.struct([pa.field("text", pa.string()), pa.field("numbers", pa.list_(pa.int64()))])
    return pa.schema([
        pa.field("id", pa.int64(), nullable=False),
        pa.field("tags", pa.list_(pa.int32())),
        pa.field("big", pa.large_list(pa.string())),
        pa.field("props", pa.map_(pa.string(), pa.int64())),
        pa.field("deep", pa.struct([pa.field("name", pa.string()),
                                    pa.field("inner", pa.struct([pa.field("value", pa.int32()),
                                                                 pa.field("leaf", leaf3)]))])),
        pa.field("points", pa.list_(pa.struct([pa.field("x", pa.int32(), nullable=False),
                                               pa.field("label", pa.string())]))),
        pa.field("map_of_lists", pa.map_(pa.string(), pa.list_(pa.int32()))),
        pa.field("category", pa.string()),
    ])


# Nulls at each level: a null value, an empty list or map, a null element, a null field of a structure.
ARROW_ROWS = [
    {"id": 1, "tags": [1, None, 3], "big": ["a", None], "props": [("a", 1), ("b", None)],
     "deep": {"name": "x", "inner": {"value": 5, "leaf": {"text": "t", "numbers": [1, 2]}}},
     "points": [{"x": 1, "label": "p"}, None], "map_of_lists": [("k", [1, 2]), ("n", None)], "category": "a"},
    {"id": 2, "tags": None, "big": None, "props": None, "deep": None, "points": None, "map_of_lists": None,
     "category": None},
    {"id": 3, "tags": [], "big": [], "props": [], "deep": {"name": None, "inner": None}, "points": [],
     "map_of_lists": [], "category": "a"},
    {"id": 4, "tags": [None], "big": [None], "props": [("c", None)],
     "deep": {"name": "y", "inner": {"value": None, "leaf": None}}, "points": [None], "map_of_lists": [("e", [])],
     "category": "b"},
    {"id": 5, "tags": [7], "big": ["z"], "props": [("d", 4)],
     "deep": {"name": "z", "inner": {"value": 7, "leaf": {"text": None, "numbers": None}}},
     "points": [{"x": 2, "label": None}], "map_of_lists": [("f", [None, 9])], "category": "a"},
    {"id": 6, "tags": [8, 9], "big": ["y", "y"], "props": [("e", 5)],
     "deep": {"name": "w", "inner": {"value": 8, "leaf": {"text": "u", "numbers": []}}},
     "points": [{"x": 3, "label": "q"}], "map_of_lists": [("g", [1])], "category": "a"},
]

OLDEST_ROWS = [
    {"id": 10, "TAGS": [1], "deep": {"name": "o", "inner": {"value": 1, "leaf": {"text": "old", "numbers": [5]}}},
     "retired": [("r", "v")]},
    {"id": 11, "TAGS": None, "deep": None, "retired": None},
    {"id": 12, "TAGS": [None, 2], "deep": {"name": None, "inner": None}, "retired": [("s", None)]},
    {"id": 13, "TAGS": [], "deep": {"name": "p", "inner": {"value": None, "leaf": None}}, "retired": []},
]


def write_arrow_files(lake: Container) -> None:
    """The three files of the folder: the newest with the names of the specification, a middle one with the legacy
    list/item, and the oldest with a column in another case and a column that the newest no longer has."""
    import pyarrow as pa
    import pyarrow.parquet as pq

    schema = arrow_schema(pa)
    table = pa.Table.from_pylist(ARROW_ROWS, schema=schema)
    for name, moment, options in ((NEWEST, NEWER, {"row_group_size": 3}),
                                  (MIDDLE, OLDER + datetime.timedelta(minutes=30),
                                   {"row_group_size": 2, "compression": "zstd", "use_compliant_nested_type": False})):
        path = lake.full(f"{FOLDER}/{name}")
        path.parent.mkdir(parents=True, exist_ok=True)
        pq.write_table(table, str(path), **options)
        lake.touch(f"{FOLDER}/{name}", moment)
    oldest = pa.schema([schema.field("id"), pa.field("TAGS", pa.list_(pa.int32())), schema.field("deep"),
                        pa.field("retired", pa.map_(pa.string(), pa.string()))])
    pq.write_table(pa.Table.from_pylist(OLDEST_ROWS, schema=oldest), str(lake.full(f"{FOLDER}/{OLDEST}")),
                   row_group_size=2, compression="gzip")
    lake.touch(f"{FOLDER}/{OLDEST}", OLDER)


@pytest.fixture
def arrow_folder(tmp_path):
    lake = Container(tmp_path / "arrow")
    write_arrow_files(lake)
    doc, table = collect(LocalStorage(str(lake.root)), FOLDER)
    assert of_table(doc, "issues", table["id"]) == []
    return lake, doc, table


# The leaves of the newest file as pyarrow writes them: path, type, repetition, annotation.
NEWEST_LEAVES = [
    ("id", "INT64", "REQUIRED", None),
    ("tags.list.element", "INT32", "OPTIONAL", None),
    ("big.list.element", "BYTE_ARRAY", "OPTIONAL", "STRING"),
    ("props.key_value.key", "BYTE_ARRAY", "REQUIRED", "STRING"),
    ("props.key_value.value", "INT64", "OPTIONAL", None),
    ("deep.name", "BYTE_ARRAY", "OPTIONAL", "STRING"),
    ("deep.inner.value", "INT32", "OPTIONAL", None),
    ("deep.inner.leaf.text", "BYTE_ARRAY", "OPTIONAL", "STRING"),
    ("deep.inner.leaf.numbers.list.element", "INT64", "OPTIONAL", None),
    ("points.list.element.x", "INT32", "REQUIRED", None),
    ("points.list.element.label", "BYTE_ARRAY", "OPTIONAL", "STRING"),
    ("map_of_lists.key_value.key", "BYTE_ARRAY", "REQUIRED", "STRING"),
    ("map_of_lists.key_value.value.list.element", "INT32", "OPTIONAL", None),
    ("category", "BYTE_ARRAY", "OPTIONAL", "STRING"),
]


def legacy(path: str) -> str:
    return path.replace(".list.element", ".list.item")


def arrow_metadata(lake: Container, name: str):
    import pyarrow.parquet as pq
    return pq.ParquetFile(str(lake.full(f"{FOLDER}/{name}"))).metadata


@needs_pyarrow
def test_each_leaf_of_a_nested_schema_is_one_physical_column_with_every_name_of_its_path_in_the_order_of_the_leaves(
        arrow_folder):
    lake, doc, table = arrow_folder
    assert len(of_table(doc, "physicalSchemas", table["id"])) == 3
    for name in (OLDEST, MIDDLE, NEWEST):
        schema = arrow_metadata(lake, name).schema
        physical = physical_columns(doc, table, name)
        assert [column["ordinal"] for column in physical] == list(range(len(schema)))
        assert [path_of(column) for column in physical] == [schema.column(i).path for i in range(len(schema))]
    assert [path_of(column) for column in physical_columns(doc, table, NEWEST)] == [path for path, _, _, _ in
                                                                                   NEWEST_LEAVES]
    # The legacy names of use_compliant_nested_type=False.
    assert [path_of(column) for column in physical_columns(doc, table, MIDDLE)] == [legacy(path) for path, _, _, _ in
                                                                                   NEWEST_LEAVES]


@needs_pyarrow
def test_a_physical_column_has_the_types_of_its_leaf_and_not_those_of_the_groups_above_it(arrow_folder):
    lake, doc, table = arrow_folder
    for name in (OLDEST, MIDDLE, NEWEST):
        schema = arrow_metadata(lake, name).schema
        parsed = parse_footer(metadata_bytes(lake.full(f"{FOLDER}/{name}").read_bytes()))
        physical = physical_columns(doc, table, name)
        assert [column["physicalType"] for column in physical] == [schema.column(i).physical_type
                                                                   for i in range(len(schema))]
        assert [column.get("logicalType") for column in physical] == [leaf_.type_annotation for leaf_ in parsed.leaves]
        assert all(column.get("typeLength") is None and column.get("fieldId") is None for column in physical)
    # The annotation and the repetition of the leaf itself: a LIST or MAP group, a REPEATED group, and an OPTIONAL
    # structure above it.
    assert [(path_of(column), column["physicalType"], column.get("repetition"), column.get("logicalType"))
            for column in physical_columns(doc, table, NEWEST)] == NEWEST_LEAVES


@dataclass(frozen=True)
class Node:
    """A field of a schema with a field id, written as NestedColumnTests.cs writes it."""

    name: str
    type: Optional[str] = None
    repetition: Optional[str] = "OPTIONAL"
    children: Tuple["Node", ...] = ()
    converted: Optional[str] = None
    logical: Any = None
    field_id: Optional[int] = None


def node(name: str, physical: str, repetition: Optional[str] = "OPTIONAL", field_id: Optional[int] = None) -> Node:
    return Node(name, physical, repetition, field_id=field_id)


def node_group(name: str, *children: Node, repetition: Optional[str] = "OPTIONAL", field_id: Optional[int] = None,
               **annotations: Any) -> Node:
    return Node(name, None, repetition, tuple(children), field_id=field_id, **annotations)


def id_footer(fields: Sequence[Node], rows_: int = 1) -> bytes:
    """collect_kit.schema_footer with the field ids of the nodes, between the precision and the logical type."""
    w = Thrift()
    w.i32(1, 2)
    elements: List[Tuple[Node, Tuple[str, ...]]] = []
    leaves: List[Tuple[Node, Tuple[str, ...]]] = []

    def walk(element: Node, path: Tuple[str, ...]) -> None:
        elements.append((element, path))
        if element.type is not None:
            leaves.append((element, path))
        for child in element.children:
            walk(child, path + (child.name,))

    for field in fields:
        walk(field, (field.name,))
    w.list_of(2, Thrift.STRUCT, len(elements) + 1)
    w.element().binary(4, "schema").i32(5, len(fields)).end()
    for element, _ in elements:
        w.element()
        if element.type is not None:
            w.i32(1, PHYSICAL_TYPES.index(element.type))
        if element.repetition is not None:
            w.i32(3, REPETITIONS.index(element.repetition))
        w.binary(4, element.name)
        if element.type is None:
            w.i32(5, len(element.children))
        if element.converted is not None:
            w.i32(6, CONVERTED_TYPES.index(element.converted))
        if element.field_id is not None:
            w.i32(9, element.field_id)
        if element.logical is not None:
            _write_logical(w, element.logical)
        w.end()
    w.i64(3, rows_)
    w.list_of(4, Thrift.STRUCT, 1)
    w.element()
    w.list_of(1, Thrift.STRUCT, len(leaves))
    for index, (element, path) in enumerate(leaves):
        w.element()
        w.i64(2, 4)
        w.begin(3)
        w.i32(1, PHYSICAL_TYPES.index(element.type or "INT64"))
        w.list_of(2, Thrift.I32, 1).value_i32(0)
        w.list_of(3, Thrift.BINARY, len(path))
        for name in path:
            w.value_binary(name)
        w.i32(4, 6).i64(5, rows_).i64(6, 100 + index).i64(7, 50 + index).i64(9, 4)
        w.end()
        w.end()
    w.i64(2, 100 * len(leaves)).i64(3, rows_)
    w.end()
    w.binary(6, "Synthetic writer 1.0")
    w.end()
    return w.bytes()


def tags_list(name: str, list_field_id: Optional[int] = None, element_field_id: Optional[int] = None) -> Node:
    return node_group(name, node_group("list", node("element", "INT32", field_id=element_field_id),
                                       repetition="REPEATED"), logical="LIST", field_id=list_field_id)


def test_the_field_id_of_a_physical_column_is_that_of_its_leaf_and_not_of_its_top_level_field(tmp_path):
    lake = Container(tmp_path / "ids")
    fields = [node_group("tags", node_group("list", node("element", "INT32", field_id=21), repetition="REPEATED",
                                            field_id=20), logical="LIST", field_id=2),
              node_group("point", node("x", "DOUBLE", field_id=31), node("y", "DOUBLE"), field_id=3),
              node("plain", "INT64", field_id=4)]
    lake.write("data/ids/part-0001.parquet", parquet_file(id_footer(fields)))
    doc, table = collect(LocalStorage(str(lake.root)), "data/ids")
    physical = physical_columns(doc, table)
    assert [(path_of(column), column.get("fieldId")) for column in physical] == [
        ("tags.list.element", 21), ("point.x", 31), ("point.y", None), ("plain", 4)]
    # A folder of Parquet files has no field ids, so the leaves find their column by name.
    columns = of_table(doc, "columns", table["id"])
    assert all(column.get("fieldId") is None for column in columns)
    assert [name_of(columns, column) for column in physical] == ["tags", "point", "point", "plain"]


# ---- N3: the column of a leaf ----

@needs_pyarrow
def test_the_column_of_a_leaf_is_its_top_level_field_by_name_exactly_then_without_case_and_a_field_that_the_table_does_not_declare_is_stale(  # noqa: E501
        arrow_folder):
    lake, doc, table = arrow_folder
    columns = of_table(doc, "columns", table["id"])
    assert [column["name"] for column in columns] == ["id", "tags", "big", "props", "deep", "points", "map_of_lists",
                                                      "category"]
    for name in (MIDDLE, NEWEST):
        assert all(name_of(columns, column) == column["path"][0] for column in physical_columns(doc, table, name))
    oldest = physical_columns(doc, table, OLDEST)
    assert [name_of(columns, column) for column in oldest] == [
        {"TAGS": "tags", "retired": None}.get(column["path"][0], column["path"][0]) for column in oldest]
    # The leaves of retired, which the table does not declare, are the stale size.
    md = arrow_metadata(lake, OLDEST)
    stale = sum(md.row_group(g).column(c).total_compressed_size for g in range(md.num_row_groups)
                for c in range(md.num_columns) if md.row_group(g).column(c).path_in_schema.startswith("retired."))
    assert stale > 0
    assert table["staleColumnSize"] == stale


def two_crafted_files(tmp_path, folder: str, newer: Sequence[str], older: Sequence[str]):
    lake = Container(tmp_path / "crafted")
    lake.write(f"{folder}/{NEWEST}", parquet_file(schema_footer([leaf(name, "INT64") for name in newer])))
    lake.touch(f"{folder}/{NEWEST}", NEWER)
    lake.write(f"{folder}/{OLDEST}", parquet_file(schema_footer([leaf(name, "INT64") for name in older])))
    lake.touch(f"{folder}/{OLDEST}", OLDER)
    return collect(LocalStorage(str(lake.root)), folder)


def test_a_leaf_that_matches_two_columns_without_case_and_none_exactly_is_stale_and_an_exact_match_wins(tmp_path):
    doc, table = two_crafted_files(tmp_path, "data/case", ["dup", "DUP", "Mixed", "MIXED", "x"], ["Dup", "MIXED", "X"])
    columns = of_table(doc, "columns", table["id"])
    assert [column["name"] for column in columns] == ["dup", "DUP", "Mixed", "MIXED", "x"]
    assert [name_of(columns, column) for column in physical_columns(doc, table, OLDEST)] == [None, "MIXED", "x"]
    # The synthetic footers give a leaf of ordinal i the compressed size 50 + i: Dup is the first leaf.
    assert table["staleColumnSize"] == 50
    assert [column["fileCountWithoutColumn"] for column in columns] == [1, 1, 1, 0, 0]


def test_a_column_of_a_long_s_does_not_match_a_leaf_of_an_s_and_an_e_acute_matches_its_capital_as_ordinal_ignore_case_compares(  # noqa: E501
        tmp_path):
    # P4 as amended: the same fold for column names and for the keys of the allow-list; a character outside ASCII never
    # folds into ASCII, and the others fold to their upper case.
    doc, table = two_crafted_files(tmp_path, "data/fold", ["ſum", "Été", "both"],
                                   ["sum", "été", "BOTH"])
    columns = of_table(doc, "columns", table["id"])
    assert [column["name"] for column in columns] == ["ſum", "Été", "both"]
    assert [name_of(columns, column) for column in physical_columns(doc, table, OLDEST)] == [None, "Été",
                                                                                             "both"]
    assert table["staleColumnSize"] == 50
    assert [column["fileCountWithoutColumn"] for column in columns] == [1, 0, 0]


def mapped_schema() -> str:
    def mapping(field_id: int, physical: str) -> Dict[str, Any]:
        return {"delta.columnMapping.id": field_id, "delta.columnMapping.physicalName": physical}
    return json.dumps({"type": "struct", "fields": [
        {"name": "amount", "type": "long", "nullable": True, "metadata": mapping(1, "col-amount")},
        {"name": "tags", "type": {"type": "array", "elementType": "integer", "containsNull": True}, "nullable": True,
         "metadata": mapping(2, "col-tags")},
        {"name": "point", "type": {"type": "struct", "fields": [
            {"name": "x", "type": "double", "nullable": True, "metadata": mapping(4, "col-x")}]},
         "nullable": True, "metadata": mapping(3, "col-point")},
    ]})


def test_the_column_of_a_leaf_is_found_by_the_field_id_of_its_top_level_field_or_else_by_the_physical_name_of_a_mapped_column(  # noqa: E501
        tmp_path):
    lake = Container(tmp_path / "mapped")
    table_path = "data/mapped"
    # With field ids: a name that is no physical name but the id of amount, and the physical name of amount with an id
    # that no column has.
    with_ids = [node("other-name", "INT64", field_id=1), tags_list("col-tags", 2, 5),
                node_group("col-point", node("col-x", "DOUBLE", field_id=4), field_id=3),
                node("col-amount", "INT32", field_id=99)]
    # Without field ids: the physical names, one in another case, and the logical name of a column, which the files do
    # not use.
    without_ids = [node("col-amount", "INT64"), tags_list("COL-TAGS"), node("amount", "INT64"),
                   node_group("col-point", node("col-x", "DOUBLE"))]
    first, second = parquet_file(id_footer(with_ids, 3)), parquet_file(id_footer(without_ids, 4))
    lake.write(f"{table_path}/with-ids.parquet", first)
    lake.write(f"{table_path}/without-ids.parquet", second)
    lake.commit(table_path, 0, protocol(2, 5),
                metadata(mapped_schema(), configuration={"delta.columnMapping.mode": "id",
                                                         "delta.columnMapping.maxColumnId": "5"}),
                add("with-ids.parquet", len(first), 3), add("without-ids.parquet", len(second), 4), commit_info(0))
    storage = LocalStorage(str(lake.root))
    [location] = discover_folder(storage, table_path)
    assert location.format == TableFormats.DELTA
    doc, table = collect(storage, table_path)
    assert of_table(doc, "issues", table["id"]) == []
    columns = of_table(doc, "columns", table["id"])
    assert [(column["name"], column.get("fieldId"), column.get("physicalName")) for column in columns] == [
        ("amount", 1, "col-amount"), ("tags", 2, "col-tags"), ("point", 3, "col-point")]
    assert [(path_of(column), name_of(columns, column)) for column in physical_columns(doc, table, "with-ids.parquet")] \
        == [("other-name", "amount"), ("col-tags.list.element", "tags"), ("col-point.col-x", "point"),
            ("col-amount", None)]
    assert [(path_of(column), name_of(columns, column))
            for column in physical_columns(doc, table, "without-ids.parquet")] == [
        ("col-amount", "amount"), ("COL-TAGS.list.element", "tags"), ("amount", None), ("col-point.col-x", "point")]
    assert [column.get("fieldId") for column in physical_columns(doc, table, "with-ids.parquet")] == [1, 5, 4, 99]
    # A leaf of ordinal i has the compressed size 50 + i: col-amount (3) of the first file and amount (2) of the
    # second are stale.
    assert table["staleColumnSize"] == 53 + 52
    assert [column["compressedSize"] for column in columns] == [50 + 50, 51 + 51, 52 + 53]
    assert [column["valueCount"] for column in columns] == [3 + 4, 3 + 4, 3 + 4]
    assert all(column["fileCountWithoutColumn"] == 0 for column in columns)


# ---- N4: the totals of a column, over all its leaves ----

def total(terms: Sequence[Optional[int]]) -> Optional[int]:
    """A sum that is unknown when a term is, and when there is no term."""
    if not terms or any(term is None for term in terms):
        return None
    return sum(terms)


def fallback(chunk) -> Optional[int]:
    """As the README of the collector says: from the encoding statistics when the footer has them; without them, false
    when the chunk lists only dictionary and level encodings, and unknown otherwise."""
    if not chunk.has_dictionary_page:
        return 0
    if chunk.encoding_stats is not None:
        data = sum(stats.count for stats in chunk.encoding_stats if stats.is_data_page)
        dictionary = sum(stats.count for stats in chunk.encoding_stats if stats.is_data_page and stats.is_dictionary_encoded)
        return 1 if dictionary < data else 0
    if all(encoding in ("PLAIN_DICTIONARY", "RLE_DICTIONARY", "RLE", "BIT_PACKED") for encoding in chunk.encodings):
        return 0
    return None


TOTALS = ("compressedSize", "uncompressedSize", "chunkCount", "valueCount", "nullCount", "dictionaryChunkCount",
          "fallbackChunkCount", "dictionaryPageSize", "unencodedByteArrayDataBytes", "codecs", "encodings",
          "largestChunkDistinctCount", "fileCountWithoutColumn")


@needs_pyarrow
def test_the_totals_of_a_nested_column_are_over_the_column_chunks_of_all_its_leaves_in_every_file(arrow_folder):
    lake, doc, table = arrow_folder
    columns = of_table(doc, "columns", table["id"])
    mapped = {"TAGS": "tags", "retired": None}
    # Each chunk of the three files, with the values that pyarrow reads, and the encoding statistics and the size
    # statistics, which pyarrow does not give, from the parsed footer.
    chunks: List[Dict[str, Any]] = []
    for name in (OLDEST, MIDDLE, NEWEST):
        md = arrow_metadata(lake, name)
        parsed = parse_footer(metadata_bytes(lake.full(f"{FOLDER}/{name}").read_bytes()))
        assert md.num_row_groups > 1
        for g in range(md.num_row_groups):
            for c in range(md.num_columns):
                m = md.row_group(g).column(c)
                top = m.path_in_schema.split(".")[0]
                stats = m.statistics if m.is_stats_set else None
                dictionary = m.has_dictionary_page
                size = (m.data_page_offset - m.dictionary_page_offset
                        if dictionary and 0 < (m.dictionary_page_offset or 0) < m.data_page_offset else None)
                chunks.append({
                    "file": name, "ordinal": c, "column": mapped.get(top, top), "type": m.physical_type,
                    "compressed": m.total_compressed_size, "uncompressed": m.total_uncompressed_size,
                    "values": m.num_values,
                    "nulls": stats.null_count if stats is not None and stats.has_null_count else None,
                    "distinct": stats.distinct_count if stats is not None and stats.has_distinct_count else None,
                    "dictionary": dictionary, "dictionaryPageSize": size if dictionary else 0,
                    "fallback": fallback(parsed.row_groups[g].columns[c]),
                    "unencoded": parsed.row_groups[g].columns[c].unencoded_byte_array_data_bytes,
                    "codec": m.compression, "encodings": m.encodings})
    assert any(chunk["dictionary"] for chunk in chunks)
    assert {chunk["codec"] for chunk in chunks} == {"SNAPPY", "ZSTD", "GZIP"}
    for column in columns:
        own = [chunk for chunk in chunks if chunk["column"] == column["name"]]
        byte_arrays = [chunk for chunk in own if chunk["type"] == "BYTE_ARRAY"]
        distinct = [chunk["distinct"] for chunk in own if chunk["distinct"] is not None]
        expected = {
            "compressedSize": total([chunk["compressed"] for chunk in own]),
            "uncompressedSize": total([chunk["uncompressed"] for chunk in own]),
            "chunkCount": len(own),
            "valueCount": total([chunk["values"] for chunk in own]),
            "nullCount": total([chunk["nulls"] for chunk in own]),
            "dictionaryChunkCount": sum(1 for chunk in own if chunk["dictionary"]),
            "fallbackChunkCount": total([chunk["fallback"] for chunk in own]),
            "dictionaryPageSize": total([chunk["dictionaryPageSize"] for chunk in own]),
            "unencodedByteArrayDataBytes": total([chunk["unencoded"] for chunk in byte_arrays]),
            "codecs": sorted({chunk["codec"] for chunk in own}) or None,
            "encodings": sorted({encoding for chunk in own for encoding in chunk["encodings"]}) or None,
            "largestChunkDistinctCount": max(distinct) if distinct else None,
            "fileCountWithoutColumn": sum(1 for name in (OLDEST, MIDDLE, NEWEST)
                                          if not any(chunk["file"] == name for chunk in own)),
        }
        assert {name: column.get(name) for name in TOTALS} == expected, column["name"]
    by_name = {column["name"]: column for column in columns}
    assert by_name["props"]["fileCountWithoutColumn"] == 1 and by_name["tags"]["fileCountWithoutColumn"] == 0
    # The table: every chunk, and the chunks of the leaves without a column as the stale size.
    assert table["compressedSize"] == sum(chunk["compressed"] for chunk in chunks)
    assert table["staleColumnSize"] == sum(chunk["compressed"] for chunk in chunks if chunk["column"] is None)
    assert table["footerCount"] == 3
    # A physical column: the chunks of its leaf in its file.
    for name in (OLDEST, MIDDLE, NEWEST):
        for physical in physical_columns(doc, table, name):
            own = [chunk for chunk in chunks if chunk["file"] == name and chunk["ordinal"] == physical["ordinal"]]
            assert (physical["compressedSize"], physical["uncompressedSize"], physical["chunkCount"]) == (
                sum(chunk["compressed"] for chunk in own), sum(chunk["uncompressed"] for chunk in own), len(own))


@needs_pyarrow
def test_the_footer_parser_reads_the_chunks_of_pyarrow_as_pyarrow_reads_them(tmp_path):
    lake = Container(tmp_path / "arrow")
    write_arrow_files(lake)
    for name in (OLDEST, MIDDLE, NEWEST):
        md = arrow_metadata(lake, name)
        parsed = parse_footer(metadata_bytes(lake.full(f"{FOLDER}/{name}").read_bytes()))
        assert (parsed.num_rows, len(parsed.row_groups), parsed.created_by) == (md.num_rows, md.num_row_groups,
                                                                                md.created_by)
        for g, group_ in enumerate(parsed.row_groups):
            for c, chunk in enumerate(group_.columns):
                m = md.row_group(g).column(c)
                assert (chunk.codec, chunk.num_values, chunk.total_compressed_size, chunk.total_uncompressed_size,
                        chunk.data_page_offset, chunk.dictionary_page_offset, chunk.null_count) == (
                    m.compression, m.num_values, m.total_compressed_size, m.total_uncompressed_size,
                    m.data_page_offset, m.dictionary_page_offset, m.statistics.null_count)
                assert sorted(chunk.encodings) == sorted(m.encodings)


# ---- N5: the types of the nested columns of a folder ----

ARROW_COLUMNS = [
    ("id", "long", False), ("tags", "array<integer>", True), ("big", "array<string>", True),
    ("props", "map<string,long>", True),
    ("deep", "struct<name:string,inner:struct<value:integer,leaf:struct<text:string,numbers:array<long>>>>", True),
    ("points", "array<struct<x:integer,label:string>>", True), ("map_of_lists", "map<string,array<integer>>", True),
    ("category", "string", True),
]


@needs_pyarrow
def test_the_type_of_a_nested_column_of_a_folder_is_in_the_names_of_the_delta_schema_and_its_nullability_that_of_its_top_level_field(  # noqa: E501
        arrow_folder):
    _, doc, table = arrow_folder
    assert [(column["name"], column["dataType"], column["isNullable"])
            for column in of_table(doc, "columns", table["id"])] == ARROW_COLUMNS


@needs_pyarrow
def test_a_file_with_the_legacy_names_of_pyarrow_gives_the_same_columns(tmp_path):
    lake = Container(tmp_path / "arrow")
    write_arrow_files(lake)
    alone = Container(tmp_path / "alone")
    alone.write(f"{FOLDER}/{MIDDLE}", lake.full(f"{FOLDER}/{MIDDLE}").read_bytes())
    doc, table = collect(LocalStorage(str(alone.root)), FOLDER)
    assert of_table(doc, "issues", table["id"]) == []
    assert [(column["name"], column["dataType"], column["isNullable"])
            for column in of_table(doc, "columns", table["id"])] == ARROW_COLUMNS
    assert [path_of(column) for column in physical_columns(doc, table)] == [legacy(path) for path, _, _, _ in
                                                                           NEWEST_LEAVES]


# The layouts of lists and maps of the specification and of older writers, as NestedColumnTests.cs writes them: the
# field, its column, and its leaves (path, type, repetition, annotation).
LAYOUTS = [
    (group("tags", group("list", leaf("element", "INT32", "REQUIRED"), repetition="REPEATED"), logical="LIST"),
     "array<integer>", True, [("tags.list.element", "INT32", "REQUIRED", None)]),
    (group("items", group("list", leaf("item", "BYTE_ARRAY", logical="STRING"), repetition="REPEATED"), logical="LIST"),
     "array<string>", True, [("items.list.item", "BYTE_ARRAY", "OPTIONAL", "STRING")]),
    (group("legacy", leaf("value", "INT64", "REPEATED"), converted="LIST"),
     "array<long>", True, [("legacy.value", "INT64", "REPEATED", None)]),
    (group("points", group("array", leaf("x", "DOUBLE"), repetition="REPEATED"), converted="LIST"),
     "array<struct<x:double>>", True, [("points.array.x", "DOUBLE", "OPTIONAL", None)]),
    (group("pairs", group("pairs_tuple", leaf("a", "INT32"), repetition="REPEATED"), converted="LIST"),
     "array<struct<a:integer>>", True, [("pairs.pairs_tuple.a", "INT32", "OPTIONAL", None)]),
    (group("hive", group("bag", leaf("array_element", "INT32"), repetition="REPEATED"), converted="LIST"),
     "array<integer>", True, [("hive.bag.array_element", "INT32", "OPTIONAL", None)]),
    (group("spans", group("item", leaf("a", "INT32"), leaf("b", "INT32"), repetition="REPEATED"), logical="LIST"),
     "array<struct<a:integer,b:integer>>", True,
     [("spans.item.a", "INT32", "OPTIONAL", None), ("spans.item.b", "INT32", "OPTIONAL", None)]),
    (group("props", group("key_value", leaf("key", "BYTE_ARRAY", "REQUIRED", logical="STRING"), leaf("value", "INT64"),
                          repetition="REPEATED"), logical="MAP"),
     "map<string,long>", True,
     [("props.key_value.key", "BYTE_ARRAY", "REQUIRED", "STRING"), ("props.key_value.value", "INT64", "OPTIONAL", None)]),
    (group("old_map", group("map", leaf("key", "BYTE_ARRAY", "REQUIRED", converted="UTF8"), leaf("value", "DOUBLE"),
                            repetition="REPEATED"), converted="MAP_KEY_VALUE"),
     "map<string,double>", True,
     [("old_map.map.key", "BYTE_ARRAY", "REQUIRED", "UTF8"), ("old_map.map.value", "DOUBLE", "OPTIONAL", None)]),
    (group("kv", group("map", leaf("key", "INT32", "REQUIRED"), leaf("value", "BOOLEAN"), repetition="REPEATED",
                       converted="MAP_KEY_VALUE"), converted="MAP"),
     "map<integer,boolean>", True, [("kv.map.key", "INT32", "REQUIRED", None), ("kv.map.value", "BOOLEAN", "OPTIONAL", None)]),
    (group("mls", group("key_value", leaf("key", "INT32", "REQUIRED"),
                        group("value", group("list", group("element", leaf("p", "FLOAT"),
                                                           leaf("q", "BYTE_ARRAY", logical="STRING")),
                                             repetition="REPEATED"), logical="LIST"),
                        repetition="REPEATED"), logical="MAP"),
     "map<integer,array<struct<p:float,q:string>>>", True,
     [("mls.key_value.key", "INT32", "REQUIRED", None), ("mls.key_value.value.list.element.p", "FLOAT", "OPTIONAL", None),
      ("mls.key_value.value.list.element.q", "BYTE_ARRAY", "OPTIONAL", "STRING")]),
    (group("deep", group("l2", group("l3", leaf("x", "INT32"),
                                     group("xs", group("list", leaf("element", "INT64"), repetition="REPEATED"),
                                           logical="LIST")))),
     "struct<l2:struct<l3:struct<x:integer,xs:array<long>>>>", True,
     [("deep.l2.l3.x", "INT32", "OPTIONAL", None), ("deep.l2.l3.xs.list.element", "INT64", "OPTIONAL", None)]),
    (leaf("numbers", "INT32", "REPEATED"), "array<integer>", False, [("numbers", "INT32", "REPEATED", None)]),
    (group("records", leaf("a", "INT32"), repetition="REPEATED"), "array<struct<a:integer>>", False,
     [("records.a", "INT32", "OPTIONAL", None)]),
    (group("matrix", group("list", group("element", group("list", leaf("element", "DOUBLE"), repetition="REPEATED"),
                                         logical="LIST"), repetition="REPEATED"), logical="LIST"),
     "array<array<double>>", True, [("matrix.list.element.list.element", "DOUBLE", "OPTIONAL", None)]),
    (group("req", leaf("a", "INT32", "REQUIRED"), repetition="REQUIRED"), "struct<a:integer>", False,
     [("req.a", "INT32", "REQUIRED", None)]),
]


@pytest.fixture
def layouts(tmp_path):
    lake = Container(tmp_path / "layouts")
    lake.write("data/layouts/part-0001.parquet", parquet_file(schema_footer([field for field, _, _, _ in LAYOUTS], 7)))
    doc, table = collect(LocalStorage(str(lake.root)), "data/layouts")
    assert of_table(doc, "issues", table["id"]) == []
    return doc, table


def test_the_paths_of_the_leaves_keep_the_names_of_the_levels_of_lists_and_maps_of_every_layout(layouts):
    doc, table = layouts
    columns = of_table(doc, "columns", table["id"])
    physical = physical_columns(doc, table)
    assert [(path_of(column), column["physicalType"], column.get("repetition"), column.get("logicalType"),
             name_of(columns, column)) for column in physical] == [
        (path, physical_type, repetition, annotation, field.name)
        for field, _, _, leaves in LAYOUTS for path, physical_type, repetition, annotation in leaves]
    assert all(column.get("typeLength") is None and column.get("fieldId") is None for column in physical)


def test_the_type_of_a_column_of_every_layout_of_lists_and_maps_is_the_one_that_spark_reads(layouts):
    doc, table = layouts
    assert [(column["name"], column["dataType"], column["isNullable"])
            for column in of_table(doc, "columns", table["id"])] == [
        (field.name, data_type, nullable) for field, data_type, nullable, _ in LAYOUTS]


def test_the_totals_of_a_column_of_every_layout_are_the_sums_over_its_leaves(layouts):
    doc, table = layouts
    columns = {column["name"]: column for column in of_table(doc, "columns", table["id"])}
    # The synthetic footers give the leaf of ordinal i the compressed size 50 + i, the uncompressed size 100 + i, the
    # rows of the file as its values, ZSTD, PLAIN, and no statistics, dictionary, or size statistics.
    ordinal = 0
    for field, _, _, leaves in LAYOUTS:
        ordinals = range(ordinal, ordinal + len(leaves))
        ordinal += len(leaves)
        expected = {"compressedSize": sum(50 + i for i in ordinals), "uncompressedSize": sum(100 + i for i in ordinals),
                    "chunkCount": len(leaves), "valueCount": 7 * len(leaves), "nullCount": None,
                    "dictionaryChunkCount": 0, "fallbackChunkCount": 0, "dictionaryPageSize": 0,
                    "unencodedByteArrayDataBytes": None, "codecs": ["ZSTD"], "encodings": ["PLAIN"],
                    "largestChunkDistinctCount": None, "fileCountWithoutColumn": 0}
        assert {name: columns[field.name].get(name) for name in TOTALS} == expected, field.name
