"""OneLakeStorage against a fake OneLake DFS endpoint on 127.0.0.1 (collect_kit.FakeDfs): the listing in pages, the read
of a range, the token in the Authorization header, the retries, refused requests, the trace, the locations and the
identifiers of the discovery, and footer reads that fail over HTTP. Nothing leaves the machine. The collection of the
whole fixture needs pyarrow for the checkpoint of its lakehouse table; everything else runs on the standard library.
"""
from __future__ import annotations

import math
import re
import threading

import pytest

from collect_kit import (FIXTURE, HAS_PYARROW, ORDERS, SALES, TABLES, FakeDfs, Lake, StateCheckpointReader, add,
                         assert_valid, collect_bytes, commit_info, data_files, differences, discover_all, error_answer,
                         file_bytes, is_location, metadata, of_table, open_bytes, protocol, range_of, remove, row_of,
                         rows, EPOCH, timestamp)
from pqax.collect import LocalStorage, OneLakeStorage, SourceKinds, StorageError, discover, list_items
from test_collect_failures import LARGEST, TABLE as COPIED_TABLE, TAGGED, assert_footer_not_read, copied_lake

WORKSPACE = "Test Workspace"
TOKEN = "test-token-not-a-secret-7Q2"
WORKSPACE_ID = "aaaaaaaa-bbbb-4ccc-8ddd-000000000001"
ITEM_ID = "aaaaaaaa-bbbb-4ccc-8ddd-000000000002"
needs_pyarrow = pytest.mark.skipif(not HAS_PYARROW, reason="the checkpoint of the fixture needs pyarrow, pqax[collect]")


class Tokens:
    """A token provider that counts its calls."""

    def __init__(self, token=TOKEN):
        self.token = token
        self.calls = 0
        self._lock = threading.Lock()

    def __call__(self):
        with self._lock:
            self.calls += 1
        return self.token


def onelake(fake, workspace=WORKSPACE, tokens=None, **options):
    options.setdefault("max_retries", 0)
    return OneLakeStorage(workspace, tokens or Tokens(), endpoint=fake.endpoint, **options)


def abfss(fake, workspace, path):
    return f"abfss://{workspace}@{fake.host}/{path}"


# ---- the collection ----

def assert_remote_like_local(fake, storage, tokens, tables, **options):
    discovered = discover_all(storage)
    before_requests = storage.request_count
    remote, builder = collect_bytes(storage, discovered, **options)
    local, _ = collect_bytes(LocalStorage(str(FIXTURE)), [table for table in discover_all(LocalStorage(str(FIXTURE)))
                                                          if table.path in {t.path for t in discovered}], **options)
    assert_valid(remote)
    a, b = open_bytes(remote), open_bytes(local)
    assert differences(a, b, is_location) == []
    for dump in tables:
        table = row_of(a, dump)
        [source] = [source for source in rows(a, "sources") if source["id"] == table["source"]]
        assert source["location"] == abfss(fake, WORKSPACE, dump.item_folder)
        assert source.get("identifiers") is None
        assert table["location"] == abfss(fake, WORKSPACE, dump.table_path)
        assert table["unreferencedFileCount"] == dump.dump["unreferencedFileCount"]
        assert table["unreferencedFileSize"] == dump.dump["unreferencedFileSize"]

    calls = fake.recorded()
    assert calls
    for call in calls:
        assert call.method == "GET"
        assert call.authorization == "Bearer " + TOKEN
        assert call.raw_path.startswith("/Test%20Workspace")
    assert tokens.calls == len(calls) == storage.request_count
    assert builder.collection["requestCount"] == storage.request_count - before_requests
    assert any("continuation" in call.query for call in calls)
    assert any(call.range is not None for call in calls)
    recursive = [call for call in calls if call.query.get("recursive") == "true"]
    assert any("continuation" in call.query for call in recursive)
    for dump in tables:
        assert any(call.query.get("directory") == dump.table_path for call in recursive), dump.table_path


@needs_pyarrow
def test_collecting_the_fixture_through_onelake_gives_the_footprint_of_the_folder():
    tokens = Tokens()
    with FakeDfs(FIXTURE, WORKSPACE) as fake:
        assert_remote_like_local(fake, onelake(fake, tokens=tokens), tokens, TABLES)


def test_collecting_the_warehouse_of_the_fixture_through_onelake_gives_the_footprint_of_the_folder():
    tokens = Tokens()
    with FakeDfs(FIXTURE, WORKSPACE) as fake:
        storage = onelake(fake, tokens=tokens)
        discovered = [table for table in discover_all(storage) if table.path == ORDERS.table_path]
        before = storage.request_count
        remote, builder = collect_bytes(storage, discovered, checkpoint_reader=None)
        local, _ = collect_bytes(LocalStorage(str(FIXTURE)), items=[ORDERS.item_folder], checkpoint_reader=None)
        assert_valid(remote)
        assert differences(open_bytes(remote), open_bytes(local), is_location) == []
        doc = open_bytes(remote)
        table = row_of(doc, ORDERS)
        assert table["location"] == abfss(fake, WORKSPACE, ORDERS.table_path)
        assert builder.collection["requestCount"] == storage.request_count - before
        assert all(call.authorization == "Bearer " + TOKEN for call in fake.recorded())
        assert tokens.calls == len(fake.recorded())


# ---- listings and reads ----

def test_a_listing_follows_the_continuation_of_every_page():
    log = ORDERS.table_path + "/_delta_log"
    with FakeDfs(FIXTURE, WORKSPACE, page_size=1) as fake:
        storage = onelake(fake)
        entries = storage.list(log)
        local = LocalStorage(str(FIXTURE)).list(log)
        assert [(e.path, e.name, e.is_directory, e.size) for e in entries] == \
            [(e.path, e.name, e.is_directory, e.size) for e in local]
        assert all(entry.last_modified is not None and entry.last_modified.utcoffset().total_seconds() == 0
                   for entry in entries)
        assert len(fake.recorded()) == len(local) == 2
        assert storage.request_count == 2
        assert fake.recorded()[1].query.get("continuation") == "1"


def test_the_root_is_listed_without_a_directory_parameter():
    with FakeDfs(FIXTURE, WORKSPACE, page_size=100) as fake:
        storage = onelake(fake)
        root = storage.list("")
        [call] = fake.recorded()
        assert call.query.get("resource") == "filesystem"
        assert call.query.get("recursive") == "false"
        assert "directory" not in call.query
        local = LocalStorage(str(FIXTURE)).list("")
        assert [(e.path, e.is_directory) for e in root] == [(e.path, e.is_directory) for e in local]
        assert list_items(storage) == sorted({table.item_folder for table in TABLES})

        assert storage.list("Item.Lakehouse/Tables") == []
        assert fake.recorded()[-1].query.get("directory") == "Item.Lakehouse/Tables"
        assert storage.list(SALES.item_folder + "/Tables")
        assert fake.recorded()[-1].query.get("directory") == SALES.item_folder + "/Tables"


@pytest.mark.parametrize("ignore_range", [False, True], ids=["206", "200 with the whole file"])
def test_a_read_returns_the_range_even_when_the_server_sends_the_whole_file(ignore_range):
    path = data_files()[0]
    data = file_bytes(path)
    with FakeDfs(FIXTURE, WORKSPACE, ignore_range=ignore_range) as fake:
        storage = onelake(fake)
        assert storage.read_range(path, 10, 100) == data[10:110]
        assert storage.read_range(path, len(data) - 5, 100) == data[-5:]
        assert storage.read_range(path, len(data), 10) == b""
        ranged = [call for call in fake.recorded() if call.range is not None]
        assert range_of(ranged[0]) == (10, 109)
        if not ignore_range:  # a server that sends the whole file makes the storage read it all
            assert storage.bytes_read == 105


def test_a_missing_folder_lists_as_empty_and_a_missing_file_is_not_found():
    with FakeDfs(FIXTURE, WORKSPACE) as fake:
        storage = onelake(fake)
        assert storage.list("Nothing.Lakehouse/Tables") == []
        with pytest.raises(StorageError) as caught:
            storage.read_range(SALES.table_path + "/no_such_file.parquet", 0, 10)
        assert caught.value.is_not_found and caught.value.status == 404
        assert "no_such_file" not in str(caught.value)


@pytest.mark.parametrize("status", [401, 403])
def test_a_refused_request_is_a_storage_error_that_names_no_path_and_no_token(status):
    with FakeDfs(FIXTURE, WORKSPACE, answer=lambda call: error_answer(status, "Forbidden")) as fake:
        storage = onelake(fake)
        errors = []
        for attempt in (lambda: storage.list(SALES.table_path + "/_delta_log"),
                        lambda: storage.read_range(data_files()[0], 0, 10)):
            with pytest.raises(StorageError) as caught:
                attempt()
            errors.append(caught.value)
        for error in errors:
            assert error.is_access_denied
            assert error.status == status
            assert not error.is_not_found
            for hidden in (TOKEN, SALES.item, "Workspace", "Bearer"):
                assert hidden not in str(error)
        assert len(fake.recorded()) == 2


def test_a_transient_failure_is_retried_after_retry_after_and_counted():
    path = data_files()[0]
    seen = []

    def once(call):
        seen.append(call)
        return (503, {"Retry-After": "0"}, b"") if len(seen) == 1 else None

    with FakeDfs(FIXTURE, WORKSPACE, answer=once) as fake:
        tokens = Tokens()
        storage = onelake(fake, tokens=tokens, max_retries=1)
        assert storage.read_range(path, 0, 10) == file_bytes(path)[:10]
        assert len(fake.recorded()) == 2
        assert storage.request_count == 2
        assert tokens.calls == 2  # the token provider is called before every request

    with FakeDfs(FIXTURE, WORKSPACE, answer=lambda call: (503, {"Retry-After": "0"}, b"")) as fake:
        storage = onelake(fake, max_retries=0)
        with pytest.raises(StorageError) as caught:
            storage.read_range(path, 0, 10)
        assert caught.value.status == 503
        assert len(fake.recorded()) == 1

    with FakeDfs(FIXTURE, WORKSPACE, answer=lambda call: (503, {"Retry-After": "0"}, b"")) as fake:
        storage = onelake(fake, max_retries=2)
        with pytest.raises(StorageError):
            storage.list("")
        assert len(fake.recorded()) == 3


def test_a_request_without_an_answer_is_a_storage_error_with_no_status():
    with FakeDfs(FIXTURE, WORKSPACE, answer=lambda call: 1.5) as fake:
        storage = onelake(fake, timeout=0.3, max_retries=0)
        with pytest.raises(StorageError) as caught:
            storage.read_range(data_files()[0], 0, 10)
        error = caught.value
        assert error.status is None
        assert not error.is_access_denied and not error.is_not_found
        assert TOKEN not in str(error)
        assert storage.request_count == 1


def test_the_trace_has_one_line_for_each_answer_and_never_the_token():
    lines = []
    with FakeDfs(FIXTURE, WORKSPACE) as fake:
        storage = onelake(fake, trace=lines.append)
        storage.list(ORDERS.table_path)
        storage.read_range(data_files()[0], 0, 10)
        storage.list("Nothing.Lakehouse/Tables")
        assert len(lines) == len(fake.recorded()) == storage.request_count
    pattern = re.compile(r"^GET http://127\.0\.0\.1:\d+/\S+ -> (\d{3})$")
    statuses = []
    for line in lines:
        match = pattern.match(line)
        assert match, line
        statuses.append(match.group(1))
        assert TOKEN not in line and "Bearer" not in line
    assert statuses[-1] == "404"
    assert "206" in statuses or "200" in statuses


def test_only_get_requests_are_sent():
    with FakeDfs(FIXTURE, WORKSPACE) as fake:
        storage = onelake(fake)
        discover_all(storage)
        storage.read_range(data_files()[0], 0, 10)
        assert {call.method for call in fake.recorded()} == {"GET"}


def test_wrong_arguments_are_refused():
    with pytest.raises(ValueError):
        OneLakeStorage("", Tokens())
    with pytest.raises(ValueError):
        OneLakeStorage(WORKSPACE, Tokens(), max_retries=-1)


# ---- locations and identifiers ----

def workspace_lake(root, item):
    """A workspace with one item and two tables, one of them under a schema, each with a log of one commit."""
    lake = Lake(root)
    for table in (f"{item}/Tables/plain", f"{item}/Tables/schema1/nested"):
        lake.commit(table, 0, protocol(), metadata(), commit_info(0))
    return lake


def test_by_names_the_locations_are_abfss_uris_with_the_host_of_the_endpoint_and_no_identifiers(tmp_path):
    lake = workspace_lake(tmp_path, "Sales.Lakehouse")
    with FakeDfs(lake.root, "My Workspace") as fake:
        storage = onelake(fake, "My Workspace")
        tables = discover(storage, "Sales.Lakehouse")
        source = f"abfss://My Workspace@{fake.host}/Sales.Lakehouse"
        assert len(tables) == 2
        for table in tables:
            assert (table.source_kind, table.source_name, table.source_location) == (SourceKinds.ONELAKE_LAKEHOUSE, "Sales", source)
            assert not table.source_identifiers
        assert {table.name: table.location for table in tables} == {"plain": source + "/Tables/plain",
                                                                    "nested": source + "/Tables/schema1/nested"}
        # The requests escape the workspace; the locations keep it as the caller gave it.
        assert all(call.raw_path.startswith("/My%20Workspace") for call in fake.recorded())
        assert storage.location_of("Sales.Lakehouse") == source
        assert not storage.identifiers_of("Sales.Lakehouse")

        data, _ = collect_bytes(storage, tables, checkpoint_reader=None)
        assert_valid(data)
        doc = open_bytes(data)
        [row] = rows(doc, "sources")
        assert (row["kind"], row["name"], row["location"], row.get("identifiers")) == (SourceKinds.ONELAKE_LAKEHOUSE, "Sales", source, None)
        assert {table["name"]: table["location"] for table in rows(doc, "tables")} == \
            {"plain": source + "/Tables/plain", "nested": source + "/Tables/schema1/nested"}
        assert rows(doc, "issues") == []


def test_by_ids_the_locations_use_the_ids_and_the_identifiers_hold_them(tmp_path):
    lake = workspace_lake(tmp_path, ITEM_ID)
    identifiers = {"workspaceId": WORKSPACE_ID, "itemId": ITEM_ID}
    with FakeDfs(lake.root, WORKSPACE_ID) as fake:
        storage = onelake(fake, WORKSPACE_ID)
        tables = discover(storage, ITEM_ID)
        source = f"abfss://{WORKSPACE_ID}@{fake.host}/{ITEM_ID}"
        assert len(tables) == 2
        for table in tables:
            assert (table.source_kind, table.source_name, table.source_location) == (SourceKinds.ONELAKE_ITEM, ITEM_ID, source)
            assert dict(table.source_identifiers) == identifiers
        assert dict(storage.identifiers_of(ITEM_ID)) == identifiers
        data, _ = collect_bytes(storage, tables, checkpoint_reader=None)
        doc = open_bytes(data)
        [row] = rows(doc, "sources")
        assert (row["kind"], row["name"], row["location"]) == (SourceKinds.ONELAKE_ITEM, ITEM_ID, source)
        assert row["identifiers"] == identifiers
        assert {table["name"]: table["location"] for table in rows(doc, "tables")} == \
            {"plain": source + "/Tables/plain", "nested": source + "/Tables/schema1/nested"}
        assert_valid(data)


@pytest.mark.parametrize("workspace, item, workspace_id, item_id", [
    (WORKSPACE_ID, ITEM_ID, WORKSPACE_ID, ITEM_ID),
    ("My Workspace", ITEM_ID, None, ITEM_ID),
    (WORKSPACE_ID, "Sales.Lakehouse", WORKSPACE_ID, None),
    ("My Workspace", "Sales.Lakehouse", None, None),
    (WORKSPACE_ID.replace("-", ""), ITEM_ID.replace("-", ""), None, None),
    ("{" + WORKSPACE_ID + "}", "(" + ITEM_ID + ")", None, None),
], ids=["ids", "item id", "workspace id", "names", "no hyphens", "braces"])
def test_an_identifier_is_recorded_only_for_a_guid_with_hyphens(tmp_path, workspace, item, workspace_id, item_id):
    lake = workspace_lake(tmp_path, item)
    expected = {name: value for name, value in (("workspaceId", workspace_id), ("itemId", item_id)) if value}
    with FakeDfs(lake.root, workspace) as fake:
        storage = onelake(fake, workspace)
        tables = discover(storage, item)
        assert len(tables) == 2
        for table in tables:
            assert table.source_location == f"abfss://{workspace}@{fake.host}/{item}"
            assert dict(table.source_identifiers or {}) == expected


@pytest.mark.parametrize("endpoint, host", [
    (None, "onelake.dfs.fabric.microsoft.com"),
    ("https://onelake.test", "onelake.test"),
    ("https://onelake.test:8443", "onelake.test:8443"),
    ("https://account.dfs.core.windows.net", "account.dfs.core.windows.net"),
])
def test_the_location_has_the_host_of_the_endpoint(endpoint, host):
    options = {} if endpoint is None else {"endpoint": endpoint}
    storage = OneLakeStorage("My Workspace", Tokens(), **options)
    assert storage.location_of("Sales.Lakehouse") == f"abfss://My Workspace@{host}/Sales.Lakehouse"
    assert storage.location_of("Sales.Lakehouse/Tables/sales") == f"abfss://My Workspace@{host}/Sales.Lakehouse/Tables/sales"
    assert storage.request_count == 0


# ---- what the collection makes of refused and failed requests ----

def test_access_denied_to_a_log_becomes_an_issue_of_the_table():
    log = ORDERS.table_path + "/_delta_log"

    def deny(call):
        return error_answer(403, "Forbidden") if call.query.get("directory") == log else None

    with FakeDfs(FIXTURE, WORKSPACE, answer=deny) as fake:
        storage = onelake(fake)
        data, _ = collect_bytes(storage, items=[ORDERS.item_folder], checkpoint_reader=None)
        assert_valid(data)
        doc = open_bytes(data)
        table = row_of(doc, ORDERS)
        assert "AccessDenied" in [issue["code"] for issue in of_table(doc, "issues", table["id"])]
        assert table["detailDepth"] == "Summary"


def test_the_recursive_listing_of_onelake_gives_the_same_unreferenced_files_as_local_storage(tmp_path):
    table = "Synth.Lakehouse/Tables/events"
    lake = Lake(tmp_path)
    lake.commit(table, 0, protocol(), metadata(), commit_info(0), add("a.parquet", 10, 1), add("dir%20one/b.parquet", 20, 2))
    lake.commit(table, 1, commit_info(1), remove("a.parquet", 10, EPOCH + 61_000))
    for path, size in (("a.parquet", 10), ("dir one/b.parquet", 20), ("dir one/stray.PARQUET", 3),
                       ("p=1/_change_data/x.parquet", 11), ("_change_data/cdc.parquet", 13),
                       ("_delta_log/stray.parquet", 15), ("notes.txt", 17)):
        lake.data(table, path, size)
    with FakeDfs(lake.root, WORKSPACE, page_size=2) as fake:
        storage = onelake(fake)
        remote, _ = collect_bytes(storage, level=1, checkpoint_reader=StateCheckpointReader())
        local, _ = collect_bytes(LocalStorage(str(lake.root)), level=1, checkpoint_reader=StateCheckpointReader())
        [a] = rows(open_bytes(remote), "tables")
        [b] = rows(open_bytes(local), "tables")
        fields = ("unreferencedFileCount", "unreferencedFileSize", "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt",
                  "nonLiveFileCount")
        assert [a.get(name) for name in fields] == [b.get(name) for name in fields]
        assert (a["unreferencedFileCount"], a["unreferencedFileSize"]) == (3, 24)
        assert a["nonLiveOldestRemovedAt"] == timestamp(EPOCH + 61_000)
        assert rows(open_bytes(remote), "issues") == []
        calls = fake.recorded()
        assert any(call.query.get("recursive") == "true" and "continuation" in call.query for call in calls)
        assert len(calls) == storage.request_count


# ---- footer reads that fail over HTTP ----

FAILURES = [("401", "AccessDenied"), ("403", "AccessDenied"), ("404", "FooterNotRead"), ("400", "FooterNotRead"),
            ("500", "FooterNotRead"), ("503 after a retry", "FooterNotRead"), ("timeout", "FooterNotRead"),
            ("416", "FooterInvalid"), ("short", "FooterInvalid")]


def failing(target, failure, folder):
    """An answer that fails every read of one file and lets every other request through."""
    def answer(call):
        if not call.reads(target):
            return None
        if failure == "timeout":
            return 1.5
        if failure == "short":
            start, stop = range_of(call)
            data = (folder / target).read_bytes()[start:stop + 1]
            return 206, {}, data[:len(data) // 2]
        if failure == "503 after a retry":
            return 503, {"Retry-After": "0"}, b""
        return error_answer(int(failure), "Failure")
    return answer


@pytest.mark.parametrize("failure, code", FAILURES, ids=[failure for failure, _ in FAILURES])
def test_a_footer_read_that_fails_on_onelake_becomes_an_issue_with_the_code_of_the_failure(tmp_path, failure, code):
    lake = copied_lake(tmp_path)
    target = f"{COPIED_TABLE}/{LARGEST}"
    retried = failure == "503 after a retry"
    with FakeDfs(lake.root, WORKSPACE, answer=failing(target, failure, lake.root)) as fake:
        storage = onelake(fake, max_retries=1 if retried else 0, timeout=0.4 if failure == "timeout" else 30)
        data, _ = collect_bytes(storage, checkpoint_reader=None)
        assert_valid(data)
        assert_footer_not_read(data, LARGEST, code)
        assert len([call for call in fake.recorded() if call.reads(target)]) == (2 if retried else 1)
        doc = open_bytes(data)
        [issue] = rows(doc, "issues")
        assert TOKEN not in issue["message"]


def test_a_tagged_file_whose_footer_is_refused_keeps_its_v_order(tmp_path):
    lake = copied_lake(tmp_path)
    target = f"{COPIED_TABLE}/{TAGGED}"
    with FakeDfs(lake.root, WORKSPACE, answer=failing(target, "403", lake.root)) as fake:
        data, _ = collect_bytes(onelake(fake), checkpoint_reader=None)
    assert_footer_not_read(data, TAGGED, "AccessDenied")
    [file] = [row for row in rows(open_bytes(data), "files") if row["path"] == TAGGED]
    assert file["isVOrdered"] is True
