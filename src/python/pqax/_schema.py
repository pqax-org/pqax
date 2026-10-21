"""The names of the format: tables, fields, required fields, references, and timestamps.

They follow docs/object-model.md and docs/serialization.md, format version 0.2.
"""
from __future__ import annotations

import re

FORMAT = "pqax"
FORMAT_VERSION = "0.2"
KNOWN_MAJOR_VERSIONS = frozenset({0})
MANIFEST_PART = "manifest.json"
SUMMARY_PART = "summary.json"
EMBEDDED_PART = "Footprint.pqax"
ROWS_PER_PART = 250_000
MAX_INTEGER = 2**53 - 1

# The largest part, in bytes of JSON text, that the reader and the validator inflate. A part of 250,000 column chunks
# takes about 20 MB, so the limit leaves a wide margin and still stops a part that claims gigabytes.
MAX_PART_SIZE = 1 << 30

SUMMARY_TABLES = (
    "sources", "tables", "columns", "partitionSpecs", "partitionFields", "sortOrders", "sortFields",
    "physicalSchemas", "physicalColumns", "writers", "operations", "issues",
    "modelBindings", "tableBindings", "columnBindings",
)
DETAIL_TABLES = ("partitions", "files", "rowGroups", "columnChunks", "deleteFiles", "commits")
TABLES = SUMMARY_TABLES + DETAIL_TABLES

# Every field of every table: the fields of the object in the object model, the reference to the parent, and the
# position of an ordered child. A field that is not here is an extension, and it is kept as it is.
FIELDS = {
    "sources": ("id", "kind", "name", "location", "identifiers"),
    "tables": (
        "id", "source", "namespace", "name", "location", "format", "tableId", "description", "createdAt", "properties",
        "snapshotId", "snapshotTimestamp", "delta", "iceberg", "clusteringColumns", "collectedLevel", "detailDepth",
        "fileCount", "fileSize", "rowCount", "deletedRowCount", "lastModifiedAt", "vOrderedFileCount", "partitionCount",
        "deleteFileCount", "deleteFileSize", "commitCount", "commitsRead", "oldestCommitAt", "nonLiveFileCount",
        "nonLiveFileSize", "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt", "unreferencedFileCount",
        "unreferencedFileSize", "metadataFileCount", "metadataSize", "footerCount", "rowGroupCount", "compressedSize",
        "uncompressedSize", "footerSize", "staleColumnSize", "columnTotalsSource", "fileSizes", "rowGroupRowCounts",
        "partitionSizes",
    ),
    "columns": (
        "id", "table", "fieldId", "name", "dataType", "isNullable", "physicalName", "isPartitionColumn", "description",
        "compressedSize", "uncompressedSize", "valueCount", "nullCount", "chunkCount", "dictionaryChunkCount",
        "fallbackChunkCount", "dictionaryPageSize", "unencodedByteArrayDataBytes", "codecs", "encodings",
        "fileCountWithoutColumn", "largestChunkDistinctCount", "distinctCount", "distinctCountIsEstimate",
        "distinctCountSource",
    ),
    "partitionSpecs": ("id", "table", "specId", "isDefault"),
    "partitionFields": ("partitionSpec", "position", "name", "sourceColumns", "transform"),
    "sortOrders": ("id", "table", "orderId", "isDefault"),
    "sortFields": ("sortOrder", "position", "sourceColumn", "transform", "direction", "nullOrder"),
    "physicalSchemas": ("id", "table", "fileCount"),
    "physicalColumns": (
        "id", "physicalSchema", "ordinal", "path", "physicalType", "typeLength", "logicalType", "repetition", "fieldId",
        "column", "compressedSize", "uncompressedSize", "chunkCount",
    ),
    "writers": ("id", "table", "createdBy", "isVOrdered", "vOrderLevel", "fileCount", "fileSize"),
    "operations": ("table", "name", "commitCount", "lastAt"),
    "issues": ("table", "path", "code", "message"),
    "modelBindings": ("id", "modelName", "serverName"),
    "tableBindings": ("id", "modelBinding", "modelTable", "table"),
    "columnBindings": ("tableBinding", "modelColumn", "column"),
    "partitions": (
        "id", "table", "partitionSpec", "values", "fileCount", "fileSize", "rowCount", "deletedRowCount",
        "lastModifiedAt",
    ),
    "files": (
        "id", "table", "path", "format", "fileSize", "modifiedAt", "rowCount", "deletedRowCount", "deletionVectorSize",
        "partition", "sortOrder", "clusteringProvider", "isVOrdered", "footerSize", "physicalSchema", "writer",
        "rowGroupCount", "compressedSize", "uncompressedSize",
    ),
    "rowGroups": ("id", "file", "ordinal", "rowCount", "uncompressedSize", "compressedSize"),
    "columnChunks": (
        "rowGroup", "physicalColumn", "codec", "encodings", "valueCount", "nullCount", "compressedSize",
        "uncompressedSize", "hasDictionaryPage", "dictionaryPageSize", "dataPageCount", "dictionaryEncodedPageCount",
        "unencodedByteArrayDataBytes", "bloomFilterSize", "minValue", "maxValue", "distinctCount",
        "dictionaryEntryCount",
    ),
    "deleteFiles": (
        "id", "table", "kind", "path", "format", "fileSize", "recordCount", "referencedFile", "equalityColumns",
        "partition",
    ),
    "commits": (
        "table", "commitId", "parentId", "timestamp", "operation", "engine", "isBlindAppend", "metrics", "parameters",
    ),
}

# The tables that have an id, because other tables refer to them.
ID_TABLES = frozenset(name for name, fields in FIELDS.items() if "id" in fields)

# The fields that a row must have: the "Required" line of each object of the object model, the id (section 6
# rule 1), the reference to the parent and the position (serialization 5.3: both required, except the reference in
# issues).
REQUIRED = {
    "sources": ("id", "kind"),
    "tables": ("id", "name", "format", "detailDepth"),
    "columns": ("id", "table", "name", "dataType"),
    "partitionSpecs": ("id", "table"),
    "partitionFields": ("partitionSpec", "position"),
    "sortOrders": ("id", "table"),
    "sortFields": ("sortOrder", "position", "sourceColumn", "direction"),
    "physicalSchemas": ("id", "table"),
    "physicalColumns": ("id", "physicalSchema", "ordinal", "path", "physicalType"),
    "writers": ("id", "table"),
    "operations": ("table", "name"),
    "issues": ("code",),
    "modelBindings": ("id", "modelName"),
    "tableBindings": ("id", "modelBinding", "modelTable", "table"),
    "columnBindings": ("tableBinding", "modelColumn", "column"),
    "partitions": ("id", "table"),
    "files": ("id", "table"),
    "rowGroups": ("id", "file", "ordinal", "rowCount"),
    "columnChunks": ("rowGroup", "physicalColumn", "compressedSize", "uncompressedSize"),
    "deleteFiles": ("id", "table", "kind"),
    "commits": ("table", "commitId"),
}

# The fields of type integer, with every id and every reference. json.integer and json.integerRange look at these
# values and at no other number (conformance/rules.md, 4.1).
INTEGERS = {
    "sources": ("id",),
    "tables": (
        "id", "source", "clusteringColumns", "collectedLevel", "fileCount", "fileSize", "rowCount", "deletedRowCount",
        "vOrderedFileCount", "partitionCount", "deleteFileCount", "deleteFileSize", "commitCount", "commitsRead",
        "nonLiveFileCount", "nonLiveFileSize", "unreferencedFileCount", "unreferencedFileSize", "metadataFileCount",
        "metadataSize", "footerCount", "rowGroupCount", "compressedSize", "uncompressedSize", "footerSize",
        "staleColumnSize",
    ),
    "columns": (
        "id", "table", "fieldId", "compressedSize", "uncompressedSize", "valueCount", "nullCount", "chunkCount",
        "dictionaryChunkCount", "fallbackChunkCount", "dictionaryPageSize", "unencodedByteArrayDataBytes",
        "fileCountWithoutColumn", "largestChunkDistinctCount", "distinctCount",
    ),
    "partitionSpecs": ("id", "table", "specId"),
    "partitionFields": ("partitionSpec", "position", "sourceColumns"),
    "sortOrders": ("id", "table", "orderId"),
    "sortFields": ("sortOrder", "position", "sourceColumn"),
    "physicalSchemas": ("id", "table", "fileCount"),
    "physicalColumns": (
        "id", "physicalSchema", "ordinal", "typeLength", "fieldId", "column", "compressedSize", "uncompressedSize",
        "chunkCount",
    ),
    "writers": ("id", "table", "fileCount", "fileSize"),
    "operations": ("table", "commitCount"),
    "issues": ("table",),
    "modelBindings": ("id",),
    "tableBindings": ("id", "modelBinding", "table"),
    "columnBindings": ("tableBinding", "column"),
    "partitions": ("id", "table", "partitionSpec", "fileCount", "fileSize", "rowCount", "deletedRowCount"),
    "files": (
        "id", "table", "fileSize", "rowCount", "deletedRowCount", "deletionVectorSize", "partition", "sortOrder",
        "footerSize", "physicalSchema", "writer", "rowGroupCount", "compressedSize", "uncompressedSize",
    ),
    "rowGroups": ("id", "file", "ordinal", "rowCount", "uncompressedSize", "compressedSize"),
    "columnChunks": (
        "rowGroup", "physicalColumn", "valueCount", "nullCount", "compressedSize", "uncompressedSize",
        "dictionaryPageSize", "dataPageCount", "dictionaryEncodedPageCount", "unencodedByteArrayDataBytes",
        "bloomFilterSize", "distinctCount", "dictionaryEntryCount",
    ),
    "deleteFiles": ("id", "table", "fileSize", "recordCount", "referencedFile", "equalityColumns", "partition"),
    "commits": ("table",),
}

# The fields of type timestamp.
TIMESTAMPS = {
    "tables": (
        "createdAt", "snapshotTimestamp", "lastModifiedAt", "oldestCommitAt", "nonLiveOldestRemovedAt",
        "nonLiveNewestRemovedAt",
    ),
    "operations": ("lastAt",),
    "partitions": ("lastModifiedAt",),
    "files": ("modifiedAt",),
    "commits": ("timestamp",),
}

# The fields whose value is a list of integers: the lists of references.
INTEGER_LISTS = frozenset({("tables", "clusteringColumns"), ("partitionFields", "sourceColumns"),
                           ("deleteFiles", "equalityColumns")})
BOOLEANS = frozenset({
    ("columns", "isNullable"), ("columns", "isPartitionColumn"), ("columns", "distinctCountIsEstimate"),
    ("partitionSpecs", "isDefault"), ("sortOrders", "isDefault"), ("writers", "isVOrdered"), ("files", "isVOrdered"),
    ("columnChunks", "hasDictionaryPage"), ("commits", "isBlindAppend"),
})
STRING_LISTS = frozenset({("tables", "namespace"), ("columns", "codecs"), ("columns", "encodings"),
                          ("physicalColumns", "path"), ("columnChunks", "encodings"), ("partitions", "values")})
MAPS = frozenset({("sources", "identifiers"), ("tables", "properties"), ("commits", "metrics"),
                  ("commits", "parameters")})
# The only list whose elements can be null: a partition value can be null.
NULLABLE_ELEMENTS = frozenset({("partitions", "values")})

# The objects that a field holds, with the type of each member: a Distribution, DeltaDetails, IcebergDetails.
DISTRIBUTION = ("count", "sum", "minimum", "maximum", "firstQuartile", "median", "thirdQuartile")
_DISTRIBUTION_TYPES = {member: "integer" for member in DISTRIBUTION}
OBJECT_MEMBERS = {
    ("tables", "delta"): {
        "minReaderVersion": "integer", "minWriterVersion": "integer", "readerFeatures": "strings",
        "writerFeatures": "strings", "checkpointVersion": "integer", "commitsAfterCheckpoint": "integer",
        "domains": "strings",
    },
    ("tables", "iceberg"): {member: "integer" for member in (
        "formatVersion", "currentSchemaId", "schemaCount", "snapshotCount", "refCount", "manifestCount",
        "manifestSize", "statisticsFileCount")},
    ("tables", "fileSizes"): _DISTRIBUTION_TYPES,
    ("tables", "rowGroupRowCounts"): _DISTRIBUTION_TYPES,
    ("tables", "partitionSizes"): _DISTRIBUTION_TYPES,
}
NESTED_INTEGERS = {key: tuple(member for member, kind in members.items() if kind == "integer")
                   for key, members in OBJECT_MEMBERS.items()}


def _field_type(table: str, field: str) -> str:
    if field in INTEGERS[table]:
        return "integers" if (table, field) in INTEGER_LISTS else "integer"
    if field in TIMESTAMPS.get(table, ()):
        return "timestamp"
    if (table, field) in BOOLEANS:
        return "boolean"
    if (table, field) in STRING_LISTS:
        return "strings"
    if (table, field) in MAPS:
        return "map"
    if (table, field) in OBJECT_MEMBERS:
        return "object"
    return "string"


# The type of every field of the format: integer, string, timestamp, boolean, strings (a list of strings),
# integers (a list of references), map (an object of strings), or object (one of OBJECT_MEMBERS).
TYPES = {table: {field: _field_type(table, field) for field in fields} for table, fields in FIELDS.items()}

COLLECTION_INTEGERS = ("level", "requestCount", "bytesRead")
COLLECTION_TYPES = {
    "collectorApp": "string", "collectorAppVersion": "string", "collectorLib": "string",
    "collectorLibVersion": "string", "startedAt": "timestamp", "completedAt": "timestamp", "level": "integer",
    "includesDataValues": "boolean", "includesPartitionValues": "boolean", "requestCount": "integer",
    "bytesRead": "integer",
}
OBFUSCATION_MEMBERS = ("obfuscatorDictionaryId", "obfuscatorLib", "obfuscatorLibVersion")

# Every reference: the table that holds it, the field, and the table it points to.
REFERENCES = (
    ("tables", "source", "sources"),
    ("tables", "clusteringColumns", "columns"),
    ("columns", "table", "tables"),
    ("partitionSpecs", "table", "tables"),
    ("partitionFields", "partitionSpec", "partitionSpecs"),
    ("partitionFields", "sourceColumns", "columns"),
    ("sortOrders", "table", "tables"),
    ("sortFields", "sortOrder", "sortOrders"),
    ("sortFields", "sourceColumn", "columns"),
    ("physicalSchemas", "table", "tables"),
    ("physicalColumns", "physicalSchema", "physicalSchemas"),
    ("physicalColumns", "column", "columns"),
    ("writers", "table", "tables"),
    ("operations", "table", "tables"),
    ("issues", "table", "tables"),
    ("tableBindings", "modelBinding", "modelBindings"),
    ("tableBindings", "table", "tables"),
    ("columnBindings", "tableBinding", "tableBindings"),
    ("columnBindings", "column", "columns"),
    ("partitions", "table", "tables"),
    ("partitions", "partitionSpec", "partitionSpecs"),
    ("files", "table", "tables"),
    ("files", "partition", "partitions"),
    ("files", "sortOrder", "sortOrders"),
    ("files", "physicalSchema", "physicalSchemas"),
    ("files", "writer", "writers"),
    ("rowGroups", "file", "files"),
    ("columnChunks", "rowGroup", "rowGroups"),
    ("columnChunks", "physicalColumn", "physicalColumns"),
    ("deleteFiles", "table", "tables"),
    ("deleteFiles", "referencedFile", "files"),
    ("deleteFiles", "equalityColumns", "columns"),
    ("deleteFiles", "partition", "partitions"),
    ("commits", "table", "tables"),
)

COLLECTION_FIELDS = (
    "collectorApp", "collectorAppVersion", "collectorLib", "collectorLibVersion", "startedAt", "completedAt", "level",
    "includesDataValues", "includesPartitionValues", "requestCount", "bytesRead",
)
COLLECTION_TIMESTAMPS = ("startedAt", "completedAt")
MANIFEST_MEMBERS = (
    "format", "formatVersion", "collection", "obfuscatorDictionaryId", "obfuscatorLib", "obfuscatorLibVersion", "parts",
)
MANIFEST_REQUIRED = ("format", "formatVersion", "collection", "parts")

# The values of detailDepth, from the shallowest. A value that is not here is deeper than the last one.
DEPTHS = ("Summary", "Files", "Row groups", "Column chunks")

PART_NAME = re.compile("[A-Za-z0-9._-]+")
FORMAT_VERSION_PATTERN = re.compile("[0-9]+[.][0-9]+")
TIMESTAMP_PATTERN = re.compile("[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}[.][0-9]{3}Z")


def major_version(value: object) -> int | None:
    """The first number of a formatVersion, or None when the value does not start with one."""
    if not isinstance(value, str):
        return None
    head = value.split(".", 1)[0]
    if not head or not head.isascii() or not head.isdigit():
        return None
    return int(head)
