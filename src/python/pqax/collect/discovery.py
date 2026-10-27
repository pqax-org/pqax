"""The tables of the items of a OneLake workspace, or of a folder with the same layout:
<item>.<Kind>/Tables/<table> or <item>.<Kind>/Tables/<schema>/<table>; and the tables under a folder of any other
layout, such as a container of Azure Data Lake Storage Gen2."""
from __future__ import annotations

import dataclasses
import os
from typing import Dict, List, Optional, Tuple

from .storage import LocalStorage, OneLakeStorage

__all__ =["DEFAULT_FOLDER_DEPTH", "SourceKinds", "TableFormats", "TableLocation", "discover", "discover_folder",
           "list_items"]

#: The levels below a folder that discover_folder() tests for a table.
DEFAULT_FOLDER_DEPTH = 4


class TableFormats:
    """The table formats that the collector reads, as the object model spells them."""

    #: A Delta table: a folder with _delta_log.
    DELTA = "Delta"
    #: A folder of Parquet files with no table format.
    PARQUET = "Parquet"


class SourceKinds:
    """The kinds of source that the discovery assigns, as the object model spells them."""

    ONELAKE_LAKEHOUSE = "OneLake lakehouse"
    ONELAKE_WAREHOUSE = "OneLake warehouse"
    ONELAKE_MIRRORED_DATABASE = "OneLake mirrored database"
    ONELAKE_SQL_DATABASE = "OneLake SQL database"
    #: Another OneLake item with a Tables folder, or an item addressed by its id.
    ONELAKE_ITEM = "OneLake item"
    #: A storage folder that holds tables.
    STORAGE_FOLDER = "Storage folder"


_KINDS = (
    (".Lakehouse", SourceKinds.ONELAKE_LAKEHOUSE),
    (".Warehouse", SourceKinds.ONELAKE_WAREHOUSE),
    (".MirroredDatabase", SourceKinds.ONELAKE_MIRRORED_DATABASE),
    (".SQLDatabase", SourceKinds.ONELAKE_SQL_DATABASE),
)


@dataclasses.dataclass(frozen=True)
class TableLocation:
    """A table to collect: where it is in the storage, its format, and the source and the names that the footprint
    records.

    The tables with the same source kind, source name, and source location share one source in the footprint, which
    takes its identifiers from the first of them. A format other than Delta or Parquet raises ValueError.
    """

    #: The kind of the source, such as SourceKinds.ONELAKE_LAKEHOUSE.
    source_kind: str
    #: The name of the lakehouse, warehouse, or folder.
    source_name: str
    #: The schema or namespace path of the table, empty for none.
    namespace: Tuple[str, ...]
    #: The table name.
    name: str
    #: The folder of the table, relative to the root of the storage: the one with _delta_log, or with the Parquet files.
    path: str
    #: The root location that the footprint records for the table, such as its URL. None to record none.
    location: Optional[str] = None
    #: The base location that the footprint records for the source. None to record none.
    source_location: Optional[str] = None
    #: The identifiers that the platform assigns to the source, sorted by key. None to record none.
    source_identifiers: Optional[Dict[str, str]] = None
    #: The format of the table: TableFormats.DELTA or TableFormats.PARQUET.
    format: str = TableFormats.DELTA

    def __post_init__(self) -> None:
        if self.format not in (TableFormats.DELTA, TableFormats.PARQUET):
            raise ValueError("The format is Delta or Parquet.")
        for name in ("source_kind", "source_name", "name", "path"):
            if not isinstance(getattr(self, name), str):
                raise TypeError(f"{name} is a string.")
        object.__setattr__(self, "namespace", tuple(self.namespace or ()))
        object.__setattr__(self, "path", self.path.strip("/"))
        if self.source_identifiers is not None:
            for key, value in self.source_identifiers.items():
                if value is None:
                    raise ValueError("An identifier has no value.")
            object.__setattr__(self, "source_identifiers",
                               dict(sorted(self.source_identifiers.items(), key=lambda item: _ordinal(item[0]))))

    def __hash__(self) -> int:
        return hash((self.source_kind, self.source_name, self.namespace, self.name, self.path))

    def __str__(self) -> str:
        return self.path


def _ordinal(text: str):
    return text.encode("utf-16-be", "surrogatepass")


def _kind_index(item: str) -> int:
    lower = item.lower()
    for index, (suffix, _) in enumerate(_KINDS):
        if len(item) > len(suffix) and lower.endswith(suffix.lower()):
            return index
    return -1


def list_items(storage) -> List[str]:
    """The items at the root of a storage that can hold tables: lakehouses, warehouses, mirrored databases, and SQL
    databases, such as Sales.Lakehouse, in the order of the listing. One request."""
    if storage is None:
        raise TypeError("The storage is missing.")
    return [entry.name for entry in storage.list("") if entry.is_directory and _kind_index(entry.name) >= 0]


def discover(storage, item: str) -> List[TableLocation]:
    """The tables of an item, in the order of their folders. In a warehouse, every folder under Tables is a schema. In
    other items, a folder under Tables is a table when it holds _delta_log, and a schema otherwise. One request for
    Tables and one for each folder under it. Empty when the item has no Tables folder.

    On a storage that knows its locations, as LocalStorage and OneLakeStorage, each table gets the location of its
    folder, and the location and the identifiers of its source.
    """
    if storage is None:
        raise TypeError("The storage is missing.")
    if not item:
        raise ValueError("The item is missing.")
    item = item.strip("/")
    index = _kind_index(item)
    kind = _KINDS[index][1] if index >= 0 else SourceKinds.ONELAKE_ITEM
    name = item[:len(item) - len(_KINDS[index][0])] if index >= 0 else item
    tables_path = item + "/Tables"
    location_of = getattr(storage, "location_of", None)
    identifiers_of = getattr(storage, "identifiers_of", None)
    source_location = location_of(item) if location_of is not None else None
    identifiers = identifiers_of(item) if identifiers_of is not None else None
    tables: List[TableLocation] = []
    for entry in storage.list(tables_path):
        if not entry.is_directory:
            continue
        children = storage.list(entry.path)
        is_table = kind != SourceKinds.ONELAKE_WAREHOUSE and any(
            child.is_directory and child.name == "_delta_log" for child in children)
        if is_table:
            location = location_of(f"{tables_path}/{entry.name}") if location_of is not None else None
            tables.append(TableLocation(kind, name, (), entry.name, entry.path, location, source_location, identifiers))
            continue
        for child in children:
            if child.is_directory:
                location = (location_of(f"{tables_path}/{entry.name}/{child.name}")
                            if location_of is not None else None)
                tables.append(TableLocation(kind, name, (entry.name,), child.name, child.path, location,
                                            source_location, identifiers))
    return tables


def is_parquet_file(entry) -> bool:
    """Whether an entry of a listing is a data file of a Parquet table: a visible file whose name ends with .parquet."""
    return not entry.is_directory and not _is_hidden(entry.name) and entry.name.lower().endswith(".parquet")


def _is_hidden(name: str) -> bool:
    return name.startswith("_") or name.startswith(".")


def _root_name(storage) -> str:
    """The name of the root of a storage: the container or the workspace of a OneLakeStorage, the folder of a
    LocalStorage."""
    if isinstance(storage, OneLakeStorage):
        return storage.workspace
    if isinstance(storage, LocalStorage):
        return os.path.basename(storage.root.rstrip("\\/"))
    return ""


def discover_folder(storage, folder: str, max_depth: int = DEFAULT_FOLDER_DEPTH) -> List[TableLocation]:
    """The tables under a folder of a storage that has no item layout, such as a container of Azure Data Lake Storage
    Gen2, sorted by path. One request for each folder walked. Empty when the folder does not exist.

    The walk starts at the folder, "" for the root, which may itself be a table, and goes down depth first, by ordinal
    name. A folder that holds _delta_log is a Delta table, and the walk does not enter it. A folder with Parquet files
    and no subfolder is a Parquet table. Any other folder is walked while its depth is below max_depth. Folders and
    files whose name starts with _ or . are hidden, as Spark treats them, and a folder whose name holds = is the
    partition of a folder of Parquet files, which this version does not read: none of them is walked. The tables share
    one source of the kind SourceKinds.STORAGE_FOLDER, named after the folder, with the location of the folder and no
    identifiers; the folders between it and a table are the namespace.
    """
    if storage is None:
        raise TypeError("The storage is missing.")
    if not isinstance(folder, str):
        raise TypeError("The folder is a string, \"\" for the root.")
    if isinstance(max_depth, bool) or not isinstance(max_depth, int):
        raise TypeError("max_depth is an integer.")
    if max_depth < 0:
        raise ValueError("The depth is negative.")
    folder = folder.strip("/")
    source_name = folder.rsplit("/", 1)[-1] if folder else _root_name(storage)
    location_of = getattr(storage, "location_of", None)
    source_location = location_of(folder) if location_of is not None else None
    tables: List[TableLocation] = []
    # Depth first, by ordinal name: each folder with the folders between the folder of the walk and it, and the levels
    # that it may still go down.
    pending: List[Tuple[str, Tuple[str, ...], int]] = [(folder, (), max_depth)]
    while pending:
        path, segments, depth_left = pending.pop()
        is_delta = has_parquet = False
        folders = []
        for entry in storage.list(path):
            if entry.is_directory and entry.name == "_delta_log":
                is_delta = True
            elif entry.is_directory and not _is_hidden(entry.name):
                folders.append(entry)
            elif is_parquet_file(entry):
                has_parquet = True
        table_format = (TableFormats.DELTA if is_delta
                        else TableFormats.PARQUET if has_parquet and not folders else None)
        if table_format is not None:
            # The folder given is a table itself: its name is the table name, with no namespace.
            name = segments[-1] if segments else source_name
            location = location_of(path) if location_of is not None else None
            tables.append(TableLocation(SourceKinds.STORAGE_FOLDER, source_name, segments[:-1], name, path, location,
                                        source_location, None, table_format))
            continue
        if depth_left == 0:
            continue
        children = sorted((entry for entry in folders if "=" not in entry.name), key=lambda entry: _ordinal(entry.name))
        for child in reversed(children):
            pending.append((child.path.strip("/"), segments + (child.name,), depth_left - 1))
    return tables
