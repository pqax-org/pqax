"""The measures of section 10 of docs/object-model.md that pqax.measures gains so as to offer all of them, as the
storage views do: the share of each component of the stack (10.2), the file size figures and the V-Order shares (10.3),
the VACUUM and the OPTIMIZE (10.4), the partitions (10.5), the measures of a column (10.6), and those of the collection
(10.7). The footprints are those of tests/typescript-views/section10.test.ts. The expected values come from the text of
section 10, not from the code, with the arithmetic in the comments. A measure that needs an absent field is None,
never 0: the principle "Unknown is not zero" of section 2 and rule 2 of 10.1."""
from __future__ import annotations

import json
from datetime import datetime, timezone

import pytest

import pqax
from pqax import measures
from pqax.__main__ import main

COLLECTION = {"startedAt": "2026-10-01T00:00:00.000Z", "completedAt": "2026-10-11T12:00:00.000Z", "level": 2}

STACK_SHARES = ("live", "nonLive", "unreferenced", "deleted", "stale", "metadata", "footers")


def columnar(rows):
    keys = []
    for row in rows:
        keys += [key for key in row if key not in keys]
    return {key: [row.get(key) for row in rows] for key in keys}


def instant(text):
    return None if text is None else datetime.fromisoformat(text.replace("Z", "+00:00"))


def day(month, day_):
    return datetime(2026, month, day_, tzinfo=timezone.utc)


def stack(file_size=None, deleted=None, footer_size=None, stale_column_size=None, non_live_file_size=None,
          metadata_size=None, unreferenced_file_size=None):
    return measures.stack_of(file_size, deleted, footer_size, stale_column_size, non_live_file_size, metadata_size,
                             unreferenced_file_size)


# ---- 10.2 the share of each component ----

def test_10_2_each_of_the_seven_shares_is_the_component_over_data():
    # FileSize 1000, Deleted 0, FooterSize 40, StaleColumnSize 10, NonLiveFileSize 300, UnreferencedFileSize 200,
    # MetadataSize 50. Live = max(0, 1000 - 0 - 40 - 10) = 950. Listed: NonLive = min(300, 200) = 200, Unreferenced =
    # 200 - 200 = 0. Waste = 200 + 0 + 0 + 10 = 210. Data = 950 + 210 = 1160.
    result = stack(1000, 0, 40, 10, 300, 50, 200)
    assert result["data"] == 1160
    assert result["shares"] == {"live": 950 / 1160, "nonLive": 200 / 1160, "unreferenced": 0, "deleted": 0,
                                "stale": 10 / 1160, "metadata": 50 / 1160, "footers": 40 / 1160}


def test_10_2_the_share_of_an_absent_component_is_none():
    # Live 1000, NonLive 100 without a listing, every other component absent. Data 1100.
    result = stack(1000, non_live_file_size=100)
    assert result["shares"] == {"live": 1000 / 1100, "nonLive": 100 / 1100, "unreferenced": None, "deleted": None,
                                "stale": None, "metadata": None, "footers": None}


def test_10_2_the_shares_of_metadata_and_footers_can_exceed_1():
    # FileSize 100, MetadataSize 300: Live 100, Waste None, Data 100. 300 / 100 = 3, not clipped.
    result = stack(100, metadata_size=300)
    assert result["shares"]["metadata"] == 3
    assert result["shares"]["live"] == 1


@pytest.mark.parametrize("result", [
    # FileSize absent: Live and Data None.
    stack(None, 10, 5, 2, 7, 3),
    # Data 0: a zero denominator.
    stack(0, 0, 0, 0, 0, 30, 0),
    # Nothing at all.
    stack(),
])
def test_10_2_every_share_is_none_when_data_is_none_or_0(result):
    assert set(result["shares"]) == set(STACK_SHARES)
    assert all(value is None for value in result["shares"].values()), result["shares"]


def test_10_2_measure_tables_gives_the_shares_of_each_table():
    [table] = measures.measure_tables({"tables": columnar([
        {"id": 1, "name": "t", "fileSize": 1000, "rowCount": 100, "deletedRowCount": 0, "footerSize": 40,
         "staleColumnSize": 10, "nonLiveFileSize": 300, "unreferencedFileSize": 200, "metadataSize": 50}])}, None,
        COLLECTION)
    assert table["stack"]["shares"]["live"] == 950 / 1160
    assert table["stack"]["shares"]["footers"] == 40 / 1160


# ---- 10.3 the files of a table ----

# Table 1 keeps five files with a size, one without, and one with a negative size, which is absent; its FileSizes and
# VOrderedFileCount differ from the files, so that the form from the files shows. Table 2 keeps four files, none with
# IsVOrdered. Tables 3 to 6 keep no file.
FILE_TABLES = [
    {"id": 1, "name": "five_files", "detailDepth": "Files", "fileCount": 5, "fileSize": 150, "vOrderedFileCount": 5,
     "fileSizes": {"count": 99, "sum": 9999, "minimum": 1, "firstQuartile": 2, "median": 3, "thirdQuartile": 4,
                   "maximum": 5}},
    {"id": 2, "name": "four_files", "detailDepth": "Files", "fileCount": 4, "fileSize": 1000, "vOrderedFileCount": 3},
    {"id": 3, "name": "totals", "detailDepth": "Summary", "fileCount": 12, "fileSize": 90, "vOrderedFileCount": 15,
     "fileSizes": {"minimum": 5, "firstQuartile": 6, "median": 7, "thirdQuartile": 8, "maximum": 9}},
    {"id": 4, "name": "own_count", "fileCount": 99, "fileSize": 999, "fileSizes": {"count": 3, "sum": 60, "median": 20}},
    {"id": 5, "name": "zero", "fileCount": 0, "fileSize": 0, "vOrderedFileCount": 0},
    {"id": 6, "name": "negative", "fileCount": -3, "fileSize": -10, "vOrderedFileCount": 1,
     "fileSizes": {"count": -1, "sum": -2, "median": -5}},
]

FILE_ROWS = [{"id": index + 1, "table": table, "fileSize": size, "isVOrdered": v_ordered}
             for index, (table, size, v_ordered) in enumerate([
                 (1, 50, True), (1, 10, False), (1, 40, True), (1, 20, None), (1, 30, False), (1, None, None),
                 (1, -7, None), (2, 300, None), (2, 100, None), (2, 400, None), (2, 200, None)])]

FIGURES = ("count", "sum", "minimum", "firstQuartile", "median", "thirdQuartile", "maximum")


@pytest.fixture(scope="module")
def file_tables():
    return measures.measure_tables({"tables": columnar(FILE_TABLES)}, columnar(FILE_ROWS), COLLECTION)


def figures(table):
    return [table["files"][key] for key in FIGURES]


def test_10_3_the_files_dict_has_the_figures_and_v_order():
    [table] = measures.measure_tables({"tables": columnar([{"id": 1, "name": "t"}])}, None, COLLECTION)
    assert set(table["files"]) == {*FIGURES, "vOrder"}


def test_10_3_from_the_files_the_quartiles_are_taken_by_position(file_tables):
    # Table 1: the sizes 10, 20, 30, 40, 50 (the file without a size and the one of -7 are left out). n = 5: positions
    # floor(5/4) = 1, floor(5/2) = 2, floor(15/4) = 3. Count 5 and sum 150, not the 99 and 9999 of FileSizes.
    assert figures(file_tables[0]) == [5, 150, 10, 20, 30, 40, 50]
    # Table 2: 100, 200, 300, 400. n = 4: positions 1, 2, 3, so 200, 300, 400, not the interpolated 175, 250, 325.
    assert figures(file_tables[1]) == [4, 1000, 100, 200, 300, 400, 400]


def test_10_3_from_file_sizes_with_file_count_and_file_size_for_a_missing_count_and_sum(file_tables):
    # Table 3: FileSizes has no Count and no Sum: FileCount 12 and FileSize 90.
    assert figures(file_tables[2]) == [12, 90, 5, 6, 7, 8, 9]
    # Table 4: Count 3 and Sum 60 of FileSizes, not FileCount 99 and FileSize 999.
    assert figures(file_tables[3]) == [3, 60, None, None, 20, None, None]
    # Table 5: no FileSizes; FileCount 0 and FileSize 0 are values, the shape is None.
    assert figures(file_tables[4]) == [0, 0, None, None, None, None, None]
    # Table 6: every value negative, so absent.
    assert figures(file_tables[5]) == [None] * 7


def test_10_3_v_order_share_from_the_files_else_from_the_totals(file_tables):
    assert [table["files"]["vOrder"] for table in file_tables] == [
        # Table 1: IsVOrdered on 4 files, true on 2: 2 / 4. The bytes: (50 + 40) / (50 + 10 + 40 + 30). Not 5 / 5.
        {"share": 0.5, "bytesShare": 90 / 130},
        # Table 2: no file has IsVOrdered: 3 / 4 from the totals; VOrderBytesShare None.
        {"share": 0.75, "bytesShare": None},
        # Table 3: min(1, 15 / 12) = 1.
        {"share": 1, "bytesShare": None},
        # Table 4: no VOrderedFileCount. Table 5: FileCount 0. Table 6: FileCount negative, so absent.
        None, None, None,
    ]


def test_10_3_without_files_the_figures_come_from_the_totals_and_bytes_share_is_none():
    [table] = measures.measure_tables({"tables": columnar([FILE_TABLES[0]])}, None, COLLECTION)
    # The FileSizes of table 1 now, and min(1, 5 / 5) = 1.
    assert figures(table) == [99, 9999, 1, 2, 3, 4, 5]
    assert table["files"]["vOrder"] == {"share": 1, "bytesShare": None}


def test_10_3_a_file_with_is_v_ordered_and_no_file_size_counts_in_the_share_and_adds_no_bytes():
    # IsVOrdered is present on the three files, true on two: 2 / 3. The bytes: 100 / (100 + 300).
    [table] = measures.measure_tables(
        {"tables": columnar([{"id": 1, "name": "t", "detailDepth": "Files", "fileCount": 3}])},
        columnar([{"id": 1, "table": 1, "fileSize": 100, "isVOrdered": True},
                  {"id": 2, "table": 1, "fileSize": None, "isVOrdered": True},
                  {"id": 3, "table": 1, "fileSize": 300, "isVOrdered": False}]), COLLECTION)
    assert table["files"]["vOrder"] == {"share": 2 / 3, "bytesShare": 0.25}
    assert (table["files"]["count"], table["files"]["sum"]) == (2, 400)


# ---- 10.4 VACUUM and OPTIMIZE ----

HISTORY_TABLES = [{"id": 1, "name": "maintained"}, {"id": 2, "name": "written"}, {"id": 3, "name": "no_operation"},
                  {"id": 4, "name": "unnamed_only"}, {"id": 5, "name": "vacuum_untimed"}]

HISTORY_OPERATIONS = [{"table": table, "name": name, "commitCount": 1, "lastAt": last_at} for table, name, last_at in [
    (1, "WRITE", "2026-09-03T00:00:00.000Z"),
    (1, "VACUUM START", "2026-09-05T00:00:00.000Z"),
    (1, "vacuum end", "2026-09-07T00:00:00.000Z"),
    (1, "VACUUM END", "2026-09-06T00:00:00.000Z"),
    (1, "Optimize", "2026-09-04T00:00:00.000Z"),
    (1, "OPTIMIZE", None),
    (2, "WRITE", "2026-09-02T00:00:00.000Z"),
    (2, "MERGE", "2026-09-03T00:00:00.000Z"),
    (4, None, "2026-09-06T00:00:00.000Z"),
    (5, "VACUUM END", None),
]]


def test_10_4_has_vacuum_has_optimize_and_their_latest_times():
    tables = measures.measure_tables({"tables": columnar(HISTORY_TABLES), "operations": columnar(HISTORY_OPERATIONS)},
                                     None, COLLECTION)

    def of(table):
        m = table["maintenance"]
        return [m["hasVacuum"], instant(m["lastVacuumAt"]), m["hasOptimize"], instant(m["lastOptimizeAt"])]

    # The latest of 09-05, 09-07 ("vacuum end", lower case), and 09-06, whatever their order. OPTIMIZE without a
    # LastAt counts for HasOptimize and leaves LastOptimizeAt at the 09-04 of "Optimize".
    assert of(tables[0]) == [True, day(9, 7), True, day(9, 4)]
    # Operations, none of them a VACUUM or an OPTIMIZE: False.
    assert of(tables[1]) == [False, None, False, None]
    # No Operation for the table, and an operation without a Name: None.
    assert of(tables[2]) == [None, None, None, None]
    assert of(tables[3]) == [None, None, None, None]
    # A VACUUM without a LastAt: True, and no time.
    assert of(tables[4]) == [True, None, False, None]


def test_10_4_without_operations_the_four_keys_are_none():
    [table] = measures.measure_tables({"tables": columnar([{"id": 1, "name": "t"}])}, None, COLLECTION)
    assert table["maintenance"] == {"lastCommitAt": None, "commitsPerDay": None, "daysSinceLastCommit": None,
                                    "hasVacuum": None, "lastVacuumAt": None, "hasOptimize": None,
                                    "lastOptimizeAt": None}


# ---- 10.5 the partitions ----

PARTITION_TABLES = [
    {"id": 1, "name": "counted_and_kept", "partitionCount": 10}, {"id": 2, "name": "kept_only"},
    {"id": 3, "name": "counted_only", "partitionCount": 4}, {"id": 4, "name": "neither"},
    {"id": 5, "name": "negative_count", "partitionCount": -1}, {"id": 6, "name": "zero_count", "partitionCount": 0},
]

# The row of table 99 belongs to no table of the file.
PARTITION_ROWS = [{"id": index + 1, "table": table, "lastModifiedAt": modified}
                  for index, (table, modified) in enumerate([
                      (1, "2026-09-05T00:00:00.000Z"), (1, "2026-09-01T00:00:00.000Z"),
                      (1, "2026-09-03T00:00:00.000Z"), (2, "2026-09-02T00:00:00.000Z"), (2, None), (5, None),
                      (99, "2026-01-01T00:00:00.000Z")])]


def partitions_of(table):
    p = table["partitions"]
    assert set(p) == {"count", "kept", "oldestModifiedAt", "newestModifiedAt"}
    return [p["count"], p["kept"], instant(p["oldestModifiedAt"]), instant(p["newestModifiedAt"])]


def test_10_5_partitions_count_from_partition_count_else_from_the_partitions_kept():
    tables = measures.measure_tables({"tables": columnar(PARTITION_TABLES)}, None, COLLECTION,
                                     columnar(PARTITION_ROWS))
    assert [partitions_of(table) for table in tables] == [
        # PartitionCount 10, three rows kept, from 09-01 to 09-05.
        [10, 3, day(9, 1), day(9, 5)],
        # No PartitionCount: the two rows kept. One has no LastModifiedAt.
        [2, 2, day(9, 2), day(9, 2)],
        [4, 0, None, None],
        # Neither: None, and kept 0.
        [None, 0, None, None],
        # A negative PartitionCount is absent: the one row kept.
        [1, 1, None, None],
        # A PartitionCount of 0 is a value.
        [0, 0, None, None],
    ]


def test_10_5_without_the_partitions_table_kept_is_0():
    tables = measures.measure_tables({"tables": columnar(PARTITION_TABLES)}, None, COLLECTION)
    assert [partitions_of(table) for table in tables] == [
        [10, 0, None, None], [None, 0, None, None], [4, 0, None, None], [None, 0, None, None],
        [None, 0, None, None], [0, 0, None, None]]


def test_10_5_measure_reads_the_partitions_table(tmp_path):
    builder = pqax.Builder({"collectorApp": "pqax-tests", "collectorAppVersion": "0.0", "level": 1,
                            "completedAt": datetime(2026, 10, 11, tzinfo=timezone.utc)})
    table = builder.add_table(name="partitioned", format="Delta", detailDepth="Summary")
    builder.add_table(name="plain", format="Delta", detailDepth="Summary")
    builder.add_partition(table=table, lastModifiedAt=datetime(2026, 9, 1, tzinfo=timezone.utc))
    builder.add_partition(table=table, lastModifiedAt=datetime(2026, 9, 8, tzinfo=timezone.utc))
    path = tmp_path / "partitions.pqax"
    builder.write(str(path))
    with pqax.open(str(path)) as doc:
        assert doc.row_count("partitions") == 2
        result = measures.measure(doc)
    # No PartitionCount: the two partitions kept, from 09-01 to 09-08.
    assert [partitions_of(t) for t in result["tables"]] == [[2, 2, day(9, 1), day(9, 8)], [None, 0, None, None]]


# ---- 10.6 the measures of a column ----

# Table 1 read 4 footers, table 2 has no FooterCount, table 3 read none.
COLUMN_TABLES = [{"id": 1, "name": "a", "footerCount": 4}, {"id": 2, "name": "b"},
                 {"id": 3, "name": "c", "footerCount": 0}]

COLUMN_ROWS = [
    {"id": 1, "table": 1, "name": "full", "valueCount": 100, "nullCount": 25, "compressedSize": 400,
     "uncompressedSize": 1600, "chunkCount": 8, "dictionaryChunkCount": 6, "fallbackChunkCount": 3,
     "unencodedByteArrayDataBytes": 600, "fileCountWithoutColumn": 1},
    {"id": 2, "table": 1, "name": "more_nulls", "valueCount": 10, "nullCount": 15, "fileCountWithoutColumn": 0},
    {"id": 3, "table": 1, "name": "all_null", "valueCount": 20, "nullCount": 20, "unencodedByteArrayDataBytes": 0,
     "chunkCount": 0, "dictionaryChunkCount": 0, "fallbackChunkCount": 0, "compressedSize": 0, "uncompressedSize": 10},
    {"id": 4, "table": 2, "name": "no_footer_count", "valueCount": 0, "nullCount": 0, "fileCountWithoutColumn": 1},
    {"id": 5, "table": 3, "name": "zero_footers", "fileCountWithoutColumn": 0},
    {"id": 6, "table": 99, "name": "orphan", "valueCount": 10, "nullCount": 1, "fileCountWithoutColumn": 1},
    {"id": 7, "table": 1, "name": "negative", "valueCount": -10, "nullCount": 2, "compressedSize": -1,
     "uncompressedSize": 5, "chunkCount": 4, "dictionaryChunkCount": -2, "fileCountWithoutColumn": -1},
    {"id": 8, "table": 1},
    {"id": 1, "table": 1},
]

COLUMN_KEYS = ("id", "table", "name", "label", "nullShare", "compressionRatio", "dictionaryShare",
               "dictionaryFallbackShare", "averageValueLength", "filesWithoutColumnShare")


def test_10_6_the_measures_of_each_column():
    columns = measures.measure_columns({"tables": columnar(COLUMN_TABLES), "columns": columnar(COLUMN_ROWS)})
    assert all(set(column) == set(COLUMN_KEYS) for column in columns), columns
    assert [[column[key] for key in COLUMN_KEYS] for column in columns] == [
        # 25 / 100, 1600 / 400, 6 / 8, 3 / 6, 600 / (100 - 25), 1 / 4.
        [1, 1, "full", "full", 0.25, 4, 0.75, 0.5, 8, 0.25],
        # min(1, 15 / 10) = 1; 0 / 4 = 0.
        [2, 1, "more_nulls", "more_nulls", 1, None, None, None, None, 0],
        # 20 / 20 = 1. CompressedSize 0, ChunkCount 0, DictionaryChunkCount 0, ValueCount - NullCount 0: None.
        [3, 1, "all_null", "all_null", 1, None, None, None, None, None],
        # ValueCount 0: None. Its table has no FooterCount: None.
        [4, 2, "no_footer_count", "no_footer_count", None, None, None, None, None, None],
        # FooterCount 0: None.
        [5, 3, "zero_footers", "zero_footers", None, None, None, None, None, None],
        # A column of no table of the file: its table is None, 1 / 10, and no FooterCount to divide by.
        [6, None, "orphan", "orphan", 0.1, None, None, None, None, None],
        # Every negative value is absent.
        [7, 1, "negative", "negative", None, None, None, None, None, None],
        # No field: a partition column that the files do not store. The label names the id, then the row.
        [8, 1, None, "Column 8", None, None, None, None, None, None],
        [None, 1, None, "Column at row 9", None, None, None, None, None, None],
    ]


def test_10_6_a_summary_without_columns_has_none():
    assert measures.measure_columns({"tables": columnar(COLUMN_TABLES)}) == []
    assert measures.measure_columns({}) == []


# ---- 10.7 the measures of the collection ----

# Sources 1, 2, 2 again, and 0, which is no id. t1: Live 1000 - 0 - 50 - 0 = 950, Waste 100, Overhead 30 + 50 = 80,
# Total 1130. t2: Live 500, Total 500, Waste and Overhead None. t3: nothing, no DetailDepth. t4: FileCount and FileSize
# negative so absent, Live None, Overhead 20, Total 20, Waste None.
COLLECTION_SUMMARY = {
    "sources": columnar([{"id": 1, "kind": "Lakehouse"}, {"id": 2, "kind": "Lakehouse"},
                         {"id": 2, "kind": "Lakehouse"}, {"id": 0, "kind": "Lakehouse"}]),
    "tables": columnar([
        {"id": 1, "source": 1, "name": "t1", "detailDepth": "Files", "fileCount": 3, "fileSize": 1000, "rowCount": 10,
         "deletedRowCount": 0, "footerSize": 50, "staleColumnSize": 0, "nonLiveFileSize": 100, "metadataSize": 30},
        {"id": 2, "source": 2, "name": "t2", "detailDepth": "Summary", "fileCount": 2, "fileSize": 500},
        {"id": 3, "source": 1, "name": "t3"},
        {"id": 4, "source": 2, "name": "t4", "detailDepth": "Summary", "fileCount": -1, "fileSize": -5, "rowCount": 7,
         "metadataSize": 20},
    ]),
    "columns": columnar([{"id": 1, "table": 1, "name": "x"}, {"id": 2, "table": 2, "name": "y"},
                         {"id": 3, "table": 4, "name": "z"}]),
}


def collection_of(summary):
    return measures.measure_collection(summary, measures.measure_tables(summary, None, COLLECTION))


def test_10_7_the_totals_and_the_depths_of_the_collection():
    result = collection_of(COLLECTION_SUMMARY)
    assert result == {
        "totals": {
            "sources": 2, "tables": 4, "columns": 3,
            # 3 + 2; 1000 + 500; 10 + 7.
            "files": 5, "fileSize": 1500, "rowCount": 17,
            # 1130 + 500 + 20; 100; 80 + 20.
            "footprint": 1650, "waste": 100, "overhead": 100,
        },
        "depths": {"Files": 1, "Summary": 2, "Unknown": 1},
    }
    assert all(type(value) is int for value in result["totals"].values())


def test_10_7_every_sum_is_none_when_no_table_has_the_value():
    assert collection_of({"tables": columnar([{"id": 1, "name": "bare"}])}) == {
        "totals": {"sources": 0, "tables": 1, "columns": 0, "files": None, "fileSize": None, "rowCount": None,
                   "footprint": None, "waste": None, "overhead": None},
        "depths": {"Unknown": 1},
    }
    empty = collection_of({})
    assert empty["totals"]["tables"] == 0 and empty["totals"]["footprint"] is None
    assert empty["depths"] == {}


# ---- measure and python -m pqax tables ----

def test_measure_gives_the_columns_and_the_collection(tmp_path):
    builder = pqax.Builder({"collectorApp": "pqax-tests", "collectorAppVersion": "0.0", "level": 2})
    source = builder.add_source(kind="Lakehouse")
    table = builder.add_table(source=source, name="t", detailDepth="Summary", fileCount=2, fileSize=500, footerCount=4)
    builder.add_column(table=table, name="c", valueCount=10, nullCount=5, fileCountWithoutColumn=2)
    path = tmp_path / "columns.pqax"
    builder.write(str(path))
    with pqax.open(str(path)) as doc:
        result = measures.measure(doc)
    assert list(result) == ["filesRead", "warnings", "tables", "columns", "collection"]
    [column] = result["columns"]
    # 5 / 10 and 2 / 4.
    assert (column["label"], column["table"], column["nullShare"], column["filesWithoutColumnShare"]) == \
        ("c", table, 0.5, 0.5)
    assert result["collection"]["totals"] == {"sources": 1, "tables": 1, "columns": 1, "files": 2, "fileSize": 500,
                                              "rowCount": None, "footprint": 500, "waste": None, "overhead": None}
    assert result["collection"]["depths"] == {"Summary": 1}
    assert json.loads(json.dumps(result)) == result


TABLE_KEYS = ["id", "name", "label", "rowCount", "fileCount", "fileSize", "stack", "smallFiles", "tinyFiles",
              "measures", "maintenance"]
STACK_KEYS = ["live", "nonLive", "unreferenced", "deleted", "stale", "metadata", "footers", "deletedSource", "listed",
              "data", "total", "waste", "overhead", "wasteShare", "overheadShare"]
DOCUMENT_KEYS = ["file", "embeddedPart", "format", "formatVersion", "collector", "library", "collectedAt", "level",
                 "smallFileSize", "tinyFileSize", "sort", "top", "tableCount", "filesRead", "warnings", "tables"]


def test_tables_json_prints_only_the_keys_it_printed_before(tmp_path, capsys):
    builder = pqax.Builder({"collectorApp": "pqax-tests", "collectorAppVersion": "0.0", "level": 2,
                            "completedAt": datetime(2026, 10, 11, tzinfo=timezone.utc)})
    table = builder.add_table(name="t", detailDepth="Files", fileCount=2, fileSize=300, partitionCount=1,
                              vOrderedFileCount=1)
    builder.add_column(table=table, name="c", valueCount=10, nullCount=1)
    builder.add_operation(table=table, name="VACUUM END", commitCount=1,
                          lastAt=datetime(2026, 9, 1, tzinfo=timezone.utc))
    builder.add_partition(table=table, lastModifiedAt=datetime(2026, 9, 1, tzinfo=timezone.utc))
    builder.add_file(table=table, fileSize=100, isVOrdered=True)
    builder.add_file(table=table, fileSize=200, isVOrdered=False)
    path = tmp_path / "keys.pqax"
    builder.write(str(path))
    try:
        code = main(["tables", str(path), "--json"])
    except SystemExit as error:
        code = error.code
    out, err = capsys.readouterr()
    assert code == 0, out + err
    document = json.loads(out)
    assert list(document) == DOCUMENT_KEYS
    [printed] = document["tables"]
    assert list(printed) == TABLE_KEYS
    assert list(printed["stack"]) == STACK_KEYS
    assert list(printed["maintenance"]) == ["lastCommitAt", "commitsPerDay", "daysSinceLastCommit"]
