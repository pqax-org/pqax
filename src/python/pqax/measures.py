"""The derived measures of the tables of a footprint: the storage stack, the small files, the ratios, and the pace of
the commits.

    import pqax
    from pqax import measures

    with pqax.open("footprint.pqax") as doc:
        result = measures.measure(doc)
        for table in result["tables"]:
            print(table["label"], table["stack"]["waste"], table["stack"]["wasteShare"])

The measures are those of section 10 of the object model. They follow the view model of the storage views,
src/typescript/views/src/model.ts, function by function and with its names, so that the numbers are the same. A value
that cannot be computed is None, never 0. A stack component whose input is missing is None; the waste, the overhead,
and the total count it as 0, and are None only when every component they add is missing; the data, and so the shares,
are None when the live data is. The results are dicts with the keys of the JSON of pqax tables --json, and an integral
float is returned as an int.
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
    "commits_per_day", "days_between", "deleted_estimate", "measure", "measure_tables", "ratio", "small_share_at_least",
    "stack_of",
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
    })
    return _plain(stack)


# ---- the measures of a footprint ----

class _FilesOfTable:
    __slots__ = ("sizes", "deleted", "deleted_known")

    def __init__(self) -> None:
        self.sizes: List[float] = []
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
        # Only a file with a size counts, so that the files that give an estimate are among the sizes.
        estimate = None if file_size is None else deleted_estimate(
            file_size, _size(files.get("deletedRowCount"), i), _size(files.get("rowCount"), i))
        if estimate is not None:
            entry.deleted += estimate
            entry.deleted_known += 1
    for entry in result.values():
        entry.sizes.sort()
    return result


def _small(threshold: float, count: Optional[int], share: Optional[float]) -> Dict[str, Any]:
    return {"threshold": threshold, "count": count, "shareAtLeast": share}


def _positive(value: Any, fallback: int) -> Any:
    number = _number(value)
    return number if number is not None and number > 0 else fallback


def measure_tables(summary: Mapping[str, Any], files: Optional[Mapping[str, Any]] = None,
                   collection: Optional[Mapping[str, Any]] = None, *,
                   small_file_size: float = DEFAULT_SMALL_FILE_SIZE,
                   tiny_file_size: float = DEFAULT_TINY_FILE_SIZE) -> List[Dict[str, Any]]:
    """The measures of the rows of tables, in the order of the file, as dicts.

    summary is doc.summary, files the files table or None, and collection the collection object of the manifest or
    None. A threshold that is not a positive number takes its default.
    """
    threshold = _positive(small_file_size, DEFAULT_SMALL_FILE_SIZE)
    tiny_threshold = _positive(tiny_file_size, DEFAULT_TINY_FILE_SIZE)
    summary = summary or {}
    tables = summary.get("tables") if isinstance(summary.get("tables"), dict) else {}
    columns = summary.get("columns") if isinstance(summary.get("columns"), dict) else {}
    operations = summary.get("operations") if isinstance(summary.get("operations"), dict) else {}
    by_table = _files_by_table(files)
    collection = collection if isinstance(collection, dict) else {}
    # The end of the collection: the first of completedAt and startedAt that parses.
    collected_at = next((value for value in (collection.get("completedAt"), collection.get("startedAt"))
                         if _time(value) is not None), None)

    operations_by_table: Dict[int, List[Optional[str]]] = {}
    for i in range(_length(operations)):
        table = _num(operations.get("table"), i)
        if _is_id(table) and _str(operations.get("name"), i) is not None:
            operations_by_table.setdefault(_key(table), []).append(_str(operations.get("lastAt"), i))

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

        # The small files: counted from the files, else bounded from the quartiles of fileSizes.
        if from_files is not None and from_files.sizes:
            def below(limit: float, sizes: Sequence[float] = from_files.sizes) -> Dict[str, Any]:
                count = sum(1 for value in sizes if value < limit)
                return _small(limit, count, count / len(sizes))
            small, tiny = below(threshold), below(tiny_threshold)
        else:
            dist = _distribution(field("fileSizes"), row)
            count = _member(dist, "count")
            figures = {"count": count if count is not None else file_count,
                       **{name: _member(dist, name) for name in ("minimum", "firstQuartile", "median", "thirdQuartile",
                                                                 "maximum")}}
            small = _small(threshold, None, small_share_at_least(figures, threshold))
            tiny = _small(tiny_threshold, None, small_share_at_least(figures, tiny_threshold))

        # The pace of the commits.
        last_operation_at: Optional[str] = None
        for last_at in operations_by_table.get(valid_id, []) if valid_id is not None else []:
            last_operation_at = _later(last_operation_at, last_at)
        candidates = (_str(field("snapshotTimestamp"), row), last_operation_at, _str(field("lastModifiedAt"), row))
        last_commit_at = next((value for value in candidates if _time(value) is not None), None)
        maintenance = {"lastCommitAt": last_commit_at,
                       "commitsPerDay": commits_per_day(_size(field("commitsRead"), row),
                                                        _str(field("oldestCommitAt"), row), last_commit_at),
                       "daysSinceLastCommit": days_between(last_commit_at, collected_at)}

        # The ratios.
        row_groups = _distribution(field("rowGroupRowCounts"), row)
        footer_count = _size(field("footerCount"), row)
        partitions = _distribution(field("partitionSizes"), row)
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
        average_partition_size = ratio(_member(partitions, "sum"), _member(partitions, "count"))
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
            "fileSize": file_size, "stack": stack, "smallFiles": small, "tinyFiles": tiny, "measures": measures,
            "maintenance": maintenance,
        }))
    return result


def measure(document: Any, *, small_file_size: float = DEFAULT_SMALL_FILE_SIZE,
            tiny_file_size: float = DEFAULT_TINY_FILE_SIZE) -> Dict[str, Any]:
    """The measures of an open document: the summary, and the files table when the manifest states rows for it.

    Returns {"filesRead": bool, "warnings": [...], "tables": [...]}. A files table that cannot be read is left out
    with a warning, and the measures use the totals of the summary.
    """
    warnings: List[str] = []
    files = None
    if document.row_count("files") > 0:
        try:
            files = document.table("files")
        except (PqaxError, OSError) as error:
            code = getattr(error, "code", None)
            warnings.append(f"The files table could not be read{f' ({code})' if isinstance(code, str) else ''}. "
                            f"The measures use the totals of the summary.")
    tables = measure_tables(document.summary, files, document.collection, small_file_size=small_file_size,
                            tiny_file_size=tiny_file_size)
    return {"filesRead": _length(files) > 0, "warnings": warnings, "tables": tables}
