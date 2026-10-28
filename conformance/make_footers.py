"""Writes Parquet files with every writer that the running Python has, for the footer check of the collectors.

Usage, once in each environment that has writers installed:

    python conformance/make_footers.py --out DIR [--label NAME]

Each case is one file, in a folder of its own, DIR/<label>/<writer>/<case>/part-0.parquet, so that a collection of
DIR finds each file as a table: a folder of Parquet files. A Delta table written by deltalake is a folder with its
_delta_log. DIR/<label>/index.json lists every case: the writer and its version, the options, and the reason when the
writer of this environment cannot write it. The data is synthetic and small, and every run with the same versions
writes the same values.

Writers, each when its package imports: pyarrow (its options: compressions, dictionary, data page versions,
statistics, byte stream split, encodings, page index, bloom filters, sorting columns, INT96 timestamps, decimals as
integers, the legacy names of nested types, field ids, encryption; the types: every primitive and logical type it
writes, lists, large lists, fixed-size lists, maps, structures nested several levels deep, nulls at every level,
many row groups, many columns), DuckDB, polars, fastparquet, datafusion (the writer of arrow-rs), and deltalake
(delta-rs, a Delta table). pyspark needs a Java runtime: it is used only when one is on the PATH.

footer_check.py reads every file with both collectors and compares them with each other and with the metadata of
pyarrow. The files stay outside the repository: the kit keeps only the few under samples/footers.
"""
from __future__ import annotations

import argparse
import datetime
import decimal
import gc
import importlib
import json
import os
import shutil
import sys
import traceback
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

ROWS = 60


class Cases:
    """The cases of one writer: each writes one file, or raises to say that the writer cannot."""

    def __init__(self, root: Path, writer: str, version: str) -> None:
        self.root = root / writer
        self.writer = writer
        self.version = version
        self.entries: List[Dict[str, Any]] = []

    def case(self, name: str, write: Callable[[Path], Any], **options: Any) -> None:
        folder = self.root / name
        target = folder / "part-0.parquet"
        entry: Dict[str, Any] = {"writer": self.writer, "version": self.version, "case": name,
                                 "path": f"{self.writer}/{name}/part-0.parquet", "options": options}
        try:
            if folder.exists():
                shutil.rmtree(folder)
            folder.mkdir(parents=True)
            result = write(target)
            if isinstance(result, str):  # a writer that names its own file, such as a Delta table
                entry["path"] = f"{self.writer}/{name}/{result}"
            entry["skipped"] = None
        except Exception as error:  # noqa: BLE001 - a case that a version cannot write is recorded, not fatal
            # A writer that failed may still hold its file open until it is collected, and Windows keeps an open file:
            # the frames of the traceback hold the writer.
            error.__traceback__ = None
            gc.collect()
            shutil.rmtree(folder, ignore_errors=True)
            entry["skipped"] = f"{type(error).__name__}: {str(error).splitlines()[0][:300] if str(error) else ''}"
            if folder.exists():
                entry["skipped"] += "; the folder of the case could not be removed"
        self.entries.append(entry)


def module(name: str) -> Optional[Any]:
    try:
        return importlib.import_module(name)
    except Exception:  # noqa: BLE001 - a missing or broken writer is skipped
        return None


# ---- the data ----

def values(rows: int = ROWS) -> Dict[str, List[Any]]:
    """Plain Python values for the columns of most cases, with nulls in every column."""
    def nullable(make: Callable[[int], Any], every: int = 7) -> List[Any]:
        return [None if i % every == 3 else make(i) for i in range(rows)]

    return {
        "id": list(range(rows)),
        "flag": nullable(lambda i: i % 3 == 0),
        "small": nullable(lambda i: (i % 200) - 100),
        "number": nullable(lambda i: i * 1_000_003 - 7),
        "big": nullable(lambda i: (i - 30) * 9_007_199_254_740_993),
        "ratio": nullable(lambda i: i / 7.0 - 3.0),
        "name": nullable(lambda i: f"name {i % 9} {'é' * (i % 3)}"),
        "code": nullable(lambda i: ["red", "green", "blue"][i % 3]),
        "blob": nullable(lambda i: bytes([i % 256, (i * 7) % 256, 0])),
        "day": nullable(lambda i: datetime.date(2020, 1, 1) + datetime.timedelta(days=i * 11)),
        "moment": nullable(lambda i: datetime.datetime(2021, 3, 4, 5, 6, 7, 123456) + datetime.timedelta(hours=i)),
        "amount": nullable(lambda i: decimal.Decimal(i * 1234 - 30000).scaleb(-2)),
    }


def nested_values(rows: int = ROWS) -> Dict[str, List[Any]]:
    """Nested values with nulls at every level: a null list, an empty list, a null element, a null structure, a null
    field, a null map, a null map value."""
    def ints(i: int) -> Optional[List[Optional[int]]]:
        if i % 11 == 5:
            return None
        if i % 11 == 6:
            return []
        return [None if (i + j) % 5 == 2 else i * 10 + j for j in range(i % 4 + 1)]

    def point(i: int) -> Optional[Dict[str, Any]]:
        if i % 13 == 4:
            return None
        return {"x": None if i % 6 == 1 else i * 1.5, "y": i, "label": None if i % 4 == 0 else f"p{i}"}

    def deep(i: int) -> Optional[Dict[str, Any]]:
        if i % 17 == 8:
            return None
        inner = None if i % 9 == 2 else {
            "level3": None if i % 8 == 1 else {"leaf": i, "tags": ints(i), "level4": {"value": f"v{i}", "n": i % 7}},
            "flag": i % 2 == 0,
        }
        return {"level2": inner, "count": i}

    def scores(i: int) -> Optional[List[Any]]:
        if i % 10 == 9:
            return None
        return [("k" + str(j), None if (i + j) % 6 == 0 else i * j) for j in range(i % 3)]

    def items(i: int) -> Optional[List[Optional[Dict[str, Any]]]]:
        if i % 12 == 7:
            return None
        return [None if j == 1 else {"sku": f"s{i}-{j}", "qty": j, "parts": ints(i + j)} for j in range(i % 3 + 1)]

    def lookup(i: int) -> Optional[List[Any]]:
        if i % 8 == 3:
            return None
        return [(f"key{j}", None if j == 2 else [i, j, None]) for j in range(i % 4)]

    return {"id": list(range(rows)), "ints": [ints(i) for i in range(rows)], "point": [point(i) for i in range(rows)],
            "deep": [deep(i) for i in range(rows)], "scores": [scores(i) for i in range(rows)],
            "items": [items(i) for i in range(rows)], "lookup": [lookup(i) for i in range(rows)],
            "matrix": [None if i % 9 == 4 else [ints(i), ints(i + 1)] for i in range(rows)]}


# ---- pyarrow ----

def pyarrow_cases(cases: Cases, pa: Any, pq: Any) -> None:
    data = values()
    nested = nested_values()
    major = int(pa.__version__.split(".")[0])

    def base_table() -> Any:
        return pa.table({
            "id": pa.array(data["id"], pa.int64()), "flag": pa.array(data["flag"], pa.bool_()),
            "small": pa.array(data["small"], pa.int16()), "number": pa.array(data["number"], pa.int32()),
            "big": pa.array(data["big"], pa.int64()), "ratio": pa.array(data["ratio"], pa.float64()),
            "name": pa.array(data["name"], pa.string()), "code": pa.array(data["code"], pa.string()),
            "blob": pa.array(data["blob"], pa.binary()), "day": pa.array(data["day"], pa.date32()),
            "moment": pa.array(data["moment"], pa.timestamp("us")),
            "amount": pa.array(data["amount"], pa.decimal128(12, 2)),
        })

    def write(table_maker: Callable[[], Any], **options: Any) -> Callable[[Path], None]:
        def run(target: Path) -> None:
            # Into memory first: a writer that fails keeps its file open on Windows, and pyarrow 8 keeps it after a
            # collection too, so the case could not be removed.
            sink = pa.BufferOutputStream()
            pq.write_table(table_maker(), sink, **options)
            target.write_bytes(sink.getvalue().to_pybytes())
        return run

    base = base_table
    cases.case("base", write(base), **{})
    for codec in ("NONE", "SNAPPY", "GZIP", "BROTLI", "LZ4", "ZSTD", "LZO"):
        cases.case(f"codec-{codec.lower()}", write(base, compression=codec), compression=codec)
    cases.case("dictionary-off", write(base, use_dictionary=False), use_dictionary=False)
    cases.case("dictionary-some", write(base, use_dictionary=["name", "code"]), use_dictionary=["name", "code"])
    cases.case("dictionary-tiny-page", write(base, dictionary_pagesize_limit=64), dictionary_pagesize_limit=64)
    cases.case("page-v2", write(base, data_page_version="2.0"), data_page_version="2.0")
    cases.case("page-v2-zstd", write(base, data_page_version="2.0", compression="ZSTD"),
               data_page_version="2.0", compression="ZSTD")
    cases.case("statistics-off", write(base, write_statistics=False), write_statistics=False)
    cases.case("statistics-some", write(base, write_statistics=["id", "name"]), write_statistics=["id", "name"])
    for version in ("1.0", "2.4", "2.6"):
        cases.case(f"version-{version}", write(base, version=version), version=version)
    cases.case("byte-stream-split", write(base, use_dictionary=False, use_byte_stream_split=["ratio", "number", "big"]),
               use_dictionary=False, use_byte_stream_split="ratio, number, big")
    cases.case("encodings", write(base, use_dictionary=False, column_encoding={
        "id": "DELTA_BINARY_PACKED", "number": "DELTA_BINARY_PACKED", "name": "DELTA_LENGTH_BYTE_ARRAY",
        "code": "DELTA_BYTE_ARRAY", "flag": "RLE", "ratio": "BYTE_STREAM_SPLIT"}), column_encoding="mixed")
    cases.case("page-index", write(base, write_page_index=True), write_page_index=True)
    cases.case("page-checksum", write(base, write_page_checksum=True), write_page_checksum=True)
    cases.case("bloom-filter", write(base, bloom_filter_options={"id": {"ndv": 100, "fpp": 0.05}, "name": True}),
               bloom_filter_options="id, name")
    def sorted_by(target: Path) -> None:
        sink = pa.BufferOutputStream()
        pq.write_table(base(), sink, sorting_columns=[pq.SortingColumn(0), pq.SortingColumn(4, True, True)])
        target.write_bytes(sink.getvalue().to_pybytes())

    cases.case("sorting-columns", sorted_by, sorting_columns="id asc, big desc nulls first")
    cases.case("int96", write(base, use_deprecated_int96_timestamps=True), use_deprecated_int96_timestamps=True)
    cases.case("coerce-ms", write(base, coerce_timestamps="ms", allow_truncated_timestamps=True), coerce_timestamps="ms")
    cases.case("decimal-as-integer", write(base, store_decimal_as_integer=True), store_decimal_as_integer=True)
    cases.case("no-arrow-schema", write(base, store_schema=False), store_schema=False)
    cases.case("row-groups-7", write(base, row_group_size=7), row_group_size=7)
    cases.case("row-groups-1", write(lambda: pa.table({"id": pa.array(range(3000), pa.int32()),
                                                        "name": pa.array([f"n{i % 10}" for i in range(3000)])}),
                                     row_group_size=1), row_group_size=1, rows=3000)
    cases.case("rows-per-page-5", write(base, max_rows_per_page=5), max_rows_per_page=5)
    cases.case("batch-size-3", write(base, write_batch_size=3, data_page_size=64), write_batch_size=3,
               data_page_size=64)
    cases.case("content-defined-chunking", write(base, use_content_defined_chunking=True),
               use_content_defined_chunking=True)
    cases.case("empty", write(lambda: base().slice(0, 0)), rows=0)
    cases.case("wide-1500", write(lambda: pa.table({f"c{i:04d}": pa.array([i, None, i + 1], pa.int32())
                                                      for i in range(1500)})), columns=1500)
    cases.case("long-names", write(lambda: pa.table({("n" * 300) + str(i): pa.array([1, 2], pa.int8())
                                                       for i in range(3)})), name_length=301)
    cases.case("unicode-names", write(lambda: pa.table({"ünïcødé": [1, 2], "名前": ["a", "b"], "a.b c": [1.5, None],
                                                          "emoji 😀": [True, False]})))
    cases.case("key-value-metadata", write(lambda: base().replace_schema_metadata(
        {"com.microsoft.parquet.vorder.enabled": "true", "com.microsoft.parquet.vorder.level": "700",
         "org.apache.spark.version": "3.5.1", "my.string_prefix_length": "12", "secret": "x" * 500})))

    def types_table() -> Any:
        columns: Dict[str, Any] = {
            "int8": pa.array([1, None, -3], pa.int8()), "int16": pa.array([1, None, -3], pa.int16()),
            "int32": pa.array([1, None, -3], pa.int32()), "int64": pa.array([1, None, -3], pa.int64()),
            "uint8": pa.array([1, None, 255], pa.uint8()), "uint16": pa.array([1, None, 65535], pa.uint16()),
            "uint32": pa.array([1, None, 4294967295], pa.uint32()),
            "uint64": pa.array([1, None, 18446744073709551615], pa.uint64()),
            "float32": pa.array([1.5, None, -0.0], pa.float32()), "float64": pa.array([1e300, None, float("nan")]),
            "bool": pa.array([True, None, False]), "string": pa.array(["a", None, "ccc"]),
            "large_string": pa.array(["a", None, "c"], pa.large_string()),
            "binary": pa.array([b"\x00", None, b"\xff"]), "large_binary": pa.array([b"a", None, b"c"], pa.large_binary()),
            "fixed16": pa.array([b"0123456789abcdef", None, b"fedcba9876543210"], pa.binary(16)),
            "date32": pa.array([datetime.date(2024, 2, 29), None, datetime.date(1, 1, 1)], pa.date32()),
            "date64": pa.array([datetime.date(2024, 2, 29), None, datetime.date(1970, 1, 1)], pa.date64()),
            "time32ms": pa.array([datetime.time(1, 2, 3, 4000), None, datetime.time(23, 59, 59)], pa.time32("ms")),
            "time64us": pa.array([datetime.time(1, 2, 3, 4), None, datetime.time(0)], pa.time64("us")),
            "time64ns": pa.array([1, None, 86399999999999], pa.time64("ns")),
            "ts_s": pa.array([0, None, 1700000000], pa.timestamp("s")),
            "ts_ms": pa.array([0, None, 1700000000000], pa.timestamp("ms")),
            "ts_us_utc": pa.array([0, None, 1700000000000000], pa.timestamp("us", tz="UTC")),
            "ts_ns": pa.array([0, None, 1700000000000000000], pa.timestamp("ns")),
            "ts_ns_tz": pa.array([0, None, -1000], pa.timestamp("ns", tz="Europe/Rome")),
            "duration": pa.array([1, None, -5], pa.duration("ms")),
            "dec4": pa.array([decimal.Decimal("1.5"), None, decimal.Decimal("-99.9")], pa.decimal128(4, 1)),
            "dec9": pa.array([decimal.Decimal("1234567.89"), None, decimal.Decimal("-0.01")], pa.decimal128(9, 2)),
            "dec18": pa.array([decimal.Decimal("123456789012345.678"), None, decimal.Decimal("0")],
                              pa.decimal128(18, 3)),
            "dec38": pa.array([decimal.Decimal("1" * 28 + ".0123456789"), None, decimal.Decimal("-1")],
                              pa.decimal128(38, 10)),
            "dec76": pa.array([decimal.Decimal("9" * 56 + "." + "1" * 20), None, decimal.Decimal("1")],
                              pa.decimal256(76, 20)),
            "dictionary": pa.array(["x", None, "y"]).dictionary_encode(),
            "null": pa.array([None, None, None], pa.null()),
        }
        if hasattr(pa, "float16") and major >= 15:  # pyarrow writes FLOAT16 from version 15 on
            try:
                import numpy
                columns["float16"] = pa.array(numpy.array([1.5, 0.0, -2.0], dtype=numpy.float16), pa.float16())
            except Exception:  # noqa: BLE001
                pass
        if hasattr(pa, "uuid"):
            columns["uuid"] = pa.array([b"\x01" * 16, None, b"\x02" * 16], pa.uuid())
        if hasattr(pa, "json_"):
            columns["json"] = pa.array(['{"a": 1}', None, "[]"], pa.json_())
        if hasattr(pa, "bool8"):
            columns["bool8"] = pa.array([1, None, 0], pa.bool8())
        return pa.table(columns)

    cases.case("types", write(types_table))
    cases.case("types-v1", write(types_table, version="1.0"), version="1.0")
    cases.case("types-int96", write(types_table, use_deprecated_int96_timestamps=True),
               use_deprecated_int96_timestamps=True)
    cases.case("types-decimal-as-integer", write(types_table, store_decimal_as_integer=True),
               store_decimal_as_integer=True)
    cases.case("types-statistics-off", write(types_table, write_statistics=False), write_statistics=False)

    def nested_table() -> Any:
        point = pa.struct([("x", pa.float64()), ("y", pa.int32()), ("label", pa.string())])
        level4 = pa.struct([("value", pa.string()), ("n", pa.int8())])
        level3 = pa.struct([("leaf", pa.int64()), ("tags", pa.list_(pa.int64())), ("level4", level4)])
        deep = pa.struct([("level2", pa.struct([("level3", level3), ("flag", pa.bool_())])), ("count", pa.int32())])
        item = pa.struct([("sku", pa.string()), ("qty", pa.int16()), ("parts", pa.list_(pa.int64()))])
        return pa.table({
            "id": pa.array(nested["id"], pa.int64()),
            "ints": pa.array(nested["ints"], pa.list_(pa.int64())),
            "point": pa.array(nested["point"], point),
            "deep": pa.array(nested["deep"], deep),
            "scores": pa.array(nested["scores"], pa.map_(pa.string(), pa.int64())),
            "items": pa.array(nested["items"], pa.list_(item)),
            "lookup": pa.array(nested["lookup"], pa.map_(pa.string(), pa.list_(pa.int64()))),
            "matrix": pa.array(nested["matrix"], pa.list_(pa.list_(pa.int64()))),
            "large": pa.array(nested["ints"], pa.large_list(pa.int64())),
            # pyarrow writes a null fixed-size list from version 20 on.
            "fixed": pa.array([None if i % 5 == 0 and major >= 20 else [i, None, i + 2] for i in range(ROWS)],
                              pa.list_(pa.int32(), 3)),
            "notnull": pa.array([[i] for i in range(ROWS)], pa.list_(pa.field("element", pa.int32(), nullable=False))),
        })

    cases.case("nested", write(nested_table))
    cases.case("nested-legacy-names", write(nested_table, use_compliant_nested_type=False),
               use_compliant_nested_type=False)
    cases.case("nested-compliant-names", write(nested_table, use_compliant_nested_type=True),
               use_compliant_nested_type=True)
    cases.case("nested-v1-pages", write(nested_table, data_page_version="1.0", use_dictionary=False),
               data_page_version="1.0", use_dictionary=False)
    cases.case("nested-v2-pages", write(nested_table, data_page_version="2.0"), data_page_version="2.0")
    cases.case("nested-row-groups-9", write(nested_table, row_group_size=9), row_group_size=9)
    cases.case("nested-statistics-off", write(nested_table, write_statistics=False), write_statistics=False)

    def deep_struct(levels: int) -> Callable[[], Any]:
        def make() -> Any:
            kind = pa.int32()
            value: Any = 7
            for level in reversed(range(levels)):
                kind = pa.struct([(f"s{level}", kind)])
                value = {f"s{level}": value}
            return pa.table({"top": pa.array([value, None], kind)})
        return make

    def deep_list(levels: int) -> Callable[[], Any]:
        def make() -> Any:
            kind = pa.int32()
            value: Any = 7
            for _ in range(levels):
                kind = pa.list_(kind)
                value = [value]
            return pa.table({"top": pa.array([value, None], kind)})
        return make

    for levels in (10, 30, 60):
        cases.case(f"deep-struct-{levels}", write(deep_struct(levels)), levels=levels)
    for levels in (10, 30):
        cases.case(f"deep-list-{levels}", write(deep_list(levels)), levels=levels)

    def field_ids() -> Any:
        def field(name: str, kind: Any, field_id: int, nullable: bool = True) -> Any:
            return pa.field(name, kind, nullable, metadata={b"PARQUET:field_id": str(field_id).encode()})
        inner = pa.struct([field("a", pa.int32(), 11), field("b", pa.string(), 12)])
        schema = pa.schema([field("id", pa.int64(), 1), field("s", inner, 2),
                            field("l", pa.list_(field("element", pa.int32(), 31)), 3),
                            field("m", pa.map_(field("key", pa.string(), 41, False), field("value", pa.int32(), 42)), 4)])
        return pa.table({"id": [1, 2], "s": [{"a": 1, "b": "x"}, None], "l": [[1], []],
                         "m": [[("k", 1)], None]}, schema=schema)

    cases.case("field-ids", write(field_ids))

    def encrypted(footer_plaintext: bool) -> Callable[[Path], None]:
        def run(target: Path) -> None:
            import base64

            import pyarrow.parquet.encryption as pe

            class Kms(pe.KmsClient):
                def __init__(self, _config: Any) -> None:
                    pe.KmsClient.__init__(self)

                def wrap_key(self, key_bytes: bytes, master_key_identifier: str) -> bytes:
                    return base64.b64encode(bytes(b ^ 0x5A for b in key_bytes))

                def unwrap_key(self, wrapped_key: bytes, master_key_identifier: str) -> bytes:
                    return bytes(b ^ 0x5A for b in base64.b64decode(wrapped_key))

            factory = pe.CryptoFactory(lambda config: Kms(config))
            kms = pe.KmsConnectionConfig(custom_kms_conf={"footer": "0123456789012345", "columns": "1234567890123450"})
            configuration = pe.EncryptionConfiguration(footer_key="footer", column_keys={"columns": ["name", "amount"]},
                                                       plaintext_footer=footer_plaintext,
                                                       encryption_algorithm="AES_GCM_V1", double_wrapping=False)
            properties = factory.file_encryption_properties(kms, configuration)
            sink = pa.BufferOutputStream()
            with pq.ParquetWriter(sink, base().schema, encryption_properties=properties) as writer:
                writer.write_table(base())
            target.write_bytes(sink.getvalue().to_pybytes())
        return run

    cases.case("encrypted-footer", encrypted(False), plaintext_footer=False)
    cases.case("encrypted-columns", encrypted(True), plaintext_footer=True)
    cases.case("metadata-file", lambda target: write_metadata(pa, pq, target, base()))


def write_metadata(pa: Any, pq: Any, target: Path, table: Any) -> None:
    """A _metadata file, as a dataset writes it, whose column chunks name their data file in file_path."""
    collector: List[Any] = []
    pq.write_table(table, str(target.parent / "data.parquet"), metadata_collector=collector)
    collector[-1].set_file_path("data.parquet")
    pq.write_metadata(table.schema, str(target), metadata_collector=collector)


# ---- DuckDB ----

def duckdb_cases(cases: Cases, duckdb: Any) -> None:
    def copy(select: str, options: str = "") -> Callable[[Path], None]:
        def run(target: Path) -> None:
            connection = duckdb.connect()
            try:
                extra = f", {options}" if options else ""
                connection.execute(f"COPY ({select}) TO '{target.as_posix()}' (FORMAT PARQUET{extra})")
            finally:
                connection.close()
        return run

    base = ("SELECT i AS id, i % 3 = 0 AS flag, (i % 200 - 100)::SMALLINT AS small, i * 1000003 AS number, "
            "CASE WHEN i % 7 = 3 THEN NULL ELSE 'name ' || (i % 9) END AS name, "
            "DATE '2020-01-01' + i::INTEGER AS day, TIMESTAMP '2021-03-04 05:06:07' + INTERVAL (i) HOUR AS moment, "
            "(i * 1234 - 30000)::DECIMAL(12,2) / 100 AS amount, i / 7.0 AS ratio FROM range(60) t(i)")
    cases.case("base", copy(base))
    for codec in ("uncompressed", "snappy", "gzip", "zstd", "lz4", "lz4_raw", "brotli"):
        cases.case(f"codec-{codec}", copy(base, f"COMPRESSION {codec}"), compression=codec)
    cases.case("row-groups-7", copy(base, "ROW_GROUP_SIZE 7"), row_group_size=7)
    cases.case("page-v2", copy(base, "PARQUET_VERSION V2"), parquet_version="V2")
    cases.case("field-ids", copy("SELECT 1 AS a, {'x': 2, 'y': [3]} AS s",
                                 "FIELD_IDS {a: 10, s: {__duckdb_field_id: 20, x: 21, y: {__duckdb_field_id: 22}}}"))
    cases.case("kv-metadata", copy(base, "KV_METADATA {vorder: 'true', 'org.apache.spark.version': '3.5.1'}"))
    cases.case("dictionary-off", copy(base, "DICTIONARY_COMPRESSION_RATIO_THRESHOLD -1"),
               dictionary_compression_ratio_threshold=-1)
    cases.case("types", copy(
        "SELECT 1::TINYINT a, 2::SMALLINT b, 3::INTEGER c, 4::BIGINT d, 5::HUGEINT e, 6::UTINYINT f, 7::USMALLINT g, "
        "8::UINTEGER h, 9::UBIGINT i, 1.5::FLOAT j, 2.5::DOUBLE k, 1.5::DECIMAL(4,1) l, 2.5::DECIMAL(9,2) m, "
        "3.5::DECIMAL(18,3) n, 4.5::DECIMAL(38,10) o, 'x'::VARCHAR p, '\\x00\\xff'::BLOB q, DATE '2024-02-29' r, "
        "TIME '01:02:03' s, TIMESTAMP '2024-01-01 01:02:03.456789' t, TIMESTAMPTZ '2024-01-01 01:02:03+00' u, "
        "TIMESTAMP_MS '2024-01-01 01:02:03.456' v, TIMESTAMP_NS '2024-01-01 01:02:03.456789123' w, "
        "TIMESTAMP_S '2024-01-01 01:02:03' x, INTERVAL 3 DAY y, "
        "'00000000-0000-0000-0000-000000000001'::UUID uu, true bo"))
    cases.case("json", copy("SELECT '{\"a\": 1}'::JSON j"))
    cases.case("enum", copy("SELECT (['a','b','c'][i % 3 + 1])::ENUM('a','b','c') e FROM range(10) t(i)"))
    cases.case("nested", copy(
        "SELECT i AS id, CASE WHEN i % 11 = 5 THEN NULL ELSE [i, NULL, i + 2] END AS ints, "
        "CASE WHEN i % 13 = 4 THEN NULL ELSE {'x': i * 1.5, 'y': i, 'label': CASE WHEN i % 4 = 0 THEN NULL ELSE "
        "'p' || i END} END AS point, "
        "{'level2': {'level3': {'leaf': i, 'tags': [i, i + 1], 'level4': {'value': 'v' || i}}, 'flag': i % 2 = 0}, "
        "'count': i} AS deep, "
        "CASE WHEN i % 10 = 9 THEN NULL ELSE MAP {'k0': i, 'k1': NULL} END AS scores, "
        "[{'sku': 's' || i, 'qty': 1, 'parts': [i]}, NULL] AS items, "
        "MAP {'key0': [i, NULL]} AS lookup, [[i], [], NULL] AS matrix, [i, i + 1, i + 2]::INTEGER[3] AS fixed "
        "FROM range(60) t(i)"))
    cases.case("union", copy("SELECT union_value(num := 2)::UNION(num INTEGER, str VARCHAR) u"))
    cases.case("empty", copy(base + " LIMIT 0"), rows=0)
    cases.case("wide-1200", copy("SELECT " + ", ".join(f"{i}::INTEGER c{i:04d}" for i in range(1200))), columns=1200)
    cases.case("row-groups-many", copy("SELECT i AS id FROM range(20000) t(i)", "ROW_GROUP_SIZE 10"),
               row_group_size=10, rows=20000)


# ---- polars ----

def polars_cases(cases: Cases, pl: Any) -> None:
    data = values()
    nested = nested_values()

    def frame() -> Any:
        return pl.DataFrame({
            "id": data["id"], "flag": data["flag"], "small": pl.Series(data["small"], dtype=pl.Int16),
            "number": data["number"], "big": data["big"], "ratio": data["ratio"], "name": data["name"],
            "code": pl.Series(data["code"], dtype=pl.Categorical), "blob": data["blob"], "day": data["day"],
            "moment": data["moment"],
            "amount": pl.Series(data["amount"], dtype=pl.Decimal(12, 2)),
        })

    def write(make: Callable[[], Any], **options: Any) -> Callable[[Path], None]:
        def run(target: Path) -> None:
            make().write_parquet(str(target), **options)
        return run

    cases.case("base", write(frame))
    for codec in ("uncompressed", "snappy", "gzip", "lzo", "brotli", "lz4", "zstd"):
        cases.case(f"codec-{codec}", write(frame, compression=codec), compression=codec)
    cases.case("statistics-off", write(frame, statistics=False), statistics=False)
    cases.case("statistics-full", write(frame, statistics="full"), statistics="full")
    cases.case("row-groups-7", write(frame, row_group_size=7), row_group_size=7)
    cases.case("page-64", write(frame, data_page_size=64), data_page_size=64)
    cases.case("pyarrow", write(frame, use_pyarrow=True), use_pyarrow=True)

    def nested_frame() -> Any:
        return pl.DataFrame({
            "id": nested["id"], "ints": nested["ints"], "point": nested["point"], "deep": nested["deep"],
            "items": nested["items"], "matrix": nested["matrix"],
            "fixed": pl.Series([None if i % 5 == 0 else [i, None, i + 2] for i in range(ROWS)],
                               dtype=pl.Array(pl.Int32, 3)),
        })

    cases.case("nested", write(nested_frame))
    cases.case("nested-row-groups-9", write(nested_frame, row_group_size=9), row_group_size=9)
    cases.case("types", write(lambda: pl.DataFrame({
        "i8": pl.Series([1, None], dtype=pl.Int8), "u8": pl.Series([1, 255], dtype=pl.UInt8),
        "u16": pl.Series([1, 65535], dtype=pl.UInt16), "u32": pl.Series([1, 4294967295], dtype=pl.UInt32),
        "u64": pl.Series([1, 18446744073709551615], dtype=pl.UInt64), "f32": pl.Series([1.5, None], dtype=pl.Float32),
        "time": [datetime.time(1, 2, 3), None], "duration": [datetime.timedelta(seconds=3), None],
        "datetime_ms": pl.Series([datetime.datetime(2024, 1, 1), None], dtype=pl.Datetime("ms")),
        "datetime_ns_utc": pl.Series([datetime.datetime(2024, 1, 1), None], dtype=pl.Datetime("ns", "UTC")),
        "null": pl.Series([None, None], dtype=pl.Null), "enum": pl.Series(["a", "b"], dtype=pl.Enum(["a", "b"])),
        "binary": [b"\x00", None]})))
    cases.case("empty", write(lambda: frame().head(0)), rows=0)


# ---- fastparquet ----

def fastparquet_cases(cases: Cases, fastparquet: Any) -> None:
    import pandas

    writer = module("fastparquet.writer")
    bson = module("bson")
    if writer is not None and bson is not None and hasattr(bson, "BSON"):
        # The BSON of pymongo encodes to a subclass of bytes, which the encoder of fastparquet refuses: the same bytes.
        writer.tobson = lambda value: bytes(bson.BSON.encode(value))

    data = values()

    def frame() -> Any:
        return pandas.DataFrame({
            "id": data["id"], "flag": pandas.array(data["flag"], dtype="boolean"),
            "small": pandas.array(data["small"], dtype="Int16"), "number": pandas.array(data["number"], dtype="Int64"),
            "ratio": data["ratio"], "name": data["name"], "code": pandas.Categorical(data["code"]),
            "blob": data["blob"], "moment": pandas.to_datetime(data["moment"]),
        })

    def write(make: Callable[[], Any], **options: Any) -> Callable[[Path], None]:
        def run(target: Path) -> None:
            fastparquet.write(str(target), make(), **options)
        return run

    cases.case("base", write(frame))
    for codec in ("UNCOMPRESSED", "SNAPPY", "GZIP", "BROTLI", "LZ4", "ZSTD", "LZ4_RAW", "LZO"):
        cases.case(f"codec-{codec.lower()}", write(frame, compression=codec), compression=codec)
    cases.case("int96", write(frame, times="int96"), times="int96")
    cases.case("statistics-off", write(frame, stats=False), stats=False)
    cases.case("row-groups-7", write(frame, row_group_offsets=7), row_group_offsets=7)
    cases.case("object-encodings", write(lambda: pandas.DataFrame({
        "u": ["a", None, "c"], "j": [{"a": 1}, None, [1, 2]], "b": [{"x": 1}, {"z": "t"}, {"y": [1]}],
        "raw": [b"\x00", None, b"\xff"]}), object_encoding={"u": "utf8", "j": "json", "b": "bson", "raw": "bytes"}),
        object_encoding="utf8, json, bson, bytes")
    cases.case("nulls-off", write(lambda: pandas.DataFrame({"id": data["id"], "ratio": [i / 3 for i in data["id"]]}),
                                   has_nulls=False), has_nulls=False)
    cases.case("empty", write(lambda: frame().head(0)), rows=0)


# ---- datafusion, the writer of arrow-rs ----

def datafusion_cases(cases: Cases, datafusion: Any) -> None:
    # The table function range() is not in the older versions: the rows are a list of values.
    rows = "(VALUES " + ", ".join(f"({i})" for i in range(ROWS)) + ") AS t(value)"

    def copy(select: str, options: str = "") -> Callable[[Path], None]:
        def run(target: Path) -> None:
            context = datafusion.SessionContext()
            extra = f" OPTIONS ({options})" if options else ""
            context.sql(f"COPY ({select}) TO '{target.as_posix()}' STORED AS PARQUET{extra}").collect()
        return run

    base = ("SELECT value AS id, value % 3 = 0 AS flag, CAST(value * 1000003 AS INT) AS number, "
            "CASE WHEN value % 7 = 3 THEN NULL ELSE CONCAT('name ', CAST(value % 9 AS VARCHAR)) END AS name, "
            "CAST(value AS DOUBLE) / 7.0 AS ratio, CAST(value * 1234 AS DECIMAL(12,2)) AS amount, "
            "to_timestamp_micros(value * 3600000000) AS moment FROM " + rows)
    cases.case("base", copy(base))
    for codec in ("uncompressed", "snappy", "gzip(6)", "brotli(4)", "lz4", "lz4_raw", "zstd(3)"):
        cases.case(f"codec-{codec.split('(')[0]}", copy(base, f"'format.compression' '{codec}'"), compression=codec)
    cases.case("writer-v2", copy(base, "'format.writer_version' '2.0'"), writer_version="2.0")
    cases.case("statistics-none", copy(base, "'format.statistics_enabled' 'none'"), statistics_enabled="none")
    cases.case("statistics-chunk", copy(base, "'format.statistics_enabled' 'chunk'"), statistics_enabled="chunk")
    cases.case("bloom-filter", copy(base, "'format.bloom_filter_on_write' 'true'"), bloom_filter_on_write=True)
    cases.case("dictionary-off", copy(base, "'format.dictionary_enabled' 'false'"), dictionary_enabled=False)
    cases.case("row-groups-7", copy(base, "'format.max_row_group_size' '7'"), max_row_group_size=7)
    cases.case("nested", copy(
        "SELECT value AS id, CASE WHEN value % 11 = 5 THEN NULL ELSE make_array(value, value + 1) END AS ints, "
        "named_struct('x', CAST(value AS DOUBLE), 'y', value, 'inner', named_struct('z', make_array("
        "named_struct('w', value)), 'label', CONCAT('p', CAST(value AS VARCHAR)))) AS point, "
        "map(['k0', 'k1'], [value, value + 1]) AS scores, "
        "make_array(make_array(value), make_array(value, value)) AS matrix FROM " + rows))


# ---- deltalake, the writer of delta-rs ----

def deltalake_cases(cases: Cases, deltalake: Any, pa: Any) -> None:
    nested = nested_values()

    def table() -> Any:
        point = pa.struct([("x", pa.float64()), ("y", pa.int32()), ("label", pa.string())])
        item = pa.struct([("sku", pa.string()), ("qty", pa.int16()), ("parts", pa.list_(pa.int64()))])
        return pa.table({"id": pa.array(nested["id"], pa.int64()),
                         "ints": pa.array(nested["ints"], pa.list_(pa.int64())),
                         "point": pa.array(nested["point"], point),
                         "scores": pa.array(nested["scores"], pa.map_(pa.string(), pa.int64())),
                         "items": pa.array(nested["items"], pa.list_(item))})

    def write(configuration: Optional[Dict[str, str]] = None) -> Callable[[Path], str]:
        def run(target: Path) -> str:
            folder = target.parent
            deltalake.write_deltalake(str(folder), table(), configuration=configuration)
            names = sorted(path.relative_to(folder).as_posix() for path in folder.rglob("*.parquet"))
            return names[0]
        return run

    cases.case("nested", write())
    cases.case("nested-column-mapping-name", write({"delta.columnMapping.mode": "name",
                                                     "delta.minReaderVersion": "2", "delta.minWriterVersion": "5"}),
               column_mapping="name")


# ---- Parquet.Net, through the .NET SDK ----

DOTNET_WRITER = r"""#:package Parquet.Net@__VERSION__
#:property DefineConstants=__DEFINE__
#:property JsonSerializerIsReflectionEnabledByDefault=true
using System.Text.Json;
using Parquet;
using Parquet.Serialization;
using Parquet.Serialization.Attributes;

// The cases of Parquet.Net: writes <root>/<case>/part-0.parquet and <root>/cases.json.
string root = args[0];
var cases = new List<Dictionary<string, object?>>();
var rows = Enumerable.Range(0, 60).Select(Row.Make).ToList();
var nested = Enumerable.Range(0, 60).Select(NestedRow.Make).ToList();

await Case("base", rows, CompressionMethod.Snappy, null, true);
foreach (CompressionMethod codec in Enum.GetValues<CompressionMethod>())
{
    await Case("codec-" + codec.ToString().ToLowerInvariant(), rows, codec, null, true);
}

await Case("row-groups-7", rows, CompressionMethod.Snappy, 7, true);
await Case("dictionary-off", rows, CompressionMethod.Snappy, null, false);
await Case("nested", nested, CompressionMethod.Snappy, null, true);
await Case("nested-row-groups-9", nested, CompressionMethod.Gzip, 9, true);
await Case("empty", new List<Row>(), CompressionMethod.Snappy, null, true);
File.WriteAllText(Path.Combine(root, "cases.json"), JsonSerializer.Serialize(cases));
return 0;

async Task Case<T>(string name, List<T> data, CompressionMethod codec, int? rowGroupSize, bool dictionary)
{
    string folder = Path.Combine(root, name);
    Directory.CreateDirectory(folder);
    string path = Path.Combine(folder, "part-0.parquet");
    var entry = new Dictionary<string, object?> { ["case"] = name, ["compression"] = codec.ToString(),
        ["row_group_size"] = rowGroupSize, ["dictionary"] = dictionary, ["skipped"] = null };
    try
    {
#if PQN6
        var options = new ParquetOptions { CompressionMethod = codec, DictionaryEncodingThreshold = dictionary ? 0.8 : 0 };
        if (rowGroupSize != null)
        {
            options.RowGroupSize = rowGroupSize.Value;
        }

        await ParquetSerializer.SerializeAsync(data, path, options);
#else
        var options = new ParquetSerializerOptions { CompressionMethod = codec,
            ParquetOptions = new ParquetOptions { UseDictionaryEncoding = dictionary } };
        if (rowGroupSize != null)
        {
            options.RowGroupSize = rowGroupSize.Value;
        }

        await ParquetSerializer.SerializeAsync(data, path, options);
#endif
    }
    catch (Exception e)
    {
        Directory.Delete(folder, true);
        entry["skipped"] = e.GetType().Name + ": " + e.Message.Split('\n')[0];
    }

    cases.Add(entry);
}

public sealed class Row
{
    public int Id { get; set; }
    public bool? Flag { get; set; }
    public short? Small { get; set; }
    public long Big { get; set; }
    public double? Ratio { get; set; }
    public float Single { get; set; }
    public string? Name { get; set; }
    public byte[]? Blob { get; set; }
    public DateTime Moment { get; set; }
    [ParquetTimestamp(ParquetTimestampResolution.Microseconds)]
    public DateTime MomentMicros { get; set; }
    public DateTimeOffset? Offset { get; set; }
    public TimeSpan Elapsed { get; set; }
    public Guid Key { get; set; }
    [ParquetDecimal(18, 4)]
    public decimal Amount { get; set; }
    [ParquetDecimal(38, 10)]
    public decimal? Wide { get; set; }
    public decimal Plain { get; set; }

    public static Row Make(int i) => new Row
    {
        Id = i, Flag = i % 7 == 3 ? null : i % 2 == 0, Small = i % 7 == 3 ? null : (short)(i - 30), Big = i * 9_007_199_254_741L,
        Ratio = i % 5 == 0 ? null : i / 7.0, Single = i * 1.5f, Name = i % 4 == 1 ? null : "name " + (i % 9), Blob = new byte[] { (byte)i, 0 },
        Moment = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddHours(i), MomentMicros = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(i * 10),
        Offset = i % 3 == 0 ? null : new DateTimeOffset(2024, 2, 29, 1, 2, 3, TimeSpan.Zero), Elapsed = TimeSpan.FromMilliseconds(i * 1000),
        Key = new Guid(i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10), Amount = i * 12.3456m, Wide = i % 6 == 0 ? null : i * 1234567890.0123456789m, Plain = i / 3m,
    };
}

public sealed class Point
{
    public double? X { get; set; }
    public int Y { get; set; }
    public string? Label { get; set; }
    public Inner? Inner { get; set; }
}

public sealed class Inner
{
    public long Leaf { get; set; }
    public List<long>? Tags { get; set; }
    public Dictionary<string, int>? Counts { get; set; }
}

public sealed class Item
{
    public string? Sku { get; set; }
    public short Qty { get; set; }
    public List<long>? Parts { get; set; }
}

public sealed class NestedRow
{
    public int Id { get; set; }
    public List<int>? Ints { get; set; }
    public List<string?>? Words { get; set; }
    public Point? Point { get; set; }
    public Dictionary<string, long>? Scores { get; set; }
    public List<Item>? Items { get; set; }
    public Dictionary<string, Point>? Places { get; set; }

    public static NestedRow Make(int i) => new NestedRow
    {
        Id = i,
        Ints = i % 11 == 5 ? null : Enumerable.Range(0, i % 4).ToList(),
        Words = i % 9 == 2 ? null : new List<string?> { "w" + i, i % 3 == 0 ? null : "x" },
        Point = i % 13 == 4 ? null : new Point { X = i % 6 == 1 ? null : i * 1.5, Y = i, Label = "p" + i,
            Inner = i % 5 == 0 ? null : new Inner { Leaf = i, Tags = new List<long> { i, i + 1 }, Counts = new Dictionary<string, int> { ["a"] = i } } },
        Scores = i % 10 == 9 ? null : new Dictionary<string, long> { ["k0"] = i, ["k1"] = i * 2 },
        Items = i % 12 == 7 ? null : Enumerable.Range(0, i % 3 + 1).Select(j => new Item { Sku = "s" + j, Qty = (short)j, Parts = new List<long> { j } }).ToList(),
        Places = i % 8 == 3 ? null : new Dictionary<string, Point> { ["home"] = new Point { X = 1, Y = i, Label = "h" } },
    };
}
"""


def dotnet_cases(root: Path, version: str) -> List[Dict[str, Any]]:
    """The cases of Parquet.Net at a version, written by a file-based app of the .NET SDK."""
    import subprocess
    import tempfile

    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise RuntimeError("dotnet is not on the PATH")
    target = root / "parquet.net"
    target.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="pqax-parquetnet-") as folder:
        source = Path(folder) / "writer.cs"
        define = "PQN6" if int(version.split(".")[0]) >= 6 else "PQN5"
        source.write_text(DOTNET_WRITER.replace("__VERSION__", version).replace("__DEFINE__", define), encoding="utf-8")
        completed = subprocess.run([dotnet, "run", str(source), "--", str(target)], cwd=folder, capture_output=True,
                                   timeout=900)
        if completed.returncode != 0:
            raise RuntimeError("the Parquet.Net writer failed: " + (completed.stdout + completed.stderr)
                               .decode("utf-8", "replace")[-1500:])
    entries = []
    for case in json.loads((target / "cases.json").read_text(encoding="utf-8")):
        name = case.pop("case")
        skipped = case.pop("skipped")
        entries.append({"writer": "parquet.net", "version": version, "case": name,
                        "path": f"parquet.net/{name}/part-0.parquet", "options": case, "skipped": skipped})
    (target / "cases.json").unlink()
    return entries


# ---- pyspark, when a Java runtime is there ----

def pyspark_cases(cases: Cases) -> Optional[str]:
    if not shutil.which("java") and not os.environ.get("JAVA_HOME"):
        return "no Java runtime on the PATH or in JAVA_HOME"
    pyspark = module("pyspark")
    if pyspark is None:
        return "pyspark is not installed"
    from pyspark.sql import SparkSession  # type: ignore

    spark = SparkSession.builder.master("local[1]").appName("pqax-footers").getOrCreate()
    try:
        def write(select: str) -> Callable[[Path], str]:
            def run(target: Path) -> str:
                folder = target.parent / "spark"
                spark.sql(select).coalesce(1).write.parquet(str(folder))
                name = next(name for name in sorted(os.listdir(folder)) if name.endswith(".parquet"))
                shutil.move(str(folder / name), str(target))
                shutil.rmtree(folder)
                return target.name
            return run

        cases.case("nested", write("SELECT id, array(id, id + 1) ints, named_struct('x', id) s, map('k', id) m "
                                   "FROM range(60)"))
    finally:
        spark.stop()
    return None


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", required=True, help="the folder that receives the files")
    parser.add_argument("--label", help="the name of the folder of this environment, by default pyarrow-<version>")
    parser.add_argument("--dotnet", metavar="VERSION",
                        help="write the cases of Parquet.Net at VERSION instead, with the .NET SDK, in DIR/<label>")
    arguments = parser.parse_args(argv)
    if arguments.dotnet:
        label = arguments.label or f"parquet.net-{arguments.dotnet}"
        root = Path(arguments.out).resolve() / label
        entries = dotnet_cases(root, arguments.dotnet)
        index = {"label": label, "python": sys.version.split()[0], "notes": {}, "cases": entries}
        (root / "index.json").write_text(json.dumps(index, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        written = sum(1 for entry in entries if entry["skipped"] is None)
        print(f"{label}: {written} files written, {len(entries) - written} cases skipped")
        for entry in entries:
            if entry["skipped"]:
                print(f"  skipped {entry['writer']}/{entry['case']}: {entry['skipped']}")
        return 0
    pa = module("pyarrow")
    pq = module("pyarrow.parquet") if pa is not None else None
    label = arguments.label or (f"pyarrow-{pa.__version__}" if pa is not None else "no-pyarrow")
    root = Path(arguments.out).resolve() / label
    root.mkdir(parents=True, exist_ok=True)
    entries: List[Dict[str, Any]] = []
    notes: Dict[str, str] = {}
    writers = []
    if pq is not None:
        writers.append(("pyarrow", pa.__version__, lambda c: pyarrow_cases(c, pa, pq)))
    for name, run in (("duckdb", duckdb_cases), ("polars", polars_cases), ("fastparquet", fastparquet_cases),
                      ("datafusion", datafusion_cases)):
        package = module(name)
        if package is None:
            notes[name] = "not installed"
            continue
        writers.append((name, getattr(package, "__version__", "?"), lambda c, run=run, package=package: run(c, package)))
    deltalake = module("deltalake")
    if deltalake is not None and pa is not None:
        writers.append(("deltalake", getattr(deltalake, "__version__", "?"), lambda c: deltalake_cases(c, deltalake, pa)))
    else:
        notes["deltalake"] = "not installed" if deltalake is None else "needs pyarrow"
    for name, version, run in writers:
        cases = Cases(root, name, version)
        try:
            run(cases)
        except Exception:  # noqa: BLE001 - a writer that breaks is reported, and the others go on
            notes[name] = "failed: " + traceback.format_exc().splitlines()[-1]
        entries += cases.entries
    spark = Cases(root, "pyspark", "?")
    reason = pyspark_cases(spark)
    if reason is not None:
        notes["pyspark"] = reason
    entries += spark.entries
    index = {"label": label, "python": sys.version.split()[0], "notes": notes, "cases": entries}
    (root / "index.json").write_text(json.dumps(index, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    written = sum(1 for entry in entries if entry["skipped"] is None)
    print(f"{label}: {written} files written, {len(entries) - written} cases skipped; "
          + "; ".join(f"{name}: {note}" for name, note in notes.items()))
    for entry in entries:
        if entry["skipped"]:
            print(f"  skipped {entry['writer']}/{entry['case']}: {entry['skipped']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
