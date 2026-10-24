"""Read, validate, and write PQAX files: the footprint of Parquet tables, as a zip of JSON tables.

    import pqax

    with pqax.open("footprint.pqax") as doc:
        tables = doc.summary["tables"]                  # every summary table is read when the file opens
        chunks = doc.table("columnChunks")              # a detail table is read when it is asked for
        print(sum(chunks.get("compressedSize", [])))

    result = pqax.validate("footprint.pqax")            # every issue, with the codes of the conformance kit
    print(result.is_valid, [issue.code for issue in result.issues])

    with pqax.open("footprint.pqax") as doc:
        tables = pqax.measures.measure(doc)["tables"]   # the waste and the other measures of the storage views

    builder = pqax.collect.collect(pqax.collect.LocalStorage("workspace"))   # the footprint of the Delta tables
    builder.write("footprint.pqax")

The package needs only the standard library. to_dataframe uses pandas or polars when one is installed, the export
to Parquet and Delta needs pyarrow and deltalake, and the collector reads Delta checkpoints with pyarrow.
"""
from __future__ import annotations

__version__ = "0.2.2"

from ._errors import (
    ContainerError, FormatError, JsonError, ManifestMissingError, PartMissingError, PqaxError, UnknownVersionError,
)
from ._json import format_timestamp, parse_timestamp
from ._schema import DETAIL_TABLES, FIELDS, FORMAT_VERSION, REFERENCES, REQUIRED, ROWS_PER_PART, SUMMARY_TABLES, TABLES
from . import collect, export, measures
from .builder import Builder, BuilderOptions
from .dataframe import to_dataframe
from .reader import PqaxDocument, open, open_embedded
from .validator import Issue, ValidationResult, validate, validate_embedded

__all__ = [
    "Builder", "BuilderOptions", "ContainerError", "DETAIL_TABLES", "FIELDS", "FORMAT_VERSION", "FormatError",
    "Issue", "JsonError", "ManifestMissingError", "PartMissingError", "PqaxDocument", "PqaxError", "REFERENCES",
    "REQUIRED", "ROWS_PER_PART", "SUMMARY_TABLES", "TABLES", "UnknownVersionError", "ValidationResult",
    "format_timestamp", "measures", "open", "open_embedded", "parse_timestamp", "to_dataframe", "validate",
    "validate_embedded",
]
