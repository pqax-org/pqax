"""python -m pqax diff OLDER NEWER: the comparison of two footprints, as a text report or as one JSON object.

The text and the JSON are the same as those of pqax diff in C#, apart from the line ends.
"""
from __future__ import annotations

import json
import os
import sys
from typing import Any, Dict, List, Optional, Tuple

from . import diff
from ._json import format_timestamp, parse_timestamp
from .reader import open as open_document, open_embedded

MISSING = "n/a"
ABSENT = "-"


def _time(value: Any) -> Optional[str]:
    try:
        return format_timestamp(parse_timestamp(value)) if isinstance(value, str) else None
    except (ValueError, TypeError, OverflowError):
        return None


def _header(name: str, document: Any, side: Dict[str, Any], joined: Any) -> Dict[str, Any]:
    """The header of a file in the JSON: as in python -m pqax tables --json, with the time in the form of the format."""
    manifest, collection = document.manifest, document.collection
    level = collection.get("level")
    collected = _time(collection.get("completedAt"))
    return {
        "file": name, "embeddedPart": document.embedded_part, "format": manifest.get("format"),
        "formatVersion": manifest.get("formatVersion"),
        "collector": joined(collection, "collectorApp", "collectorAppVersion"),
        "library": joined(collection, "collectorLib", "collectorLibVersion"),
        "collectedAt": collected if collected is not None else _time(collection.get("startedAt")),
        "level": level if isinstance(level, int) and not isinstance(level, bool) else None,
        "tableCount": side["tableCount"], "filesRead": side["filesRead"], "warnings": side["warnings"],
    }


def _signed_bytes(value: Optional[float], format_bytes: Any) -> str:
    if value is None:
        return MISSING
    if value == 0:
        return "0 B"
    return ("+" if value > 0 else "") + format_bytes(value)


def _signed_count(value: Optional[int]) -> str:
    if value is None:
        return MISSING
    return f"+{value:,}" if value > 0 else f"{value:,}"


def _negated(value: Optional[float]) -> Optional[float]:
    return None if value is None else 0 if value == 0 else -value


def _count(value: Optional[int]) -> str:
    return MISSING if value is None else f"{value:,}"


def _flag(value: Optional[bool]) -> str:
    return MISSING if value is None else "true" if value else "false"


def _align(rows: List[List[str]], right_from: int) -> List[str]:
    widths = [max(len(row[column]) for row in rows) for column in range(len(rows[0]))]
    return ["  ".join(cell.rjust(widths[i]) if i >= right_from else cell.ljust(widths[i])
                      for i, cell in enumerate(row)).rstrip() for row in rows]


def text_report(older: Dict[str, Any], newer: Dict[str, Any], result: Dict[str, Any],
                ranked: List[Tuple[str, Dict[str, Any]]], order: str, top: Optional[int], format_bytes: Any) -> str:
    """The text of python -m pqax diff, from the two headers of the JSON, the result of compare, and the ranked changes."""
    lines = []
    for title, header in (("Older", older), ("Newer", newer)):
        count = header["tableCount"]
        lines.append(f"{title}: {header['file']}, {count:,} {'table' if count == 1 else 'tables'}, collected "
                     f"{header['collectedAt'] or MISSING}")
    counts = result["counts"]
    lines.append(f"Tables: {counts['added']:,} added, {counts['removed']:,} removed, {counts['kept']:,} kept, "
                 f"{counts['changed']:,} changed; by {order}" + (f", the first {top:,}" if top is not None else ""))
    footprint, waste = result["collection"]["footprint"], result["collection"]["waste"]
    lines.append(f"Footprint: {format_bytes(footprint['older'])} to {format_bytes(footprint['newer'])}, "
                 f"{_signed_bytes(footprint['delta'], format_bytes)}; waste: {format_bytes(waste['older'])} to "
                 f"{format_bytes(waste['newer'])}, {_signed_bytes(waste['delta'], format_bytes)}")
    lines.append("")
    rows = [["Change", "Table", "Total before", "Total after", "Total change", "Waste change", "Files change",
             "Rows change", "Files added", "Files removed"]]
    columns = [["Table", "Column", "Change", "Detail"]]
    for kind, entry in ranked:
        if kind == "added":
            rows.append(["recreated" if entry["recreated"] else "added", entry["label"], ABSENT,
                         format_bytes(entry["total"]), _signed_bytes(entry["total"], format_bytes),
                         _signed_bytes(entry["waste"], format_bytes), _signed_count(entry["fileCount"]),
                         _signed_count(entry["rowCount"]), ABSENT, ABSENT])
            continue
        if kind == "removed":
            rows.append(["replaced" if entry["recreated"] else "removed", entry["label"], format_bytes(entry["total"]),
                         ABSENT, _signed_bytes(_negated(entry["total"]), format_bytes),
                         _signed_bytes(_negated(entry["waste"]), format_bytes),
                         _signed_count(_negated(entry["fileCount"])), _signed_count(_negated(entry["rowCount"])),
                         ABSENT, ABSENT])
            continue
        label = entry["newer"]["label"] + (f" (was {entry['older']['label']})" if entry["renamed"] else "")
        files = entry["files"]
        rows.append(["renamed" if entry["renamed"] else entry["status"], label, format_bytes(entry["older"]["total"]),
                     format_bytes(entry["newer"]["total"]),
                     _signed_bytes(entry["measures"]["stack.total"]["delta"], format_bytes),
                     _signed_bytes(entry["measures"]["stack.waste"]["delta"], format_bytes),
                     _signed_count(entry["totals"]["fileCount"]["delta"]),
                     _signed_count(entry["totals"]["rowCount"]["delta"]),
                     _count(files["added"]) if files is not None else MISSING,
                     _count(files["removed"]) if files is not None else MISSING])
        table = entry["newer"]["label"]
        for column in entry["columns"]["added"]:
            columns.append([table, column["label"], "added", column["dataType"] or MISSING])
        for column in entry["columns"]["removed"]:
            columns.append([table, column["label"], "removed", column["dataType"] or MISSING])
        for column in entry["columns"]["kept"]:
            if column["status"] != "changed":
                continue
            old, new = column["older"], column["newer"]
            parts = []
            if column["renamed"]:
                parts.append(f"was {old['name'] if old['name'] is not None else MISSING}")
            if old["dataType"] != new["dataType"]:
                parts.append(f"type {old['dataType'] or MISSING} to {new['dataType'] or MISSING}")
            if old["isNullable"] != new["isNullable"]:
                parts.append(f"nullable {_flag(old['isNullable'])} to {_flag(new['isNullable'])}")
            size = column["totals"]["compressedSize"]["delta"]
            if size is not None and size != 0:
                parts.append(f"size {_signed_bytes(size, format_bytes)}")
            columns.append([table, new["label"], "renamed" if column["renamed"] else "changed", "; ".join(parts)])
    lines += _align(rows, 2)
    if len(columns) > 1:
        lines.append("")
        lines += _align(columns, 4)
    return "\n".join(lines)


def run(older_path: str, newer_path: str, part: Optional[str], order: str, top: Optional[int], as_json: bool) -> int:
    """Runs python -m pqax diff and returns the exit code. The errors of the reader go to the caller."""
    from . import __main__ as cli  # the helpers that python -m pqax tables uses, imported here to avoid a cycle

    def opened(path: str) -> Any:
        embedded = cli._embedded_part(path, part)
        return open_embedded(path, embedded) if embedded else open_document(path)

    with opened(older_path) as older, opened(newer_path) as newer:
        result = diff.compare(older, newer)
        headers = (_header(os.path.basename(older_path), older, result["older"], cli._joined),
                   _header(os.path.basename(newer_path), newer, result["newer"], cli._joined))
    for header in headers:
        for warning in header["warnings"]:
            print(f"pqax diff: {header['file']}: {warning}", file=sys.stderr)
    ranked = diff.rank(result, order, top)
    if as_json:
        print(json.dumps({
            "older": headers[0], "newer": headers[1], "smallFileSize": result["smallFileSize"],
            "tinyFileSize": result["tinyFileSize"], "sort": order, "top": top, "counts": result["counts"],
            "collection": result["collection"],
            "added": [entry for kind, entry in ranked if kind == "added"],
            "removed": [entry for kind, entry in ranked if kind == "removed"],
            "kept": [entry for kind, entry in ranked if kind == "kept"],
        }, indent=2, ensure_ascii=False))
        return 0
    print(text_report(headers[0], headers[1], result, ranked, order, top, cli.format_bytes))
    return 0

