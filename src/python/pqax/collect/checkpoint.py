"""Delta checkpoints in Parquet, read with pyarrow, the one library that the collector needs: pqax[collect].

The reader returns the add, remove, metaData, protocol, domainMetadata, sidecar, and checkpointMetadata actions of a
classic checkpoint, one part of a multi-part checkpoint, a v2 checkpoint in Parquet, or a sidecar file. It reads the
file whole, through the storage of the collection, in one request for a file under 64 MB. It reads the statistics as
JSON text when the checkpoint has them so, and as numbers from stats_parsed otherwise, keeping the number of records
and the null counts only. Field names are matched in any case, as the C# reader matches them.
"""
from __future__ import annotations

import io
from typing import Any, Dict, List, Optional

from .delta import (
    AddAction, CheckpointMetadataAction, DeletionVector, DomainMetadataAction, MetadataAction, ProtocolAction,
    RemoveAction, SidecarAction,
)
from .storage import read_all

__all__ = ["ArrowCheckpointReader", "require_pyarrow"]

_INT64_MIN, _INT64_MAX = -(1 << 63), (1 << 63) - 1
_ACTIONS = ("add", "remove", "metadata", "protocol", "domainmetadata", "sidecar", "checkpointmetadata")


def require_pyarrow():
    """The pyarrow modules, or ImportError naming the extra that installs them."""
    try:
        import pyarrow  # noqa: F401
        import pyarrow.parquet as parquet
    except ImportError as error:
        raise ImportError("Reading Delta checkpoints needs pyarrow: pip install \"pqax[collect]\"") from error
    return pyarrow, parquet


def _long(value: Any) -> Optional[int]:
    """A value as a 64-bit integer, as Convert.ToInt64 gives it; None when it is not one."""
    if value is None:
        return None
    if isinstance(value, bool):
        return int(value)
    if isinstance(value, int):
        return value if _INT64_MIN <= value <= _INT64_MAX else None
    if isinstance(value, float):
        if value != value or value in (float("inf"), float("-inf")):
            return None
        number = round(value)
        return int(number) if _INT64_MIN <= number <= _INT64_MAX else None
    if isinstance(value, str):
        try:
            number = int(value.strip())
        except ValueError:
            return None
        return number if _INT64_MIN <= number <= _INT64_MAX else None
    return None


def _text(value: Any) -> Optional[str]:
    return value if isinstance(value, str) else None


def _string(value: Any) -> str:
    """A value as text, as ToString() gives it in C#."""
    if isinstance(value, bool):
        return "True" if value else "False"
    if isinstance(value, bytes):
        return "System.Byte[]"
    return str(value)


class _Struct:
    """The children of a struct column, found by name in any case, with the nulls of the struct applied."""

    def __init__(self, array: Any, rows: int) -> None:
        self.rows = rows
        self.children: Dict[str, Any] = {}
        if array is None:
            return
        for index, child in enumerate(array.flatten()):
            self.children.setdefault(array.type.field(index).name.lower(), child)

    def array(self, name: str) -> Any:
        return self.children.get(name.lower())

    def values(self, name: str) -> Optional[List[Any]]:
        """The values of a scalar child, one for each row; None when the struct has no such child."""
        child = self.array(name)
        if child is None:
            return None
        if _is_nested(child.type):
            return None
        return child.to_pylist()

    def struct(self, name: str) -> "_Struct":
        child = self.array(name)
        return _Struct(child if child is not None and _is_struct(child.type) else None, self.rows)

    def lists(self, name: str) -> Optional[List[Optional[List[str]]]]:
        """One list for each row: None when the row has no element. None when there is no such list."""
        child = self.array(name)
        if child is None or not _is_list(child.type):
            return None
        result: List[Optional[List[str]]] = []
        for items in child.to_pylist():
            values = [_string(item) for item in items or () if item is not None]
            result.append(values or None)
        return result

    def maps(self, name: str) -> Optional[List[Optional[Dict[str, Optional[str]]]]]:
        """One map for each row: None when the row has no entry. None when there is no such map."""
        child = self.array(name)
        if child is None or not _is_map(child.type):
            return None
        result: List[Optional[Dict[str, Optional[str]]]] = []
        for entries in child.to_pylist():
            values: Dict[str, Optional[str]] = {}
            for key, value in entries or ():
                values["" if key is None else _string(key)] = None if value is None else _string(value)
            result.append(values or None)
        return result


def _is_struct(kind: Any) -> bool:
    import pyarrow.types as types
    return types.is_struct(kind)


def _is_list(kind: Any) -> bool:
    import pyarrow.types as types
    return types.is_list(kind) or types.is_large_list(kind)


def _is_map(kind: Any) -> bool:
    import pyarrow.types as types
    return types.is_map(kind)


def _is_nested(kind: Any) -> bool:
    import pyarrow.types as types
    return types.is_nested(kind)


def _vectors(action: _Struct) -> List[Optional[DeletionVector]]:
    vector = action.struct("deletionVector")
    kinds = vector.values("storageType")
    if kinds is None:
        return [None] * action.rows
    locations = vector.values("pathOrInlineDv") or [None] * action.rows
    offsets = vector.values("offset") or [None] * action.rows
    sizes = vector.values("sizeInBytes") or [None] * action.rows
    cardinalities = vector.values("cardinality") or [None] * action.rows
    return [DeletionVector(kind, _text(locations[i]), _long(offsets[i]), _long(sizes[i]), _long(cardinalities[i]))
            if isinstance(kind, str) else None for i, kind in enumerate(kinds)]


def _column(values: Optional[List[Any]], rows: int) -> List[Any]:
    return values if values is not None else [None] * rows


class ArrowCheckpointReader:
    """Reads Delta checkpoints in Parquet with pyarrow. A callable (storage, path) -> list of actions."""

    def __init__(self) -> None:
        self._pyarrow, self._parquet = require_pyarrow()

    def __call__(self, storage: Any, path: str) -> List[Any]:
        content = read_all(storage, path)
        parquet_file = self._parquet.ParquetFile(io.BytesIO(content))
        names = {field.name.lower(): field.name for field in parquet_file.schema_arrow}
        columns = [names[name] for name in _ACTIONS if name in names]
        actions: List[Any] = []
        for group in range(parquet_file.num_row_groups):
            table = parquet_file.read_row_group(group, columns=columns)
            rows = table.num_rows
            structs = {}
            for name in _ACTIONS:
                original = names.get(name)
                array = table.column(original).combine_chunks() if original is not None else None
                structs[name] = _Struct(array if array is not None and _is_struct(array.type) else None, rows)
            self._read(structs, rows, actions)
        return actions

    @staticmethod
    def _read(structs: Dict[str, _Struct], rows: int, actions: List[Any]) -> None:
        add = structs["add"]
        paths = add.values("path")
        if paths is not None:
            size = _column(add.values("size"), rows)
            time = _column(add.values("modificationTime"), rows)
            stats = _column(add.values("stats"), rows)
            parsed = add.struct("stats_parsed")
            records = _column(parsed.values("numRecords"), rows)
            clustering = _column(add.values("clusteringProvider"), rows)
            partitions = add.maps("partitionValues")
            tags = add.maps("tags")
            vectors = _vectors(add)
            # The null counts of the top-level columns, when the checkpoint has the statistics as a struct.
            null_struct = parsed.struct("nullCount")
            null_columns = []
            if null_struct.children:
                array = parsed.array("nullCount")
                for index in range(array.type.num_fields):
                    field = array.type.field(index)
                    if not _is_nested(field.type):
                        values = null_struct.values(field.name)
                        if values is not None:
                            null_columns.append((field.name, values))
            for i in range(rows):
                path = paths[i]
                if not isinstance(path, str):
                    continue
                counts: Optional[Dict[str, int]] = None
                for name, values in null_columns:
                    count = _long(values[i])
                    if count is not None:
                        if counts is None:
                            counts = {}
                        counts[name] = count
                partition_values = partitions[i] if partitions is not None else None
                if partition_values is None and partitions is not None:
                    partition_values = {}
                actions.append(AddAction(path=path, partition_values=partition_values, size=_long(size[i]),
                                         modification_time=_long(time[i]), stats=_text(stats[i]),
                                         num_records=_long(records[i]), null_counts=counts,
                                         tags=tags[i] if tags is not None else None, deletion_vector=vectors[i],
                                         clustering_provider=_text(clustering[i])))

        remove = structs["remove"]
        paths = remove.values("path")
        if paths is not None:
            size = _column(remove.values("size"), rows)
            deletion = _column(remove.values("deletionTimestamp"), rows)
            vectors = _vectors(remove)
            for i in range(rows):
                if isinstance(paths[i], str):
                    actions.append(RemoveAction(paths[i], _long(size[i]), _long(deletion[i]), vectors[i]))

        metadata = structs["metadata"]
        ids = metadata.values("id")
        if ids is not None:
            name = _column(metadata.values("name"), rows)
            description = _column(metadata.values("description"), rows)
            provider = _column(metadata.struct("format").values("provider"), rows)
            schema = _column(metadata.values("schemaString"), rows)
            created = _column(metadata.values("createdTime"), rows)
            partition_columns = metadata.lists("partitionColumns")
            configuration = metadata.maps("configuration")
            for i in range(rows):
                if not isinstance(ids[i], str):
                    continue
                columns = partition_columns[i] if partition_columns is not None else None
                config = configuration[i] if configuration is not None else None
                actions.append(MetadataAction(
                    ids[i], _text(name[i]), _text(description[i]), _text(provider[i]), _text(schema[i]),
                    list(columns) if columns is not None else [],
                    {key: value for key, value in (config or {}).items() if value is not None},
                    _long(created[i])))

        protocol = structs["protocol"]
        readers = protocol.values("minReaderVersion")
        if readers is not None:
            writers = _column(protocol.values("minWriterVersion"), rows)
            reader_features = protocol.lists("readerFeatures")
            writer_features = protocol.lists("writerFeatures")
            for i in range(rows):
                version = _long(readers[i])
                if version is not None:
                    actions.append(ProtocolAction(
                        version, _long(writers[i]),
                        reader_features[i] if reader_features is not None else None,
                        writer_features[i] if writer_features is not None else None))

        domain = structs["domainmetadata"]
        domains = domain.values("domain")
        if domains is not None:
            configuration = _column(domain.values("configuration"), rows)
            removed = _column(domain.values("removed"), rows)
            for i in range(rows):
                if isinstance(domains[i], str):
                    actions.append(DomainMetadataAction(domains[i], _text(configuration[i]), removed[i] is True))

        sidecar = structs["sidecar"]
        sidecars = sidecar.values("path")
        if sidecars is not None:
            size = _column(sidecar.values("sizeInBytes"), rows)
            for i in range(rows):
                if isinstance(sidecars[i], str):
                    actions.append(SidecarAction(sidecars[i], _long(size[i])))

        checkpoint = structs["checkpointmetadata"]
        versions = checkpoint.values("version")
        if versions is not None:
            for i in range(rows):
                version = _long(versions[i])
                if version is not None:
                    actions.append(CheckpointMetadataAction(version))
