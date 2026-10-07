"""Reading a PQAX file: the manifest and the summary when the file opens, a detail part when a table needs it."""
from __future__ import annotations

import json
from typing import Any, Dict, Iterator, List, NamedTuple, Optional

from ._container import open_archive, read_embedded, read_entry
from ._errors import FormatError, ManifestMissingError, PartMissingError, UnknownVersionError
from ._json import loads
from ._schema import EMBEDDED_PART, FORMAT, KNOWN_MAJOR_VERSIONS, MANIFEST_PART, SUMMARY_PART, TYPES, major_version

Table = Dict[str, List[Any]]


class _Part(NamedTuple):
    name: str
    tables: Dict[str, Optional[int]]  # the rows that the manifest states for each table, None when not a count


def _show(value: Any) -> str:
    text = json.dumps(value, ensure_ascii=True, default=str)
    return text if len(text) <= 40 else text[:37] + "..."


def _list_parts(manifest: Dict[str, Any]) -> List[_Part]:
    """The parts of the manifest, in order, each once, with what it states for each table."""
    parts: List[_Part] = []
    seen = set()
    items = manifest.get("parts")
    for item in items if isinstance(items, list) else ():
        if not isinstance(item, dict) or not isinstance(item.get("name"), str) or item["name"] in seen:
            continue
        seen.add(item["name"])
        tables = item.get("tables")
        counts: Dict[str, Optional[int]] = {}
        if isinstance(tables, dict):
            for table, count in tables.items():
                counts[table] = count if type(count) is int and count >= 0 else None
        parts.append(_Part(item["name"], counts))
    return parts


def _fit(values: List[Any], length: int) -> List[Any]:
    return values[:length] if len(values) > length else values + [None] * (length - len(values))


def _arrays(table: Dict[str, Any]) -> Table:
    return {field: values for field, values in table.items() if isinstance(values, list)}


def _rows(arrays: Table) -> int:
    return max((len(values) for values in arrays.values()), default=0)


_NONE = type(None)
# The Python types that a value of each type of field can have. A value of another JSON type reads as None.
_ALLOWED = {"integer": {int, float, _NONE}, "string": {str, _NONE}, "timestamp": {str, _NONE},
            "boolean": {bool, _NONE}, "strings": {list, _NONE}, "integers": {list, _NONE}, "map": {dict, _NONE},
            "object": {dict, _NONE}}


def _clean(name: str, arrays: Table) -> Table:
    """Replaces with None each value whose JSON type is not the type of its field, as json.type reports it."""
    kinds = TYPES.get(name)
    if kinds is None:
        return arrays
    for field, values in arrays.items():
        kind = kinds.get(field)
        if kind is not None:
            allowed = _ALLOWED[kind]
            if not set(map(type, values)) <= allowed:
                arrays[field] = [value if type(value) in allowed else None for value in values]
    return arrays


def _concatenate(name: str, pieces: List[Dict[str, Any]]) -> Table:
    """One table from its pieces, in order: the arrays of each piece, with None where a piece lacks a field.

    A piece has the rows of its longest array. In a valid file that is what the manifest states, and a shorter array
    is completed with None. The count of the manifest is not used here, so that a file that states more rows than it
    holds does not make the reader allocate them.
    """
    if len(pieces) == 1:
        arrays = _arrays(pieces[0])
        rows = _rows(arrays)
        for field, values in arrays.items():
            if len(values) != rows:
                arrays[field] = _fit(values, rows)
        return _clean(name, arrays)
    prepared = []
    names: Dict[str, None] = {}
    for table in pieces:
        arrays = _arrays(table)
        prepared.append((arrays, _rows(arrays)))
        names.update(dict.fromkeys(arrays))
    result: Table = {}
    for field in names:
        values: List[Any] = []
        for arrays, rows in prepared:
            piece = arrays.get(field)
            if piece is None:
                values.extend([None] * rows)
            else:
                values.extend(piece if len(piece) == rows else _fit(piece, rows))
        result[field] = values
    return _clean(name, result)


class PqaxDocument:
    """A PQAX file open for reading. Create it with pqax.open or pqax.open_embedded.

    Opening reads the zip directory, manifest.json, and summary.json. A detail part is read the first time a table that
    it holds is asked for, and the table is then kept. Close the document, or use it in a with statement, to release
    the file.
    """

    def __init__(self, archive: Any, fp: Any, owns_file: bool, *, embedded_part: Optional[str] = None,
                 embedded_compression: Optional[str] = None) -> None:
        self._archive = archive
        self._fp = fp
        self._owns_file = owns_file
        self._closed = False
        self._cache: Dict[str, Table] = {}
        #: The name of the part of the package that holds the file, for a document opened with open_embedded.
        self.embedded_part = embedded_part
        #: How that part is compressed in the package: "stored" or "deflated".
        self.embedded_compression = embedded_compression
        self._entries = {info.filename: info for info in archive.infolist()}
        try:
            self._open()
        except BaseException:
            self.close()
            raise

    def _open(self) -> None:
        info = self._entries.get(MANIFEST_PART)
        if info is None:
            hint = ""
            if any(name.lower().endswith(".pqax") for name in self._entries):
                hint = " It has a .pqax part, so it may be a package such as a VPAX: open it with pqax.open_embedded."
            raise ManifestMissingError(f"The archive has no manifest.json entry, so it is not a PQAX file.{hint}")
        manifest = loads(read_entry(self._archive, info), MANIFEST_PART)
        if not isinstance(manifest, dict):
            raise FormatError("The manifest is not a JSON object, so it does not say that the format is pqax.")
        if manifest.get("format") != FORMAT:
            raise FormatError(f"The manifest says that the format is {_show(manifest.get('format'))}, not pqax.")
        version = manifest.get("formatVersion")
        major = major_version(version)
        if major is None or major not in KNOWN_MAJOR_VERSIONS:
            raise UnknownVersionError(f"The format version is {_show(version)}. This package reads the versions 0.x.")
        self._manifest: Dict[str, Any] = manifest
        self._parts = _list_parts(manifest)
        content: Any = {}
        # A summary.json that the manifest lists and the archive lacks gives empty summary tables, as in the other
        # readers. validate() reports it as manifest.partMissing.
        if any(part.name == SUMMARY_PART for part in self._parts) and SUMMARY_PART in self._entries:
            content = self._read_part(SUMMARY_PART)
        self._summary_content: Dict[str, Any] = content if isinstance(content, dict) else {}
        self._summary: Dict[str, Table] = {}
        for name, table in self._summary_content.items():
            if isinstance(table, dict):
                self._summary[name] = _concatenate(name, [table])

    def _read_part(self, name: str) -> Any:
        info = self._entries.get(name)
        if info is None:
            raise PartMissingError(f"The manifest lists the part {name}, and the archive has no entry with that name.")
        if self._closed:
            raise ValueError("The document is closed.")
        return loads(read_entry(self._archive, info), name)

    # ---- what the file says about itself ----

    @property
    def manifest(self) -> Dict[str, Any]:
        """The manifest as parsed: format, formatVersion, collection, parts, and any other member."""
        return self._manifest

    @property
    def format_version(self) -> str:
        """The formatVersion of the manifest."""
        return self._manifest.get("formatVersion")  # type: ignore[return-value]

    @property
    def collection(self) -> Dict[str, Any]:
        """The collection object of the manifest: how the footprint was collected."""
        value = self._manifest.get("collection")
        return value if isinstance(value, dict) else {}

    @property
    def part_names(self) -> List[str]:
        """The names of the parts, in the order of the manifest."""
        return [part.name for part in self._parts]

    @property
    def summary(self) -> Dict[str, Table]:
        """Every table of summary.json: table name, then field name, then one value for each row.

        A table that the file does not have is not in the dictionary, and neither is a field that has no value in any
        row. A member of a table that is not an array is left out: raw() returns it.
        """
        return self._summary

    @property
    def table_names(self) -> List[str]:
        """The tables of the file: those of summary.json, then those of the other parts, in the order of the manifest."""
        names = list(self._summary)
        for part in self._parts:
            if part.name != SUMMARY_PART:
                names.extend(name for name in part.tables if name not in names)
        return names

    def row_count(self, name: str) -> int:
        """The rows of a table, as the manifest states them: the sum over the parts that hold it, 0 when none does."""
        return sum(part.tables.get(name) or 0 for part in self._parts)

    # ---- tables ----

    def table(self, name: str) -> Table:
        """A table with the rows of every part that holds it, in the order of the manifest.

        The result maps each field to its values, one for each row. A field that a part does not have is None in the
        rows of that part. The result is kept, so a second call does not read the parts again. A table that the file
        does not have is an empty dictionary. Treat the lists as read-only: they are shared with later calls.

        The rows of a part are the length of its longest array, which in a valid file is what the manifest states
        and row_count() returns. validate() reports a part whose arrays and manifest disagree.
        """
        cached = self._cache.get(name)
        if cached is not None:
            return cached
        pieces: List[Dict[str, Any]] = []
        for part in self._parts:
            if part.name == SUMMARY_PART:
                if name in self._summary:
                    pieces.append(self._summary_content[name])
                continue
            if name not in part.tables:
                continue
            content = self._read_part(part.name)
            table = content.get(name) if isinstance(content, dict) else None
            pieces.append(table if isinstance(table, dict) else {})
        if len(pieces) == 1 and name in self._summary:
            result = self._summary[name]
        else:
            result = _concatenate(name, pieces) if pieces else {}
        self._cache[name] = result
        return result

    def rows(self, name: str) -> Iterator[Dict[str, Any]]:
        """The rows of a table, one dictionary for each, with every field of the table: None where it has no value."""
        table = self.table(name)
        fields = list(table)
        for values in zip(*table.values()):
            yield dict(zip(fields, values))

    def raw(self, name: str, part: Optional[str] = None) -> Any:
        """A table as the parts hold it, with every member, including those that are not arrays.

        With part, the member of that part, as parsed. Without it, the table as parsed when one part holds it. When
        several parts hold it, their arrays are joined as table() joins them, and every other member is taken from the
        first part that has it. A table that no part holds is an empty dictionary.
        """
        found: List[Any] = []
        for item in self._parts:
            if part is not None and item.name != part:
                continue
            if item.name == SUMMARY_PART:
                value = self._summary_content.get(name)
            elif name in item.tables or part is not None:
                content = self._read_part(item.name)
                value = content.get(name) if isinstance(content, dict) else None
            else:
                continue
            if value is not None:
                found.append(value)
        if not found:
            return {}
        if len(found) == 1 or not all(isinstance(value, dict) for value in found):
            return found[0]
        merged: Dict[str, Any] = dict(_concatenate("", found))  # no table name: values of another type stay
        for value in found:
            for member, content in value.items():
                if not isinstance(content, list) and member not in merged:
                    merged[member] = content
        return merged

    # ---- lifetime ----

    def close(self) -> None:
        """Releases the file. The tables already read stay available."""
        if self._closed:
            return
        self._closed = True
        try:
            self._archive.close()
        finally:
            if self._owns_file:
                self._fp.close()

    @property
    def closed(self) -> bool:
        """Whether the document is closed."""
        return self._closed

    def __enter__(self) -> "PqaxDocument":
        return self

    def __exit__(self, *exc_info: Any) -> None:
        self.close()

    def __repr__(self) -> str:
        version = getattr(self, "_manifest", {}).get("formatVersion")
        where = f", embedded in {self.embedded_part}" if self.embedded_part else ""
        state = ", closed" if self._closed else ""
        return f"<PqaxDocument pqax {version}, {len(getattr(self, '_parts', ()))} parts{where}{state}>"


def open(path_or_file: Any) -> PqaxDocument:  # noqa: A001 - the name of the API
    """Opens a PQAX file: a path, the bytes of the file, or a binary file object.

    It reads the manifest and the summary, and nothing else. It raises a PqaxError when the archive has no
    manifest.json, when the format is not pqax, when the first number of formatVersion is not 0, and when the manifest
    or the summary is not JSON. A file object is left open when the document closes.
    """
    archive, fp, owned = open_archive(path_or_file)
    return PqaxDocument(archive, fp, owned)


def open_embedded(path_or_file: Any, part_name: str = EMBEDDED_PART) -> PqaxDocument:
    """Opens the PQAX file that a package such as a VPAX holds as a part, stored or deflated.

    The part is read into memory, and the package is closed before this function returns. A package with no part of
    that name raises PartMissingError, whose code is embedded.partMissing.
    """
    data, entry_name, compression = read_embedded(path_or_file, part_name)
    archive, fp, owned = open_archive(data)
    return PqaxDocument(archive, fp, owned, embedded_part=entry_name, embedded_compression=compression)
