"""Rebuilds the two footprints of the comparison test of the PQAX conformance kit, and the diff expected of them.

Usage, from the root of the repository:

    python conformance/make_diff.py [--check] [--no-build]

Output, which replaces these files:

    samples/diff-before.pqax, samples/diff-after.pqax     two footprints of one synthetic lakehouse, a week apart
    expected/diff-before.json, expected/diff-after.json    what a reader must get from each, as for every valid sample
    expected/diff/diff-before.diff-after.json             what pqax diff diff-before.pqax diff-after.pqax --json writes

The footprints are written with pqax.Builder, the writer of the Python package. Between the two, the lakehouse
changed in known ways, listed in SCENARIO below: tables added, removed, renamed with the same TableId, and dropped and
created again with a new one; columns added, removed, renamed with the same FieldId, dropped and added again with a
new FieldId, and changed in type; files added and removed, a deletion vector that grew, an OPTIMIZE and a VACUUM; a
table with no table format, matched by its name; and a table kept at a lower depth in the newer footprint.

The expected diff does not come from an implementation of the diff. The pairs of tables and columns, the tables
recreated, and the counts of the files, partitions, and commits added, removed, and kept are declared in SCENARIO, and
the script checks them against the data it writes. The totals come from the rows it writes. The measures come from the
storage views, through make_measures.py and node, as expected/measures does. The order and the shape follow section 13
of the object model and the README of the kit.

--check writes nothing and exits with 1 when a file differs from what the script would write. --no-build uses the
dist/ folders of the TypeScript packages as they are. make_kit.py writes the two samples and their expected results
too, so that a rebuild of the kit keeps them; it does not write the expected diff, which needs node.

Exit code 0 when the files are written or up to date, 1 when --check finds a difference, 2 when node or the packages
are missing. Standard library and src/python only.
"""
from __future__ import annotations

import argparse
import datetime
import io
import json
import math
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

KIT = Path(__file__).resolve().parent
ROOT = KIT.parent
SAMPLES = KIT / "samples"
EXPECTED = KIT / "expected"
DIFF = EXPECTED / "diff"
sys.dont_write_bytecode = True
sys.path.insert(0, str(ROOT / "src" / "python"))
sys.path.insert(0, str(KIT))
import pqax  # noqa: E402
import make_measures  # noqa: E402

BEFORE, AFTER = "diff-before.pqax", "diff-after.pqax"
EXPECTED_DIFF = "diff-before.diff-after.json"
SMALL_FILE_SIZE, TINY_FILE_SIZE = 134217728, 8388608
COLLECTOR = {"collectorApp": "pqax-conformance-kit", "collectorAppVersion": "0.2", "level": 2,
             "includesDataValues": False, "includesPartitionValues": True}
SOURCE = {"kind": "OneLake lakehouse", "name": "Diff_Lakehouse"}
COMMITS_KEPT = 8          # the collector keeps the 8 most recent commits of a table
FOOTER = 1500             # the footer of each data file, in bytes


# ---- the lakehouse at the two times ----

def uuid(n: int) -> str:
    return f"6d1f0c2a-0000-4000-8000-{n:012d}"


def column(name, data_type, compressed=None, uncompressed=None, values=None, nulls=None, *, field_id=None,
           nullable=True, partition=False, chunks=None, dictionary=0, fallback=0, dictionary_page=0, unencoded=None,
           without=0, distinct=None, codecs=("SNAPPY",), encodings=("PLAIN", "RLE")):
    """A column with its totals. A partition column that the files do not store has none."""
    row = {"name": name, "dataType": data_type, "isNullable": nullable, "fieldId": field_id,
           "isPartitionColumn": partition or None}
    if not partition:
        row.update(compressedSize=compressed, uncompressedSize=uncompressed, valueCount=values, nullCount=nulls,
                   chunkCount=chunks, dictionaryChunkCount=dictionary, fallbackChunkCount=fallback,
                   dictionaryPageSize=dictionary_page, unencodedByteArrayDataBytes=unencoded,
                   codecs=list(codecs), encodings=list(encodings), fileCountWithoutColumn=without,
                   largestChunkDistinctCount=distinct)
    return row


def file(path, size, rows, modified, *, deleted=0, dv=None, partition=None, v_ordered=False):
    return {"path": path, "fileSize": size, "rowCount": rows, "deletedRowCount": deleted, "deletionVectorSize": dv,
            "modifiedAt": modified, "isVOrdered": v_ordered, "footerSize": FOOTER, "rowGroupCount": 1,
            "partition": partition}


def commits(*items: Tuple[int, str, str]) -> List[Dict[str, Any]]:
    return [{"commitId": str(version), "timestamp": at, "operation": operation} for version, at, operation in items]


SALES_COMMITS = commits((0, "2026-09-01T08:00:00.000Z", "CREATE TABLE"), (1, "2026-09-02T08:00:00.000Z", "WRITE"),
                        (2, "2026-09-10T08:00:00.000Z", "WRITE"), (3, "2026-09-20T08:00:00.000Z", "WRITE"),
                        (4, "2026-09-28T08:00:00.000Z", "DELETE"), (5, "2026-10-02T08:00:00.000Z", "DELETE"),
                        (6, "2026-10-03T08:00:00.000Z", "RENAME COLUMN"), (7, "2026-10-03T09:00:00.000Z", "DROP COLUMNS"),
                        (8, "2026-10-03T10:00:00.000Z", "ADD COLUMNS"), (9, "2026-10-06T08:00:00.000Z", "WRITE"))
ORDERS_COMMITS = commits((0, "2026-09-01T09:00:00.000Z", "CREATE TABLE"), (1, "2026-09-05T09:00:00.000Z", "WRITE"),
                         (2, "2026-09-10T09:00:00.000Z", "WRITE"), (3, "2026-09-15T09:00:00.000Z", "WRITE"),
                         (4, "2026-09-25T09:00:00.000Z", "WRITE"), (5, "2026-10-04T09:00:00.000Z", "CHANGE COLUMN"),
                         (6, "2026-10-05T12:00:00.000Z", "OPTIMIZE"), (7, "2026-10-06T07:00:00.000Z", "VACUUM START"),
                         (8, "2026-10-06T07:05:00.000Z", "VACUUM END"))

BEFORE_TABLES = [
    {"name": "sales", "tableId": uuid(1), "detailDepth": "Files", "snapshotId": "4", "createdAt": "2026-09-01T08:00:00.000Z",
     "partitionedBy": "year", "commits": SALES_COMMITS[:5],
     "metadata": (7, 10500), "nonLive": (1, 8000, "2026-09-28T08:00:00.000Z", "2026-09-28T08:00:00.000Z"),
     "unreferenced": (1, 8000), "stale": 0,
     "columns": [column("id", "bigint", 30000, 85000, 10600, 0, field_id=1, nullable=False, chunks=4),
                 column("amt", "decimal(18,2)", 25000, 42400, 10600, 0, field_id=2, chunks=4),
                 column("discount", "double", 20000, 84800, 10600, 2400, field_id=3, chunks=4, dictionary=4,
                        dictionary_page=1600, distinct=40),
                 column("note", "string", 25000, 60000, 10600, 9000, field_id=4, chunks=4, dictionary=4, fallback=1,
                        dictionary_page=2400, unencoded=48000, distinct=300),
                 column("year", "int", field_id=5, partition=True)],
     "files": [file("year=2024/part-00001.parquet", 10000, 1000, "2026-09-02T08:00:00.000Z", partition=("2024",)),
               file("year=2025/part-00002.parquet", 40000, 4000, "2026-09-10T08:00:00.000Z", partition=("2025",)),
               file("year=2025/part-00003.parquet", 36000, 3600, "2026-09-20T08:00:00.000Z", deleted=100, dv=40,
                    partition=("2025",)),
               file("year=2026/part-00004.parquet", 20000, 2000, "2026-09-28T08:00:00.000Z", partition=("2026",),
                    v_ordered=True)]},
    {"name": "orders", "tableId": uuid(2), "detailDepth": "Files", "snapshotId": "4", "createdAt": "2026-09-01T09:00:00.000Z",
     "commits": ORDERS_COMMITS[:5],
     "metadata": (6, 9000), "nonLive": (3, 24000, "2026-09-10T09:00:00.000Z", "2026-09-15T09:00:00.000Z"),
     "unreferenced": (10, 100000), "stale": 0,
     "columns": [column("order_id", "bigint", 9000, 25600, 3200, 0, nullable=False, chunks=4),
                 column("qty", "int", 4000, 12800, 3200, 0, chunks=4, dictionary=4, dictionary_page=200, distinct=20),
                 column("price", "int", 6000, 12800, 3200, 100, chunks=4),
                 column("customer", "string", 9000, 30000, 3200, 0, chunks=4, dictionary=4, dictionary_page=1800,
                        unencoded=25600, distinct=150)],
     "files": [file(f"part-{n:05d}.parquet", 8000, 800, at) for n, at in
               ((1, "2026-09-05T09:00:00.000Z"), (2, "2026-09-10T09:00:00.000Z"), (3, "2026-09-15T09:00:00.000Z"),
                (4, "2026-09-25T09:00:00.000Z"))]},
    {"name": "customers", "tableId": uuid(3), "detailDepth": "Summary", "snapshotId": "7",
     "createdAt": "2026-08-01T10:00:00.000Z", "commits": commits((6, "2026-09-12T10:00:00.000Z", "WRITE"),
                                                                 (7, "2026-09-14T10:00:00.000Z", "MERGE")),
     "metadata": (9, 14000), "nonLive": (0, 0, None, None), "unreferenced": (0, 0), "stale": 0,
     "columns": [column("customer_id", "bigint", 20000, 40000, 5000, 0, nullable=False, chunks=2),
                 column("name", "string", 25000, 70000, 5000, 50, chunks=2, dictionary=2, dictionary_page=9000,
                        unencoded=60000, distinct=4800)],
     "files": [file("part-00001.parquet", 26000, 2600, "2026-09-12T10:00:00.000Z"),
               file("part-00002.parquet", 24000, 2400, "2026-09-14T10:00:00.000Z")]},
    {"name": "products", "tableId": uuid(4), "detailDepth": "Summary", "snapshotId": "12",
     "createdAt": "2026-07-01T10:00:00.000Z", "commits": commits((12, "2026-09-18T10:00:00.000Z", "WRITE")),
     "metadata": (14, 21000), "nonLive": (2, 30000, "2026-09-18T10:00:00.000Z", "2026-09-18T10:00:00.000Z"),
     "unreferenced": (2, 30000), "stale": 0,
     "columns": [column("product_id", "bigint", 12000, 48000, 6000, 0, nullable=False, chunks=3),
                 column("title", "string", 30000, 90000, 6000, 0, chunks=3, unencoded=80000),
                 column("price", "decimal(10,2)", 12000, 30000, 6000, 0, chunks=3)],
     "files": [file(f"part-{n:05d}.parquet", 20000, 2000, "2026-09-18T10:00:00.000Z") for n in (1, 2, 3)]},
    {"name": "legacy", "tableId": uuid(6), "detailDepth": "Files", "snapshotId": "20",
     "createdAt": "2025-01-01T00:00:00.000Z", "commits": commits((20, "2026-03-01T00:00:00.000Z", "WRITE")),
     "metadata": (30, 45000), "nonLive": (0, 0, None, None), "unreferenced": (0, 0), "stale": 0,
     "columns": [column("key", "string", 60000, 150000, 15000, 0, chunks=2, unencoded=120000),
                 column("value", "string", 70000, 160000, 15000, 300, chunks=2, unencoded=140000)],
     "files": [file("old/part-00001.parquet", 70000, 7000, "2026-03-01T00:00:00.000Z"),
               file("old/part-00002.parquet", 66000, 8000, "2026-03-01T00:00:00.000Z")]},
    {"name": "staging", "namespace": ["raw"], "format": "Parquet", "detailDepth": "Files", "commits": [],
     "metadata": (None, None), "nonLive": (None, None, None, None), "unreferenced": (0, 0), "stale": 0,
     "columns": [column("batch_id", "bigint", 2400, 8160, 1020, 0, chunks=2),
                 column("payload", "string", 4600, 12000, 1020, 0, chunks=2, unencoded=10000)],
     "files": [file("batch-001.parquet", 5000, 500, "2026-09-29T00:00:00.000Z"),
               file("batch-002.parquet", 5200, 520, "2026-09-30T00:00:00.000Z")]},
    {"name": "audit", "tableId": uuid(7), "detailDepth": "Files", "snapshotId": "3",
     "createdAt": "2026-06-01T00:00:00.000Z",
     "commits": commits((0, "2026-06-01T00:00:00.000Z", "CREATE TABLE"), (1, "2026-06-02T00:00:00.000Z", "WRITE"),
                        (2, "2026-06-03T00:00:00.000Z", "WRITE"), (3, "2026-06-04T00:00:00.000Z", "OPTIMIZE")),
     "metadata": (4, 6000), "nonLive": (3, 9000, "2026-06-04T00:00:00.000Z", "2026-06-04T00:00:00.000Z"),
     "unreferenced": (0, 0), "stale": 0,
     "columns": [column("event_time", "timestamp", 4000, 12000, 1500, 0, chunks=1),
                 column("actor", "string", 5000, 9000, 1500, 0, chunks=1, dictionary=1, dictionary_page=300,
                        unencoded=8000, distinct=12)],
     "files": [file("part-00004.parquet", 12000, 1500, "2026-06-04T00:00:00.000Z", v_ordered=True)]},
]

AFTER_TABLES = [
    {"name": "events", "tableId": uuid(8), "detailDepth": "Files", "snapshotId": "1", "createdAt": "2026-10-04T00:00:00.000Z",
     "commits": commits((0, "2026-10-04T00:00:00.000Z", "CREATE TABLE"), (1, "2026-10-07T00:00:00.000Z", "WRITE")),
     "metadata": (2, 3000), "nonLive": (0, 0, None, None), "unreferenced": (0, 0), "stale": 0,
     "columns": [column("event_id", "bigint", 16000, 40000, 5000, 0, field_id=1, nullable=False, chunks=2),
                 column("kind", "string", 6000, 20000, 5000, 0, field_id=2, chunks=2, dictionary=2,
                        dictionary_page=100, unencoded=30000, distinct=6)],
     "files": [file("part-00001.parquet", 12000, 2500, "2026-10-07T00:00:00.000Z", v_ordered=True),
               file("part-00002.parquet", 13000, 2500, "2026-10-07T00:00:00.000Z", v_ordered=True)]},
    {"name": "sales", "tableId": uuid(1), "detailDepth": "Files", "snapshotId": "9", "createdAt": "2026-09-01T08:00:00.000Z",
     "partitionedBy": "year", "commits": SALES_COMMITS[2:],
     "metadata": (12, 16000), "nonLive": (2, 18000, "2026-09-28T08:00:00.000Z", "2026-10-02T08:00:00.000Z"),
     "unreferenced": (2, 18000), "stale": 7000,
     "columns": [column("id", "bigint", 38500, 108800, 13600, 0, field_id=1, nullable=False, chunks=5),
                 column("amount", "decimal(18,2)", 32000, 54400, 13600, 0, field_id=2, chunks=5),
                 column("year", "int", field_id=5, partition=True),
                 column("discount", "double", 9000, 32000, 4000, 4000, field_id=6, chunks=2, without=3),
                 column("region", "string", 7000, 24000, 4000, 0, field_id=7, chunks=2, dictionary=2,
                        dictionary_page=500, unencoded=20000, distinct=8, without=3)],
     "files": [file("year=2025/part-00002.parquet", 40000, 4000, "2026-09-10T08:00:00.000Z", partition=("2025",)),
               file("year=2025/part-00003.parquet", 36000, 3600, "2026-09-20T08:00:00.000Z", deleted=300, dv=56,
                    partition=("2025",)),
               file("year=2026/part-00004.parquet", 20000, 2000, "2026-09-28T08:00:00.000Z", partition=("2026",),
                    v_ordered=True),
               file("year=2026/part-00005.parquet", 25000, 2500, "2026-10-06T08:00:00.000Z", partition=("2026",),
                    v_ordered=True),
               file("year=2027/part-00006.parquet", 15000, 1500, "2026-10-06T08:00:00.000Z", partition=("2027",),
                    v_ordered=True)]},
    {"name": "clients", "tableId": uuid(3), "detailDepth": "Summary", "snapshotId": "7",
     "createdAt": "2026-08-01T10:00:00.000Z", "commits": commits((6, "2026-09-12T10:00:00.000Z", "WRITE"),
                                                                 (7, "2026-09-14T10:00:00.000Z", "MERGE")),
     "metadata": (9, 14000), "nonLive": (0, 0, None, None), "unreferenced": (0, 0), "stale": 0,
     "columns": [column("customer_id", "bigint", 20000, 40000, 5000, 0, nullable=False, chunks=2),
                 column("name", "string", 25000, 70000, 5000, 50, chunks=2, dictionary=2, dictionary_page=9000,
                        unencoded=60000, distinct=4800)],
     "files": [file("part-00001.parquet", 26000, 2600, "2026-09-12T10:00:00.000Z"),
               file("part-00002.parquet", 24000, 2400, "2026-09-14T10:00:00.000Z")]},
    {"name": "orders", "tableId": uuid(2), "detailDepth": "Files", "snapshotId": "8", "createdAt": "2026-09-01T09:00:00.000Z",
     "commits": ORDERS_COMMITS[1:],
     "metadata": (10, 12500), "nonLive": (7, 56000, "2026-09-10T09:00:00.000Z", "2026-10-05T12:00:00.000Z"),
     "unreferenced": (4, 32000), "stale": 0,
     "columns": [column("order_id", "bigint", 8000, 25600, 3200, 0, nullable=False, chunks=1),
                 column("qty", "int", 3500, 12800, 3200, 0, chunks=1, dictionary=1, dictionary_page=200, distinct=20),
                 column("price", "bigint", 7000, 25600, 3200, 100, chunks=1),
                 column("customer", "string", 8000, 30000, 3200, 0, chunks=1, dictionary=1, dictionary_page=1800,
                        unencoded=25600, distinct=150)],
     "files": [file("part-00005.parquet", 30000, 3200, "2026-10-05T12:00:00.000Z", v_ordered=True)]},
    {"name": "products", "tableId": uuid(5), "detailDepth": "Summary", "snapshotId": "0",
     "createdAt": "2026-10-03T10:00:00.000Z", "commits": commits((0, "2026-10-03T10:00:00.000Z", "CREATE TABLE")),
     "metadata": (1, 2000), "nonLive": (0, 0, None, None), "unreferenced": (0, 0), "stale": 0,
     "columns": [column("product_id", "bigint", 4000, 16000, 2000, 0, nullable=False, chunks=1),
                 column("title", "string", 9000, 30000, 2000, 0, chunks=1, unencoded=26000),
                 column("price", "decimal(10,2)", 4000, 10000, 2000, 0, chunks=1),
                 column("category", "string", 1000, 4000, 2000, 0, chunks=1, dictionary=1, dictionary_page=50,
                        distinct=5)],
     "files": [file("part-00001.parquet", 20000, 2000, "2026-10-03T10:00:00.000Z")]},
    {"name": "staging", "namespace": ["raw"], "format": "Parquet", "detailDepth": "Files", "commits": [],
     "metadata": (None, None), "nonLive": (None, None, None, None), "unreferenced": (0, 0), "stale": 0,
     "columns": [column("batch_id", "bigint", 3600, 12480, 1560, 0, chunks=3),
                 column("payload", "string", 6900, 18000, 1560, 0, chunks=3, unencoded=15000),
                 column("source_file", "string", 600, 2000, 540, 0, chunks=1, without=2)],
     "files": [file("batch-001.parquet", 5000, 500, "2026-09-29T00:00:00.000Z"),
               file("batch-002.parquet", 5200, 520, "2026-09-30T00:00:00.000Z"),
               file("batch-003.parquet", 5400, 540, "2026-10-07T00:00:00.000Z")]},
    {"name": "audit", "tableId": uuid(7), "detailDepth": "Summary", "snapshotId": "3",
     "createdAt": "2026-06-01T00:00:00.000Z",
     "commits": commits((0, "2026-06-01T00:00:00.000Z", "CREATE TABLE"), (1, "2026-06-02T00:00:00.000Z", "WRITE"),
                        (2, "2026-06-03T00:00:00.000Z", "WRITE"), (3, "2026-06-04T00:00:00.000Z", "OPTIMIZE")),
     "metadata": (4, 6000), "nonLive": (3, 9000, "2026-06-04T00:00:00.000Z", "2026-06-04T00:00:00.000Z"),
     "unreferenced": (0, 0), "stale": 0,
     "columns": [column("event_time", "timestamp", 4000, 12000, 1500, 0, chunks=1),
                 column("actor", "string", 5000, 9000, 1500, 0, chunks=1, dictionary=1, dictionary_page=300,
                        unencoded=8000, distinct=12)],
     "files": [file("part-00004.parquet", 12000, 1500, "2026-06-04T00:00:00.000Z", v_ordered=True)]},
]

# The SCENARIO: what changed, declared, so that the expected diff does not depend on an implementation of the rules.
# Tables by their name in each footprint; columns by their name in each table.
SCENARIO = {
    "kept": [
        # older name, newer name, matched by, renamed, changed
        ("sales", "sales", "tableId", False, True),
        ("orders", "orders", "tableId", False, True),
        ("customers", "clients", "tableId", True, True),
        ("staging", "staging", "name", False, True),
        ("audit", "audit", "tableId", False, False),
    ],
    "removed": [("products", True), ("legacy", False)],   # name, recreated
    "added": [("events", False), ("products", True)],
    "columns": {
        # older table: kept columns (older, newer, matched by), added columns, removed columns
        "sales": ([("id", "id", "fieldId"), ("amt", "amount", "fieldId"), ("year", "year", "fieldId")],
                  ["discount", "region"], ["discount", "note"]),
        "orders": ([("order_id", "order_id", "name"), ("qty", "qty", "name"), ("price", "price", "name"),
                    ("customer", "customer", "name")], [], []),
        "customers": ([("customer_id", "customer_id", "name"), ("name", "name", "name")], [], []),
        "staging": ([("batch_id", "batch_id", "name"), ("payload", "payload", "name")], ["source_file"], []),
        "audit": ([("event_time", "event_time", "name"), ("actor", "actor", "name")], [], []),
    },
    # older table: files, partitions, commits as (added, removed, kept, changed); None when not compared
    "detail": {
        "sales": ((2, 1, 3, 1), (1, 1, 2, 2), (5, 2, 3, None)),
        "orders": ((1, 4, 0, 0), (0, 0, 0, 0), (4, 1, 4, None)),
        "customers": (None, (0, 0, 0, 0), (0, 0, 2, None)),
        "staging": ((1, 0, 2, 0), (0, 0, 0, 0), (0, 0, 0, None)),
        "audit": (None, (0, 0, 0, 0), (0, 0, 4, None)),
    },
}


# ---- writing the footprints ----

def distribution(values: Sequence[int]) -> Optional[Dict[str, int]]:
    """A Distribution with its quartiles taken by position, as section 4.3 of the object model says."""
    if not values:
        return None
    v, n = sorted(values), len(values)
    return {"count": n, "sum": sum(v), "minimum": v[0], "firstQuartile": v[n // 4], "median": v[n // 2],
            "thirdQuartile": v[3 * n // 4], "maximum": v[n - 1]}


def partitions_of(spec: Dict[str, Any]) -> List[Dict[str, Any]]:
    """The partitions of a partitioned table, in the order of their first file."""
    result: Dict[Tuple[str, ...], Dict[str, Any]] = {}
    for f in spec["files"]:
        if f["partition"] is None:
            continue
        p = result.setdefault(f["partition"], {"values": list(f["partition"]), "fileCount": 0, "fileSize": 0,
                                                "rowCount": 0, "deletedRowCount": 0, "lastModifiedAt": None})
        p["fileCount"] += 1
        p["fileSize"] += f["fileSize"]
        p["rowCount"] += f["rowCount"]
        p["deletedRowCount"] += f["deletedRowCount"]
        p["lastModifiedAt"] = max(filter(None, (p["lastModifiedAt"], f["modifiedAt"])))
    return list(result.values())


def table_row(spec: Dict[str, Any], source: int) -> Dict[str, Any]:
    """The fields of a table: what the spec says, and the totals computed from its files, columns, and commits."""
    files, columns, history = spec["files"], spec["columns"], spec["commits"]
    partitions = partitions_of(spec) if spec.get("partitionedBy") else []
    metadata_count, metadata_size = spec["metadata"]
    non_live_count, non_live_size, non_live_oldest, non_live_newest = spec["nonLive"]
    stored = [c for c in columns if not c.get("isPartitionColumn")]
    stale = spec["stale"]
    row = {
        "source": source, "namespace": spec.get("namespace"), "name": spec["name"], "format": spec.get("format", "Delta"),
        "tableId": spec.get("tableId"), "createdAt": spec.get("createdAt"), "snapshotId": spec.get("snapshotId"),
        "snapshotTimestamp": history[-1]["timestamp"] if history else None, "collectedLevel": 2,
        "detailDepth": spec["detailDepth"],
        "fileCount": len(files), "fileSize": sum(f["fileSize"] for f in files),
        "rowCount": sum(f["rowCount"] for f in files), "deletedRowCount": sum(f["deletedRowCount"] for f in files),
        "lastModifiedAt": max(f["modifiedAt"] for f in files),
        "vOrderedFileCount": sum(1 for f in files if f["isVOrdered"]),
        "partitionCount": len(partitions) if spec.get("partitionedBy") else None,
        "commitCount": int(history[-1]["commitId"]) + 1 if history else None,
        "commitsRead": len(history) if history else None,
        "oldestCommitAt": history[0]["timestamp"] if history else None,
        "nonLiveFileCount": non_live_count, "nonLiveFileSize": non_live_size,
        "nonLiveOldestRemovedAt": non_live_oldest, "nonLiveNewestRemovedAt": non_live_newest,
        "unreferencedFileCount": spec["unreferenced"][0], "unreferencedFileSize": spec["unreferenced"][1],
        "metadataFileCount": metadata_count, "metadataSize": metadata_size,
        "footerCount": len(files), "rowGroupCount": sum(f["rowGroupCount"] for f in files),
        "compressedSize": sum(c["compressedSize"] for c in stored) + stale,
        "uncompressedSize": sum(c["uncompressedSize"] for c in stored) + 2 * stale,
        "footerSize": sum(f["footerSize"] for f in files), "staleColumnSize": stale,
        "columnTotalsSource": "Footers",
        "fileSizes": distribution([f["fileSize"] for f in files]),
        "rowGroupRowCounts": distribution([f["rowCount"] for f in files]),
        "partitionSizes": distribution([p["fileSize"] for p in partitions]),
    }
    return row


def footprint(tables: List[Dict[str, Any]], started: str, completed: str, requests: int) -> Tuple[bytes, Dict]:
    """The bytes of a PQAX file, and the rows of each table by its name: the table, its columns, and its files."""
    builder = pqax.Builder(dict(COLLECTOR, startedAt=started, completedAt=completed, requestCount=requests,
                                bytesRead=requests * 4096))
    source = builder.add_source(**SOURCE)
    written: Dict[str, Dict[str, Any]] = {}
    for spec in tables:
        row = table_row(spec, source)
        table = builder.add_table(**row)
        column_ids = {}
        for c in spec["columns"]:
            column_ids[c["name"]] = builder.add_column(table=table, **c)
        partition_ids = {}
        if spec.get("partitionedBy"):
            spec_id = builder.add_partition_spec(table=table)
            builder.add_partition_field(partitionSpec=spec_id, position=0, name=spec["partitionedBy"],
                                        sourceColumns=[column_ids[spec["partitionedBy"]]], transform="identity")
            for p in partitions_of(spec):
                partition_ids[tuple(p["values"])] = builder.add_partition(table=table, partitionSpec=spec_id, **p)
        if spec["detailDepth"] != "Summary":
            for f in spec["files"]:
                fields = dict(f, partition=partition_ids.get(f["partition"]) if f["partition"] else None)
                builder.add_file(table=table, **fields)
        history = spec["commits"][-COMMITS_KEPT:]
        for c in history:
            builder.add_commit(table=table, **c)
        operations: Dict[str, List[str]] = {}
        for c in history:
            operations.setdefault(c["operation"], []).append(c["timestamp"])
        for name, times in operations.items():
            builder.add_operation(table=table, name=name, commitCount=len(times), lastAt=max(times))
        written[spec["name"]] = {"id": table, "row": row, "columns": spec["columns"], "spec": spec,
                                 "partitions": partitions_of(spec) if spec.get("partitionedBy") else []}
    buffer = io.BytesIO()
    builder.write(buffer)
    return buffer.getvalue(), written


def build() -> Dict[str, Tuple[bytes, Dict]]:
    return {BEFORE: footprint(BEFORE_TABLES, "2026-10-01T05:58:00.000Z", "2026-10-01T06:00:00.000Z", 61),
            AFTER: footprint(AFTER_TABLES, "2026-10-08T05:58:00.000Z", "2026-10-08T06:00:00.000Z", 66)}


def write_samples(samples: Path = SAMPLES, expected: Path = EXPECTED) -> Dict[str, Tuple[bytes, Dict]]:
    """Writes the two samples and their expected results as a reader gets them, as make_kit.py does for the others."""
    import make_kit  # noqa: E402 - imported here, since make_kit imports this module in its main
    built = build()
    for name, (data, _) in built.items():
        (samples / name).write_bytes(data)
        result = make_kit.expected_results(name, data)
        make_kit.write_text(str(expected / (Path(name).stem + ".json")), make_kit.pretty(result))
        print(f"{name:32} {len(data):>9,} bytes   {len(result['parts'])} parts, {sum(result['rows'].values()):,} rows")
    return built


# ---- the expected diff ----

TIMESTAMPS = {"lastModifiedAt", "oldestCommitAt", "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt",
              "maintenance.lastCommitAt", "maintenance.lastVacuumAt", "maintenance.lastOptimizeAt",
              "partitions.oldestModifiedAt", "partitions.newestModifiedAt"}
TABLE_TOTALS = ("fileCount", "fileSize", "rowCount", "deletedRowCount", "lastModifiedAt", "vOrderedFileCount",
                "partitionCount", "deleteFileCount", "deleteFileSize", "commitCount", "commitsRead", "oldestCommitAt",
                "nonLiveFileCount", "nonLiveFileSize", "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt",
                "unreferencedFileCount", "unreferencedFileSize", "metadataFileCount", "metadataSize", "footerCount",
                "rowGroupCount", "compressedSize", "uncompressedSize", "footerSize", "staleColumnSize")
DISTRIBUTIONS = ("fileSizes", "rowGroupRowCounts", "partitionSizes")
MEMBERS = ("count", "sum", "minimum", "firstQuartile", "median", "thirdQuartile", "maximum")
STACK = ("live", "nonLive", "unreferenced", "deleted", "stale", "metadata", "footers")
TABLE_MEASURES = (
    [f"stack.{key}" for key in STACK]
    + ["stack.listed", "stack.data", "stack.total", "stack.waste", "stack.overhead", "stack.wasteShare",
       "stack.overheadShare"]
    + [f"stack.shares.{key}" for key in STACK]
    + ["smallFiles.count", "smallFiles.shareAtLeast", "tinyFiles.count", "tinyFiles.shareAtLeast"]
    + [f"files.{key}" for key in MEMBERS] + ["files.vOrder.share", "files.vOrder.bytesShare"]
    + [f"measures.{key}" for key in ("compressionRatio", "deletedRowShare", "averageFileSize", "averageRowGroupRows",
                                     "footerCoverage", "staleColumnShare", "nonLiveShare", "averagePartitionSize",
                                     "dictionaryFallbackShare")]
    + [f"maintenance.{key}" for key in ("lastCommitAt", "commitsPerDay", "daysSinceLastCommit", "hasVacuum",
                                        "lastVacuumAt", "hasOptimize", "lastOptimizeAt")]
    + [f"partitions.{key}" for key in ("count", "kept", "oldestModifiedAt", "newestModifiedAt")])
COLUMN_TOTALS = ("compressedSize", "uncompressedSize", "valueCount", "nullCount", "chunkCount", "dictionaryChunkCount",
                 "fallbackChunkCount", "dictionaryPageSize", "unencodedByteArrayDataBytes", "codecs", "encodings",
                 "fileCountWithoutColumn", "largestChunkDistinctCount", "distinctCount")
COLUMN_MEASURES = ("nullShare", "compressionRatio", "dictionaryShare", "dictionaryFallbackShare", "averageValueLength",
                   "filesWithoutColumnShare")
COLLECTION = ("sources", "tables", "columns", "files", "fileSize", "rowCount", "footprint", "waste", "overhead")


def plain(value: Any) -> Any:
    if isinstance(value, float) and value.is_integer():
        return int(value)
    if isinstance(value, dict):
        return {key: plain(item) for key, item in value.items()}
    if isinstance(value, list):
        return [plain(item) for item in value]
    return value


def moment(text: Optional[str]) -> Optional[int]:
    if text is None:
        return None
    value = datetime.datetime.fromisoformat(text.replace("Z", "+00:00"))
    return round(value.timestamp() * 1000)


def is_number(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def change(name: str, older: Any, newer: Any) -> Dict[str, Any]:
    """A compared value: the older, the newer, and the delta."""
    if older is None or newer is None:
        delta = None
    elif name in TIMESTAMPS:
        delta = moment(newer) - moment(older)
    elif is_number(older) and is_number(newer):
        delta = newer - older
    else:
        delta = None
    return {"older": older, "newer": newer, "delta": delta}


def changed(item: Dict[str, Any]) -> bool:
    return item["older"] != item["newer"] or (type(item["older"]) is bool) != (type(item["newer"]) is bool)


def path(value: Any, dotted: str) -> Any:
    for key in dotted.split("."):
        value = value.get(key) if isinstance(value, dict) else None
    return value


def table_totals(row: Dict[str, Any]) -> Dict[str, Any]:
    values = {name: row.get(name) for name in TABLE_TOTALS}
    for name in DISTRIBUTIONS:
        for member in MEMBERS:
            values[f"{name}.{member}"] = (row.get(name) or {}).get(member)
    return values


def side(written: Dict[str, Any], measured: Dict[str, Any]) -> Dict[str, Any]:
    row = written["row"]
    return {"id": measured["id"], "label": measured["label"], "name": row["name"], "namespace": row["namespace"],
            "tableId": row["tableId"], "snapshotId": row["snapshotId"], "detailDepth": row["detailDepth"],
            "columnCount": len(written["columns"]), "fileCount": row["fileCount"], "fileSize": row["fileSize"],
            "rowCount": row["rowCount"], "total": measured["stack"]["total"], "waste": measured["stack"]["waste"]}


def column_side(c: Dict[str, Any], measured: Dict[str, Any]) -> Dict[str, Any]:
    return {"id": measured["id"], "label": measured["label"], "name": c["name"], "fieldId": c.get("fieldId"),
            "dataType": c["dataType"], "isNullable": c["isNullable"], "compressedSize": c.get("compressedSize")}


def detail(counts: Optional[Tuple], added_size: Any = None, removed_size: Any = None,
           commits: bool = False) -> Optional[Dict[str, Any]]:
    if counts is None:
        return None
    added, removed, kept, changes = counts
    if commits:
        return {"added": added, "removed": removed, "kept": kept}
    return {"added": added, "removed": removed, "kept": kept, "changed": changes,
            "addedFileSize": added_size, "removedFileSize": removed_size}


def header(name: str, data: bytes, measures: Dict[str, Any]) -> Dict[str, Any]:
    with pqax.open(io.BytesIO(data)) as document:
        manifest, collection = document.manifest, document.collection
    return {"file": name, "embeddedPart": None, "format": manifest["format"], "formatVersion": manifest["formatVersion"],
            "collector": f"{collection['collectorApp']} {collection['collectorAppVersion']}", "library": None,
            "collectedAt": collection["completedAt"], "level": collection["level"],
            "tableCount": len(measures["tables"]), "filesRead": measures["filesRead"], "warnings": measures["warnings"]}


def expected_diff(built: Dict[str, Tuple[bytes, Dict]], views: Dict[str, Dict[str, Any]]) -> Dict[str, Any]:
    (old_data, old), (new_data, new) = built[BEFORE], built[AFTER]
    old_measures, new_measures = views[BEFORE], views[AFTER]

    def measured(measures: Dict[str, Any], written: Dict[str, Any]) -> Dict[str, Any]:
        return next(t for t in measures["tables"] if t["id"] == written["id"])

    def columns_measured(measures: Dict[str, Any], written: Dict[str, Any]) -> Dict[str, Dict[str, Any]]:
        return {c["name"]: c for c in measures["columns"] if c["table"] == written["id"]}

    def position(written_all: Dict[str, Any], name: str) -> int:
        return list(written_all).index(name)

    entries: List[Tuple[str, Dict[str, Any], int]] = []   # kind, JSON entry, position in its footprint
    for name, recreated in SCENARIO["removed"]:
        entries.append(("removed", dict(side(old[name], measured(old_measures, old[name])), recreated=recreated),
                        position(old, name)))
    for name, recreated in SCENARIO["added"]:
        entries.append(("added", dict(side(new[name], measured(new_measures, new[name])), recreated=recreated),
                        position(new, name)))
    for old_name, new_name, matched_by, renamed, is_changed in SCENARIO["kept"]:
        o, n = old[old_name], new[new_name]
        om, nm = measured(old_measures, o), measured(new_measures, n)
        ot, nt = table_totals(o["row"]), table_totals(n["row"])
        totals = {key: change(key, ot[key], nt[key]) for key in ot}
        measures = {}
        for key in TABLE_MEASURES:
            measures[key] = change(key, path(om, key), path(nm, key))
        kept_columns, added_columns, removed_columns = SCENARIO["columns"][old_name]
        ocm, ncm = columns_measured(old_measures, o), columns_measured(new_measures, n)
        ocs = {c["name"]: c for c in o["columns"]}
        ncs = {c["name"]: c for c in n["columns"]}
        new_order = [c["name"] for c in n["columns"]]
        old_order = [c["name"] for c in o["columns"]]
        column_entries = []
        for old_column, new_column, column_matched in kept_columns:
            oc, nc = ocs[old_column], ncs[new_column]
            column_totals = {key: change(key, oc.get(key), nc.get(key)) for key in COLUMN_TOTALS}
            column_measures = {key: change(key, ocm[old_column][key], ncm[new_column][key]) for key in COLUMN_MEASURES}
            column_renamed = old_column != new_column
            column_changed = (column_renamed or oc["dataType"] != nc["dataType"]
                              or oc["isNullable"] != nc["isNullable"]
                              or any(changed(item) for item in column_totals.values()))
            column_entries.append((new_order.index(new_column), {
                "status": "changed" if column_changed else "unchanged", "matchedBy": column_matched,
                "renamed": column_renamed, "older": column_side(oc, ocm[old_column]),
                "newer": column_side(nc, ncm[new_column]), "totals": column_totals, "measures": column_measures}))
        # A column dropped and added again has the same name in both lists: the newer list for added, the older one
        # for removed.
        added_list = [column_side(c, ncm[c["name"]]) for c in n["columns"] if c["name"] in added_columns]
        removed_list = [column_side(c, ocm[c["name"]]) for c in o["columns"] if c["name"] in removed_columns]
        assert [c["name"] for c in added_list] == [x for x in new_order if x in added_columns]
        assert [c["name"] for c in removed_list] == [x for x in old_order if x in removed_columns]
        kept_list = [entry for _, entry in sorted(column_entries, key=lambda item: item[0])]

        files_counts, partition_counts, commit_counts = SCENARIO["detail"][old_name]
        files = partitions = None
        if files_counts is not None:
            old_files = {f["path"]: f for f in o["spec"]["files"]}
            new_files = {f["path"]: f for f in n["spec"]["files"]}
            added_paths = [p for p in new_files if p not in old_files]
            removed_paths = [p for p in old_files if p not in new_files]
            kept_paths = [p for p in new_files if p in old_files]
            changes = sum(1 for p in kept_paths if any(old_files[p][k] != new_files[p][k] for k in (
                "fileSize", "rowCount", "deletedRowCount", "deletionVectorSize")))
            assert (len(added_paths), len(removed_paths), len(kept_paths), changes) == files_counts, old_name
            files = detail(files_counts, sum(new_files[p]["fileSize"] for p in added_paths) if added_paths else 0,
                           sum(old_files[p]["fileSize"] for p in removed_paths) if removed_paths else 0)
        if partition_counts is not None:
            old_parts = {tuple(p["values"]): p for p in o["partitions"]}
            new_parts = {tuple(p["values"]): p for p in n["partitions"]}
            added_keys = [k for k in new_parts if k not in old_parts]
            removed_keys = [k for k in old_parts if k not in new_parts]
            kept_keys = [k for k in new_parts if k in old_parts]
            changes = sum(1 for k in kept_keys if any(old_parts[k][f] != new_parts[k][f] for f in (
                "fileCount", "fileSize", "rowCount", "deletedRowCount")))
            assert (len(added_keys), len(removed_keys), len(kept_keys), changes) == partition_counts, old_name
            partitions = detail(partition_counts,
                                sum(new_parts[k]["fileSize"] for k in added_keys) if added_keys else 0,
                                sum(old_parts[k]["fileSize"] for k in removed_keys) if removed_keys else 0)
        old_commits = [c["commitId"] for c in o["spec"]["commits"][-COMMITS_KEPT:]]
        new_commits = [c["commitId"] for c in n["spec"]["commits"][-COMMITS_KEPT:]]
        counted = (sum(1 for c in new_commits if c not in old_commits), sum(1 for c in old_commits if c not in new_commits),
                   sum(1 for c in new_commits if c in old_commits), None)
        assert counted == commit_counts, (old_name, counted)
        commits = detail(commit_counts, commits=True)

        table_renamed = (o["row"]["name"], o["row"]["namespace"]) != (n["row"]["name"], n["row"]["namespace"])
        assert table_renamed == renamed, old_name
        table_changed = (table_renamed or o["row"]["snapshotId"] != n["row"]["snapshotId"]
                         or any(changed(item) for item in totals.values()) or added_list or removed_list
                         or any(c["status"] == "changed" for c in kept_list)
                         or any(d is not None and (d["added"] or d["removed"] or d.get("changed"))
                                for d in (files, partitions, commits)))
        assert bool(table_changed) == is_changed, old_name
        entries.append(("kept", {
            "status": "changed" if is_changed else "unchanged", "matchedBy": matched_by, "renamed": renamed,
            "older": side(o, om), "newer": side(n, nm), "totals": totals, "measures": measures,
            "columns": {"added": added_list, "removed": removed_list, "kept": kept_list},
            "files": files, "partitions": partitions, "commits": commits}, position(new, new_name)))

    # The order of section 13.3 and of pqax diff --sort total: the size of the change of the stack Total, the largest
    # first, a null change last; then the label in ordinal order, the kind, and the position in the footprint.
    def magnitude(kind: str, entry: Dict[str, Any]) -> Optional[float]:
        if kind == "kept":
            return None if entry["measures"]["stack.total"]["delta"] is None else abs(entry["measures"]["stack.total"]["delta"])
        return entry["total"]

    def label(kind: str, entry: Dict[str, Any]) -> str:
        return entry["newer"]["label"] if kind == "kept" else entry["label"]

    rank = {"added": 0, "removed": 1, "kept": 2}
    entries.sort(key=lambda item: (magnitude(item[0], item[1]) is None, -(magnitude(item[0], item[1]) or 0),
                                   label(item[0], item[1]).encode("utf-16-be"), rank[item[0]], item[2]))

    collection = {key: change(key, old_measures["collection"]["totals"][key], new_measures["collection"]["totals"][key])
                  for key in COLLECTION}
    old_depths, new_depths = old_measures["collection"]["depths"], new_measures["collection"]["depths"]
    for depth in sorted(set(old_depths) | set(new_depths), key=lambda text: text.encode("utf-16-be")):
        collection[f"depths.{depth}"] = change("depths", old_depths.get(depth, 0), new_depths.get(depth, 0))
    kept = [entry for kind, entry, _ in entries if kind == "kept"]
    return plain({
        "older": header(BEFORE, old_data, old_measures), "newer": header(AFTER, new_data, new_measures),
        "smallFileSize": SMALL_FILE_SIZE, "tinyFileSize": TINY_FILE_SIZE, "sort": "total", "top": None,
        "counts": {"added": len(SCENARIO["added"]), "removed": len(SCENARIO["removed"]), "kept": len(kept),
                   "changed": sum(1 for entry in kept if entry["status"] == "changed")},
        "collection": collection,
        "added": [entry for kind, entry, _ in entries if kind == "added"],
        "removed": [entry for kind, entry, _ in entries if kind == "removed"],
        "kept": kept,
    })


def normalized(measures: Dict[str, Any]) -> Dict[str, Any]:
    """The measures of the views with every time in the form of the format, as the diff writes it."""
    def walk(value: Any, key: str = "") -> Any:
        if isinstance(value, dict):
            return {k: walk(v, k) for k, v in value.items()}
        if isinstance(value, list):
            return [walk(v, key) for v in value]
        if isinstance(value, str) and key.endswith("At"):
            moment_ms = moment(value)
            when = datetime.datetime(1970, 1, 1, tzinfo=datetime.timezone.utc) + datetime.timedelta(milliseconds=moment_ms)
            return when.strftime("%Y-%m-%dT%H:%M:%S.") + f"{when.microsecond // 1000:03d}Z"
        return value
    return walk(measures)


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Rebuild the samples and the expected diff of the comparison test.")
    parser.add_argument("--check", action="store_true", help="write nothing; exit 1 when a file is not up to date")
    parser.add_argument("--no-build", action="store_true", help="do not build the TypeScript packages")
    arguments = parser.parse_args(argv)
    work = Path(tempfile.mkdtemp(prefix="pqax-diff-"))
    try:
        built = build()
        for name, (data, _) in built.items():
            (work / name).write_bytes(data)
        command = make_measures.prepare(work, not arguments.no_build)
        results = make_measures.node_measures(command, [work / BEFORE, work / AFTER])
    except make_measures.Unavailable as reason:
        print(f"make_diff: {reason}")
        return 2
    finally:
        shutil.rmtree(work, ignore_errors=True)
    views = {BEFORE: normalized(results[0]), AFTER: normalized(results[1])}
    text = make_measures.pretty(expected_diff(built, views)) + "\n"
    target = DIFF / EXPECTED_DIFF
    if arguments.check:
        problems = [f"samples/{name} differs" for name, (data, _) in built.items()
                    if not (SAMPLES / name).is_file() or (SAMPLES / name).read_bytes() != data]
        if not target.is_file() or target.read_text(encoding="utf-8") != text:
            problems.append(f"expected/diff/{EXPECTED_DIFF} differs")
        for problem in problems:
            print(f"make_diff: {problem}")
        if problems:
            print("Run python conformance/make_diff.py to rebuild them.")
            return 1
        print("The samples and the expected diff are up to date.")
        return 0
    write_samples()
    DIFF.mkdir(parents=True, exist_ok=True)
    with open(target, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    print(f"Wrote expected/diff/{EXPECTED_DIFF}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
