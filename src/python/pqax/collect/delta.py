"""The Delta log of a table: its actions, the JSON commits and checkpoints, the replay into the state of the last
version, and the history of the commits, by the rules of the Delta protocol and as the C# collector reads them.

The JSON parser never keeps the user names, notebook identifiers, or cluster identifiers of a commitInfo, it keeps the
parameters of an operation only when asked, and it reads only numRecords and the null counts from the statistics of a
file, whose bounds are data values.
"""
from __future__ import annotations

import dataclasses
import datetime
import json
import json.decoder
import json.scanner
import re
import threading
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple

from ._issues import IssueCodes, IssueList
from .storage import StorageEntry, StorageError, read_all

__all__ = [
    "AddAction", "CheckpointMetadataAction", "CommitInfoAction", "DeletionVector", "DomainMetadataAction",
    "MAX_CHECKPOINT_BYTES", "MetadataAction", "ProtocolAction", "RemoveAction", "SidecarAction", "parse_actions",
]

#: The largest checkpoint that the collector reads, all parts together.
MAX_CHECKPOINT_BYTES = 512 * 1024 * 1024

_INT64_MIN, _INT64_MAX = -(1 << 63), (1 << 63) - 1
_EPOCH = datetime.datetime(1970, 1, 1, tzinfo=datetime.timezone.utc)
_NO_READER = "The table has a checkpoint in Parquet, and the collection has no checkpoint reader."
_COMMIT_FILE = re.compile(r"([0-9]{20})\.json")
_CLASSIC_CHECKPOINT = re.compile(r"([0-9]{20})\.checkpoint\.parquet")
_MULTIPART_CHECKPOINT = re.compile(r"([0-9]{20})\.checkpoint\.([0-9]{10})\.([0-9]{10})\.parquet")
_V2_CHECKPOINT = re.compile(r"([0-9]{20})\.checkpoint\.([0-9a-fA-F-]{36})\.(json|parquet)")
_SIGNED = re.compile(r"[+-]?[0-9]+")


# ---- actions ----

@dataclasses.dataclass
class DeletionVector:
    """The deletion vector of a data file. The collector keeps its row count and size; the location identifies it."""

    storage_type: Optional[str] = None
    path_or_inline_dv: Optional[str] = None
    offset: Optional[int] = None
    size_in_bytes: Optional[int] = None
    cardinality: Optional[int] = None

    @property
    def unique_id(self) -> str:
        """The unique identifier of the Delta protocol: storage type, location, and offset when there is one."""
        return ((self.storage_type or "") + (self.path_or_inline_dv or "")
                + ("" if self.offset is None else f"@{self.offset}"))


@dataclasses.dataclass
class AddAction:
    """An add action: a data file that joins the table."""

    path: Optional[str] = None
    partition_values: Optional[Dict[str, Optional[str]]] = None
    size: Optional[int] = None
    modification_time: Optional[int] = None
    #: The statistics as JSON text, of which numRecords and nullCount are read.
    stats: Optional[str] = None
    #: The rows, when a checkpoint gives the statistics as a struct.
    num_records: Optional[int] = None
    #: The null values of each top-level column, when a checkpoint gives the statistics as a struct.
    null_counts: Optional[Dict[str, int]] = None
    tags: Optional[Dict[str, Optional[str]]] = None
    deletion_vector: Optional[DeletionVector] = None
    clustering_provider: Optional[str] = None


@dataclasses.dataclass
class RemoveAction:
    """A remove action: a data file that leaves the table."""

    path: Optional[str] = None
    size: Optional[int] = None
    deletion_timestamp: Optional[int] = None
    deletion_vector: Optional[DeletionVector] = None


@dataclasses.dataclass
class MetadataAction:
    """A metaData action."""

    id: Optional[str] = None
    name: Optional[str] = None
    description: Optional[str] = None
    provider: Optional[str] = None
    schema_string: Optional[str] = None
    partition_columns: Optional[List[str]] = None
    configuration: Optional[Dict[str, str]] = None
    created_time: Optional[int] = None


@dataclasses.dataclass
class ProtocolAction:
    """A protocol action."""

    min_reader_version: Optional[int] = None
    min_writer_version: Optional[int] = None
    reader_features: Optional[List[str]] = None
    writer_features: Optional[List[str]] = None


@dataclasses.dataclass
class DomainMetadataAction:
    """A domainMetadata action."""

    domain: Optional[str] = None
    configuration: Optional[str] = None
    removed: bool = False


@dataclasses.dataclass
class SidecarAction:
    """A sidecar action of a v2 checkpoint: a file in _delta_log/_sidecars that holds file actions."""

    path: Optional[str] = None
    size_in_bytes: Optional[int] = None


@dataclasses.dataclass
class CheckpointMetadataAction:
    """The checkpointMetadata action of a v2 checkpoint."""

    version: Optional[int] = None


@dataclasses.dataclass
class CommitInfoAction:
    """The commitInfo of a commit, without the user, the notebook, or the cluster."""

    timestamp: Optional[int] = None
    in_commit_timestamp: Optional[int] = None
    operation: Optional[str] = None
    engine_info: Optional[str] = None
    is_blind_append: Optional[bool] = None
    operation_metrics: Optional[Dict[str, str]] = None
    operation_parameters: Optional[Dict[str, str]] = None


# ---- JSON ----

class _Object(dict):
    """A JSON object that keeps its members in order, duplicates included, as C# enumerates them; lookups give the
    last of a repeated name, as JsonElement.TryGetProperty does. An object of the raw parser also has the text of
    each member value, as JsonElement.GetRawText gives it."""

    __slots__ = ("pairs", "raws")

    def __init__(self, pairs: List[Tuple[str, Any]]) -> None:
        super().__init__(pairs)
        self.pairs = pairs
        self.raws: Optional[List[str]] = None

    def raw(self, index: int) -> str:
        """The text of the value of a member, as the file has it; the compact JSON of the value without the raw
        parser, which _RAW.needed then asks for."""
        if self.raws is not None:
            return self.raws[index]
        _RAW.needed = True
        return json.dumps(self.pairs[index][1], ensure_ascii=False, separators=(",", ":"))

    def raw_of(self, name: str) -> str:
        """The text of the value of the last member with a name."""
        for index in range(len(self.pairs) - 1, -1, -1):
            if self.pairs[index][0] == name:
                return self.raw(index)
        raise KeyError(name)


def _reject_constant(name: str) -> Any:
    raise ValueError(f"{name} is not a JSON value")


_DECODER = json.JSONDecoder(object_pairs_hook=_Object, parse_constant=_reject_constant)
_RAW = threading.local()  # needed: whether a value asked for its raw text without the raw parser


class _RawDecoder(json.JSONDecoder):
    """A parser that keeps the text of every member value of every object, for the values that C# writes as their raw
    text. It uses the pure Python scanner, so it is slower, and it runs only on the text that needs it."""

    def __init__(self) -> None:
        super().__init__(object_pairs_hook=_Object, parse_constant=_reject_constant)
        self._stack: List[List[str]] = []
        self.parse_object = self._object  # before the scanner is made, which takes them from here
        self.parse_array = self._array
        inner = json.scanner.py_make_scanner(self)

        def scan_once(text: str, index: int) -> Tuple[Any, int]:
            value, end = inner(text, index)
            if self._stack:
                self._stack[-1].append(text[index:end])
            return value, end

        self._scan_once = scan_once
        self.scan_once = scan_once

    def _object(self, s_and_end, strict, scan_once, object_hook, object_pairs_hook, memo=None, _w=None, _ws=None):
        self._stack.append([])
        try:
            arguments = (s_and_end, strict, self._scan_once, object_hook, object_pairs_hook, memo)
            value, end = json.decoder.JSONObject(*arguments) if _w is None else \
                json.decoder.JSONObject(*arguments, _w, _ws)
        finally:
            raws = self._stack.pop()
        if isinstance(value, _Object):
            value.raws = raws
        return value, end

    def _array(self, s_and_end, scan_once, _w=None, _ws=None):
        self._stack.append([])
        try:
            return json.decoder.JSONArray(s_and_end, self._scan_once) if _w is None else \
                json.decoder.JSONArray(s_and_end, self._scan_once, _w, _ws)
        finally:
            self._stack.pop()


def loads(text: Any, raw: bool = False) -> Any:
    """Parses JSON as System.Text.Json does: no NaN or Infinity, and objects that keep their repeated members, and
    with raw the text of every member value too."""
    if isinstance(text, (bytes, bytearray, memoryview)):
        text = bytes(text).decode("utf-8")
    return _RawDecoder().decode(text) if raw else _DECODER.decode(text)


def _with_raw(parse: Callable[[bool], Any]) -> Any:
    """Runs parse(False), and again with the raw parser when a value asked for its raw text."""
    _RAW.needed = False
    result = parse(False)
    if _RAW.needed:
        _RAW.needed = False
        result = parse(True)
    return result


def _text(element: dict, name: str) -> Optional[str]:
    value = element.get(name)
    return value if isinstance(value, str) else None


def _long(element: dict, name: str) -> Optional[int]:
    if name not in element:
        return None
    value = element[name]
    if type(value) is int:
        return value if _INT64_MIN <= value <= _INT64_MAX else None
    if isinstance(value, str) and _SIGNED.fullmatch(value):
        number = int(value)
        return number if _INT64_MIN <= number <= _INT64_MAX else None
    return None


def _bool(element: dict, name: str) -> Optional[bool]:
    value = element.get(name)
    return value if isinstance(value, bool) else None


def _list(element: dict, name: str) -> Optional[List[str]]:
    value = element.get(name)
    if not isinstance(value, list):
        return None
    return [item for item in value if isinstance(item, str)]


def _map(element: dict, name: str) -> Optional[Dict[str, str]]:
    value = element.get(name)
    if not isinstance(value, dict):
        return None
    result: Dict[str, str] = {}
    for index, (key, item) in enumerate(value.pairs):
        if isinstance(item, str):
            result[key] = item
        elif item is not None:
            result[key] = value.raw(index)
    return result


def _nullable_map(element: dict, name: str) -> Optional[Dict[str, Optional[str]]]:
    value = element.get(name)
    if not isinstance(value, dict):
        return None
    result: Dict[str, Optional[str]] = {}
    for index, (key, item) in enumerate(value.pairs):
        result[key] = item if isinstance(item, str) or item is None else value.raw(index)
    return result


def _deletion_vector(element: dict) -> Optional[DeletionVector]:
    vector = element.get("deletionVector")
    if not isinstance(vector, dict):
        return None
    return DeletionVector(_text(vector, "storageType"), _text(vector, "pathOrInlineDv"), _long(vector, "offset"),
                          _long(vector, "sizeInBytes"), _long(vector, "cardinality"))


def _action(name: str, value: dict, include_parameters: bool) -> Optional[Any]:
    if name == "add":
        return AddAction(path=_text(value, "path"), partition_values=_nullable_map(value, "partitionValues"),
                         size=_long(value, "size"), modification_time=_long(value, "modificationTime"),
                         stats=_text(value, "stats"), tags=_nullable_map(value, "tags"),
                         deletion_vector=_deletion_vector(value), clustering_provider=_text(value, "clusteringProvider"))
    if name == "remove":
        return RemoveAction(_text(value, "path"), _long(value, "size"), _long(value, "deletionTimestamp"),
                            _deletion_vector(value))
    if name == "metaData":
        form = value.get("format")
        return MetadataAction(_text(value, "id"), _text(value, "name"), _text(value, "description"),
                              _text(form, "provider") if isinstance(form, dict) else None,
                              _text(value, "schemaString"), _list(value, "partitionColumns"),
                              _map(value, "configuration"), _long(value, "createdTime"))
    if name == "protocol":
        return ProtocolAction(_long(value, "minReaderVersion"), _long(value, "minWriterVersion"),
                              _list(value, "readerFeatures"), _list(value, "writerFeatures"))
    if name == "domainMetadata":
        return DomainMetadataAction(_text(value, "domain"), _text(value, "configuration"), _bool(value, "removed") is True)
    if name == "commitInfo":
        return CommitInfoAction(_long(value, "timestamp"), _long(value, "inCommitTimestamp"), _text(value, "operation"),
                                _text(value, "engineInfo"), _bool(value, "isBlindAppend"),
                                _map(value, "operationMetrics"),
                                _map(value, "operationParameters") if include_parameters else None)
    if name == "sidecar":
        return SidecarAction(_text(value, "path"), _long(value, "sizeInBytes"))
    if name == "checkpointMetadata":
        return CheckpointMetadataAction(_long(value, "version"))
    return None


def parse_actions(content: bytes, include_parameters: bool = False) -> List[Any]:
    """The actions of a commit file, or of a checkpoint in JSON: one JSON object per line. Raises ValueError when a
    line is not a JSON object."""
    actions: List[Any] = []
    start = 3 if content[:3] == b"\xef\xbb\xbf" else 0
    for line in content[start:].split(b"\n"):
        line = line.strip(b" \r\t\n")
        if line:
            actions.extend(_with_raw(lambda raw: _line_actions(line, raw, include_parameters)))
    return actions


def _line_actions(line: bytes, raw: bool, include_parameters: bool) -> List[Any]:
    root = loads(line, raw)
    if not isinstance(root, _Object):
        raise ValueError("A line of the log is not a JSON object.")
    actions = []
    for name, value in root.pairs:
        if isinstance(value, dict):
            action = _action(name, value, include_parameters)
            if action is not None:
                actions.append(action)
    return actions


def parse_stats(stats: str) -> Tuple[bool, Optional[int], Optional[List[Tuple[str, int]]]]:
    """numRecords and the null counts of the top-level columns of the statistics of a file. False when the text is
    not a JSON object."""
    try:
        root = loads(stats)
    except (ValueError, RecursionError):
        return False, None, None
    if not isinstance(root, _Object):
        return False, None, None
    rows: Optional[int] = None
    nulls: Optional[List[Tuple[str, int]]] = None
    for name, value in root.pairs:
        if name == "numRecords":
            if type(value) is int and _INT64_MIN <= value <= _INT64_MAX:
                rows = value
        elif name == "nullCount" and isinstance(value, _Object):
            # A nested column has an object of counts; the model keeps top-level columns only.
            nulls = [(column, count) for column, count in value.pairs
                     if type(count) is int and _INT64_MIN <= count <= _INT64_MAX]
    return True, rows, nulls


# ---- the schema ----

@dataclasses.dataclass
class DeltaColumn:
    """A top-level column of the schema of a Delta table."""

    name: str
    data_type: str
    is_nullable: Optional[bool] = None
    field_id: Optional[int] = None
    mapped_name: Optional[str] = None
    description: Optional[str] = None

    @property
    def file_name(self) -> str:
        """The name that the files use: the physical name with column mapping, the name otherwise."""
        return self.mapped_name if self.mapped_name is not None else self.name


def parse_schema(schema_string: str) -> List[DeltaColumn]:
    """The top-level columns of a schemaString, with the type as Spark writes it in DDL. Raises ValueError."""
    return _with_raw(lambda raw: _schema(schema_string, raw))


def _schema(schema_string: str, raw: bool) -> List[DeltaColumn]:
    root = loads(schema_string, raw)
    fields = root.get("fields") if isinstance(root, dict) else None
    if not isinstance(fields, list):
        raise ValueError("The schema is not a struct with fields.")
    columns = []
    for field in fields:
        if not isinstance(field, dict) or not isinstance(field.get("name"), str):
            raise ValueError("A field of the schema has no name.")
        column = DeltaColumn(field["name"], _type_text(field, "type", 0) if "type" in field else "unknown")
        if isinstance(field.get("nullable"), bool):
            column.is_nullable = field["nullable"]
        metadata = field.get("metadata")
        if isinstance(metadata, dict):
            field_id = metadata.get("delta.columnMapping.id")
            if type(field_id) is int and _INT64_MIN <= field_id <= _INT64_MAX:
                column.field_id = field_id
            if isinstance(metadata.get("delta.columnMapping.physicalName"), str):
                column.mapped_name = metadata["delta.columnMapping.physicalName"]
            if isinstance(metadata.get("comment"), str):
                column.description = metadata["comment"]
        columns.append(column)
    return columns


def _type_text(parent: _Object, member: str, depth: int) -> str:
    """The type of a member of the schema as Spark writes it in DDL, or its JSON as it is for a kind this version does
    not know."""
    if depth > 64:
        raise ValueError("The schema is nested too deep.")
    kind = parent[member]
    if isinstance(kind, str):
        return kind
    if not isinstance(kind, dict) or not isinstance(kind.get("type"), str):
        return parent.raw_of(member)
    name = kind["type"]
    if name == "struct" and isinstance(kind.get("fields"), list):
        parts = []
        for field in kind["fields"]:
            field_name = field.get("name") if isinstance(field, dict) else None
            text = (field_name if isinstance(field_name, str) else "?") + ":"
            if isinstance(field, dict) and "type" in field:
                text += _type_text(field, "type", depth + 1)
            parts.append(text)
        return "struct<" + ",".join(parts) + ">"
    if name == "array" and "elementType" in kind:
        return "array<" + _type_text(kind, "elementType", depth + 1) + ">"
    if name == "map" and "keyType" in kind and "valueType" in kind:
        return "map<" + _type_text(kind, "keyType", depth + 1) + "," + _type_text(kind, "valueType", depth + 1) + ">"
    return parent.raw_of(member)  # a user-defined type or a kind this version does not know: the JSON as it is


# ---- the state ----

def unescape(path: str) -> str:
    """A path of the log decoded as C# Uri.UnescapeDataString decodes it: the escapes that form UTF-8, and the others
    left as they are."""
    if "%" not in path:
        return path
    out: List[str] = []
    i = 0
    n = len(path)
    while i < n:
        if path[i] != "%":
            out.append(path[i])
            i += 1
            continue
        # A run of escapes: decode as many complete UTF-8 sequences as it holds.
        run_start = i
        data = bytearray()
        texts: List[str] = []
        while i + 3 <= n and path[i] == "%" and _is_hex(path[i + 1:i + 3]):
            data.append(int(path[i + 1:i + 3], 16))
            texts.append(path[i:i + 3])
            i += 3
        if not data:
            out.append("%")
            i = run_start + 1
            continue
        out.append(_decode_run(bytes(data), texts))
    return "".join(out)


def _is_hex(text: str) -> bool:
    return len(text) == 2 and all(c in "0123456789abcdefABCDEF" for c in text)


def _decode_run(data: bytes, texts: List[str]) -> str:
    """Bytes of escapes as text: valid UTF-8 sequences decoded, any other byte kept as its escape."""
    out: List[str] = []
    i = 0
    while i < len(data):
        b = data[i]
        length = 1 if b < 0x80 else 2 if 0xC2 <= b <= 0xDF else 3 if 0xE0 <= b <= 0xEF else 4 if 0xF0 <= b <= 0xF4 else 0
        if length:
            try:
                out.append(data[i:i + length].decode("utf-8"))
                i += length
                continue
            except UnicodeDecodeError:
                pass
        out.append(texts[i])
        i += 1
    return "".join(out)


@dataclasses.dataclass
class LiveFile:
    """A data file of the version that the log describes."""

    raw_path: str
    path: str
    size: Optional[int] = None
    modification_time: Optional[int] = None
    num_records: Optional[int] = None
    null_counts: Optional[List[Tuple[str, int]]] = None
    partition_values: Optional[Dict[str, Optional[str]]] = None
    vorder_tag: Optional[bool] = None
    deleted_row_count: Optional[int] = None
    deletion_vector_size: Optional[int] = None
    clustering_provider: Optional[str] = None

    @property
    def is_outside_table(self) -> bool:
        """Whether the path is absolute: with a scheme or a leading slash."""
        return "://" in self.path or self.path.startswith("/")


def _vorder(tags: Optional[Dict[str, Optional[str]]]) -> Optional[bool]:
    if tags is None:
        return None
    for key, value in tags.items():
        if key.lower() == "vorder":
            if value is not None and value.lower() == "true":
                return True
            if value is not None and value.lower() == "false":
                return False
    return None


def _ordinal(text: str):
    return text.encode("utf-16-be", "surrogatepass")


class DeltaState:
    """The state of a table while its log is replayed: the live files by their key, the path and the deletion vector,
    the tombstones, the metadata, the protocol, and the domains. The latest action wins."""

    def __init__(self) -> None:
        self.live: Dict[str, LiveFile] = {}
        self.removed: Dict[str, Tuple[Optional[int], Optional[int]]] = {}
        self.metadata: Optional[MetadataAction] = None
        self.protocol: Optional[ProtocolAction] = None
        self.domains: Dict[str, str] = {}
        self.sidecars: List[SidecarAction] = []
        self.stats_errors = 0

    @staticmethod
    def _key(path: str, vector: Optional[DeletionVector]) -> str:
        return path + "\0" if vector is None else path + "\0\1" + vector.unique_id

    def apply(self, action: Any) -> None:
        if isinstance(action, AddAction):
            self._add(action)
        elif isinstance(action, RemoveAction):
            self._remove(action)
        elif isinstance(action, MetadataAction):
            self.metadata = action
        elif isinstance(action, ProtocolAction):
            self.protocol = action
        elif isinstance(action, DomainMetadataAction) and action.domain:
            if action.removed:
                self.domains.pop(action.domain, None)
            else:
                self.domains[action.domain] = action.configuration or ""
        elif isinstance(action, SidecarAction) and action.path:
            self.sidecars.append(action)

    def _add(self, add: AddAction) -> None:
        if not add.path:
            return
        path = unescape(add.path)
        vector = add.deletion_vector
        file = LiveFile(add.path, path, add.size, add.modification_time, add.num_records,
                        list(add.null_counts.items()) if add.null_counts is not None else None,
                        add.partition_values, _vorder(add.tags), 0 if vector is None else vector.cardinality,
                        vector.size_in_bytes if vector is not None else None, add.clustering_provider)
        if add.stats:
            valid, rows, nulls = parse_stats(add.stats)
            if valid:
                if rows is not None:
                    file.num_records = rows
                if nulls is not None:
                    file.null_counts = nulls
            else:
                self.stats_errors += 1
        self.live[self._key(path, vector)] = file

    def _remove(self, remove: RemoveAction) -> None:
        if not remove.path:
            return
        path = unescape(remove.path)
        key = self._key(path, remove.deletion_vector)
        size = remove.size
        file = self.live.pop(key, None)
        if file is not None and size is None:
            size = file.size
        # The latest remove of a path wins, and what it does not record comes from an earlier remove of the same path.
        removed_at = remove.deletion_timestamp
        known = self.removed.get(path)
        if known is not None:
            if size is None:
                size = known[0]
            if removed_at is None:
                removed_at = known[1]
        self.removed[path] = (size, removed_at)

    def finish(self, log: "DeltaLog") -> None:
        """Writes the files, sorted by modification time and path, and the tombstones into the log."""
        files = sorted(self.live.values(), key=lambda f: (_INT64_MIN if f.modification_time is None
                                                          else f.modification_time, _ordinal(f.raw_path)))
        log.files = files
        live_paths = {file.path for file in files}
        count = size = 0
        size_known = True
        oldest: Optional[int] = None
        newest: Optional[int] = None
        for path, (removed_size, removed_at) in self.removed.items():
            if path in live_paths:
                continue  # removed with one deletion vector and added back with another: still live
            count += 1
            if removed_size is None:
                size_known = False
            else:
                size += removed_size
            if removed_at is not None:
                oldest = removed_at if oldest is None else min(oldest, removed_at)
                newest = removed_at if newest is None else max(newest, removed_at)
        log.non_live_file_count = count
        log.non_live_file_size = size if size_known else None
        log.non_live_oldest_removed_at = oldest
        log.non_live_newest_removed_at = newest
        log.metadata = self.metadata
        log.protocol = self.protocol
        log.domains = dict(sorted(self.domains.items(), key=lambda item: _ordinal(item[0])))


# ---- the log ----

def timestamp(milliseconds: int) -> datetime.datetime:
    """Milliseconds since 1970 as a datetime in UTC. Raises OverflowError outside the years 1 to 9999."""
    return _EPOCH + datetime.timedelta(milliseconds=milliseconds)


@dataclasses.dataclass
class CommitRecord:
    """A commit read for the history: its version, its commitInfo, and its time, which is the in-commit timestamp, else
    the timestamp of the commit information, else the time of the commit file."""

    version: int
    info: Optional[CommitInfoAction]
    file_time: Optional[datetime.datetime]
    timestamp: Optional[datetime.datetime] = dataclasses.field(init=False)

    def __post_init__(self) -> None:
        milliseconds = None
        if self.info is not None:
            milliseconds = self.info.in_commit_timestamp
            if milliseconds is None:
                milliseconds = self.info.timestamp
        self.timestamp = timestamp(milliseconds) if milliseconds is not None else self.file_time


@dataclasses.dataclass
class DeltaLog:
    """What the Delta log of a table says: the state of its last version, the history read, and the log itself."""

    found: bool = False
    files_complete: bool = False
    version: Optional[int] = None
    snapshot_timestamp: Optional[datetime.datetime] = None
    metadata: Optional[MetadataAction] = None
    protocol: Optional[ProtocolAction] = None
    domains: Dict[str, str] = dataclasses.field(default_factory=dict)
    checkpoint_version: Optional[int] = None
    commits_after_checkpoint: Optional[int] = None
    commit_count: int = 0
    metadata_file_count: int = 0
    metadata_size: Optional[int] = 0
    files: List[LiveFile] = dataclasses.field(default_factory=list)
    non_live_file_count: Optional[int] = None
    non_live_file_size: Optional[int] = None
    non_live_oldest_removed_at: Optional[int] = None
    non_live_newest_removed_at: Optional[int] = None
    history: List[CommitRecord] = dataclasses.field(default_factory=list)


class _Checkpoint:
    __slots__ = ("version", "parts")

    def __init__(self, version: int, parts: List[StorageEntry]) -> None:
        self.version = version
        self.parts = parts


class _Listing:
    """The files of _delta_log by kind."""

    def __init__(self) -> None:
        self.commits: Dict[int, StorageEntry] = {}
        self.last_checkpoint: Optional[StorageEntry] = None
        self.has_sidecar_folder = False
        self._classic: Dict[int, StorageEntry] = {}
        self._uuid: Dict[int, StorageEntry] = {}
        self._multipart: Dict[Tuple[int, int], Dict[int, StorageEntry]] = {}

    def classify(self, entry: StorageEntry) -> None:
        name = entry.name
        match = _COMMIT_FILE.fullmatch(name)
        if match:
            self.commits[int(match.group(1))] = entry
        elif name == "_last_checkpoint":
            self.last_checkpoint = entry
        elif (match := _CLASSIC_CHECKPOINT.fullmatch(name)):
            self._classic[int(match.group(1))] = entry
        elif (match := _MULTIPART_CHECKPOINT.fullmatch(name)):
            key = (int(match.group(1)), int(match.group(3)))
            self._multipart.setdefault(key, {})[int(match.group(2))] = entry
        elif (match := _V2_CHECKPOINT.fullmatch(name)):
            version = int(match.group(1))
            existing = self._uuid.get(version)
            if existing is None or _ordinal(name) < _ordinal(existing.name):
                self._uuid[version] = entry

    def sorted_commits(self) -> List[Tuple[int, StorageEntry]]:
        return sorted(self.commits.items())

    def newest(self) -> Optional[_Checkpoint]:
        """The newest complete checkpoint: a multi-part checkpoint counts when all its parts are there."""
        best: Optional[_Checkpoint] = None

        def better(candidate: _Checkpoint) -> None:
            nonlocal best
            if best is None or candidate.version > best.version:
                best = candidate

        for version, entry in self._classic.items():
            better(_Checkpoint(version, [entry]))
        for version, entry in self._uuid.items():
            better(_Checkpoint(version, [entry]))
        for (version, count), parts in self._multipart.items():
            numbers = sorted(parts)
            if len(parts) == count and numbers[0] == 1 and numbers[-1] == count:
                better(_Checkpoint(version, [parts[number] for number in numbers]))
        return best


CheckpointReader = Callable[[Any, str], Sequence[Any]]


class DeltaLogReader:
    """Reads the Delta log of one table: one listing of _delta_log, _last_checkpoint, the checkpoint through the
    checkpoint reader, the commits after it, and older commits for the history up to a limit."""

    def __init__(self, storage, checkpoints: Optional[CheckpointReader], max_commits: int,
                 include_parameters: bool) -> None:
        self.storage = storage
        self.checkpoints = checkpoints
        self.max_commits = max_commits
        self.include_parameters = include_parameters

    def read(self, table_path: str, issues: IssueList) -> DeltaLog:
        log_path = table_path.rstrip("/") + "/_delta_log"
        log = DeltaLog()
        entries = self.storage.list(log_path)
        if not entries:
            issues.add(IssueCodes.DELTA_LOG_MISSING, "The table folder has no Delta log, or the log is empty.")
            return log
        log.found = True
        listing = _Listing()
        log_size: Optional[int] = 0
        for entry in entries:
            if entry.is_directory:
                if entry.name == "_sidecars":
                    listing.has_sidecar_folder = True
                continue
            log.metadata_file_count += 1
            log_size = None if entry.size is None or log_size is None else log_size + entry.size
            listing.classify(entry)
        if listing.has_sidecar_folder:
            for sidecar in self.storage.list(log_path + "/_sidecars"):
                if not sidecar.is_directory:
                    log.metadata_file_count += 1
                    log_size = None if sidecar.size is None or log_size is None else log_size + sidecar.size
        log.metadata_size = log_size
        log.commit_count = len(listing.commits)
        commits = listing.sorted_commits()

        hinted = self._last_checkpoint(listing.last_checkpoint)
        checkpoint = listing.newest()
        if hinted is not None and (checkpoint is None or hinted > checkpoint.version):
            issues.add(IssueCodes.DELTA_CHECKPOINT_MISSING,
                       f"_last_checkpoint names version {hinted}, whose checkpoint the log does not have.")

        state = DeltaState()
        complete = True
        replay_from = 0
        if checkpoint is not None:
            log.checkpoint_version = checkpoint.version
            log.commits_after_checkpoint = sum(1 for version in listing.commits if version > checkpoint.version)
            failure = self._load_checkpoint(log_path, checkpoint, state)
            replay_from = checkpoint.version + 1
            if failure is not None:
                if self._has_all_commits(listing, checkpoint.version):
                    # Every commit up to the checkpoint is still there: the JSON commits give the same state. A
                    # checkpoint that failed is still worth an issue; one that was skipped for want of a reader is not.
                    if failure != _NO_READER:
                        issues.add(IssueCodes.DELTA_CHECKPOINT_NOT_READ,
                                   failure + " The state comes from the JSON commits, which the log still has from "
                                             "version 0.")
                    state = DeltaState()
                    replay_from = 0
                else:
                    issues.add(IssueCodes.DELTA_CHECKPOINT_NOT_READ,
                               failure + " The files of the table are unknown; the summary holds what the JSON "
                                         "commits say.")
                    complete = False
        elif not commits:
            issues.add(IssueCodes.DELTA_LOG_INCOMPLETE, "The log has no commit and no checkpoint.")
            complete = False
        elif commits[0][0] != 0:
            issues.add(IssueCodes.DELTA_LOG_INCOMPLETE,
                       f"The log has no checkpoint, and its oldest commit is version {commits[0][0]}.")
            complete = False

        # Replay the commits that the state needs, in order: every one is read, whatever the limit.
        history: List[CommitRecord] = []
        files_read = 0
        expected = replay_from
        for version, entry in commits:
            if version < replay_from:
                continue
            if version != expected and complete:
                issues.add(IssueCodes.DELTA_LOG_INCOMPLETE,
                           f"Commits {expected} to {version - 1} are missing from the log.")
                complete = False
            expected = version + 1
            files_read += 1
            actions = self._read_commit(entry, issues)
            if actions is None:
                complete = False
                continue
            info: Optional[CommitInfoAction] = None
            for action in actions:
                if isinstance(action, CommitInfoAction):
                    if info is None:
                        info = action
                else:
                    state.apply(action)
            history.append(CommitRecord(version, info, entry.last_modified))

        # Then older commits for the history only, the most recent first, until the commit files read reach the limit.
        # A file whose read fails counts too, since it cost a request; it has an issue instead of a place.
        if files_read < self.max_commits:
            for version, entry in reversed(commits):
                if files_read >= self.max_commits:
                    break
                if version >= replay_from:
                    continue
                files_read += 1
                actions = self._read_commit(entry, issues)
                if actions is not None:
                    info = next((action for action in actions if isinstance(action, CommitInfoAction)), None)
                    history.append(CommitRecord(version, info, entry.last_modified))

        history.sort(key=lambda record: record.version)
        log.history = history
        state.finish(log)
        if state.stats_errors:
            issues.add(IssueCodes.DELTA_STATS_NOT_PARSED,
                       f"The statistics of {state.stats_errors} files are not valid JSON; their rows come from the "
                       f"footers when those are read.")
        latest = commits[-1][0] if commits else None
        if latest is None:
            log.version = checkpoint.version if checkpoint is not None else None
        else:
            log.version = latest if checkpoint is None else max(latest, checkpoint.version)
        if log.version is not None:
            last = next((record for record in reversed(history) if record.version == log.version), None)
            moment = last.timestamp if last is not None else None
            if moment is None:
                file = listing.commits.get(log.version)
                moment = file.last_modified if file is not None else None
            log.snapshot_timestamp = moment
        log.files_complete = complete
        return log

    @staticmethod
    def _has_all_commits(listing: _Listing, up_to: int) -> bool:
        if len(listing.commits) <= up_to:
            return False
        return all(version in listing.commits for version in range(up_to + 1))

    def _last_checkpoint(self, entry: Optional[StorageEntry]) -> Optional[int]:
        if entry is None:
            return None
        try:
            document = loads(read_all(self.storage, entry.path, entry.size))
        except (ValueError, RecursionError, StorageError):
            return None  # the file is only a hint: the listing has the checkpoints
        if isinstance(document, dict):
            version = document.get("version")
            if type(version) is int and _INT64_MIN <= version <= _INT64_MAX:
                return version
        return None

    def _load_checkpoint(self, log_path: str, checkpoint: _Checkpoint, state: DeltaState) -> Optional[str]:
        """Reads the checkpoint into the state. None when it was read, otherwise why not."""
        size = sum(part.size or 0 for part in checkpoint.parts)
        if size > MAX_CHECKPOINT_BYTES:
            return f"The checkpoint takes {size} bytes, more than the limit of {MAX_CHECKPOINT_BYTES}."
        is_json = len(checkpoint.parts) == 1 and checkpoint.parts[0].name.endswith(".json")
        if not is_json and self.checkpoints is None:
            return _NO_READER
        try:
            for part in checkpoint.parts:
                if is_json:
                    actions = parse_actions(read_all(self.storage, part.path, part.size), False)
                else:
                    actions = self.checkpoints(self.storage, part.path)
                for action in actions:
                    state.apply(action)
            # A v2 checkpoint can keep its file actions in sidecar files, which are always Parquet.
            for sidecar in list(state.sidecars):
                if self.checkpoints is None:
                    return "The v2 checkpoint keeps its files in sidecar files, and the collection has no checkpoint reader."
                name = unescape(sidecar.path)
                path = log_path + "/_sidecars/" + name.rsplit("/", 1)[-1]
                for action in self.checkpoints(self.storage, path):
                    state.apply(action)
            return None
        except Exception as error:  # noqa: BLE001 - any failure of a checkpoint is a reason, as in C#
            reason = f"The checkpoint could not be read: {type(error).__name__}: {error}"
            return reason if reason.endswith(".") else reason + "."

    def _read_commit(self, entry: StorageEntry, issues: IssueList) -> Optional[List[Any]]:
        try:
            content = read_all(self.storage, entry.path, entry.size)
        except StorageError as error:
            issues.add(IssueCodes.ACCESS_DENIED if error.is_access_denied else IssueCodes.DELTA_COMMIT_NOT_READ,
                       f"A commit could not be read: {error}", "_delta_log/" + entry.name)
            return None
        try:
            return parse_actions(content, self.include_parameters)
        except (ValueError, RecursionError) as error:
            issues.add(IssueCodes.DELTA_COMMIT_NOT_READ, f"A commit is not valid JSON: {error}",
                       "_delta_log/" + entry.name)
            return None
