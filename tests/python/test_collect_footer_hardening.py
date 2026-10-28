"""The footer reader and parser of pqax.collect on malformed and hostile footers, from the rules of
conformance/footers.md: R1-R6, the outcome of a footer read whatever the bytes
and the stated size, and P1-P5, the parse. Where a rule keeps the behaviour that the parser had before, the test
pins it, messages included.

The footers are written with the Thrift writer of collect_kit, and FooterHardeningTests.cs of the C# tests writes the
same bytes (the hashes of the two base footers are pinned in both), so that the two suites expect the same code and the
same message for the same bytes, as R2 asks. Standard library only.
"""
from __future__ import annotations

import hashlib
import random
import struct
import time
import tracemalloc
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple

import pytest

from collect_kit import (Container, Thrift, VirtualFileStorage, assert_valid, collect_bytes, data_files, every_type,
                         file_bytes, footer, group, leaf, metadata_bytes, nested_lists, nested_structures, of_table,
                         open_bytes, parquet_ending_with, parquet_file, rows, schema_footer, tail)
from pqax.collect import (MAX_FOOTER_LENGTH, FooterError, FooterReader, IssueCodes, LocalStorage, StorageError,
                          TableFormats, discover_folder, parse_footer)
from pqax.collect.footer import is_kept_key

INVALID = IssueCodes.FOOTER_INVALID
CODES = (IssueCodes.FOOTER_INVALID, IssueCodes.FOOTER_ENCRYPTED, IssueCodes.FOOTER_TOO_LARGE)

# The messages of the checks that the rules keep as they were, the same in C#.
TOO_SMALL = "The file is too small to be a Parquet file: {} bytes."
SHORTER = "The file is shorter than the size that the table format gives."
ENCRYPTED = "The footer is encrypted."
NO_MAGIC = "The file does not end with the Parquet magic bytes."
NOT_FITTING = "The length of the footer, {}, does not fit the file."
TOO_LARGE = "The footer takes {} bytes, more than the limit of 67108864."
TRUNCATED = "The footer ends in the middle of a value."
LIST_TOO_LONG = "A list of the footer is too long."
VARINT_32 = "A varint of the footer is longer than 32 bits."
LACKS = "The footer lacks a required field: version, schema, num_rows, or row_groups."
NO_NAME = "A schema element of the footer has no name."
GROUP_LACKS = "A row group of the footer lacks its column chunks, its number of rows, or its size."
META_LACKS = "The metadata of a column chunk lacks num_values, a size, or data_page_offset."
CHUNKS = "A row group has {} column chunks for {} leaf columns."
SCHEMA_TOO_DEEP = "The schema of the footer is nested too deep."
# P1: the one message of a nesting deeper than 64 levels, for every kind of container.
TOO_DEEP = "The footer nests values more than 64 levels deep."
# R5: the limits of the lists, the maps, and the column chunks of a footer.
LIST_LIMIT = "A list of the footer has {} elements, more than the limit of 1000000."
MAP_LIMIT = "A map of the footer has {} entries, more than the limit of 1000000."
CHUNK_LIMIT = "The row groups of the footer hold more column chunks than the limit of 4194304."
TOO_LARGE_CODE = IssueCodes.FOOTER_TOO_LARGE

MIB = 1024 * 1024

# Enumerations of parquet.thrift.
INT32, INT64, DOUBLE, BYTE_ARRAY, FIXED = 1, 2, 5, 6, 7
REQUIRED, OPTIONAL, REPEATED = 0, 1, 2
PLAIN, PLAIN_DICTIONARY, RLE, RLE_DICTIONARY = 0, 2, 3, 8
UNCOMPRESSED, SNAPPY, ZSTD = 0, 1, 6
DATA_PAGE, DICTIONARY_PAGE, DATA_PAGE_V2 = 0, 2, 3


def le32(value: int) -> bytes:
    return struct.pack("<i", value)


def le64(value: int) -> bytes:
    return struct.pack("<q", value)


def be(value: int, length: int) -> bytes:
    """A two's complement integer in big endian, as a decimal of a byte array holds its unscaled value."""
    return value.to_bytes(length, "big", signed=True)


# ---- what a test compares ----

def summary(parsed) -> Dict[str, Any]:
    """Every value of a parsed footer, to compare two parses that must give the same footer."""
    def chunk(c) -> Tuple:
        stats = None if c.encoding_stats is None else tuple((s.page_type, s.encoding, s.count) for s in c.encoding_stats)
        return (c.has_metadata, c.is_encrypted, c.file_path, c.physical_type, c.codec, tuple(c.encodings), c.num_values,
                c.total_uncompressed_size, c.total_compressed_size, c.data_page_offset, c.index_page_offset,
                c.dictionary_page_offset, c.has_statistics, c.null_count, c.distinct_count, c.has_min_max,
                c.min_value, c.max_value, stats, c.bloom_filter_offset, c.bloom_filter_length,
                c.unencoded_byte_array_data_bytes, c.offset_index_offset, c.offset_index_length,
                c.column_index_offset, c.column_index_length)

    return {
        "version": parsed.version, "num_rows": parsed.num_rows, "created_by": parsed.created_by,
        "encryption": parsed.has_encryption_algorithm,
        "keys": [(pair.key, pair.value_length, pair.value) for pair in parsed.key_value_metadata],
        "schema": [(e.name, e.type, e.type_length, e.repetition, e.num_children, e.converted_type, e.scale,
                    e.precision, e.field_id, e.logical_type) for e in parsed.schema],
        "leaves": [(leaf.ordinal, tuple(leaf.path), leaf.physical_type, leaf.type_length, leaf.repetition,
                    leaf.logical_type, leaf.converted_type, leaf.scale, leaf.precision, leaf.field_id,
                    leaf.top_level_field_id, leaf.type_annotation) for leaf in parsed.leaves],
        "groups": [(group.num_rows, group.total_byte_size, group.total_compressed_size, group.file_offset,
                    group.ordinal,
                    None if group.sorting_columns is None else tuple(
                        (s.column_index, s.descending, s.nulls_first) for s in group.sorting_columns),
                    [chunk(c) for c in group.columns]) for group in parsed.row_groups],
    }


def refusal(call: Callable[[], Any]) -> FooterError:
    """The footer error that a call raises; any other exception fails the test, as R1 says."""
    try:
        call()
    except FooterError as error:
        return error
    except Exception as error:  # noqa: BLE001 - R1: nothing but the footer exception escapes
        pytest.fail(f"{type(error).__name__} instead of a footer error: {error}")
    pytest.fail("No footer error.")


def assert_refused(data: bytes, message: str, code: str = INVALID, include_data_values: bool = False) -> None:
    error = refusal(lambda: parse_footer(data, include_data_values))
    assert (error.code, str(error)) == (code, message)


def only_footer_errors(call: Callable[[], Any], where: str) -> Optional[FooterError]:
    """Runs a call that may succeed or raise the footer error, with one of its three codes and a message."""
    try:
        call()
    except FooterError as error:
        assert error.code in CODES, f"{where}: code {error.code}"
        assert str(error), f"{where}: no message"
        return error
    except Exception as error:  # noqa: BLE001 - R1
        pytest.fail(f"{where}: {type(error).__name__}: {error}")
    return None


def read_refusal(storage: Any, size: int, tail_bytes: int = 32768, include_data_values: bool = False) -> FooterError:
    return refusal(lambda: FooterReader(storage, tail_bytes, include_data_values).read("file.parquet", size))


BASELINE = footer()


# ---- the footers of the tests ----

# The elements of the schema of the rich footer: name, type, repetition, children, converted type, scale, precision,
# field id, type length, logical type.
RICH_SCHEMA = [
    ("schema", None, None, 8, None, None, None, None, None, None),
    ("id", INT64, REQUIRED, None, 18, None, None, 1, None, "int64"),
    ("name", BYTE_ARRAY, OPTIONAL, None, 0, None, None, 2, None, "string"),
    ("price", FIXED, OPTIONAL, None, 5, 4, 38, 3, 16, "decimal"),
    ("at", INT64, OPTIONAL, None, 10, None, None, 4, None, "timestamp"),
    ("tags", None, OPTIONAL, 1, 3, None, None, 5, None, "list"),
    ("list", None, REPEATED, 1, None, None, None, None, None, None),
    ("element", INT32, OPTIONAL, None, None, None, None, 6, None, None),
    ("props", None, OPTIONAL, 1, 1, None, None, 7, None, "map"),
    ("key_value", None, REPEATED, 2, None, None, None, None, None, None),
    ("key", BYTE_ARRAY, REQUIRED, None, 0, None, None, None, None, "string"),
    ("value", DOUBLE, OPTIONAL, None, None, None, None, None, None, None),
    ("day", INT32, OPTIONAL, None, 6, None, None, 8, None, "date"),
    ("blob", BYTE_ARRAY, OPTIONAL, None, None, None, None, 9, None, None),
]
# The leaves of the rich footer: type, path, lower bound, upper bound.
RICH_LEAVES = [
    (INT64, ("id",), le64(1), le64(500)),
    (BYTE_ARRAY, ("name",), b"alpha", b"omega"),
    (FIXED, ("price",), be(-12345, 16), be(987654321, 16)),
    (INT64, ("at",), le64(1_700_000_000_000_000), le64(1_800_000_000_000_000)),
    (INT32, ("tags", "list", "element"), le32(-3), le32(42)),
    (BYTE_ARRAY, ("props", "key_value", "key"), b"a", b"z"),
    (DOUBLE, ("props", "key_value", "value"), struct.pack("<d", -1.5), struct.pack("<d", 2.25)),
    (INT32, ("day",), le32(19000), le32(20000)),
    (BYTE_ARRAY, ("blob",), b"\x00\xff", b"\xff\xfe"),
]
RICH_KEYS = [("com.microsoft.parquet.vorder.enabled", "true"), ("com.microsoft.parquet.vorder.level", "default"),
             ("org.apache.spark.version", "3.5.1"), ("org.apache.spark.sql.parquet.row.metadata", '{"type":"struct"}'),
             ("no.value", None)]


def write_logical(w: Thrift, kind: str) -> None:
    w.begin(10)
    if kind == "int64":
        w.begin(10).byte(1, 64).boolean(2, True).end()
    elif kind == "string":
        w.begin(1).end()
    elif kind == "decimal":
        w.begin(5).i32(1, 4).i32(2, 38).end()
    elif kind == "timestamp":
        w.begin(8).boolean(1, True).begin(2).begin(2).end().end().end()
    elif kind == "list":
        w.begin(3).end()
    elif kind == "map":
        w.begin(2).end()
    elif kind == "date":
        w.begin(6).end()
    else:
        raise ValueError(kind)
    w.end()


def rich_footer() -> bytes:
    """A footer with most of what parquet.thrift holds: logical and converted types, field ids, a list and a map, two
    row groups whose chunks have statistics with bounds, encoding statistics, size statistics, bloom filters, and
    indexes, sorting columns, key-value metadata, column orders, and unknown fields of every type."""
    w = Thrift()
    w.i32(1, 2)
    w.list_of(2, Thrift.STRUCT, len(RICH_SCHEMA))
    for name, physical, repetition, children, converted, scale, precision, field_id, type_length, logical in RICH_SCHEMA:
        w.element()
        if physical is not None:
            w.i32(1, physical)
        if type_length is not None:
            w.i32(2, type_length)
        if repetition is not None:
            w.i32(3, repetition)
        w.binary(4, name)
        if children is not None:
            w.i32(5, children)
        if converted is not None:
            w.i32(6, converted)
        if scale is not None:
            w.i32(7, scale)
        if precision is not None:
            w.i32(8, precision)
        if field_id is not None:
            w.i32(9, field_id)
        if logical is not None:
            write_logical(w, logical)
        w.end()
    w.i64(3, 1000)
    w.list_of(4, Thrift.STRUCT, 2)
    for g in range(2):
        w.element()
        w.list_of(1, Thrift.STRUCT, len(RICH_LEAVES))
        uncompressed = compressed = 0
        for i, (physical, path, minimum, maximum) in enumerate(RICH_LEAVES):
            base = 4 + 4000 * g + 400 * i
            w.element()
            w.i64(2, base + 50)
            w.begin(3)
            w.i32(1, physical)
            w.list_of(2, Thrift.I32, 3).value_i32(PLAIN).value_i32(RLE).value_i32(RLE_DICTIONARY)
            w.list_of(3, Thrift.BINARY, len(path))
            for name in path:
                w.value_binary(name)
            w.i32(4, ZSTD if (i + g) % 2 == 0 else SNAPPY)
            w.i64(5, 500 + i).i64(6, 2000 + 100 * i + g).i64(7, 1000 + 100 * i + g)
            w.list_of(8, Thrift.STRUCT, 1).element().binary(1, "chunk.key").binary(2, "chunk.value").end()
            w.i64(9, base + 50).i64(11, base)
            w.begin(12)
            w.binary(1, maximum).binary(2, minimum).i64(3, i).i64(4, 10 + i).binary(5, maximum).binary(6, minimum)
            w.boolean(7, True).boolean(8, True)
            w.end()
            w.list_of(13, Thrift.STRUCT, 3)
            w.element().i32(1, DICTIONARY_PAGE).i32(2, PLAIN).i32(3, 1).end()
            w.element().i32(1, DATA_PAGE).i32(2, RLE_DICTIONARY).i32(3, 2).end()
            w.element().i32(1, DATA_PAGE_V2).i32(2, PLAIN).i32(3, 1 if i % 3 == 0 else 0).end()
            w.i64(14, 90000 + i).i32(15, 64 + i)
            w.begin(16)
            if physical == BYTE_ARRAY:
                w.i64(1, 300 + i)
            w.list_of(2, Thrift.I64, 2).varint(w.zigzag(1)).varint(w.zigzag(2))
            w.list_of(3, Thrift.I64, 2).varint(w.zigzag(3)).varint(w.zigzag(4))
            w.end()
            w.end()
            w.i64(4, 80000 + 100 * i + g).i32(5, 20 + i).i64(6, 70000 + 100 * i + g).i32(7, 30 + i)
            w.end()
            uncompressed += 2000 + 100 * i + g
            compressed += 1000 + 100 * i + g
        w.i64(2, uncompressed).i64(3, 500)
        w.list_of(4, Thrift.STRUCT, 2)
        w.element().i32(1, 0).boolean(2, False).boolean(3, True).end()
        w.element().i32(1, 3).boolean(2, True).boolean(3, False).end()
        w.i64(5, 4 + 4000 * g).i64(6, compressed).i16(7, g)
        w.end()
    w.list_of(5, Thrift.STRUCT, len(RICH_KEYS))
    for key, value in RICH_KEYS:
        w.element().binary(1, key)
        if value is not None:
            w.binary(2, value)
        w.end()
    w.binary(6, "parquet-mr version 1.13.1 (build synthetic)")
    w.list_of(7, Thrift.STRUCT, len(RICH_LEAVES))
    for _ in RICH_LEAVES:
        w.element().begin(1).end().end()
    every_type(w, 100)
    w.end()
    return w.bytes()


RICH = rich_footer()


def nested_chain(w: Thrift, field: int, depth: int, kinds: Sequence[int]) -> None:
    """An unknown field that holds containers nested depth levels deep, of the kinds in turn, with one i32 at the
    bottom: a list or a set of one element, a map of one entry whose value is the next level, a structure whose field 1
    is the next level. Written without recursion, so that any depth can be built."""
    chain = [kinds[i % len(kinds)] for i in range(depth)]
    w.header(field, chain[0])
    for i, kind in enumerate(chain):
        inner = chain[i + 1] if i + 1 < depth else Thrift.I32
        if kind in (Thrift.LIST, Thrift.SET):
            w.collection_header(inner, 1)
        elif kind == Thrift.MAP:
            w.varint(1).raw(bytes([(Thrift.I32 << 4) | inner])).value_i32(0)
        else:
            w.element().header(1, inner)
    w.value_i32(1)
    for kind in reversed(chain):
        if kind == Thrift.STRUCT:
            w.end()


CONTAINERS = {
    "structures": (Thrift.STRUCT,),
    "lists": (Thrift.LIST,),
    "sets": (Thrift.SET,),
    "maps": (Thrift.MAP,),
    "lists, structures, maps, and sets in turn": (Thrift.LIST, Thrift.STRUCT, Thrift.MAP, Thrift.SET),
}


def chain_schema(levels: int):
    """A top-level field with groups nested inside it, levels in all below the root, the leaf bottom included."""
    field = leaf("bottom", "INT32")
    for level in reversed(range(levels - 1)):
        field = group(f"g{level}", field)
    return field


def chain_type(levels: int) -> str:
    """The type of the column of chain_schema(levels), as Spark reads it."""
    text, child = "integer", "bottom"
    for level in reversed(range(levels - 1)):
        text = f"struct<{child}:{text}>"
        child = f"g{level}"
    return text


def list_chain(lists: int, name: str = "l0"):
    """Lists of the three levels of the specification nested in one another, 2 * lists + 1 levels with the leaf."""
    element = leaf("element", "INT32")
    for level in reversed(range(lists)):
        element = group(name if level == 0 else "element", group("list", element, repetition="REPEATED"),
                        logical="LIST")
    return element


def bound_footer(physical: int, minimum: bytes, maximum: bytes, *, type_length: Optional[int] = None,
                 converted: Optional[int] = None, scale: Optional[int] = None, precision: Optional[int] = None,
                 logical: Optional[Tuple[int, int]] = None) -> bytes:
    """A footer with one leaf d of a physical type and its annotation, and one column chunk whose statistics have these
    bounds. logical is a DECIMAL logical type, as (precision, scale)."""
    w = Thrift()
    w.i32(1, 1)
    w.list_of(2, Thrift.STRUCT, 2)
    w.element().binary(4, "schema").i32(5, 1).end()
    w.element().i32(1, physical)
    if type_length is not None:
        w.i32(2, type_length)
    w.i32(3, OPTIONAL).binary(4, "d")
    if converted is not None:
        w.i32(6, converted)
    if scale is not None:
        w.i32(7, scale)
    if precision is not None:
        w.i32(8, precision)
    if logical is not None:
        w.begin(10).begin(5).i32(1, logical[1]).i32(2, logical[0]).end().end()
    w.end()
    w.i64(3, 1)
    w.list_of(4, Thrift.STRUCT, 1).element()
    w.list_of(1, Thrift.STRUCT, 1).element()
    w.i64(2, 4)
    w.begin(3).i32(1, physical)
    w.list_of(2, Thrift.I32, 1).value_i32(PLAIN)
    w.list_of(3, Thrift.BINARY, 1).value_binary("d")
    w.i32(4, UNCOMPRESSED).i64(5, 1).i64(6, 100).i64(7, 50).i64(9, 4)
    w.begin(12).i64(3, 0).binary(5, maximum).binary(6, minimum).end()
    w.end()
    w.end()
    w.i64(2, 100).i64(3, 1)
    w.end()
    w.end()
    return w.bytes()


def second_meta_data(w: Thrift) -> None:
    """A second meta_data of a column chunk, with values of its own and no dictionary page, statistics, encoding
    statistics, size statistics, or bloom filter."""
    w.begin(3)
    w.i32(1, INT64)
    w.list_of(2, Thrift.I32, 1).value_i32(PLAIN)
    w.list_of(3, Thrift.BINARY, 1).value_binary("id")
    w.i32(4, SNAPPY).i64(5, 11).i64(6, 22).i64(7, 33).i64(9, 44)
    w.end()


def columns_list(count: int, first_values: int = 0) -> Callable[[Thrift], None]:
    """A hook that writes a columns list (field 1) of a row group with count chunks, whose values start at
    first_values, so that a test tells them from those of footer()."""
    def write(w: Thrift) -> None:
        w.list_of(1, Thrift.STRUCT, count)
        for index in range(count):
            w.element()
            w.i64(2, 4)
            w.begin(3)
            w.i32(1, INT64)
            w.list_of(2, Thrift.I32, 1).value_i32(PLAIN)
            w.i32(4, SNAPPY).i64(5, first_values + index).i64(6, 2000 + index).i64(7, 1000 + index)
            w.i64(9, 4)
            w.end()
            w.end()
    return write


# ---- R2: the bytes of the two suites ----

def test_the_footers_of_the_tests_have_the_same_bytes_as_in_the_csharp_suite():
    assert hashlib.sha256(BASELINE).hexdigest() == BASELINE_SHA256
    assert hashlib.sha256(RICH).hexdigest() == RICH_SHA256


BASELINE_SHA256 = "475e962d4f73ab3a2d2ccecc6bb146227473f18c62909f787b14630b7ab49e99"
RICH_SHA256 = "fe940985faa9087b3746fabddda49d73695dfb66128f7a62be9670ea84ac540c"


def test_the_rich_footer_is_a_valid_footer_with_every_value():
    parsed = parse_footer(RICH, include_data_values=True)
    assert (parsed.version, parsed.num_rows, parsed.created_by) == (2, 1000, "parquet-mr version 1.13.1 (build synthetic)")
    assert [tuple(leaf.path) for leaf in parsed.leaves] == [path for _, path, _, _ in RICH_LEAVES]
    assert [leaf.field_id for leaf in parsed.leaves] == [1, 2, 3, 4, 6, None, None, 8, 9]
    assert [leaf.top_level_field_id for leaf in parsed.leaves] == [1, 2, 3, 4, 5, 7, 7, 8, 9]
    assert len(parsed.row_groups) == 2
    chunk = parsed.row_groups[1].columns[2]
    assert (chunk.min_value, chunk.max_value) == ("-1.2345", "98765.4321")
    assert chunk.unencoded_byte_array_data_bytes is None
    assert parsed.row_groups[1].columns[1].unencoded_byte_array_data_bytes == 301
    assert [pair.value for pair in parsed.key_value_metadata] == ["true", "default", "3.5.1", None, None]


# ---- R1: nothing but the footer exception ----

def test_every_truncation_of_a_footer_ends_in_the_middle_of_a_value():
    for data in (BASELINE, RICH):
        for length in range(len(data)):
            error = refusal(lambda: parse_footer(data[:length]))
            assert (error.code, str(error)) == (INVALID, TRUNCATED), f"{length} bytes of {len(data)}"


def test_every_truncation_of_a_file_is_refused_with_the_footer_error_only():
    data = parquet_ending_with(RICH, len(RICH) + 100)
    for cut in range(1, len(RICH) + 9):
        shorter = data[:len(data) - cut]
        # Stated as the size of the whole file: the tail read is short.
        error = read_refusal(VirtualFileStorage(len(shorter), shorter), len(data))
        assert (error.code, str(error)) == (INVALID, SHORTER), f"{cut} bytes cut"
        # Stated as the size of what is left: the end is no longer the end of a footer.
        only_footer_errors(lambda: FooterReader(VirtualFileStorage(len(shorter), shorter)).read("file.parquet",
                                                                                                 len(shorter)),
                           f"{cut} bytes cut, stated as the size left")


class FailingStorage:
    """A storage whose reads fail with an error."""

    def __init__(self, error: BaseException) -> None:
        self.error = error

    def list(self, path: str) -> List[Any]:
        return []

    def read_range(self, path: str, offset: int, length: int) -> bytes:
        raise self.error


def test_an_error_of_the_storage_propagates_as_it_is():
    for error in (StorageError("The storage answered 500.", 500), StorageError("A request timed out.")):
        with pytest.raises(StorageError) as caught:
            FooterReader(FailingStorage(error)).read("file.parquet", 1000)
        assert caught.value is error


def hook(where: str, write: Callable[[Thrift], Any]) -> bytes:
    return footer(extra={where: write})


def known(name: str, write: Callable[[Thrift], Any]) -> bytes:
    return footer(replace={name: write})


# (where, footer, message): list headers that claim more elements than the bytes left can hold.
HUGE_COUNTS = [
    ("the schema", known("file.schema", lambda w: w.list_of(2, Thrift.STRUCT, 2 ** 31 - 1)), TRUNCATED),
    ("the row groups", known("file.row_groups", lambda w: w.list_of(4, Thrift.STRUCT, 2 ** 31 - 1)), TRUNCATED),
    ("the key-value metadata", known("file.key_value_metadata", lambda w: w.list_of(5, Thrift.STRUCT, 2 ** 31 - 1)),
     TRUNCATED),
    ("the columns of a row group", known("row_group.columns", lambda w: w.list_of(1, Thrift.STRUCT, 2 ** 31 - 1)),
     TRUNCATED),
    ("the encodings of a chunk", hook("column", lambda w: w.list_of(2, Thrift.I32, 2 ** 31 - 1)), TRUNCATED),
    ("the path of a chunk", hook("column", lambda w: w.list_of(3, Thrift.BINARY, 2 ** 31 - 1)), TRUNCATED),
    ("the encoding statistics", hook("column", lambda w: w.list_of(13, Thrift.STRUCT, 2 ** 31 - 1)), TRUNCATED),
    ("the sorting columns", hook("row_group", lambda w: w.list_of(4, Thrift.STRUCT, 2 ** 31 - 1)), TRUNCATED),
    ("the column orders", hook("file", lambda w: w.list_of(7, Thrift.STRUCT, 2 ** 31 - 1)), TRUNCATED),
    ("an unknown list of structures", hook("file", lambda w: w.list_of(300, Thrift.STRUCT, 2 ** 31 - 1)), TRUNCATED),
    ("an unknown list of booleans", hook("statistics", lambda w: w.list_of(300, Thrift.TRUE, 2 ** 31 - 1)),
     TRUNCATED),
    ("an unknown set of binaries", hook("leaf", lambda w: w.list_of(300, Thrift.BINARY, 2 ** 31 - 1, Thrift.SET)),
     TRUNCATED),
    ("an unknown map", hook("chunk", lambda w: w.map_of(300, Thrift.BINARY, Thrift.STRUCT, 2 ** 32 - 1)), TRUNCATED),
    ("an unknown binary", hook("root", lambda w: w.header(300, Thrift.BINARY).varint(2 ** 32 - 1)), TRUNCATED),
    # Today a count over 2^31 - 1 is "too long" before the bytes left are counted; R5 puts the check of the bytes left
    # first, and does not say which of the two messages such a count keeps, so the test takes either.
    ("a list longer than 2^31 - 1", hook("file", lambda w: w.list_of(300, Thrift.I64, 2 ** 32 - 1)),
     (LIST_TOO_LONG, TRUNCATED)),
    ("a count of more than 32 bits", hook("file", lambda w: w.list_of(300, Thrift.I64, 2 ** 40)), VARINT_32),
]


@pytest.mark.parametrize("data, message", [(data, message) for _, data, message in HUGE_COUNTS],
                         ids=[where for where, _, _ in HUGE_COUNTS])
def test_a_list_that_claims_more_elements_than_the_bytes_left_is_invalid_and_allocates_nothing_for_them(data, message):
    tracemalloc.start()
    try:
        error = refusal(lambda: parse_footer(data))
        peak = tracemalloc.get_traced_memory()[1]
    finally:
        tracemalloc.stop()
    assert error.code == INVALID
    assert str(error) in (message if isinstance(message, tuple) else (message,))
    assert peak < MIB, f"{peak} bytes for a footer of {len(data)}"


# ---- R5: the limits of the lists, the maps, and the column chunks ----

def test_the_limits_of_a_footer_are_a_million_elements_and_4194304_column_chunks():
    import pqax.collect.footer as module
    assert getattr(module, "MAX_LIST_LENGTH", None) == 1_000_000
    assert getattr(module, "MAX_COLUMN_CHUNKS", None) == 4_194_304


def claim(field: int, element: int, count: int, filler: int, kind: int = Thrift.LIST) -> Callable[[Thrift], None]:
    """A hook that writes the header of a list (or a set) that claims count elements, and filler bytes after it, so
    that the elements fit the bytes left when filler >= count."""
    return lambda w: w.list_of(field, element, count, kind).raw(bytes([1]) * filler)


def claim_map(field: int, count: int, filler: int) -> Callable[[Thrift], None]:
    return lambda w: w.map_of(field, Thrift.TRUE, Thrift.TRUE, count).raw(bytes([1]) * filler)


# (where, footer, message): a list or a map past the limit, whose elements would fit the bytes left.
PAST_THE_LIMIT = [
    ("an unknown list", lambda: hook("file", claim(300, Thrift.TRUE, 1_000_001, 1_000_001)), LIST_LIMIT.format(1_000_001)),
    ("an unknown set", lambda: hook("file", claim(300, Thrift.TRUE, 1_000_001, 1_000_001, Thrift.SET)),
     LIST_LIMIT.format(1_000_001)),
    ("an unknown list of two million", lambda: hook("leaf", claim(300, Thrift.STRUCT, 2_000_000, 2_000_000)),
     LIST_LIMIT.format(2_000_000)),
    ("an unknown map", lambda: hook("file", claim_map(300, 1_000_001, 1_000_001)), MAP_LIMIT.format(1_000_001)),
    ("the schema", lambda: known("file.schema", claim(2, Thrift.STRUCT, 1_000_001, 1_000_001)),
     LIST_LIMIT.format(1_000_001)),
    ("the row groups", lambda: known("file.row_groups", claim(4, Thrift.STRUCT, 1_000_001, 1_000_001)),
     LIST_LIMIT.format(1_000_001)),
    ("the key-value metadata", lambda: known("file.key_value_metadata", claim(5, Thrift.STRUCT, 1_000_001, 1_000_001)),
     LIST_LIMIT.format(1_000_001)),
    ("the columns of a row group", lambda: known("row_group.columns", claim(1, Thrift.STRUCT, 1_000_001, 1_000_001)),
     LIST_LIMIT.format(1_000_001)),
    ("the encodings of a chunk", lambda: hook("column", claim(2, Thrift.I32, 1_000_001, 1_000_001)),
     LIST_LIMIT.format(1_000_001)),
]


@pytest.mark.parametrize("build, message", [case[1:] for case in PAST_THE_LIMIT], ids=[case[0] for case in PAST_THE_LIMIT])
def test_a_list_or_a_map_of_more_than_a_million_elements_is_too_large(build, message):
    assert_refused(build(), message, TOO_LARGE_CODE)


@pytest.mark.parametrize("build", [lambda: hook("file", claim(300, Thrift.TRUE, 1_000_001, 1_000_001 - 2)),
                                   lambda: hook("file", claim_map(300, 1_000_001, 1_000_001 - 3)),
                                   lambda: known("file.schema", claim(2, Thrift.STRUCT, 2_000_000, 1_000_000))],
                         ids=["a list", "a map", "the schema"])
def test_a_list_past_the_limit_whose_elements_do_not_fit_the_bytes_left_stays_invalid(build):
    assert_refused(build(), TRUNCATED)


@pytest.mark.parametrize("build", [lambda: hook("file", claim(300, Thrift.TRUE, 1_000_000, 1_000_000)),
                                   lambda: hook("file", claim_map(300, 1_000_000, 2_000_000))],
                         ids=["a list of a million booleans", "a map of a million entries"])
def test_a_list_or_a_map_of_a_million_elements_is_read(build):
    assert summary(parse_footer(build())) == summary(parse_footer(BASELINE))


@pytest.mark.large
def test_more_than_4194304_column_chunks_in_all_the_lists_read_are_too_large():
    # The columns list of footer() has one chunk; then lists of empty chunks, each replacing the one before (P3), all
    # of them counting.
    def lists(w: Thrift) -> None:
        for _ in range(83):
            w.list_of(1, Thrift.STRUCT, 50_000).raw(bytes(50_000))
        w.list_of(1, Thrift.STRUCT, 44_304).raw(bytes(44_304))
    assert_refused(footer(extra={"row_group": lists}), CHUNK_LIMIT, TOO_LARGE_CODE)


# ---- R6: a field header of type 0 ----

MARKER = b"MARKER-BEFORE-THE-STOP"


def marked(where: str) -> bytes:
    """A footer with an unknown binary field at the end of a structure, so that a test finds the stop byte after it."""
    return footer(extra={where: lambda w: w.binary(200, MARKER)})


def with_stop(data: bytes, at: int, header: int) -> bytes:
    assert data[at] == 0
    return data[:at] + bytes([header]) + data[at + 1:]


@pytest.mark.parametrize("header", range(0x10, 0x100, 0x10), ids=lambda header: f"0x{header:02X}")
def test_a_field_header_of_type_0_ends_the_footer(header):
    assert summary(parse_footer(with_stop(BASELINE, len(BASELINE) - 1, header))) == summary(parse_footer(BASELINE))


@pytest.mark.parametrize("where", ["statistics", "column", "chunk", "row_group", "leaf", "root"])
@pytest.mark.parametrize("header", [0x10, 0x70, 0xF0], ids=lambda header: f"0x{header:02X}")
def test_a_field_header_of_type_0_ends_the_structure_that_it_is_in(where, header):
    data = marked(where)
    at = data.index(MARKER) + len(MARKER)
    assert summary(parse_footer(with_stop(data, at, header))) == summary(parse_footer(data))


def test_a_field_header_of_type_0_ends_the_footer_before_the_fields_after_it():
    data = footer(replace={"file.created_by": lambda w: w.raw(b"\x10").binary(6, "after the end")})
    parsed = parse_footer(data)
    assert parsed.created_by is None
    expected = summary(parse_footer(footer(created_by=None)))
    assert summary(parsed) == expected


MUTATIONS = ("set", "flip", "truncate", "insert", "duplicate", "huge")


def mutate(generator: random.Random, data: bytes) -> bytes:
    kind = MUTATIONS[generator.randrange(len(MUTATIONS))]
    out = bytearray(data)
    if kind == "set":
        for _ in range(1 + generator.randrange(4)):
            out[generator.randrange(len(out))] = generator.randrange(256)
    elif kind == "flip":
        for _ in range(1 + generator.randrange(3)):
            out[generator.randrange(len(out))] ^= 1 << generator.randrange(8)
    elif kind == "truncate":
        del out[generator.randrange(len(out)):]
    elif kind == "insert":
        at = generator.randrange(len(out) + 1)
        out[at:at] = bytes(generator.randrange(256) for _ in range(1 + generator.randrange(6)))
    elif kind == "duplicate":
        start = generator.randrange(len(out))
        stop = min(len(out), start + 1 + generator.randrange(40))
        at = generator.randrange(len(out) + 1)
        out[at:at] = out[start:stop]
    else:
        # A header of a list of a random type that claims a huge count.
        at = generator.randrange(len(out))
        out[at:at + 1] = bytes([0xF0 | generator.randrange(16), 0xFF, 0xFF, 0xFF, 0xFF, 0x07])
    return bytes(out)


def test_changed_bytes_in_footers_and_files_raise_only_the_footer_error_and_end_within_a_time_limit():
    generator = random.Random(20261028)
    sources = [RICH, BASELINE, footer(leaves=("a", "b", "c"), row_group_count=3, key_values=[("vorder", "true")]),
               schema_footer([list_chain(3), chain_schema(10)]), bound_footer(FIXED, be(-1, 16), be(1, 16),
                                                                               type_length=16, logical=(38, 4))]
    sources += [metadata_bytes(file_bytes(path)) for path in data_files()[:2]]
    started = time.monotonic()
    parsed = refused = 0
    slowest = 0.0
    for round_ in range(8000):
        source = sources[round_ % len(sources)]
        changed = mutate(generator, source)
        include = round_ % 2 == 0
        if round_ % 4 == 3:
            # Through the reader, with the end of the file changed too and a stated size that may be wrong.
            data = bytearray(parquet_ending_with(changed, len(changed) + 20))
            if generator.randrange(3) == 0:
                data[len(data) - 1 - generator.randrange(8)] = generator.randrange(256)
            stated = len(data) + (0, 0, -1, 1, -9, 100)[generator.randrange(6)]
            storage = VirtualFileStorage(len(data), bytes(data))
            call = (lambda: FooterReader(storage, 1 + 11 + generator.randrange(64), include)
                    .read("file.parquet", stated))
        else:
            call = (lambda: parse_footer(changed, include))
        one = time.monotonic()
        error = only_footer_errors(call, f"round {round_}")
        slowest = max(slowest, time.monotonic() - one)
        if error is None:
            parsed += 1
        else:
            refused += 1
    elapsed = time.monotonic() - started
    assert parsed > 0 and refused > 0
    assert slowest < 2, f"one footer took {slowest:.2f} s"
    assert elapsed < 30, f"the loop took {elapsed:.1f} s"


# ---- R3: the checks of a footer read, in their order ----

SIZE = 1000
LARGE = 100 * MIB


def virtual(size: int, end: bytes) -> Callable[[], VirtualFileStorage]:
    return lambda: VirtualFileStorage(size, end)


# (case, storage, stated size, code, message, reads)
CHECKS = [
    ("a stated size of 11", virtual(11, tail(1)), 11, INVALID, TOO_SMALL.format(11), 0),
    ("a stated size of 0", virtual(0, b""), 0, INVALID, TOO_SMALL.format(0), 0),
    ("a negative stated size", virtual(0, b""), -1, INVALID, TOO_SMALL.format(-1), 0),
    ("a tail read shorter than asked", virtual(SIZE, tail(100)), 2 * SIZE, INVALID, SHORTER, 1),
    ("a short tail read before a wrong magic", virtual(SIZE, tail(100, b"XXXX")), 2 * SIZE, INVALID, SHORTER, 1),
    ("PARE", virtual(SIZE, tail(100, b"PARE")), SIZE, IssueCodes.FOOTER_ENCRYPTED, ENCRYPTED, 1),
    ("PARE before a length of 0", virtual(SIZE, tail(0, b"PARE")), SIZE, IssueCodes.FOOTER_ENCRYPTED, ENCRYPTED, 1),
    ("PARE before a length over 64 MiB", virtual(LARGE, tail(MAX_FOOTER_LENGTH + 1, b"PARE")), LARGE,
     IssueCodes.FOOTER_ENCRYPTED, ENCRYPTED, 1),
    ("PAR2", virtual(SIZE, tail(100, b"PAR2")), SIZE, INVALID, NO_MAGIC, 1),
    ("XXXX", virtual(SIZE, tail(100, b"XXXX")), SIZE, INVALID, NO_MAGIC, 1),
    ("par1", virtual(SIZE, tail(100, b"par1")), SIZE, INVALID, NO_MAGIC, 1),
    ("PARe", virtual(SIZE, tail(100, b"PARe")), SIZE, INVALID, NO_MAGIC, 1),
    ("1RAP", virtual(SIZE, tail(100, b"1RAP")), SIZE, INVALID, NO_MAGIC, 1),
    ("a wrong magic before a length of 0", virtual(SIZE, tail(0, b"PAR0")), SIZE, INVALID, NO_MAGIC, 1),
    ("a length of 0", virtual(SIZE, tail(0)), SIZE, INVALID, NOT_FITTING.format(0), 1),
    ("a length of -1", virtual(SIZE, tail(-1)), SIZE, INVALID, NOT_FITTING.format(-1), 1),
    ("the smallest length", virtual(SIZE, tail(-2 ** 31)), SIZE, INVALID, NOT_FITTING.format(-2 ** 31), 1),
    ("a length of the size minus 11", virtual(SIZE, tail(SIZE - 11)), SIZE, INVALID, NOT_FITTING.format(SIZE - 11), 1),
    ("a length over 64 MiB that does not fit", virtual(SIZE, tail(MAX_FOOTER_LENGTH + 1)), SIZE, INVALID,
     NOT_FITTING.format(MAX_FOOTER_LENGTH + 1), 1),
    ("a length of the size minus 12, whose zeros are parsed", virtual(SIZE, tail(SIZE - 12)), SIZE, INVALID, LACKS, 1),
    ("a length over 64 MiB", virtual(LARGE, tail(MAX_FOOTER_LENGTH + 1)), LARGE, IssueCodes.FOOTER_TOO_LARGE,
     TOO_LARGE.format(MAX_FOOTER_LENGTH + 1), 1),
    ("a length of 64 MiB, read and parsed", virtual(LARGE, tail(MAX_FOOTER_LENGTH)), LARGE, INVALID, LACKS, 2),
]


@pytest.mark.parametrize("storage, stated, code, message, reads", [case[1:] for case in CHECKS],
                         ids=[case[0] for case in CHECKS])
def test_the_checks_of_a_footer_read_come_in_their_order_with_their_messages(storage, stated, code, message, reads):
    served = storage()
    error = read_refusal(served, stated)
    assert (error.code, str(error)) == (code, message)
    assert len(served.reads) == reads


class ShortSecondRead:
    """A storage that serves a file and cuts its second read in half."""

    def __init__(self, data: bytes) -> None:
        self.data = data
        self.reads: List[Tuple[int, int]] = []

    def list(self, path: str) -> List[Any]:
        return []

    def read_range(self, path: str, offset: int, length: int) -> bytes:
        self.reads.append((offset, length))
        data = self.data[offset:offset + length]
        return data if len(self.reads) == 1 else data[:len(data) // 2]


def test_a_second_read_that_is_short_is_invalid():
    data = parquet_file(RICH)
    storage = ShortSecondRead(data)
    error = read_refusal(storage, len(data), tail_bytes=12)
    assert (error.code, str(error)) == (INVALID, SHORTER)
    assert len(storage.reads) == 2


@pytest.mark.parametrize("data", [footer(omit=["file.num_rows"]), footer(leaves=("a", "b"), chunks=1),
                                  footer(extra={"file": lambda w: nested_structures(w, 300, 100)}),
                                  bytes([0x19, 0xFC]) + RICH[2:]],
                         ids=["a missing field", "chunks that do not match", "too deep", "a broken list"])
@pytest.mark.parametrize("tail_bytes", [12, 32768])
def test_after_the_checks_the_reader_gives_the_outcome_of_the_parse_of_the_metadata(data, tail_bytes):
    expected = refusal(lambda: parse_footer(data))
    file = parquet_file(data)
    error = read_refusal(VirtualFileStorage(len(file), file), len(file), tail_bytes)
    assert (error.code, str(error)) == (expected.code, str(expected))


@pytest.mark.parametrize("tail_bytes", [12, 100, 32768])
def test_a_valid_footer_reads_as_its_metadata_parses(tail_bytes):
    file = parquet_ending_with(RICH, len(RICH) + 5000)
    read = FooterReader(VirtualFileStorage(len(file), file), tail_bytes, True).read("file.parquet", len(file))
    assert summary(read) == summary(parse_footer(RICH, True))
    assert (read.footer_length, read.request_count) == (len(RICH), 1 if tail_bytes >= len(RICH) + 8 else 2)


# ---- R4: bounded work ----

def peak_memory(call: Callable[[], Any]) -> int:
    tracemalloc.start()
    try:
        call()
        return tracemalloc.get_traced_memory()[1]
    finally:
        tracemalloc.stop()


def best_time(call: Callable[[], Any], runs: int = 3) -> float:
    best = None
    for _ in range(runs):
        started = time.perf_counter()
        call()
        elapsed = time.perf_counter() - started
        best = elapsed if best is None else min(best, elapsed)
    return best


def many_leaves(count: int) -> bytes:
    return footer(leaves=[f"c{i}" for i in range(count)])


def deep_leaves(count: int) -> bytes:
    """Many leaves at the bottom of a schema of 200 levels: their paths are long."""
    bottom = group("g198", *[leaf(f"c{i}", "INT32") for i in range(count)])
    field = bottom
    for level in reversed(range(198)):
        field = group(f"g{level}", field)
    return schema_footer([field])


def unknown_fields(count: int) -> bytes:
    def write(w: Thrift) -> None:
        for i in range(count):
            nested_chain(w, 300 + i % 50, 30, (Thrift.LIST, Thrift.STRUCT, Thrift.MAP, Thrift.SET))
    return footer(extra={"file": write})


def repeated_columns(count: int) -> bytes:
    """A row group whose columns list comes count times: the last one wins."""
    def write(w: Thrift) -> None:
        for i in range(count):
            columns_list(1, i)(w)
    return footer(extra={"row_group": write})


GROWTH = {"many leaves": many_leaves, "many leaves deep in the schema": deep_leaves,
          "many unknown nested values": unknown_fields, "a columns list repeated": repeated_columns}


@pytest.mark.parametrize("build", GROWTH.values(), ids=GROWTH.keys())
def test_the_memory_and_the_time_of_a_parse_grow_with_the_length_of_the_footer(build):
    small, large = build(150), build(1200)
    assert len(large) > 6 * len(small)
    ratio = len(large) / len(small)

    def parse(data: bytes) -> Callable[[], Any]:
        return lambda: only_footer_errors(lambda: parse_footer(data), "parse")

    memory = peak_memory(parse(large)) / max(1, peak_memory(parse(small)))
    assert memory < 2 * ratio, f"memory grows {memory:.1f} times for {ratio:.1f} times the bytes"
    elapsed = best_time(parse(large)) / max(best_time(parse(small)), 1e-4)
    assert elapsed < 5 * ratio, f"time grows {elapsed:.1f} times for {ratio:.1f} times the bytes"


def test_a_footer_of_a_megabyte_of_hostile_values_is_read_in_bounded_time():
    def write(w: Thrift) -> None:
        for i in range(8500):
            nested_chain(w, 300 + i % 50, 60, (Thrift.LIST, Thrift.MAP))
    data = footer(extra={"file": write})
    assert len(data) > 1_000_000
    file = parquet_file(data)
    started = time.monotonic()
    only_footer_errors(lambda: FooterReader(VirtualFileStorage(len(file), file)).read("file.parquet", len(file)),
                       "a megabyte of nested values")
    assert time.monotonic() - started < 20


# ---- P1: one depth for every container ----

@pytest.mark.parametrize("kinds", CONTAINERS.values(), ids=CONTAINERS.keys())
def test_containers_nested_up_to_64_levels_with_the_footer_are_skipped_and_one_more_level_is_invalid(kinds):
    # The FileMetaData is the first level, as it is for the structures today.
    fits = footer(extra={"file": lambda w: nested_chain(w, 300, 63, kinds)})
    assert summary(parse_footer(fits)) == summary(parse_footer(BASELINE))
    assert_refused(footer(extra={"file": lambda w: nested_chain(w, 300, 64, kinds)}), TOO_DEEP)
    assert_refused(footer(extra={"file": lambda w: nested_chain(w, 300, 65, kinds)}), TOO_DEEP)


def test_the_lists_and_structures_of_the_footer_count_in_the_depth_of_what_they_hold():
    # Statistics are the seventh level: FileMetaData, row_groups, RowGroup, columns, ColumnChunk, ColumnMetaData.
    fits = footer(extra={"statistics": lambda w: nested_lists(w, 300, 57)})
    assert summary(parse_footer(fits)) == summary(parse_footer(BASELINE))
    assert_refused(footer(extra={"statistics": lambda w: nested_lists(w, 300, 58)}), TOO_DEEP)


@pytest.mark.parametrize("depth", [10_000, 100_000])
@pytest.mark.parametrize("where", ["file", "statistics", "leaf"])
def test_ten_thousand_and_a_hundred_thousand_nested_lists_are_too_deep(where, depth):
    data = footer(extra={where: lambda w: nested_lists(w, 300, depth)})
    assert_refused(data, TOO_DEEP)
    file = parquet_file(data)
    error = read_refusal(VirtualFileStorage(len(file), file), len(file))
    assert (error.code, str(error)) == (INVALID, TOO_DEEP)


@pytest.mark.parametrize("kinds", [(Thrift.MAP,), (Thrift.SET,), (Thrift.MAP, Thrift.SET), (Thrift.STRUCT,)],
                         ids=["maps", "sets", "maps and sets", "structures"])
@pytest.mark.parametrize("depth", [65, 100, 1000, 10_000])
def test_maps_sets_and_structures_nested_past_64_levels_are_too_deep(kinds, depth):
    assert_refused(footer(extra={"column": lambda w: nested_chain(w, 300, depth, kinds)}), TOO_DEEP)


# ---- P2: the schema tree ----

@pytest.mark.parametrize("levels", [1, 64, 65, 128, 255, 256])
def test_a_schema_of_up_to_256_levels_below_the_root_is_parsed(levels):
    parsed = parse_footer(schema_footer([chain_schema(levels)]))
    [only] = parsed.leaves
    assert len(only.path) == levels
    assert only.path[-1] == "bottom"


def test_a_schema_of_257_levels_below_the_root_is_nested_too_deep():
    assert_refused(schema_footer([chain_schema(257)]), SCHEMA_TOO_DEEP)
    assert_refused(schema_footer([group("top", list_chain(128))]), SCHEMA_TOO_DEEP)


def collect_folder(tmp_path, files: Dict[str, bytes], **options: Any):
    """Collects a folder of Parquet files that holds these files, and returns the open footprint and its table."""
    lake = Container(tmp_path / "lake")
    for name, data in files.items():
        lake.write(f"data/t/{name}", data)
    storage = LocalStorage(str(lake.root))
    [location] = discover_folder(storage, "data/t")
    assert location.format == TableFormats.PARQUET
    data, _ = collect_bytes(storage, [location], checkpoint_reader=None, **options)
    assert_valid(data)
    doc = open_bytes(data)
    [table] = rows(doc, "tables")
    return doc, table


def physical_columns(doc, table) -> List[Dict[str, Any]]:
    schemas = {schema["id"] for schema in of_table(doc, "physicalSchemas", table["id"])}
    return [row for row in rows(doc, "physicalColumns") if row["physicalSchema"] in schemas]


DEEP_SCHEMAS = [
    ("64 levels of structures", lambda: chain_schema(64), "g0", lambda: chain_type(64), 64),
    ("65 levels of structures", lambda: chain_schema(65), "g0", lambda: chain_type(65), 65),
    ("66 levels of structures", lambda: chain_schema(66), "g0", lambda: chain_type(66), 66),
    ("128 levels of structures", lambda: chain_schema(128), "g0", lambda: chain_type(128), 128),
    ("255 levels of structures", lambda: chain_schema(255), "g0", lambda: chain_type(255), 255),
    ("256 levels of structures", lambda: chain_schema(256), "g0", lambda: chain_type(256), 256),
    ("33 nested lists", lambda: list_chain(33), "l0", lambda: "array<" * 33 + "integer" + ">" * 33, 67),
    ("127 nested lists", lambda: list_chain(127), "l0", lambda: "array<" * 127 + "integer" + ">" * 127, 255),
    ("a structure of 127 nested lists", lambda: group("top", list_chain(127)), "top",
     lambda: "struct<l0:" + "array<" * 127 + "integer" + ">" * 127 + ">", 256),
]


@pytest.mark.parametrize("field, name, data_type, levels", [case[1:] for case in DEEP_SCHEMAS],
                         ids=[case[0] for case in DEEP_SCHEMAS])
def test_the_columns_of_a_folder_are_typed_for_every_schema_that_the_parser_accepts(tmp_path, field, name, data_type,
                                                                                   levels):
    doc, table = collect_folder(tmp_path, {"part-0001.parquet": parquet_file(schema_footer([field()]))})
    assert of_table(doc, "issues", table["id"]) == []
    assert (table["collectedLevel"], table["footerCount"]) == (2, 1)
    [column] = of_table(doc, "columns", table["id"])
    assert (column["name"], column["dataType"], column["isNullable"]) == (name, data_type(), True)
    [physical] = physical_columns(doc, table)
    assert len(physical["path"]) == levels
    assert (physical["path"][0], physical["column"]) == (name, column["id"])


def test_a_schema_of_257_levels_in_a_folder_is_an_issue_of_its_file_and_the_folder_has_no_columns(tmp_path):
    doc, table = collect_folder(tmp_path, {"part-0001.parquet": parquet_file(schema_footer([chain_schema(257)]))})
    [issue] = of_table(doc, "issues", table["id"])
    assert (issue["code"], issue["message"], issue["path"]) == (INVALID, SCHEMA_TOO_DEEP, "part-0001.parquet")
    assert of_table(doc, "columns", table["id"]) == []
    assert table["footerCount"] == 0


# ---- P3: repeated and missing fields ----

@pytest.mark.parametrize("leaves, first, second", [(("a",), 2, 1), (("a", "b"), 1, 2), (("a", "b"), 2, 2),
                                                   (("a", "b", "c"), 5, 3)],
                         ids=["shorter", "longer", "as long", "shorter again"])
def test_a_second_columns_list_of_a_row_group_replaces_the_first(leaves, first, second):
    data = footer(leaves=leaves, chunks=first, extra={"row_group": columns_list(second, 70)})
    parsed = only_footer_errors(lambda: parse_footer(data), "a second columns list")
    assert parsed is None, str(parsed)
    [group_] = parse_footer(data).row_groups
    assert [chunk.num_values for chunk in group_.columns] == [70 + i for i in range(second)]
    assert [chunk.codec for chunk in group_.columns] == ["SNAPPY"] * second
    assert [chunk.total_compressed_size for chunk in group_.columns] == [1000 + i for i in range(second)]
    assert all(not chunk.has_statistics and chunk.null_count is None for chunk in group_.columns)


@pytest.mark.parametrize("first, second", [(1, 2), (2, 3), (1, 0)])
def test_a_second_columns_list_that_does_not_match_the_leaves_is_invalid_with_its_own_count(first, second):
    assert_refused(footer(leaves=("a",), chunks=first, extra={"row_group": columns_list(second)}),
                   CHUNKS.format(second, 1))


def second_schema(w: Thrift) -> None:
    w.list_of(2, Thrift.STRUCT, 2)
    w.element().binary(4, "root").i32(5, 1).end()
    w.element().i32(1, INT32).i32(3, REQUIRED).binary(4, "z").end()


def second_row_groups(w: Thrift) -> None:
    w.list_of(4, Thrift.STRUCT, 2)
    for ordinal in range(2):
        w.element()
        columns_list(1, 30 + ordinal)(w)
        w.i64(2, 5).i64(3, 6 + ordinal)
        w.end()


def second_statistics(w: Thrift) -> None:
    w.begin(12).i64(3, 9).end()


def second_pair(w: Thrift) -> None:
    w.list_of(5, Thrift.STRUCT, 1)
    w.element().binary(1, "first.key").binary(1, "org.apache.spark.version").binary(2, "3.4").binary(2, "3.5.1").end()


def first(parsed):
    return parsed.row_groups[0].columns[0]


# (structure and field, hooks, what the parse gives): the field written twice, the second time with another value.
TWICE = [
    ("FileMetaData.version", {"file": lambda w: w.i32(1, 7)}, lambda p: p.version == 7),
    ("FileMetaData.num_rows", {"file": lambda w: w.i64(3, 55)}, lambda p: p.num_rows == 55),
    ("FileMetaData.created_by", {"file": lambda w: w.binary(6, "second writer")},
     lambda p: p.created_by == "second writer"),
    ("FileMetaData.schema", {"file": second_schema},
     lambda p: [(tuple(leaf.path), leaf.physical_type, leaf.repetition) for leaf in p.leaves] == [(("z",), "INT32",
                                                                                                   "REQUIRED")]),
    ("FileMetaData.row_groups", {"file": second_row_groups},
     lambda p: [(g.num_rows, first_chunk(g).num_values) for g in p.row_groups] == [(6, 30), (7, 31)]),
    ("FileMetaData.key_value_metadata", {"file": lambda w: (
        w.list_of(5, Thrift.STRUCT, 1).element().binary(1, "vorder").binary(2, "x").end(),
        w.list_of(5, Thrift.STRUCT, 1).element().binary(1, "org.apache.spark.version").binary(2, "3.5.1").end())},
     lambda p: [(kv.key, kv.value_length, kv.value) for kv in p.key_value_metadata] == [
         ("org.apache.spark.version", 5, "3.5.1")]),
    ("KeyValue.key and KeyValue.value", {"file": second_pair},
     lambda p: [(kv.key, kv.value_length, kv.value) for kv in p.key_value_metadata] == [
         ("org.apache.spark.version", 5, "3.5.1")]),
    ("SchemaElement.name", {"leaf": lambda w: w.binary(4, "renamed")}, lambda p: p.leaves[0].path == ("renamed",)),
    ("SchemaElement.type", {"leaf": lambda w: w.i32(1, INT32)}, lambda p: p.leaves[0].physical_type == "INT32"),
    ("SchemaElement.repetition", {"leaf": lambda w: w.i32(3, REQUIRED)},
     lambda p: p.leaves[0].repetition == "REQUIRED"),
    ("SchemaElement.converted_type", {"leaf": lambda w: w.i32(6, 0)}, lambda p: p.leaves[0].converted_type == "UTF8"),
    ("SchemaElement.field_id", {"leaf": lambda w: w.i32(9, 5).i32(9, 6)}, lambda p: p.leaves[0].field_id == 6),
    ("SchemaElement.logicalType", {"leaf": lambda w: w.begin(10).begin(1).end().end().begin(10).begin(6).end().end()},
     lambda p: p.leaves[0].logical_type == "DATE"),
    ("LogicalType, two members", {"leaf": lambda w: w.begin(10).begin(1).end().begin(6).end().end()},
     lambda p: p.leaves[0].logical_type == "DATE"),
    ("DecimalType.scale", {"leaf": lambda w: w.begin(10).begin(5).i32(1, 2).i32(2, 10).i32(1, 3).end().end()},
     lambda p: p.leaves[0].logical_type == "DECIMAL(10,3)"),
    ("RowGroup.num_rows", {"row_group": lambda w: w.i64(3, 77)}, lambda p: p.row_groups[0].num_rows == 77),
    ("RowGroup.total_byte_size", {"row_group": lambda w: w.i64(2, 88)},
     lambda p: p.row_groups[0].total_byte_size == 88),
    ("RowGroup.ordinal", {"row_group": lambda w: w.i16(7, 9)}, lambda p: p.row_groups[0].ordinal == 9),
    ("RowGroup.sorting_columns", {"row_group": lambda w: (
        w.list_of(4, Thrift.STRUCT, 1).element().i32(1, 0).boolean(2, True).end(),
        w.list_of(4, Thrift.STRUCT, 1).element().i32(1, 0).boolean(3, True).end())},
     lambda p: [(s.column_index, s.descending, s.nulls_first) for s in p.row_groups[0].sorting_columns] == [
         (0, False, True)]),
    ("ColumnChunk.file_path", {"chunk": lambda w: w.binary(1, "a.parquet").binary(1, "b.parquet")},
     lambda p: first(p).file_path == "b.parquet"),
    ("ColumnChunk.meta_data", {"chunk": second_meta_data},
     lambda p: (first(p).codec, tuple(first(p).encodings), first(p).num_values, first(p).total_uncompressed_size,
                first(p).total_compressed_size, first(p).data_page_offset, first(p).dictionary_page_offset,
                first(p).has_statistics, first(p).null_count, first(p).distinct_count, first(p).has_min_max)
     == ("SNAPPY", ("PLAIN",), 11, 22, 33, 44, None, False, None, None, False)),
    ("ColumnMetaData.codec", {"column": lambda w: w.i32(4, SNAPPY)}, lambda p: first(p).codec == "SNAPPY"),
    ("ColumnMetaData.num_values", {"column": lambda w: w.i64(5, 33)}, lambda p: first(p).num_values == 33),
    ("ColumnMetaData.total_compressed_size", {"column": lambda w: w.i64(7, 44)},
     lambda p: first(p).total_compressed_size == 44),
    ("ColumnMetaData.encodings", {"column": lambda w: w.list_of(2, Thrift.I32, 1).value_i32(PLAIN_DICTIONARY)},
     lambda p: tuple(first(p).encodings) == ("PLAIN_DICTIONARY",)),
    ("ColumnMetaData.dictionary_page_offset", {"column": lambda w: w.i64(11, 2).i64(11, 3)},
     lambda p: first(p).dictionary_page_offset == 3),
    ("ColumnMetaData.statistics", {"column": second_statistics},
     lambda p: (first(p).null_count, first(p).distinct_count, first(p).has_min_max) == (9, None, False)),
    ("ColumnMetaData.encoding_stats", {"column": lambda w: (
        w.list_of(13, Thrift.STRUCT, 1).element().i32(1, DATA_PAGE).i32(2, PLAIN).i32(3, 1).end(),
        w.list_of(13, Thrift.STRUCT, 1).element().i32(1, DATA_PAGE).i32(2, RLE_DICTIONARY).i32(3, 4).end())},
     lambda p: [(s.page_type, s.encoding, s.count) for s in first(p).encoding_stats] == [
         ("DATA_PAGE", "RLE_DICTIONARY", 4)]),
    ("PageEncodingStats.count", {"column": lambda w: w.list_of(13, Thrift.STRUCT, 1).element().i32(1, DATA_PAGE)
                                 .i32(2, PLAIN).i32(3, 1).i32(3, 5).end()},
     lambda p: [(s.page_type, s.encoding, s.count) for s in first(p).encoding_stats] == [("DATA_PAGE", "PLAIN", 5)]),
    ("ColumnMetaData.size_statistics", {"column": lambda w: w.begin(16).i64(1, 5).end().begin(16).end()},
     lambda p: first(p).unencoded_byte_array_data_bytes is None),
    ("ColumnMetaData.bloom_filter_length", {"column": lambda w: w.i32(15, 3).i32(15, 4)},
     lambda p: first(p).bloom_filter_length == 4),
    ("Statistics.null_count", {"statistics": lambda w: w.i64(3, 5)}, lambda p: first(p).null_count == 5),
    ("Statistics.distinct_count", {"statistics": lambda w: w.i64(4, 8)}, lambda p: first(p).distinct_count == 8),
    ("Statistics.min_value", {"statistics": lambda w: w.binary(6, le64(42))}, lambda p: first(p).min_value == "42"),
]


def first_chunk(group_):
    return group_.columns[0]


@pytest.mark.parametrize("hooks, check", [case[1:] for case in TWICE], ids=[case[0] for case in TWICE])
def test_a_field_that_appears_twice_takes_its_last_value_in_every_structure(hooks, check):
    data = footer(extra=hooks)
    error = only_footer_errors(lambda: parse_footer(data, True), "a field twice")
    assert error is None, str(error)
    assert check(parse_footer(data, True))


@pytest.mark.parametrize("hooks, check", [
    ({"file": lambda w: w.binary(1, "two")}, lambda p: p.version == 1),
    ({"file": lambda w: w.binary(3, "many")}, lambda p: p.num_rows == 100),
    ({"leaf": lambda w: w.i32(4, 1)}, lambda p: p.leaves[0].path == ("id",)),
    ({"row_group": lambda w: w.binary(3, "rows")}, lambda p: p.row_groups[0].num_rows == 100),
    ({"column": lambda w: w.binary(5, "values")}, lambda p: first(p).num_values == 100),
    ({"statistics": lambda w: w.binary(3, "nulls")}, lambda p: first(p).null_count == 0),
], ids=["version", "num_rows", "name", "row group num_rows", "num_values", "null_count"])
def test_a_known_field_with_an_unexpected_wire_type_after_the_field_is_skipped(hooks, check):
    assert check(parse_footer(footer(extra=hooks)))


def meta_data_without(missing: int) -> Callable[[Thrift], None]:
    def write(w: Thrift) -> None:
        w.begin(3).i32(1, INT64)
        for field, value in ((5, 100), (6, 800), (7, 400), (9, 4)):
            if field != missing:
                w.i64(field, value)
        w.end()
    return write


MISSING = [
    ("version", footer(omit=["file.version"]), LACKS),
    ("schema", footer(omit=["file.schema"]), LACKS),
    ("num_rows", footer(omit=["file.num_rows"]), LACKS),
    ("row_groups", footer(omit=["file.row_groups"]), LACKS),
    ("version with an unexpected type", footer(replace={"file.version": lambda w: w.binary(1, "1")}), LACKS),
    ("the name of a schema element", footer(omit=["leaf.name"]), NO_NAME),
    ("the columns of a row group", footer(omit=["row_group.columns"]), GROUP_LACKS),
    ("the num_rows of a row group", footer(omit=["row_group.num_rows"]), GROUP_LACKS),
    ("the total_byte_size of a row group", footer(omit=["row_group.total_byte_size"]), GROUP_LACKS),
    ("columns with an unexpected type", footer(replace={"row_group.columns": lambda w: w.binary(1, "x")}),
     GROUP_LACKS),
    ("num_values", footer(meta_data=False, extra={"chunk": meta_data_without(5)}), META_LACKS),
    ("total_uncompressed_size", footer(meta_data=False, extra={"chunk": meta_data_without(6)}), META_LACKS),
    ("total_compressed_size", footer(meta_data=False, extra={"chunk": meta_data_without(7)}), META_LACKS),
    ("data_page_offset", footer(meta_data=False, extra={"chunk": meta_data_without(9)}), META_LACKS),
    ("more leaves than chunks", footer(leaves=("a", "b"), chunks=1), CHUNKS.format(1, 2)),
    ("more chunks than leaves", footer(leaves=("a",), chunks=2), CHUNKS.format(2, 1)),
    ("no chunks", footer(leaves=("a", "b", "c"), chunks=0), CHUNKS.format(0, 3)),
]


@pytest.mark.parametrize("data, message", [case[1:] for case in MISSING], ids=[case[0] for case in MISSING])
def test_a_required_field_that_is_missing_stays_invalid_as_today(data, message):
    assert_refused(data, message)


# ---- P4: text ----

UTF8 = [
    (b"plain ASCII", "plain ASCII"),
    (b"\xc3\xa9t\xc3\xa9 \xe2\x82\xac \xf0\x9f\x98\x80", "été € \U0001F600"),
    (b"\xff", "�"),
    (b"a\x80b", "a�b"),
    (b"\xe2\x82", "�"),
    (b"\xe2\x82A", "�A"),
    (b"\xf0\x9f\x98", "�"),
    (b"\xf0\x9f\x98 ok", "� ok"),
    (b"\xed\xa0\x80", "���"),
    (b"\xc0\xaf", "��"),
    (b"\xe0\x80\xaf", "���"),
    (b"\xf4\x90\x80\x80", "����"),
    (b"\xf8\x88\x80\x80\x80", "�����"),
    (b"caf\xc3", "caf�"),
    (b"\xc3\x28", "�("),
]


@pytest.mark.parametrize("raw, text", UTF8, ids=[raw.hex() for raw, _ in UTF8])
def test_every_string_is_utf8_with_a_replacement_character_for_each_maximal_invalid_subpart(raw, text):
    def pair(w: Thrift) -> None:
        w.list_of(5, Thrift.STRUCT, 2)
        w.element().binary(1, raw).binary(2, b"v").end()
        w.element().binary(1, "org.apache.spark.version").binary(2, raw).end()
    parsed = parse_footer(footer(replace={"file.created_by": lambda w: w.binary(6, raw),
                                          "leaf.name": lambda w: w.binary(4, raw),
                                          "file.key_value_metadata": pair}))
    assert parsed.created_by == text
    assert parsed.leaves[0].path == (text,)
    assert [(pair.key, pair.value_length) for pair in parsed.key_value_metadata] == [
        (text, 1), ("org.apache.spark.version", len(raw))]
    assert parsed.key_value_metadata[1].value == text


# The keys of the allow-list compare as OrdinalIgnoreCase of .NET compares them (P4 as amended: C# unchanged, Python
# follows it): the ASCII letters in any case. OrdinalIgnoreCase does not fold a character outside ASCII into ASCII,
# as measured on .NET 10: the long s is not s, although its upper case is S; the dotless i is not i; the Kelvin sign
# is not k, although str.lower() makes it k; the dotted capital I is not i, although str.lower() makes it i and a
# combining dot.
KEPT = ["com.microsoft.parquet.vorder.enabled", "COM.MICROSOFT.PARQUET.VORDER.ENABLED", "x.VOrder.level", "vorder",
        "ORG.APACHE.SPARK.VERSION", "Org.Apache.Spark.Version", "org.apache.spark.version",
        "spark.sql.parquet.STRING_PREFIX_LENGTH", "String_Prefix_Length", "org.apache.ſpark.vorder.x"]
NOT_KEPT = ["org.apache.ſpark.version", "org.apache.spark.verſion", "org.apache.spark.versıon",
            "ORG.APACHE.ſPARK.VERSıON", "spark.ſtring_prefix_length", "spark.string_prefıx_length",
            "org.apache.sparK.version", "org.apache.spark.versİon", "spark.strİng_prefix_length",
            "vörder", "org.apache.spark.version2", " org.apache.spark.version", "string_prefix_length.x",
            "ｖｏｒｄｅｒ"]


def escaped(text: str) -> str:
    return text.encode("unicode_escape").decode()


@pytest.mark.parametrize("key", KEPT, ids=[escaped(key) for key in KEPT])
def test_a_key_of_the_allow_list_is_kept_in_any_case_as_ordinal_ignore_case_compares_it(key):
    assert is_kept_key(key)
    parsed = parse_footer(footer(key_values=[(key, "kept value")]))
    assert [(pair.key, pair.value_length, pair.value) for pair in parsed.key_value_metadata] == [
        (key, 10, "kept value")]


@pytest.mark.parametrize("key", NOT_KEPT, ids=[escaped(key) for key in NOT_KEPT])
def test_a_key_that_ordinal_ignore_case_does_not_match_with_the_allow_list_is_not_kept(key):
    assert not is_kept_key(key)
    parsed = parse_footer(footer(key_values=[(key, "a data value")]))
    assert [(pair.key, pair.value_length, pair.value) for pair in parsed.key_value_metadata] == [(key, 12, None)]


@pytest.mark.parametrize("pairs, vordered, level", [
    ([("com.microsoft.parquet.vorder.enabled", "true"), ("com.microsoft.parquet.vorder.level", "default")], True,
     "default"),
    ([("COM.MICROSOFT.PARQUET.VORDER.ENABLED", "TRUE"), ("X.VOrder.Level", "High")], True, "High"),
    ([("org.apache.ſpark.vorder.enabled", "True")], True, None),
    ([("com.microsoft.parquet.vorder.enabled", "false"), ("com.microsoft.parquet.vorder.level", "Default")], False,
     "Default"),
    ([("com.microsoft.parquet.vorder.enabled", "yes")], False, None),
    ([("com.microsoft.parquet.vorder.enabled.not", "true")], False, None),
], ids=["lower case", "any case", "a long s before the suffix", "false", "another value", "another key"])
def test_the_v_order_keys_and_value_of_a_footer_compare_without_case(tmp_path, pairs, vordered, level):
    doc, table = collect_folder(tmp_path, {"part-0001.parquet": parquet_file(footer(key_values=pairs))})
    [writer] = of_table(doc, "writers", table["id"])
    assert (writer["isVOrdered"], writer.get("vOrderLevel")) == (vordered, level)
    [file] = of_table(doc, "files", table["id"])
    assert file["isVOrdered"] is vordered


# ---- P5: the bounds of a decimal ----

def decimal_text(raw: bytes, scale: int) -> str:
    """The text of a decimal from its unscaled value in big endian and its scale, as the collectors write it."""
    unscaled = int.from_bytes(raw, "big", signed=True) if raw else 0
    if scale <= 0:
        return str(unscaled)
    digits = str(abs(unscaled)).rjust(scale + 1, "0")
    text = digits[:-scale] + "." + digits[-scale:]
    return "-" + text if unscaled < 0 else text


def bounds(data: bytes) -> Tuple[Optional[str], Optional[str]]:
    error = only_footer_errors(lambda: parse_footer(data, True), "bounds")
    assert error is None, str(error)
    chunk = first(parse_footer(data, True))
    assert chunk.has_min_max
    return chunk.min_value, chunk.max_value


LARGEST = bytes([0x7F]) + b"\xff" * 31  # 2^255 - 1, the largest value of 32 bytes
SMALLEST = bytes([0x80]) + bytes(31)   # -2^255

WRITTEN = [
    ("16 bytes, scale 4", FIXED, 16, be(-12345, 16), be(987654321, 16), 38, 4),
    ("1 byte, scale 2", BYTE_ARRAY, None, b"\x85", b"\x7f", 3, 2),
    ("no byte", BYTE_ARRAY, None, b"", b"\x01", 3, 2),
    ("32 bytes, scale 0", BYTE_ARRAY, None, SMALLEST, LARGEST, 77, 0),
    ("32 bytes, scale 76", BYTE_ARRAY, None, SMALLEST, LARGEST, 77, 76),
    ("32 bytes of a fixed length, scale 76", FIXED, 32, SMALLEST, LARGEST, 76, 76),
    ("2 bytes, scale 76", FIXED, 2, be(-2, 2), be(300, 2), 76, 76),
]


@pytest.mark.parametrize("physical, length, minimum, maximum, precision, scale", [case[1:] for case in WRITTEN],
                         ids=[case[0] for case in WRITTEN])
def test_a_decimal_bound_of_at_most_32_bytes_and_a_scale_of_at_most_76_is_written(physical, length, minimum, maximum,
                                                                               precision, scale):
    expected = (decimal_text(minimum, scale), decimal_text(maximum, scale))
    assert bounds(bound_footer(physical, minimum, maximum, type_length=length, logical=(precision, scale))) == expected
    # The same with the converted type, which gives the scale in its own field.
    assert bounds(bound_footer(physical, minimum, maximum, type_length=length, converted=5, scale=scale,
                               precision=precision)) == expected


@pytest.mark.parametrize("physical, value, scale, text", [
    (INT32, le32(12345), 2, "123.45"), (INT32, le32(-5), 2, "-0.05"), (INT64, le64(-1), 76, "-0." + "0" * 75 + "1"),
    (INT64, le64(2 ** 63 - 1), 0, str(2 ** 63 - 1))], ids=["int32", "negative int32", "int64, scale 76", "int64"])
def test_a_decimal_bound_of_an_integer_is_written_up_to_a_scale_of_76(physical, value, scale, text):
    assert bounds(bound_footer(physical, value, value, logical=(18, scale))) == (text, text)


NOT_WRITTEN = [
    ("33 bytes", BYTE_ARRAY, None, b"\x01" + bytes(32), 77, 0),
    ("33 bytes of a fixed length", FIXED, 33, b"\x01" + bytes(32), 79, 2),
    ("2,000 bytes", BYTE_ARRAY, None, b"\x7f" * 2000, 38, 2),
    ("scale 77", FIXED, 16, be(5, 16), 38, 77),
    ("scale 1000", FIXED, 16, be(5, 16), 38, 1000),
    ("scale 2^31 - 1", FIXED, 16, be(5, 16), 38, 2 ** 31 - 1),
]


@pytest.mark.parametrize("physical, length, value, precision, scale", [case[1:] for case in NOT_WRITTEN],
                         ids=[case[0] for case in NOT_WRITTEN])
def test_a_decimal_bound_of_more_than_32_bytes_or_a_scale_over_76_is_null(physical, length, value, precision, scale):
    # A scale over 76 is checked on 77 first, so that a parser that builds a string as long as the scale fails here and
    # not on the allocation of the larger scales.
    if scale > 77:
        assert bounds(bound_footer(physical, value, value, type_length=length, logical=(precision, 77))) == (None, None)
    assert bounds(bound_footer(physical, value, value, type_length=length, logical=(precision, scale))) == (None, None)
    assert bounds(bound_footer(physical, value, value, type_length=length, converted=5, scale=scale,
                               precision=precision)) == (None, None)


def test_each_bound_of_a_decimal_is_written_or_null_on_its_own():
    small = be(-7, 2)
    assert bounds(bound_footer(BYTE_ARRAY, small, b"\x01" + bytes(32), logical=(38, 2))) == ("-0.07", None)
    assert bounds(bound_footer(BYTE_ARRAY, b"\x01" + bytes(32), small, logical=(38, 2))) == (None, "-0.07")


def test_a_decimal_bound_of_an_integer_with_a_scale_over_76_is_null():
    assert bounds(bound_footer(INT64, le64(5), le64(5), logical=(18, 77))) == (None, None)
    assert bounds(bound_footer(INT32, le32(5), le32(5), converted=5, scale=2 ** 31 - 1, precision=9)) == (None, None)


@pytest.mark.parametrize("physical, value, annotation, text", [
    (INT32, le32(7), {}, "7"),
    (INT64, le64(-9), {}, "-9"),
    (INT32, le32(1), {"converted": 6}, "1970-01-02"),
    (INT64, le64(1), {"converted": 10}, "1970-01-01T00:00:00.000001"),
    (BYTE_ARRAY, b"abc", {"converted": 0}, "abc"),
    (BYTE_ARRAY, b"\xff\x00", {}, "0xff00"),
    (BYTE_ARRAY, "naïve".encode(), {}, "naïve"),
    (FIXED, b"\x01\x02", {"type_length": 2}, "0x0102"),
    (DOUBLE, struct.pack("<d", 1.5), {}, "1.5"),
    (0, b"\x01", {}, "true"),
], ids=["int32", "int64", "date", "timestamp", "string", "binary", "text without annotation", "fixed", "double",
        "boolean"])
def test_every_other_bound_is_written_as_today(physical, value, annotation, text):
    assert bounds(bound_footer(physical, value, value, **annotation)) == (text, text)
