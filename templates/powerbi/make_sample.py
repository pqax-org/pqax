"""Builds a sample export of fifteen footprints over fourteen days, for the Power BI template.

    python templates/powerbi/make_sample.py                     # into templates/powerbi/sample, as Parquet
    python templates/powerbi/make_sample.py --to C:\\pqax\\sample --format csv

Each footprint is conformance/samples/summary-only.pqax of the kit, rewritten with the collection time of its day and
with the totals of its two tables changed day by day, so that the report has a history to show. The numbers are
synthetic. The lakehouse table sales gets six small files a day, an OPTIMIZE on the tenth day that compacts them, and
a VACUUM on the thirteenth that deletes the files the OPTIMIZE removed. The warehouse table dbo.orders gets one very
small file a day and files that no commit references. The last day has two collections, at 05:30 and at 14:00, as when
a collection is run again by hand. Every footprint passes the validator before it is exported with pqax export.
It needs pyarrow for Parquet, and deltalake for Delta.
"""
from __future__ import annotations

import argparse
import datetime
import math
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Dict, List, Tuple

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
sys.path.insert(0, str(ROOT / "src" / "python"))

import pqax  # noqa: E402
from pqax import export  # noqa: E402

BASE = ROOT / "conformance" / "samples" / "summary-only.pqax"
FIRST_DAY = datetime.date(2026, 9, 22)
DAYS = 14
MIB = 1024 * 1024
OPTIMIZE_DAY = 9
VACUUM_DAY = 12
BYTES_PER_ROW = 120
FOOTER = 16 * 1024


def _distribution(sizes: List[int]) -> Dict[str, int]:
    """A Distribution with the quartiles taken by position, as the collector takes them."""
    values = sorted(sizes)
    n = len(values)
    return {"count": n, "sum": sum(values), "minimum": values[0], "firstQuartile": values[n // 4],
            "median": values[n // 2], "thirdQuartile": values[3 * n // 4], "maximum": values[-1]}


def _sales(day: int) -> Tuple[List[int], int, int, List[Tuple[str, int, int]]]:
    """The file sizes of sales on a day, its non-live and unreferenced bytes, and the operations after the kit's."""
    large = [280 * MIB] * 10
    small_day = [2 * MIB, 3 * MIB, 5 * MIB, 9 * MIB, 14 * MIB, 25 * MIB]
    history = 5  # days of small files before the first day
    if day < OPTIMIZE_DAY:
        sizes = large + small_day * (history + day + 1)
        non_live = 150 * MIB + 20 * MIB * day
    else:
        compacted = sum(small_day) * (history + OPTIMIZE_DAY)
        sizes = large + [256 * MIB] * math.ceil(compacted / (256 * MIB)) + small_day * (day - OPTIMIZE_DAY + 1)
        non_live = 150 * MIB + 20 * MIB * day + compacted
    stored = non_live if day < VACUUM_DAY else 20 * MIB * (day - VACUUM_DAY)
    operations = []
    if day >= OPTIMIZE_DAY:
        operations.append(("OPTIMIZE", OPTIMIZE_DAY))
    if day >= VACUUM_DAY:
        operations += [("VACUUM START", VACUUM_DAY), ("VACUUM END", VACUUM_DAY)]
    return sizes, non_live, stored + 5 * MIB, [(name, 1, at) for name, at in operations]


def _orders(day: int) -> Tuple[List[int], int]:
    """The file sizes of dbo.orders on a day, and its unreferenced bytes."""
    return [64 * MIB] * 6 + [6 * MIB] * (day + 1), 2 * MIB * (day + 1)


def _at(day: int, hour: int, minute: int) -> datetime.datetime:
    date = FIRST_DAY + datetime.timedelta(days=day)
    return datetime.datetime(date.year, date.month, date.day, hour, minute, tzinfo=datetime.timezone.utc)


def _totals(sizes: List[int], deleted: int) -> Dict[str, object]:
    file_size = sum(sizes)
    footer_size = FOOTER * len(sizes)
    rows = file_size // BYTES_PER_ROW
    return {"fileCount": len(sizes), "fileSize": file_size, "rowCount": rows, "deletedRowCount": deleted,
            "footerCount": len(sizes), "footerSize": footer_size, "compressedSize": file_size - footer_size,
            "uncompressedSize": 2 * (file_size - footer_size),
            "rowGroupCount": sum(max(1, math.ceil(size / (128 * MIB))) for size in sizes),
            "fileSizes": _distribution(sizes), "rowGroupRowCounts": None, "partitionSizes": None}


def footprint(day: int, hour: int = 5, minute: int = 30) -> bytes:
    """The footprint of a day: the kit sample with the totals and the times of that day."""
    with pqax.open(BASE) as document:
        builder = pqax.Builder.from_document(document)
        old = {name: list(values) for name, values in document.summary["columns"].items()}
    completed = _at(day, hour, minute)
    builder.collection.update({"collectorApp": "pqax-template-sample", "collectorAppVersion": "0.2",
                               "startedAt": completed - datetime.timedelta(minutes=4), "completedAt": completed})
    commit = completed - datetime.timedelta(hours=3)

    sales, non_live, unreferenced, operations = _sales(day)
    if hour >= 12:
        sales = sales + [4 * MIB, 4 * MIB]  # the files that the commits of the morning added
    totals = _totals(sales, 25 + 1500 * day)
    builder.update("tables", 1, **totals, nonLiveFileSize=non_live, nonLiveFileCount=12 + 2 * day,
                   unreferencedFileSize=unreferenced, unreferencedFileCount=3 + (2 * day if day < VACUUM_DAY else 0),
                   nonLiveOldestRemovedAt=_at(max(0, day - 6), 1, 0), nonLiveNewestRemovedAt=commit,
                   snapshotTimestamp=commit, lastModifiedAt=commit, oldestCommitAt=_at(max(0, day - 6), 2, 0),
                   commitsRead=30, commitCount=40 + 6 * day, vOrderedFileCount=len(sales) // 3,
                   metadataSize=12 * MIB + MIB * day // 2, metadataFileCount=40 + 6 * day)
    for name, count, at in operations:
        builder.add_operation(table=1, name=name, commitCount=count, lastAt=_at(at, 2, 30))

    orders, orders_unreferenced = _orders(day)
    builder.update("tables", 2, **_totals(orders, 400 * day), nonLiveFileSize=0, nonLiveFileCount=0,
                   unreferencedFileSize=orders_unreferenced, unreferencedFileCount=day + 1,
                   snapshotTimestamp=commit, lastModifiedAt=commit, oldestCommitAt=_at(max(0, day - 6), 2, 0),
                   commitsRead=day + 2, commitCount=day + 2, metadataSize=2 * MIB + 64 * 1024 * day,
                   metadataFileCount=day + 2)

    # The columns grow with the rows of their table, so that their ratios stay those of the kit.
    factors = {1: totals["rowCount"] / 17000, 2: (sum(orders) // BYTES_PER_ROW) / 4100}
    for row, identifier in enumerate(old["id"]):
        factor = factors[old["table"][row]]
        scaled = {name: round(old[name][row] * factor) for name in (
            "compressedSize", "uncompressedSize", "valueCount", "nullCount", "unencodedByteArrayDataBytes")
            if name in old and old[name][row] is not None}
        builder.update("columns", identifier, **scaled)

    with tempfile.TemporaryDirectory() as folder:
        path = Path(folder) / "footprint.pqax"
        builder.write(path)
        data = path.read_bytes()
    result = pqax.validate(data)
    if not result.is_valid:
        raise SystemExit(f"The footprint of day {day} is not valid: {[str(issue) for issue in result.issues]}")
    return data


def main() -> int:
    parser = argparse.ArgumentParser(description="Build the sample export of the Power BI template.")
    parser.add_argument("--to", default=str(HERE / "sample"), help="the folder (default: templates/powerbi/sample)")
    parser.add_argument("--format", choices=export.FORMATS, default="parquet", help="the format (default: parquet)")
    arguments = parser.parse_args()
    target = Path(arguments.to)
    if target.exists():
        shutil.rmtree(target)
    collections = [(day, 5, 30) for day in range(DAYS)] + [(DAYS - 1, 14, 0)]
    for day, hour, minute in collections:
        data = footprint(day, hour, minute)
        date = FIRST_DAY + datetime.timedelta(days=day)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / f"footprint-{date.isoformat()}-{hour:02d}{minute:02d}.pqax"
            path.write_bytes(data)
            result = export.export(path, target, format=arguments.format)
        print(f"{path.name}: {result['footprintId'][:12]}, {sum(result['tables'].values()):,} rows")
    print(f"{len(collections)} footprints in {target}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
