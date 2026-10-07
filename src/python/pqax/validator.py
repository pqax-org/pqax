"""Validation of a PQAX file: the rules of the serialization that a program can check, with their codes.

The codes come from section 4.1 of docs/contracts.md, and the exact condition of each, the fields
that each covers, and the rules that are skipped after an earlier failure from conformance/rules.md.
"""
from __future__ import annotations

import collections
import dataclasses
import itertools
import json
import re
import struct
import zipfile
import zlib
from typing import Any, Dict, List, NamedTuple, Optional, Set, Tuple

from ._container import find_entry, open_archive, read_embedded, zip64_findings
from ._errors import ContainerError, PartMissingError
from ._json import BOM, _reject_constant
from ._schema import (
    COLLECTION_INTEGERS, COLLECTION_TIMESTAMPS, COLLECTION_TYPES, DEPTHS, DETAIL_TABLES, EMBEDDED_PART, FORMAT,
    FORMAT_VERSION_PATTERN, ID_TABLES, INTEGER_LISTS, INTEGERS, KNOWN_MAJOR_VERSIONS, MANIFEST_PART,
    MANIFEST_REQUIRED, MAX_INTEGER, MAX_PART_SIZE, NESTED_INTEGERS, NULLABLE_ELEMENTS, OBFUSCATION_MEMBERS,
    OBJECT_MEMBERS, PART_NAME, REFERENCES, REQUIRED, ROWS_PER_PART, SUMMARY_PART, SUMMARY_TABLES, TABLES,
    TIMESTAMP_PATTERN, TIMESTAMPS, TYPES, major_version,
)

_NONE = type(None)
_READABLE_METHODS = {zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED, zipfile.ZIP_BZIP2, zipfile.ZIP_LZMA}
_READ_ERRORS = (RuntimeError, NotImplementedError, zipfile.BadZipFile, zipfile.LargeZipFile, zlib.error, EOFError,
                ValueError, struct.error, OSError)
_TIME_LIKE = re.compile("[0-9TZ:. +-]{1,40}")
_ASCII_LOWER = {code: code + 32 for code in range(ord("A"), ord("Z") + 1)}
_FAILED = object()


@dataclasses.dataclass(frozen=True)
class Issue:
    """A rule that the file breaks: its code, a message, and the part, the table, and the field when they apply."""

    code: str
    message: str
    part: Optional[str] = None
    table: Optional[str] = None
    field: Optional[str] = None

    def __str__(self) -> str:
        where = ", ".join(f"{label} {value}" for label, value in
                          (("part", self.part), ("table", self.table), ("field", self.field)) if value is not None)
        return f"{self.code}: {self.message}" + (f" [{where}]" if where else "")


@dataclasses.dataclass
class ValidationResult:
    """What validate() found: the issues, and the number of checks that it ran."""

    issues: List[Issue]
    checks: int

    @property
    def is_valid(self) -> bool:
        """True when the file breaks no rule."""
        return not self.issues

    @property
    def codes(self) -> Set[str]:
        """The codes of the issues, each once."""
        return {issue.code for issue in self.issues}


class _PartInfo(NamedTuple):
    name: str
    tables: Optional[Dict[str, Any]]  # the rows stated for each table, a number or None; None with no tables at all
    present: bool


class _Piece(NamedTuple):
    part: str
    arrays: Dict[str, List[Any]]
    rows: int  # the length of the longest array, never the count of the manifest, which can be anything
    aligned: bool  # all the arrays of the table have one length in this part
    stated: Optional[int]  # the rows that the manifest states for the table in this part, when it is an integer


def _number(value: Any) -> bool:
    """Whether a value is a JSON number: an int or a float, not a boolean."""
    kind = type(value)
    return kind is int or kind is float


def _key(value: Any) -> Any:
    """A value that can be an id: a number, or None."""
    return value if _number(value) else None


def _json_type(value: Any) -> str:
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "boolean"
    if isinstance(value, (int, float)):
        return "number"
    if isinstance(value, str):
        return "string"
    return "array" if isinstance(value, list) else "object"


def _show(value: Any) -> str:
    """A value for a message: never a string that could be a name from the source."""
    if value is None:
        return "null"
    if isinstance(value, bool):
        return json.dumps(value)
    if isinstance(value, int):
        return f"{value:,}"
    if isinstance(value, float):
        return repr(value)
    if isinstance(value, str) and _TIME_LIKE.fullmatch(value):
        return repr(value)
    return f"a value of type {_json_type(value)}"


def _show_known(value: Any) -> str:
    """A value that the format defines, such as a format or a version, for a message."""
    text = json.dumps(value, ensure_ascii=True, default=str)
    return text if len(text) <= 40 else text[:37] + "..."


def _differs(stated: Any, actual: Any) -> bool:
    """Whether a total differs from what the detail says. A null total is not compared, and neither is a total of
    another JSON type, which json.type reports and which is unknown to every other rule."""
    return _number(stated) and stated != actual


# The Python types that a value of each type of field can have, null included, and those of the elements of a list
# or of the values of a map.
_TYPES = {"integer": {int, float, _NONE}, "string": {str, _NONE}, "timestamp": {str, _NONE},
          "boolean": {bool, _NONE}, "strings": {list, _NONE}, "integers": {list, _NONE}, "map": {dict, _NONE},
          "object": {dict, _NONE}}
_ELEMENTS = {"strings": {str}, "integers": {int, float}, "map": {str}}
_EXPECTED = {"integer": "numbers", "string": "strings", "timestamp": "strings", "boolean": "true or false",
             "strings": "arrays of strings", "integers": "arrays of numbers", "map": "objects of strings",
             "object": "objects"}


def _type_problem(value: Any, kind: str, nullable_elements: bool) -> Optional[str]:
    """What is wrong with the JSON type of a value, or None when nothing is."""
    if type(value) not in _TYPES[kind]:
        return f"of type {_json_type(value)}"
    if value is None or kind not in _ELEMENTS:
        return None
    elements = _ELEMENTS[kind]
    for item in (value.values() if kind == "map" else value):
        if type(item) not in elements and not (item is None and nullable_elements):
            return f"of type {_json_type(value)} with an element of type {_json_type(item)}"
    return None


class _Validator:
    def __init__(self) -> None:
        self.issues: List[Issue] = []
        self.checks = 0
        self.pieces: Dict[str, List[_Piece]] = collections.defaultdict(list)
        self.incomplete: Set[str] = set()  # tables with rows in a part that could not be read
        self.lost_unknown = False  # a part that could not be read, for which the manifest states no tables

    def count(self, checks: int = 1) -> None:
        self.checks += checks

    def report(self, code: str, message: str, part: Optional[str] = None, table: Optional[str] = None,
               field: Optional[str] = None) -> None:
        self.issues.append(Issue(code, message, part, table, field))

    def result(self) -> ValidationResult:
        return ValidationResult(self.issues, self.checks)

    # ---- the steps ----

    def run(self, archive: zipfile.ZipFile, fp: Any) -> None:
        entries = archive.infolist()
        self.check_container(archive, fp, entries)
        by_name = {info.filename: info for info in entries}
        self.count()
        info = by_name.get(MANIFEST_PART)
        if info is None:
            message = "The archive has no manifest.json entry."
            embedded = find_entry(archive, EMBEDDED_PART)
            if embedded is not None:
                message += f" It has a part named {embedded.filename}: validate it with validate_embedded."
            self.report("container.manifestMissing", message)
            return
        manifest = self.load(archive, info, MANIFEST_PART)
        if manifest is _FAILED:
            return
        self.count()
        if not isinstance(manifest, dict):
            self.report("json.topLevel", "The top level of the manifest is not an object.", MANIFEST_PART)
            return
        parts = self.check_manifest(manifest, by_name)
        if parts is None:
            return
        for part in parts:
            if not part.present:
                self.lose(part)
                continue
            content = self.load(archive, by_name[part.name], part.name)
            if content is _FAILED:
                self.lose(part)
                continue
            self.count()
            if not isinstance(content, dict):
                self.report("json.topLevel", "The top level of the part is not an object.", part.name)
                self.lose(part)
                continue
            self.check_part(part, content)
        self.check_required()
        ids = self.check_ids()
        if not self.lost_unknown:
            self.check_references(ids)
            self.check_depth_and_totals()

    def lose(self, part: _PartInfo) -> None:
        """Records that the rows of a part are not available, so that the rules that need them are skipped."""
        if part.tables is None:
            self.lost_unknown = True
        else:
            self.incomplete.update(part.tables)

    def load(self, archive: zipfile.ZipFile, info: zipfile.ZipInfo, name: str) -> Any:
        """A document as parsed, after the rules of section 4 on its bytes, or _FAILED when it cannot be parsed."""
        if info.flag_bits & 0x1 or info.compress_type not in _READABLE_METHODS:
            return _FAILED  # container.encrypted or container.method says why
        if info.file_size > MAX_PART_SIZE:
            self.count()
            self.report("json.invalid", f"The document holds {info.file_size:,} bytes, more than the {MAX_PART_SIZE:,} "
                                        f"that the validator reads.", name)
            return _FAILED
        try:
            data = archive.read(info)
        except _READ_ERRORS as error:
            self.count()
            self.report("json.invalid", f"The document cannot be read: {error}.", name)
            return _FAILED
        self.count()
        if data.startswith(BOM):
            self.report("json.bom", "The document starts with a byte order mark.", name)
            data = data[len(BOM):]
        self.count()
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError as error:
            self.report("json.invalid", f"The document is not valid UTF-8: {error}.", name)
            return _FAILED
        repeated = [0]

        def unique(pairs: List[Tuple[str, Any]]) -> Dict[str, Any]:
            value = dict(pairs)
            if len(value) != len(pairs):
                repeated[0] += len(pairs) - len(value)
            return value

        try:
            value = json.loads(text, object_pairs_hook=unique, parse_constant=_reject_constant)
        except (ValueError, RecursionError) as error:
            self.report("json.invalid", f"The document is not valid JSON: {error}.", name)
            return _FAILED
        self.count()
        if repeated[0]:
            self.report("json.duplicateMember", f"An object repeats a member name, {repeated[0]:,} time(s) in the "
                                                f"document.", name)
        return value

    # ---- section 3: the container ----

    def check_container(self, archive: zipfile.ZipFile, fp: Any, entries: List[zipfile.ZipInfo]) -> None:
        for info in entries:
            self.count(2)
            if info.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
                self.report("container.method", f"The entry is compressed with method {info.compress_type}. The "
                                                f"format allows stored (0) and deflate (8).", info.filename)
            if info.flag_bits & 0x1:
                self.report("container.encrypted", "The entry is encrypted.", info.filename)
        self.count()
        for entry, finding in zip64_findings(fp, archive):
            self.report("container.zip64", f"{finding} The format does not allow ZIP64.", entry)
        self.count()
        groups: Dict[str, List[str]] = collections.defaultdict(list)
        for info in entries:
            groups[info.filename.translate(_ASCII_LOWER)].append(info.filename)
        for names in groups.values():
            if len(names) > 1:
                self.report("container.caseCollision", f"{len(names)} entries have names that are the same or differ "
                                                       f"only in case: {', '.join(names)}.", names[0])

    # ---- sections 7.1 and 9: the manifest ----

    def check_manifest(self, manifest: Dict[str, Any], by_name: Dict[str, Any]) -> Optional[List[_PartInfo]]:
        """The rules on the manifest. It returns the parts to read, or None when a reader would refuse the file."""
        for member in MANIFEST_REQUIRED:
            self.count()
            if manifest.get(member) is None:
                self.report("manifest.required", f"The manifest has no {member}.", MANIFEST_PART, field=member)
        for member in ("format", "formatVersion"):
            if member in manifest:
                self.check_types([manifest[member]], "string", MANIFEST_PART, None, member)
        value = manifest.get("format")
        self.count()
        if value is not None and value != FORMAT:
            self.report("manifest.format", f"The manifest says that the format is {_show_known(value)}, not pqax.",
                        MANIFEST_PART, field="format")
            return None
        version = manifest.get("formatVersion")
        if version is not None:
            self.count()
            if not (isinstance(version, str) and FORMAT_VERSION_PATTERN.fullmatch(version)):
                self.report("manifest.formatVersion", f"formatVersion is {_show_known(version)}, which is not two "
                                                      f"numbers separated by a dot.", MANIFEST_PART,
                            field="formatVersion")
            elif major_version(version) not in KNOWN_MAJOR_VERSIONS:
                self.report("manifest.versionUnknown", f"The format version is {_show_known(version)}. This "
                                                       f"validator knows the versions 0.x.", MANIFEST_PART,
                            field="formatVersion")
                return None
        for member in OBFUSCATION_MEMBERS:
            if member in manifest:
                self.check_types([manifest[member]], "string", MANIFEST_PART, None, member)
        collection = manifest.get("collection")
        if collection is not None:
            self.count()
            if not isinstance(collection, dict):
                self.report("json.type", "collection is not an object.", MANIFEST_PART, field="collection")
            else:
                self.count()
                if collection.get("level") is None:
                    self.report("manifest.required", "collection has no level.", MANIFEST_PART,
                                field="collection.level")
                for member, kind in COLLECTION_TYPES.items():
                    if member in collection:
                        self.check_types([collection[member]], kind, MANIFEST_PART, None, f"collection.{member}")
                for member in COLLECTION_INTEGERS:
                    if member in collection:
                        self.check_integers([collection[member]], False, MANIFEST_PART, None, f"collection.{member}")
                for member in COLLECTION_TIMESTAMPS:
                    if member in collection:
                        self.check_timestamps([collection[member]], MANIFEST_PART, None, f"collection.{member}")
        parts = manifest.get("parts")
        if parts is None:
            return None  # manifest.required says so, and no rule on parts runs
        self.count()
        if not isinstance(parts, list):
            self.report("json.type", "parts is not an array.", MANIFEST_PART, field="parts")
            return None
        return self.check_parts(parts, by_name)

    def check_parts(self, parts: List[Any], by_name: Dict[str, Any]) -> List[_PartInfo]:
        self.count()
        first = parts[0].get("name") if parts and isinstance(parts[0], dict) else None
        if first != SUMMARY_PART:
            message = ("The manifest lists no part, so summary.json is not the first." if not parts
                       else "The first part of the manifest is not summary.json.")
            self.report("manifest.firstPart", message, first if isinstance(first, str) else MANIFEST_PART,
                        field="parts")
        result: List[_PartInfo] = []
        seen: Set[str] = set()
        for index, item in enumerate(parts):
            self.count()
            if not isinstance(item, dict):
                self.report("manifest.required", f"Element {index} of parts is not an object with a name and tables.",
                            MANIFEST_PART, field="parts")
                self.report("json.type", f"Element {index} of parts is of type {_json_type(item)}, not an object.",
                            MANIFEST_PART, field="parts")
                self.lost_unknown = True
                continue
            name = item.get("name")
            if not isinstance(name, str):
                if name is None:
                    self.report("manifest.required", f"Element {index} of parts has no name.", MANIFEST_PART,
                                field="parts")
                else:
                    self.report("json.type", f"The name of element {index} of parts is not a string.", MANIFEST_PART,
                                field="parts")
                # The part cannot be read: the rules that need its tables are skipped.
                if isinstance(item.get("tables"), dict):
                    self.incomplete.update(item["tables"])
                else:
                    self.lost_unknown = True
                continue
            self.count()
            if name in seen:
                self.report("manifest.duplicatePart", "The manifest lists the part more than once.", name)
                continue
            seen.add(name)
            self.count()
            if not PART_NAME.fullmatch(name):
                self.report("container.partName", "The name of the part is empty, or has a character other than "
                                                  "ASCII letters, digits, '.', '-', and '_'.", name)
            self.count()
            if name not in by_name:
                self.report("manifest.partMissing", "The manifest lists the part, and the archive has no entry with "
                                                    "that name.", name)
            result.append(_PartInfo(name, self.check_counts(name, item.get("tables")), name in by_name))
        return result

    def check_counts(self, part: str, tables: Any) -> Optional[Dict[str, Any]]:
        """The rows that the manifest states for each table of a part, or None when it states no tables."""
        self.count()
        if tables is None:
            self.report("manifest.required", "The element of parts has no tables.", part, field="tables")
            return None
        if not isinstance(tables, dict):
            self.report("json.type", "The tables of the element of parts are not an object.", part, field="tables")
            return None
        counts: Dict[str, Any] = {}
        for table, count in tables.items():
            self.count()
            if _number(count):
                self.check_integers([count], False, part, table, None, what="row count(s) of the manifest")
            elif count is not None:
                self.report("json.type", f"The manifest states a value of type {_json_type(count)} as the rows of the "
                                         f"table, not a number.", part, table)
            counts[table] = count if _number(count) else None
        return counts

    # ---- sections 5 and 7.2: a part and its tables ----

    def check_part(self, part: _PartInfo, content: Dict[str, Any]) -> None:
        is_summary = part.name == SUMMARY_PART
        stated = part.tables
        if stated is not None:
            for name in content:
                self.count()
                if name not in stated:
                    self.report("part.tables", "The part holds the table, and the manifest does not state it for "
                                               "this part.", part.name, name)
            for name in stated:
                self.count()
                if name not in content:
                    self.report("part.tables", "The manifest states the table for this part, and the part does not "
                                               "hold it.", part.name, name)
                    self.incomplete.add(name)
        for name in content:
            self.count()
            if is_summary and name in DETAIL_TABLES:
                self.report("part.kind", "A detail table is in summary.json.", part.name, name)
            elif not is_summary and name in SUMMARY_TABLES:
                self.report("part.kind", "A summary table is in a part other than summary.json.", part.name, name)
        if not is_summary:
            self.count()
            if len(content) > 1:
                self.report("part.singleTable", f"The part holds {len(content)} tables, and a detail part holds one.",
                            part.name)
            if stated is not None:
                self.count()
                rows = sum(count for count in stated.values() if count is not None and count > 0)
                if rows > ROWS_PER_PART:
                    self.report("part.tooLarge", f"The manifest states {rows:,} rows for the part, and a detail part "
                                                 f"holds at most {ROWS_PER_PART:,}.", part.name)
        for name, table in content.items():
            self.check_table(part, name, table if isinstance(table, dict) else {},
                             stated.get(name) if stated is not None else None)

    def check_table(self, part: _PartInfo, name: str, table: Dict[str, Any], expected: Any) -> None:
        """The rules on one table in one part."""
        arrays = {field: values for field, values in table.items() if isinstance(values, list)}
        lengths = {field: len(values) for field, values in arrays.items()}
        if expected is not None:
            for field, length in lengths.items():
                self.count()
                if length != expected:
                    self.report("part.rowCount", f"The array has {length:,} elements, and the manifest states "
                                                 f"{_show(expected)} rows for the table in this part.", part.name,
                                name, field)
        if name not in TABLES:
            return  # only the part.* rules apply to a table of a producer or of a later version
        for field, values in arrays.items():
            kind = TYPES[name].get(field)
            if kind is not None:
                self.check_types(values, kind, part.name, name, field, (name, field) in NULLABLE_ELEMENTS,
                                 OBJECT_MEMBERS.get((name, field)))
        for field in INTEGERS[name]:
            if field in arrays:
                self.check_integers(arrays[field], (name, field) in INTEGER_LISTS, part.name, name, field)
        for (table_name, field), members in NESTED_INTEGERS.items():
            if table_name == name and field in arrays:
                for member in members:
                    values = [value.get(member) for value in arrays[field] if isinstance(value, dict)]
                    if any(value is not None for value in values):
                        self.check_integers(values, False, part.name, name, f"{field}.{member}")
        for field in TIMESTAMPS.get(name, ()):
            if field in arrays:
                self.check_timestamps(arrays[field], part.name, name, field)
        distinct = set(lengths.values())
        stated = expected if type(expected) is int and expected >= 0 else None
        self.pieces[name].append(_Piece(part.name, arrays, max(distinct, default=0), len(distinct) <= 1, stated))

    # ---- section 4: values ----

    def check_types(self, values: List[Any], kind: str, part: str, table: Optional[str], field: str,
                    nullable_elements: bool = False, members: Optional[Dict[str, str]] = None) -> None:
        """json.type: each value is null or has the JSON type of its field, and so do the elements of a list, the
        values of a map, and the members of a nested object that the format defines."""
        self.count()
        allowed = _TYPES[kind]
        problem = not set(map(type, values)) <= allowed
        if not problem and kind in _ELEMENTS:
            elements = _ELEMENTS[kind] | ({_NONE} if nullable_elements else set())
            if kind == "map":
                found = set(map(type, itertools.chain.from_iterable(value.values() for value in values
                                                                    if type(value) is dict)))
            else:
                found = set(map(type, itertools.chain.from_iterable(value for value in values
                                                                    if type(value) is list)))
            problem = not found <= elements
        if problem:
            wrong = 0
            first: Optional[Tuple[int, str]] = None
            for index, value in enumerate(values):
                what = _type_problem(value, kind, nullable_elements)
                if what is not None:
                    wrong += 1
                    if first is None:
                        first = (index, what)
            if first is not None:
                self.report("json.type", f"{wrong:,} value(s) are not {_EXPECTED[kind]}. The first, in element "
                                         f"{first[0]:,}, is {first[1]}.", part, table, field)
        if members:
            objects = [value for value in values if type(value) is dict]
            for member, member_kind in members.items():
                found_values = [value.get(member) for value in objects]
                if any(value is not None for value in found_values):
                    self.check_types(found_values, member_kind, part, table, f"{field}.{member}")

    def check_integers(self, values: List[Any], lists: bool, part: str, table: Optional[str], field: Optional[str],
                       what: str = "") -> None:
        """json.integer and json.integerRange on the integers of the format: numbers, or lists of numbers."""
        self.count()
        kinds = set(map(type, values))
        if not lists:
            if kinds <= {int, _NONE}:
                integers = values if _NONE not in kinds else [value for value in values if value is not None]
                if not integers or (-MAX_INTEGER <= min(integers) and max(integers) <= MAX_INTEGER):
                    return
            elif float not in kinds and int not in kinds:
                return  # no number: a value of another type has no code
        fractions = far = 0
        first_fraction: Optional[Tuple[int, Any]] = None
        first_far: Optional[Tuple[int, Any]] = None
        for index, value in enumerate(values):
            for item in (value if lists and type(value) is list else (value,)):
                kind = type(item)
                if kind is float:
                    fractions += 1
                    if first_fraction is None:
                        first_fraction = (index, item)
                elif kind is not int:
                    continue
                if not -MAX_INTEGER <= item <= MAX_INTEGER:
                    far += 1
                    if first_far is None:
                        first_far = (index, item)
        subject = what or "number(s)"

        def where(index: int) -> str:
            return "" if what else f", in element {index:,}"

        if first_fraction is not None:
            self.report("json.integer", f"{fractions:,} {subject} are not integers. The first is "
                                        f"{first_fraction[1]!r}{where(first_fraction[0])}.", part, table, field)
        if first_far is not None:
            self.report("json.integerRange", f"{far:,} {subject} are beyond 2^53 - 1 in absolute value. The first is "
                                             f"{_show(first_far[1])}{where(first_far[0])}.", part, table, field)

    def check_timestamps(self, values: List[Any], part: str, table: Optional[str], field: str) -> None:
        self.count()
        wrong = 0
        first: Optional[Tuple[int, Any]] = None
        for index, value in enumerate(values):
            if type(value) is not str or TIMESTAMP_PATTERN.fullmatch(value):
                continue  # null is no value, and a value of another JSON type is a json.type issue
            wrong += 1
            if first is None:
                first = (index, value)
        if first is not None:
            self.report("json.timestamp", f"{wrong:,} value(s) are not timestamps in the form "
                                          f"2026-10-05T08:15:30.000Z. The first is {_show(first[1])}, in element "
                                          f"{first[0]:,}.", part, table, field)

    # ---- the tables across parts: object model and section 6 ----

    def rows(self, table: str) -> int:
        return sum(piece.rows for piece in self.pieces.get(table, ()))

    def column(self, table: str, field: str) -> List[Any]:
        """The values of a field across the parts, with None where a part has no value."""
        pieces = self.pieces.get(table, ())
        if len(pieces) == 1 and field in pieces[0].arrays and len(pieces[0].arrays[field]) == pieces[0].rows:
            return pieces[0].arrays[field]
        values: List[Any] = []
        for piece in pieces:
            array = piece.arrays.get(field)
            if array is None:
                values.extend([None] * piece.rows)
            else:
                values.extend(array)
                values.extend([None] * (piece.rows - len(array)))
        return values

    def part_of(self, table: str, row: int) -> Optional[str]:
        """The part that holds a row of a table."""
        for piece in self.pieces.get(table, ()):
            if row < piece.rows:
                return piece.part
            row -= piece.rows
        return None

    def usable(self, *tables: str) -> bool:
        """Whether the rows of the tables are all there, and aligned, so that the rules across tables can run."""
        return all(table not in self.incomplete and all(piece.aligned for piece in self.pieces.get(table, ()))
                   for table in tables)

    def typed(self, table: str, *fields: str) -> bool:
        """Whether fields that link rows hold only numbers and nulls. A value of another JSON type is unknown, so the
        rules that follow the link are skipped instead of counting the row for no table."""
        numbers = {int, float, _NONE}
        return all(set(map(type, piece.arrays.get(field, ()))) <= numbers
                   for piece in self.pieces.get(table, ()) for field in fields)

    def check_required(self) -> None:
        for table, fields in REQUIRED.items():
            pieces = self.pieces.get(table)
            if not pieces or not fields:
                continue
            # A part whose table has no array has the rows that the manifest states, all without a value.
            sizes = [piece.rows if piece.arrays else (piece.stated or 0) for piece in pieces]
            for field in fields:
                self.count()
                missing = 0
                first_part = None
                for piece, rows in zip(pieces, sizes):
                    values = piece.arrays.get(field)
                    lacking = rows if values is None else values.count(None) + max(0, rows - len(values))
                    if lacking:
                        missing += lacking
                        first_part = first_part or piece.part
                if missing:
                    self.report("table.required", f"The field is required, and {missing:,} of {sum(sizes):,} row(s) "
                                                  f"have no value.", first_part, table, field)

    def check_ids(self) -> Dict[str, Set[Any]]:
        ids: Dict[str, Set[Any]] = {}
        for table in sorted(ID_TABLES):
            pieces = self.pieces.get(table)
            if not pieces:
                continue
            self.count()
            values = self.column(table, "id")
            invalid = [value for value in values if not (type(value) is int and value > 0)]
            seen: Set[int] = set()
            repeated: List[int] = []
            for value in values:
                if type(value) is int and value > 0:
                    if value in seen:
                        repeated.append(value)
                    seen.add(value)
            ids[table] = seen
            problems = []
            if invalid:
                problems.append(f"{len(invalid):,} id(s) are not integers greater than 0, the first is "
                                f"{_show(invalid[0])}")
            if repeated:
                problems.append(f"{len(repeated):,} id(s) repeat an id of the table, the first is {repeated[0]:,}")
            if problems:
                first_part = pieces[0].part if len(pieces) == 1 else None
                self.report("table.idUnique", "; ".join(problems) + ".", first_part, table, "id")
        return ids

    def check_references(self, ids: Dict[str, Set[Any]]) -> None:
        for table, field, target in REFERENCES:
            pieces = self.pieces.get(table)
            if not pieces or not any(field in piece.arrays for piece in pieces) or target in self.incomplete:
                continue
            if self.rows(target) and not any("id" in piece.arrays for piece in self.pieces[target]):
                continue  # the target has rows and no id: table.required says so
            known = ids.get(target, set())
            self.count()
            dangling = 0
            first: Any = None
            first_part = None
            for piece in pieces:
                for value in piece.arrays.get(field, ()):
                    if value is None:
                        continue
                    for item in (value if type(value) is list else (value,)):
                        if item is None or (_number(item) and item in known):
                            continue
                        dangling += 1
                        if first_part is None:
                            first, first_part = item, piece.part
            if dangling:
                self.report("table.reference", f"{dangling:,} reference(s) point to rows that {target} does not have. "
                                               f"The first is {_show(first)}.", first_part, table, field)

    # ---- section 8: depth and totals ----

    def check_depth_and_totals(self) -> None:
        if not self.usable("tables") or not self.rows("tables"):
            return
        column = self.column
        table_ids = column("tables", "id")
        depths = column("tables", "detailDepth")
        files_ok = self.usable("files") and self.typed("files", "id", "table")
        groups_ok = files_ok and self.usable("rowGroups") and self.typed("rowGroups", "file")
        chunks_ok = (self.usable("physicalSchemas", "physicalColumns", "columnChunks")
                     and self.typed("physicalSchemas", "id", "table")
                     and self.typed("physicalColumns", "id", "physicalSchema", "column")
                     and self.typed("columnChunks", "physicalColumn"))

        file_table: Dict[Any, Any] = {}
        file_count: Dict[Any, int] = collections.Counter()
        file_size: Dict[Any, Any] = collections.Counter()
        size_unknown: Set[Any] = set()
        if files_ok:
            for file_id, table, size in zip(column("files", "id"), column("files", "table"), column("files", "fileSize")):
                table = _key(table)
                if table is None:
                    continue
                file_count[table] += 1
                if _key(file_id) is not None:
                    file_table[file_id] = table
                if _number(size):
                    file_size[table] += size
                else:
                    size_unknown.add(table)
        group_count: Dict[Any, int] = collections.Counter()
        if groups_ok:
            for file_id in column("rowGroups", "file"):
                table = file_table.get(_key(file_id))
                if table is not None:
                    group_count[table] += 1
        chunk_count: Dict[Any, int] = collections.Counter()
        chunk_size: Dict[Any, Any] = collections.Counter()
        column_size: Dict[Any, Any] = collections.Counter()
        size_unknown_tables: Set[Any] = set()
        size_unknown_columns: Set[Any] = set()
        if chunks_ok:
            schema_table = {schema: _key(table) for schema, table in zip(column("physicalSchemas", "id"),
                                                                        column("physicalSchemas", "table"))
                            if _key(schema) is not None}
            physical: Dict[Any, Tuple[Any, Any]] = {}
            for physical_id, schema, column_id in zip(column("physicalColumns", "id"),
                                                      column("physicalColumns", "physicalSchema"),
                                                      column("physicalColumns", "column")):
                if _key(physical_id) is not None:
                    physical[physical_id] = (schema_table.get(_key(schema)), _key(column_id))
            for physical_id, size in zip(column("columnChunks", "physicalColumn"),
                                         column("columnChunks", "compressedSize")):
                table, column_id = physical.get(_key(physical_id), (None, None))
                known = _number(size)
                if table is not None:
                    chunk_count[table] += 1
                    if known:
                        chunk_size[table] += size
                    else:
                        size_unknown_tables.add(table)
                if column_id is not None:
                    if known:
                        column_size[column_id] += size
                    else:
                        size_unknown_columns.add(column_id)

        levels: Dict[Any, int] = {}
        totals = zip(table_ids, depths, column("tables", "fileCount"), column("tables", "fileSize"),
                     column("tables", "rowGroupCount"), column("tables", "compressedSize"))
        for row, (table, depth, files, size, groups, compressed) in enumerate(totals):
            if _key(table) is None or type(depth) is not str:
                continue  # without an id or a detailDepth, table.required or a type says why
            level = DEPTHS.index(depth) if depth in DEPTHS else len(DEPTHS)
            levels[table] = level
            part = self.part_of("tables", row)
            if files_ok:
                if level >= 1:
                    self.count(2)
                    if _differs(files, file_count[table]):
                        self.report("totals.fileCount", f"Table {table} states fileCount {_show(files)}, and files "
                                                        f"holds {file_count[table]:,} of its files.", part, "tables",
                                    "fileCount")
                    if table not in size_unknown and _differs(size, file_size[table]):
                        self.report("totals.fileSize", f"Table {table} states fileSize {_show(size)}, and its files "
                                                       f"sum to {_show(file_size[table])}.", part, "tables", "fileSize")
                else:
                    self.count()
                    if file_count[table]:
                        self.report("depth.files", f"Table {table} is at the Summary depth, and files holds "
                                                   f"{file_count[table]:,} row(s) of it.", None, "files")
            if groups_ok:
                self.count()
                if level >= 2:
                    if _differs(groups, group_count[table]):
                        self.report("totals.rowGroupCount", f"Table {table} states rowGroupCount {_show(groups)}, and "
                                                            f"rowGroups holds {group_count[table]:,} of its row "
                                                            f"groups.", part, "tables", "rowGroupCount")
                elif group_count[table]:
                    self.report("depth.rowGroups", f"Table {table} is at the {depth} depth, and rowGroups holds "
                                                   f"{group_count[table]:,} row(s) of it.", None, "rowGroups")
            if chunks_ok:
                self.count()
                if level >= 3:
                    if table not in size_unknown_tables and _differs(compressed, chunk_size[table]):
                        self.report("totals.compressedSize", f"Table {table} states compressedSize "
                                                             f"{_show(compressed)}, and its column chunks sum to "
                                                             f"{_show(chunk_size[table])}.", part, "tables",
                                    "compressedSize")
                elif chunk_count[table]:
                    self.report("depth.columnChunks", f"Table {table} is at the {depth} depth, and columnChunks holds "
                                                      f"{chunk_count[table]:,} row(s) of it.", None, "columnChunks")

        if not (chunks_ok and self.usable("columns")):
            return
        for row, (column_id, table, size) in enumerate(zip(column("columns", "id"), column("columns", "table"),
                                                           column("columns", "compressedSize"))):
            table = _key(table)
            if (size is None or _key(column_id) is None or table is None or levels.get(table, 0) < 3
                    or not chunk_count[table] or column_id in size_unknown_columns):
                continue
            self.count()
            if _differs(size, column_size[column_id]):
                self.report("totals.columnCompressedSize", f"Column {column_id} states compressedSize {_show(size)}, "
                                                           f"and its column chunks sum to "
                                                           f"{_show(column_size[column_id])}.",
                            self.part_of("columns", row), "columns", "compressedSize")


def validate(path_or_file: Any) -> ValidationResult:
    """Checks a PQAX file against the rules of the serialization, and reports every issue with its code.

    The file is a path, the bytes of the file, or a binary file object. A file that is not a zip archive is reported
    with container.manifestMissing. A path that cannot be opened raises OSError.
    """
    validator = _Validator()
    try:
        archive, fp, owned = open_archive(path_or_file)
    except ContainerError as error:
        validator.count()
        validator.report("container.manifestMissing", f"{error} It has no manifest.json entry.")
        return validator.result()
    try:
        validator.run(archive, fp)
    finally:
        archive.close()
        if owned:
            fp.close()
    return validator.result()


def validate_embedded(path_or_file: Any, part_name: str = EMBEDDED_PART) -> ValidationResult:
    """Checks the PQAX file that a package such as a VPAX holds as a part.

    A package with no part of that name is embedded.partMissing. A file that is not a zip archive is
    container.manifestMissing, as validate() reports it.
    """
    validator = _Validator()
    try:
        data, _, _ = read_embedded(path_or_file, part_name)
    except PartMissingError as error:
        validator.count()
        validator.report("embedded.partMissing", str(error), part_name)
        return validator.result()
    except ContainerError as error:
        validator.count()
        validator.report("container.manifestMissing", f"{error} It has no manifest.json entry.")
        return validator.result()
    return validate(data)
