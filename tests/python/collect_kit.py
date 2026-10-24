"""What the tests of the collector, pqax.collect, share: the fixture and its probe dump, synthetic Delta logs, storages
that record or fail requests, a writer of the Thrift compact protocol for footers that the fixture does not have, a
fake OneLake DFS endpoint on 127.0.0.1, and the comparison of two footprints.

The tests are written from the interface note of the Python collector (24 October 2026), the README of the C#
collector, section 4.6 of docs/contracts.md, and the kit; never from the code of pqax.collect. This module imports
pqax.collect only inside the functions that need it, so that it imports on the standard library alone.
"""
from __future__ import annotations

import base64
import datetime
import email.utils
import importlib.util
import io
import json
import os
import pathlib
import struct
import threading
import time
import urllib.parse
import zipfile
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Callable, Dict, Iterable, List, Optional, Sequence, Tuple

import pqax
from pqax_kit import KIT, SAMPLES, load_json

FIXTURE = KIT / "fixtures" / "lakehouse"
DUMP = KIT / "fixtures" / "probe-dump"
INDEX = load_json(DUMP / "index.json")
UTC = datetime.timezone.utc


def has_module(name: str) -> bool:
    try:
        return importlib.util.find_spec(name) is not None
    except (ImportError, ValueError):
        return False


HAS_PYARROW = has_module("pyarrow")
HAS_AZURE_IDENTITY = has_module("azure.identity")


# ---- the fixture and the probe dump ----

@dataclass(frozen=True)
class DumpTable:
    id: str
    item: str
    item_kind: str
    namespace: Tuple[str, ...]
    name: str
    dump: Dict[str, Any]

    @property
    def item_folder(self) -> str:
        return f"{self.item}.{self.item_kind}"

    @property
    def table_path(self) -> str:
        return "/".join((self.item_folder, "Tables", *self.namespace, self.name))

    @property
    def delta(self) -> Dict[str, Any]:
        return self.dump["delta"]

    @property
    def files(self) -> List[Dict[str, Any]]:
        return self.dump["files"]

    @property
    def commits(self) -> List[Dict[str, Any]]:
        return self.dump["commits"]

    @property
    def schemas(self) -> List[Dict[str, Any]]:
        return self.dump["schemas"]

    @property
    def source_kind(self) -> str:
        return "OneLake warehouse" if self.item_kind == "Warehouse" else "OneLake lakehouse"

    def __repr__(self) -> str:
        return self.id


TABLES: List[DumpTable] = [
    DumpTable(entry["id"], entry["item"], entry["itemKind"], tuple(entry["namespace"]), entry["name"],
              load_json(DUMP / f"{entry['id']}.json"))
    for entry in INDEX["tables"]
]
SALES = next(table for table in TABLES if "checkpointVersion" in table.delta)    # the table with a checkpoint
ORDERS = next(table for table in TABLES if "checkpointVersion" not in table.delta)  # JSON commits from version 0


def local_path(path: str) -> pathlib.Path:
    """The file or folder on disk of a path of the fixture storage, which has forward slashes."""
    return FIXTURE.joinpath(*path.split("/"))


def size_of(path: str) -> int:
    return local_path(path).stat().st_size


def file_bytes(path: str) -> bytes:
    return local_path(path).read_bytes()


def all_data_files() -> List[str]:
    """Every Parquet file of the fixture outside a Delta log, relative to its root, sorted."""
    found = []
    for path in FIXTURE.rglob("*.parquet"):
        relative = path.relative_to(FIXTURE).as_posix()
        if "/_delta_log/" not in relative:
            found.append(relative)
    return sorted(found)


def data_files() -> List[str]:
    """The data files that the logs reference: those whose size is the size of a file of the dump."""
    sizes = {file["size"] for table in TABLES for file in table.files}
    return [path for path in all_data_files() if size_of(path) in sizes]


def unreferenced_data_files() -> List[str]:
    return [path for path in all_data_files() if path not in data_files()]


def data_files_of(table: DumpTable) -> List[str]:
    return [path for path in data_files() if path.startswith(table.table_path + "/")]


def dump_file_of_size(size: int) -> Tuple[DumpTable, Dict[str, Any]]:
    """The dump of a data file, found by its size, which is distinct for every file of the fixture."""
    matches = [(table, file) for table in TABLES for file in table.files if file["size"] == size]
    assert len(matches) == 1, f"{len(matches)} files of the dump have {size} bytes"
    return matches[0]


def metadata_bytes(data: bytes) -> bytes:
    """The encoded FileMetaData of a Parquet file: the bytes before its length and the closing magic."""
    length = int.from_bytes(data[-8:-4], "little")
    return data[len(data) - 8 - length:len(data) - 8]


def row_groups(file: Dict[str, Any]) -> List[Dict[str, Any]]:
    return file["footer"]["rowGroups"]


def column_count(group: Dict[str, Any]) -> int:
    return len(group["codec"])


def chunk_sum(file: Dict[str, Any], name: str) -> int:
    return sum(value or 0 for group in row_groups(file) for value in group[name])


def encodings_of(group: Dict[str, Any], index: int) -> List[str]:
    return sorted(group["encodings"][index].split(","))


def key_value(footer: Dict[str, Any], suffix: str) -> Optional[str]:
    for pair in footer.get("keyValues", []):
        if pair["key"].endswith(suffix):
            return pair.get("value")
    return None


def vorder_tag(file: Dict[str, Any]) -> Optional[bool]:
    """What the VORDER tag of the log says: True, False, or None when the file has no such tag."""
    tag = (file.get("tags") or {}).get("VORDER")
    if tag is None:
        return None
    return tag.lower() == "true" if tag.lower() in ("true", "false") else None


def is_vordered(file: Dict[str, Any]) -> bool:
    """Whether a file tag or the footer marks the file as V-Ordered, as the object model defines it."""
    return vorder_tag(file) is True or (key_value(file["footer"], "vorder.enabled") or "").lower() == "true"


def timestamp(milliseconds: int) -> str:
    """A time of the log, in milliseconds since 1970, as the format writes it."""
    moment = datetime.datetime.fromtimestamp(milliseconds // 1000, tz=UTC)
    return moment.strftime("%Y-%m-%dT%H:%M:%S") + f".{milliseconds % 1000:03d}Z"


# ---- synthetic Delta logs ----

EPOCH = 1_700_000_000_000  # the time of version 0, in milliseconds
TABLE_ID = "00000000-0000-4000-8000-000000000001"
SCHEMA_STRING = json.dumps({"type": "struct", "fields": [
    {"name": "id", "type": "long", "nullable": True, "metadata": {}}]})
PARTITIONED_SCHEMA_STRING = json.dumps({"type": "struct", "fields": [
    {"name": "id", "type": "long", "nullable": True, "metadata": {}},
    {"name": "p", "type": "string", "nullable": True, "metadata": {}}]})


def commit_name(version: int) -> str:
    return f"{version:020d}.json"


def checkpoint_name(version: int) -> str:
    return f"{version:020d}.checkpoint.parquet"


def time_of(version: int) -> int:
    """The time that the commit of a version records: one minute after the previous one."""
    return EPOCH + version * 60_000


def protocol(reader: int = 1, writer: int = 2, reader_features: Optional[List[str]] = None,
             writer_features: Optional[List[str]] = None) -> Dict[str, Any]:
    action: Dict[str, Any] = {"minReaderVersion": reader, "minWriterVersion": writer}
    if reader_features is not None:
        action["readerFeatures"] = reader_features
    if writer_features is not None:
        action["writerFeatures"] = writer_features
    return {"protocol": action}


def metadata(schema_string: str = SCHEMA_STRING, partition_columns: Sequence[str] = (),
             configuration: Optional[Dict[str, str]] = None, table_id: str = TABLE_ID) -> Dict[str, Any]:
    return {"metaData": {"id": table_id, "format": {"provider": "parquet", "options": {}},
                         "schemaString": schema_string, "partitionColumns": list(partition_columns),
                         "configuration": dict(configuration or {}), "createdTime": EPOCH}}


def partitioned_metadata() -> Dict[str, Any]:
    """A table with two columns, id and the partition column p, a string."""
    return metadata(PARTITIONED_SCHEMA_STRING, ["p"])


def commit_info(version: int, **extra: Any) -> Dict[str, Any]:
    info = {"timestamp": time_of(version), "operation": "WRITE", "engineInfo": "Test engine", "isBlindAppend": True,
            "operationMetrics": {"numFiles": "0"}}
    info.update(extra)
    return {"commitInfo": info}


_UNSET: Any = object()


def add(path: str, size: Optional[int], rows: Optional[int], modification_time: Optional[int] = EPOCH,
        partition: Any = _UNSET, stats: Any = _UNSET, deletion_vector: Optional[Dict[str, Any]] = None,
        tags: Optional[Dict[str, str]] = None) -> Dict[str, Any]:
    """The add action of a file. A size or a modification time of None is left out of the log; partition is the value
    of the partition column p, and stats replaces the statistics, which hold the rows otherwise."""
    action: Dict[str, Any] = {"path": path, "partitionValues": {} if partition is _UNSET else {"p": partition},
                              "dataChange": True}
    if size is not None:
        action["size"] = size
    if modification_time is not None:
        action["modificationTime"] = modification_time
    if stats is _UNSET:
        if rows is not None:
            action["stats"] = json.dumps({"numRecords": rows})
    elif stats is not None:
        action["stats"] = stats
    if deletion_vector is not None:
        action["deletionVector"] = deletion_vector
    if tags is not None:
        action["tags"] = tags
    return {"add": action}


def remove(path: str, size: Optional[int], deletion_timestamp: Optional[int],
           deletion_vector: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    action: Dict[str, Any] = {"path": path, "dataChange": True}
    if deletion_timestamp is not None:
        action["deletionTimestamp"] = deletion_timestamp
    if size is not None:
        action["size"] = size
    if deletion_vector is not None:
        action["deletionVector"] = deletion_vector
    return {"remove": action}


def deletion_vector(size: int, cardinality: int, path: str = "ab^-aqEH.-t@S}K{vb[*k^") -> Dict[str, Any]:
    return {"storageType": "u", "pathOrInlineDv": path, "offset": 1, "sizeInBytes": size, "cardinality": cardinality}


class Lake:
    """A folder with the layout of a OneLake workspace, for what the fixture does not have."""

    def __init__(self, root: pathlib.Path) -> None:
        self.root = pathlib.Path(root)
        self.root.mkdir(parents=True, exist_ok=True)

    def full(self, path: str) -> pathlib.Path:
        return self.root.joinpath(*path.split("/"))

    def write(self, path: str, content: Any) -> pathlib.Path:
        target = self.full(path)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content.encode("utf-8") if isinstance(content, str) else bytes(content))
        return target

    def commit(self, table: str, version: int, *actions: Dict[str, Any]) -> None:
        """The commit file of a version, one action for each line."""
        self.write(f"{table}/_delta_log/{commit_name(version)}", "".join(json.dumps(a) + "\n" for a in actions))

    def checkpoint(self, table: str, version: int) -> None:
        """A classic checkpoint whose bytes only StateCheckpointReader reads, and the _last_checkpoint that names it."""
        self.write(f"{table}/_delta_log/{checkpoint_name(version)}", "A checkpoint for a test reader.")
        self.write(f"{table}/_delta_log/_last_checkpoint", json.dumps({"version": version, "size": 2}))

    def data(self, table: str, path: str, size: int) -> None:
        """A file of a size under the folder of a table. Its bytes are zeros: level 1 reads no footer."""
        self.write(f"{table}/{path}", bytes(size))


def state_actions() -> List[Any]:
    """What a checkpoint of the synthetic tables holds: the protocol and the metadata, and no file."""
    from pqax.collect import MetadataAction, ProtocolAction
    return [ProtocolAction(min_reader_version=1, min_writer_version=2),
            MetadataAction(id=TABLE_ID, provider="parquet", schema_string=SCHEMA_STRING, partition_columns=[],
                           configuration={}, created_time=EPOCH)]


class StateCheckpointReader:
    """A checkpoint reader for the synthetic tables: whatever the file holds, it gives state_actions(). It records the
    paths that it is asked for."""

    def __init__(self) -> None:
        self.paths: List[str] = []
        self._lock = threading.Lock()

    def __call__(self, storage: Any, path: str) -> List[Any]:
        with self._lock:
            self.paths.append(path)
        return state_actions()


class FailingCheckpointReader:
    def __call__(self, storage: Any, path: str) -> List[Any]:
        raise ValueError("A checkpoint that does not line up.")


# ---- storages for the tests ----

@dataclass(frozen=True)
class Request:
    kind: str  # "list" or "read"
    path: str
    offset: int
    length: int


class RecordingStorage:
    """A storage over another one that records every request, and can replace the content of a file or a range, or
    make a request fail. It has none of list_files, location_of, and identifiers_of, so the collector lists it one
    folder at a time and records no location, as for a storage that wraps one of the two storages."""

    def __init__(self, inner: Any, *, content: Optional[Callable[[str], Optional[bytes]]] = None,
                 ranges: Optional[Callable[[str, int, int], Optional[bytes]]] = None,
                 fail: Optional[Callable[[str, str], Optional[BaseException]]] = None) -> None:
        self.inner = inner
        self.content = content
        self.ranges = ranges
        self.fail = fail
        self._lock = threading.Lock()
        self._requests: List[Request] = []
        self._bytes = 0

    @property
    def requests(self) -> List[Request]:
        with self._lock:
            return list(self._requests)

    @property
    def request_count(self) -> int:
        with self._lock:
            return len(self._requests)

    @property
    def bytes_read(self) -> int:
        with self._lock:
            return self._bytes

    def _arrive(self, kind: str, path: str, offset: int, length: int) -> None:
        with self._lock:
            self._requests.append(Request(kind, path, offset, length))
        if self.fail is not None:
            failure = self.fail(kind, path)
            if failure is not None:
                raise failure

    def _count(self, data: bytes) -> bytes:
        with self._lock:
            self._bytes += len(data)
        return data

    def list(self, path: str) -> List[Any]:
        self._arrive("list", path, 0, 0)
        return self.inner.list(path)

    def read_range(self, path: str, offset: int, length: int) -> bytes:
        self._arrive("read", path, offset, length)
        if self.ranges is not None:
            data = self.ranges(path, offset, length)
            if data is not None:
                return self._count(data)
        if self.content is not None:
            data = self.content(path)
            if data is not None:
                return self._count(data[offset:offset + length] if offset < len(data) else b"")
        return self._count(self.inner.read_range(path, offset, length))

    def reads(self, predicate: Callable[[str], bool] = lambda path: True) -> List[Request]:
        return [request for request in self.requests if request.kind == "read" and predicate(request.path)]


class SizelessStorage:
    """A storage over another one whose listings give no size for the files that a test chooses."""

    def __init__(self, inner: Any, sizeless: Callable[[str], bool]) -> None:
        self.inner = inner
        self.sizeless = sizeless

    @property
    def request_count(self) -> int:
        return self.inner.request_count

    @property
    def bytes_read(self) -> int:
        return self.inner.bytes_read

    def list(self, path: str) -> List[Any]:
        from pqax.collect import StorageEntry
        return [StorageEntry(entry.path, False, None, entry.last_modified)
                if not entry.is_directory and self.sizeless(entry.path) else entry
                for entry in self.inner.list(path)]

    def read_range(self, path: str, offset: int, length: int) -> bytes:
        return self.inner.read_range(path, offset, length)


def virtual_slice(size: int, tail: bytes, offset: int, length: int) -> bytes:
    """A range of a file of any size that is all zeros except its last bytes, cut at the end of the file."""
    if offset >= size:
        return b""
    stop = min(size, offset + length)
    start_of_tail = size - len(tail)
    out = bytearray(stop - offset)
    for position in range(max(offset, start_of_tail), stop):
        out[position - offset] = tail[position - start_of_tail]
    return bytes(out)


class VirtualFileStorage:
    """A storage with one file of any size, all zeros except its last bytes, for files too large to build."""

    def __init__(self, size: int, tail: bytes) -> None:
        self.size = size
        self.tail = tail
        self.reads: List[Tuple[int, int]] = []
        self.bytes_read = 0

    @property
    def request_count(self) -> int:
        return len(self.reads)

    def list(self, path: str) -> List[Any]:
        return []

    def read_range(self, path: str, offset: int, length: int) -> bytes:
        self.reads.append((offset, length))
        data = virtual_slice(self.size, self.tail, offset, length)
        self.bytes_read += len(data)
        return data


# ---- Parquet footers that the fixture does not have ----

class Thrift:
    """Writes the Thrift compact protocol of parquet.thrift. A field header takes the short form when the delta from the
    previous field id is 1 to 15, and the long form otherwise, or always with always_long."""

    TRUE, FALSE, BYTE, I16, I32, I64, DOUBLE, BINARY, LIST, SET, MAP, STRUCT, UUID = range(1, 14)

    def __init__(self, always_long: bool = False) -> None:
        self.out = bytearray()
        self.always_long = always_long
        self._last = 0
        self._outer: List[int] = []

    def varint(self, value: int) -> "Thrift":
        while value >= 0x80:
            self.out.append((value & 0x7F) | 0x80)
            value >>= 7
        self.out.append(value)
        return self

    @staticmethod
    def zigzag(value: int) -> int:
        return (value << 1) ^ (value >> 63)

    def header(self, field: int, kind: int, long_form: bool = False) -> "Thrift":
        delta = field - self._last
        if 0 < delta <= 15 and not (long_form or self.always_long):
            self.out.append((delta << 4) | kind)
        else:
            self.out.append(kind)
            self.varint(self.zigzag(field))
        self._last = field
        return self

    def i16(self, field: int, value: int) -> "Thrift":
        return self.header(field, self.I16).varint(self.zigzag(value))

    def i32(self, field: int, value: int) -> "Thrift":
        return self.header(field, self.I32).varint(self.zigzag(value))

    def i64(self, field: int, value: int) -> "Thrift":
        return self.header(field, self.I64).varint(self.zigzag(value))

    def byte(self, field: int, value: int) -> "Thrift":
        self.header(field, self.BYTE)
        self.out.append(value & 0xFF)
        return self

    def boolean(self, field: int, value: bool) -> "Thrift":
        return self.header(field, self.TRUE if value else self.FALSE)

    def double(self, field: int, value: float) -> "Thrift":
        self.header(field, self.DOUBLE)
        self.out += struct.pack("<d", value)
        return self

    def binary(self, field: int, value: Any) -> "Thrift":
        self.header(field, self.BINARY)
        return self.value_binary(value)

    def uuid(self, field: int, value: bytes) -> "Thrift":
        assert len(value) == 16
        self.header(field, self.UUID)
        self.out += value
        return self

    def value_binary(self, value: Any) -> "Thrift":
        data = value.encode("utf-8") if isinstance(value, str) else bytes(value)
        self.varint(len(data))
        self.out += data
        return self

    def value_i32(self, value: int) -> "Thrift":
        return self.varint(self.zigzag(value))

    def collection_header(self, element: int, count: int) -> "Thrift":
        if count < 15:
            self.out.append((count << 4) | element)
        else:
            self.out.append(0xF0 | element)
            self.varint(count)
        return self

    def list_of(self, field: int, element: int, count: int, kind: int = 9) -> "Thrift":
        """The header of a list (or, with kind SET, a set) field; the elements follow."""
        self.header(field, kind)
        return self.collection_header(element, count)

    def map_of(self, field: int, key: int, value: int, count: int) -> "Thrift":
        self.header(field, self.MAP)
        self.varint(count)
        if count:
            self.out.append((key << 4) | value)
        return self

    def begin(self, field: int) -> "Thrift":
        """Starts a structure field: its field ids start again."""
        self.header(field, self.STRUCT)
        return self.element()

    def element(self) -> "Thrift":
        """Starts a structure that is an element of a list, or the value of a structure field."""
        self._outer.append(self._last)
        self._last = 0
        return self

    def end(self) -> "Thrift":
        """Ends a structure, or the message at the top level, with the stop byte."""
        self.out.append(0)
        self._last = self._outer.pop() if self._outer else 0
        return self

    def raw(self, data: bytes) -> "Thrift":
        self.out += data
        return self

    def bytes(self) -> bytes:
        return bytes(self.out)


# Enumerations of parquet.thrift, as the parser names them.
PHYSICAL_INT64 = 2
REPETITION_OPTIONAL = 1
CONVERTED_INT_64 = 18
ENCODING_PLAIN = 0
ENCODING_RLE = 3
CODEC_ZSTD = 6

Hook = Optional[Callable[[Thrift], Any]]


def footer(*, leaves: Sequence[str] = ("id",), chunks: Optional[int] = None, row_group_count: int = 1,
           key_values: Sequence[Tuple[str, Optional[str]]] = (), created_by: Optional[str] = "Synthetic writer 1.0",
           omit: Iterable[str] = (), replace: Optional[Dict[str, Callable[[Thrift], Any]]] = None,
           extra: Optional[Dict[str, Callable[[Thrift], Any]]] = None, meta_data: bool = True,
           statistics: bool = True, always_long: bool = False) -> bytes:
    """A FileMetaData with one INT64 leaf for each name in leaves and row groups of 100 rows, each with one column chunk
    for each leaf (or chunks). A field named as "struct.field" in omit is left out; in replace, the hook writes in its
    place. The hooks of extra write more fields at the end of a structure: "file", "leaf", "root", "row_group",
    "chunk", "column", or "statistics"."""
    omitted = set(omit)
    replaced = dict(replace or {})
    extras = dict(extra or {})
    w = Thrift(always_long=always_long)

    def field(name: str, write: Callable[[], Any]) -> None:
        if name in omitted:
            return
        if name in replaced:
            replaced[name](w)
        else:
            write()

    def more(name: str) -> None:
        if name in extras:
            extras[name](w)

    count = len(leaves) if chunks is None else chunks
    field("file.version", lambda: w.i32(1, 1))

    def schema() -> None:
        w.list_of(2, Thrift.STRUCT, len(leaves) + 1)
        w.element()
        w.binary(4, "schema").i32(5, len(leaves))
        more("root")
        w.end()
        for name in leaves:
            w.element()
            w.i32(1, PHYSICAL_INT64).i32(3, REPETITION_OPTIONAL)
            field("leaf.name", lambda: w.binary(4, name))
            w.i32(6, CONVERTED_INT_64)
            more("leaf")
            w.end()

    field("file.schema", schema)
    field("file.num_rows", lambda: w.i64(3, 100 * row_group_count))

    def groups() -> None:
        w.list_of(4, Thrift.STRUCT, row_group_count)
        for ordinal in range(row_group_count):
            w.element()

            def columns() -> None:
                w.list_of(1, Thrift.STRUCT, count)
                for index in range(count):
                    w.element()
                    w.i64(2, 4)  # file_offset
                    if meta_data:
                        w.begin(3)
                        w.i32(1, PHYSICAL_INT64)
                        w.list_of(2, Thrift.I32, 2).value_i32(ENCODING_RLE).value_i32(ENCODING_PLAIN)
                        w.list_of(3, Thrift.BINARY, 1).value_binary(leaves[index % len(leaves)])
                        field("column.codec", lambda: w.i32(4, CODEC_ZSTD))
                        field("column.num_values", lambda: w.i64(5, 100))
                        w.i64(6, 800 + index).i64(7, 400 + index)
                        field("column.data_page_offset", lambda: w.i64(9, 4))
                        field("column.dictionary_page_offset", lambda: None)
                        if statistics:
                            w.begin(12)
                            field("statistics.null_count", lambda: w.i64(3, 0))
                            w.i64(4, 7)
                            w.binary(5, struct.pack("<q", 99)).binary(6, struct.pack("<q", 1))
                            w.boolean(7, True).boolean(8, False)
                            more("statistics")
                            w.end()
                        more("column")
                        w.end()
                    more("chunk")
                    w.end()

            field("row_group.columns", columns)
            field("row_group.total_byte_size", lambda: w.i64(2, 800 * count))
            field("row_group.num_rows", lambda: w.i64(3, 100))
            w.i64(5, 4).i64(6, 400 * count).i16(7, ordinal)
            more("row_group")
            w.end()

    field("file.row_groups", groups)

    def pairs() -> None:
        w.list_of(5, Thrift.STRUCT, len(key_values))
        for key, value in key_values:
            w.element()
            w.binary(1, key)
            if value is not None:
                w.binary(2, value)
            w.end()

    if key_values or "file.key_value_metadata" in replaced:
        field("file.key_value_metadata", pairs)
    if created_by is not None:
        field("file.created_by", lambda: w.binary(6, created_by))
    more("file")
    w.end()
    return w.bytes()


def every_type(w: Thrift, first: int = 100) -> None:
    """Fields that no version of parquet.thrift has, of every type of the compact protocol, from the field id first."""
    ids = iter(range(first, first + 100))
    w.boolean(next(ids), True)
    w.boolean(next(ids), False)
    w.byte(next(ids), 0x7F)
    w.i16(next(ids), -1234)
    w.i32(next(ids), 2_000_000_000)
    w.i64(next(ids), -(2 ** 62))
    w.double(next(ids), 3.25)
    w.binary(next(ids), "unknown text")
    w.binary(next(ids), b"\x00\xff" * 150)  # a length of two bytes
    w.uuid(next(ids), bytes(range(16)))
    w.list_of(next(ids), Thrift.I32, 3).value_i32(1).value_i32(-2).value_i32(3)
    w.list_of(next(ids), Thrift.I64, 20)
    for value in range(20):
        w.varint(w.zigzag(value * 1_000_000_007))
    w.list_of(next(ids), Thrift.BINARY, 2).value_binary("a").value_binary(b"\x00" * 20)
    w.list_of(next(ids), Thrift.TRUE, 3).raw(b"\x01\x02\x01")  # booleans in a list: one byte each
    w.list_of(next(ids), Thrift.DOUBLE, 2).raw(struct.pack("<dd", 1.5, -2.5))
    w.list_of(next(ids), Thrift.UUID, 1).raw(bytes(16))
    w.list_of(next(ids), Thrift.I64, 2, kind=Thrift.SET).varint(2).varint(4)
    w.map_of(next(ids), Thrift.BINARY, Thrift.I32, 2).value_binary("k1").value_i32(1).value_binary("k2").value_i32(2)
    w.map_of(next(ids), Thrift.BINARY, Thrift.I32, 0)
    w.list_of(next(ids), Thrift.LIST, 2)
    w.collection_header(Thrift.I32, 1).value_i32(5)
    w.collection_header(Thrift.I32, 0)
    w.begin(next(ids))  # a structure with fields of its own, a list of structures, and a nested structure
    w.i32(1, 1).binary(2, "nested").boolean(3, True)
    w.list_of(4, Thrift.STRUCT, 2)
    w.element().i64(1, 5).end()
    w.element().binary(9, "x").end()
    w.begin(5).begin(1).i32(1, 1).end().end()
    w.end()
    w.list_of(next(ids), Thrift.MAP, 1)
    w.varint(1).raw(bytes([(Thrift.I32 << 4) | Thrift.STRUCT])).value_i32(7).element().i32(1, 2).end()


def nested_structures(w: Thrift, field: int, depth: int) -> None:
    """An unknown field that holds structures nested depth levels deep."""
    w.begin(field)
    for _ in range(depth - 1):
        w.begin(1)
    for _ in range(depth):
        w.end()


def nested_lists(w: Thrift, field: int, depth: int) -> None:
    """An unknown field that holds lists of lists, depth levels deep, with one i32 at the bottom."""
    w.list_of(field, Thrift.LIST if depth > 1 else Thrift.I32, 1)
    for level in range(depth - 1, 0, -1):
        w.collection_header(Thrift.LIST if level > 1 else Thrift.I32, 1)
    w.value_i32(1)


def tail(length: int, magic: bytes = b"PAR1") -> bytes:
    """The last 8 bytes of a Parquet file: the length of its footer and the magic."""
    return struct.pack("<i", length) + magic


def parquet_ending_with(meta: bytes, size: int) -> bytes:
    """A file of a size that starts with the magic and ends with a footer, its length, and the magic."""
    out = bytearray(size)
    out[0:4] = b"PAR1"
    out[size - 8 - len(meta):size - 8] = meta
    out[size - 8:] = tail(len(meta))
    return bytes(out)


def parquet_file(meta: bytes) -> bytes:
    """A small Parquet file: the magic, 100 zero bytes, the footer, its length, and the magic."""
    return b"PAR1" + bytes(100) + meta + tail(len(meta))


# ---- collecting and comparing ----

def collect_bytes(storage: Any, tables: Any = None, **options: Any) -> Tuple[bytes, Any]:
    """Collects, writes the footprint to memory, and returns its bytes and the builder."""
    from pqax.collect import collect
    builder = collect(storage, tables, **options)
    buffer = io.BytesIO()
    builder.write(buffer)
    return buffer.getvalue(), builder


def open_bytes(data: bytes) -> Any:
    return pqax.open(io.BytesIO(data))


def assert_valid(data: bytes) -> None:
    result = pqax.validate(io.BytesIO(data))
    assert [f"{issue.code}: {issue.message}" for issue in result.issues] == []
    assert result.is_valid


def discover_all(storage: Any) -> List[Any]:
    from pqax.collect import discover, list_items
    tables: List[Any] = []
    for item in list_items(storage):
        tables.extend(discover(storage, item))
    return tables


def stated_tables(doc: Any) -> List[str]:
    names: List[str] = []
    for part in doc.manifest["parts"]:
        names.extend(name for name in part["tables"] if name not in names)
    return names


def canonical(value: Any) -> str:
    return json.dumps(value, sort_keys=True, ensure_ascii=False)


def field_values(table: Dict[str, List[Any]], name: str, count: int) -> List[Any]:
    values = table.get(name)
    return list(values) if values is not None else [None] * count


def is_location(table: str, name: str) -> bool:
    return name == "location" and table in ("sources", "tables")


def differences(actual: Any, expected: Any, skip: Optional[Callable[[str, str], bool]] = None) -> List[str]:
    """Every field of every table that differs between two open documents; a missing field is null in every row."""
    found: List[str] = []
    for name in list(dict.fromkeys(stated_tables(actual) + stated_tables(expected))):
        left, right = actual.table(name), expected.table(name)
        left_rows, right_rows = actual.row_count(name), expected.row_count(name)
        if left_rows != right_rows:
            found.append(f"{name}: {left_rows} rows, expected {right_rows}")
            continue
        for column in list(dict.fromkeys(list(left) + list(right))):
            if skip is not None and skip(name, column):
                continue
            got = canonical(field_values(left, column, left_rows))
            want = canonical(field_values(right, column, right_rows))
            if got != want:
                found.append(f"{name}.{column}: {got[:300]}, expected {want[:300]}")
    return found


def rows(doc: Any, name: str) -> List[Dict[str, Any]]:
    return list(doc.rows(name))


def table_row(doc: Any, name: str, namespace: Sequence[str] = ()) -> Dict[str, Any]:
    matches = [row for row in doc.rows("tables") if row["name"] == name and list(row["namespace"] or []) == list(namespace)]
    assert len(matches) == 1, f"{len(matches)} tables named {name}"
    return matches[0]


def row_of(doc: Any, dump: DumpTable) -> Dict[str, Any]:
    return table_row(doc, dump.name, dump.namespace)


def of_table(doc: Any, name: str, table_id: int) -> List[Dict[str, Any]]:
    return [row for row in doc.rows(name) if row.get("table") == table_id]


def all_text(data: bytes) -> str:
    """The text of every entry of a zip archive, to look for values that must not be there."""
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        return "\n".join(archive.read(name).decode("utf-8", "replace") for name in archive.namelist())


# ---- a fake OneLake DFS endpoint ----

@dataclass
class Call:
    method: str
    path: str  # the path of the URL, decoded, as /<workspace>/...
    raw_path: str  # the path and query as sent
    query: Dict[str, str]
    authorization: Optional[str]
    range: Optional[str]

    def reads(self, path: str) -> bool:
        """Whether the call reads a range of the file at this path of the workspace."""
        return self.range is not None and self.path.split("/", 2)[-1] == path


Answer = Tuple[int, Dict[str, str], bytes]


class FakeDfs:
    """Serves a folder as a OneLake workspace over HTTP on 127.0.0.1: the listing of the filesystem API, recursive or
    not, in pages with x-ms-continuation, and the read of a range of a file (206, 416 past the end, 404 when
    missing). answer(call) returns (status, headers, body) to replace the normal answer, a number of seconds to wait
    before answering normally, or None."""

    def __init__(self, folder: Any, workspace: str, *, page_size: int = 2, ignore_range: bool = False,
                 answer: Optional[Callable[[Call], Any]] = None) -> None:
        self.folder = pathlib.Path(folder)
        self.workspace = workspace
        self.page_size = page_size
        self.ignore_range = ignore_range
        self.answer = answer
        self.calls: List[Call] = []
        self._lock = threading.Lock()
        self._server: Optional[ThreadingHTTPServer] = None

    @property
    def port(self) -> int:
        assert self._server is not None
        return self._server.server_address[1]

    @property
    def host(self) -> str:
        return f"127.0.0.1:{self.port}"

    @property
    def endpoint(self) -> str:
        return f"http://{self.host}"

    def __enter__(self) -> "FakeDfs":
        fake = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args: Any) -> None:
                pass

            def do_GET(self) -> None:
                fake._handle(self, "GET")

            def do_HEAD(self) -> None:
                fake._handle(self, "HEAD")

            def do_PUT(self) -> None:
                fake._handle(self, "PUT")

            def do_DELETE(self) -> None:
                fake._handle(self, "DELETE")

            def do_PATCH(self) -> None:
                fake._handle(self, "PATCH")

            def do_POST(self) -> None:
                fake._handle(self, "POST")

        self._server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self._server.daemon_threads = True
        self._server.block_on_close = False
        threading.Thread(target=self._server.serve_forever, daemon=True).start()
        return self

    def __exit__(self, *exc_info: Any) -> None:
        if self._server is not None:
            self._server.shutdown()
            self._server.server_close()

    def recorded(self) -> List[Call]:
        with self._lock:
            return list(self.calls)

    def _handle(self, handler: BaseHTTPRequestHandler, method: str) -> None:
        split = urllib.parse.urlsplit(handler.path)
        query = dict(urllib.parse.parse_qsl(split.query, keep_blank_values=True))
        call = Call(method, urllib.parse.unquote(split.path), handler.path, query,
                    handler.headers.get("Authorization"), handler.headers.get("Range"))
        with self._lock:
            self.calls.append(call)
        try:
            status, headers, body = self._answer(call)
        except Exception as error:  # a bug of the fake shows as a 500 with its text
            status, headers, body = 500, {}, repr(error).encode()
        handler.send_response(status)
        for name, value in headers.items():
            handler.send_header(name, value)
        handler.send_header("Content-Length", str(len(body)))
        handler.end_headers()
        if method != "HEAD":
            try:
                handler.wfile.write(body)
            except OSError:
                pass

    def _answer(self, call: Call) -> Answer:
        if self.answer is not None:
            replaced = self.answer(call)
            if isinstance(replaced, (int, float)) and not isinstance(replaced, bool):
                time.sleep(replaced)
            elif replaced is not None:
                return replaced
        if call.method != "GET":
            return 405, {}, b""
        segments = [segment for segment in call.path.split("/") if segment]
        if not segments or segments[0] != self.workspace:
            return error_answer(404, "FilesystemNotFound")
        if len(segments) == 1 and call.query.get("resource") == "filesystem":
            return self._list(call.query)
        return self._read("/".join(segments[1:]), call.range)

    def _list(self, query: Dict[str, str]) -> Answer:
        if "directory" in query and query["directory"] == "":
            return error_answer(400, "InvalidQueryParameterValue")
        directory = query.get("directory", "").strip("/")
        full = self.folder.joinpath(*directory.split("/")) if directory else self.folder
        if not full.is_dir():
            return error_answer(404, "PathNotFound")
        recursive = query.get("recursive") == "true"
        entries = sorted(full.rglob("*") if recursive else full.iterdir(),
                         key=lambda entry: entry.relative_to(full).as_posix())
        start = int(query.get("continuation") or 0)
        page = entries[start:start + self.page_size]
        paths = []
        for entry in page:
            relative = entry.relative_to(full).as_posix()
            item: Dict[str, Any] = {"name": f"{directory}/{relative}" if directory else relative,
                                    "lastModified": email.utils.formatdate(entry.stat().st_mtime, usegmt=True)}
            if entry.is_dir():
                item["isDirectory"] = "true"
                item["contentLength"] = "0"
            else:
                item["contentLength"] = str(entry.stat().st_size)
            paths.append(item)
        headers = {"Content-Type": "application/json"}
        if start + len(page) < len(entries):
            headers["x-ms-continuation"] = str(start + len(page))
        return 200, headers, json.dumps({"paths": paths}).encode()

    def _read(self, path: str, given_range: Optional[str]) -> Answer:
        full = self.folder.joinpath(*path.split("/"))
        if not full.is_file():
            return error_answer(404, "PathNotFound")
        data = full.read_bytes()
        if given_range is None or self.ignore_range:
            return 200, {}, data
        first, _, last = given_range.split("=", 1)[1].partition("-")
        start = int(first)
        if start >= len(data):
            return 416, {}, b""
        stop = min(int(last) if last else len(data) - 1, len(data) - 1)
        return 206, {"Content-Range": f"bytes {start}-{stop}/{len(data)}"}, data[start:stop + 1]


def error_answer(status: int, code: str) -> Answer:
    return status, {"Content-Type": "application/json"}, json.dumps({"error": {"code": code}}).encode()


def range_of(call: Call) -> Tuple[int, int]:
    first, _, last = (call.range or "").split("=", 1)[1].partition("-")
    return int(first), int(last)


# ---- tokens ----

def jwt(claims: Dict[str, Any]) -> str:
    """An unsigned JWT with these claims: what a test needs to read its expiry and audience."""
    def encode(value: Dict[str, Any]) -> str:
        return base64.urlsafe_b64encode(json.dumps(value).encode()).decode().rstrip("=")
    return encode({"alg": "none", "typ": "JWT"}) + "." + encode(claims) + "."


def epoch_seconds(value: Any) -> Optional[float]:
    """An expiry as seconds since 1970, from a datetime or a number."""
    if value is None:
        return None
    if isinstance(value, datetime.datetime):
        return value.timestamp()
    return float(value)


# ---- the command line ----

def run_cli(arguments: Sequence[Any], capsys: Any) -> Tuple[int, str, str]:
    """python -m pqax with these arguments, in the process: the exit code, the output, and the error output."""
    from pqax.__main__ import main
    try:
        code = main([str(argument) for argument in arguments])
    except SystemExit as stop:
        code = stop.code if isinstance(stop.code, int) else (0 if stop.code is None else 2)
    out, err = capsys.readouterr()
    return code if code is not None else 0, out, err


def sample(name: str) -> pathlib.Path:
    return SAMPLES / name


def copy_tree(source: pathlib.Path, target: pathlib.Path) -> pathlib.Path:
    import shutil
    shutil.copytree(source, target)
    return target


def environment_without(*names: str) -> Dict[str, str]:
    return {name: value for name, value in os.environ.items() if name not in names}
