"""The Delta log as pqax.collect reads it, on synthetic logs written into tmp_path in the OneLake layout (README of the
C# collector: What it reads, Commits, Unreferenced files, Issues). The checkpoints of these logs are read by a test
reader, StateCheckpointReader, or are JSON, which the collector reads itself, so everything here runs on the standard
library.
"""
from __future__ import annotations

import json

import pytest

from collect_kit import (EPOCH, TABLE_ID, Lake, RecordingStorage, SizelessStorage,
                         StateCheckpointReader, add, all_text, assert_valid, checkpoint_name, collect_bytes,
                         commit_info, commit_name, deletion_vector, discover_all, metadata, of_table, open_bytes,
                         partitioned_metadata, protocol, remove, rows, time_of, timestamp)
from pqax.collect import IssueCodes, LocalStorage, SourceKinds, StorageError, TableLocation

TABLE = "Synth.Lakehouse/Tables/events"
LOG = TABLE + "/_delta_log"
DEFAULT_READER = object()


class Run:
    """A collection of a lake: its footprint, the row of its one table (or of the table named), and its issues."""

    def __init__(self, data, builder, storage, reader, name=None):
        self.data = data
        self.builder = builder
        self.storage = storage
        self.reader = reader
        self.doc = open_bytes(data)
        tables = rows(self.doc, "tables")
        if name is None:
            assert len(tables) == 1, [table["name"] for table in tables]
            self.table = tables[0]
        else:
            [self.table] = [table for table in tables if table["name"] == name]

    @property
    def issues(self):
        return of_table(self.doc, "issues", self.table["id"])

    @property
    def codes(self):
        return [issue["code"] for issue in self.issues]

    @property
    def files(self):
        return of_table(self.doc, "files", self.table["id"])

    @property
    def commits(self):
        return of_table(self.doc, "commits", self.table["id"])

    def file(self, path):
        [found] = [file for file in self.files if file["path"] == path]
        return found


def run(storage, *, reader=DEFAULT_READER, tables=None, name=None, valid=True, **options):
    """Collects a storage with a StateCheckpointReader, or the reader given (None for no reader)."""
    if reader is DEFAULT_READER:
        reader = StateCheckpointReader()
    data, builder = collect_bytes(storage, tables, checkpoint_reader=reader, **options)
    if valid:
        assert_valid(data)
    return Run(data, builder, storage, reader, name)


def local(lake):
    return LocalStorage(str(lake.root))


def walked(lake, **kwargs):
    """A storage that the collector lists one folder at a time."""
    return RecordingStorage(LocalStorage(str(lake.root)), **kwargs)


def first(*actions):
    """The actions of version 0: the protocol, the metadata, the commit, and the actions given."""
    return (protocol(), metadata(), commit_info(0), *actions)


def later(version, *actions):
    return (commit_info(version), *actions)


# ---- the commits that are read ----

def history(lake, latest, checkpoint, oldest=0):
    for version in range(oldest, latest + 1):
        if version == 0:
            lake.commit(TABLE, 0, protocol(), metadata(), commit_info(0))
        else:
            lake.commit(TABLE, version, commit_info(version))
    if checkpoint is not None:
        lake.checkpoint(TABLE, checkpoint)
    return lake


@pytest.fixture(scope="module")
def lake_145(tmp_path_factory):
    """A table whose log keeps the commits 0 to 145, with a checkpoint at 100."""
    return history(Lake(tmp_path_factory.mktemp("lake145")), 145, 100)


def commit_version(path):
    name = path[len(LOG) + 1:] if path.startswith(LOG + "/") else ""
    if len(name) == 25 and name.endswith(".json") and name[:20].isdigit():
        return int(name[:20])
    return None


def requested(storage):
    return [version for version in (commit_version(request.path) for request in storage.reads()) if version is not None]


def assert_read(result, versions, failed=()):
    """The collection requested each of these commit files once, and every one read without a failure is in commitsRead,
    the commits, the operations, and oldestCommitAt."""
    assert sorted(requested(result.storage)) == list(versions)
    read = [version for version in versions if version not in failed]
    assert result.table["commitsRead"] == len(read)
    commits = result.commits
    assert sorted(int(commit["commitId"]) for commit in commits) == read
    for commit in commits:
        assert commit["timestamp"] == timestamp(time_of(int(commit["commitId"])))
        assert commit["operation"] == "WRITE"
        assert commit["engine"] == "Test engine"
    [operation] = of_table(result.doc, "operations", result.table["id"])
    assert operation["name"] == "WRITE"
    assert operation["commitCount"] == len(read)
    assert operation["lastAt"] == timestamp(time_of(max(read)))
    assert result.table["oldestCommitAt"] == timestamp(time_of(min(read)))


def assert_complete(result, latest, checkpoint, kept):
    table = result.table
    assert table["snapshotId"] == str(latest)
    assert table["snapshotTimestamp"] == timestamp(time_of(latest))
    assert table["commitCount"] == kept
    assert table["fileCount"] == 0
    assert table["delta"].get("checkpointVersion") == checkpoint
    assert table["delta"].get("commitsAfterCheckpoint") == (None if checkpoint is None else latest - checkpoint)
    assert result.reader.paths == ([] if checkpoint is None else [f"{LOG}/{checkpoint_name(checkpoint)}"])
    assert result.issues == []


@pytest.mark.parametrize("limit", [30, 0, 45])
def test_a_limit_up_to_the_commits_after_the_checkpoint_reads_those_commits_and_no_older_one(lake_145, limit):
    result = run(walked(lake_145), max_commits=limit)
    assert_read(result, range(101, 146))
    assert result.table["commitsRead"] == 45
    assert_complete(result, 145, 100, 146)


def test_a_limit_above_the_commits_after_the_checkpoint_also_reads_older_commits_the_most_recent_first(lake_145):
    result = run(walked(lake_145), max_commits=50)
    assert_read(result, range(96, 146))
    assert result.table["commitsRead"] == 50
    assert_complete(result, 145, 100, 146)


def test_the_older_commits_stop_where_the_log_ends_before_the_limit(tmp_path):
    lake = history(Lake(tmp_path), 145, 100, oldest=90)
    result = run(walked(lake), max_commits=60)
    assert_read(result, range(90, 146))
    assert result.table["commitsRead"] == 56
    assert_complete(result, 145, 100, 56)


@pytest.fixture(scope="module")
def lake_39(tmp_path_factory):
    return history(Lake(tmp_path_factory.mktemp("lake39")), 39, None)


@pytest.mark.parametrize("limit", [0, 1, 30, 40, 50])
def test_without_a_checkpoint_every_commit_is_read_whatever_the_limit(lake_39, limit):
    result = run(walked(lake_39), max_commits=limit)
    assert_read(result, range(0, 40))
    assert result.table["commitsRead"] == 40
    assert_complete(result, 39, None, 40)


def test_a_checkpoint_that_is_not_read_with_every_commit_kept_makes_the_state_need_them_all(lake_145):
    result = run(walked(lake_145), reader=None, max_commits=30)
    assert sorted(requested(result.storage)) == list(range(0, 146))
    assert result.table["commitsRead"] == 146
    assert result.table["snapshotId"] == "145"
    assert result.table["fileCount"] == 0
    assert result.issues == []


@pytest.mark.parametrize("failure, code", [("denied", "AccessDenied"), ("not JSON", "DeltaCommitNotRead")])
def test_a_commit_whose_read_fails_counts_toward_the_limit_and_gets_an_issue_and_no_row(tmp_path, failure, code):
    lake = history(Lake(tmp_path), 145, 100)
    failed = f"{LOG}/{commit_name(98)}"
    if failure == "not JSON":
        lake.write(failed, "this line is not JSON\n")

    def fail(kind, path):
        return StorageError("Access was denied.", 403) if failure == "denied" and kind == "read" and path == failed else None

    result = run(walked(lake, fail=fail), max_commits=50)
    assert_read(result, range(96, 146), failed=[98])
    assert len(requested(result.storage)) == 50
    assert result.table["commitsRead"] == 49
    [issue] = result.issues
    assert issue["code"] == code
    assert issue["path"] == "_delta_log/" + commit_name(98)
    assert issue["message"]
    assert result.table["snapshotId"] == "145"
    assert result.table["fileCount"] == 0


# ---- the state of the log ----

def test_a_log_with_every_commit_from_0_is_replayed_without_a_checkpoint_reader(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1), add("b.parquet", 20, 2)))
    lake.commit(TABLE, 1, *later(1, add("c.parquet", 30, 3), remove("a.parquet", 10, time_of(1))))
    lake.commit(TABLE, 2, *later(2, add("d.parquet", 40, 4)))
    result = run(local(lake), reader=None, level=1)
    assert result.issues == []
    assert result.table["fileCount"] == 3
    assert result.table["fileSize"] == 90
    assert result.table["rowCount"] == 9
    assert result.table["nonLiveFileCount"] == 1
    assert result.table["detailDepth"] == "Files"
    assert sorted(file["path"] for file in result.files) == ["b.parquet", "c.parquet", "d.parquet"]


def test_a_log_without_its_first_commit_and_no_checkpoint_is_incomplete(tmp_path):
    lake = Lake(tmp_path)
    for version in range(1, 4):
        lake.commit(TABLE, version, *later(version, add(f"f{version}.parquet", 10, 1)))
    result = run(local(lake), level=1)
    assert "DeltaLogIncomplete" in result.codes
    assert result.table.get("fileCount") is None
    assert result.table.get("fileSize") is None
    assert result.table["detailDepth"] == "Summary"
    assert result.reader.paths == []


def test_a_gap_after_the_checkpoint_is_incomplete(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first())
    for version in (1, 2, 3, 5, 6):
        lake.commit(TABLE, version, *later(version, add(f"f{version}.parquet", 10, 1)))
    lake.checkpoint(TABLE, 2)
    result = run(local(lake), level=1)
    assert "DeltaLogIncomplete" in result.codes
    assert result.reader.paths == [f"{LOG}/{checkpoint_name(2)}"]


def test_a_last_checkpoint_that_names_a_missing_checkpoint_gets_an_issue_and_the_commits_give_the_state(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1)))
    for version in range(1, 5):
        lake.commit(TABLE, version, *later(version, add(f"f{version}.parquet", 10, 1)))
    lake.write(f"{LOG}/_last_checkpoint", json.dumps({"version": 3, "size": 4}))
    result = run(local(lake), level=1)
    assert result.codes == ["DeltaCheckpointMissing"]
    assert result.table["fileCount"] == 5
    assert result.table["snapshotId"] == "4"
    assert result.reader.paths == []


def test_a_commit_that_is_not_json_gets_an_issue_with_its_path(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1)))
    lake.write(f"{LOG}/{commit_name(1)}", '{"commitInfo": {"timestamp": 1\n')
    lake.commit(TABLE, 2, *later(2, add("b.parquet", 10, 1)))
    result = run(local(lake), level=1)
    issues = [issue for issue in result.issues if issue["code"] == "DeltaCommitNotRead"]
    assert [issue["path"] for issue in issues] == ["_delta_log/" + commit_name(1)]


def test_statistics_that_are_not_json_get_an_issue_and_leave_the_rows_unknown(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1), add("b.parquet", 20, None, stats='{"numRecords": 5,')))
    result = run(local(lake), level=1)
    assert "DeltaStatsNotParsed" in result.codes
    assert result.file("a.parquet")["rowCount"] == 1
    assert result.file("b.parquet").get("rowCount") is None
    assert result.table.get("rowCount") is None
    assert result.table["fileCount"] == 2


def test_a_schema_string_that_is_not_json_gets_an_issue(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, protocol(), metadata(schema_string="{not a schema"), commit_info(0), add("a.parquet", 10, 1))
    result = run(local(lake), level=1)
    assert "DeltaSchemaNotParsed" in result.codes
    assert of_table(result.doc, "columns", result.table["id"]) == []
    assert result.table["fileCount"] == 1


def test_a_table_folder_without_a_log_gets_an_issue_and_its_own_source(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1)))
    lake.data("Synth.Lakehouse/Tables/no_log", "x.parquet", 10)
    storage = local(lake)
    tables = discover_all(storage)
    assert [table.name for table in tables] == ["events"]  # a folder without _delta_log is not a table
    known = tables[0]
    tables.append(TableLocation(known.source_kind, known.source_name, (), "no_log", "Synth.Lakehouse/Tables/no_log"))
    result = run(storage, tables=tables, name="no_log", level=1)
    assert result.codes == ["DeltaLogMissing"]
    assert result.table["detailDepth"] == "Summary"
    assert result.table.get("location") is None
    events = Run(result.data, None, storage, None, "events")
    assert events.issues == []
    assert events.table["fileCount"] == 1
    sources = rows(result.doc, "sources")
    assert len(sources) == 2  # the location made by hand has no source location, so it does not share the source
    assert events.table["source"] != result.table["source"]
    [mine] = [source for source in sources if source["id"] == result.table["source"]]
    assert (mine["kind"], mine["name"], mine.get("location")) == (SourceKinds.ONELAKE_LAKEHOUSE, "Synth", None)


def test_issues_are_capped_at_100_and_one_more_counts_the_rest(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(*(add(f"f{index:03d}.parquet", None, 1) for index in range(150))))
    storage = walked(lake)
    result = run(storage)
    issues = result.issues
    assert len(issues) == 101
    assert [issue["code"] for issue in issues[:100]] == ["FooterNotRead"] * 100
    assert issues[100]["code"] == "IssuesTruncated"
    assert result.table["fileCount"] == 150
    assert result.table["footerCount"] == 0
    assert result.table["collectedLevel"] == 1
    assert result.table["detailDepth"] == "Files"
    assert storage.reads(lambda path: not path.startswith(LOG + "/")) == []  # no size, no request


# ---- deletion vectors and tombstones ----

def test_a_file_removed_with_one_deletion_vector_and_added_with_another_stays_live(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 100, deletion_vector=deletion_vector(40, 5)),
                                 add("b.parquet", 20, 200)))
    lake.commit(TABLE, 1, *later(1, remove("a.parquet", 10, time_of(1), deletion_vector=deletion_vector(40, 5)),
                                 add("a.parquet", 10, 100, deletion_vector=deletion_vector(50, 9, "cd^-aqEH.-t@S}K{vb[*k^"))))
    lake.data(TABLE, "a.parquet", 10)
    lake.data(TABLE, "b.parquet", 20)
    result = run(local(lake), level=1)
    assert result.issues == []
    assert result.table["fileCount"] == 2
    assert result.table["fileSize"] == 30
    assert result.table["deletedRowCount"] == 9
    file = result.file("a.parquet")
    assert file["deletionVectorSize"] == 50
    assert file["deletedRowCount"] == 9
    assert result.file("b.parquet")["deletedRowCount"] == 0
    # As part-0001.parquet of the fixture, whose remove and add of a deletion vector leave one tombstone in the kit:
    # the file that the checkpoint removed, not this one.
    assert result.table["nonLiveFileCount"] == 0
    assert result.table["unreferencedFileCount"] == 0


def test_a_removed_file_still_in_storage_is_unreferenced_and_a_removed_file_deleted_is_not(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1), add("b.parquet", 20, 2), add("c.parquet", 30, 3)))
    lake.commit(TABLE, 1, *later(1, remove("b.parquet", 20, EPOCH + 61_000), remove("c.parquet", 30, EPOCH + 61_000)))
    lake.data(TABLE, "a.parquet", 10)
    lake.data(TABLE, "b.parquet", 20)
    result = both(lake)
    assert result.table["fileCount"] == 1
    assert result.table["nonLiveFileCount"] == 2
    assert result.table["nonLiveFileSize"] == 50
    assert result.table["unreferencedFileCount"] == 1
    assert result.table["unreferencedFileSize"] == 20
    assert result.table["nonLiveOldestRemovedAt"] == timestamp(EPOCH + 61_000)
    assert result.table["nonLiveNewestRemovedAt"] == timestamp(EPOCH + 61_000)
    assert result.issues == []


def test_a_remove_of_a_file_that_no_commit_added_is_a_tombstone(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1)))
    lake.commit(TABLE, 1, *later(1, remove("ghost.parquet", 70, EPOCH + 75_000)))
    result = run(local(lake), level=1)
    assert result.table["fileCount"] == 1
    assert result.table["nonLiveFileCount"] == 1
    assert result.table["nonLiveFileSize"] == 70
    assert result.table["nonLiveOldestRemovedAt"] == timestamp(EPOCH + 75_000)
    assert result.table["nonLiveNewestRemovedAt"] == timestamp(EPOCH + 75_000)


def test_remove_actions_without_a_timestamp_give_no_removal_time_and_are_still_counted(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1), add("b.parquet", 20, 2), add("c.parquet", 30, 3)))
    lake.commit(TABLE, 1, *later(1, remove("b.parquet", 20, None), remove("c.parquet", 30, None)))
    lake.data(TABLE, "a.parquet", 10)
    result = both(lake)
    assert result.table["nonLiveFileCount"] == 2
    assert result.table["nonLiveFileSize"] == 50
    assert result.table.get("nonLiveOldestRemovedAt") is None
    assert result.table.get("nonLiveNewestRemovedAt") is None
    assert result.table["unreferencedFileCount"] == 0
    assert result.issues == []


def test_removals_at_distinct_times_give_distinct_oldest_and_newest(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1), add("b.parquet", 20, 2), add("c.parquet", 30, 3),
                                 add("d.parquet", 40, 4)))
    # The later commit holds the earlier removal time, and one remove has no time.
    lake.commit(TABLE, 1, *later(1, remove("b.parquet", 20, EPOCH + 90_000)))
    lake.commit(TABLE, 2, *later(2, remove("c.parquet", 30, EPOCH + 30_000), remove("d.parquet", 40, None)))
    lake.data(TABLE, "a.parquet", 10)
    result = both(lake)
    assert result.table["nonLiveFileCount"] == 3
    assert result.table["nonLiveOldestRemovedAt"] == timestamp(EPOCH + 30_000)
    assert result.table["nonLiveNewestRemovedAt"] == timestamp(EPOCH + 90_000)
    assert result.issues == []


def test_a_path_removed_twice_whose_later_remove_has_no_timestamp_keeps_the_earlier_one(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1), add("b.parquet", 20, 2)))
    lake.commit(TABLE, 1, *later(1, remove("b.parquet", 20, EPOCH + 61_000)))
    lake.commit(TABLE, 2, *later(2, remove("b.parquet", 20, None)))
    lake.data(TABLE, "a.parquet", 10)
    result = both(lake)
    assert result.table["nonLiveFileCount"] == 1
    assert result.table["nonLiveOldestRemovedAt"] == timestamp(EPOCH + 61_000)
    assert result.table["nonLiveNewestRemovedAt"] == timestamp(EPOCH + 61_000)


def test_a_path_removed_twice_with_two_timestamps_takes_the_later_remove(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1), add("b.parquet", 20, 2)))
    lake.commit(TABLE, 1, *later(1, remove("b.parquet", 20, EPOCH + 61_000)))
    lake.commit(TABLE, 2, *later(2, remove("b.parquet", 20, EPOCH + 121_000)))
    lake.data(TABLE, "a.parquet", 10)
    result = both(lake)
    assert result.table["nonLiveFileCount"] == 1
    assert result.table["nonLiveOldestRemovedAt"] == timestamp(EPOCH + 121_000)
    assert result.table["nonLiveNewestRemovedAt"] == timestamp(EPOCH + 121_000)


def test_a_removed_file_that_is_added_back_is_not_a_tombstone(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1)))
    lake.commit(TABLE, 1, *later(1, remove("a.parquet", 10, EPOCH + 61_000)))
    lake.commit(TABLE, 2, *later(2, add("a.parquet", 10, 1)))
    lake.data(TABLE, "a.parquet", 10)
    result = both(lake)
    assert result.table["fileCount"] == 1
    assert result.table["nonLiveFileCount"] == 0
    assert result.table.get("nonLiveOldestRemovedAt") is None
    assert result.table.get("nonLiveNewestRemovedAt") is None
    assert result.table["unreferencedFileCount"] == 0
    assert result.table["unreferencedFileSize"] == 0
    assert result.issues == []


# ---- unreferenced files ----

def unreferenced_fields(table):
    return tuple(table.get(name) for name in ("unreferencedFileCount", "unreferencedFileSize", "nonLiveOldestRemovedAt",
                                              "nonLiveNewestRemovedAt", "nonLiveFileCount"))


def both(lake):
    """Collects at level 1 through LocalStorage, which lists a folder at any depth in one request, and through a storage
    that the collector lists one folder at a time; asserts that both give the same fields, and returns the first."""
    result = run(local(lake), level=1)
    other = run(walked(lake), level=1)
    assert unreferenced_fields(result.table) == unreferenced_fields(other.table)
    assert result.codes == other.codes
    return result


def test_a_table_whose_folder_has_only_its_live_files_has_no_unreferenced_file(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1), add("p=1/b.parquet", 20, 2)))
    lake.data(TABLE, "a.parquet", 10)
    lake.data(TABLE, "p=1/b.parquet", 20)
    result = both(lake)
    assert result.table["fileCount"] == 2
    assert result.table["unreferencedFileCount"] == 0
    assert result.table["unreferencedFileSize"] == 0
    assert result.table["nonLiveFileCount"] == 0
    assert result.table.get("nonLiveOldestRemovedAt") is None
    assert result.issues == []


def test_an_unreferenced_file_in_a_partition_folder_and_one_in_the_root_are_counted(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1), add("p=1/b.parquet", 20, 2)))
    lake.data(TABLE, "a.parquet", 10)
    lake.data(TABLE, "p=1/b.parquet", 20)
    lake.data(TABLE, "p=1/stray.parquet", 11)
    lake.data(TABLE, "stray.parquet", 7)
    lake.data(TABLE, "p=2/deeper/folder/stray.parquet", 5)
    result = both(lake)
    assert result.table["fileCount"] == 2
    assert result.table["unreferencedFileCount"] == 3
    assert result.table["unreferencedFileSize"] == 23
    assert result.issues == []


def test_change_data_files_and_files_that_are_not_parquet_are_not_counted(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1)))
    lake.data(TABLE, "a.parquet", 10)
    lake.data(TABLE, "_change_data/cdc-00000.snappy.parquet", 13)
    lake.data(TABLE, "_change_data/p=1/cdc-00001.snappy.parquet", 14)
    lake.data(TABLE, "_delta_log/stray.parquet", 15)
    lake.data(TABLE, "deletion_vector_0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b.bin", 16)
    lake.data(TABLE, ".a.parquet.crc", 17)
    lake.data(TABLE, "a.parquet.tmp", 18)
    lake.data(TABLE, "p=1/notes.txt", 19)
    lake.data(TABLE, "parquet", 20)
    result = both(lake)
    assert result.table["unreferencedFileCount"] == 0
    assert result.table["unreferencedFileSize"] == 0
    assert result.issues == []


def test_only_the_log_and_change_data_folders_directly_under_the_table_folder_are_left_out(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1)))
    lake.data(TABLE, "a.parquet", 10)
    lake.data(TABLE, "p=1/_change_data/x.parquet", 11)
    lake.data(TABLE, "nested/_delta_log/y.parquet", 12)
    result = both(lake)
    assert result.table["unreferencedFileCount"] == 2
    assert result.table["unreferencedFileSize"] == 23
    assert result.issues == []


def test_an_extension_in_another_case_counts(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1)))
    lake.data(TABLE, "a.parquet", 10)
    lake.data(TABLE, "UPPER.PARQUET", 5)
    lake.data(TABLE, "p=1/Mixed.Parquet", 6)
    result = both(lake)
    assert result.table["unreferencedFileCount"] == 2
    assert result.table["unreferencedFileSize"] == 11
    assert result.issues == []


def test_a_path_that_the_log_percent_encodes_matches_its_listing_entry(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("dir%20one/part%20a.parquet", 10, 1), add("p=x%25y/b.parquet", 20, 2)))
    lake.data(TABLE, "dir one/part a.parquet", 10)
    lake.data(TABLE, "p=x%y/b.parquet", 20)
    lake.data(TABLE, "dir one/stray b.parquet", 7)
    result = both(lake)
    assert result.table["fileCount"] == 2
    assert result.table["unreferencedFileCount"] == 1
    assert result.table["unreferencedFileSize"] == 7
    assert result.issues == []


def test_the_comparison_of_paths_is_case_sensitive(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("Upper.parquet", 10, 1)))
    lake.data(TABLE, "upper.parquet", 10)
    result = both(lake)
    assert result.table["unreferencedFileCount"] == 1
    assert result.table["unreferencedFileSize"] == 10


def test_a_listing_entry_without_a_size_counts_in_the_count_and_not_in_the_size(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1)))
    lake.data(TABLE, "a.parquet", 10)
    lake.data(TABLE, "stray-sized.parquet", 11)
    lake.data(TABLE, "p=1/stray-unsized.parquet", 7)
    storage = SizelessStorage(local(lake), lambda path: path.endswith("/a.parquet") or "unsized" in path)
    result = run(storage, level=1)
    assert result.table["fileCount"] == 1
    assert result.table["fileSize"] == 10
    assert result.table["unreferencedFileCount"] == 2
    assert result.table["unreferencedFileSize"] == 11
    assert result.issues == []


@pytest.mark.parametrize("status, code", [(403, "AccessDenied"), (401, "AccessDenied"), (500, "DataFilesNotListed"),
                                          (503, "DataFilesNotListed"), (None, "DataFilesNotListed")])
def test_a_listing_that_fails_gives_an_issue_and_no_unreferenced_fields(tmp_path, status, code):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("a.parquet", 10, 1), add("p=1/b.parquet", 20, 2)))
    lake.commit(TABLE, 1, *later(1, remove("a.parquet", 10, EPOCH + 61_000)))
    lake.data(TABLE, "a.parquet", 10)
    lake.data(TABLE, "p=1/b.parquet", 20)
    lake.data(TABLE, "p=1/stray.parquet", 7)

    def fail(kind, path):
        return StorageError("A listing failed.", status) if kind == "list" and path.strip("/") == TABLE + "/p=1" else None

    result = run(walked(lake, fail=fail), level=1)
    [issue] = result.issues
    assert issue["code"] == code
    assert result.table.get("unreferencedFileCount") is None
    assert result.table.get("unreferencedFileSize") is None
    # The rest of the table does not depend on the listing.
    assert result.table["detailDepth"] == "Files"
    assert result.table["fileCount"] == 1
    assert result.table["fileSize"] == 20
    assert result.table["nonLiveFileCount"] == 1
    assert result.table["nonLiveOldestRemovedAt"] == timestamp(EPOCH + 61_000)
    assert result.table["nonLiveNewestRemovedAt"] == timestamp(EPOCH + 61_000)


def test_a_log_that_cannot_be_read_whole_leaves_out_the_unreferenced_fields_and_the_removal_times(tmp_path):
    # The log keeps no commit before its checkpoint, so without a checkpoint reader the live files are unknown.
    lake = Lake(tmp_path)
    lake.checkpoint(TABLE, 2)
    lake.commit(TABLE, 3, *later(3, add("a.parquet", 10, 1), remove("b.parquet", 20, EPOCH + 61_000)))
    lake.data(TABLE, "a.parquet", 10)
    lake.data(TABLE, "stray.parquet", 7)
    result = run(local(lake), reader=None, level=1)
    assert "DeltaCheckpointNotRead" in result.codes
    assert result.table.get("fileCount") is None
    assert result.table.get("unreferencedFileCount") is None
    assert result.table.get("unreferencedFileSize") is None
    assert result.table.get("nonLiveOldestRemovedAt") is None
    assert result.table.get("nonLiveNewestRemovedAt") is None
    assert result.table["detailDepth"] == "Summary"


def test_the_requests_of_the_listing_are_counted(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, *first(add("p=1/a.parquet", 10, 1), add("p=2/b.parquet", 20, 2)))
    lake.data(TABLE, "p=1/a.parquet", 10)
    lake.data(TABLE, "p=2/b.parquet", 20)
    lake.data(TABLE, "p=2/deeper/stray.parquet", 7)
    lake.data(TABLE, "_change_data/cdc.parquet", 8)

    # Folder by folder: one listing for each folder of the table outside the log and the change data.
    storage = walked(lake)
    tables = discover_all(storage)
    discovery = storage.request_count
    data, builder = collect_bytes(storage, tables, level=1, checkpoint_reader=StateCheckpointReader())
    requests = storage.requests[discovery:]
    assert builder.collection["requestCount"] == len(requests)
    for folder in ("", "/p=1", "/p=2", "/p=2/deeper"):
        assert any(request.kind == "list" and request.path.strip("/") == TABLE + folder for request in requests), folder
    below = [request for request in requests if request.kind == "list"
             and request.path.strip("/").startswith(TABLE + "/")
             and not request.path.strip("/").startswith(TABLE + "/_delta_log")]
    assert len(below) >= 3

    # Through LocalStorage, the folder is listed at any depth in one request, in place of the listing of the table
    # folder and of each folder below it.
    plain = local(lake)
    again = discover_all(plain)
    before = plain.request_count
    _, recursive = collect_bytes(plain, again, level=1, checkpoint_reader=StateCheckpointReader())
    assert plain.request_count - before == recursive.collection["requestCount"]
    assert recursive.collection["requestCount"] == builder.collection["requestCount"] - len(below)
    [table] = rows(open_bytes(data), "tables")
    assert (table["unreferencedFileCount"], table["unreferencedFileSize"]) == (1, 7)


# ---- partitions ----

def test_a_partition_has_the_latest_modification_of_its_files_and_none_when_a_file_has_none(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, protocol(), partitioned_metadata(), commit_info(0),
                add("p=1/a.parquet", 10, 1, EPOCH + 5_000, "1"),
                add("p=1/b.parquet", 20, 2, EPOCH + 9_000, "1"),
                add("p=1/c.parquet", 30, 3, EPOCH + 7_000, "1"),
                add("p=2/d.parquet", 40, 4, EPOCH + 8_000, "2"),
                add("p=2/e.parquet", 50, 5, None, "2"))
    result = run(local(lake), level=1, include_partition_values=True)
    partitions = rows(result.doc, "partitions")
    assert len(partitions) == 2
    [one] = [partition for partition in partitions if partition["values"] == ["1"]]
    [two] = [partition for partition in partitions if partition["values"] == ["2"]]
    assert one["lastModifiedAt"] == timestamp(EPOCH + 9_000)
    assert one["fileCount"] == 3
    assert two.get("lastModifiedAt") is None
    assert two["fileCount"] == 2
    assert result.table.get("lastModifiedAt") is None


def test_a_table_whose_files_all_have_a_modification_time_has_the_latest(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, protocol(), partitioned_metadata(), commit_info(0),
                add("p=1/a.parquet", 10, 1, EPOCH + 5_000, "1"),
                add("p=2/b.parquet", 20, 2, EPOCH + 9_000, "2"))
    result = run(local(lake), level=1, include_partition_values=True)
    assert result.table["lastModifiedAt"] == timestamp(EPOCH + 9_000)
    by_value = {tuple(partition["values"]): partition for partition in rows(result.doc, "partitions")}
    assert by_value[("1",)]["lastModifiedAt"] == timestamp(EPOCH + 5_000)
    assert by_value[("2",)]["lastModifiedAt"] == timestamp(EPOCH + 9_000)


def partitioned_lake(tmp_path):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, protocol(), partitioned_metadata(), commit_info(0),
                add("p=alphavalue/a.parquet", 10, 1, EPOCH, "alphavalue"),
                add("p=betavalue/b.parquet", 20, 2, EPOCH, "betavalue"),
                add("p=betavalue/c.parquet", 30, 3, EPOCH, "betavalue"))
    for path, size in (("p=alphavalue/a.parquet", 10), ("p=betavalue/b.parquet", 20), ("p=betavalue/c.parquet", 30)):
        lake.data(TABLE, path, size)
    return lake


def test_partition_values_in_paths_become_numbers_unless_asked(tmp_path):
    lake = partitioned_lake(tmp_path)
    result = run(local(lake), level=1)
    assert sorted(file["path"] for file in result.files) == ["p=#0/a.parquet", "p=#1/b.parquet", "p=#1/c.parquet"]
    partitions = rows(result.doc, "partitions")
    assert len(partitions) == 2
    assert all(partition.get("values") is None for partition in partitions)
    assert sorted(partition["fileCount"] for partition in partitions) == [1, 2]
    assert result.doc.manifest["collection"]["includesPartitionValues"] is False
    text = all_text(result.data)
    assert "alphavalue" not in text and "betavalue" not in text
    assert result.table["unreferencedFileCount"] == 0
    assert result.table["partitionCount"] == 2


def test_partition_values_and_paths_are_kept_when_asked(tmp_path):
    lake = partitioned_lake(tmp_path)
    result = run(local(lake), level=1, include_partition_values=True)
    assert sorted(file["path"] for file in result.files) == ["p=alphavalue/a.parquet", "p=betavalue/b.parquet",
                                                             "p=betavalue/c.parquet"]
    assert sorted(tuple(partition["values"]) for partition in rows(result.doc, "partitions")) == [("alphavalue",), ("betavalue",)]
    assert result.doc.manifest["collection"]["includesPartitionValues"] is True


# ---- data values and names ----

def constrained_lake(tmp_path):
    lake = Lake(tmp_path)
    configuration = {"delta.constraints.positive_identifier": "id > 424242", "delta.appendOnly": "true"}
    lake.commit(TABLE, 0, protocol(), metadata(configuration=configuration),
                commit_info(0, operationParameters={"mode": "Append", "predicate": "secretpredicate = 1"}),
                add("a.parquet", 10, 1))
    return lake


def test_check_constraints_and_operation_parameters_are_dropped_unless_data_values_are_asked(tmp_path):
    lake = constrained_lake(tmp_path)
    result = run(local(lake), level=1)
    assert result.table["properties"] == {"delta.appendOnly": "true"}
    assert all(commit.get("parameters") is None for commit in result.commits)
    text = all_text(result.data)
    assert "424242" not in text and "secretpredicate" not in text
    assert result.doc.manifest["collection"]["includesDataValues"] is False


def test_check_constraints_and_operation_parameters_are_kept_when_asked(tmp_path):
    lake = constrained_lake(tmp_path)
    result = run(local(lake), level=1, include_data_values=True)
    assert result.table["properties"] == {"delta.constraints.positive_identifier": "id > 424242",
                                          "delta.appendOnly": "true"}
    [commit] = result.commits
    assert sorted(commit["parameters"]) == ["mode", "predicate"]
    assert result.doc.manifest["collection"]["includesDataValues"] is True


@pytest.mark.parametrize("options", [{}, {"include_data_values": True, "include_partition_values": True}],
                         ids=["default", "with values"])
def test_the_user_notebook_and_cluster_of_a_commit_are_never_written(tmp_path, options):
    lake = Lake(tmp_path)
    lake.commit(TABLE, 0, protocol(), metadata(),
                commit_info(0, userName="someone.private@example.com", userId="user-id-7f3a",
                            notebook={"notebookId": "notebook-5521"}, clusterId="cluster-0099",
                            job={"jobId": "job-8812"}),
                add("a.parquet", 10, 1))
    result = run(local(lake), level=1, **options)
    text = all_text(result.data)
    for hidden in ("someone.private", "user-id-7f3a", "notebook-5521", "cluster-0099", "job-8812", '"userName"',
                   '"userId"'):
        assert hidden not in text, hidden
    [commit] = result.commits
    assert commit["engine"] == "Test engine"
    assert commit["isBlindAppend"] is True
    assert commit["metrics"] == {"numFiles": "0"}


# ---- checkpoints in JSON ----

def test_a_v2_checkpoint_in_json_is_read_by_the_collector_itself(tmp_path):
    lake = Lake(tmp_path)
    checkpoint = f"{LOG}/{2:020d}.checkpoint.0a1b2c3d-0000-4000-8000-00000000000a.json"
    actions = [{"checkpointMetadata": {"version": 2}},
               protocol(3, 7, ["v2Checkpoint"], ["v2Checkpoint"]),
               metadata(),
               add("a.parquet", 10, 1), add("b.parquet", 20, 2),
               remove("gone.parquet", 30, EPOCH + 61_000)]
    lake.write(checkpoint, "".join(json.dumps(action) + "\n" for action in actions))
    lake.write(f"{LOG}/_last_checkpoint", json.dumps({"version": 2, "size": len(actions)}))
    lake.commit(TABLE, 3, *later(3, add("c.parquet", 40, 3)))
    result = run(local(lake), reader=None, level=1)
    assert result.issues == []
    assert result.table["fileCount"] == 3
    assert result.table["fileSize"] == 70
    assert result.table["snapshotId"] == "3"
    assert result.table["delta"]["checkpointVersion"] == 2
    assert result.table["delta"]["commitsAfterCheckpoint"] == 1
    assert result.table["delta"]["readerFeatures"] == ["v2Checkpoint"]
    assert result.table["nonLiveFileCount"] == 1
    assert result.table["nonLiveOldestRemovedAt"] == timestamp(EPOCH + 61_000)
    assert result.table["detailDepth"] == "Files"


def test_a_custom_checkpoint_reader_gets_the_storage_and_the_path_of_the_checkpoint(tmp_path):
    lake = Lake(tmp_path)
    lake.checkpoint(TABLE, 5)
    lake.commit(TABLE, 6, *later(6, add("a.parquet", 10, 1)))
    seen = []

    def reader(storage, path):
        seen.append(path)
        assert callable(getattr(storage, "read_range", None))
        return StateCheckpointReader()(storage, path)

    result = run(local(lake), reader=reader, level=1)
    assert seen == [f"{LOG}/{checkpoint_name(5)}"]
    assert result.issues == []
    assert result.table["fileCount"] == 1
    assert result.table["tableId"] == TABLE_ID


def test_a_checkpoint_reader_that_fails_gives_an_issue_and_no_exception(tmp_path):
    lake = Lake(tmp_path)
    lake.checkpoint(TABLE, 5)
    lake.commit(TABLE, 6, *later(6, add("a.parquet", 10, 1)))

    def reader(storage, path):
        raise ValueError("A checkpoint that does not line up.")

    result = run(local(lake), reader=reader, level=1)
    assert "DeltaCheckpointNotRead" in result.codes
    assert result.table.get("fileCount") is None


def test_the_issue_codes_are_those_of_the_readme():
    from pqax.collect import ISSUE_CODES
    readme = ["AccessDenied", "TableNotRead", "DeltaLogMissing", "DeltaLogIncomplete", "DeltaCheckpointNotRead",
              "DeltaCheckpointMissing", "DeltaCommitNotRead", "DeltaSchemaNotParsed", "DeltaStatsNotParsed",
              "DataFilesNotListed", "FileOutsideTable", "FooterNotRead", "FooterEncrypted", "FooterInvalid",
              "FooterTooLarge", "FooterColumnsEncrypted", "IssuesTruncated"]
    assert len(ISSUE_CODES) == 17
    assert sorted(ISSUE_CODES) == sorted(readme)
    constants = {name: value for name, value in vars(IssueCodes).items() if name.isupper()}
    assert sorted(constants.values()) == sorted(readme)
    assert IssueCodes.ACCESS_DENIED == "AccessDenied"
    assert IssueCodes.DELTA_LOG_INCOMPLETE == "DeltaLogIncomplete"
    assert IssueCodes.ISSUES_TRUNCATED == "IssuesTruncated"
    assert IssueCodes.FILE_OUTSIDE_TABLE == "FileOutsideTable"
