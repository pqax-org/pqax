"""The collection: the tables of a storage collected in parallel, and the footprint built with pqax.Builder."""
from __future__ import annotations

import dataclasses
import datetime
import math
import threading
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Callable, Dict, List, Optional, Sequence, Union

from .._schema import FIELDS
from ..builder import Builder
from ._table import CollectedTable, DetailDepth, TableCollector
from .discovery import TableLocation, discover, list_items
from .footer import DEFAULT_TAIL_BYTES, MAX_FOOTER_LENGTH

__all__ = ["LIBRARY_NAME", "CollectionProgress", "CollectorOptions", "DetailDepth", "collect"]

#: The name that the footprint records as collectorLib.
LIBRARY_NAME = "pqax.collect"

_DEPTHS = {"summary": DetailDepth.SUMMARY, "files": DetailDepth.FILES, "rowgroups": DetailDepth.ROW_GROUPS,
           "columnchunks": DetailDepth.COLUMN_CHUNKS}


def _depth(value: Union[DetailDepth, str, int]) -> DetailDepth:
    if isinstance(value, str):
        depth = _DEPTHS.get(value.replace(" ", "").replace("_", "").lower())
        if depth is None:
            raise ValueError(f"Unknown depth {value!r}: use summary, files, rowGroups, or columnChunks.")
        return depth
    if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= 3:
        raise ValueError(f"Unknown depth {value!r}.")
    return DetailDepth(value)


@dataclasses.dataclass
class CollectorOptions:
    """What a collection reads and what it keeps, as CollectorOptions of the C# collector."""

    #: 1 reads the Delta logs only, 2 also the Parquet footers.
    level: int = 2
    #: The detail kept for every table: less when it cannot be complete, Summary when the files are unknown and at
    #: most Files when no footer was read. A string such as "rowGroups" is accepted too.
    depth: Union[DetailDepth, str] = DetailDepth.COLUMN_CHUNKS
    #: The footers read for each table, spread evenly over the files sorted by modification time; 0 for all.
    max_footers_per_table: int = 0
    #: The commit files read for each table: the commits that the state needs, then older ones up to the limit.
    max_commits_per_table: int = 30
    #: The storage requests in flight at the same time, for the whole collection.
    parallelism: int = 16
    #: Keeps the bounds of the column chunks, the parameters of the operations, and the check constraints.
    include_data_values: bool = False
    #: Keeps the values of the partitions and the file paths as the log has them.
    include_partition_values: bool = False
    #: The bytes read from the end of the first file of a table, and the most read from any file in one request.
    footer_tail_bytes: int = DEFAULT_TAIL_BYTES
    #: The factor applied to the largest footer of a table seen so far, to choose the tail of its next files.
    footer_tail_margin: float = 1.25
    #: The fewest bytes read from the end of a file.
    footer_tail_floor_bytes: int = 8192

    def validate(self) -> None:
        """Checks the values, raising ValueError when one is out of range."""
        def integer(name: str) -> int:
            value = getattr(self, name)
            if isinstance(value, bool) or not isinstance(value, int):
                raise ValueError(f"{name} is an integer, not {value!r}.")
            return value

        if integer("level") not in (1, 2):
            raise ValueError("The collector supports levels 1 and 2.")
        self.depth = _depth(self.depth)
        if integer("max_footers_per_table") < 0:
            raise ValueError("max_footers_per_table is negative.")
        if integer("max_commits_per_table") < 0:
            raise ValueError("max_commits_per_table is negative.")
        if integer("parallelism") < 1:
            raise ValueError("parallelism must allow at least one request.")
        if not 12 <= integer("footer_tail_bytes") <= MAX_FOOTER_LENGTH:
            raise ValueError("footer_tail_bytes must hold at least 12 bytes and at most the largest footer.")
        margin = self.footer_tail_margin
        if isinstance(margin, bool) or not isinstance(margin, (int, float)) or not margin >= 1.0 or math.isinf(margin):
            raise ValueError("footer_tail_margin must be at least 1.")
        if integer("footer_tail_floor_bytes") < 12:
            raise ValueError("footer_tail_floor_bytes must hold at least the 12 bytes around a footer.")


@dataclasses.dataclass(frozen=True)
class CollectionProgress:
    """The progress of a collection, reported each time a table is done."""

    #: The table that is done.
    table: TableLocation
    #: The tables done so far, this one included.
    tables_completed: int
    #: The tables of the collection.
    table_count: int
    #: The footers read for this table.
    footers_read: int
    #: The issues of this table.
    issue_count: int
    #: The storage requests of the collection so far.
    request_count: int
    #: The bytes read by the collection so far.
    bytes_read: int


class _CountingStorage:
    """Wraps the storage of a collection: it limits the requests in flight to the parallelism of the options, and it
    counts the calls and the bytes, for a storage that does not count them itself."""

    def __init__(self, inner: Any, parallelism: int) -> None:
        self._inner = inner
        self._gate = threading.BoundedSemaphore(parallelism)
        self._lock = threading.Lock()
        self.request_count = 0
        self.bytes_read = 0

    def _counted(self, read: int = 0) -> None:
        with self._lock:
            self.request_count += 1
            self.bytes_read += read

    def list(self, path: str):
        with self._gate:
            self._counted()
            return self._inner.list(path)

    def list_files(self, path: str, excluded: Sequence[str] = ()):
        if getattr(self._inner, "list_files", None) is None:
            # Each folder goes through list(), so each one is a call, counted and limited as any other.
            from .storage import _walk
            return _walk(self, path, excluded)
        with self._gate:
            self._counted()
            return self._inner.list_files(path, excluded)

    def read_range(self, path: str, offset: int, length: int) -> bytes:
        with self._gate:
            data = self._inner.read_range(path, offset, length)
            self._counted(len(data))
            return data


def _now() -> datetime.datetime:
    now = datetime.datetime.now(datetime.timezone.utc)
    return now.replace(microsecond=now.microsecond // 1000 * 1000)


def _statistics(storage: Any):
    requests = getattr(storage, "request_count", None)
    read = getattr(storage, "bytes_read", None)
    return (requests, read) if isinstance(requests, int) and isinstance(read, int) else None


def collect(storage: Any, tables: Optional[Sequence[TableLocation]] = None, *, items: Optional[Sequence[str]] = None,
            level: int = 2, depth: Union[DetailDepth, str] = DetailDepth.COLUMN_CHUNKS, max_footers: int = 0,
            max_commits: int = 30, parallelism: int = 16, include_data_values: bool = False,
            include_partition_values: bool = False, options: Optional[CollectorOptions] = None,
            progress: Optional[Callable[[CollectionProgress], None]] = None,
            checkpoint_reader: Any = "auto") -> Builder:
    """Collects the footprint of the Delta tables, and of the folders of Parquet files, of a storage and returns a
    pqax.Builder with it.

    tables are the tables to collect, as discover() or discover_folder() finds them; when None, the tables of items, or
    of every item that list_items() finds. The keywords are the options, or options= gives them all. progress receives a
    CollectionProgress each time a table is done, one call at a time. checkpoint_reader reads the Delta checkpoints in
    Parquet: "auto" uses pyarrow and raises ImportError naming pqax[collect] when it is missing, None reads none (a
    table that needs its checkpoint then gets an issue), and any callable (storage, path) -> actions is used as it is.

    The builder has the collection of the manifest: collectorLib, collectorLibVersion, startedAt, completedAt, level,
    includesDataValues, includesPartitionValues, requestCount, and bytesRead. The caller sets collectorApp and
    collectorAppVersion, and writes the file with builder.write(path). A failure on one table or file becomes an issue
    in the footprint, and the collection goes on.
    """
    if storage is None:
        raise TypeError("The storage is missing.")
    run = dataclasses.replace(options) if options is not None else CollectorOptions(
        level, depth, max_footers, max_commits, parallelism, include_data_values, include_partition_values)
    run.validate()
    if isinstance(checkpoint_reader, str):
        if checkpoint_reader != "auto":
            raise ValueError('checkpoint_reader is "auto", None, or a callable.')
        from .checkpoint import ArrowCheckpointReader
        checkpoint_reader = ArrowCheckpointReader()
    if tables is None:
        found: List[TableLocation] = []
        for item in (items if items is not None else list_items(storage)):
            found.extend(discover(storage, item))
        tables = found
    tables = list(tables)
    for table in tables:
        if not isinstance(table, TableLocation):
            raise TypeError("A table is a TableLocation, as discover() returns.")

    from .. import __version__
    started = _now()
    before = _statistics(storage)
    sources: List[TableLocation] = []
    source_index: Dict[tuple, int] = {}
    table_source: List[int] = []
    for table in tables:
        # Two lakehouses of the same name in two workspaces are two sources: the location tells them apart.
        key = (table.source_kind, table.source_name, table.source_location)
        if key not in source_index:
            source_index[key] = len(sources)
            sources.append(table)
        table_source.append(source_index[key])

    counting = _CountingStorage(storage, run.parallelism)
    results: List[Optional[CollectedTable]] = [None] * len(tables)
    report = threading.Lock()
    completed = [0]
    workers = max(1, min(run.parallelism, len(tables)))
    with ThreadPoolExecutor(max_workers=run.parallelism, thread_name_prefix="pqax-footer") as footer_pool, \
            ThreadPoolExecutor(max_workers=workers, thread_name_prefix="pqax-table") as table_pool:
        collector = TableCollector(counting, run, checkpoint_reader, footer_pool if run.parallelism > 1 else None)

        def one(i: int) -> None:
            result = collector.collect(tables[i], table_source[i])
            results[i] = result
            if progress is not None:
                with report:  # one report at a time, so that the receiver does not have to be thread-safe
                    completed[0] += 1
                    progress(CollectionProgress(tables[i], completed[0], len(tables), result.footers_read,
                                                len(result.issues), counting.request_count, counting.bytes_read))

        for future in [table_pool.submit(one, i) for i in range(len(tables))]:
            future.result()

    after = _statistics(storage)
    collection: Dict[str, Any] = {
        "collectorLib": LIBRARY_NAME,
        "collectorLibVersion": __version__,
        "startedAt": started,
        "completedAt": _now(),
        "level": run.level,
        "includesDataValues": run.include_data_values,
        "includesPartitionValues": run.include_partition_values,
        "requestCount": after[0] - before[0] if before and after else counting.request_count,
        "bytesRead": after[1] - before[1] if before and after else counting.bytes_read,
    }
    return _assemble(collection, sources, [result for result in results if result is not None], run)


def _without_none(values: Dict[str, Any]) -> Dict[str, Any]:
    return {name: value for name, value in values.items() if value is not None}


def _assemble(collection: Dict[str, Any], sources: List[TableLocation], tables: List[CollectedTable],
              options: CollectorOptions) -> Builder:
    """The rows of the footprint, table by table, so that rows of the same parent stay together in every table."""
    builder = Builder(collection)
    source_ids = [builder.add_source(kind=source.source_kind, name=source.source_name, location=source.source_location,
                                     identifiers=source.source_identifiers) for source in sources]
    for table in tables:
        _add_table(builder, table, source_ids[table.source], options)
    return builder


def _add_table(builder: Builder, table: CollectedTable, source_id: int, options: CollectorOptions) -> None:
    totals = table.totals
    location = table.location
    row: Dict[str, Any] = {
        "source": source_id, "namespace": list(location.namespace), "name": location.name,
        "location": location.location, "format": location.format, "tableId": table.table_id,
        "description": table.description, "createdAt": table.created_at, "properties": table.properties,
        "snapshotId": table.snapshot_id, "snapshotTimestamp": table.snapshot_timestamp,
        "delta": _without_none(table.delta) if table.delta is not None else None,
        "collectedLevel": table.collected_level, "detailDepth": table.depth.text,
    }
    for name in FIELDS["tables"][FIELDS["tables"].index("fileCount"):]:
        row[name] = totals.get(name)
    table_id = builder.add_table(**row)

    column_ids = []
    for column in table.columns:
        column_ids.append(builder.add_column(
            table=table_id, fieldId=column.field_id, name=column.name, dataType=column.data_type,
            isNullable=column.is_nullable, physicalName=column.physical_name,
            isPartitionColumn=column.is_partition_column, description=column.description,
            compressedSize=column.compressed_size.value, uncompressedSize=column.uncompressed_size.value,
            valueCount=column.value_count.value,
            nullCount=column.null_count.value if column.null_count.value is not None else column.log_null_count,
            chunkCount=column.chunk_count.value, dictionaryChunkCount=column.dictionary_chunk_count.value,
            fallbackChunkCount=column.fallback_chunk_count.value,
            dictionaryPageSize=column.dictionary_page_size.value,
            unencodedByteArrayDataBytes=column.unencoded_byte_array_data_bytes.value,
            codecs=sorted(column.codecs, key=_ordinal) or None, encodings=sorted(column.encodings, key=_ordinal) or None,
            fileCountWithoutColumn=column.file_count_without_column,
            largestChunkDistinctCount=column.largest_chunk_distinct_count))
    if table.clustering_columns is not None:
        builder.update("tables", table_id, clusteringColumns=[column_ids[i] for i in table.clustering_columns])

    spec_id: Optional[int] = None
    if table.partition_fields is not None:
        spec_id = builder.add_partition_spec(table=table_id, isDefault=True)
        for position, (name, source) in enumerate(table.partition_fields):
            builder.add_partition_field(partitionSpec=spec_id, position=position, name=name,
                                        sourceColumns=None if source is None else [column_ids[source]],
                                        transform="identity")

    schema_ids: Dict[int, int] = {}
    physical_ids: Dict[int, List[int]] = {}
    for schema in table.physical_schemas:
        schema_id = builder.add_physical_schema(table=table_id, fileCount=schema.file_count)
        schema_ids[id(schema)] = schema_id
        ids = []
        for physical in schema.columns:
            leaf = physical.leaf
            ids.append(builder.add_physical_column(
                physicalSchema=schema_id, ordinal=leaf.ordinal, path=list(leaf.path), physicalType=leaf.physical_type,
                typeLength=leaf.type_length, logicalType=leaf.type_annotation, repetition=leaf.repetition,
                fieldId=leaf.field_id, column=None if physical.column is None else column_ids[physical.column],
                compressedSize=physical.compressed_size.value, uncompressedSize=physical.uncompressed_size.value,
                chunkCount=physical.chunk_count))
        physical_ids[id(schema)] = ids

    writer_ids: Dict[int, int] = {}
    for writer in table.writers:
        writer_ids[id(writer)] = builder.add_writer(
            table=table_id, createdBy=writer.created_by, isVOrdered=writer.is_v_ordered,
            vOrderLevel=writer.v_order_level, fileCount=writer.file_count, fileSize=writer.file_size.value)

    for operation in table.operations:
        builder.add_operation(table=table_id, name=operation["name"], commitCount=operation["commitCount"],
                              lastAt=operation["lastAt"])
    for issue in table.issues:
        builder.add_issue(table=table_id, path=issue.path, code=issue.code, message=issue.message)

    partition_ids = []
    for partition in table.partitions:
        partition_ids.append(builder.add_partition(
            table=table_id, partitionSpec=spec_id,
            values=list(partition.values) if options.include_partition_values else None,
            fileCount=partition.file_count, fileSize=partition.file_size.value, rowCount=partition.row_count.value,
            deletedRowCount=partition.deleted_row_count.value, lastModifiedAt=partition.last_modified_at))

    for file in table.files:
        file_id = builder.add_file(
            table=table_id, path=file.path, fileSize=file.file_size, modifiedAt=file.modified_at,
            rowCount=file.row_count, deletedRowCount=file.deleted_row_count,
            deletionVectorSize=file.deletion_vector_size,
            partition=None if file.partition is None else partition_ids[file.partition],
            clusteringProvider=file.clustering_provider, isVOrdered=file.is_v_ordered, footerSize=file.footer_size,
            physicalSchema=None if file.physical_schema is None else schema_ids[id(file.physical_schema)],
            writer=None if file.writer is None else writer_ids[id(file.writer)], rowGroupCount=file.row_group_count,
            compressedSize=file.compressed_size, uncompressedSize=file.uncompressed_size)
        if table.depth < DetailDepth.ROW_GROUPS or file.row_groups is None or file.physical_schema is None:
            continue
        leaf_ids = physical_ids[id(file.physical_schema)]
        for group in file.row_groups:
            group_id = builder.add_row_group(file=file_id, ordinal=group["ordinal"], rowCount=group["rowCount"],
                                             uncompressedSize=group["uncompressedSize"],
                                             compressedSize=group["compressedSize"])
            if table.depth < DetailDepth.COLUMN_CHUNKS or group["chunks"] is None:
                continue
            for chunk in group["chunks"]:
                fields = dict(chunk)
                leaf = fields.pop("leaf")
                if not options.include_data_values:
                    fields["minValue"] = fields["maxValue"] = None
                builder.add_column_chunk(rowGroup=group_id, physicalColumn=leaf_ids[leaf], **fields)

    for commit in table.commits:
        fields = dict(commit)
        if not options.include_data_values:
            fields["parameters"] = None
        builder.add_commit(table=table_id, **fields)


def _ordinal(text: str):
    return text.encode("utf-16-be", "surrogatepass")
