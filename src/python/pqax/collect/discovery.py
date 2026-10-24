"""The tables of the items of a OneLake workspace, or of a folder with the same layout:
<item>.<Kind>/Tables/<table> or <item>.<Kind>/Tables/<schema>/<table>."""
from __future__ import annotations

import dataclasses
from typing import Dict, List, Optional, Tuple

__all__ = ["SourceKinds", "TableLocation", "discover", "list_items"]


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
    """A table to collect: where it is in the storage, and the source and the names that the footprint records.

    The tables with the same source kind, source name, and source location share one source in the footprint, which
    takes its identifiers from the first of them.
    """

    #: The kind of the source, such as SourceKinds.ONELAKE_LAKEHOUSE.
    source_kind: str
    #: The name of the lakehouse, warehouse, or folder.
    source_name: str
    #: The schema or namespace path of the table, empty for none.
    namespace: Tuple[str, ...]
    #: The table name.
    name: str
    #: The folder of the table in the storage, the one that holds _delta_log, relative to its root.
    path: str
    #: The root location that the footprint records for the table, such as its URL. None to record none.
    location: Optional[str] = None
    #: The base location that the footprint records for the source. None to record none.
    source_location: Optional[str] = None
    #: The identifiers that the platform assigns to the source, sorted by key. None to record none.
    source_identifiers: Optional[Dict[str, str]] = None

    def __post_init__(self) -> None:
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
