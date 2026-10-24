"""The collector: the footprint of the Delta tables of a lakehouse, from their logs and the footers of their Parquet
files, without reading a row. A port of the C# collector, Pqax.Collector, with the same options, issues, and output.

    import pqax.collect

    storage = pqax.collect.LocalStorage.mount("/lakehouse/default", "Sales.Lakehouse")    # in a Fabric notebook
    builder = pqax.collect.collect(storage)
    builder.collection.update(collectorApp="my notebook", collectorAppVersion="1")
    builder.write("/lakehouse/default/Files/footprints/sales.pqax")

The parser of the footers and the Delta log reader need only the standard library. The Delta checkpoints in Parquet
need pyarrow, the extra pqax[collect]; OneLake is read with a token callable, and the identities of the Azure SDK need
azure-identity, the extra pqax[azure]. The command line is python -m pqax collect.
"""
from __future__ import annotations

from ._issues import ISSUE_CODES, Issue, IssueCodes
from .checkpoint import ArrowCheckpointReader
from .collector import LIBRARY_NAME, CollectionProgress, CollectorOptions, DetailDepth, collect
from .delta import (
    AddAction, CheckpointMetadataAction, CommitInfoAction, DeletionVector, DomainMetadataAction, MetadataAction,
    ProtocolAction, RemoveAction, SidecarAction,
)
from .discovery import SourceKinds, TableLocation, discover, list_items
from .footer import (
    DEFAULT_TAIL_BYTES, MAX_FOOTER_LENGTH, FooterError, FooterReader, FooterTailPlanner, ParquetColumnChunk,
    ParquetFooter, ParquetKeyValue, ParquetLeafColumn, ParquetPageEncodingStats, ParquetRowGroup,
    ParquetSchemaElement, ParquetSortingColumn, parse_footer,
)
from .storage import DEFAULT_ENDPOINT, SCOPE, LocalStorage, OneLakeStorage, StorageEntry, StorageError
from . import identity

__all__ = [
    "AddAction", "ArrowCheckpointReader", "CheckpointMetadataAction", "CollectionProgress", "CollectorOptions",
    "CommitInfoAction", "DEFAULT_ENDPOINT", "DEFAULT_TAIL_BYTES", "DeletionVector", "DetailDepth",
    "DomainMetadataAction", "FooterError", "FooterReader", "FooterTailPlanner", "ISSUE_CODES", "Issue", "IssueCodes",
    "LIBRARY_NAME", "LocalStorage", "MAX_FOOTER_LENGTH", "MetadataAction", "OneLakeStorage", "ParquetColumnChunk",
    "ParquetFooter", "ParquetKeyValue", "ParquetLeafColumn", "ParquetPageEncodingStats", "ParquetRowGroup",
    "ParquetSchemaElement", "ParquetSortingColumn", "ProtocolAction", "RemoveAction", "SCOPE", "SidecarAction",
    "SourceKinds", "StorageEntry", "StorageError", "TableLocation", "collect", "discover", "identity", "list_items",
    "parse_footer",
]
