"""Writing a PQAX file: rows go into tables, and write() lays the tables out in parts, as the serialization says."""
from __future__ import annotations

import copy
import dataclasses
import datetime
import os
import zipfile
from typing import Any, Dict, List, Mapping, Optional, Sequence, Set, Tuple

from ._errors import PqaxError
from ._json import dumps, format_timestamp
from ._schema import (
    COLLECTION_TIMESTAMPS, DETAIL_TABLES, FIELDS, FORMAT, FORMAT_VERSION, ID_TABLES, MANIFEST_MEMBERS, MANIFEST_PART,
    PART_NAME, ROWS_PER_PART, SUMMARY_PART, SUMMARY_TABLES, TABLES, TIMESTAMPS,
)

_FIELD_SETS = {table: frozenset(fields) for table, fields in FIELDS.items()}
_ENTRY_TIME = (1980, 1, 1, 0, 0, 0)  # a fixed time, so that the same footprint gives the same bytes


def _value(value: Any) -> Any:
    if isinstance(value, datetime.datetime):
        return format_timestamp(value)
    if isinstance(value, tuple):
        return list(value)
    return value


def _write_entry(archive: zipfile.ZipFile, name: str, value: Any, level: int) -> None:
    """Writes one part: compact JSON in UTF-8, deflated, with a fixed time."""
    try:
        data = dumps(value)
    except (TypeError, ValueError) as error:
        raise type(error)(f"A value in {name} cannot be written as JSON: {error}") from error
    info = zipfile.ZipInfo(name, date_time=_ENTRY_TIME)
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3
    info.external_attr = 0o100644 << 16
    archive.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=level)


@dataclasses.dataclass
class BuilderOptions:
    """How a Builder writes a file."""

    #: The most rows of a detail part, at most 250,000. A table with more rows continues in more parts.
    rows_per_part: int = ROWS_PER_PART
    #: The deflate level of the entries, from 0 to 9.
    compression_level: int = 9


class _Columns:
    """The rows of one table, stored as one list for each field."""

    __slots__ = ("fields", "rows", "max_id", "positions")

    def __init__(self) -> None:
        self.fields: Dict[str, List[Any]] = {}
        self.rows = 0
        self.max_id = 0
        self.positions: Dict[int, int] = {}  # the position of the row of each id

    def append(self, row: Dict[str, Any]) -> int:
        position = self.rows
        fields = self.fields
        for name, values in fields.items():
            values.append(row.get(name))
        for name, value in row.items():
            if name not in fields:
                fields[name] = [None] * position + [value]
        self.rows = position + 1
        return position


class Builder:
    """Builds a footprint row by row, and writes it as a PQAX file.

    Each table has an add method that takes the fields of one row as keyword arguments, named as in the file:
    ``builder.add_file(table=1, path="part-0001.parquet", fileSize=47820)``. For a table with an id, the method keeps
    a positive id that the row brings, assigns the next free id when the row has none (greater than every id so far
    in the table, so from 1), and returns the id; write() refuses a table with a repeated id. For a table without an
    id, the method returns the position of the row, from 0. A field with the value None is left out of the row. A
    datetime becomes a timestamp string in UTC; a naive datetime is taken as UTC.

    The add methods accept the fields of the object model and the fields of a producer, whose names have a prefix and
    an underscore, as in ``acme_note``. Builder.add accepts any table and any field.
    """

    def __init__(self, collection: Mapping[str, Any], *, rows_per_part: int = ROWS_PER_PART,
                 compression_level: int = 9) -> None:
        if not isinstance(collection, Mapping):
            raise TypeError("collection is a mapping with the fields of the Collection object, such as {'level': 2}")
        #: The collection object of the manifest. The object model requires its level.
        self.collection: Dict[str, Any] = dict(collection)
        self.options = BuilderOptions(rows_per_part, compression_level)
        #: The obfuscation fields of the manifest, set when names were obfuscated.
        self.obfuscator_dictionary_id: Optional[str] = None
        self.obfuscator_lib: Optional[str] = None
        self.obfuscator_lib_version: Optional[str] = None
        #: Members that a producer adds to the manifest, written after the members of the format.
        self.manifest_extensions: Dict[str, Any] = {}
        self._tables: Dict[str, _Columns] = {}
        self._detail: Dict[str, bool] = {}  # where a table that this version does not define goes

    # ---- options ----

    @property
    def rows_per_part(self) -> int:
        """The most rows of a detail part."""
        return self.options.rows_per_part

    @rows_per_part.setter
    def rows_per_part(self, value: int) -> None:
        self.options.rows_per_part = value

    @property
    def compression_level(self) -> int:
        """The deflate level of the entries."""
        return self.options.compression_level

    @compression_level.setter
    def compression_level(self, value: int) -> None:
        self.options.compression_level = value

    # ---- rows ----

    def add(self, table: str, /, **fields: Any) -> int:
        """Adds a row to any table, also one that this version of the format does not define, with any field.

        A table that the format does not define goes into summary.json, unless extend() placed it in detail parts.
        """
        return self._add(table, fields, None)

    def _add(self, table: str, fields: Dict[str, Any], method: Optional[str]) -> int:
        if not isinstance(table, str) or not table:
            raise ValueError("The name of a table is a non-empty string.")
        if method is not None:
            known = _FIELD_SETS[table]
            unknown = [name for name in fields if name not in known and "_" not in name]
            if unknown:
                raise TypeError(f"{method}() got fields that {table} does not have: {', '.join(unknown)}. A field that "
                                f"a producer adds has a prefix and an underscore, as in acme_note.")
        row = {name: _value(value) for name, value in fields.items() if value is not None}
        columns = self._tables.get(table)
        if columns is None:
            columns = self._tables[table] = _Columns()
        if table in ID_TABLES or "id" in columns.fields or "id" in row:
            identifier = row.pop("id", None)
            if identifier is None:
                identifier = columns.max_id + 1  # the next free id: greater than every id so far in the table
            elif type(identifier) is not int or identifier <= 0:
                raise ValueError(f"The id of a row of {table} is a positive integer, not {identifier!r}.")
            position = columns.append({"id": identifier, **row})
            columns.positions.setdefault(identifier, position)  # a repeated id is kept here, and write() refuses it
            columns.max_id = max(columns.max_id, identifier)
            return identifier
        return columns.append(row)

    def update(self, table: str, key: int, /, **fields: Any) -> None:
        """Sets fields of a row already added: the row with that id in a table with ids, the row at that position,
        from 0, in a table without. A field set to None loses its value. The id itself does not change.

        It serves the totals that are known only after the detail, as in
        ``builder.update("tables", table_id, fileCount=3, fileSize=159121)``.
        """
        columns = self._tables.get(table)
        if columns is None:
            raise KeyError(f"{table} has no rows.")
        if "id" in fields:
            raise ValueError("update does not change the id of a row.")
        if table in ID_TABLES or "id" in columns.fields:
            if key not in columns.positions:
                raise KeyError(f"{table} has no row with id {key!r}.")
            position = columns.positions[key]
        elif type(key) is int and 0 <= key < columns.rows:
            position = key
        else:
            raise IndexError(f"{table} has no row at position {key!r}.")
        for name, value in fields.items():
            value = _value(value)
            values = columns.fields.get(name)
            if values is None:
                if value is None:
                    continue
                values = columns.fields[name] = [None] * columns.rows
            values[position] = value

    def extend(self, table: str, columns: Mapping[str, Sequence[Any]], /, *, detail: Optional[bool] = None) -> None:
        """Adds many rows at once, from a columnar table: each field with a list of values, all of the same length.

        In a table of the format with ids, a row keeps a positive id that it brings, and a row without one gets the
        next free id. An id that is not a positive integer raises ValueError. detail places a table that the format
        does not define: in detail parts when True, in summary.json when False. The format places its own tables.
        """
        if not isinstance(table, str) or not table:
            raise ValueError("The name of a table is a non-empty string.")
        if detail is not None:
            if (table in SUMMARY_TABLES and detail) or (table in DETAIL_TABLES and not detail):
                place = "detail parts" if table in DETAIL_TABLES else SUMMARY_PART
                raise ValueError(f"The format places {table} in {place}.")
            if table not in TABLES:
                self._detail[table] = bool(detail)
        lists = {name: [_value(value) for value in values] if name in TIMESTAMPS.get(table, ()) else list(values)
                 for name, values in columns.items()}
        lengths = {len(values) for values in lists.values()}
        if len(lengths) > 1:
            raise ValueError(f"The lists of {table} have different lengths: {sorted(lengths)}.")
        count = lengths.pop() if lengths else 0
        target = self._tables.get(table)
        if target is None:
            target = self._tables[table] = _Columns()
        if count == 0:
            return
        if table in ID_TABLES and "id" not in lists:
            start = target.max_id + 1
            lists = {"id": list(range(start, start + count)), **lists}
        elif table in ID_TABLES:
            given = lists["id"]
            for value in given:
                if value is not None and (type(value) is not int or value <= 0):
                    raise ValueError(f"The id of a row of {table} is a positive integer, not {value!r}.")
            if None in given:
                # A row without an id gets the next free id, greater than every id of the table and of these rows.
                following = max([target.max_id] + [value for value in given if value is not None]) + 1
                filled = []
                for value in given:
                    if value is None:
                        value, following = following, following + 1
                    filled.append(value)
                lists["id"] = filled
        existing = target.rows
        if "id" in lists:
            for offset, value in enumerate(lists["id"]):
                if type(value) is int:
                    target.positions.setdefault(value, existing + offset)
                    if value > target.max_id:
                        target.max_id = value
        for name, values in target.fields.items():
            values.extend(lists[name] if name in lists else [None] * count)
        for name, values in lists.items():
            if name not in target.fields:
                target.fields[name] = [None] * existing + values
        target.rows = existing + count

    def row_count(self, table: str) -> int:
        """The rows of a table so far."""
        columns = self._tables.get(table)
        return columns.rows if columns else 0

    def table(self, table: str) -> Dict[str, List[Any]]:
        """A copy of a table so far: each field with its values, None where a row has no value."""
        columns = self._tables.get(table)
        return {name: list(values) for name, values in columns.fields.items()} if columns else {}

    # ---- one method for each table of the format ----

    def add_source(self, **fields: Any) -> int:
        """Adds a row to sources and returns its id. Fields: id, kind, name, location, identifiers."""
        return self._add("sources", fields, "add_source")

    def add_table(self, **fields: Any) -> int:
        """Adds a row to tables and returns its id. Required: name, format, detailDepth. See the object model, 6.4."""
        return self._add("tables", fields, "add_table")

    def add_column(self, **fields: Any) -> int:
        """Adds a row to columns and returns its id. Required: table, name, dataType. See the object model, 6.5."""
        return self._add("columns", fields, "add_column")

    def add_partition_spec(self, **fields: Any) -> int:
        """Adds a row to partitionSpecs and returns its id. Fields: id, table, specId, isDefault."""
        return self._add("partitionSpecs", fields, "add_partition_spec")

    def add_partition_field(self, **fields: Any) -> int:
        """Adds a row to partitionFields and returns its position. Fields: partitionSpec, position, name,
        sourceColumns, transform."""
        return self._add("partitionFields", fields, "add_partition_field")

    def add_sort_order(self, **fields: Any) -> int:
        """Adds a row to sortOrders and returns its id. Fields: id, table, orderId, isDefault."""
        return self._add("sortOrders", fields, "add_sort_order")

    def add_sort_field(self, **fields: Any) -> int:
        """Adds a row to sortFields and returns its position. Fields: sortOrder, position, sourceColumn, transform,
        direction, nullOrder. Required: sortOrder, position, sourceColumn, direction."""
        return self._add("sortFields", fields, "add_sort_field")

    def add_physical_schema(self, **fields: Any) -> int:
        """Adds a row to physicalSchemas and returns its id. Fields: id, table, fileCount."""
        return self._add("physicalSchemas", fields, "add_physical_schema")

    def add_physical_column(self, **fields: Any) -> int:
        """Adds a row to physicalColumns and returns its id. Required: physicalSchema, ordinal, path, physicalType."""
        return self._add("physicalColumns", fields, "add_physical_column")

    def add_writer(self, **fields: Any) -> int:
        """Adds a row to writers and returns its id. Fields: id, table, createdBy, isVOrdered, vOrderLevel, fileCount,
        fileSize."""
        return self._add("writers", fields, "add_writer")

    def add_operation(self, **fields: Any) -> int:
        """Adds a row to operations and returns its position. Fields: table, name, commitCount, lastAt."""
        return self._add("operations", fields, "add_operation")

    def add_issue(self, **fields: Any) -> int:
        """Adds a row to issues and returns its position. Fields: table, path, code, message."""
        return self._add("issues", fields, "add_issue")

    def add_model_binding(self, **fields: Any) -> int:
        """Adds a row to modelBindings and returns its id. Fields: id, modelName, serverName."""
        return self._add("modelBindings", fields, "add_model_binding")

    def add_table_binding(self, **fields: Any) -> int:
        """Adds a row to tableBindings and returns its id. Fields: id, modelBinding, modelTable, table."""
        return self._add("tableBindings", fields, "add_table_binding")

    def add_column_binding(self, **fields: Any) -> int:
        """Adds a row to columnBindings and returns its position. Fields: tableBinding, modelColumn, column."""
        return self._add("columnBindings", fields, "add_column_binding")

    def add_partition(self, **fields: Any) -> int:
        """Adds a row to partitions and returns its id. Fields: id, table, partitionSpec, values, fileCount, fileSize,
        rowCount, deletedRowCount."""
        return self._add("partitions", fields, "add_partition")

    def add_file(self, **fields: Any) -> int:
        """Adds a row to files and returns its id. Required: table. See the object model, 6.11."""
        return self._add("files", fields, "add_file")

    def add_row_group(self, **fields: Any) -> int:
        """Adds a row to rowGroups and returns its id. Required: file, ordinal, rowCount. See the object model, 6.12."""
        return self._add("rowGroups", fields, "add_row_group")

    def add_column_chunk(self, **fields: Any) -> int:
        """Adds a row to columnChunks and returns its position. Required: rowGroup, physicalColumn, compressedSize,
        uncompressedSize. See the object model, 6.13."""
        return self._add("columnChunks", fields, "add_column_chunk")

    def add_delete_file(self, **fields: Any) -> int:
        """Adds a row to deleteFiles and returns its id. Required: table, kind. See the object model, 6.14."""
        return self._add("deleteFiles", fields, "add_delete_file")

    def add_commit(self, **fields: Any) -> int:
        """Adds a row to commits and returns its position. Required: table, commitId. See the object model, 6.15."""
        return self._add("commits", fields, "add_commit")

    # The same methods with the name of the table, which is plural.
    add_sources = add_source
    add_tables = add_table
    add_columns = add_column
    add_partition_specs = add_partition_spec
    add_partition_fields = add_partition_field
    add_sort_orders = add_sort_order
    add_sort_fields = add_sort_field
    add_physical_schemas = add_physical_schema
    add_physical_columns = add_physical_column
    add_writers = add_writer
    add_operations = add_operation
    add_issues = add_issue
    add_model_bindings = add_model_binding
    add_table_bindings = add_table_binding
    add_column_bindings = add_column_binding
    add_partitions = add_partition
    add_files = add_file
    add_row_groups = add_row_group
    add_column_chunks = add_column_chunk
    add_delete_files = add_delete_file
    add_commits = add_commit

    # ---- writing ----

    def _names(self, detail: bool) -> List[str]:
        own = DETAIL_TABLES if detail else SUMMARY_TABLES
        names = [name for name in own if name in self._tables]
        names.extend(name for name in self._tables if name not in TABLES and self._detail.get(name, False) == detail)
        return names

    def _plan(self) -> List[Tuple[str, List[Tuple[str, int, int]]]]:
        """The parts: each with its name and, for each table, the rows from start to end."""
        per = self.options.rows_per_part
        if type(per) is not int or not 1 <= per <= ROWS_PER_PART:
            raise ValueError(f"rows_per_part is an integer from 1 to {ROWS_PER_PART:,}, the most rows that the format "
                             f"allows in a detail part.")
        for name in sorted(ID_TABLES & self._tables.keys()):
            ids = self._tables[name].fields.get("id", ())
            if len(set(ids)) == len(ids):
                continue
            seen: Set[Any] = set()
            for value in ids:
                if value in seen:
                    raise ValueError(f"The table {name} has the id {value!r} in more than one row. An id is unique "
                                     f"in its table.")
                seen.add(value)
        plan = [(SUMMARY_PART, [(name, 0, self._tables[name].rows) for name in self._names(False)
                                if self._tables[name].rows])]
        for name in self._names(True):
            rows = self._tables[name].rows
            if not rows:
                continue
            if not PART_NAME.fullmatch(name):
                raise ValueError(f"The table {name!r} cannot name a part: a part name has ASCII letters, digits, "
                                 f"'.', '-', and '_'.")
            if rows <= per:
                plan.append((f"{name}.json", [(name, 0, rows)]))
            else:
                for number, start in enumerate(range(0, rows, per), 1):
                    plan.append((f"{name}.{number}.json", [(name, start, min(rows, start + per))]))
        seen: Set[str] = {MANIFEST_PART}
        for part, _ in plan:
            if part.lower() in seen:
                raise ValueError(f"Two parts would have the name {part}, or names that differ only in case.")
            seen.add(part.lower())
        return plan

    def _manifest(self, plan: List[Tuple[str, List[Tuple[str, int, int]]]]) -> Dict[str, Any]:
        collection = {name: _value(value) if name in COLLECTION_TIMESTAMPS else value
                      for name, value in self.collection.items()}
        manifest: Dict[str, Any] = {"format": FORMAT, "formatVersion": FORMAT_VERSION, "collection": collection}
        for member, value in (("obfuscatorDictionaryId", self.obfuscator_dictionary_id),
                              ("obfuscatorLib", self.obfuscator_lib),
                              ("obfuscatorLibVersion", self.obfuscator_lib_version)):
            if value is not None:
                manifest[member] = value
        manifest["parts"] = [{"name": part, "tables": {name: end - start for name, start, end in content}}
                             for part, content in plan]
        for member, value in self.manifest_extensions.items():
            if member in MANIFEST_MEMBERS:
                raise ValueError(f"{member} is a member that the format defines, not an extension of the manifest.")
            manifest[member] = value
        return manifest

    def _content(self, content: List[Tuple[str, int, int]]) -> Dict[str, Any]:
        """The JSON object of a part: for each table, the fields that have a value in some row of the part."""
        result: Dict[str, Any] = {}
        for name, start, end in content:
            table: Dict[str, List[Any]] = {}
            for field, values in self._tables[name].fields.items():
                piece = values if start == 0 and end == len(values) else values[start:end]
                if any(value is not None for value in piece):
                    table[field] = piece
            result[name] = table
        return result

    def _write_to(self, target: Any, plan: List[Tuple[str, List[Tuple[str, int, int]]]],
                  manifest: Dict[str, Any]) -> None:
        level = self.options.compression_level
        with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, allowZip64=False, compresslevel=level) as archive:
            _write_entry(archive, MANIFEST_PART, manifest, level)
            for part, content in plan:
                _write_entry(archive, part, self._content(content), level)

    def write(self, path_or_file: Any) -> None:
        """Writes the file: manifest.json first, then summary.json, then the detail parts.

        A detail part holds one table and at most rows_per_part rows. It is named after its table, as in files.json,
        and numbered when the table needs several, as in columnChunks.1.json. A field with no value in any row of a
        part is left out of that part, and so is a table with no rows. summary.json is always written. A path is
        written through a temporary file, so that a failure leaves no partial file.
        """
        plan = self._plan()
        manifest = self._manifest(plan)
        try:
            if isinstance(path_or_file, (str, os.PathLike)):
                path = os.fspath(path_or_file)
                temporary = f"{path}.{os.getpid()}.tmp"
                try:
                    self._write_to(temporary, plan, manifest)
                    os.replace(temporary, path)
                finally:
                    if os.path.exists(temporary):
                        os.remove(temporary)
            else:
                self._write_to(path_or_file, plan, manifest)
        except zipfile.LargeZipFile as error:
            raise PqaxError(f"The file would need ZIP64, which the format does not allow: {error}",
                            code="container.zip64") from error

    # ---- rewriting a file ----

    @classmethod
    def from_document(cls, document: Any, *, rows_per_part: int = ROWS_PER_PART,
                      compression_level: int = 9) -> "Builder":
        """A builder with every table of an open document, the fields that this version does not know included.

        The ids stay as they are, so the references still hold. The collection, the obfuscation fields, and the other
        members of the manifest are copied. What the reader does not keep is lost: entries that the manifest does not
        list, members of a table that are not arrays, and members of a part of the manifest other than name and tables.
        """
        manifest = document.manifest
        collection = manifest.get("collection")
        builder = cls(copy.deepcopy(collection) if isinstance(collection, dict) else {},
                      rows_per_part=rows_per_part, compression_level=compression_level)
        builder.obfuscator_dictionary_id = manifest.get("obfuscatorDictionaryId")
        builder.obfuscator_lib = manifest.get("obfuscatorLib")
        builder.obfuscator_lib_version = manifest.get("obfuscatorLibVersion")
        builder.manifest_extensions = {member: copy.deepcopy(value) for member, value in manifest.items()
                                       if member not in MANIFEST_MEMBERS}
        for name in document.table_names:
            detail = None if name in TABLES else name not in document.summary
            builder.extend(name, document.table(name), detail=detail)
        return builder

    def __repr__(self) -> str:
        rows = ", ".join(f"{name} {columns.rows}" for name, columns in self._tables.items() if columns.rows)
        return f"<Builder {rows or 'empty'}>"
