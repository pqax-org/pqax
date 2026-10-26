"""The collection of one table: its Delta log, the footers of its files at level 2, and its columns, partitions,
physical schemas, writers, operations, details, and totals; or, for a folder of Parquet files, its files from one
listing and its columns from the footer of its most recent file. A failure becomes an issue of the table."""
from __future__ import annotations

import datetime
import enum
import threading
import urllib.parse
from concurrent.futures import Executor
from typing import Any, Dict, List, Optional, Tuple

from ._issues import Issue, IssueCodes, IssueList
from .delta import (
    CommitInfoAction, DeltaColumn, DeltaLog, DeltaLogReader, LiveFile, MetadataAction, loads, parse_schema, timestamp,
)
from .discovery import TableFormats, TableLocation, is_parquet_file
from .footer import (
    FooterError, FooterReader, FooterTailPlanner, ParquetColumnChunk, ParquetFooter, ParquetLeafColumn, top_level_fields,
)
from .storage import StorageError, list_files

_CONSTRAINT_PREFIX = "delta.constraints."
_NON_DATA_FOLDERS = ("_delta_log", "_change_data")
_EPOCH = datetime.datetime(1970, 1, 1, tzinfo=datetime.timezone.utc)
_MILLISECOND = datetime.timedelta(milliseconds=1)
#: Below any time in milliseconds: a file without a time is older than every other.
_NO_TIME = -(2 ** 63)


class DetailDepth(enum.IntEnum):
    """How much detail a footprint keeps for a table, in the terms of section 4 of the object model."""

    #: The table, its columns, partition specs, physical schemas, writers, operations, and issues.
    SUMMARY = 0
    #: Also one row for each data file, and the partitions and commits.
    FILES = 1
    #: Also one row for each row group of the files whose footer was read.
    ROW_GROUPS = 2
    #: Also one row for each column chunk of those row groups.
    COLUMN_CHUNKS = 3

    @property
    def text(self) -> str:
        """The name of the depth in the file: Summary, Files, Row groups, Column chunks."""
        return ("Summary", "Files", "Row groups", "Column chunks")[self.value]


class Total:
    """A sum whose terms can be unknown: unknown when any term is, and unknown when nothing was added unless it
    starts at zero."""

    __slots__ = ("sum", "any", "unknown")

    def __init__(self, zero: bool = False) -> None:
        self.sum = 0
        self.any = zero
        self.unknown = False

    @property
    def value(self) -> Optional[int]:
        return self.sum if self.any and not self.unknown else None

    def add(self, term: Optional[int]) -> None:
        self.any = True
        if term is None:
            self.unknown = True
        else:
            self.sum += term


def distribution(values: List[int]) -> Optional[Dict[str, int]]:
    """The distribution of the format, with quartiles taken by position in the sorted values."""
    if not values:
        return None
    ordered = sorted(values)
    n = len(ordered)
    return {"count": n, "sum": sum(ordered), "minimum": ordered[0], "firstQuartile": ordered[n // 4],
            "median": ordered[n // 2], "thirdQuartile": ordered[3 * n // 4], "maximum": ordered[n - 1]}


def _time(milliseconds: Optional[int]) -> Optional[datetime.datetime]:
    return None if milliseconds is None else timestamp(milliseconds)


def _milliseconds(moment: Optional[datetime.datetime]) -> Optional[int]:
    """A time of a listing in milliseconds since 1970, the precision of the log, rounded down as in C#. A time without
    a zone is in UTC."""
    if moment is None:
        return None
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=datetime.timezone.utc)
    return (moment - _EPOCH) // _MILLISECOND


class CollectedColumn:
    __slots__ = ("name", "data_type", "file_name", "field_id", "is_nullable", "physical_name", "is_partition_column",
                 "description", "compressed_size", "uncompressed_size", "value_count", "null_count", "chunk_count",
                 "dictionary_chunk_count", "fallback_chunk_count", "dictionary_page_size",
                 "unencoded_byte_array_data_bytes", "codecs", "encodings", "file_count_without_column",
                 "largest_chunk_distinct_count", "log_null_count", "is_stored")

    def __init__(self, name: str, data_type: str, file_name: str) -> None:
        self.name = name
        self.data_type = data_type
        self.file_name = file_name
        self.field_id: Optional[int] = None
        self.is_nullable: Optional[bool] = None
        self.physical_name: Optional[str] = None
        self.is_partition_column = False
        self.description: Optional[str] = None
        self.compressed_size = Total()
        self.uncompressed_size = Total()
        self.value_count = Total()
        self.null_count = Total()
        self.chunk_count = Total()
        self.dictionary_chunk_count = Total()
        self.fallback_chunk_count = Total()
        self.dictionary_page_size = Total()
        self.unencoded_byte_array_data_bytes = Total()
        self.codecs: set = set()
        self.encodings: set = set()
        self.file_count_without_column: Optional[int] = None
        self.largest_chunk_distinct_count: Optional[int] = None
        self.log_null_count: Optional[int] = None
        self.is_stored = False


class CollectedPartition:
    __slots__ = ("values", "file_count", "file_size", "row_count", "deleted_row_count", "_last_modified",
                 "_modified_unknown")

    def __init__(self, values: List[Optional[str]]) -> None:
        self.values = values
        self.file_count = 0
        self.file_size = Total(True)
        self.row_count = Total(True)
        self.deleted_row_count = Total(True)
        self._last_modified: Optional[datetime.datetime] = None
        self._modified_unknown = False

    @property
    def last_modified_at(self) -> Optional[datetime.datetime]:
        return None if self._modified_unknown else self._last_modified

    def add_modified_at(self, moment: Optional[datetime.datetime]) -> None:
        if moment is None:
            self._modified_unknown = True
        elif self._last_modified is None or moment > self._last_modified:
            self._last_modified = moment


class PhysicalColumn:
    __slots__ = ("leaf", "column", "compressed_size", "uncompressed_size", "chunk_count")

    def __init__(self, leaf: ParquetLeafColumn, column: Optional[int]) -> None:
        self.leaf = leaf
        self.column = column
        self.compressed_size = Total(True)
        self.uncompressed_size = Total(True)
        self.chunk_count = 0


class PhysicalSchema:
    __slots__ = ("first_file", "file_count", "columns")

    def __init__(self, first_file: int, columns: List[PhysicalColumn]) -> None:
        self.first_file = first_file
        self.file_count = 0
        self.columns = columns


class Writer:
    __slots__ = ("first_file", "created_by", "is_v_ordered", "v_order_level", "file_count", "file_size")

    def __init__(self, first_file: int, created_by: Optional[str], is_v_ordered: bool,
                 v_order_level: Optional[str]) -> None:
        self.first_file = first_file
        self.created_by = created_by
        self.is_v_ordered = is_v_ordered
        self.v_order_level = v_order_level
        self.file_count = 0
        self.file_size = Total(True)


class CollectedFile:
    __slots__ = ("path", "file_size", "modified_at", "row_count", "deleted_row_count", "deletion_vector_size",
                 "partition", "clustering_provider", "is_v_ordered", "footer_size", "physical_schema", "writer",
                 "row_group_count", "compressed_size", "uncompressed_size", "row_groups")

    def __init__(self, path: str) -> None:
        self.path = path
        self.file_size: Optional[int] = None
        self.modified_at: Optional[datetime.datetime] = None
        self.row_count: Optional[int] = None
        self.deleted_row_count: Optional[int] = None
        self.deletion_vector_size: Optional[int] = None
        self.partition: Optional[int] = None
        self.clustering_provider: Optional[str] = None
        self.is_v_ordered: Optional[bool] = None
        self.footer_size: Optional[int] = None
        self.physical_schema: Optional[PhysicalSchema] = None
        self.writer: Optional[Writer] = None
        self.row_group_count: Optional[int] = None
        self.compressed_size: Optional[int] = None
        self.uncompressed_size: Optional[int] = None
        self.row_groups: Optional[List[Dict[str, Any]]] = None


class CollectedTable:
    """What was collected for one table, before the rows of the footprint are built."""

    def __init__(self, location: TableLocation, source: int) -> None:
        self.location = location
        self.source = source
        self.table_id: Optional[str] = None
        self.description: Optional[str] = None
        self.created_at: Optional[datetime.datetime] = None
        self.properties: Optional[Dict[str, str]] = None
        self.snapshot_id: Optional[str] = None
        self.snapshot_timestamp: Optional[datetime.datetime] = None
        self.delta: Optional[Dict[str, Any]] = None
        self.clustering_columns: Optional[List[int]] = None
        self.collected_level: Optional[int] = None
        self.depth = DetailDepth.SUMMARY
        self.totals: Dict[str, Any] = {}
        self.columns: List[CollectedColumn] = []
        self.partition_fields: Optional[List[Tuple[str, Optional[int]]]] = None
        self.partitions: List[CollectedPartition] = []
        self.physical_schemas: List[PhysicalSchema] = []
        self.writers: List[Writer] = []
        self.operations: List[Dict[str, Any]] = []
        self.files: List[CollectedFile] = []
        self.commits: List[Dict[str, Any]] = []
        self.issues: List[Issue] = []
        self.footers_read = 0


class PartitionOrdinals:
    """Numbers the values of each partition column from 0 in the order in which they first appear, a null being a
    value of its own, and puts the numbers in place of the values in the paths of the files."""

    def __init__(self) -> None:
        self._columns: Dict[str, Dict[Tuple[bool, str], int]] = {}

    def get(self, column: str, value: Optional[str]) -> int:
        values = self._columns.setdefault(column, {})
        key = (False, "") if value is None else (True, value)
        ordinal = values.get(key)
        if ordinal is None:
            ordinal = values[key] = len(values)
        return ordinal

    def sanitize(self, path: str) -> str:
        segments = path.split("/")
        changed = False
        for i in range(len(segments) - 1):
            segment = segments[i]
            equal = segment.find("=")
            if equal <= 0:
                continue
            column = segment[:equal]
            segments[i] = f"{column}=#{self.get(column, segment[equal + 1:])}"
            changed = True
        return "/".join(segments) if changed else path


def _fold(text: str) -> str:
    """A string for a comparison that ignores case as C# OrdinalIgnoreCase does: one character at a time."""
    return "".join(c if len(c.upper()) != 1 else c.upper() for c in text)


def _find_column(columns: List[CollectedColumn], name: str, by_file_name: bool) -> Optional[int]:
    for i, column in enumerate(columns):
        if (column.file_name if by_file_name else column.name) == name:
            return i
    match = None
    folded = _fold(name)
    for i, column in enumerate(columns):
        if _fold(column.file_name if by_file_name else column.name) == folded:
            if match is not None:
                return None
            match = i
    return match


def _map_leaf(leaf: ParquetLeafColumn, columns: List[CollectedColumn]) -> Optional[int]:
    """The column of a leaf: by the field id of its top-level field when the file and the table have field ids,
    otherwise by the name that the files use, which is the physical name with column mapping."""
    if leaf.top_level_field_id is not None and any(column.field_id is not None for column in columns):
        for i, column in enumerate(columns):
            if column.field_id == leaf.top_level_field_id:
                return i
        return None
    return _find_column(columns, leaf.path[0], True) if leaf.path else None


def _signature(footer: ParquetFooter) -> tuple:
    return tuple((e.name, e.type, e.type_length, e.repetition, e.num_children, e.converted_type, e.scale, e.precision,
                  e.field_id, e.logical_type) for e in footer.schema)


def _fallback(chunk: ParquetColumnChunk) -> Optional[bool]:
    """Whether a chunk with a dictionary fell back to another encoding: from the encoding statistics when the footer has
    them; without them, false when the chunk lists only dictionary and level encodings, and unknown otherwise."""
    if not chunk.has_dictionary_page:
        return False
    if chunk.encoding_stats is not None:
        data = dictionary = 0
        for stats in chunk.encoding_stats:
            if stats.is_data_page:
                data += stats.count
                if stats.is_dictionary_encoded:
                    dictionary += stats.count
        return dictionary < data
    for encoding in chunk.encodings:
        if encoding not in ("PLAIN_DICTIONARY", "RLE_DICTIONARY", "RLE", "BIT_PACKED"):
            return None
    return False


def _clustering(configuration: str, columns: List[CollectedColumn]) -> Optional[List[int]]:
    try:
        document = loads(configuration)
    except (ValueError, RecursionError):
        return None
    paths = document.get("clusteringColumns") if isinstance(document, dict) else None
    if not isinstance(paths, list):
        return None
    result: List[int] = []
    for path in paths:
        first = (path[0] if isinstance(path, list) and path and isinstance(path[0], str)
                 else path if isinstance(path, str) else None)
        column = _find_column(columns, first, True) if first is not None else None
        if column is not None and column not in result:
            result.append(column)
    return result


def _sample(count: int, maximum: int) -> List[int]:
    """The files whose footer is read: all of them, or a sample spread evenly over the sorted list."""
    if maximum == 0 or count <= maximum:
        return list(range(count))
    if maximum == 1:
        return [0]
    sample: List[int] = []
    previous = -1
    for i in range(maximum):
        index = i * (count - 1) // (maximum - 1)
        if index != previous:
            sample.append(index)
            previous = index
    return sample


def _footer_failure(error: BaseException, path: str) -> Issue:
    """The issue of a footer read that failed. A 401 or a 403 of the storage is AccessDenied, another failure of the
    storage FooterNotRead, a footer that the reader refuses has the code of its FooterError, and anything else is
    FooterNotRead. Whatever the code, the footer counts as not read."""
    if isinstance(error, StorageError):
        code = IssueCodes.ACCESS_DENIED if error.is_access_denied else IssueCodes.FOOTER_NOT_READ
        return Issue(code, f"The footer could not be read: {error}", path)
    if isinstance(error, FooterError):
        return Issue(error.code, str(error), path)
    return Issue(IssueCodes.FOOTER_NOT_READ, f"The footer could not be read: {type(error).__name__}: {error}", path)


def _excluded(root: str, path: str) -> bool:
    if root and not (len(path) > len(root) and path[len(root)] == "/" and path.startswith(root)):
        return False
    relative = path if not root else path[len(root) + 1:]
    slash = relative.find("/")
    return slash > 0 and relative[:slash] in _NON_DATA_FOLDERS


class TableCollector:
    """Collects one table with the options of a collection, through the storage of the collection."""

    def __init__(self, storage, options, checkpoints, footer_pool: Optional[Executor]) -> None:
        self.storage = storage
        self.options = options
        self.checkpoints = checkpoints
        self.footer_pool = footer_pool
        self.footer_reader = FooterReader(storage, options.footer_tail_bytes, options.include_data_values)

    def collect(self, location: TableLocation, source: int) -> CollectedTable:
        if location.format == TableFormats.PARQUET:
            return self._collect_parquet(location, source)
        issues = IssueList()
        table = CollectedTable(location, source)
        log: Optional[DeltaLog] = None
        try:
            reader = DeltaLogReader(self.storage, self.checkpoints, self.options.max_commits_per_table,
                                    self.options.include_data_values)
            log = reader.read(location.path, issues)
        except StorageError as error:
            issues.add(IssueCodes.ACCESS_DENIED if error.is_access_denied else IssueCodes.TABLE_NOT_READ,
                       f"The Delta log could not be read: {error}")
        except Exception as error:  # noqa: BLE001 - a failure on one table becomes an issue
            issues.add(IssueCodes.TABLE_NOT_READ, f"The Delta log could not be read: {type(error).__name__}: {error}")
        if log is not None and log.found:
            try:
                self._build(table, log, issues)
            except Exception as error:  # noqa: BLE001 - whatever was built is dropped, so the table stays consistent
                issues.add(IssueCodes.TABLE_NOT_READ,
                           f"The table could not be collected: {type(error).__name__}: {error}")
                table = CollectedTable(location, source)
        table.issues.extend(issues.to_list())
        return table

    def _collect_parquet(self, location: TableLocation, source: int) -> CollectedTable:
        """A folder of Parquet files with no table format: its files from one listing of the folder, its columns from
        the footer of the most recent file, and at level 2 every footer. Nothing is known of a history, a version, or
        metadata files, and every Parquet file of the folder is a file of the table."""
        issues = IssueList()
        table = CollectedTable(location, source)
        entries = None
        try:
            entries = self.storage.list(location.path)
        except StorageError as error:
            issues.add(IssueCodes.ACCESS_DENIED if error.is_access_denied else IssueCodes.TABLE_NOT_READ,
                       f"The table folder could not be listed: {error}")
        except Exception as error:  # noqa: BLE001 - a failure on one table becomes an issue
            issues.add(IssueCodes.TABLE_NOT_READ,
                       f"The table folder could not be listed: {type(error).__name__}: {error}")
        if entries is not None:
            try:
                self._build_parquet(table, entries, issues)
            except Exception as error:  # noqa: BLE001 - whatever was built is dropped, so the table stays consistent
                issues.add(IssueCodes.TABLE_NOT_READ,
                           f"The table could not be collected: {type(error).__name__}: {error}")
                table = CollectedTable(location, source)
        table.issues.extend(issues.to_list())
        return table

    def _build_parquet(self, table: CollectedTable, entries: List[Any], issues: IssueList) -> None:
        files = [LiveFile(urllib.parse.quote(entry.name, safe=""), entry.name, size=entry.size,
                          modification_time=_milliseconds(entry.last_modified))
                 for entry in sorted((entry for entry in entries if is_parquet_file(entry)),
                                     key=lambda entry: _ordinal(entry.name))]
        totals = table.totals
        # Every Parquet file of the folder is a file of the table, so none is unreferenced.
        totals["unreferencedFileCount"] = 0
        totals["unreferencedFileSize"] = 0
        totals["footerCount"] = 0
        table.collected_level = 1
        # The columns come from the most recent file, as the object model says: the latest modification time, then the
        # last name.
        newest = -1
        for i, file in enumerate(files):
            time = file.modification_time if file.modification_time is not None else _NO_TIME
            if newest < 0 or time >= (files[newest].modification_time if files[newest].modification_time is not None
                                      else _NO_TIME):
                newest = i
        collected = self._files(table, files)
        planner = FooterTailPlanner(self.options.footer_tail_bytes, self.options.footer_tail_floor_bytes,
                                    self.options.footer_tail_margin)
        schema_footer: Optional[ParquetFooter] = None
        if newest >= 0:
            schema_footer = self._read_columns_footer(table.location, files[newest], collected[newest], planner, issues)
            if schema_footer is not None:
                for name, data_type, is_nullable in top_level_fields(schema_footer):
                    column = CollectedColumn(name, data_type, name)
                    column.is_nullable = is_nullable
                    table.columns.append(column)
        aggregate = _FooterAggregate(table, self.options.depth)
        if self.options.level >= 2:
            self._read_footers(table, files, collected, aggregate, issues, planner, newest if newest >= 0 else None,
                               schema_footer)
        self._totals(table, files, collected, aggregate)

    def _read_columns_footer(self, location: TableLocation, file: LiveFile, collected: CollectedFile,
                             planner: FooterTailPlanner, issues: IssueList) -> Optional[ParquetFooter]:
        """The footer of the file that gives the columns of a folder of Parquet files, or None after the issue of its
        failure."""
        if file.size is None:
            issues.add(IssueCodes.FOOTER_NOT_READ, "The listing gives no size for the file, so its footer was not read.",
                       collected.path)
            return None
        try:
            footer = self.footer_reader.read(f"{location.path}/{file.path}", file.size, planner.current)
            planner.observe(footer.footer_length)
            return footer
        except Exception as error:  # noqa: BLE001 - every failure is an issue, as for the other footers
            issue = _footer_failure(error, collected.path)
            issues.add(issue.code, issue.message, issue.path)
            return None

    def _properties(self, metadata: Optional[MetadataAction]) -> Optional[Dict[str, str]]:
        if metadata is None:
            return None
        properties: Dict[str, str] = {}
        for key, value in (metadata.configuration or {}).items():
            # A check constraint is an expression that can hold data values.
            if not self.options.include_data_values and key.lower().startswith(_CONSTRAINT_PREFIX):
                continue
            properties[key] = value
        return properties

    def _build(self, table: CollectedTable, log: DeltaLog, issues: IssueList) -> None:
        metadata = log.metadata
        table.table_id = metadata.id if metadata is not None else None
        table.description = metadata.description if metadata is not None and metadata.description else None
        table.created_at = _time(metadata.created_time) if metadata is not None else None
        table.properties = self._properties(metadata)
        table.snapshot_id = None if log.version is None else str(log.version)
        table.snapshot_timestamp = log.snapshot_timestamp
        protocol = log.protocol
        delta: Dict[str, Any] = {
            "minReaderVersion": protocol.min_reader_version if protocol is not None else None,
            "minWriterVersion": protocol.min_writer_version if protocol is not None else None,
            "readerFeatures": None if protocol is None else list(protocol.reader_features or []),
            "writerFeatures": None if protocol is None else list(protocol.writer_features or []),
            "checkpointVersion": log.checkpoint_version,
            "commitsAfterCheckpoint": log.commits_after_checkpoint,
            # Without the whole state, the domains of the checkpoint are unknown, so the list is too.
            "domains": list(log.domains) if log.files_complete else None,
        }
        table.delta = delta
        totals = table.totals
        totals["commitCount"] = log.commit_count
        totals["metadataFileCount"] = log.metadata_file_count
        totals["metadataSize"] = log.metadata_size

        self._columns(table, metadata, issues)
        if "delta.clustering" in log.domains:
            table.clustering_columns = _clustering(log.domains["delta.clustering"], table.columns)
        self._history(table, log)
        totals["footerCount"] = 0
        table.collected_level = 1
        if not log.files_complete:
            return  # the files are unknown, so are their totals, and no detail can be complete
        files = log.files
        collected = self._files(table, files)
        totals["nonLiveFileCount"] = log.non_live_file_count
        totals["nonLiveFileSize"] = log.non_live_file_size
        totals["nonLiveOldestRemovedAt"] = _time(log.non_live_oldest_removed_at)
        totals["nonLiveNewestRemovedAt"] = _time(log.non_live_newest_removed_at)
        self._unreferenced(table, files, issues)
        aggregate = _FooterAggregate(table, self.options.depth)
        if self.options.level >= 2:
            self._read_footers(table, files, collected, aggregate, issues)
        self._totals(table, files, collected, aggregate)

    def _unreferenced(self, table: CollectedTable, files: List[LiveFile], issues: IssueList) -> None:
        """Counts the Parquet files of the table folder, at any depth, outside _delta_log and _change_data, that no
        live file of the version is. When the listing fails, both totals stay unknown."""
        root = table.location.path.strip("/")
        try:
            listing = list_files(self.storage, root, _NON_DATA_FOLDERS)
        except StorageError as error:
            issues.add(IssueCodes.ACCESS_DENIED if error.is_access_denied else IssueCodes.DATA_FILES_NOT_LISTED,
                       f"The table folder could not be listed for its data files: {error}")
            return
        except Exception as error:  # noqa: BLE001
            issues.add(IssueCodes.DATA_FILES_NOT_LISTED,
                       f"The table folder could not be listed for its data files: {type(error).__name__}: {error}")
            return
        location = table.location.location
        location = location.replace("\\", "/").rstrip("/") if location is not None else None
        referenced = set()
        for file in files:
            path = file.path
            if (file.is_outside_table and location and len(path) > len(location) + 1
                    and path[len(location)] == "/" and path.startswith(location)):
                path = path[len(location) + 1:]
            referenced.add(path)
        count = size = 0
        for entry in listing:
            if entry.is_directory or not entry.name.lower().endswith(".parquet") or _excluded(root, entry.path):
                continue
            relative = entry.path if not root else entry.path[len(root) + 1:]
            if relative in referenced:
                continue
            count += 1
            size += entry.size or 0
        table.totals["unreferencedFileCount"] = count
        table.totals["unreferencedFileSize"] = size

    def _columns(self, table: CollectedTable, metadata: Optional[MetadataAction], issues: IssueList) -> None:
        delta_columns: List[DeltaColumn] = []
        if metadata is not None and metadata.schema_string is not None:
            try:
                delta_columns = parse_schema(metadata.schema_string)
            except (ValueError, RecursionError) as error:
                issues.add(IssueCodes.DELTA_SCHEMA_NOT_PARSED, f"The schema of the table could not be parsed: {error}")
        partition_columns = (metadata.partition_columns if metadata is not None else None) or []
        for column in delta_columns:
            collected = CollectedColumn(column.name, column.data_type, column.file_name)
            collected.field_id = column.field_id
            collected.is_nullable = column.is_nullable
            collected.physical_name = (column.mapped_name if column.mapped_name is not None
                                       and column.mapped_name != column.name else None)
            collected.description = column.description
            table.columns.append(collected)
        if partition_columns:
            table.partition_fields = []
            for name in partition_columns:
                index = _find_column(table.columns, name, False)
                if index is not None:
                    table.columns[index].is_partition_column = True
                table.partition_fields.append((name, index))

    def _history(self, table: CollectedTable, log: DeltaLog) -> None:
        operations: Dict[str, Dict[str, Any]] = {}
        oldest: Optional[datetime.datetime] = None
        for commit in log.history:
            moment = commit.timestamp
            if moment is not None and (oldest is None or moment < oldest):
                oldest = moment
            info: Optional[CommitInfoAction] = commit.info
            if info is not None and info.operation:
                operation = operations.get(info.operation)
                if operation is None:
                    operation = operations[info.operation] = {"name": info.operation, "commitCount": 0, "lastAt": None}
                    table.operations.append(operation)
                operation["commitCount"] += 1
                if moment is not None and (operation["lastAt"] is None or moment > operation["lastAt"]):
                    operation["lastAt"] = moment
            if self.options.depth >= DetailDepth.FILES:
                table.commits.append({
                    "commitId": str(commit.version),
                    "timestamp": moment,
                    "operation": info.operation if info is not None else None,
                    "engine": info.engine_info if info is not None else None,
                    "isBlindAppend": info.is_blind_append if info is not None else None,
                    "metrics": None if info is None else dict(info.operation_metrics or {}),
                    "parameters": info.operation_parameters if info is not None and self.options.include_data_values
                    else None,
                })
        table.totals["commitsRead"] = len(log.history)
        table.totals["oldestCommitAt"] = oldest

    def _files(self, table: CollectedTable, files: List[LiveFile]) -> List[CollectedFile]:
        ordinals = PartitionOrdinals()
        partitions: Dict[tuple, int] = {}
        fields = table.partition_fields
        collected: List[CollectedFile] = []
        for file in files:
            partition: Optional[int] = None
            if fields is not None:
                values = [self._partition_value(file, name, source, table.columns) for name, source in fields]
                for (name, _), value in zip(fields, values):
                    ordinals.get(name, value)
                signature = tuple((value is not None, value or "") for value in values)
                partition = partitions.get(signature)
                if partition is None:
                    partition = partitions[signature] = len(table.partitions)
                    table.partitions.append(CollectedPartition(values))
            path = file.path if self.options.include_partition_values else ordinals.sanitize(file.path)
            row = CollectedFile(path)
            row.file_size = file.size
            row.modified_at = _time(file.modification_time)
            row.row_count = file.num_records
            row.deleted_row_count = file.deleted_row_count
            row.deletion_vector_size = file.deletion_vector_size
            row.partition = partition
            row.clustering_provider = file.clustering_provider
            row.is_v_ordered = file.vorder_tag
            collected.append(row)
        return collected

    @staticmethod
    def _partition_value(file: LiveFile, name: str, source: Optional[int],
                         columns: List[CollectedColumn]) -> Optional[str]:
        values = file.partition_values
        if values is None:
            return None
        # With column mapping the log keys the partition values by physical name.
        if source is not None and columns[source].file_name in values:
            return values[columns[source].file_name]
        return values.get(name)

    def _read_footers(self, table: CollectedTable, files: List[LiveFile], collected: List[CollectedFile],
                      aggregate: "_FooterAggregate", issues: IssueList, planner: Optional[FooterTailPlanner] = None,
                      known: Optional[int] = None, known_footer: Optional[ParquetFooter] = None) -> None:
        sample = _sample(len(files), self.options.max_footers_per_table)
        if planner is None:
            planner = FooterTailPlanner(self.options.footer_tail_bytes, self.options.footer_tail_floor_bytes,
                                        self.options.footer_tail_margin)
        file_issues: List[Optional[Issue]] = [None] * len(files)
        if known is not None:
            # The footer of this file was read already, for the columns: it is not read again, and a failure is not
            # reported twice. When the sample of a limit leaves the file out, it takes the place of the last file of
            # the sample, so that the limit holds.
            if known in sample:
                sample.remove(known)
            elif sample:
                sample.pop()
            if known_footer is not None:
                file_issues[known] = self._absorb(files[known], collected[known], known, known_footer, aggregate)
        # The first files of a table start together, so they are read with the initial tail, as in C#; the later ones
        # with the tail that the footers read so far give.
        together = min(self.options.parallelism, len(sample))
        initial = planner.current

        def read(position: int) -> None:
            index = sample[position]
            tail = initial if position < together else planner.current
            file_issues[index] = self._read_footer(table.location, files[index], collected[index], index, aggregate,
                                                   planner, tail)

        if self.footer_pool is None or len(sample) <= 1:
            for position in range(len(sample)):
                read(position)
        else:
            futures = [self.footer_pool.submit(read, position) for position in range(len(sample))]
            for future in futures:
                future.result()
        for issue in file_issues:
            if issue is not None:
                issues.add(issue.code, issue.message, issue.path)

    def _read_footer(self, location: TableLocation, file: LiveFile, collected: CollectedFile, index: int,
                     aggregate: "_FooterAggregate", planner: FooterTailPlanner, tail: int) -> Optional[Issue]:
        if file.is_outside_table:
            return Issue(IssueCodes.FILE_OUTSIDE_TABLE, "The file is outside the table folder, so its footer was not read.",
                         collected.path)
        if file.size is None:
            return Issue(IssueCodes.FOOTER_NOT_READ, "The log gives no size for the file, so its footer was not read.",
                         collected.path)
        try:
            footer = self.footer_reader.read(f"{location.path}/{file.path}", file.size, tail)
            planner.observe(footer.footer_length)
        except Exception as error:  # noqa: BLE001 - any failure: the footer counts as not read
            return _footer_failure(error, collected.path)
        return self._absorb(file, collected, index, footer, aggregate)

    @staticmethod
    def _absorb(file: LiveFile, collected: CollectedFile, index: int, footer: ParquetFooter,
                aggregate: "_FooterAggregate") -> Optional[Issue]:
        if any(not chunk.has_metadata for group in footer.row_groups for chunk in group.columns):
            return Issue(IssueCodes.FOOTER_COLUMNS_ENCRYPTED, "Column chunks of the file have no metadata, as "
                         "encrypted columns, so the footer counts as not read.", collected.path)
        aggregate.absorb(index, file, collected, footer)
        return None

    def _totals(self, table: CollectedTable, files: List[LiveFile], collected: List[CollectedFile],
                aggregate: "_FooterAggregate") -> None:
        file_size, rows, deleted = Total(True), Total(True), Total(True)
        vordered = 0
        vorder_known = True
        last_modified: Optional[datetime.datetime] = None
        modified_known = len(files) > 0
        sizes: List[int] = []
        for file in collected:
            file_size.add(file.file_size)
            rows.add(file.row_count)
            deleted.add(file.deleted_row_count)
            if file.file_size is not None:
                sizes.append(file.file_size)
            if file.is_v_ordered is None:
                vorder_known = False
            elif file.is_v_ordered:
                vordered += 1
            if file.modified_at is None:
                modified_known = False
            elif last_modified is None or file.modified_at > last_modified:
                last_modified = file.modified_at
            if file.partition is not None:
                partition = table.partitions[file.partition]
                partition.file_count += 1
                partition.file_size.add(file.file_size)
                partition.row_count.add(file.row_count)
                partition.deleted_row_count.add(file.deleted_row_count)
                partition.add_modified_at(file.modified_at)
        totals = table.totals
        totals["fileCount"] = len(files)
        totals["fileSize"] = file_size.value
        totals["rowCount"] = rows.value
        totals["deletedRowCount"] = deleted.value
        totals["lastModifiedAt"] = last_modified if modified_known else None
        totals["vOrderedFileCount"] = vordered if vorder_known else None
        totals["fileSizes"] = distribution(sizes) if len(sizes) == len(files) else None
        if table.partition_fields is not None:
            totals["partitionCount"] = len(table.partitions)
            partition_sizes = [partition.file_size.value for partition in table.partitions]
            totals["partitionSizes"] = (distribution(partition_sizes) if None not in partition_sizes else None)
        level_two = self.options.level >= 2 and (aggregate.footer_count > 0 or not files)
        totals["footerCount"] = aggregate.footer_count
        table.footers_read = aggregate.footer_count
        table.collected_level = 2 if level_two else 1
        if level_two:
            aggregate.finish(table)
        else:
            self._log_null_counts(table, files)
        depth = self.options.depth
        if not level_two and depth > DetailDepth.FILES:
            depth = DetailDepth.FILES
        table.depth = depth
        if depth >= DetailDepth.FILES:
            table.files.extend(collected)
        else:
            table.partitions.clear()

    @staticmethod
    def _log_null_counts(table: CollectedTable, files: List[LiveFile]) -> None:
        """At level 1, the null count of a column is the sum over the statistics of the log, when every file has one."""
        if not files:
            return
        found_any = False
        for column in table.columns:
            total = 0
            known = True
            for file in files:
                count = None
                if file.null_counts is not None:
                    count = next((value for name, value in file.null_counts if name == column.file_name), None)
                if count is None:
                    known = False
                    break
                total += count
            if known:
                column.log_null_count = total
                found_any = True
        if found_any:
            table.totals["columnTotalsSource"] = "Table format"


class _FooterAggregate:
    """The totals of level 2 over the footers read. absorb() is safe to call from several threads."""

    def __init__(self, table: CollectedTable, depth: DetailDepth) -> None:
        self._lock = threading.Lock()
        self.table = table
        self.depth = depth
        self.schemas: Dict[tuple, PhysicalSchema] = {}
        self.writers: Dict[tuple, Writer] = {}
        self.row_group_rows: List[int] = []
        self.row_group_count = 0
        self.compressed = 0
        self.uncompressed = 0
        self.footer_size = 0
        self.stale = 0
        self.footer_count = 0

    def absorb(self, index: int, file: LiveFile, collected: CollectedFile, footer: ParquetFooter) -> None:
        footer_vorder = False
        level: Optional[str] = None
        for key_value in footer.key_value_metadata:
            key = key_value.key.lower()
            if key.endswith("vorder.enabled"):
                footer_vorder = key_value.value is not None and key_value.value.lower() == "true"
            elif key.endswith("vorder.level"):
                level = key_value.value
        vordered = file.vorder_tag is True or footer_vorder
        signature = _signature(footer)
        table = self.table
        with self._lock:
            schema = self.schemas.get(signature)
            if schema is None:
                columns = [PhysicalColumn(leaf, _map_leaf(leaf, table.columns)) for leaf in footer.leaves]
                schema = self.schemas[signature] = PhysicalSchema(index, columns)
            schema.first_file = min(schema.first_file, index)
            schema.file_count += 1
            writer_key = (footer.created_by, vordered, level)
            writer = self.writers.get(writer_key)
            if writer is None:
                writer = self.writers[writer_key] = Writer(index, footer.created_by, vordered, level)
            writer.first_file = min(writer.first_file, index)
            writer.file_count += 1
            writer.file_size.add(file.size)

            collected.is_v_ordered = vordered
            collected.footer_size = footer.footer_length
            collected.physical_schema = schema
            collected.writer = writer
            collected.row_group_count = len(footer.row_groups)
            if collected.row_count is None:
                collected.row_count = footer.num_rows
            groups: Optional[List[Dict[str, Any]]] = [] if self.depth >= DetailDepth.ROW_GROUPS else None
            file_compressed = file_uncompressed = 0
            for g, group in enumerate(footer.row_groups):
                self.row_group_count += 1
                self.row_group_rows.append(group.num_rows)
                chunks: Optional[List[Dict[str, Any]]] = [] if self.depth >= DetailDepth.COLUMN_CHUNKS else None
                group_compressed = 0
                for c, chunk in enumerate(group.columns):
                    physical = schema.columns[c]
                    physical.compressed_size.add(chunk.total_compressed_size)
                    physical.uncompressed_size.add(chunk.total_uncompressed_size)
                    physical.chunk_count += 1
                    group_compressed += chunk.total_compressed_size
                    file_compressed += chunk.total_compressed_size
                    file_uncompressed += chunk.total_uncompressed_size
                    if physical.column is None:
                        self.stale += chunk.total_compressed_size
                    else:
                        self._add_to_column(table.columns[physical.column], chunk, physical.leaf)
                    if chunks is not None:
                        chunks.append(self._chunk(c, chunk))
                if groups is not None:
                    groups.append({"ordinal": g, "rowCount": group.num_rows, "uncompressedSize": group.total_byte_size,
                                   "compressedSize": group.total_compressed_size if group.total_compressed_size
                                   is not None else group_compressed, "chunks": chunks})
            collected.compressed_size = file_compressed
            collected.uncompressed_size = file_uncompressed
            collected.row_groups = groups
            self.compressed += file_compressed
            self.uncompressed += file_uncompressed
            self.footer_size += footer.footer_length
            self.footer_count += 1

    def finish(self, table: CollectedTable) -> None:
        """Writes the totals of level 2 into the table, and orders the schemas and the writers by their first file."""
        totals = table.totals
        totals["rowGroupCount"] = self.row_group_count
        totals["compressedSize"] = self.compressed
        totals["uncompressedSize"] = self.uncompressed
        totals["footerSize"] = self.footer_size
        totals["staleColumnSize"] = self.stale
        totals["columnTotalsSource"] = "Footers"
        totals["rowGroupRowCounts"] = distribution(self.row_group_rows)
        table.physical_schemas = sorted(self.schemas.values(), key=lambda schema: schema.first_file)
        table.writers = sorted(self.writers.values(), key=lambda writer: writer.first_file)
        for i, column in enumerate(table.columns):
            without = 0
            for schema in table.physical_schemas:
                if any(physical.column == i for physical in schema.columns):
                    column.is_stored = True
                else:
                    without += schema.file_count
            # A partition column that no file stores has no totals, as the object model says.
            column.file_count_without_column = None if column.is_partition_column and not column.is_stored else without

    @staticmethod
    def _add_to_column(column: CollectedColumn, chunk: ParquetColumnChunk, leaf: ParquetLeafColumn) -> None:
        column.compressed_size.add(chunk.total_compressed_size)
        column.uncompressed_size.add(chunk.total_uncompressed_size)
        column.value_count.add(chunk.num_values)
        column.null_count.add(chunk.null_count)
        column.chunk_count.add(1)
        dictionary = chunk.has_dictionary_page
        column.dictionary_chunk_count.add(1 if dictionary else 0)
        fallback = _fallback(chunk)
        column.fallback_chunk_count.add(None if fallback is None else 1 if fallback else 0)
        column.dictionary_page_size.add(chunk.dictionary_page_size if dictionary else 0)
        if leaf.physical_type == "BYTE_ARRAY":
            column.unencoded_byte_array_data_bytes.add(chunk.unencoded_byte_array_data_bytes)
        if chunk.codec is not None:
            column.codecs.add(chunk.codec)
        column.encodings.update(chunk.encodings)
        distinct = chunk.distinct_count
        if distinct is not None and (column.largest_chunk_distinct_count is None
                                     or distinct > column.largest_chunk_distinct_count):
            column.largest_chunk_distinct_count = distinct

    @staticmethod
    def _chunk(ordinal: int, chunk: ParquetColumnChunk) -> Dict[str, Any]:
        data_pages: Optional[int] = None
        dictionary_pages: Optional[int] = None
        if chunk.encoding_stats is not None:
            data_pages = dictionary_pages = 0
            for stats in chunk.encoding_stats:
                if stats.is_data_page:
                    data_pages += stats.count
                    if stats.is_dictionary_encoded:
                        dictionary_pages += stats.count
        dictionary = chunk.has_dictionary_page
        return {
            "leaf": ordinal,
            "codec": chunk.codec,
            "encodings": sorted(chunk.encodings, key=_ordinal),
            "valueCount": chunk.num_values,
            "nullCount": chunk.null_count,
            "compressedSize": chunk.total_compressed_size,
            "uncompressedSize": chunk.total_uncompressed_size,
            "hasDictionaryPage": dictionary,
            "dictionaryPageSize": chunk.dictionary_page_size if dictionary else None,
            "dataPageCount": data_pages,
            "dictionaryEncodedPageCount": dictionary_pages,
            "unencodedByteArrayDataBytes": chunk.unencoded_byte_array_data_bytes,
            "bloomFilterSize": chunk.bloom_filter_length,
            "minValue": chunk.min_value,
            "maxValue": chunk.max_value,
            "distinctCount": chunk.distinct_count,
        }


def _ordinal(text: str):
    return text.encode("utf-16-be", "surrogatepass")
