"""The command line: python -m pqax check <file>, python -m pqax info <file>.

check exits with 0 when the file is valid, 1 when it is not, and 2 on a usage error or a file that cannot be read.
Both commands also take a VPAX file, and then work on the PQAX file that it embeds as Footprint.pqax.
"""
from __future__ import annotations

import argparse
import json
import sys
import zipfile
from typing import Any, List, Optional

from . import __version__
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


def main(argv: Optional[List[str]] = None) -> int:
    """Runs the command line and returns the exit code."""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(errors="backslashreplace")  # type: ignore[union-attr]
        except (AttributeError, ValueError):
            pass
    parser = argparse.ArgumentParser(prog="pqax", description="Check and describe PQAX files.")
    parser.add_argument("--version", action="version",
                        version=f"pqax {__version__}, reads format 0.x, writes format {FORMAT_VERSION}")
    commands = parser.add_subparsers(dest="command", metavar="command")
    commands.required = True
    check = commands.add_parser("check", help="validate a file; exit code 0 when valid, 1 when not")
    info = commands.add_parser("info", help="print the manifest: format, version, collection, and parts")
    for command in (check, info):
        command.add_argument("file", help="a .pqax file, or a .vpax file that embeds one")
        command.add_argument("--part", help=f"the embedded part of a package (default: {EMBEDDED_PART} when the file "
                                            f"has no manifest.json)")
    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "check":
            return _check(arguments.file, arguments.part)
        return _info(arguments.file, arguments.part)
    except OSError as error:
        print(f"pqax: {error}", file=sys.stderr)
        return 2
    except (PqaxError, zipfile.BadZipFile) as error:
        print(f"pqax: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
