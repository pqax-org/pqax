"""The derived measures, pqax.measures: every valid sample of the kit against conformance/expected/measures, which the
views compute, and each function on synthetic values, as src/typescript/views/src/model.ts and the README of the views
define them."""
from __future__ import annotations

import json
import math
from datetime import datetime, timezone

import pytest

import pqax
from pqax import measures
from pqax_kit import EXPECTED, SAMPLES, expected_by_name, load_json

KIT = expected_by_name()
MEASURES = EXPECTED / "measures"
MIB = 1024 * 1024

# The relative tolerance of a float. 0: the measures must equal the views to the bit.
TOLERANCE = 0.0


def open_sample(name):
    expected = KIT[name]
    path = SAMPLES / expected["file"]
    part = expected["embeddedPart"]
    return pqax.open_embedded(path, part) if part else pqax.open(path)


def instant(text):
    return datetime.fromisoformat(text.replace("Z", "+00:00"))


def differences(actual, expected, path="", tolerance=TOLERANCE):
    """The differences between two JSON values: numbers exactly or within a relative tolerance, a time (a key that
    ends in At, such as lastCommitAt or newestModifiedAt) as an instant, an integral float as a fault, since the module
    returns it as an int."""
    found = []
    if isinstance(expected, dict):
        if not isinstance(actual, dict):
            return [f"{path}: got {actual!r}, expected an object"]
        for key in sorted(set(expected) | set(actual)):
            if key not in expected or key not in actual:
                found.append(f"{path}.{key}: present in only one of the two")
            else:
                found += differences(actual[key], expected[key], f"{path}.{key}", tolerance)
        return found
    if isinstance(expected, list):
        if not isinstance(actual, list) or len(actual) != len(expected):
            return [f"{path}: got {actual!r}, expected {expected!r}"]
        for index, (a, e) in enumerate(zip(actual, expected)):
            found += differences(a, e, f"{path}[{index}]", tolerance)
        return found
    if isinstance(actual, float) and actual.is_integer():
        found.append(f"{path}: {actual!r} is an integral float, not an int")
    if path.rsplit(".", 1)[-1].endswith("At") and isinstance(expected, str) and isinstance(actual, str):
        if instant(actual) != instant(expected):
            found.append(f"{path}: got {actual}, expected {expected}")
        return found
    numbers = (int, float)
    if isinstance(expected, numbers) and not isinstance(expected, bool):
        if not isinstance(actual, numbers) or isinstance(actual, bool):
            return [f"{path}: got {actual!r}, expected {expected!r}"]
        equal = actual == expected if tolerance == 0 else math.isclose(actual, expected, rel_tol=tolerance, abs_tol=0)
        if not equal:
            found.append(f"{path}: got {actual!r}, expected {expected!r}")
        return found
    if actual != expected or type(actual) is not type(expected):
        found.append(f"{path}: got {actual!r}, expected {expected!r}")
    return found


# ---- the kit ----

def test_every_valid_sample_has_its_expected_measures():
    assert sorted(path.stem for path in MEASURES.glob("*.json")) == sorted(KIT)


@pytest.mark.parametrize("name", sorted(KIT))
def test_the_measures_of_a_sample_are_those_of_the_views(name):
    expected = load_json(MEASURES / f"{name}.json")
    assert expected["file"] == KIT[name]["file"]
    assert expected["embeddedPart"] == KIT[name]["embeddedPart"]
    with open_sample(name) as doc:
        result = measures.measure(doc)
    assert result["filesRead"] == expected["filesRead"]
    assert len(result["warnings"]) == len(expected["warnings"])
    assert list(result) == ["filesRead", "warnings", "tables", "columns", "collection"]
    found = differences(result["tables"], expected["tables"], "tables")
    found += differences(result["columns"], expected["columns"], "columns")
    found += differences(result["collection"], expected["collection"], "collection")
    assert not found, "\n".join(found)


@pytest.mark.parametrize("name", sorted(KIT))
def test_measure_tables_columns_and_collection_give_the_same(name):
    expected = load_json(MEASURES / f"{name}.json")
    with open_sample(name) as doc:
        files = doc.table("files") if doc.row_count("files") > 0 else None
        partitions = doc.table("partitions") if doc.row_count("partitions") > 0 else None
        tables = measures.measure_tables(doc.summary, files, doc.collection, partitions)
        columns = measures.measure_columns(doc.summary)
        collection = measures.measure_collection(doc.summary, tables)
    found = differences(tables, expected["tables"], "tables")
    found += differences(columns, expected["columns"], "columns")
    found += differences(collection, expected["collection"], "collection")
    assert not found, "\n".join(found)


def test_measure_with_other_thresholds_counts_the_files_below_them():
    with open_sample("sample") as doc:
        result = measures.measure(doc, small_file_size=40000, tiny_file_size=2000)
    sales, orders = result["tables"]
    # The files of sales are 47820, 83049, and 28252 bytes, those of orders 38103 and 1781.
    assert sales["smallFiles"] == {"threshold": 40000, "count": 1, "shareAtLeast": 1 / 3}
    assert orders["smallFiles"] == {"threshold": 40000, "count": 2, "shareAtLeast": 1}
    assert orders["tinyFiles"] == {"threshold": 2000, "count": 1, "shareAtLeast": 0.5}
    assert sales["tinyFiles"] == {"threshold": 2000, "count": 0, "shareAtLeast": 0}


def test_measure_of_a_summary_gives_a_lower_bound_from_the_quartiles():
    with open_sample("summary-only") as doc:
        result = measures.measure(doc, small_file_size=40000)
    assert result["filesRead"] is False
    sales, orders = result["tables"]
    assert sales["smallFiles"] == {"threshold": 40000, "count": None, "shareAtLeast": 0.25}
    assert orders["smallFiles"] == {"threshold": 40000, "count": None, "shareAtLeast": 1}


@pytest.mark.parametrize("value", [0, -5])
def test_a_threshold_that_is_not_positive_takes_the_default(value):
    with open_sample("sample") as doc:
        result = measures.measure(doc, small_file_size=value, tiny_file_size=value)
    for table in result["tables"]:
        assert table["smallFiles"]["threshold"] == measures.DEFAULT_SMALL_FILE_SIZE
        assert table["tinyFiles"]["threshold"] == measures.DEFAULT_TINY_FILE_SIZE


def test_the_constants_are_those_of_the_views():
    assert measures.DEFAULT_SMALL_FILE_SIZE == 134217728
    assert measures.DEFAULT_TINY_FILE_SIZE == 8388608
    assert tuple(measures.STACK_COMPONENTS) == ("live", "nonLive", "unreferenced", "deleted", "stale", "metadata", "footers")
    assert tuple(measures.WASTE_COMPONENTS) == ("nonLive", "unreferenced", "deleted", "stale")
    assert tuple(measures.OVERHEAD_COMPONENTS) == ("metadata", "footers")
    import pqax.measures as module
    assert module is measures


# ---- one function each ----

@pytest.mark.parametrize("a, b, expected", [
    (1, 2, 0.5), (0, 5, 0), (1, 0, None), (3, -1, None), (None, 2, None), (1, None, None),
])
def test_ratio_is_none_without_a_positive_denominator(a, b, expected):
    assert measures.ratio(a, b) == expected


@pytest.mark.parametrize("file_size, deleted, rows, expected", [
    (100, 0, None, 0), (None, 0, None, 0), (100, 25, 100, 25), (100, 200, 100, 100), (None, 5, 10, None),
    (100, None, 10, None), (100, 5, None, None), (100, 5, 0, None), (100, 5, -1, None),
])
def test_deleted_estimate_takes_the_share_of_the_rows(file_size, deleted, rows, expected):
    assert measures.deleted_estimate(file_size, deleted, rows) == expected


def test_deleted_estimate_multiplies_before_it_divides():
    assert measures.deleted_estimate(159121, 25, 17000) == 159121 * 25 / 17000
    assert measures.deleted_estimate(47820, 25, 5000) == 47820 * 25 / 5000


def dist(count, minimum, first, median, third, maximum):
    return {"count": count, "minimum": minimum, "firstQuartile": first, "median": median, "thirdQuartile": third,
            "maximum": maximum}


@pytest.mark.parametrize("distribution, threshold, expected", [
    (dist(8, 10, 20, 30, 40, 50), 51, 1),
    (dist(8, 10, 20, 30, 40, 50), 50, 0.75),
    (dist(8, 10, 20, 30, 40, 50), 41, 0.75),
    (dist(8, 10, 20, 30, 40, 50), 31, 0.5),
    (dist(8, 10, 20, 30, 40, 50), 21, 0.25),
    (dist(8, 10, 20, 30, 40, 50), 11, 0.125),
    (dist(None, 10, 20, 30, 40, 50), 11, 0),
    (dist(0, 10, 20, 30, 40, 50), 11, 0),
    (dist(8, 10, 20, 30, 40, 50), 10, 0),
    (dist(8, None, None, None, None, None), 5, None),
    (dist(8, None, None, None, None, 50), 60, 1),
    (dist(8, None, None, None, None, 50), 40, 0),
    (dist(8, None, None, 30, None, None), 40, 0.5),
    (dist(4, 10, None, None, None, None), 40, 0.25),
])
def test_small_share_at_least_is_a_lower_bound_from_the_quartiles(distribution, threshold, expected):
    assert measures.small_share_at_least(distribution, threshold) == expected


def test_commits_per_day_divides_by_the_days_of_the_span_at_least_one():
    t0 = "2023-11-14T22:14:50.000Z"
    assert measures.commits_per_day(4, t0, "2023-11-15T22:14:50.000Z") == 4
    assert measures.commits_per_day(4, t0, "2023-11-16T22:14:50.000Z") == 2
    assert measures.commits_per_day(6, t0, "2023-11-16T10:14:50.000Z") == 4
    assert measures.commits_per_day(4, t0, "2023-11-14T22:44:50.000Z") == 4
    assert measures.commits_per_day(4, t0, "2023-11-11T22:14:50.000Z") == 4
    assert measures.commits_per_day(7, t0, "2023-11-19T02:14:50.000Z") == 7 / (100 / 24)
    assert measures.commits_per_day(None, t0, t0) is None
    assert measures.commits_per_day(4, None, t0) is None
    assert measures.commits_per_day(4, t0, None) is None
    assert measures.commits_per_day(4, "not a time", t0) is None


def test_days_between_counts_whole_days_and_never_less_than_0():
    t0 = "2023-11-14T22:14:50.000Z"
    assert measures.days_between(t0, "2026-10-05T20:55:12.749Z") == 1055
    assert measures.days_between(t0, "2023-11-15T22:14:49.999Z") == 0
    assert measures.days_between(t0, "2023-11-15T22:14:50.000Z") == 1
    assert measures.days_between(t0, "2023-11-11T00:00:00.000Z") == 0
    assert measures.days_between(None, t0) is None
    assert measures.days_between(t0, None) is None
    assert measures.days_between(t0, "garbage") is None


def stack(file_size=None, deleted=None, footer_size=None, stale_column_size=None, non_live_file_size=None,
          metadata_size=None, unreferenced_file_size=None, deleted_source=None):
    return measures.stack_of(file_size, deleted, footer_size, stale_column_size, non_live_file_size, metadata_size,
                             unreferenced_file_size=unreferenced_file_size, deleted_source=deleted_source)


def test_stack_of_draws_the_overlap_of_non_live_and_unreferenced_once():
    result = stack(1000, 100, 50, 20, 300, 30, unreferenced_file_size=200, deleted_source="files")
    assert result == {
        "live": 830, "nonLive": 200, "unreferenced": 0, "deleted": 100, "stale": 20, "metadata": 30, "footers": 50,
        "deletedSource": "files", "listed": True, "data": 1150, "total": 1230, "waste": 320, "overhead": 80,
        "wasteShare": 320 / 1150, "overheadShare": 80 / 1150,
        # Section 10.2: each component over Data, 1150.
        "shares": {"live": 830 / 1150, "nonLive": 200 / 1150, "unreferenced": 0, "deleted": 100 / 1150,
                   "stale": 20 / 1150, "metadata": 30 / 1150, "footers": 50 / 1150},
    }
    more = stack(1000, 0, 0, 0, 100, 0, unreferenced_file_size=250)
    assert (more["nonLive"], more["unreferenced"], more["waste"], more["total"]) == (100, 150, 250, 1250)


def test_stack_of_without_non_live_files_puts_every_unreferenced_byte_in_unreferenced():
    result = stack(1000, unreferenced_file_size=250)
    assert result["nonLive"] is None
    assert result["unreferenced"] == 250
    assert result["listed"] is True


def test_stack_of_without_unreferenced_files_keeps_the_upper_bound():
    result = stack(1000, 0, 10, 0, 300, 30)
    assert result["nonLive"] == 300
    assert result["unreferenced"] is None
    assert result["listed"] is False
    assert result["waste"] == 300
    assert result["total"] == 1000 + 30 + 300


def test_stack_of_clips_the_live_bytes_at_0():
    result = stack(100, 80, 50, 10)
    assert result["live"] == 0
    assert (result["waste"], result["data"], result["total"]) == (90, 90, 140)


def test_stack_of_has_no_shares_when_the_data_is_0():
    result = stack(0, 0, 0, 0, 0, 30, unreferenced_file_size=0)
    assert (result["data"], result["total"], result["waste"]) == (0, 30, 0)
    assert result["wasteShare"] is None
    assert result["overheadShare"] is None


def test_stack_of_counts_a_missing_component_as_0_and_keeps_it_none():
    result = stack()
    for key in measures.STACK_COMPONENTS:
        assert result[key] is None, key
    assert result["deletedSource"] is None
    assert result["listed"] is False
    # Section 10.2: with every component absent, the sums and the data are None.
    assert (result["data"], result["total"], result["waste"], result["overhead"]) == (None, None, None, None)
    assert result["wasteShare"] is None
    no_size = stack(None, 10, 5, 2, 7, 3)
    assert no_size["live"] is None
    assert (no_size["waste"], no_size["data"], no_size["total"], no_size["wasteShare"]) == (19, None, 27, None)


def test_stack_of_keeps_the_source_of_the_deleted_rows_only_with_the_deleted_rows():
    assert stack(100, 5, deleted_source="table")["deletedSource"] == "table"
    assert stack(100, None, deleted_source="table")["deletedSource"] is None


def test_an_integral_float_is_returned_as_an_int():
    result = stack(1000.0, 100.0, 50.0, 20.0, 300.0, 30.0, unreferenced_file_size=200.0)
    for key in ("live", "nonLive", "unreferenced", "data", "total", "waste", "overhead"):
        assert type(result[key]) is int, key


# ---- measure_tables on synthetic tables ----

def columnar(rows):
    keys = []
    for row in rows:
        keys += [key for key in row if key not in keys]
    return {key: [row.get(key) for row in rows] for key in keys}


def table(identifier, name="t", file_size=1000, row_count=100, deleted_row_count=10, **more):
    row = {"id": identifier, "name": name, "fileCount": 2, "fileSize": file_size, "rowCount": row_count,
           "deletedRowCount": deleted_row_count, "footerSize": 0, "staleColumnSize": 0}
    row.update(more)
    return row


def files(*rows):
    return columnar([{"id": index + 1, "table": t, "fileSize": size, "rowCount": rows_, "deletedRowCount": deleted}
                     for index, (t, size, rows_, deleted) in enumerate(rows)])


COLLECTION = {"startedAt": "2026-10-01T00:00:00.000Z", "completedAt": "2026-10-11T12:00:00.000Z", "level": 2}


def measure(rows, file_rows=None, collection=COLLECTION, operations=None, columns=None, **options):
    summary = {"tables": columnar(rows)}
    if operations is not None:
        summary["operations"] = columnar(operations)
    if columns is not None:
        summary["columns"] = columnar(columns)
    return measures.measure_tables(summary, file_rows, collection, **options)


def test_the_deleted_rows_come_from_the_files_when_every_file_with_a_size_gives_an_estimate():
    [result] = measure([table(1)], files((1, 600, 60, 6), (1, 400, 40, 0), (1, None, None, None), (2, 999, 10, 10)))
    assert result["stack"]["deleted"] == 60
    assert result["stack"]["deletedSource"] == "files"
    assert result["stack"]["live"] == 940


def test_a_file_with_a_size_and_no_estimate_sends_the_deleted_rows_back_to_the_table():
    [result] = measure([table(1)], files((1, 600, 60, 6), (1, 400, None, 4)))
    assert result["stack"]["deleted"] == 100
    assert result["stack"]["deletedSource"] == "table"


def test_a_file_with_no_deleted_row_gives_an_estimate_of_0_without_its_row_count():
    [result] = measure([table(1)], files((1, 600, 60, 6), (1, 400, None, 0)))
    assert (result["stack"]["deleted"], result["stack"]["deletedSource"]) == (60, "files")


def test_without_files_of_the_table_the_deleted_rows_come_from_the_table():
    [result] = measure([table(1)], files((2, 600, 60, 6)))
    assert (result["stack"]["deleted"], result["stack"]["deletedSource"]) == (100, "table")
    [result] = measure([table(1)])
    assert result["stack"]["deletedSource"] == "table"


def test_without_a_row_count_and_files_the_deleted_rows_are_none():
    [result] = measure([table(1, row_count=None)])
    assert result["stack"]["deleted"] is None
    assert result["stack"]["deletedSource"] is None
    assert result["stack"]["live"] == 1000


def test_a_repeated_id_is_none_for_the_second_row_and_the_files_go_to_the_first():
    first, second = measure([table(1, "first"), table(1, "second")], files((1, 600, 60, 30), (1, 400, 40, 0)))
    assert (first["id"], second["id"]) == (1, None)
    assert (first["label"], second["label"]) == ("first", "second")
    assert (first["stack"]["deleted"], first["stack"]["deletedSource"]) == (300, "files")
    assert (second["stack"]["deleted"], second["stack"]["deletedSource"]) == (100, "table")
    assert first["smallFiles"]["count"] == 2
    assert second["smallFiles"]["count"] is None


@pytest.mark.parametrize("identifier", [0, -3, 1.5, "1", None, True])
def test_an_id_that_is_not_a_positive_integer_is_none(identifier):
    [result] = measure([table(identifier)], files((identifier, 600, 60, 30)))
    assert result["id"] is None
    assert result["stack"]["deletedSource"] == "table"
    assert result["smallFiles"]["count"] is None


def test_the_label_joins_the_namespace_and_the_name_or_names_the_id_or_the_row():
    a, b, c = measure([{"id": 4, "namespace": ["dbo", "sub"], "name": "orders"}, {"id": 7}, {"id": 7}])
    assert (a["label"], a["name"]) == ("dbo.sub.orders", "orders")
    assert (b["label"], b["name"]) == ("Table 7", None)
    assert c["label"] == "Table at row 3"


def test_a_missing_input_gives_none_and_never_0():
    [result] = measure([{"id": 1, "name": "bare"}])
    assert (result["rowCount"], result["fileCount"], result["fileSize"]) == (None, None, None)
    for key in measures.STACK_COMPONENTS:
        assert result["stack"][key] is None, key
    assert result["stack"]["deletedSource"] is None
    assert result["stack"]["listed"] is False
    assert (result["stack"]["data"], result["stack"]["total"]) == (None, None)
    assert result["stack"]["wasteShare"] is None
    assert result["smallFiles"]["count"] is None and result["smallFiles"]["shareAtLeast"] is None
    assert result["tinyFiles"]["shareAtLeast"] is None
    assert all(value is None for value in result["measures"].values()), result["measures"]
    assert result["maintenance"] == {"lastCommitAt": None, "commitsPerDay": None, "daysSinceLastCommit": None,
                                     "hasVacuum": None, "lastVacuumAt": None, "hasOptimize": None,
                                     "lastOptimizeAt": None}
    assert all(value is None for value in result["stack"]["shares"].values()), result["stack"]["shares"]
    assert all(value is None for value in result["files"].values()), result["files"]
    assert result["partitions"] == {"count": None, "kept": 0, "oldestModifiedAt": None, "newestModifiedAt": None}


@pytest.mark.parametrize("value", [-5, "1000", True, float("nan"), float("inf"), [1000], {"a": 1}])
def test_a_size_of_the_wrong_type_or_negative_is_missing(value):
    [result] = measure([{"id": 1, "name": "t", "fileSize": value, "rowCount": value, "metadataSize": value}])
    assert result["fileSize"] is None
    assert result["rowCount"] is None
    assert result["stack"]["live"] is None
    assert result["stack"]["metadata"] is None


def test_the_files_give_the_exact_count_below_each_threshold():
    sizes = [1 * MIB, 8 * MIB, 10 * MIB, 128 * MIB, 200 * MIB]
    file_rows = files(*[(1, size, 1, 0) for size in sizes])
    [result] = measure([table(1)], file_rows)
    assert result["tinyFiles"] == {"threshold": measures.DEFAULT_TINY_FILE_SIZE, "count": 1, "shareAtLeast": 0.2}
    assert result["smallFiles"] == {"threshold": measures.DEFAULT_SMALL_FILE_SIZE, "count": 3, "shareAtLeast": 0.6}
    [other] = measure([table(1)], file_rows, small_file_size=300 * MIB, tiny_file_size=9 * MIB)
    assert other["smallFiles"]["count"] == 5 and other["smallFiles"]["shareAtLeast"] == 1
    assert other["tinyFiles"]["count"] == 2


def test_without_files_the_thresholds_use_the_quartiles_of_the_table():
    sizes = {"count": 4, "sum": 400 * MIB, "minimum": 1 * MIB, "firstQuartile": 4 * MIB, "median": 9 * MIB,
             "thirdQuartile": 100 * MIB, "maximum": 200 * MIB}
    [result] = measure([table(1, fileSizes=sizes)])
    assert result["tinyFiles"]["count"] is None and result["tinyFiles"]["shareAtLeast"] == 0.25
    assert result["smallFiles"]["count"] is None and result["smallFiles"]["shareAtLeast"] == 0.75


def test_a_table_of_format_0_1_has_no_unreferenced_files():
    [result] = measure([table(1, nonLiveFileSize=300, metadataSize=30)])
    assert result["stack"]["unreferenced"] is None
    assert result["stack"]["listed"] is False
    assert result["stack"]["nonLive"] == 300
    assert result["stack"]["waste"] == 400


def test_a_table_with_a_listing_splits_the_two_sets():
    [result] = measure([table(1, nonLiveFileSize=300, unreferencedFileSize=120)])
    assert result["stack"]["listed"] is True
    assert (result["stack"]["nonLive"], result["stack"]["unreferenced"]) == (120, 0)
    assert result["measures"]["nonLiveShare"] == 300 / 1300


def test_the_last_commit_is_the_snapshot_else_the_latest_operation_else_the_last_change():
    rows = [
        table(1, "a", snapshotTimestamp="2026-10-01T12:00:00.000Z", lastModifiedAt="2026-09-01T00:00:00.000Z",
              commitCount=10, commitsRead=4, oldestCommitAt="2026-09-29T12:00:00.000Z"),
        table(2, "b", lastModifiedAt="2026-09-01T00:00:00.000Z", commitCount=6, oldestCommitAt="2026-09-20T00:00:00.000Z"),
        table(3, "c", lastModifiedAt="2026-09-01T00:00:00.000Z"),
    ]
    operations = [
        {"table": 2, "name": "WRITE", "commitCount": 3, "lastAt": "2026-09-25T00:00:00.000Z"},
        {"table": 2, "name": "OPTIMIZE", "commitCount": 1, "lastAt": "2026-09-26T00:00:00.000Z"},
        {"table": 2, "name": "DELETE", "commitCount": 2, "lastAt": "2026-09-22T00:00:00.000Z"},
    ]
    a, b, c = measure(rows, operations=operations)
    assert instant(a["maintenance"]["lastCommitAt"]) == datetime(2026, 10, 1, 12, tzinfo=timezone.utc)
    assert (a["maintenance"]["commitsPerDay"], a["maintenance"]["daysSinceLastCommit"]) == (2, 10)
    assert instant(b["maintenance"]["lastCommitAt"]) == datetime(2026, 9, 26, tzinfo=timezone.utc)
    # Section 10.4: the commits per day divide commitsRead only, which this table lacks; commitCount is not a fallback.
    assert (b["maintenance"]["commitsPerDay"], b["maintenance"]["daysSinceLastCommit"]) == (None, 15)
    assert instant(c["maintenance"]["lastCommitAt"]) == datetime(2026, 9, 1, tzinfo=timezone.utc)
    assert (c["maintenance"]["commitsPerDay"], c["maintenance"]["daysSinceLastCommit"]) == (None, 40)


def test_the_days_since_the_last_commit_count_to_the_start_without_an_end():
    row = table(1, snapshotTimestamp="2026-10-01T12:00:00.000Z")
    [started] = measure([row], collection={"startedAt": "2026-10-04T00:00:00.000Z", "level": 2})
    assert started["maintenance"]["daysSinceLastCommit"] == 2
    [neither] = measure([row], collection={"level": 2})
    assert neither["maintenance"]["daysSinceLastCommit"] is None
    [none] = measure([row], collection=None)
    assert none["maintenance"]["daysSinceLastCommit"] is None


def test_the_measures_of_a_table_follow_the_views():
    full = table(1, "full", 1200, 1000, 50, fileCount=4, compressedSize=1000, uncompressedSize=2500,
                 staleColumnSize=100, nonLiveFileSize=300, footerCount=4, rowGroupCount=8, partitionCount=3,
                 rowGroupRowCounts={"count": 5, "sum": 1000}, partitionSizes={"count": 2, "sum": 1200})
    fallback = table(2, "fallback", 1200, 1000, 0, fileCount=4, footerCount=4, rowGroupCount=8, partitionCount=3)
    partial = table(3, "partial", fileCount=4, footerCount=3, rowGroupCount=8)
    columns = [
        {"id": 1, "table": 1, "name": "a", "dictionaryChunkCount": 10, "fallbackChunkCount": 1},
        {"id": 2, "table": 1, "name": "b", "dictionaryChunkCount": 6, "fallbackChunkCount": 3},
        {"id": 3, "table": 1, "name": "c", "dictionaryChunkCount": 100},
        {"id": 4, "table": 2, "name": "d", "dictionaryChunkCount": 0, "fallbackChunkCount": 0},
    ]
    a, b, c = measure([full, fallback, partial], columns=columns)
    assert a["measures"] == {
        "compressionRatio": 2.5, "deletedRowShare": 0.05, "averageFileSize": 300, "averageRowGroupRows": 200,
        "footerCoverage": 1, "staleColumnShare": 0.1, "nonLiveShare": 300 / 1500, "averagePartitionSize": 600,
        "dictionaryFallbackShare": 4 / 16,
    }
    assert b["measures"]["averageRowGroupRows"] == 125
    assert b["measures"]["averagePartitionSize"] == 400
    assert b["measures"]["dictionaryFallbackShare"] is None
    assert b["measures"]["compressionRatio"] is None
    assert b["measures"]["deletedRowShare"] == 0
    assert c["measures"]["averageRowGroupRows"] is None
    assert c["measures"]["footerCoverage"] == 0.75


def test_the_result_is_json():
    with open_sample("sample") as doc:
        result = measures.measure(doc)
    assert json.loads(json.dumps(result)) == result
