"""The derived measures of a footprint: for each table the storage stack, the file sizes, the small files, the V-Order
shares, the history, the ratios, and the partitions; the measures of each column; and those of the collection.

    import pqax
    from pqax import measures

    with pqax.open("footprint.pqax") as doc:
        result = measures.measure(doc)
        for table in result["tables"]:
            print(table["label"], table["stack"]["waste"], table["stack"]["wasteShare"], table["files"]["median"])
        print(result["collection"]["totals"]["footprint"])

The measures are those of section 10 of the object model, except the two over a set of columns, which only the views
offer. They follow the view model of the storage views, src/typescript/views/src/model.ts, function by function and
with its names, so that the numbers are the same. A value that cannot be computed is None, never 0. A stack component
whose input is missing is None; the waste, the overhead, and the total count it as 0, and are None only when every
component they add is missing; the data, and so the shares, are None when the live data is. The results are dicts with
the names of the view model, a time is the text of the file, and an integral float is returned as an int.
"""
from __future__ import annotations

import datetime
import math
from typing import Any, Dict, List, Mapping, Optional, Sequence

from ._errors import PqaxError
from ._json import parse_timestamp

DEFAULT_SMALL_FILE_SIZE = 128 * 1024 * 1024
"""The default size below which a file is small: 128 MiB."""

DEFAULT_TINY_FILE_SIZE = 8 * 1024 * 1024
"""The default size below which a file is very small: 8 MiB."""

STACK_COMPONENTS = ("live", "nonLive", "unreferenced", "deleted", "stale", "metadata", "footers")
"""The components of a storage stack, in the order the stack draws them."""

WASTE_COMPONENTS = ("nonLive", "unreferenced", "deleted", "stale")
"""The components that make the waste: the space that holds no live data."""

OVERHEAD_COMPONENTS = ("metadata", "footers")
"""The components that make the overhead: the metadata of the table format and the footers of the data files."""

_DAY_MS = 86_400_000
_EPOCH = datetime.datetime(1970, 1, 1, tzinfo=datetime.timezone.utc)

__all__ = [
    "DEFAULT_SMALL_FILE_SIZE", "DEFAULT_TINY_FILE_SIZE", "OVERHEAD_COMPONENTS", "STACK_COMPONENTS", "WASTE_COMPONENTS",
    "commits_per_day", "days_between", "deleted_estimate", "measure", "measure_collection", "measure_columns",
    "measure_tables", "ratio", "small_share_at_least", "stack_of",
]


# ---- reading values defensively, as the views do ----

def _at(field: Any, row: int) -> Any:
    return field[row] if isinstance(field, list) and 0 <= row < len(field) else None


def _number(value: Any) -> Optional[float]:
    """A finite number, else None. A bool is not a number."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    return value if math.isfinite(value) else None


def _num(field: Any, row: int) -> Optional[float]:
    return _number(_at(field, row))


def _size(field: Any, row: int) -> Optional[float]:
    """A finite number that is not negative, else None: sizes and counts."""
    value = _num(field, row)
    return value if value is not None and value >= 0 else None


def _str(field: Any, row: int) -> Optional[str]:
    value = _at(field, row)
    return value if isinstance(value, str) else None


def _strings(field: Any, row: int) -> List[str]:
    value = _at(field, row)
    return [item for item in value if isinstance(item, str)] if isinstance(value, list) else []


def _distribution(field: Any, row: int) -> Optional[Mapping[str, Any]]:
    value = _at(field, row)
    return value if isinstance(value, dict) else None


def _member(value: Optional[Mapping[str, Any]], name: str) -> Optional[float]:
    item = _number(value.get(name)) if value is not None else None
    return item if item is not None and item >= 0 else None


def _length(table: Any) -> int:
    """The number of rows of a table: its longest field."""
    if not isinstance(table, dict):
        return 0
    return max((len(value) for value in table.values() if isinstance(value, list)), default=0)


def _is_id(value: Optional[float]) -> bool:
    return value is not None and float(value).is_integer() and value > 0


def _key(value: float) -> int:
    return int(value)


def _time(value: Any) -> Optional[int]:
    """Milliseconds since 1970 of a timestamp of the file, else None."""
    if not isinstance(value, str):
        return None
    try:
        moment = parse_timestamp(value)
    except (ValueError, TypeError, OverflowError):
        return None
    delta = moment - _EPOCH
    return (delta.days * 86_400 + delta.seconds) * 1000 + delta.microseconds // 1000


def _sum_known(values: Sequence[Optional[float]]) -> Optional[float]:
    """The sum of the values that are not None, in their order, None when all are."""
    total: Optional[float] = None
    for value in values:
        if value is not None:
            total = (total if total is not None else 0) + value
    return total


def _later(a: Optional[str], b: Optional[str]) -> Optional[str]:
    if a is None:
        return b
    if b is None:
        return a
    tb, ta = _time(b), _time(a)
    return b if (tb if tb is not None else -math.inf) > (ta if ta is not None else -math.inf) else a


def _plain(value: Any) -> Any:
    """An integral float as an int, inside dicts and lists too."""
    if isinstance(value, float) and value.is_integer():
        return int(value)
    if isinstance(value, dict):
        return {key: _plain(item) for key, item in value.items()}
    if isinstance(value, list):
        return [_plain(item) for item in value]
    return value


# ---- derived values, one function each ----

def ratio(a: Optional[float], b: Optional[float]) -> Optional[float]:
    """a / b when both are known and b is positive, else None."""
    return a / b if a is not None and b is not None and b > 0 else None


def deleted_estimate(file_size: Optional[float], deleted_row_count: Optional[float],
                     row_count: Optional[float]) -> Optional[float]:
    """The bytes that the deleted rows hold, estimated from the share of the rows: file_size * deleted_row_count /
    row_count, with deleted_row_count at most row_count. 0 when deleted_row_count is 0, None when a value is missing or
    row_count is not positive."""
    if deleted_row_count == 0:
        return 0
    if file_size is None or deleted_row_count is None or row_count is None or row_count <= 0:
        return None
    return file_size * min(deleted_row_count, row_count) / row_count


def small_share_at_least(dist: Mapping[str, Any], threshold: float) -> Optional[float]:
    """A lower bound of the share of the files below a threshold, from a distribution with count, minimum,
    firstQuartile, median, thirdQuartile, and maximum: 1 when the maximum is below it, 0.75 for the third quartile, 0.5
    for the median, 0.25 for the first quartile, one file for the minimum, and 0 otherwise. None when the distribution
    has no quartile."""
    count, minimum = dist.get("count"), dist.get("minimum")
    first, median = dist.get("firstQuartile"), dist.get("median")
    third, maximum = dist.get("thirdQuartile"), dist.get("maximum")
    if maximum is not None and maximum < threshold:
        return 1
    if third is not None and third < threshold:
        return 0.75
    if median is not None and median < threshold:
        return 0.5
    if first is not None and first < threshold:
        return 0.25
    if minimum is not None and minimum < threshold:
        return 1 / count if count is not None and count > 0 else 0
    return None if minimum is None and first is None and median is None and third is None and maximum is None else 0


def commits_per_day(commits: Optional[float], oldest_commit_at: Optional[str],
                    newest_commit_at: Optional[str]) -> Optional[float]:
    """Commits per day: the commits read over the days from the oldest to the newest of them, at least one day. The
    times are timestamps of the file. None when a value is missing or a time does not parse."""
    oldest, newest = _time(oldest_commit_at), _time(newest_commit_at)
    if commits is None or oldest is None or newest is None:
        return None
    return commits / max(1, (newest - oldest) / _DAY_MS)


def days_between(start: Optional[str], end: Optional[str]) -> Optional[int]:
    """Whole days from a timestamp of the file to a later one, at least 0. None when a time is missing or does not
    parse."""
    first, last = _time(start), _time(end)
    return None if first is None or last is None else max(0, math.floor((last - first) / _DAY_MS))


def stack_of(file_size: Optional[float], deleted: Optional[float], footer_size: Optional[float],
             stale_column_size: Optional[float], non_live_file_size: Optional[float], metadata_size: Optional[float],
             unreferenced_file_size: Optional[float] = None, deleted_source: Optional[str] = None) -> Dict[str, Any]:
    """A storage stack from its inputs, as section 10.2 of the object model defines it, as the dict of the key stack
    of a table. A None input is an absent component. When the unreferenced files are known, the non-live component is
    the smaller of the two sizes, and the unreferenced component is the rest. The sums count an absent component as 0
    and are None when every component they add is absent; the data, and so the shares, are None when the live data
    is."""
    live = (None if file_size is None
            else max(0, file_size - (deleted or 0) - (footer_size or 0) - (stale_column_size or 0)))
    listed = unreferenced_file_size is not None
    non_live = (min(non_live_file_size, unreferenced_file_size) if listed and non_live_file_size is not None
                else non_live_file_size)
    unreferenced = unreferenced_file_size - (non_live or 0) if listed else None
    values = {"live": live, "nonLive": non_live, "unreferenced": unreferenced, "deleted": deleted,
              "stale": stale_column_size, "metadata": metadata_size, "footers": footer_size}
    waste = _sum_known([values[key] for key in WASTE_COMPONENTS])
    overhead = _sum_known([values[key] for key in OVERHEAD_COMPONENTS])
    data = None if live is None else live + (waste if waste is not None else 0)
    stack: Dict[str, Any] = dict(values)
    stack.update({
        "deletedSource": deleted_source if deleted is not None else None, "listed": listed,
        "data": data, "total": _sum_known([live, waste, overhead]), "waste": waste, "overhead": overhead,
        "wasteShare": ratio(waste, data), "overheadShare": ratio(overhead, data),
        "shares": {key: ratio(values[key], data) for key in STACK_COMPONENTS},
    })
    return _plain(stack)


# ---- the measures of a footprint ----

class _FilesOfTable:
    __slots__ = ("sizes", "v_ordered", "v_order_known", "v_ordered_bytes", "v_order_known_bytes", "deleted",
                 "deleted_known")

    def __init__(self) -> None:
        self.sizes: List[float] = []
        self.v_ordered = 0
        self.v_order_known = 0
        self.v_ordered_bytes: float = 0
        self.v_order_known_bytes: float = 0
        self.deleted: float = 0
        self.deleted_known = 0


def _files_by_table(files: Optional[Mapping[str, Any]]) -> Dict[int, _FilesOfTable]:
    result: Dict[int, _FilesOfTable] = {}
    if not files:
        return result
    for i in range(_length(files)):
        table = _num(files.get("table"), i)
        if not _is_id(table):
            continue
        entry = result.setdefault(_key(table), _FilesOfTable())
        file_size = _size(files.get("fileSize"), i)
        if file_size is not None:
            entry.sizes.append(file_size)
        v_ordered = _at(files.get("isVOrdered"), i)
        if isinstance(v_ordered, bool):
            entry.v_order_known += 1
            entry.v_order_known_bytes += file_size if file_size is not None else 0
            if v_ordered:
                entry.v_ordered += 1
                entry.v_ordered_bytes += file_size if file_size is not None else 0
        # Only a file with a size counts, so that the files that give an estimate are among the sizes.
        estimate = None if file_size is None else deleted_estimate(
            file_size, _size(files.get("deletedRowCount"), i), _size(files.get("rowCount"), i))
        if estimate is not None:
            entry.deleted += estimate
            entry.deleted_known += 1
    for entry in result.values():
        entry.sizes.sort()
    return result


class _PartitionsOfTable:
    __slots__ = ("kept", "oldest", "newest")

    def __init__(self) -> None:
        self.kept = 0
        self.oldest: Optional[str] = None
        self.newest: Optional[str] = None


def _partitions_by_table(partitions: Optional[Mapping[str, Any]]) -> Dict[int, _PartitionsOfTable]:
    """What the partitions table says about each table: the rows, and the range of lastModifiedAt."""
    result: Dict[int, _PartitionsOfTable] = {}
    if not partitions:
        return result
    for i in range(_length(partitions)):
        table = _num(partitions.get("table"), i)
        if not _is_id(table):
            continue
        entry = result.setdefault(_key(table), _PartitionsOfTable())
        entry.kept += 1
        modified = _str(partitions.get("lastModifiedAt"), i)
        moment = _time(modified)
        if moment is not None:
            entry.newest = _later(entry.newest, modified)
            entry.oldest = modified if entry.oldest is None or moment < _time(entry.oldest) else entry.oldest
    return result


def _small(threshold: float, count: Optional[int], share: Optional[float]) -> Dict[str, Any]:
    return {"threshold": threshold, "count": count, "shareAtLeast": share}


def _positive(value: Any, fallback: int) -> Any:
    number = _number(value)
    return number if number is not None and number > 0 else fallback


def measure_tables(summary: Mapping[str, Any], files: Optional[Mapping[str, Any]] = None,
                   collection: Optional[Mapping[str, Any]] = None, partitions: Optional[Mapping[str, Any]] = None, *,
                   small_file_size: float = DEFAULT_SMALL_FILE_SIZE,
                   tiny_file_size: float = DEFAULT_TINY_FILE_SIZE) -> List[Dict[str, Any]]:
    """The measures of the rows of tables, in the order of the file, as dicts.

    summary is doc.summary, files the files table or None, collection the collection object of the manifest or None,
    and partitions the partitions table or None. A threshold that is not a positive number takes its default.
    """
    threshold = _positive(small_file_size, DEFAULT_SMALL_FILE_SIZE)
    tiny_threshold = _positive(tiny_file_size, DEFAULT_TINY_FILE_SIZE)
    summary = summary or {}
    tables = summary.get("tables") if isinstance(summary.get("tables"), dict) else {}
    columns = summary.get("columns") if isinstance(summary.get("columns"), dict) else {}
    operations = summary.get("operations") if isinstance(summary.get("operations"), dict) else {}
    by_table = _files_by_table(files)
    partitions_by_table = _partitions_by_table(partitions)
    collection = collection if isinstance(collection, dict) else {}
    # The end of the collection: the first of completedAt and startedAt that parses.
    collected_at = next((value for value in (collection.get("completedAt"), collection.get("startedAt"))
                         if _time(value) is not None), None)

    operations_by_table: Dict[int, List[Any]] = {}
    for i in range(_length(operations)):
        table = _num(operations.get("table"), i)
        name = _str(operations.get("name"), i)
        if _is_id(table) and name is not None:
            operations_by_table.setdefault(_key(table), []).append((name, _str(operations.get("lastAt"), i)))

    columns_by_table: Dict[int, List[int]] = {}
    for i in range(_length(columns)):
        table = _num(columns.get("table"), i)
        if _is_id(table):
            columns_by_table.setdefault(_key(table), []).append(i)

    def field(name: str) -> Any:
        return tables.get(name)

    seen: set = set()
    result: List[Dict[str, Any]] = []
    for row in range(_length(tables)):
        identifier = _num(field("id"), row)
        valid_id = _key(identifier) if _is_id(identifier) and _key(identifier) not in seen else None
        if valid_id is not None:
            seen.add(valid_id)
        file_count = _size(field("fileCount"), row)
        file_size = _size(field("fileSize"), row)
        row_count = _size(field("rowCount"), row)
        deleted_row_count = _size(field("deletedRowCount"), row)
        compressed_size = _size(field("compressedSize"), row)
        uncompressed_size = _size(field("uncompressedSize"), row)
        stale_column_size = _size(field("staleColumnSize"), row)
        non_live_file_size = _size(field("nonLiveFileSize"), row)
        from_files = by_table.get(valid_id) if valid_id is not None else None

        # The deleted rows: from the files when every file of the table that has a size gives an estimate.
        deleted = deleted_estimate(file_size, deleted_row_count, row_count)
        deleted_source = "table" if deleted is not None else None
        if (from_files is not None and from_files.deleted_known > 0
                and from_files.deleted_known == len(from_files.sizes)):
            deleted = from_files.deleted
            deleted_source = "files"
        stack = stack_of(file_size, deleted, _size(field("footerSize"), row), stale_column_size, non_live_file_size,
                         _size(field("metadataSize"), row), _size(field("unreferencedFileSize"), row), deleted_source)

        # The file sizes and the small files: from the files, else from fileSizes, whose quartiles bound the small files.
        if from_files is not None and from_files.sizes:
            def below(limit: float, sizes: Sequence[float] = from_files.sizes) -> Dict[str, Any]:
                count = sum(1 for value in sizes if value < limit)
                return _small(limit, count, count / len(sizes))
            small, tiny = below(threshold), below(tiny_threshold)
            sizes, n = from_files.sizes, len(from_files.sizes)
            total: float = 0
            for value in sizes:
                total += value
            # The quartiles by position, as the collector takes them.
            file_figures: Dict[str, Any] = {
                "count": n, "sum": total, "minimum": sizes[0], "firstQuartile": sizes[n // 4], "median": sizes[n // 2],
                "thirdQuartile": sizes[3 * n // 4], "maximum": sizes[n - 1]}
        else:
            dist = _distribution(field("fileSizes"), row)
            count = _member(dist, "count")
            figures = {"count": count if count is not None else file_count,
                       **{name: _member(dist, name) for name in ("minimum", "firstQuartile", "median", "thirdQuartile",
                                                                 "maximum")}}
            small = _small(threshold, None, small_share_at_least(figures, threshold))
            tiny = _small(tiny_threshold, None, small_share_at_least(figures, tiny_threshold))
            total_size = _member(dist, "sum")
            file_figures = {"count": figures["count"], "sum": total_size if total_size is not None else file_size,
                            **{name: figures[name] for name in ("minimum", "firstQuartile", "median", "thirdQuartile",
                                                                "maximum")}}

        # The V-Order shares: from the files when one of them says, else from vOrderedFileCount over fileCount.
        v_order: Optional[Dict[str, Any]] = None
        if from_files is not None and from_files.v_order_known > 0:
            v_order = {"share": from_files.v_ordered / from_files.v_order_known,
                       "bytesShare": ratio(from_files.v_ordered_bytes, from_files.v_order_known_bytes)}
        else:
            v_ordered = _size(field("vOrderedFileCount"), row)
            if v_ordered is not None and file_count is not None and file_count > 0:
                v_order = {"share": min(1, v_ordered / file_count), "bytesShare": None}
        file_figures["vOrder"] = v_order

        # The history: the pace of the commits, and the VACUUM and OPTIMIZE among the operations, None without any.
        table_operations = operations_by_table.get(valid_id, []) if valid_id is not None else []
        last_operation_at: Optional[str] = None
        last_vacuum_at: Optional[str] = None
        last_optimize_at: Optional[str] = None
        has_vacuum = has_optimize = False
        for operation_name, last_at in table_operations:
            last_operation_at = _later(last_operation_at, last_at)
            upper = operation_name.upper()
            if "VACUUM" in upper:
                has_vacuum = True
                last_vacuum_at = _later(last_vacuum_at, last_at)
            if "OPTIMIZE" in upper:
                has_optimize = True
                last_optimize_at = _later(last_optimize_at, last_at)
        candidates = (_str(field("snapshotTimestamp"), row), last_operation_at, _str(field("lastModifiedAt"), row))
        last_commit_at = next((value for value in candidates if _time(value) is not None), None)
        maintenance = {"lastCommitAt": last_commit_at,
                       "commitsPerDay": commits_per_day(_size(field("commitsRead"), row),
                                                        _str(field("oldestCommitAt"), row), last_commit_at),
                       "daysSinceLastCommit": days_between(last_commit_at, collected_at),
                       "hasVacuum": has_vacuum if table_operations else None, "lastVacuumAt": last_vacuum_at,
                       "hasOptimize": has_optimize if table_operations else None, "lastOptimizeAt": last_optimize_at}

        # The partitions: partitionCount, else the partitions kept.
        kept = partitions_by_table.get(valid_id) if valid_id is not None else None
        partition_count = _size(field("partitionCount"), row)
        partitions_measures = {
            "count": partition_count if partition_count is not None else (kept.kept if kept is not None else None),
            "kept": kept.kept if kept is not None else 0,
            "oldestModifiedAt": kept.oldest if kept is not None else None,
            "newestModifiedAt": kept.newest if kept is not None else None,
        }

        # The ratios.
        row_groups = _distribution(field("rowGroupRowCounts"), row)
        footer_count = _size(field("footerCount"), row)
        partition_sizes = _distribution(field("partitionSizes"), row)
        fallback: Optional[float] = None
        dictionary: Optional[float] = None
        for c in columns_by_table.get(valid_id, []) if valid_id is not None else []:
            f = _size(columns.get("fallbackChunkCount"), c)
            d = _size(columns.get("dictionaryChunkCount"), c)
            if f is not None and d is not None:
                fallback = (fallback or 0) + f
                dictionary = (dictionary or 0) + d
        average_row_group_rows = ratio(_member(row_groups, "sum"), _member(row_groups, "count"))
        if average_row_group_rows is None and footer_count is not None and footer_count == file_count:
            average_row_group_rows = ratio(row_count, _size(field("rowGroupCount"), row))
        average_partition_size = ratio(_member(partition_sizes, "sum"), _member(partition_sizes, "count"))
        if average_partition_size is None:
            average_partition_size = ratio(file_size, _size(field("partitionCount"), row))
        measures = {
            "compressionRatio": ratio(uncompressed_size, compressed_size),
            "deletedRowShare": ratio(deleted_row_count, row_count),
            "averageFileSize": ratio(file_size, file_count),
            "averageRowGroupRows": average_row_group_rows,
            "footerCoverage": ratio(footer_count, file_count),
            "staleColumnShare": ratio(stale_column_size, compressed_size),
            "nonLiveShare": (ratio(non_live_file_size, file_size + non_live_file_size)
                             if file_size is not None and non_live_file_size is not None else None),
            "averagePartitionSize": average_partition_size,
            "dictionaryFallbackShare": ratio(fallback, dictionary),
        }

        name = _str(field("name"), row)
        fallback_name = f"Table {valid_id}" if valid_id is not None else f"Table at row {row + 1}"
        label = ".".join([*_strings(field("namespace"), row), name if name is not None else fallback_name])
        result.append(_plain({
            "id": valid_id, "name": name, "label": label, "rowCount": row_count, "fileCount": file_count,
            "fileSize": file_size, "stack": stack, "smallFiles": small, "tinyFiles": tiny, "files": file_figures,
            "measures": measures, "maintenance": maintenance, "partitions": partitions_measures,
        }))
    return result


def measure_columns(summary: Mapping[str, Any]) -> List[Dict[str, Any]]:
    """The measures of the rows of columns, in the order of the file, as dicts: section 10.6 of the object model.

    A column dict has id, None when not positive or repeating an earlier row, table, the id of its table or None when
    no table of the file has it, name, label, and the six measures.
    """
    summary = summary or {}
    tables = summary.get("tables") if isinstance(summary.get("tables"), dict) else {}
    columns = summary.get("columns") if isinstance(summary.get("columns"), dict) else {}
    # The footers read of each table, by the first row that has its id.
    footer_count_by_id: Dict[int, Optional[float]] = {}
    for row in range(_length(tables)):
        identifier = _num(tables.get("id"), row)
        if _is_id(identifier) and _key(identifier) not in footer_count_by_id:
            footer_count_by_id[_key(identifier)] = _size(tables.get("footerCount"), row)

    def field(name: str, row: int) -> Optional[float]:
        return _size(columns.get(name), row)

    seen: set = set()
    result: List[Dict[str, Any]] = []
    for row in range(_length(columns)):
        identifier = _num(columns.get("id"), row)
        valid_id = _key(identifier) if _is_id(identifier) and _key(identifier) not in seen else None
        if valid_id is not None:
            seen.add(valid_id)
        table = _num(columns.get("table"), row)
        table_id = _key(table) if _is_id(table) and _key(table) in footer_count_by_id else None
        value_count, null_count = field("valueCount", row), field("nullCount", row)
        dictionary_chunk_count = field("dictionaryChunkCount", row)
        non_null = value_count - null_count if value_count is not None and null_count is not None else None
        name = _str(columns.get("name"), row)
        fallback_label = f"Column {valid_id}" if valid_id is not None else f"Column at row {row + 1}"
        result.append(_plain({
            "id": valid_id, "table": table_id, "name": name, "label": name if name is not None else fallback_label,
            "nullShare": (min(1, null_count / value_count)
                          if null_count is not None and value_count is not None and value_count > 0 else None),
            "compressionRatio": ratio(field("uncompressedSize", row), field("compressedSize", row)),
            "dictionaryShare": ratio(dictionary_chunk_count, field("chunkCount", row)),
            "dictionaryFallbackShare": ratio(field("fallbackChunkCount", row), dictionary_chunk_count),
            "averageValueLength": ratio(field("unencodedByteArrayDataBytes", row), non_null),
            "filesWithoutColumnShare": ratio(field("fileCountWithoutColumn", row),
                                             footer_count_by_id[table_id] if table_id is not None else None),
        }))
    return result


def measure_collection(summary: Mapping[str, Any], tables: Sequence[Mapping[str, Any]]) -> Dict[str, Any]:
    """The measures of the collection, section 10.7 of the object model: {"totals": {...}, "depths": {...}}.

    tables is what measure_tables returned for the summary. A sum is None when no table has the value.
    """
    summary = summary or {}
    sources = summary.get("sources") if isinstance(summary.get("sources"), dict) else {}
    table_fields = summary.get("tables") if isinstance(summary.get("tables"), dict) else {}
    columns = summary.get("columns") if isinstance(summary.get("columns"), dict) else {}
    source_ids = {_key(value) for value in (_num(sources.get("id"), row) for row in range(_length(sources)))
                  if _is_id(value)}
    depths: Dict[str, int] = {}
    for row in range(_length(table_fields)):
        depth = _str(table_fields.get("detailDepth"), row)
        depth = depth if depth is not None else "Unknown"
        depths[depth] = depths.get(depth, 0) + 1
    totals = {
        "sources": len(source_ids), "tables": _length(table_fields), "columns": _length(columns),
        "files": _sum_known([table["fileCount"] for table in tables]),
        "fileSize": _sum_known([table["fileSize"] for table in tables]),
        "rowCount": _sum_known([table["rowCount"] for table in tables]),
        "footprint": _sum_known([table["stack"]["total"] for table in tables]),
        "waste": _sum_known([table["stack"]["waste"] for table in tables]),
        "overhead": _sum_known([table["stack"]["overhead"] for table in tables]),
    }
    return _plain({"totals": totals, "depths": depths})


def measure(document: Any, *, small_file_size: float = DEFAULT_SMALL_FILE_SIZE,
            tiny_file_size: float = DEFAULT_TINY_FILE_SIZE) -> Dict[str, Any]:
    """The measures of an open document: the summary, and the files and partitions tables when the manifest states
    rows for them.

    Returns {"filesRead": bool, "warnings": [...], "tables": [...], "columns": [...], "collection": {...}}. A detail
    table that cannot be read is left out with a warning, and the measures use the totals of the summary.
    """
    warnings: List[str] = []

    def detail(name: str) -> Optional[Mapping[str, Any]]:
        if document.row_count(name) <= 0:
            return None
        try:
            return document.table(name)
        except (PqaxError, OSError) as error:
            code = getattr(error, "code", None)
            warnings.append(f"The {name} table could not be read{f' ({code})' if isinstance(code, str) else ''}. "
                            f"The measures use the totals of the summary.")
            return None

    files = detail("files")
    partitions = detail("partitions")
    tables = measure_tables(document.summary, files, document.collection, partitions, small_file_size=small_file_size,
                            tiny_file_size=tiny_file_size)
    return {"filesRead": _length(files) > 0, "warnings": warnings, "tables": tables,
            "columns": measure_columns(document.summary), "collection": measure_collection(document.summary, tables)}
