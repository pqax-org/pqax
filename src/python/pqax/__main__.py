"""The command line: python -m pqax check <file>, python -m pqax info <file>, python -m pqax tables <file>, and
python -m pqax diff <older> <newer>.

check exits with 0 when the file is valid, 1 when it is not, and 2 on a usage error or a file that cannot be read.
tables writes one row per table with the derived measures of the storage views, or one JSON object with --json.
The commands also take a VPAX file, and then work on the PQAX file that it embeds as Footprint.pqax.
"""
from __future__ import annotations

import argparse
import decimal
import json
import math
import os
import sys
import zipfile
from typing import Any, Dict, List, Optional

from . import __version__, _diff_command, diff, measures
from ._container import find_entry, open_archive
from ._errors import ContainerError, PqaxError
from ._schema import EMBEDDED_PART, FORMAT_VERSION, MANIFEST_PART, SUMMARY_PART
from .reader import open as open_document, open_embedded
from .validator import validate, validate_embedded


def _embedded_part(path: str, part: Optional[str]) -> Optional[str]:
    """The part to read when the file is a package: the one asked for, or Footprint.pqax when there is no manifest."""
    if part:
        return part
    try:
        archive, fp, owned = open_archive(path)
    except ContainerError:
        return None
    try:
        if MANIFEST_PART in archive.namelist():
            return None
        info = find_entry(archive, EMBEDDED_PART)
        return info.filename if info is not None else None
    finally:
        archive.close()
        if owned:
            fp.close()


def _check(path: str, part: Optional[str]) -> int:
    embedded = _embedded_part(path, part)
    result = validate_embedded(path, embedded) if embedded else validate(path)
    if embedded:
        print(f"{path}: the PQAX file embedded as {embedded}")
    for issue in result.issues:
        print(issue)
    verdict = "valid" if result.is_valid else f"invalid, {len(result.issues):,} issue(s)"
    print(f"{path}: {verdict}, {result.checks:,} checks")
    return 0 if result.is_valid else 1


def _text(value: Any) -> str:
    return value if isinstance(value, str) else json.dumps(value, ensure_ascii=False)


def _info(path: str, part: Optional[str]) -> int:
    embedded = _embedded_part(path, part)
    with (open_embedded(path, embedded) if embedded else open_document(path)) as document:
        manifest = document.manifest
        lines: List[str] = [f"file: {path}"]
        if document.embedded_part:
            lines.append(f"embedded part: {document.embedded_part}, {document.embedded_compression}")
        lines.append(f"format: {_text(manifest.get('format'))}")
        lines.append(f"formatVersion: {_text(manifest.get('formatVersion'))}")
        collection = manifest.get("collection")
        if isinstance(collection, dict):
            lines.append("collection: " + ", ".join(f"{name} {_text(value)}" for name, value in collection.items()))
        for member in ("obfuscatorDictionaryId", "obfuscatorLib", "obfuscatorLibVersion"):
            if member in manifest:
                lines.append(f"{member}: {_text(manifest[member])}")
        others = [name for name in manifest if name not in
                  ("format", "formatVersion", "collection", "parts", "obfuscatorDictionaryId", "obfuscatorLib",
                   "obfuscatorLibVersion")]
        if others:
            lines.append("other members: " + ", ".join(others))
        parts = manifest.get("parts") if isinstance(manifest.get("parts"), list) else []
        entries = {info.filename: info for info in document._archive.infolist()}
        lines.append(f"parts: {len(parts)}")
        width = max((len(str(item.get("name"))) for item in parts if isinstance(item, dict)), default=0)
        for item in parts:
            if not isinstance(item, dict):
                continue
            name = str(item.get("name"))
            tables = item.get("tables") if isinstance(item.get("tables"), dict) else {}
            rows = ", ".join(f"{table} {count:,}" if type(count) is int else f"{table} {_text(count)}"
                             for table, count in tables.items())
            entry = entries.get(name)
            size = (f"{entry.compress_size:>10,} bytes in the zip, {entry.file_size:>11,} of JSON"
                    if entry is not None else "missing from the archive")
            lines.append(f"  {name:<{width}}  {size}  {rows}")
        total = {name: document.row_count(name) for name in document.table_names}
        if total:
            lines.append("rows: " + ", ".join(f"{name} {count:,}" for name, count in total.items()))
        if SUMMARY_PART not in document.part_names:
            lines.append("the manifest lists no summary.json")
    print("\n".join(lines))
    return 0


ORDERS = ("waste", "total", "rows", "files", "name")
MISSING = "n/a"
UNITS = ("B", "KB", "MB", "GB", "TB", "PB")


def _fixed(value: float, digits: int) -> str:
    """A number with separators and a fixed number of decimals, as the views write it: the shortest decimal that gives
    back the float, rounded half away from zero, so that 1.125 and 1.005 become 1.13 and 1.01."""
    shortest = decimal.Decimal(repr(float(value)))
    exact = shortest.quantize(decimal.Decimal(1).scaleb(-digits), rounding=decimal.ROUND_HALF_UP)
    return f"{exact:,.{digits}f}"


def format_bytes(value: Optional[float]) -> str:
    """Bytes in 1024-based units, as the storage views write them: 512 B, 1.50 KB, 21.5 MB."""
    if value is None or not math.isfinite(value):
        return MISSING
    scaled, unit = abs(value), 0
    while scaled >= 1024 and unit < len(UNITS) - 1:
        scaled /= 1024
        unit += 1
    digits = 0 if unit == 0 else 2 if scaled < 10 else 1 if scaled < 100 else 0
    return f"{'-' if value < 0 else ''}{_fixed(scaled, digits)} {UNITS[unit]}"


def format_share(value: Optional[float]) -> str:
    """A share from 0 to 1 as a percentage with one decimal; <0.1% for a share above 0 that rounds to 0."""
    if value is None or not math.isfinite(value):
        return MISSING
    return "<0.1%" if 0 < value < 0.0005 else _fixed(value * 100, 1) + "%"


def _count(value: Optional[int]) -> str:
    return MISSING if value is None else f"{value:,}"


def _small(files: Dict[str, Any]) -> str:
    """A share of small files. A share from the quartiles is a lower bound, written with >=, except 0 and 1."""
    share = files.get("shareAtLeast")
    bound = files.get("count") is None and share is not None and 0 < share < 1
    return (">=" if bound else "") + format_share(share)


def _ordinal(label: str) -> bytes:
    """The order of the views and of C#: by UTF-16 code units."""
    return label.encode("utf-16-be", errors="surrogatepass")


def sort_tables(tables: List[Dict[str, Any]], order: str) -> List[Dict[str, Any]]:
    """The tables, sorted: by waste, stack total, rows, or files, the largest first, or by label. Ties go by the stack
    total, the largest first, then by the label, then by the order of the file. A missing count, waste, or total goes
    last."""
    def descending(value: Optional[float]) -> Any:
        return (value is None, -(value or 0))

    def key(item: Any) -> Any:
        position, table = item
        stack = table["stack"]
        label = _ordinal(table["label"])
        if order == "name":
            return (label, position)
        if order in ("rows", "files"):
            primary = descending(table["rowCount" if order == "rows" else "fileCount"])
        else:
            primary = descending(stack["waste"]) if order == "waste" else (False, 0)
        return (primary, descending(stack["total"]), label, position)
    return [table for _, table in sorted(enumerate(tables), key=key)]


def _joined(collection: Dict[str, Any], app: str, version: str) -> Optional[str]:
    first, second = collection.get(app), collection.get(version)
    if not isinstance(first, str):
        return None
    return f"{first} {second}" if isinstance(second, str) else first


# The keys of a table in the JSON of pqax tables --json, as in version 0.2.0: the measures that the command shows.
_SHOWN_MAINTENANCE = ("lastCommitAt", "commitsPerDay", "daysSinceLastCommit")


def _shown(table: Dict[str, Any]) -> Dict[str, Any]:
    """A table of measures.measure_tables with the keys of the JSON of the command."""
    shown = {key: table[key] for key in ("id", "name", "label", "rowCount", "fileCount", "fileSize", "stack",
                                         "smallFiles", "tinyFiles", "measures", "maintenance")}
    shown["stack"] = {key: value for key, value in table["stack"].items() if key != "shares"}
    shown["maintenance"] = {key: table["maintenance"][key] for key in _SHOWN_MAINTENANCE}
    return shown


def _tables(path: str, part: Optional[str], order: str, top: Optional[int], as_json: bool, wide: bool) -> int:
    embedded = _embedded_part(path, part)
    with (open_embedded(path, embedded) if embedded else open_document(path)) as document:
        result = measures.measure(document)
        manifest, collection = document.manifest, document.collection
        embedded_part = document.embedded_part
    for warning in result["warnings"]:
        print(f"pqax tables: {warning}", file=sys.stderr)
    tables = sort_tables(result["tables"], order)
    if top is not None:
        tables = tables[:top]
    name = os.path.basename(path)
    if as_json:
        completed, started = collection.get("completedAt"), collection.get("startedAt")
        level = collection.get("level")
        print(json.dumps({
            "file": name, "embeddedPart": embedded_part, "format": manifest.get("format"),
            "formatVersion": manifest.get("formatVersion"),
            "collector": _joined(collection, "collectorApp", "collectorAppVersion"),
            "library": _joined(collection, "collectorLib", "collectorLibVersion"),
            "collectedAt": completed if isinstance(completed, str) else started if isinstance(started, str) else None,
            "level": level if isinstance(level, int) and not isinstance(level, bool) else None,
            "smallFileSize": measures.DEFAULT_SMALL_FILE_SIZE, "tinyFileSize": measures.DEFAULT_TINY_FILE_SIZE,
            "sort": order, "top": top, "tableCount": len(result["tables"]), "filesRead": result["filesRead"],
            "warnings": result["warnings"], "tables": [_shown(table) for table in tables],
        }, indent=2, ensure_ascii=False))
        return 0
    count = len(result["tables"])
    print(f"{name}: {count:,} {'table' if count == 1 else 'tables'}, by {order}"
          + (f", the first {top:,}" if top else ""))
    header = ["Table", "Rows", "Files", "Live", "Non-live", "Unreferenced", "Deleted", "Stale", "Waste", "Waste share"]
    if wide:
        header += ["Under 8 MiB", "Under 128 MiB", "Metadata", "Footers"]
    rows = [header]
    for table in tables:
        stack = table["stack"]
        cells = [table["label"], _count(table["rowCount"]), _count(table["fileCount"])]
        cells += [format_bytes(stack[key]) for key in ("live", "nonLive", "unreferenced", "deleted", "stale", "waste")]
        cells.append(format_share(stack["wasteShare"]))
        if wide:
            cells += [_small(table["tinyFiles"]), _small(table["smallFiles"]), format_bytes(stack["metadata"]),
                      format_bytes(stack["footers"])]
        rows.append(cells)
    widths = [max(len(row[column]) for row in rows) for column in range(len(header))]
    for row in rows:
        line = "  ".join(cell.ljust(widths[i]) if i == 0 else cell.rjust(widths[i]) for i, cell in enumerate(row))
        print(line.rstrip())
    return 0


def _positive(text: str) -> int:
    if not text.isdigit() or int(text) < 1:
        raise argparse.ArgumentTypeError("needs a number of at least 1")
    return int(text)


def main(argv: Optional[List[str]] = None) -> int:
    """Runs the command line and returns the exit code."""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(errors="backslashreplace")  # type: ignore[union-attr]
        except (AttributeError, ValueError):
            pass
    parser = argparse.ArgumentParser(prog="pqax",
                                     description="Check and describe PQAX files, and measure their tables.")
    parser.add_argument("--version", action="version",
                        version=f"pqax {__version__}, reads format 0.x, writes format {FORMAT_VERSION}")
    commands = parser.add_subparsers(dest="command", metavar="command")
    commands.required = True
    check = commands.add_parser("check", help="validate a file; exit code 0 when valid, 1 when not")
    info = commands.add_parser("info", help="print the manifest: format, version, collection, and parts")
    tables = commands.add_parser("tables", help="one row per table with its storage, waste, and waste share, as the "
                                                "storage views compute them")
    tables.add_argument("--sort", choices=ORDERS, default="waste",
                        help="the order: the largest first, or by name (default: waste)")
    tables.add_argument("--top", type=_positive, metavar="N", help="the first N tables of the order")
    tables.add_argument("--wide", action="store_true",
                        help="add the shares of the files under 8 MiB and 128 MiB, the metadata, and the footers")
    tables.add_argument("--json", action="store_true",
                        help="write one JSON object with the collection and every measure of each table")
    export = commands.add_parser("export", help="write the tables of a footprint and their measures as CSV, Parquet, "
                                                "or Delta, appending to the exports of earlier days")
    export.add_argument("file", help="a .pqax file, or a .vpax file that embeds one")
    export.add_argument("--to", required=True, metavar="FOLDER", help="the folder of the export, created when missing")
    export.add_argument("--format", choices=("csv", "parquet", "delta"), default="csv",
                        help="csv, or parquet with pqax[parquet], or delta with pqax[delta] (default: csv)")
    export.add_argument("--part", help=f"the embedded part of a package (default: {EMBEDDED_PART} when the file "
                                       f"has no manifest.json)")
    export.add_argument("--if-exists", choices=("error", "skip"), default="error",
                        help="when the folder already has the footprint: exit with 1, or write nothing and exit with 0 "
                             "(default: error)")
    for command in (check, info, tables):
        command.add_argument("file", help="a .pqax file, or a .vpax file that embeds one")
        command.add_argument("--part", help=f"the embedded part of a package (default: {EMBEDDED_PART} when the file "
                                            f"has no manifest.json)")
    compare = commands.add_parser("diff", help="compare two footprints of the same lakehouse: the tables and columns "
                                               "added, removed, and kept, and the change of every total and measure")
    compare.add_argument("older", help="the older footprint: a .pqax file, or a .vpax file that embeds one")
    compare.add_argument("newer", help="the newer footprint")
    compare.add_argument("--part", help=f"the embedded part of a package, for both files (default: {EMBEDDED_PART} "
                                        f"when a file has no manifest.json)")
    compare.add_argument("--sort", choices=diff.ORDERS, default="total",
                         help="the order: the largest change first, or by name (default: total)")
    compare.add_argument("--top", type=_positive, metavar="N", help="the first N changes of the order")
    compare.add_argument("--json", action="store_true",
                         help="write one JSON object with both headers and the tables added, removed, and kept")
    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "check":
            return _check(arguments.file, arguments.part)
        if arguments.command == "tables":
            return _tables(arguments.file, arguments.part, arguments.sort, arguments.top, arguments.json,
                           arguments.wide)
        if arguments.command == "export":
            from .export import command as export_command
            return export_command(arguments.file, arguments.to, arguments.format, arguments.part, arguments.if_exists)
        if arguments.command == "diff":
            return _diff_command.run(arguments.older, arguments.newer, arguments.part, arguments.sort, arguments.top,
                                     arguments.json)
        return _info(arguments.file, arguments.part)
    except OSError as error:
        print(f"pqax: {error}", file=sys.stderr)
        return 2
    except (PqaxError, zipfile.BadZipFile) as error:
        print(f"pqax: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
