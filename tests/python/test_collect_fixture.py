"""The acceptance test of pqax.collect: the fixture lakehouse collected through LocalStorage equals the samples of the
kit that were built from the probe dump of the same fixture, every table and field at every depth, apart from the
locations, and the dump itself; at level 1 it keeps what the README of the C# collector says a level-1 table keeps.

The lakehouse table of the fixture needs its Parquet checkpoint, so the tests that collect it need pyarrow, the extra
pqax[collect]; the warehouse table has a JSON log from version 0, and the tests on it alone run on the standard
library, with checkpoint_reader=None.
"""
from __future__ import annotations

import copy
import datetime
import io
import json
import os
import re
import subprocess
import sys
import threading

import pytest

import pqax
from collect_kit import (FIXTURE, HAS_PYARROW, ORDERS, SALES, TABLES, FailingCheckpointReader, RecordingStorage,
                         all_text, assert_valid, canonical, chunk_sum, collect_bytes, differences, discover_all,
                         dump_file_of_size, encodings_of, field_values, is_location, is_vordered, key_value, of_table,
                         open_bytes, row_groups, row_of, rows, sample, stated_tables, timestamp, vorder_tag)
from pqax_kit import ROOT
from pqax.collect import (ISSUE_CODES, CollectionProgress, CollectorOptions, DetailDepth, LocalStorage, SourceKinds,
                          StorageError, collect)

needs_pyarrow = pytest.mark.skipif(not HAS_PYARROW, reason="the checkpoint of the fixture needs pyarrow, pqax[collect]")
COLLECTION_MEMBERS = ["collectorLib", "collectorLibVersion", "startedAt", "completedAt", "level", "includesDataValues",
                      "includesPartitionValues", "requestCount", "bytesRead"]
TIME = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")


def fixture_storage():
    return LocalStorage(str(FIXTURE))


class Collected:
    def __init__(self, data, builder, tables=None):
        self.data = data
        self.builder = builder
        self.tables = tables
        self.doc = open_bytes(data)


def collect_fixture(storage=None, tables=None, discover=True, **options):
    storage = storage or fixture_storage()
    if tables is None and discover:
        tables = discover_all(storage)
    data, builder = collect_bytes(storage, tables, **options)
    return Collected(data, builder, tables)


@pytest.fixture(scope="module")
def default():
    pytest.importorskip("pyarrow")
    return collect_fixture()


@pytest.fixture(scope="module")
def any_reader():
    """The default collection with pyarrow, or the collection without a checkpoint reader, whose warehouse table is
    complete all the same."""
    return collect_fixture() if HAS_PYARROW else collect_fixture(checkpoint_reader=None)


def assert_local_locations(doc, tables=TABLES):
    for dump in tables:
        table = row_of(doc, dump)
        [source] = [source for source in rows(doc, "sources") if source["id"] == table["source"]]
        assert source["location"] == os.path.abspath(os.path.join(str(FIXTURE), dump.item_folder))
        assert source.get("identifiers") is None
        assert table["location"] == os.path.abspath(os.path.join(str(FIXTURE), *dump.table_path.split("/")))


# ---- the acceptance test ----

@needs_pyarrow
def test_the_default_collection_equals_sample_pqax_apart_from_the_locations(default):
    with pqax.open(sample("sample.pqax")) as expected:
        assert [part["name"] for part in default.doc.manifest["parts"]] == [part["name"] for part in expected.manifest["parts"]]
        assert stated_tables(default.doc) == stated_tables(expected)
        for name in stated_tables(expected):
            assert default.doc.row_count(name) == expected.row_count(name), name
        assert differences(default.doc, expected, is_location) == []
    assert_local_locations(default.doc)
    assert_valid(default.data)


@needs_pyarrow
def test_the_collection_of_the_manifest_states_the_library_the_times_the_level_and_the_requests(default):
    manifest = default.doc.manifest
    assert manifest["format"] == "pqax"
    assert manifest["formatVersion"] == "0.2"
    collection = manifest["collection"]
    assert list(collection) == COLLECTION_MEMBERS
    assert collection["collectorLib"] == "pqax.collect"
    assert collection["collectorLibVersion"] == pqax.__version__
    assert TIME.match(collection["startedAt"]) and TIME.match(collection["completedAt"])
    assert collection["startedAt"] <= collection["completedAt"]
    assert (collection["level"], collection["includesDataValues"], collection["includesPartitionValues"]) == (2, False, False)
    assert collection["requestCount"] == 17
    assert collection["bytesRead"] == 142178

    built = default.builder.collection
    assert list(built) == COLLECTION_MEMBERS
    for name in ("startedAt", "completedAt"):
        moment = built[name]
        assert isinstance(moment, datetime.datetime)
        assert moment.utcoffset() == datetime.timedelta(0)
        assert moment.microsecond % 1000 == 0
    assert built["startedAt"] <= built["completedAt"]


@needs_pyarrow
def test_the_caller_sets_the_app_and_the_file_is_valid_on_disk(tmp_path):
    collected = collect_fixture()
    collected.builder.collection["collectorApp"] = "pqax-tests"
    collected.builder.collection["collectorAppVersion"] = "1.2.3"
    path = tmp_path / "fixture.pqax"
    collected.builder.write(str(path))
    assert pqax.validate(str(path)).is_valid
    with pqax.open(str(path)) as doc:
        assert doc.manifest["collection"]["collectorApp"] == "pqax-tests"
        assert doc.manifest["collection"]["collectorAppVersion"] == "1.2.3"
        assert doc.manifest["collection"]["collectorLib"] == "pqax.collect"


@needs_pyarrow
def test_the_facts_of_the_kit_readme_hold_for_the_collection(default):
    doc = default.doc
    tables = doc.table("tables")
    assert tables["unreferencedFileCount"] == [1, 0]
    assert tables["unreferencedFileSize"] == [737, 0]
    assert tables["nonLiveOldestRemovedAt"] == ["2023-11-14T22:14:10.000Z", None]
    assert tables["nonLiveNewestRemovedAt"] == ["2023-11-14T22:14:10.000Z", None]
    assert doc.table("partitions")["lastModifiedAt"] == ["2023-11-14T22:15:00.000Z", "2023-11-14T22:15:10.000Z"]
    assert doc.table("commits")["metrics"][1] == {}
    assert tables["delta"][1]["readerFeatures"] == []
    assert rows(doc, "issues") == []
    assert tables["detailDepth"] == ["Column chunks", "Column chunks"]


@needs_pyarrow
def test_without_tables_the_collector_discovers_them(default):
    discovered = collect_fixture(discover=False)
    assert differences(discovered.doc, default.doc) == []
    collection = discovered.doc.manifest["collection"]
    assert (collection["requestCount"], collection["bytesRead"]) == (17, 142178)
    assert_valid(discovered.data)
    one = collect_fixture(discover=False, items=[ORDERS.item_folder])
    [table] = rows(one.doc, "tables")
    assert (table["name"], table["namespace"]) == (ORDERS.name, list(ORDERS.namespace))


@needs_pyarrow
@pytest.mark.parametrize("depth, file", [
    ("summary", "summary-only.pqax"), ("files", "files.pqax"), ("rowGroups", "row-groups.pqax"),
    ("columnChunks", "sample.pqax"), ("ROWGROUPS", "row-groups.pqax"), ("Files", "files.pqax"),
    (DetailDepth.SUMMARY, "summary-only.pqax"), (DetailDepth.ROW_GROUPS, "row-groups.pqax"),
])
def test_each_depth_gives_the_tables_of_the_kit_sample_of_that_depth(depth, file):
    collected = collect_fixture(depth=depth)
    with pqax.open(sample(file)) as expected:
        assert [part["name"] for part in collected.doc.manifest["parts"]] == [part["name"] for part in expected.manifest["parts"]]
        for name in stated_tables(expected):
            assert collected.doc.row_count(name) == expected.row_count(name), name
        assert differences(collected.doc, expected, is_location) == []
    assert_local_locations(collected.doc)
    assert collected.doc.manifest["collection"]["requestCount"] == 17
    assert collected.doc.manifest["collection"]["bytesRead"] == 142178
    assert_valid(collected.data)


def test_the_depths_and_their_names():
    assert [int(depth) for depth in (DetailDepth.SUMMARY, DetailDepth.FILES, DetailDepth.ROW_GROUPS,
                                     DetailDepth.COLUMN_CHUNKS)] == [0, 1, 2, 3]


# ---- level 1 ----

FOOTER_TABLE_FIELDS = ("rowGroupCount", "compressedSize", "uncompressedSize", "footerSize", "staleColumnSize",
                       "rowGroupRowCounts", "vOrderedFileCount")
FOOTER_COLUMN_FIELDS = ("compressedSize", "uncompressedSize", "valueCount", "chunkCount", "dictionaryChunkCount",
                        "fallbackChunkCount", "dictionaryPageSize", "unencodedByteArrayDataBytes", "codecs", "encodings",
                        "fileCountWithoutColumn", "largestChunkDistinctCount")
FOOTER_FILE_FIELDS = ("footerSize", "physicalSchema", "writer", "rowGroupCount", "compressedSize", "uncompressedSize")
# The columns whose null counts the statistics of every file of the lakehouse table have: id and name, 0 in each file.
LOG_NULL_COUNTS = {("sales", "id"): 0, ("sales", "name"): 0}


def expected_level_1():
    """files.pqax, with what a level-1 table keeps (README of the C# collector): the Files depth at most, no footer and
    nothing that comes from one, the rows of the statistics of the log when every file has them, the V-Order of the
    VORDER tag only, and the null counts of the statistics of the log, with columnTotalsSource "Table format", when
    every file has them."""
    with pqax.open(sample("files.pqax")) as doc:
        expected = {name: copy.deepcopy(doc.table(name)) for name in stated_tables(doc)}
        counts = {name: doc.row_count(name) for name in stated_tables(doc)}
    for name in ("physicalSchemas", "physicalColumns", "writers"):
        expected.pop(name, None)
        counts.pop(name, None)
    by_name = {dump.name: dump for dump in TABLES}
    tables = expected["tables"]
    names = tables["name"]
    for name in FOOTER_TABLE_FIELDS:
        tables[name] = [None] * len(names)
    tables["collectedLevel"] = [1] * len(names)
    tables["footerCount"] = [0] * len(names)
    every_stats = [all("numRecords" in file for file in by_name[name].files) for name in names]
    tables["rowCount"] = [sum(file["numRecords"] for file in by_name[name].files) if stats else None
                          for name, stats in zip(names, every_stats)]
    tables["columnTotalsSource"] = ["Table format" if stats else None for stats in every_stats]

    columns = expected["columns"]
    for name in FOOTER_COLUMN_FIELDS:
        columns[name] = [None] * counts["columns"]
    table_names = dict(zip(tables["id"], names))
    columns["nullCount"] = [LOG_NULL_COUNTS.get((table_names[table], column))
                            for table, column in zip(columns["table"], columns["name"])]

    files = expected["files"]
    for name in FOOTER_FILE_FIELDS:
        files[name] = [None] * counts["files"]
    dumped = [dump_file_of_size(size)[1] for size in files["fileSize"]]
    files["rowCount"] = [file.get("numRecords") for file in dumped]
    files["isVOrdered"] = [vorder_tag(file) for file in dumped]
    return expected, counts


@needs_pyarrow
def test_level_1_reads_the_logs_and_keeps_what_a_level_1_table_keeps():
    storage = RecordingStorage(fixture_storage())
    collected = collect_fixture(storage=storage, level=1)
    assert not storage.reads(lambda path: path.endswith(".parquet") and "/_delta_log/" not in path)
    doc = collected.doc
    assert doc.manifest["collection"]["level"] == 1
    expected, counts = expected_level_1()
    for name in dict.fromkeys(stated_tables(doc) + list(expected)):
        assert doc.row_count(name) == counts.get(name, 0), name
    found = []
    for name, table in expected.items():
        actual = doc.table(name)
        for column in dict.fromkeys(list(actual) + list(table)):
            if is_location(name, column):
                continue
            got = canonical(field_values(actual, column, counts[name]))
            want = canonical(field_values(table, column, counts[name]))
            if got != want:
                found.append(f"{name}.{column}: {got}, expected {want}")
    assert found == []
    assert [part["name"] for part in doc.manifest["parts"]] == ["summary.json", "partitions.json", "files.json", "commits.json"]
    # A storage that wraps LocalStorage records no location; the collection through LocalStorage does.
    assert all(source.get("location") is None for source in rows(doc, "sources"))
    assert_local_locations(collect_fixture(level=1).doc)
    assert_valid(collected.data)


@needs_pyarrow
def test_level_1_makes_12_requests_and_reads_13841_bytes():
    collected = collect_fixture(level=1)
    collection = collected.doc.manifest["collection"]
    assert (collection["level"], collection["requestCount"], collection["bytesRead"]) == (1, 12, 13841)


# ---- without pyarrow ----

@pytest.mark.skipif(HAS_PYARROW, reason="pyarrow is installed")
def test_without_pyarrow_the_default_checkpoint_reader_raises_an_import_error_naming_the_extra():
    with pytest.raises(ImportError, match=r"pqax\[collect\]"):
        collect(fixture_storage())


def test_with_pyarrow_hidden_the_default_checkpoint_reader_raises_an_import_error_naming_the_extra():
    script = ("import sys\n"
              "for name in ('pyarrow', 'pyarrow.parquet', 'pyarrow.lib'): sys.modules[name] = None\n"
              "from pqax.collect import LocalStorage, collect\n"
              "try:\n"
              "    collect(LocalStorage(sys.argv[1]))\n"
              "except ImportError as error:\n"
              "    print(error)\n"
              "    sys.exit(0 if 'pqax[collect]' in str(error) else 3)\n"
              "sys.exit(4)\n")
    environment = dict(os.environ)
    environment["PYTHONPATH"] = os.pathsep.join(filter(None, (str(ROOT / "src" / "python"), environment.get("PYTHONPATH"))))
    done = subprocess.run([sys.executable, "-c", script, str(FIXTURE)], capture_output=True, text=True, timeout=120,
                          env=environment, cwd=str(ROOT))
    assert done.returncode == 0, done.stdout + done.stderr


@pytest.mark.parametrize("reader", [None, FailingCheckpointReader()], ids=["no reader", "a failing reader"])
def test_without_a_checkpoint_the_lakehouse_table_gets_an_issue_and_the_warehouse_table_is_complete(reader):
    collected = collect_fixture(checkpoint_reader=reader)
    doc = collected.doc
    sales = row_of(doc, SALES)
    assert "DeltaCheckpointNotRead" in [issue["code"] for issue in of_table(doc, "issues", sales["id"])]
    assert sales["detailDepth"] == "Summary"
    for name in ("fileCount", "fileSize", "rowCount", "unreferencedFileCount", "unreferencedFileSize",
                 "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt"):
        assert sales.get(name) is None, name
    assert of_table(doc, "files", sales["id"]) == []
    assert_complete(doc, ORDERS)
    assert_valid(collected.data)


def assert_complete(doc, dump):
    """A table of the dump was collected completely, at the Column chunks depth, with no issue."""
    table = row_of(doc, dump)
    assert table["detailDepth"] == "Column chunks"
    assert table["fileCount"] == len(dump.files)
    assert table["fileSize"] == sum(file["size"] for file in dump.files)
    files = of_table(doc, "files", table["id"])
    assert sorted(file["fileSize"] for file in files) == sorted(file["size"] for file in dump.files)
    ids = {file["id"] for file in files}
    groups = [group for group in rows(doc, "rowGroups") if group["file"] in ids]
    assert len(groups) == sum(len(row_groups(file)) for file in dump.files)
    group_ids = {group["id"] for group in groups}
    chunks = [chunk for chunk in rows(doc, "columnChunks") if chunk["rowGroup"] in group_ids]
    assert sum(chunk["compressedSize"] for chunk in chunks) == sum(chunk_sum(file, "compressed") for file in dump.files)
    assert of_table(doc, "issues", table["id"]) == []


def test_access_denied_to_a_log_becomes_an_issue_of_its_table():
    log = ORDERS.table_path + "/_delta_log"
    storage = RecordingStorage(fixture_storage(), fail=lambda kind, path: StorageError("Access was denied.", 403)
                               if kind == "list" and path.strip("/") == log else None)
    collected = collect_fixture(storage=storage, checkpoint_reader=None, items=[ORDERS.item_folder], discover=False)
    table = row_of(collected.doc, ORDERS)
    assert "AccessDenied" in [issue["code"] for issue in of_table(collected.doc, "issues", table["id"])]
    assert table["detailDepth"] == "Summary"
    assert of_table(collected.doc, "files", table["id"]) == []
    assert_valid(collected.data)


@needs_pyarrow
def test_the_checkpoint_reader_reads_the_checkpoint_of_the_lakehouse_table_only():
    from pqax.collect import ArrowCheckpointReader
    inner = ArrowCheckpointReader()
    paths = []

    def reader(storage, path):
        paths.append(path)
        return inner(storage, path)

    collect_fixture(checkpoint_reader=reader)
    assert paths == [f"{SALES.table_path}/_delta_log/{SALES.delta['checkpointVersion']:020d}.checkpoint.parquet"]


# ---- the checkpoint reader of pyarrow ----

CHECKPOINT = f"{SALES.table_path}/_delta_log/{SALES.delta['checkpointVersion']:020d}.checkpoint.parquet"


@needs_pyarrow
def test_the_arrow_checkpoint_reader_reads_through_the_storage_it_is_given():
    from pqax.collect import ArrowCheckpointReader
    storage = RecordingStorage(fixture_storage())
    actions = ArrowCheckpointReader()(storage, CHECKPOINT)
    assert actions
    assert storage.requests
    assert all(request.path == CHECKPOINT for request in storage.requests)


@needs_pyarrow
def test_the_arrow_checkpoint_reader_gives_the_protocol_the_metadata_the_domains_and_the_removes_of_the_dump():
    from pqax.collect import (AddAction, ArrowCheckpointReader, DomainMetadataAction, MetadataAction, ProtocolAction,
                              RemoveAction)
    actions = ArrowCheckpointReader()(fixture_storage(), CHECKPOINT)
    delta = SALES.delta
    [protocol] = [action for action in actions if isinstance(action, ProtocolAction)]
    assert (protocol.min_reader_version, protocol.min_writer_version) == (delta["minReaderVersion"], delta["minWriterVersion"])
    assert list(protocol.reader_features) == delta["readerFeatures"]
    assert list(protocol.writer_features) == delta["writerFeatures"]
    [metadata] = [action for action in actions if isinstance(action, MetadataAction)]
    assert metadata.id == delta["tableId"]
    assert metadata.provider == delta["provider"]
    assert metadata.created_time == delta["createdTime"]
    assert list(metadata.partition_columns) == delta["partitionColumns"]
    assert dict(metadata.configuration) == delta["configuration"]
    assert json.loads(metadata.schema_string) == json.loads(delta["schemaString"])
    domains = [action for action in actions if isinstance(action, DomainMetadataAction) and not action.removed]
    assert {domain.domain: json.loads(domain.configuration) for domain in domains} == \
        {name: json.loads(value) for name, value in delta["domains"].items()}
    removes = [action for action in actions if isinstance(action, RemoveAction)]
    assert len(removes) == delta["removedFileCount"]
    assert sum(remove.size for remove in removes) == delta["removedFileSize"]
    adds = [action for action in actions if isinstance(action, AddAction)]
    assert sorted(add.size for add in adds) == sorted(file["size"] for file in SALES.files
                                                      if not file["path"].startswith("Year="))


@needs_pyarrow
def test_the_arrow_checkpoint_reader_refuses_a_file_that_is_not_a_checkpoint():
    from pqax.collect import ArrowCheckpointReader
    commit = f"{SALES.table_path}/_delta_log/{12:020d}.json"
    with pytest.raises(Exception):
        ArrowCheckpointReader()(fixture_storage(), commit)


# ---- what the options change ----

@needs_pyarrow
def test_partition_values_are_kept_only_when_asked():
    with_values = collect_fixture(include_partition_values=True)
    doc = with_values.doc
    assert doc.manifest["collection"]["includesPartitionValues"] is True
    sales = row_of(doc, SALES)
    files = of_table(doc, "files", sales["id"])
    partitions = {partition["id"]: partition for partition in rows(doc, "partitions")}
    prefix = SALES.table_path + "/"
    for path in (path[len(prefix):] for path in [p for p in all_data_paths() if p.startswith(prefix)]):
        if path.endswith("part-0004.parquet"):
            continue  # the unreferenced file
        [file] = [row for row in files if row["path"] == path]
        value = path.split("/")[0].split("=", 1)[1] if "/" in path else None
        assert partitions[file["partition"]]["values"] == [value]
    by_value = {tuple(partition["values"]): partition for partition in partitions.values()}
    assert by_value[(None,)]["lastModifiedAt"] == "2023-11-14T22:15:00.000Z"
    assert by_value[("2024",)]["lastModifiedAt"] == "2023-11-14T22:15:10.000Z"
    assert_valid(with_values.data)

    without = collect_fixture()
    assert without.doc.manifest["collection"]["includesPartitionValues"] is False
    assert all(partition.get("values") is None for partition in rows(without.doc, "partitions"))
    assert "Year=2024" not in all_text(without.data)


def all_data_paths():
    from collect_kit import data_files
    return data_files()


@needs_pyarrow
def test_data_values_are_kept_only_when_asked():
    with_values = collect_fixture(include_data_values=True)
    doc = with_values.doc
    assert doc.manifest["collection"]["includesDataValues"] is True
    chunks = rows(doc, "columnChunks")
    assert chunks and all(chunk.get("minValue") is not None and chunk.get("maxValue") is not None for chunk in chunks)
    for dump in TABLES:
        table = row_of(doc, dump)
        commits = {commit["commitId"]: commit for commit in of_table(doc, "commits", table["id"])}
        for commit in dump.commits:
            assert sorted((commits[str(commit["version"])].get("parameters") or {})) == sorted(commit["parameterKeys"])
    assert_valid(with_values.data)

    without = collect_fixture()
    assert all(chunk.get("minValue") is None and chunk.get("maxValue") is None for chunk in rows(without.doc, "columnChunks"))
    assert all(commit.get("parameters") is None for commit in rows(without.doc, "commits"))


@needs_pyarrow
def test_a_user_name_of_a_commit_is_never_written():
    names = []
    for path in FIXTURE.rglob("*.json"):
        for line in path.read_text(encoding="utf-8").splitlines():
            if line.strip():
                name = json.loads(line).get("commitInfo", {}).get("userName")
                if name:
                    names.append(name)
    assert names
    for collected in (collect_fixture(), collect_fixture(include_data_values=True, include_partition_values=True)):
        text = all_text(collected.data)
        assert all(name.lower() not in text.lower() for name in names)
        assert '"userName"' not in text and '"userId"' not in text


@needs_pyarrow
def test_progress_is_reported_once_for_each_table_one_call_at_a_time():
    reports = []
    inside = []
    lock = threading.Lock()

    def progress(report):
        with lock:
            inside.append(1)
            assert len(inside) == 1, "two calls at the same time"
        reports.append(report)
        with lock:
            inside.pop()

    collected = collect_fixture(progress=progress)
    assert all(isinstance(report, CollectionProgress) for report in reports)
    assert len(reports) == len(collected.tables) == 2
    assert [report.tables_completed for report in reports] == [1, 2]
    assert all(report.table_count == 2 for report in reports)
    assert sorted(report.table.path for report in reports) == sorted(table.path for table in collected.tables)
    assert sum(report.footers_read for report in reports) == 5
    assert all(report.issue_count == 0 for report in reports)
    assert reports[0].request_count <= reports[1].request_count
    assert 0 < reports[-1].request_count <= collected.doc.manifest["collection"]["requestCount"]
    assert reports[-1].bytes_read > 0


@needs_pyarrow
def test_one_request_at_a_time_gives_the_same_footprint():
    serial = collect_fixture(parallelism=1)
    parallel = collect_fixture(parallelism=64)
    assert differences(serial.doc, parallel.doc) == []
    assert serial.doc.manifest["collection"]["requestCount"] == parallel.doc.manifest["collection"]["requestCount"]


@needs_pyarrow
@pytest.mark.parametrize("limit", [1, 0])
def test_a_small_commit_limit_keeps_the_state_complete(limit):
    collected = collect_fixture(max_commits=limit)
    doc = collected.doc
    for dump in TABLES:
        table = row_of(doc, dump)
        assert table["fileCount"] == len(dump.files)
        checkpoint = dump.delta.get("checkpointVersion")
        needed = sorted(str(commit["version"]) for commit in dump.commits if checkpoint is None or commit["version"] > checkpoint)
        assert len(needed) == 2
        assert table["commitsRead"] == 2
        assert sorted(commit["commitId"] for commit in of_table(doc, "commits", table["id"])) == needed
    assert rows(doc, "issues") == []
    assert_valid(collected.data)


@needs_pyarrow
def test_a_limit_of_one_footer_reads_one_footer_for_each_table():
    storage = RecordingStorage(fixture_storage())
    collected = collect_fixture(storage=storage, max_footers=1)
    doc = collected.doc
    for dump in TABLES:
        table = row_of(doc, dump)
        assert table["footerCount"] == 1
        assert table["fileCount"] == len(dump.files)
        files = of_table(doc, "files", table["id"])
        [read] = [file for file in files if file.get("footerSize") is not None]
        assert len(storage.reads(lambda path: path.startswith(dump.table_path + "/") and path.endswith(".parquet")
                                 and "/_delta_log/" not in path)) == 1
        assert table["footerSize"] == dump_file_of_size(read["fileSize"])[1]["footer"]["footerSize"]
    assert_valid(collected.data)


@needs_pyarrow
@pytest.mark.parametrize("level", [1, 2])
@pytest.mark.parametrize("depth", ["summary", "files", "rowGroups", "columnChunks"])
@pytest.mark.parametrize("folder_by_folder", [False, True], ids=["recursive", "folder by folder"])
def test_the_unreferenced_files_removal_times_and_partition_times_hold_at_every_level_and_depth(level, depth, folder_by_folder):
    storage = RecordingStorage(fixture_storage()) if folder_by_folder else fixture_storage()
    collected = collect_fixture(storage=storage, level=level, depth=depth)
    doc = collected.doc
    for dump in TABLES:
        table = row_of(doc, dump)
        assert table["unreferencedFileCount"] == dump.dump["unreferencedFileCount"]
        assert table["unreferencedFileSize"] == dump.dump["unreferencedFileSize"]
        oldest, newest = dump.delta.get("removedOldestTimestamp"), dump.delta.get("removedNewestTimestamp")
        assert table.get("nonLiveOldestRemovedAt") == (None if oldest is None else timestamp(oldest))
        assert table.get("nonLiveNewestRemovedAt") == (None if newest is None else timestamp(newest))
        assert table["nonLiveFileCount"] == dump.delta["removedFileCount"]
        assert of_table(doc, "issues", table["id"]) == []
    if depth != "summary":
        sales = row_of(doc, SALES)
        partitions = of_table(doc, "partitions", sales["id"])
        assert sorted(partition["lastModifiedAt"] for partition in partitions) == \
            ["2023-11-14T22:15:00.000Z", "2023-11-14T22:15:10.000Z"]
    assert_valid(collected.data)


# ---- the options ----

def test_the_defaults_of_the_options():
    options = CollectorOptions()
    assert options.level == 2
    assert options.depth == DetailDepth.COLUMN_CHUNKS
    assert options.max_footers_per_table == 0
    assert options.max_commits_per_table == 30
    assert options.parallelism == 16
    assert options.include_data_values is False
    assert options.include_partition_values is False
    assert options.footer_tail_bytes == 32768
    assert options.footer_tail_margin == 1.25
    assert options.footer_tail_floor_bytes == 8192
    options.validate()
    CollectorOptions(level=1, parallelism=1, footer_tail_bytes=12, max_commits_per_table=0).validate()
    CollectorOptions(footer_tail_bytes=12, footer_tail_floor_bytes=12, footer_tail_margin=1).validate()


@pytest.mark.parametrize("setting", [
    {"footer_tail_margin": 1}, {"footer_tail_margin": 3.5}, {"footer_tail_floor_bytes": 12},
    {"footer_tail_floor_bytes": 32768}, {"footer_tail_floor_bytes": 40000}, {"footer_tail_bytes": 12},
    {"footer_tail_bytes": 4096}, {"depth": DetailDepth.SUMMARY}, {"max_footers_per_table": 5},
])
def test_an_option_in_range_is_accepted(setting):
    CollectorOptions(**setting).validate()


@pytest.mark.parametrize("setting", [
    {"level": 0}, {"level": 3}, {"depth": 7}, {"max_footers_per_table": -1}, {"max_commits_per_table": -1},
    {"parallelism": 0}, {"footer_tail_bytes": 11}, {"footer_tail_margin": 0.99}, {"footer_tail_margin": 0},
    {"footer_tail_margin": float("nan")}, {"footer_tail_margin": float("inf")}, {"footer_tail_floor_bytes": 11},
    {"footer_tail_floor_bytes": 0},
])
def test_an_option_out_of_range_is_refused(setting):
    with pytest.raises(ValueError):
        CollectorOptions(**setting).validate()
    with pytest.raises(ValueError):
        collect(fixture_storage(), [], options=CollectorOptions(**setting), checkpoint_reader=None)


@pytest.mark.parametrize("keywords", [{"level": 3}, {"depth": "deep"}, {"parallelism": 0}, {"max_commits": -1},
                                      {"max_footers": -1}])
def test_a_keyword_out_of_range_is_refused(keywords):
    with pytest.raises(ValueError):
        collect(fixture_storage(), [], checkpoint_reader=None, **keywords)


def test_an_empty_list_of_tables_gives_an_empty_valid_footprint():
    data, builder = collect_bytes(fixture_storage(), [], checkpoint_reader=None)
    assert_valid(data)
    assert rows(open_bytes(data), "tables") == []
    assert builder.collection["requestCount"] == 0


def test_the_kinds_of_sources():
    assert SourceKinds.ONELAKE_LAKEHOUSE == "OneLake lakehouse"
    assert SourceKinds.ONELAKE_WAREHOUSE == "OneLake warehouse"
    kinds = [SourceKinds.ONELAKE_LAKEHOUSE, SourceKinds.ONELAKE_WAREHOUSE, SourceKinds.ONELAKE_MIRRORED_DATABASE,
             SourceKinds.ONELAKE_SQL_DATABASE, SourceKinds.ONELAKE_ITEM, SourceKinds.STORAGE_FOLDER]
    assert len(set(kinds)) == 6
    assert len(ISSUE_CODES) == 17


# ---- the dump ----

DUMP_TABLES = [pytest.param(dump, marks=needs_pyarrow, id=dump.id) if dump is SALES else pytest.param(dump, id=dump.id)
               for dump in TABLES]


def file_of(doc, table, dumped):
    [row] = [file for file in of_table(doc, "files", table["id"]) if file["fileSize"] == dumped["size"]]
    return row


@pytest.mark.parametrize("dump", DUMP_TABLES)
def test_files_have_the_sizes_rows_deleted_rows_and_footers_of_the_dump(any_reader, dump):
    doc = any_reader.doc
    table = row_of(doc, dump)
    files = of_table(doc, "files", table["id"])
    assert len(files) == len(dump.files) == len({file["fileSize"] for file in files})
    for dumped in dump.files:
        row = file_of(doc, table, dumped)
        footer = dumped["footer"]
        assert row["modifiedAt"] == timestamp(dumped["modificationTime"])
        assert row["rowCount"] == dumped.get("numRecords", footer["numRows"])
        assert row["deletedRowCount"] == dumped.get("deletedRows", 0)
        assert row.get("deletionVectorSize") == dumped.get("deletionVectorSize")
        assert row["footerSize"] == footer["footerSize"]
        assert row["rowGroupCount"] == len(row_groups(dumped))
        assert row["compressedSize"] == chunk_sum(dumped, "compressed")
        assert row["uncompressedSize"] == chunk_sum(dumped, "uncompressed")
        assert row["isVOrdered"] == is_vordered(dumped)


@pytest.mark.parametrize("dump", DUMP_TABLES)
def test_row_groups_and_column_chunks_are_those_of_the_footers(any_reader, dump):
    doc = any_reader.doc
    table = row_of(doc, dump)
    ordinals = {column["id"]: column["ordinal"] for column in rows(doc, "physicalColumns")}
    for dumped in dump.files:
        row = file_of(doc, table, dumped)
        groups = sorted((group for group in rows(doc, "rowGroups") if group["file"] == row["id"]),
                        key=lambda group: group["ordinal"])
        assert len(groups) == len(row_groups(dumped))
        for g, (group, expected) in enumerate(zip(groups, row_groups(dumped))):
            assert group["ordinal"] == g
            assert (group["rowCount"], group["uncompressedSize"], group["compressedSize"]) == \
                (expected["numRows"], expected["totalByteSize"], expected["totalCompressedSize"])
            chunks = sorted((chunk for chunk in rows(doc, "columnChunks") if chunk["rowGroup"] == group["id"]),
                            key=lambda chunk: ordinals[chunk["physicalColumn"]])
            assert len(chunks) == len(expected["codec"])
            for c, chunk in enumerate(chunks):
                where = f"{dump.id} file of {row['fileSize']} bytes, row group {g}, column {c}"
                assert ordinals[chunk["physicalColumn"]] == c, where
                assert chunk["compressedSize"] == expected["compressed"][c], where
                assert chunk["uncompressedSize"] == expected["uncompressed"][c], where
                assert chunk["valueCount"] == expected["numValues"][c], where
                assert chunk.get("nullCount") == expected["nullCount"][c], where
                assert chunk.get("distinctCount") == expected["distinctCount"][c], where
                assert chunk["codec"] == expected["codec"][c], where
                assert chunk["encodings"] == encodings_of(expected, c), where
                dictionary = expected["dictionaryPageOffset"][c]
                assert chunk["hasDictionaryPage"] == (dictionary is not None), where
                assert chunk.get("dictionaryPageSize") == (None if dictionary is None else expected["dataPageOffset"][c] - dictionary), where
                assert chunk.get("bloomFilterSize") == expected["bloomFilterLength"][c], where
                assert chunk.get("unencodedByteArrayDataBytes") == expected["unencodedByteArrayBytes"][c], where


@pytest.mark.parametrize("dump", DUMP_TABLES)
def test_table_totals_are_the_sums_over_the_dump(any_reader, dump):
    table = row_of(any_reader.doc, dump)
    files = dump.files
    assert table["format"] == "Delta"
    assert table["detailDepth"] == "Column chunks"
    assert table["collectedLevel"] == 2
    assert table["fileCount"] == len(files)
    assert table["fileSize"] == sum(file["size"] for file in files)
    assert table["rowCount"] == sum(file.get("numRecords", file["footer"]["numRows"]) for file in files)
    assert table["deletedRowCount"] == sum(file.get("deletedRows", 0) for file in files)
    assert table["lastModifiedAt"] == timestamp(max(file["modificationTime"] for file in files))
    assert table["vOrderedFileCount"] == sum(1 for file in files if is_vordered(file))
    assert table["footerCount"] == len(files)
    assert table["rowGroupCount"] == sum(len(row_groups(file)) for file in files)
    assert table["compressedSize"] == sum(chunk_sum(file, "compressed") for file in files)
    assert table["uncompressedSize"] == sum(chunk_sum(file, "uncompressed") for file in files)
    assert table["footerSize"] == sum(file["footer"]["footerSize"] for file in files)
    counts = sorted(group["numRows"] for file in files for group in row_groups(file))
    assert (table["rowGroupRowCounts"]["count"], table["rowGroupRowCounts"]["sum"], table["rowGroupRowCounts"]["minimum"],
            table["rowGroupRowCounts"]["maximum"]) == (len(counts), sum(counts), counts[0], counts[-1])
    sizes = sorted(file["size"] for file in files)
    assert (table["fileSizes"]["count"], table["fileSizes"]["sum"], table["fileSizes"]["minimum"],
            table["fileSizes"]["maximum"]) == (len(sizes), sum(sizes), sizes[0], sizes[-1])
    partitioned = bool(dump.delta["partitionColumns"])
    expected = len({canonical(file["partition"]) for file in files}) if partitioned else None
    assert table.get("partitionCount") == expected


@pytest.mark.parametrize("dump", DUMP_TABLES)
def test_the_delta_log_details_are_those_of_the_dump(any_reader, dump):
    table = row_of(any_reader.doc, dump)
    delta = dump.delta
    assert table["tableId"] == delta["tableId"]
    assert table["snapshotId"] == str(delta["version"])
    assert table["createdAt"] == timestamp(delta["createdTime"])
    assert table["properties"] == delta["configuration"]
    assert table["metadataFileCount"] == delta["logFileCount"]
    assert table["metadataSize"] == delta["logSize"]
    assert table["nonLiveFileCount"] == delta["removedFileCount"]
    assert table["nonLiveFileSize"] == delta["removedFileSize"]
    assert table["commitCount"] == delta["commitFileCount"]
    assert table["commitsRead"] == len(dump.commits)
    assert table["oldestCommitAt"] == timestamp(min(commit["timestamp"] for commit in dump.commits))
    [last] = [commit for commit in dump.commits if commit["version"] == delta["version"]]
    assert table["snapshotTimestamp"] == timestamp(last["timestamp"])
    details = table["delta"]
    assert (details["minReaderVersion"], details["minWriterVersion"]) == (delta["minReaderVersion"], delta["minWriterVersion"])
    assert details["readerFeatures"] == delta["readerFeatures"]
    assert details["writerFeatures"] == delta["writerFeatures"]
    assert details.get("checkpointVersion") == delta.get("checkpointVersion")
    assert sorted(details.get("domains") or []) == sorted(delta["domains"])
    if "checkpointVersion" in delta:
        assert details["commitsAfterCheckpoint"] == delta["version"] - delta["checkpointVersion"]


@pytest.mark.parametrize("dump", DUMP_TABLES)
def test_columns_follow_the_schema_string_and_the_clustering_domain(any_reader, dump):
    doc = any_reader.doc
    table = row_of(doc, dump)
    fields = json.loads(dump.delta["schemaString"])["fields"]
    columns = sorted(of_table(doc, "columns", table["id"]), key=lambda column: column["id"])
    assert [(column["name"], column["dataType"], column["isNullable"], column["isPartitionColumn"]) for column in columns] == \
        [(field["name"], field["type"], field["nullable"], field["name"] in dump.delta["partitionColumns"]) for field in fields]
    clustering = dump.delta["domains"].get("delta.clustering")
    if clustering:
        names = [path[0] for path in json.loads(clustering)["clusteringColumns"]]
        ids = {column["name"]: column["id"] for column in columns}
        assert table["clusteringColumns"] == [ids[name] for name in names]
    else:
        assert table.get("clusteringColumns") is None


@pytest.mark.parametrize("dump", DUMP_TABLES)
def test_physical_schemas_and_columns_are_those_of_the_footers(any_reader, dump):
    doc = any_reader.doc
    table = row_of(doc, dump)
    schemas = of_table(doc, "physicalSchemas", table["id"])
    assert len(schemas) == len(dump.schemas)
    columns = {column["name"]: column["id"] for column in of_table(doc, "columns", table["id"])}
    for schema in schemas:
        files = [file for file in of_table(doc, "files", table["id"]) if file["physicalSchema"] == schema["id"]]
        assert schema["fileCount"] == len(files)
        [dumped] = [file for file in dump.files if file["size"] == files[0]["fileSize"]]
        leaves = dump.schemas[dumped["footer"]["schema"]]["columns"]
        physical = sorted((column for column in rows(doc, "physicalColumns") if column["physicalSchema"] == schema["id"]),
                          key=lambda column: column["ordinal"])
        assert [(column["ordinal"], column["path"], column["physicalType"], column.get("repetition"),
                 column.get("logicalType"), column.get("column")) for column in physical] == \
            [(index, leaf["path"], leaf["type"], leaf.get("repetition"), leaf.get("converted"), columns.get(leaf["path"][0]))
             for index, leaf in enumerate(leaves)]


@pytest.mark.parametrize("dump", DUMP_TABLES)
def test_writers_group_the_files_by_writer_and_v_order(any_reader, dump):
    doc = any_reader.doc
    table = row_of(doc, dump)
    groups = {}
    for file in dump.files:
        key = (file["footer"]["createdBy"], is_vordered(file), key_value(file["footer"], "vorder.level"))
        count, size = groups.get(key, (0, 0))
        groups[key] = (count + 1, size + file["size"])
    writers = of_table(doc, "writers", table["id"])
    assert sorted((writer["createdBy"], writer["isVOrdered"], writer.get("vOrderLevel"), writer["fileCount"],
                   writer["fileSize"]) for writer in writers) == \
        sorted((key[0], key[1], key[2], count, size) for key, (count, size) in groups.items())


@pytest.mark.parametrize("dump", DUMP_TABLES)
def test_operations_and_commits_are_those_of_the_commits_read(any_reader, dump):
    doc = any_reader.doc
    table = row_of(doc, dump)
    expected = {}
    for commit in dump.commits:
        count, last = expected.get(commit["operation"], (0, 0))
        expected[commit["operation"]] = (count + 1, max(last, commit["timestamp"]))
    operations = of_table(doc, "operations", table["id"])
    assert sorted((operation["name"], operation["commitCount"], operation["lastAt"]) for operation in operations) == \
        sorted((name, count, timestamp(last)) for name, (count, last) in expected.items())
    commits = {commit["commitId"]: commit for commit in of_table(doc, "commits", table["id"])}
    assert len(commits) == len(dump.commits)
    for wanted in dump.commits:
        commit = commits[str(wanted["version"])]
        assert commit["timestamp"] == timestamp(wanted["timestamp"])
        assert commit["operation"] == wanted["operation"]
        assert commit["engine"] == wanted["engine"]
        assert commit.get("isBlindAppend") == wanted.get("isBlindAppend")
        assert commit["metrics"] == wanted["metrics"]
        assert commit.get("parameters") is None


@pytest.mark.parametrize("dump", DUMP_TABLES)
def test_the_sources_are_the_items_of_the_fixture(any_reader, dump):
    doc = any_reader.doc
    table = row_of(doc, dump)
    [source] = [source for source in rows(doc, "sources") if source["id"] == table["source"]]
    assert (source["name"], source["kind"]) == (dump.item, dump.source_kind)
    assert len(rows(doc, "sources")) == 2
    assert_local_locations(doc, [dump])
