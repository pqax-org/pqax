"""The derived measures of section 10 of docs/object-model.md that pqax.measures offers, on footprints built by hand:
the storage stack when a component is absent (10.2) and the pace of the commits (10.4), and python -m pqax tables on a
table without any field of the stack. The expected values come from the text of section 10, not from the code. A
measure that needs an absent field is None, never 0: the principle "Unknown is not zero" of section 2 and rule 2 of
10.1."""
from __future__ import annotations

import json
import re
import zipfile

import pytest

from pqax import measures
from pqax.__main__ import main


def stack(file_size=None, deleted=None, footer_size=None, stale_column_size=None, non_live_file_size=None,
          metadata_size=None, unreferenced_file_size=None):
    return measures.stack_of(file_size, deleted, footer_size, stale_column_size, non_live_file_size, metadata_size,
                             unreferenced_file_size)


def columnar(rows):
    keys = []
    for row in rows:
        keys += [key for key in row if key not in keys]
    return {key: [row.get(key) for row in rows] for key in keys}


COLLECTION = {"startedAt": "2026-10-01T00:00:00.000Z", "completedAt": "2026-10-11T12:00:00.000Z", "level": 2}


def measure(rows, collection=COLLECTION):
    return measures.measure_tables({"tables": columnar(rows)}, None, collection)


# ---- 10.2 the storage stack ----

def test_10_2_data_is_none_when_live_is_none_and_so_are_the_shares_while_the_sums_add_what_is_present():
    # FileSize is absent, so Live is None. NonLive 7, Unreferenced absent, Deleted 10, Stale 2: Waste 19.
    # Metadata 3, Footers 5: Overhead 8. Total: the seven components, the absent ones as 0, 27.
    result = stack(None, 10, 5, 2, 7, 3)
    assert result["live"] is None
    assert result["data"] is None
    assert result["waste"] == 19
    assert result["overhead"] == 8
    assert result["total"] == 27
    assert result["wasteShare"] is None
    assert result["overheadShare"] is None


def test_10_2_waste_overhead_and_total_are_none_when_all_their_components_are_absent():
    result = stack()
    assert all(result[key] is None for key in measures.STACK_COMPONENTS)
    for key in ("waste", "overhead", "total", "data", "wasteShare", "overheadShare"):
        assert result[key] is None, key


def test_10_2_total_with_only_metadata_size_is_metadata_size_and_waste_is_none():
    result = stack(metadata_size=30)
    assert result["total"] == 30
    assert result["overhead"] == 30
    assert result["waste"] is None
    assert result["data"] is None


def test_10_2_waste_with_only_stale_column_size_is_stale_column_size_and_overhead_is_none():
    result = stack(stale_column_size=20)
    assert result["waste"] == 20
    assert result["overhead"] is None
    assert result["total"] == 20
    assert result["data"] is None


def test_10_2_waste_and_overhead_sum_the_present_components_an_absent_one_counting_as_0():
    # NonLive 100, Unreferenced absent (no listing), Deleted absent, Stale 20: Waste 120. Live 980, Data 1100.
    result = stack(1000, stale_column_size=20, non_live_file_size=100)
    assert result["waste"] == 120
    assert result["data"] == 1100
    assert result["total"] == 1100
    assert result["overhead"] is None
    assert stack(1000, footer_size=40)["overhead"] == 40
    assert stack(1000, metadata_size=25)["overhead"] == 25
    # A present zero is a value: the overhead is 0, not None.
    assert stack(1000, metadata_size=0)["overhead"] == 0


def test_10_2_waste_share_and_overhead_share_are_none_when_waste_or_overhead_is_none_even_when_data_is_known():
    # Only FileSize: Live 1000, Waste and Overhead None. Data = Live + Waste is None only when Live is None.
    result = stack(1000)
    assert result["live"] == 1000
    assert result["waste"] is None
    assert result["overhead"] is None
    assert result["total"] == 1000
    assert result["data"] == 1000
    # 10.1 rule 2: a measure is None when a field that it needs is absent.
    assert result["wasteShare"] is None
    assert result["overheadShare"] is None


def test_10_2_the_shares_are_none_when_data_is_0():
    result = stack(0, 0, 0, 0, 0, 30, 0)
    assert result["data"] == 0
    assert result["total"] == 30
    assert result["wasteShare"] is None
    assert result["overheadShare"] is None


def test_10_2_a_table_without_file_size_has_a_none_data_and_none_shares():
    no_file_size, bare = measure([
        {"id": 1, "name": "no_file_size", "rowCount": 100, "deletedRowCount": 0, "footerSize": 5, "staleColumnSize": 2,
         "nonLiveFileSize": 7, "metadataSize": 3},
        {"id": 2, "name": "bare"},
    ])
    result = no_file_size["stack"]
    # Deleted is 0 since DeletedRowCount is 0, whatever the other fields.
    assert result["deleted"] == 0
    assert result["live"] is None
    assert result["data"] is None
    assert result["waste"] == 7 + 0 + 2
    assert result["overhead"] == 3 + 5
    assert result["total"] == 9 + 8
    assert result["wasteShare"] is None
    assert result["overheadShare"] is None
    for key in ("data", "waste", "overhead", "total", "wasteShare", "overheadShare"):
        assert bare["stack"][key] is None, key


# ---- 10.4 the history of a table ----

def test_10_4_commits_per_day_is_commits_read_over_the_days_of_the_span_at_least_one_and_never_commit_count():
    rows = [
        {"id": 1, "name": "ten_days", "commitsRead": 20, "commitCount": 100,
         "oldestCommitAt": "2026-09-01T00:00:00.000Z", "snapshotTimestamp": "2026-09-11T00:00:00.000Z"},
        {"id": 2, "name": "count_only", "commitCount": 100,
         "oldestCommitAt": "2026-09-01T00:00:00.000Z", "snapshotTimestamp": "2026-09-11T00:00:00.000Z"},
        {"id": 3, "name": "six_hours", "commitsRead": 5, "commitCount": 100,
         "oldestCommitAt": "2026-09-01T00:00:00.000Z", "snapshotTimestamp": "2026-09-01T06:00:00.000Z"},
        {"id": 4, "name": "a_day_and_a_half", "commitsRead": 6,
         "oldestCommitAt": "2026-09-01T00:00:00.000Z", "snapshotTimestamp": "2026-09-02T12:00:00.000Z"},
        {"id": 5, "name": "no_oldest", "commitsRead": 20, "snapshotTimestamp": "2026-09-11T00:00:00.000Z"},
    ]
    rates = [table["maintenance"]["commitsPerDay"] for table in measure(rows)]
    # 20 over 10 days; no CommitsRead, although CommitCount is present; 5 in 6 hours, a span shorter than a day
    # counting as one day; 6 over 1.5 days, the days as a fraction; no OldestCommitAt.
    assert rates == [2, None, 5, 4, None]


@pytest.mark.parametrize("collection, days", [
    # floor(10 days) to CompletedAt.
    ({"startedAt": "2026-10-04T00:00:00.000Z", "completedAt": "2026-10-11T12:00:00.000Z"}, 10),
    # floor(2.5 days) to StartedAt, when CompletedAt is absent or does not parse.
    ({"startedAt": "2026-10-04T00:00:00.000Z"}, 2),
    ({"startedAt": "2026-10-04T00:00:00.000Z", "completedAt": None}, 2),
    ({"startedAt": "2026-10-04T00:00:00.000Z", "completedAt": "not a date"}, 2),
    # Neither: None.
    ({"level": 1}, None),
    ({"completedAt": "not a date"}, None),
    (None, None),
    # max(0, ...): a collection that ends before the last commit gives 0.
    ({"completedAt": "2026-09-01T00:00:00.000Z"}, 0),
])
def test_10_4_days_since_last_commit_count_to_completed_at_else_to_started_at(collection, days):
    [table] = measure([{"id": 1, "name": "t", "snapshotTimestamp": "2026-10-01T12:00:00.000Z"}], collection)
    assert table["maintenance"]["daysSinceLastCommit"] == days


# ---- python -m pqax tables, 10.2 ----

def write_footprint(path, rows):
    manifest = {"format": "pqax", "formatVersion": "0.2",
                "collection": {"collectorApp": "pqax-tests", "collectorAppVersion": "0.0",
                               "completedAt": "2026-10-11T00:00:00.000Z", "level": 2},
                "parts": [{"name": "summary.json", "tables": {"tables": len(rows)}}]}
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("manifest.json", json.dumps(manifest))
        archive.writestr("summary.json", json.dumps({"tables": columnar(rows)}))
    return path


@pytest.fixture
def bare_and_full(tmp_path):
    """bare has no FileSize and no field of the stack; full has a Waste of 100 (NonLiveFileSize, no listing), a Total of
    1100, and a WasteShare of 100 / 1100."""
    return write_footprint(tmp_path / "section10.pqax", [
        {"id": 1, "name": "bare"},
        {"id": 2, "name": "full", "fileSize": 1000, "nonLiveFileSize": 100, "metadataSize": 0, "rowCount": 10,
         "deletedRowCount": 0, "fileCount": 1},
    ])


def run_tables(capsys, *arguments):
    try:
        code = main(["tables", *map(str, arguments)])
    except SystemExit as error:
        code = error.code
    out, err = capsys.readouterr()
    return code, out, err


def test_10_2_tables_json_writes_null_for_the_waste_and_the_waste_share_of_a_table_without_them(bare_and_full, capsys):
    code, out, err = run_tables(capsys, bare_and_full, "--json")
    assert code == 0, out + err
    tables = json.loads(out)["tables"]
    # 10.2: the tables go by Waste, and a None Waste after the others.
    assert [table["name"] for table in tables] == ["full", "bare"]
    bare = tables[1]["stack"]
    for key in (*measures.STACK_COMPONENTS, "data", "total", "waste", "overhead", "wasteShare", "overheadShare"):
        assert key in bare and bare[key] is None, key
    full = tables[0]["stack"]
    assert (full["waste"], full["total"], full["overhead"]) == (100, 1100, 0)
    assert full["wasteShare"] == 100 / 1100


def test_10_2_tables_text_writes_n_a_for_the_waste_and_the_waste_share_of_a_table_without_them(bare_and_full, capsys):
    code, out, err = run_tables(capsys, bare_and_full, "--wide")
    assert code == 0, out + err
    lines = out.rstrip("\n").split("\n")
    assert len(lines) == 4
    split = lambda line: re.split(r" {2,}", line.strip())  # noqa: E731
    header = split(lines[1])
    rows = {cells[0]: cells for cells in map(split, lines[2:])}
    assert [split(line)[0] for line in lines[2:]] == ["full", "bare"]
    bare = rows["bare"]
    assert len(bare) == len(header)
    for column in ("Live", "Waste", "Waste share", "Metadata", "Footers"):
        assert bare[header.index(column)] == "n/a", column
    assert rows["full"][header.index("Waste")] == "100 B"
    assert rows["full"][header.index("Waste share")] == "9.1%"
