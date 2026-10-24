"""The Parquet footer: the FileMetaData of parquet.thrift, read from the end of a file and parsed with the standard library.

The parser is a port of the one of the C# collector. It decodes FileMetaData, SchemaElement with its logical and
converted types, RowGroup, ColumnChunk, ColumnMetaData, Statistics, PageEncodingStats, SizeStatistics, the bloom
filter, column index, and offset index lengths, and the key-value metadata. It skips what it does not know, keeps the
value of a footer key only for the keys of an allow-list, and keeps the bounds of the statistics only when asked. On
bad bytes it raises FooterError and nothing else.
"""
from __future__ import annotations

import dataclasses
import math
import struct
import threading
from typing import List, Optional, Tuple

from ._issues import IssueCodes
from ._statistics import format_bound
from ._thrift import (
    BINARY, I32, INTEGERS, LIST, STOP, STRUCT, BOOLEAN_TRUE, FooterError, Reader, invalid, is_boolean,
)

__all__ = [
    "DEFAULT_TAIL_BYTES", "MAX_FOOTER_LENGTH", "MAX_KEPT_VALUE_LENGTH", "FooterError", "FooterReader",
    "FooterTailPlanner", "ParquetColumnChunk", "ParquetFooter", "ParquetKeyValue", "ParquetLeafColumn",
    "ParquetPageEncodingStats", "ParquetRowGroup", "ParquetSchemaElement", "ParquetSortingColumn", "is_kept_key",
    "parse_footer",
]

#: The bytes read from the end of a file in the first request, unless the options say otherwise: 32 KB.
DEFAULT_TAIL_BYTES = 32768
#: The longest footer that the reader accepts: 64 MB.
MAX_FOOTER_LENGTH = 64 * 1024 * 1024
#: The longest value of a footer key that is kept, in UTF-16 code units as in C#.
MAX_KEPT_VALUE_LENGTH = 200

_TYPES = ("BOOLEAN", "INT32", "INT64", "INT96", "FLOAT", "DOUBLE", "BYTE_ARRAY", "FIXED_LEN_BYTE_ARRAY")
_REPETITIONS = ("REQUIRED", "OPTIONAL", "REPEATED")
_CONVERTED_TYPES = (
    "UTF8", "MAP", "MAP_KEY_VALUE", "LIST", "ENUM", "DECIMAL", "DATE", "TIME_MILLIS", "TIME_MICROS", "TIMESTAMP_MILLIS",
    "TIMESTAMP_MICROS", "UINT_8", "UINT_16", "UINT_32", "UINT_64", "INT_8", "INT_16", "INT_32", "INT_64", "JSON", "BSON",
    "INTERVAL",
)
_ENCODINGS = (
    "PLAIN", "GROUP_VAR_INT", "PLAIN_DICTIONARY", "RLE", "BIT_PACKED", "DELTA_BINARY_PACKED", "DELTA_LENGTH_BYTE_ARRAY",
    "DELTA_BYTE_ARRAY", "RLE_DICTIONARY", "BYTE_STREAM_SPLIT", "ALP",
)
_CODECS = ("UNCOMPRESSED", "SNAPPY", "GZIP", "LZO", "BROTLI", "LZ4", "ZSTD", "LZ4_RAW")
_PAGE_TYPES = ("DATA_PAGE", "INDEX_PAGE", "DICTIONARY_PAGE", "DATA_PAGE_V2")
_TIME_UNITS = ("", "MILLIS", "MICROS", "NANOS")
_EDGE_ALGORITHMS = ("SPHERICAL", "VINCENTY", "THOMAS", "ANDOYER", "KARNEY")
_INT32 = struct.Struct("<i")


def _name(names: Tuple[str, ...], value: int) -> str:
    return names[value] if 0 <= value < len(names) else str(value)


def _time_unit(member: int) -> str:
    return _TIME_UNITS[member] if 0 < member < len(_TIME_UNITS) else str(member)


def _utf16_length(text: str) -> int:
    """The length of a string as C# counts it: in UTF-16 code units."""
    return len(text) + sum(1 for c in text if ord(c) > 0xFFFF)


def is_kept_key(key: str) -> bool:
    """Whether the value of a footer key is kept: keys that contain vorder, org.apache.spark.version, and keys that end
    with string_prefix_length, in any case. Other values can hold data values, as the statistics that Fabric copies."""
    lower = key.lower()
    return "vorder" in lower or lower == "org.apache.spark.version" or lower.endswith("string_prefix_length")


@dataclasses.dataclass(frozen=True)
class ParquetKeyValue:
    """A key of the key-value metadata of a footer: the key, the length of its value, and the value when it is kept."""

    key: str
    value_length: Optional[int]
    value: Optional[str]

    def __str__(self) -> str:
        return self.key if self.value is None else f"{self.key}={self.value}"


@dataclasses.dataclass
class ParquetSchemaElement:
    """An element of the schema of a footer."""

    name: str = ""
    type: Optional[str] = None
    type_length: Optional[int] = None
    repetition: Optional[str] = None
    num_children: Optional[int] = None
    converted_type: Optional[str] = None
    scale: Optional[int] = None
    precision: Optional[int] = None
    field_id: Optional[int] = None
    logical_type: Optional[str] = None


@dataclasses.dataclass(frozen=True)
class ParquetLeafColumn:
    """A leaf of the schema, which the column chunks of a row group follow in order."""

    ordinal: int
    path: Tuple[str, ...]
    physical_type: str
    type_length: Optional[int]
    repetition: Optional[str]
    logical_type: Optional[str]
    converted_type: Optional[str]
    scale: Optional[int]
    precision: Optional[int]
    field_id: Optional[int]
    top_level_field_id: Optional[int]

    @property
    def type_annotation(self) -> Optional[str]:
        """The logical type, else the converted type, with the precision and the scale of a decimal."""
        if self.logical_type is not None:
            return self.logical_type
        if self.converted_type == "DECIMAL" and self.precision is not None and self.scale is not None:
            return f"DECIMAL({self.precision},{self.scale})"
        return self.converted_type


@dataclasses.dataclass(frozen=True)
class ParquetSortingColumn:
    """A sorting column of a row group."""

    column_index: int
    descending: bool
    nulls_first: bool


@dataclasses.dataclass(frozen=True)
class ParquetPageEncodingStats:
    """How many pages of a kind use an encoding."""

    page_type: str
    encoding: str
    count: int

    @property
    def is_data_page(self) -> bool:
        return self.page_type == "DATA_PAGE" or self.page_type == "DATA_PAGE_V2"

    @property
    def is_dictionary_encoded(self) -> bool:
        return self.encoding == "PLAIN_DICTIONARY" or self.encoding == "RLE_DICTIONARY"


@dataclasses.dataclass(eq=False)
class ParquetColumnChunk:
    """A column chunk of a row group, with its metadata."""

    has_metadata: bool = False
    is_encrypted: bool = False
    file_path: Optional[str] = None
    physical_type: Optional[str] = None
    codec: Optional[str] = None
    encodings: Tuple[str, ...] = ()
    num_values: int = 0
    total_uncompressed_size: int = 0
    total_compressed_size: int = 0
    data_page_offset: int = 0
    index_page_offset: Optional[int] = None
    dictionary_page_offset: Optional[int] = None
    has_statistics: bool = False
    null_count: Optional[int] = None
    distinct_count: Optional[int] = None
    has_min_max: bool = False
    min_value: Optional[str] = None
    max_value: Optional[str] = None
    encoding_stats: Optional[Tuple[ParquetPageEncodingStats, ...]] = None
    bloom_filter_offset: Optional[int] = None
    bloom_filter_length: Optional[int] = None
    unencoded_byte_array_data_bytes: Optional[int] = None
    offset_index_offset: Optional[int] = None
    offset_index_length: Optional[int] = None
    column_index_offset: Optional[int] = None
    column_index_length: Optional[int] = None

    @property
    def has_dictionary_page(self) -> bool:
        """Whether the chunk has a dictionary page: its offset, the encoding statistics, or a dictionary encoding."""
        if self.dictionary_page_offset is not None and self.dictionary_page_offset > 0:
            return True
        if self.encoding_stats is not None:
            for stats in self.encoding_stats:
                if stats.page_type == "DICTIONARY_PAGE" and stats.count > 0:
                    return True
        return any(encoding == "PLAIN_DICTIONARY" or encoding == "RLE_DICTIONARY" for encoding in self.encodings)

    @property
    def dictionary_page_size(self) -> Optional[int]:
        """The bytes from the dictionary page to the first data page, when the dictionary comes first."""
        dictionary = self.dictionary_page_offset
        if dictionary is not None and 0 < dictionary < self.data_page_offset:
            return self.data_page_offset - dictionary
        return None


@dataclasses.dataclass(eq=False)
class ParquetRowGroup:
    """A row group of a footer."""

    columns: Tuple[ParquetColumnChunk, ...]
    num_rows: int = 0
    total_byte_size: int = 0
    total_compressed_size: Optional[int] = None
    file_offset: Optional[int] = None
    ordinal: Optional[int] = None
    sorting_columns: Optional[Tuple[ParquetSortingColumn, ...]] = None


@dataclasses.dataclass(eq=False)
class ParquetFooter:
    """A parsed footer: the FileMetaData of a Parquet file."""

    version: int
    num_rows: int
    created_by: Optional[str]
    key_value_metadata: Tuple[ParquetKeyValue, ...]
    schema: Tuple[ParquetSchemaElement, ...]
    leaves: Tuple[ParquetLeafColumn, ...]
    row_groups: Tuple[ParquetRowGroup, ...]
    has_encryption_algorithm: bool
    #: The length of the footer in the file, without the 8 bytes after it. Set by the reader.
    footer_length: int = 0
    #: The requests that the reader made: 1, or 2 for a footer longer than the tail. Set by the reader.
    request_count: int = 0

    @property
    def has_encrypted_columns(self) -> bool:
        """Whether a column chunk has encryption metadata."""
        return any(chunk.is_encrypted for group in self.row_groups for chunk in group.columns)


class _RawChunk:
    __slots__ = ("chunk", "min", "max")

    def __init__(self) -> None:
        self.chunk = ParquetColumnChunk()
        self.min: Optional[bytes] = None
        self.max: Optional[bytes] = None


class _RawGroup:
    __slots__ = ("columns", "num_rows", "total_byte_size", "total_compressed_size", "file_offset", "ordinal",
                 "sorting_columns")

    def __init__(self) -> None:
        self.columns: List[_RawChunk] = []
        self.num_rows = 0
        self.total_byte_size = 0
        self.total_compressed_size: Optional[int] = None
        self.file_offset: Optional[int] = None
        self.ordinal: Optional[int] = None
        self.sorting_columns: Optional[Tuple[ParquetSortingColumn, ...]] = None


def parse_footer(data: bytes, include_data_values: bool = False, offset: int = 0, count: int = -1) -> ParquetFooter:
    """Decodes a FileMetaData from the compact protocol: the bytes of the footer, without the length and the magic
    bytes that follow it. A field that this version does not know is skipped, whatever its type, and so is a known
    field with an unexpected wire type. A required field that is missing makes the footer invalid."""
    if count < 0:
        count = len(data) - offset
    if offset < 0 or offset > len(data) - count:
        raise ValueError("The range is outside the buffer.")
    try:
        return _parse(Reader(data, offset, count), include_data_values)
    except RecursionError:
        raise invalid("The footer is nested too deep.") from None


def _parse(reader: Reader, include_data_values: bool) -> ParquetFooter:
    version: Optional[int] = None
    num_rows: Optional[int] = None
    created_by: Optional[str] = None
    schema: Optional[List[ParquetSchemaElement]] = None
    groups: Optional[List[_RawGroup]] = None
    key_values: Optional[List[ParquetKeyValue]] = None
    encryption = False
    reader.enter_struct()
    last = 0
    while True:
        last, kind = reader.field_header(last)
        if kind == STOP:
            break
        if last == 1 and kind == I32:
            version = reader.i32()
        elif last == 2 and kind == LIST:
            schema = _schema(reader)
        elif last == 3 and kind in INTEGERS:
            num_rows = reader.integer(kind)
        elif last == 4 and kind == LIST:
            groups = _row_groups(reader, include_data_values)
        elif last == 5 and kind == LIST:
            key_values = _key_values(reader)
        elif last == 6 and kind == BINARY:
            created_by = reader.string()
        elif last == 8 and kind == STRUCT:
            encryption = True
            reader.skip_struct()
        else:
            # 7 column_orders, 9 footer_signing_key_metadata, and anything newer.
            reader.skip(kind)
    reader.exit_struct()
    if version is None or schema is None or num_rows is None or groups is None:
        raise invalid("The footer lacks a required field: version, schema, num_rows, or row_groups.")
    leaves = _leaves(schema)
    row_groups = []
    for raw in groups:
        if len(raw.columns) != len(leaves):
            raise invalid(f"A row group has {len(raw.columns)} column chunks for {len(leaves)} leaf columns.")
        chunks = []
        for i, raw_chunk in enumerate(raw.columns):
            if include_data_values:
                raw_chunk.chunk.min_value = format_bound(raw_chunk.min, leaves[i])
                raw_chunk.chunk.max_value = format_bound(raw_chunk.max, leaves[i])
            chunks.append(raw_chunk.chunk)
        row_groups.append(ParquetRowGroup(tuple(chunks), raw.num_rows, raw.total_byte_size, raw.total_compressed_size,
                                          raw.file_offset, raw.ordinal, raw.sorting_columns))
    return ParquetFooter(version, num_rows, created_by, tuple(key_values or ()), tuple(schema), tuple(leaves),
                         tuple(row_groups), encryption)


def _schema(reader: Reader) -> List[ParquetSchemaElement]:
    size, element = reader.list_header()
    if element != STRUCT:
        raise invalid("The schema of the footer is not a list of structures.")
    return [_schema_element(reader) for _ in range(size)]


def _schema_element(reader: Reader) -> ParquetSchemaElement:
    element = ParquetSchemaElement()
    has_name = False
    reader.enter_struct()
    last = 0
    while True:
        last, kind = reader.field_header(last)
        if kind == STOP:
            break
        if kind == I32 and last in (1, 2, 3, 5, 6, 7, 8, 9):
            value = reader.i32()
            if last == 1:
                element.type = _name(_TYPES, value)
            elif last == 2:
                element.type_length = value
            elif last == 3:
                element.repetition = _name(_REPETITIONS, value)
            elif last == 5:
                element.num_children = value
            elif last == 6:
                element.converted_type = _name(_CONVERTED_TYPES, value)
            elif last == 7:
                element.scale = value
            elif last == 8:
                element.precision = value
            else:
                element.field_id = value
        elif last == 4 and kind == BINARY:
            element.name = reader.string()
            has_name = True
        elif last == 10 and kind == STRUCT:
            element.logical_type = _logical_type(reader)
        else:
            reader.skip(kind)
    reader.exit_struct()
    if not has_name:
        raise invalid("A schema element of the footer has no name.")
    if element.num_children is not None and element.num_children < 0:
        raise invalid("A schema element of the footer has a negative number of children.")
    return element


_EMPTY_LOGICAL = {1: "STRING", 2: "MAP", 3: "LIST", 4: "ENUM", 6: "DATE", 11: "UNKNOWN", 12: "JSON", 13: "BSON",
                  14: "UUID", 15: "FLOAT16"}


def _logical_type(reader: Reader) -> Optional[str]:
    """The union LogicalType as text, such as DECIMAL(18,2) or TIMESTAMP(MICROS, UTC)."""
    text: Optional[str] = None
    reader.enter_struct()
    last = 0
    while True:
        last, kind = reader.field_header(last)
        if kind == STOP:
            break
        if kind != STRUCT:
            reader.skip(kind)
            continue
        if last in _EMPTY_LOGICAL:
            reader.skip_struct()
            text = _EMPTY_LOGICAL[last]
        elif last == 5:
            text = _decimal(reader)
        elif last == 7:
            text = _time(reader, "TIME")
        elif last == 8:
            text = _time(reader, "TIMESTAMP")
        elif last == 10:
            text = _integer_type(reader)
        elif last == 16:
            text = _variant(reader)
        elif last == 17:
            text = _geo(reader, "GEOMETRY")
        elif last == 18:
            text = _geo(reader, "GEOGRAPHY")
        else:
            reader.skip_struct()
            text = f"LOGICAL({last})"
    reader.exit_struct()
    return text


def _fields(reader: Reader):
    """The fields of a struct, as (id, wire type), entering and leaving the struct around them."""
    reader.enter_struct()
    last = 0
    while True:
        last, kind = reader.field_header(last)
        if kind == STOP:
            break
        yield last, kind
    reader.exit_struct()


def _decimal(reader: Reader) -> str:
    scale: Optional[int] = None
    precision: Optional[int] = None
    for field, kind in _fields(reader):
        if field == 1 and kind == I32:
            scale = reader.i32()
        elif field == 2 and kind == I32:
            precision = reader.i32()
        else:
            reader.skip(kind)
    return f"DECIMAL({'?' if precision is None else precision},{'?' if scale is None else scale})"


def _time(reader: Reader, name: str) -> str:
    utc: Optional[bool] = None
    unit: Optional[str] = None
    for field, kind in _fields(reader):
        if field == 1 and is_boolean(kind):
            utc = kind == BOOLEAN_TRUE
        elif field == 2 and kind == STRUCT:
            unit = _union(reader)
        else:
            reader.skip(kind)
    adjusted = "?" if utc is None else "UTC" if utc else "LOCAL"
    return f"{name}({'?' if unit is None else unit}, {adjusted})"


def _union(reader: Reader) -> Optional[str]:
    """A union of empty structures, such as TimeUnit, as the name of the member that is set."""
    member: Optional[str] = None
    for field, kind in _fields(reader):
        if kind == STRUCT:
            reader.skip_struct()
            member = _time_unit(field)
        else:
            reader.skip(kind)
    return member


def _integer_type(reader: Reader) -> str:
    width: Optional[int] = None
    signed: Optional[bool] = None
    for field, kind in _fields(reader):
        if field == 1 and kind in INTEGERS:
            width = reader.integer(kind)
        elif field == 2 and is_boolean(kind):
            signed = kind == BOOLEAN_TRUE
        else:
            reader.skip(kind)
    sign = "?" if signed is None else "SIGNED" if signed else "UNSIGNED"
    return f"INTEGER({'?' if width is None else width}, {sign})"


def _variant(reader: Reader) -> str:
    specification: Optional[int] = None
    for field, kind in _fields(reader):
        if field == 1 and kind in INTEGERS:
            specification = reader.integer(kind)
        else:
            reader.skip(kind)
    return "VARIANT" if specification is None else f"VARIANT({specification})"


def _geo(reader: Reader, name: str) -> str:
    crs: Optional[str] = None
    algorithm: Optional[str] = None
    for field, kind in _fields(reader):
        if field == 1 and kind == BINARY:
            crs = reader.string()
        elif field == 2 and kind == I32:
            algorithm = _name(_EDGE_ALGORITHMS, reader.i32())
        else:
            reader.skip(kind)
    parameters = [value for value in (crs, algorithm) if value is not None]
    return f"{name}({', '.join(parameters)})" if parameters else name


def _row_groups(reader: Reader, include_data_values: bool) -> List[_RawGroup]:
    size, element = reader.list_header()
    if element != STRUCT:
        raise invalid("The row groups of the footer are not a list of structures.")
    return [_row_group(reader, include_data_values) for _ in range(size)]


def _row_group(reader: Reader, include_data_values: bool) -> _RawGroup:
    group = _RawGroup()
    has_columns = has_rows = has_byte_size = False
    for field, kind in _fields(reader):
        if field == 1 and kind == LIST:
            size, element = reader.list_header()
            if element != STRUCT:
                raise invalid("The column chunks of a row group are not a list of structures.")
            group.columns = [_column_chunk(reader, include_data_values) for _ in range(size)]
            has_columns = True
        elif field == 2 and kind in INTEGERS:
            group.total_byte_size = reader.integer(kind)
            has_byte_size = True
        elif field == 3 and kind in INTEGERS:
            group.num_rows = reader.integer(kind)
            has_rows = True
        elif field == 4 and kind == LIST:
            group.sorting_columns = _sorting_columns(reader)
        elif field == 5 and kind in INTEGERS:
            group.file_offset = reader.integer(kind)
        elif field == 6 and kind in INTEGERS:
            group.total_compressed_size = reader.integer(kind)
        elif field == 7 and kind in INTEGERS:
            group.ordinal = _int32(reader.integer(kind))
        else:
            reader.skip(kind)
    if not (has_columns and has_rows and has_byte_size):
        raise invalid("A row group of the footer lacks its column chunks, its number of rows, or its size.")
    return group


def _int32(value: int) -> int:
    """A value cast to an int, as C# casts a long: the low 32 bits, signed."""
    value &= 0xFFFFFFFF
    return value - 0x100000000 if value >= 0x80000000 else value


def _skip_elements(reader: Reader, element: int, size: int) -> None:
    for _ in range(size):
        reader.skip_element(element)


def _sorting_columns(reader: Reader) -> Optional[Tuple[ParquetSortingColumn, ...]]:
    size, element = reader.list_header()
    if element != STRUCT:
        _skip_elements(reader, element, size)
        return None
    columns = []
    for _ in range(size):
        index, descending, nulls_first = -1, False, False
        for field, kind in _fields(reader):
            if field == 1 and kind == I32:
                index = reader.i32()
            elif field == 2 and is_boolean(kind):
                descending = kind == BOOLEAN_TRUE
            elif field == 3 and is_boolean(kind):
                nulls_first = kind == BOOLEAN_TRUE
            else:
                reader.skip(kind)
        columns.append(ParquetSortingColumn(index, descending, nulls_first))
    return tuple(columns)


def _column_chunk(reader: Reader, include_data_values: bool) -> _RawChunk:
    raw = _RawChunk()
    chunk = raw.chunk
    for field, kind in _fields(reader):
        if field == 1 and kind == BINARY:
            chunk.file_path = reader.string()
        elif field == 3 and kind == STRUCT:
            _column_metadata(reader, raw, include_data_values)
            chunk.has_metadata = True
        elif field == 4 and kind in INTEGERS:
            chunk.offset_index_offset = reader.integer(kind)
        elif field == 5 and kind in INTEGERS:
            chunk.offset_index_length = _int32(reader.integer(kind))
        elif field == 6 and kind in INTEGERS:
            chunk.column_index_offset = reader.integer(kind)
        elif field == 7 and kind in INTEGERS:
            chunk.column_index_length = _int32(reader.integer(kind))
        elif field == 8 or field == 9:
            # crypto_metadata and encrypted_column_metadata: only their presence is kept.
            chunk.is_encrypted = True
            reader.skip(kind)
        else:
            # 2 file_offset, which is deprecated, and anything newer.
            reader.skip(kind)
    return raw


def _column_metadata(reader: Reader, raw: _RawChunk, include_data_values: bool) -> None:
    chunk = raw.chunk
    seen = 0
    for field, kind in _fields(reader):
        if field == 1 and kind == I32:
            chunk.physical_type = _name(_TYPES, reader.i32())
        elif field == 2 and kind == LIST:
            chunk.encodings = _encodings(reader)
        elif field == 4 and kind == I32:
            chunk.codec = _name(_CODECS, reader.i32())
        elif field == 5 and kind in INTEGERS:
            chunk.num_values = reader.integer(kind)
            seen |= 1
        elif field == 6 and kind in INTEGERS:
            chunk.total_uncompressed_size = reader.integer(kind)
            seen |= 2
        elif field == 7 and kind in INTEGERS:
            chunk.total_compressed_size = reader.integer(kind)
            seen |= 4
        elif field == 9 and kind in INTEGERS:
            chunk.data_page_offset = reader.integer(kind)
            seen |= 8
        elif field == 10 and kind in INTEGERS:
            chunk.index_page_offset = reader.integer(kind)
        elif field == 11 and kind in INTEGERS:
            chunk.dictionary_page_offset = reader.integer(kind)
        elif field == 12 and kind == STRUCT:
            _statistics(reader, raw, include_data_values)
            chunk.has_statistics = True
        elif field == 13 and kind == LIST:
            chunk.encoding_stats = _encoding_stats(reader)
        elif field == 14 and kind in INTEGERS:
            chunk.bloom_filter_offset = reader.integer(kind)
        elif field == 15 and kind in INTEGERS:
            chunk.bloom_filter_length = _int32(reader.integer(kind))
        elif field == 16 and kind == STRUCT:
            chunk.unencoded_byte_array_data_bytes = _size_statistics(reader)
        else:
            # 3 path_in_schema, which the schema already gives; 8 key_value_metadata; 17 geospatial_statistics,
            # which are data values; and anything newer.
            reader.skip(kind)
    if seen != 15:
        raise invalid("The metadata of a column chunk lacks num_values, a size, or data_page_offset.")


_ENCODING_LISTS: dict = {}
_ENCODING_LOCK = threading.Lock()


def _encodings(reader: Reader) -> Tuple[str, ...]:
    size, element = reader.list_header()
    if element != I32:
        _skip_elements(reader, element, size)
        return ()
    values = tuple(reader.i32() for _ in range(size))
    shared = _ENCODING_LISTS.get(values)  # a million column chunks use a handful of distinct lists
    if shared is None:
        shared = tuple(_name(_ENCODINGS, value) for value in values)
        with _ENCODING_LOCK:
            if len(_ENCODING_LISTS) < 4096:
                shared = _ENCODING_LISTS.setdefault(values, shared)
    return shared


def _statistics(reader: Reader, raw: _RawChunk, include_data_values: bool) -> None:
    chunk = raw.chunk
    legacy_max: Optional[bytes] = None
    legacy_min: Optional[bytes] = None
    for field, kind in _fields(reader):
        if kind == BINARY and field in (1, 2, 5, 6):
            chunk.has_min_max = True
            if not include_data_values:
                reader.skip_binary()  # a bound is a data value: skipped without being read
            elif field == 1:
                legacy_max = reader.binary()
            elif field == 2:
                legacy_min = reader.binary()
            elif field == 5:
                raw.max = reader.binary()
            else:
                raw.min = reader.binary()
        elif field == 3 and kind in INTEGERS:
            chunk.null_count = reader.integer(kind)
        elif field == 4 and kind in INTEGERS:
            chunk.distinct_count = reader.integer(kind)
        else:
            reader.skip(kind)
    if raw.min is None:
        raw.min = legacy_min
    if raw.max is None:
        raw.max = legacy_max


def _encoding_stats(reader: Reader) -> Optional[Tuple[ParquetPageEncodingStats, ...]]:
    size, element = reader.list_header()
    if element != STRUCT:
        _skip_elements(reader, element, size)
        return None
    stats = []
    for _ in range(size):
        page_type = encoding = count = None
        for field, kind in _fields(reader):
            if field == 1 and kind == I32:
                page_type = reader.i32()
            elif field == 2 and kind == I32:
                encoding = reader.i32()
            elif field == 3 and kind == I32:
                count = reader.i32()
            else:
                reader.skip(kind)
        if page_type is not None and encoding is not None and count is not None:
            stats.append(ParquetPageEncodingStats(_name(_PAGE_TYPES, page_type), _name(_ENCODINGS, encoding), count))
    return tuple(stats)


def _size_statistics(reader: Reader) -> Optional[int]:
    unencoded: Optional[int] = None
    for field, kind in _fields(reader):
        if field == 1 and kind in INTEGERS:
            unencoded = reader.integer(kind)
        else:
            reader.skip(kind)  # 2 and 3: the level histograms, which the model leaves out
    return unencoded


def _key_values(reader: Reader) -> List[ParquetKeyValue]:
    size, element = reader.list_header()
    result: List[ParquetKeyValue] = []
    if element != STRUCT:
        _skip_elements(reader, element, size)
        return result
    for _ in range(size):
        key: Optional[str] = None
        value_length: Optional[int] = None
        value: Optional[str] = None
        for field, kind in _fields(reader):
            if field == 1 and kind == BINARY:
                key = reader.string()
            elif field == 2 and kind == BINARY:
                # The key comes before the value in every writer, as in parquet.thrift. A value of a key that is not
                # on the allow-list, or that comes before its key, is skipped without being read.
                length = reader.binary_length()
                value_length = length
                if key is not None and is_kept_key(key) and length <= MAX_KEPT_VALUE_LENGTH * 4:
                    text = reader.utf8(length)
                    value = text if _utf16_length(text) <= MAX_KEPT_VALUE_LENGTH else None
                else:
                    reader.skip_bytes(length)
            else:
                reader.skip(kind)
        if key is not None:
            result.append(ParquetKeyValue(key, value_length, value))
    return result


def _leaves(schema: List[ParquetSchemaElement]) -> List[ParquetLeafColumn]:
    if not schema:
        raise invalid("The schema of the footer has no root.")
    leaves: List[ParquetLeafColumn] = []
    index = _visit(schema, 1, schema[0].num_children or 0, [], None, leaves, 1)
    if index != len(schema):
        raise invalid("The schema of the footer has elements that its tree does not reach.")
    return leaves


def _visit(schema: List[ParquetSchemaElement], index: int, children: int, path: List[str],
           top_level_field_id: Optional[int], leaves: List[ParquetLeafColumn], depth: int) -> int:
    if depth > 256:
        raise invalid("The schema of the footer is nested too deep.")
    for _ in range(children):
        if index >= len(schema):
            raise invalid("The schema of the footer declares more children than it has elements.")
        element = schema[index]
        index += 1
        field_id = element.field_id if depth == 1 else top_level_field_id
        path.append(element.name)
        if element.num_children is not None:
            index = _visit(schema, index, element.num_children, path, field_id, leaves, depth + 1)
        else:
            leaves.append(ParquetLeafColumn(len(leaves), tuple(path), element.type or "", element.type_length,
                                            element.repetition, element.logical_type, element.converted_type,
                                            element.scale, element.precision, element.field_id, field_id))
        path.pop()
    return index


class FooterReader:
    """Reads the footer of a Parquet file, and nothing else.

    The size of the file comes from the table format, so the end of the file is read in one request: the last
    tail_bytes bytes, which hold the footer in the common case. A second request reads the start of a longer footer.
    Nothing is read from the start of the file.
    """

    def __init__(self, storage, tail_bytes: int = DEFAULT_TAIL_BYTES, include_data_values: bool = False) -> None:
        if storage is None:
            raise TypeError("The storage is missing.")
        if tail_bytes < 12:
            raise ValueError("The tail must hold at least the 12 bytes around a footer.")
        self.storage = storage
        self.tail_bytes = tail_bytes
        self.include_data_values = include_data_values

    def read(self, path: str, file_size: int, tail_bytes: Optional[int] = None) -> ParquetFooter:
        """Reads and parses the footer of a file. Raises FooterError for a footer that cannot be read, and the
        StorageError of the storage when a read fails."""
        tail_bytes = self.tail_bytes if tail_bytes is None else tail_bytes
        if tail_bytes < 12:
            raise ValueError("The tail must hold at least the 12 bytes around a footer.")
        if file_size < 12:
            raise FooterError(IssueCodes.FOOTER_INVALID,
                              f"The file is too small to be a Parquet file: {file_size} bytes.")
        first = min(tail_bytes, file_size)
        tail = self.storage.read_range(path, file_size - first, first)
        if len(tail) != first:
            raise FooterError(IssueCodes.FOOTER_INVALID, "The file is shorter than the size that the table format gives.")
        requests = 1
        if tail[first - 4:first - 1] == b"PAR":
            if tail[first - 1] == ord("E"):
                raise FooterError(IssueCodes.FOOTER_ENCRYPTED, "The footer is encrypted.")
            if tail[first - 1] != ord("1"):
                raise FooterError(IssueCodes.FOOTER_INVALID, "The file does not end with the Parquet magic bytes.")
        else:
            raise FooterError(IssueCodes.FOOTER_INVALID, "The file does not end with the Parquet magic bytes.")
        length = _INT32.unpack_from(tail, first - 8)[0]
        if length <= 0 or length > file_size - 12:
            raise FooterError(IssueCodes.FOOTER_INVALID, f"The length of the footer, {length}, does not fit the file.")
        if length > MAX_FOOTER_LENGTH:
            raise FooterError(IssueCodes.FOOTER_TOO_LARGE,
                              f"The footer takes {length} bytes, more than the limit of {MAX_FOOTER_LENGTH}.")
        if length + 8 <= first:
            buffer, offset = tail, first - 8 - length
        else:
            # The footer starts before the tail: read the missing part, and put the two together.
            missing = length + 8 - first
            head = self.storage.read_range(path, file_size - 8 - length, missing)
            requests += 1
            if len(head) != missing:
                raise FooterError(IssueCodes.FOOTER_INVALID,
                                  "The file is shorter than the size that the table format gives.")
            buffer, offset = bytes(head) + bytes(tail[:length - missing]), 0
        footer = parse_footer(buffer, self.include_data_values, offset, length)
        footer.footer_length = length
        footer.request_count = requests
        return footer


class FooterTailPlanner:
    """Chooses how many bytes to read from the end of each data file of a table.

    The first file is read with the initial tail. The footers of one table have about the same size, so the next files
    are read with the largest footer seen so far times a margin, never below a floor and never above the initial tail.
    """

    def __init__(self, initial_tail_bytes: int, floor_bytes: int, margin: float) -> None:
        if initial_tail_bytes < 12:
            raise ValueError("The tail must hold at least the 12 bytes around a footer.")
        if floor_bytes < 12:
            raise ValueError("The floor must hold at least the 12 bytes around a footer.")
        if not (margin >= 1.0) or math.isinf(margin):
            raise ValueError("The margin must be at least 1.")
        self._lock = threading.Lock()
        self._initial = initial_tail_bytes
        self._floor = min(floor_bytes, initial_tail_bytes)
        self._margin = margin
        self._current = initial_tail_bytes
        self._largest = 0

    @property
    def current(self) -> int:
        """The bytes to read from the end of the next file."""
        with self._lock:
            return self._current

    @property
    def largest_footer_bytes(self) -> int:
        """The longest footer observed, with the 8 bytes that follow it, or 0 before the first observation."""
        with self._lock:
            return self._largest

    def observe(self, footer_length: int) -> None:
        """Records the length of a footer that was read, and adjusts the tail for the files that follow."""
        if footer_length < 0:
            raise ValueError("A footer length is not negative.")
        with self._lock:
            self._largest = max(self._largest, footer_length + 8)
            planned = math.ceil(self._largest * self._margin)
            self._current = int(min(self._initial, max(self._floor, planned)))
