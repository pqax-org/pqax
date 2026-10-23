"""The comparison of two footprints of the same lakehouse taken at different times: the tables and the columns added,
removed, and kept, and for every kept one the change of each total and of each derived measure.

    import pqax
    from pqax import diff

    with pqax.open("monday.pqax") as older, pqax.open("friday.pqax") as newer:
        result = diff.compare(older, newer)
    for kind, entry in diff.rank(result, "total", top=10):
        print(kind, entry["label"] if kind != "kept" else entry["newer"]["label"])

The rules are those of section 13 of the object model. A table is the same table when its TableId is, and otherwise,
when one of the two has none, when its source, namespace, and name are; a column when its FieldId is, and otherwise by
name; a partition by its values, a file by its path, and a commit by its CommitId. Each compared value is a dict with
the older value, the newer value, and the delta, None when either value is missing. A time is written in the form of the
format, and its delta is in milliseconds. The result is the JSON of python -m pqax diff --json without the headers of the
files, and an integral float in it is an int.
"""
from __future__ import annotations

from typing import Any, Callable, Dict, Hashable, List, Mapping, Optional, Sequence, Tuple

from . import measures as _measures
from ._errors import PqaxError
from ._json import format_timestamp, parse_timestamp

ORDERS = ("total", "waste", "rows", "files", "name")
"""The orders of rank and of python -m pqax diff --sort. The default is total."""

_STACK = _measures.STACK_COMPONENTS
_MEMBERS = ("count", "sum", "minimum", "firstQuartile", "median", "thirdQuartile", "maximum")
_DISTRIBUTIONS = ("fileSizes", "rowGroupRowCounts", "partitionSizes")
_TABLE_TOTALS = ("fileCount", "fileSize", "rowCount", "deletedRowCount", "lastModifiedAt", "vOrderedFileCount",
                 "partitionCount", "deleteFileCount", "deleteFileSize", "commitCount", "commitsRead", "oldestCommitAt",
                 "nonLiveFileCount", "nonLiveFileSize", "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt",
                 "unreferencedFileCount", "unreferencedFileSize", "metadataFileCount", "metadataSize", "footerCount",
                 "rowGroupCount", "compressedSize", "uncompressedSize", "footerSize", "staleColumnSize")
_TABLE_MEASURES = (
    tuple(f"stack.{key}" for key in _STACK)
    + ("stack.listed", "stack.data", "stack.total", "stack.waste", "stack.overhead", "stack.wasteShare",
       "stack.overheadShare")
    + tuple(f"stack.shares.{key}" for key in _STACK)
    + ("smallFiles.count", "smallFiles.shareAtLeast", "tinyFiles.count", "tinyFiles.shareAtLeast")
    + tuple(f"files.{key}" for key in _MEMBERS) + ("files.vOrder.share", "files.vOrder.bytesShare")
    + tuple(f"measures.{key}" for key in ("compressionRatio", "deletedRowShare", "averageFileSize",
                                          "averageRowGroupRows", "footerCoverage", "staleColumnShare", "nonLiveShare",
                                          "averagePartitionSize", "dictionaryFallbackShare"))
    + tuple(f"maintenance.{key}" for key in ("lastCommitAt", "commitsPerDay", "daysSinceLastCommit", "hasVacuum",
                                             "lastVacuumAt", "hasOptimize", "lastOptimizeAt"))
    + tuple(f"partitions.{key}" for key in ("count", "kept", "oldestModifiedAt", "newestModifiedAt")))
_COLUMN_TOTALS = ("compressedSize", "uncompressedSize", "valueCount", "nullCount", "chunkCount", "dictionaryChunkCount",
                  "fallbackChunkCount", "dictionaryPageSize", "unencodedByteArrayDataBytes", "codecs", "encodings",
                  "fileCountWithoutColumn", "largestChunkDistinctCount", "distinctCount")
_COLUMN_LISTS = ("codecs", "encodings")
_COLUMN_MEASURES = ("nullShare", "compressionRatio", "dictionaryShare", "dictionaryFallbackShare", "averageValueLength",
                    "filesWithoutColumnShare")
_COLLECTION = ("sources", "tables", "columns", "files", "fileSize", "rowCount", "footprint", "waste", "overhead")
_TIMES = frozenset({"lastModifiedAt", "oldestCommitAt", "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt",
                    "maintenance.lastCommitAt", "maintenance.lastVacuumAt", "maintenance.lastOptimizeAt",
                    "partitions.oldestModifiedAt", "partitions.newestModifiedAt"})
_FILE_FIELDS = ("fileSize", "rowCount", "deletedRowCount", "deletionVectorSize")
_PARTITION_FIELDS = ("fileCount", "fileSize", "rowCount", "deletedRowCount")
_KINDS = {"added": 0, "removed": 1, "kept": 2}

__all__ = ["ORDERS", "compare", "rank"]


# ---- values ----

def _at(field: Any, row: int) -> Any:
    return field[row] if isinstance(field, list) and 0 <= row < len(field) else None


def _int(value: Any) -> Optional[int]:
    """An integer of the file: an int, or a float with no fraction. A bool is not one."""
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, float) and value.is_integer():
        return int(value)
    return None


def _str(value: Any) -> Optional[str]:
    return value if isinstance(value, str) else None


def _strings(value: Any) -> Optional[List[str]]:
    return list(value) if isinstance(value, list) and all(isinstance(item, str) for item in value) else None


def _time(value: Any) -> Optional[str]:
    """A time in the form of the format, or None when it does not parse."""
    if not isinstance(value, str):
        return None
    try:
        return format_timestamp(parse_timestamp(value))
    except (ValueError, TypeError, OverflowError):
        return None


def _ms(text: str) -> int:
    moment = parse_timestamp(text)
    return (moment.toordinal() * 86_400 + moment.hour * 3_600 + moment.minute * 60 + moment.second) * 1000 \
        + moment.microsecond // 1000


def _number(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _change(name: str, older: Any, newer: Any) -> Dict[str, Any]:
    """A compared value: the older, the newer, and the delta, the newer minus the older, in milliseconds for a time."""
    if older is None or newer is None:
        delta = None
    elif name in _TIMES:
        delta = _ms(newer) - _ms(older)
    elif _number(older) and _number(newer):
        delta = newer - older
    else:
        delta = None
    return {"older": older, "newer": newer, "delta": delta}


def _changed(item: Mapping[str, Any]) -> bool:
    older, newer = item["older"], item["newer"]
    return (type(older) is bool) != (type(newer) is bool) or older != newer


def _path(value: Any, dotted: str) -> Any:
    for key in dotted.split("."):
        value = value.get(key) if isinstance(value, dict) else None
    return value


def _ordinal(label: str) -> bytes:
    """The order of the labels in every language: by UTF-16 code units."""
    return label.encode("utf-16-be", errors="surrogatepass")


# ---- one footprint ----

def _detail(document: Any, name: str) -> Optional[Mapping[str, Any]]:
    """A detail table, {} when the file has no rows of it, None when it cannot be read."""
    if document.row_count(name) <= 0:
        return {}
    try:
        return document.table(name)
    except (PqaxError, OSError):
        return None


def _rows_by_table(table: Optional[Mapping[str, Any]]) -> Dict[int, List[int]]:
    result: Dict[int, List[int]] = {}
    if not table:
        return result
    field = table.get("table")
    for row in range(_measures._length(table)):
        key = _int(_at(field, row))
        if key is not None:
            result.setdefault(key, []).append(row)
    return result


class _Footprint:
    """What the comparison needs from one document: its tables, columns, and detail, and their measures."""

    def __init__(self, document: Any, small_file_size: float, tiny_file_size: float) -> None:
        summary = document.summary
        self.measured = _measures.measure(document, small_file_size=small_file_size, tiny_file_size=tiny_file_size)
        self.tables = summary.get("tables") if isinstance(summary.get("tables"), dict) else {}
        self.columns = summary.get("columns") if isinstance(summary.get("columns"), dict) else {}
        sources = summary.get("sources") if isinstance(summary.get("sources"), dict) else {}
        self.sources: Dict[int, Tuple[Optional[str], Optional[str]]] = {}
        for row in range(_measures._length(sources)):
            key = _int(_at(sources.get("id"), row))
            if key is not None and key not in self.sources:
                self.sources[key] = (_str(_at(sources.get("kind"), row)), _str(_at(sources.get("name"), row)))
        self.count = _measures._length(self.tables)
        self.columns_by_table = _rows_by_table(self.columns)
        self.files = _detail(document, "files")
        self.partitions = _detail(document, "partitions")
        self.commits = _detail(document, "commits")
        self.files_by_table = _rows_by_table(self.files)
        self.partitions_by_table = _rows_by_table(self.partitions)
        self.commits_by_table = _rows_by_table(self.commits)
        # A list is kept by the footprint when it has rows of it and they can be read.
        self.keeps_partitions = bool(self.partitions)
        self.keeps_commits = bool(self.commits)

    def field(self, name: str, row: int) -> Any:
        return _at(self.tables.get(name), row)

    def table_id(self, row: int) -> Optional[int]:
        return _int(self.field("id", row))

    def name_key(self, row: int) -> Hashable:
        source = _int(self.field("source", row))
        kind, name = self.sources.get(source, (None, None)) if source is not None else (None, None)
        namespace = _strings(self.field("namespace", row))
        return (kind, name, tuple(namespace) if namespace is not None else None, _str(self.field("name", row)))

    def side(self, row: int) -> Dict[str, Any]:
        measured = self.measured["tables"][row]
        table_id = self.table_id(row)
        return {
            "id": measured["id"], "label": measured["label"], "name": _str(self.field("name", row)),
            "namespace": _strings(self.field("namespace", row)), "tableId": _str(self.field("tableId", row)),
            "snapshotId": _str(self.field("snapshotId", row)), "detailDepth": _str(self.field("detailDepth", row)),
            "columnCount": len(self.columns_by_table.get(table_id, ())) if table_id is not None else 0,
            "fileCount": _int(self.field("fileCount", row)), "fileSize": _int(self.field("fileSize", row)),
            "rowCount": _int(self.field("rowCount", row)),
            "total": measured["stack"]["total"], "waste": measured["stack"]["waste"],
        }

    def totals(self, row: int) -> Dict[str, Any]:
        values: Dict[str, Any] = {}
        for name in _TABLE_TOTALS:
            value = self.field(name, row)
            values[name] = _time(value) if name in _TIMES else _int(value)
        for name in _DISTRIBUTIONS:
            distribution = self.field(name, row)
            for member in _MEMBERS:
                values[f"{name}.{member}"] = _int(distribution.get(member)) if isinstance(distribution, dict) else None
        return values

    def table_measures(self, row: int) -> Dict[str, Any]:
        measured = self.measured["tables"][row]
        return {name: _time(_path(measured, name)) if name in _TIMES else _path(measured, name)
                for name in _TABLE_MEASURES}

    def keeps_files(self, row: int) -> bool:
        depth = _str(self.field("detailDepth", row))
        return self.files is not None and depth is not None and depth != "Summary"

    def column_field(self, name: str, row: int) -> Any:
        return _at(self.columns.get(name), row)

    def column_side(self, row: int) -> Dict[str, Any]:
        measured = self.measured["columns"][row]
        nullable = self.column_field("isNullable", row)
        return {"id": measured["id"], "label": measured["label"], "name": _str(self.column_field("name", row)),
                "fieldId": _int(self.column_field("fieldId", row)),
                "dataType": _str(self.column_field("dataType", row)),
                "isNullable": nullable if isinstance(nullable, bool) else None,
                "compressedSize": _int(self.column_field("compressedSize", row))}

    def column_totals(self, row: int) -> Dict[str, Any]:
        return {name: _strings(self.column_field(name, row)) if name in _COLUMN_LISTS
                else _int(self.column_field(name, row)) for name in _COLUMN_TOTALS}

    def column_measures(self, row: int) -> Dict[str, Any]:
        measured = self.measured["columns"][row]
        return {name: measured[name] for name in _COLUMN_MEASURES}


# ---- pairing ----

def _pair(old: Sequence[int], new: Sequence[int], ident: Callable[[str, int], Any], key: Callable[[str, int], Any]
          ) -> Tuple[List[Tuple[int, int, int]], List[int], List[int]]:
    """Pairs the items one to one in three passes: the same identifier and the same key, the same identifier, and the
    same key when at least one of the two has no identifier. Each pass takes the older items in their order and pairs
    each with the first newer item, in its order, that matches and is not paired yet.

    Returns the pairs (older, newer, pass from 0), the older items left, and the newer items left."""
    pairs: List[Tuple[int, int, int]] = []
    used_old: set = set()
    used_new: set = set()
    for step in range(3):
        candidates: Dict[Any, List[int]] = {}
        for n in new:
            if n in used_new:
                continue
            i = ident("new", n)
            if step == 0 and i is not None:
                candidates.setdefault((i, key("new", n)), []).append(n)
            elif step == 1 and i is not None:
                candidates.setdefault(i, []).append(n)
            elif step == 2:
                candidates.setdefault((key("new", n), i is None), []).append(n)
        for o in old:
            if o in used_old:
                continue
            i = ident("old", o)
            if step == 0:
                lists = [candidates.get((i, key("old", o)))] if i is not None else []
            elif step == 1:
                lists = [candidates.get(i)] if i is not None else []
            else:
                k = key("old", o)
                # Without an identifier, any newer item with the key; with one, only a newer item without.
                lists = [candidates.get((k, True))] + ([candidates.get((k, False))] if i is None else [])
            firsts = [next((n for n in found if n not in used_new), None) for found in lists if found]
            match = min((n for n in firsts if n is not None), default=None)
            if match is not None:
                used_old.add(o)
                used_new.add(match)
                pairs.append((o, match, step))
    return (pairs, [o for o in old if o not in used_old], [n for n in new if n not in used_new])


def _compare_detail(old_rows: Sequence[int], new_rows: Sequence[int], old_table: Mapping[str, Any],
                    new_table: Mapping[str, Any], key: Callable[[Mapping[str, Any], int], Any],
                    fields: Sequence[str], sizes: bool) -> Optional[Dict[str, Any]]:
    """The objects of a detail list added, removed, kept, and changed, paired by a key; None when one has no key."""
    old_keys = [key(old_table, row) for row in old_rows]
    new_keys = [key(new_table, row) for row in new_rows]
    if any(k is None for k in old_keys) or any(k is None for k in new_keys):
        return None
    waiting: Dict[Any, List[int]] = {}
    for position, k in enumerate(new_keys):
        waiting.setdefault(k, []).append(position)
    taken = [False] * len(new_rows)
    kept: List[Tuple[int, int]] = []
    removed: List[int] = []
    for position, k in enumerate(old_keys):
        queue = waiting.get(k)
        if queue:
            match = queue.pop(0)
            taken[match] = True
            kept.append((old_rows[position], new_rows[match]))
        else:
            removed.append(old_rows[position])
    added = [row for position, row in enumerate(new_rows) if not taken[position]]
    if not sizes:
        return {"added": len(added), "removed": len(removed), "kept": len(kept)}

    def value(table: Mapping[str, Any], name: str, row: int) -> Optional[int]:
        return _int(_at(table.get(name), row))

    def total(table: Mapping[str, Any], rows: Sequence[int]) -> Optional[int]:
        if not rows:
            return 0
        found = [size for size in (value(table, "fileSize", row) for row in rows) if size is not None]
        return sum(found) if found else None

    changed = sum(1 for o, n in kept if any(value(old_table, name, o) != value(new_table, name, n) for name in fields))
    return {"added": len(added), "removed": len(removed), "kept": len(kept), "changed": changed,
            "addedFileSize": total(new_table, added), "removedFileSize": total(old_table, removed)}


def _path_key(table: Mapping[str, Any], row: int) -> Optional[str]:
    return _str(_at(table.get("path"), row))


def _values_key(table: Mapping[str, Any], row: int) -> Optional[Tuple[Optional[str], ...]]:
    values = _at(table.get("values"), row)
    if not isinstance(values, list) or not all(item is None or isinstance(item, str) for item in values):
        return None
    return tuple(values)


def _commit_key(table: Mapping[str, Any], row: int) -> Optional[str]:
    return _str(_at(table.get("commitId"), row))


# ---- the comparison ----

def _columns(a: _Footprint, b: _Footprint, old_table: Optional[int], new_table: Optional[int]) -> Dict[str, Any]:
    old = a.columns_by_table.get(old_table, []) if old_table is not None else []
    new = b.columns_by_table.get(new_table, []) if new_table is not None else []
    sides = {"old": a, "new": b}

    def ident(side: str, row: int) -> Any:
        return _int(sides[side].column_field("fieldId", row))

    def key(side: str, row: int) -> Any:
        return _str(sides[side].column_field("name", row))

    pairs, removed, added = _pair(old, new, ident, key)
    kept = []
    for o, n, step in sorted(pairs, key=lambda pair: pair[1]):
        new_totals = b.column_totals(n)
        totals = {name: _change(name, value, new_totals[name]) for name, value in a.column_totals(o).items()}
        new_measures = b.column_measures(n)
        measures = {name: _change(name, value, new_measures[name]) for name, value in a.column_measures(o).items()}
        older, newer = a.column_side(o), b.column_side(n)
        renamed = older["name"] != newer["name"]
        changed = (renamed or older["dataType"] != newer["dataType"] or older["isNullable"] != newer["isNullable"]
                   or any(_changed(item) for item in totals.values()))
        kept.append({"status": "changed" if changed else "unchanged", "matchedBy": "fieldId" if step < 2 else "name",
                     "renamed": renamed, "older": older, "newer": newer, "totals": totals, "measures": measures})
    return {"added": [b.column_side(row) for row in added], "removed": [a.column_side(row) for row in removed],
            "kept": kept}


def _kept(a: _Footprint, b: _Footprint, o: int, n: int, step: int) -> Dict[str, Any]:
    older, newer = a.side(o), b.side(n)
    new_totals = b.totals(n)
    totals = {name: _change(name, value, new_totals[name]) for name, value in a.totals(o).items()}
    new_measures = b.table_measures(n)
    measures = {name: _change(name, value, new_measures[name]) for name, value in a.table_measures(o).items()}
    old_id, new_id = a.table_id(o), b.table_id(n)
    columns = _columns(a, b, old_id, new_id)

    def rows(by_table: Dict[int, List[int]], key: Optional[int]) -> List[int]:
        return by_table.get(key, []) if key is not None else []

    files = partitions = commits = None
    if a.keeps_files(o) and b.keeps_files(n):
        files = _compare_detail(rows(a.files_by_table, old_id), rows(b.files_by_table, new_id), a.files or {},
                                b.files or {}, _path_key, _FILE_FIELDS, True)
    if a.keeps_partitions and b.keeps_partitions:
        partitions = _compare_detail(rows(a.partitions_by_table, old_id), rows(b.partitions_by_table, new_id),
                                     a.partitions or {}, b.partitions or {}, _values_key, _PARTITION_FIELDS, True)
    if a.keeps_commits and b.keeps_commits:
        commits = _compare_detail(rows(a.commits_by_table, old_id), rows(b.commits_by_table, new_id), a.commits or {},
                                  b.commits or {}, _commit_key, (), False)
    renamed = (older["name"], older["namespace"]) != (newer["name"], newer["namespace"])
    changed = (renamed or older["snapshotId"] != newer["snapshotId"]
               or any(_changed(item) for item in totals.values())
               or bool(columns["added"]) or bool(columns["removed"])
               or any(column["status"] == "changed" for column in columns["kept"])
               or any(d is not None and (d["added"] or d["removed"] or d.get("changed"))
                      for d in (files, partitions, commits)))
    return {"status": "changed" if changed else "unchanged", "matchedBy": "tableId" if step < 2 else "name",
            "renamed": renamed, "older": older, "newer": newer, "totals": totals, "measures": measures,
            "columns": columns, "files": files, "partitions": partitions, "commits": commits}


def compare(older: Any, newer: Any, *, small_file_size: float = _measures.DEFAULT_SMALL_FILE_SIZE,
            tiny_file_size: float = _measures.DEFAULT_TINY_FILE_SIZE) -> Dict[str, Any]:
    """Compares two open documents, the older and the newer, as section 13 of the object model says.

    Returns {"older": {"tableCount", "filesRead", "warnings"}, "newer": {...}, "smallFileSize", "tinyFileSize",
    "counts": {"added", "removed", "kept", "changed"}, "collection": {...}, "added": [...], "removed": [...],
    "kept": [...]}: the tables added, in the order of the newer file; removed, in the order of the older; and kept, in
    the order of the newer. The thresholds of the small files are those of pqax.measures.
    """
    a = _Footprint(older, small_file_size, tiny_file_size)
    b = _Footprint(newer, small_file_size, tiny_file_size)
    sides = {"old": a, "new": b}

    def ident(side: str, row: int) -> Any:
        return _str(sides[side].field("tableId", row))

    def key(side: str, row: int) -> Any:
        return sides[side].name_key(row)

    pairs, removed_rows, added_rows = _pair(range(a.count), range(b.count), ident, key)
    removed_keys = {a.name_key(row) for row in removed_rows}
    added_keys = {b.name_key(row) for row in added_rows}
    added = [dict(b.side(row), recreated=b.name_key(row) in removed_keys) for row in added_rows]
    removed = [dict(a.side(row), recreated=a.name_key(row) in added_keys) for row in removed_rows]
    kept = [_kept(a, b, o, n, step) for o, n, step in sorted(pairs, key=lambda pair: pair[1])]

    old_collection, new_collection = a.measured["collection"], b.measured["collection"]
    collection = {name: _change(name, old_collection["totals"][name], new_collection["totals"][name])
                  for name in _COLLECTION}
    depths = sorted(set(old_collection["depths"]) | set(new_collection["depths"]), key=_ordinal)
    for depth in depths:
        collection[f"depths.{depth}"] = _change("depths", old_collection["depths"].get(depth, 0),
                                                new_collection["depths"].get(depth, 0))

    def header(footprint: _Footprint) -> Dict[str, Any]:
        return {"tableCount": footprint.count, "filesRead": footprint.measured["filesRead"],
                "warnings": list(footprint.measured["warnings"])}

    return _measures._plain({
        "older": header(a), "newer": header(b),
        "smallFileSize": _measures._positive(small_file_size, _measures.DEFAULT_SMALL_FILE_SIZE),
        "tinyFileSize": _measures._positive(tiny_file_size, _measures.DEFAULT_TINY_FILE_SIZE),
        "counts": {"added": len(added), "removed": len(removed), "kept": len(kept),
                   "changed": sum(1 for entry in kept if entry["status"] == "changed")},
        "collection": collection, "added": added, "removed": removed, "kept": kept,
    })


def rank(result: Mapping[str, Any], order: str = "total", top: Optional[int] = None) -> List[Tuple[str, Dict[str, Any]]]:
    """The changes of a result of compare in one order, as (kind, entry) with kind "added", "removed", or "kept".

    total, waste, rows, and files order by the size of the change, the largest first: the absolute delta of the stack
    total, the waste, the rows, or the files of a kept table, and the value itself for a table added or removed. A
    change whose size is None goes last. name skips the size. Ties go by the label, the newer one for a kept table, in
    the order of UTF-16 code units, then added before removed before kept, then the order of the list. top keeps the
    first top changes.
    """
    if order not in ORDERS:
        raise ValueError(f"The order is one of {', '.join(ORDERS)}, not {order!r}.")
    measure_key = {"total": "stack.total", "waste": "stack.waste"}.get(order)
    total_key = {"rows": "rowCount", "files": "fileCount"}.get(order)
    side_key = {"total": "total", "waste": "waste", "rows": "rowCount", "files": "fileCount"}.get(order)

    def magnitude(kind: str, entry: Mapping[str, Any]) -> Optional[float]:
        if kind != "kept":
            return entry[side_key]
        delta = (entry["measures"][measure_key] if measure_key else entry["totals"][total_key])["delta"]
        return None if delta is None else abs(delta)

    items = []
    for kind in ("added", "removed", "kept"):
        for position, entry in enumerate(result[kind]):
            label = entry["newer"]["label"] if kind == "kept" else entry["label"]
            size = None if order == "name" else magnitude(kind, entry)
            items.append(((size is None, -(size or 0), _ordinal(label), _KINDS[kind], position), kind, entry))
    items.sort(key=lambda item: item[0])
    chosen = items if top is None else items[:top]
    return [(kind, entry) for _, kind, entry in chosen]
