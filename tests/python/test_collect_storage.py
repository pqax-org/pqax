"""LocalStorage, LocalStorage.mount, the discovery of items and tables, and the locations that the discovery records
(README of the C# collector: Public API and Locations; the interface of the Python collector). The tables that only the
lakehouse table of the fixture gives need pyarrow for its checkpoint; the others run on the standard library.
"""
from __future__ import annotations

import dataclasses
import datetime
import os
import pathlib

import pytest

from collect_kit import (FIXTURE, HAS_PYARROW, ORDERS, SALES, TABLES, RecordingStorage, assert_valid, collect_bytes,
                         copy_tree, data_files, differences, discover_all, file_bytes, is_location, of_table, open_bytes,
                         row_of, rows)
from pqax.collect import LocalStorage, SourceKinds, StorageEntry, StorageError, TableLocation, discover, list_items

needs_pyarrow = pytest.mark.skipif(not HAS_PYARROW, reason="the checkpoint of the fixture needs pyarrow, pqax[collect]")
LOG = SALES.table_path + "/_delta_log"


def fixture_storage():
    return LocalStorage(str(FIXTURE))


# ---- LocalStorage ----

def test_a_listing_gives_the_entries_of_a_folder_sorted_with_their_sizes_and_times():
    storage = fixture_storage()
    entries = storage.list(LOG)
    assert len(entries) == SALES.delta["logFileCount"]
    assert sum(entry.size for entry in entries) == SALES.delta["logSize"]
    assert [entry.name for entry in entries] == sorted(entry.name for entry in entries)
    for entry in entries:
        assert isinstance(entry, StorageEntry)
        assert not entry.is_directory
        assert entry.path == f"{LOG}/{entry.name}"
        assert entry.size == (FIXTURE / pathlib.Path(*entry.path.split("/"))).stat().st_size
        assert isinstance(entry.last_modified, datetime.datetime)
        assert entry.last_modified.utcoffset() == datetime.timedelta(0)
    table = storage.list(SALES.table_path)
    [log] = [entry for entry in table if entry.name == "_delta_log"]
    assert log.is_directory and log.size is None
    assert [entry.name for entry in storage.list("")] == ["Fix_Lakehouse.Lakehouse", "Fix_WH.Warehouse"]


def test_a_missing_folder_lists_as_empty():
    assert fixture_storage().list(SALES.table_path + "/no_such_folder") == []
    assert LocalStorage(str(FIXTURE / "no_such_root")).list("") == []


def test_a_range_is_read_as_asked_and_cut_at_the_end_of_the_file():
    path = data_files()[0]
    data = file_bytes(path)
    storage = fixture_storage()
    assert storage.read_range(path, 10, 100) == data[10:110]
    assert storage.read_range(path, len(data) - 5, 100) == data[-5:]
    assert storage.read_range(path, len(data), 10) == b""
    assert storage.read_range(path, len(data) + 100, 10) == b""
    assert storage.read_range(path, 0, 0) == b""


def test_the_storage_counts_its_requests_and_bytes():
    path = data_files()[0]
    storage = fixture_storage()
    storage.list(LOG)
    storage.read_range(path, 0, 100)
    storage.read_range(path, 100, 50)
    assert storage.request_count == 3
    assert storage.bytes_read == 150


def test_a_missing_file_is_not_found_and_the_message_holds_no_path():
    with pytest.raises(StorageError) as caught:
        fixture_storage().read_range(SALES.table_path + "/no_such_file.parquet", 0, 10)
    error = caught.value
    assert error.is_not_found
    assert not error.is_access_denied
    assert error.status == 404
    assert "no_such_file" not in str(error)
    assert SALES.item not in str(error)


@pytest.mark.parametrize("path", ["../probe-dump/index.json", "a/../../probe-dump/index.json", "..", "a/./b", "a//b",
                                  "a\\..\\..\\probe-dump\\index.json", "C:/Windows/win.ini", "/etc/hosts"])
def test_a_path_that_could_leave_the_root_is_refused(path):
    storage = fixture_storage()
    with pytest.raises((ValueError, StorageError)):
        storage.read_range(path, 0, 10)
    try:
        listed = storage.list(path)
    except (ValueError, StorageError):
        listed = []
    assert listed == []


def test_the_root_is_the_full_path(monkeypatch):
    monkeypatch.chdir(FIXTURE.parent)
    storage = LocalStorage("lakehouse")
    assert storage.root == os.path.abspath(str(FIXTURE))
    assert storage.list("") == fixture_storage().list("")


def test_a_storage_error_tells_access_denied_and_not_found_from_its_status():
    for status, denied, missing in ((401, True, False), (403, True, False), (404, False, True), (500, False, False),
                                    (None, False, False)):
        error = StorageError("A request failed.", status)
        assert isinstance(error, Exception)
        assert (error.status, error.is_access_denied, error.is_not_found) == (status, denied, missing)
    assert StorageError("A request failed.").status is None


def test_a_storage_entry_is_named_after_its_last_segment():
    assert StorageEntry("A.Lakehouse/Tables/sales", True).name == "sales"
    assert StorageEntry("A.Lakehouse/Tables/sales/x.parquet", False, 10).size == 10
    assert StorageEntry("top", True).name == "top"


def test_the_location_of_a_local_path_is_its_full_path_and_it_has_no_identifiers():
    storage = fixture_storage()
    assert storage.location_of(SALES.item_folder) == os.path.abspath(os.path.join(str(FIXTURE), SALES.item_folder))
    assert storage.location_of(SALES.table_path) == os.path.abspath(os.path.join(str(FIXTURE), *SALES.table_path.split("/")))
    assert not storage.identifiers_of(SALES.item_folder)


# ---- discovery ----

def test_the_items_and_tables_of_the_fixture_are_those_of_the_probe_dump():
    storage = fixture_storage()
    items = list_items(storage)
    assert items == sorted({table.item_folder for table in TABLES})
    for item in items:
        tables = discover(storage, item)
        expected = [table for table in TABLES if table.item_folder == item]
        assert len(tables) == len(expected)
        for dump in expected:
            [location] = [table for table in tables if table.name == dump.name]
            assert isinstance(location, TableLocation)
            assert tuple(location.namespace) == dump.namespace
            assert location.path == dump.table_path
            assert location.source_name == dump.item
            assert location.source_kind == dump.source_kind
            assert location.source_location == os.path.abspath(os.path.join(str(FIXTURE), dump.item_folder))
            assert location.location == os.path.abspath(os.path.join(str(FIXTURE), *dump.table_path.split("/")))
            assert not location.source_identifiers


def test_discovery_follows_the_rules_of_items_schemas_and_tables(tmp_path):
    for folder in ("One.Lakehouse/Tables/plain/_delta_log", "One.Lakehouse/Tables/schema1/nested/_delta_log",
                   "Two.Warehouse/Tables/dbo/facts/_delta_log",
                   "Three.Notebook/Tables/other/_delta_log", "Four.Lakehouse", "Six.MirroredDatabase/Tables/m/_delta_log",
                   "Seven.SQLDatabase/Tables/dbo/s/_delta_log", "Eight.lakehouse/Tables/lower/_delta_log",
                   "Nine.WAREHOUSE/Tables/dbo/upper/_delta_log"):
        (tmp_path / folder).mkdir(parents=True)
    (tmp_path / "Five.Lakehouse").write_text("a file, not an item")
    storage = LocalStorage(str(tmp_path))
    assert list_items(storage) == ["Eight.lakehouse", "Four.Lakehouse", "Nine.WAREHOUSE", "One.Lakehouse",
                                   "Seven.SQLDatabase", "Six.MirroredDatabase", "Two.Warehouse"]

    lakehouse = discover(storage, "One.Lakehouse")
    assert len(lakehouse) == 2
    [plain] = [table for table in lakehouse if table.name == "plain"]
    assert tuple(plain.namespace) == ()
    assert plain.path == "One.Lakehouse/Tables/plain"
    [nested] = [table for table in lakehouse if table.name == "nested"]
    assert tuple(nested.namespace) == ("schema1",)
    assert nested.path == "One.Lakehouse/Tables/schema1/nested"
    assert all(table.source_kind == SourceKinds.ONELAKE_LAKEHOUSE and table.source_name == "One" for table in lakehouse)
    assert all(not table.path.startswith("/") and not table.path.endswith("/") for table in lakehouse)

    [facts] = discover(storage, "Two.Warehouse")
    assert (tuple(facts.namespace), facts.name, facts.source_kind, facts.source_name) == \
        (("dbo",), "facts", SourceKinds.ONELAKE_WAREHOUSE, "Two")
    [mirrored] = discover(storage, "Six.MirroredDatabase")
    assert mirrored.source_kind == SourceKinds.ONELAKE_MIRRORED_DATABASE
    [sql] = discover(storage, "Seven.SQLDatabase")
    assert (sql.source_kind, tuple(sql.namespace)) == (SourceKinds.ONELAKE_SQL_DATABASE, ("dbo",))
    [lower] = discover(storage, "Eight.lakehouse")
    assert lower.source_kind == SourceKinds.ONELAKE_LAKEHOUSE
    [upper] = discover(storage, "Nine.WAREHOUSE")
    assert (upper.source_kind, tuple(upper.namespace)) == (SourceKinds.ONELAKE_WAREHOUSE, ("dbo",))
    assert discover(storage, "Four.Lakehouse") == []
    assert discover(storage, "Nothing.Lakehouse") == []


def test_a_table_location_is_a_frozen_dataclass_with_optional_locations():
    location = TableLocation(SourceKinds.ONELAKE_LAKEHOUSE, "Sales", ("dbo",), "orders", "Sales.Lakehouse/Tables/dbo/orders")
    assert dataclasses.is_dataclass(location)
    assert location.location is None and location.source_location is None and location.source_identifiers is None
    with pytest.raises(AttributeError):
        location.name = "other"


# ---- LocalStorage.mount ----

def test_a_mounted_warehouse_has_one_item_and_the_locations_of_the_mount_folder(tmp_path):
    folder = copy_tree(FIXTURE / ORDERS.item_folder, tmp_path / "default")
    storage = LocalStorage.mount(str(folder), item=ORDERS.item_folder)
    assert list_items(storage) == [ORDERS.item_folder]
    [table] = discover(storage, ORDERS.item_folder)
    assert (tuple(table.namespace), table.name) == (ORDERS.namespace, ORDERS.name)
    assert (table.source_kind, table.source_name) == (SourceKinds.ONELAKE_WAREHOUSE, ORDERS.item)
    assert table.source_location == os.path.abspath(str(folder))
    assert table.location == os.path.abspath(os.path.join(str(folder), "Tables", *ORDERS.namespace, ORDERS.name))
    assert not table.source_identifiers


def test_a_mount_without_an_item_is_a_lakehouse_named_after_its_folder(tmp_path):
    folder = copy_tree(FIXTURE / SALES.item_folder, tmp_path / "default")
    storage = LocalStorage.mount(str(folder))
    assert list_items(storage) == ["default.Lakehouse"]
    [table] = discover(storage, "default.Lakehouse")
    assert (tuple(table.namespace), table.name) == ((), SALES.name)
    assert (table.source_kind, table.source_name) == (SourceKinds.ONELAKE_LAKEHOUSE, "default")
    assert table.source_location == os.path.abspath(str(folder))
    assert table.location == os.path.abspath(os.path.join(str(folder), "Tables", SALES.name))


def assert_mounted_like_local(folder, dump, **options):
    mounted, _ = collect_bytes(LocalStorage.mount(str(folder), item=dump.item_folder), **options)
    local, _ = collect_bytes(LocalStorage(str(FIXTURE)), items=[dump.item_folder], **options)
    assert_valid(mounted)
    a, b = open_bytes(mounted), open_bytes(local)
    assert differences(a, b, is_location) == []
    table = row_of(a, dump)
    [source] = rows(a, "sources")
    assert source["location"] == os.path.abspath(str(folder))
    assert table["location"] == os.path.abspath(os.path.join(str(folder), "Tables", *dump.namespace, dump.name))
    assert of_table(a, "issues", table["id"]) == []


def test_a_mounted_warehouse_gives_the_tables_of_the_local_folder(tmp_path):
    folder = copy_tree(FIXTURE / ORDERS.item_folder, tmp_path / "default")
    assert_mounted_like_local(folder, ORDERS, checkpoint_reader=None)


@pytest.mark.skipif(not HAS_PYARROW, reason="the checkpoint of the fixture needs pyarrow, pqax[collect]")
def test_a_mounted_lakehouse_gives_the_tables_of_the_local_folder(tmp_path):
    folder = copy_tree(FIXTURE / SALES.item_folder, tmp_path / "default")
    assert_mounted_like_local(folder, SALES)


# ---- locations and sources ----

def test_a_storage_that_wraps_another_gives_no_location_and_no_identifiers():
    storage = RecordingStorage(LocalStorage(str(FIXTURE)))
    tables = discover_all(storage)
    assert len(tables) == len(TABLES)
    assert all(table.location is None and table.source_location is None and not table.source_identifiers
               for table in tables)
    data, _ = collect_bytes(storage, tables, checkpoint_reader=None)
    doc = open_bytes(data)
    assert len(rows(doc, "sources")) == 2
    assert all(source.get("location") is None and source.get("identifiers") is None for source in rows(doc, "sources"))
    assert all(table.get("location") is None for table in rows(doc, "tables"))
    assert_valid(data)


def test_tables_share_a_source_when_they_have_the_same_kind_name_and_location():
    one = "abfss://One Workspace@onelake.test/Sales.Lakehouse"
    two = "abfss://Two Workspace@onelake.test/Sales.Lakehouse"
    lakehouse = SourceKinds.ONELAKE_LAKEHOUSE
    path = ORDERS.table_path
    tables = [
        TableLocation(lakehouse, "Sales", (), "sales", path, one + "/Tables/sales", one),
        TableLocation(lakehouse, "Sales", ("dbo",), "orders", path, one + "/Tables/dbo/orders", one),
        TableLocation(lakehouse, "Sales", (), "sales", path, two + "/Tables/sales", two),
        TableLocation(lakehouse, "Sales", (), "sales", path),
        TableLocation(SourceKinds.ONELAKE_WAREHOUSE, "Sales", ("dbo",), "orders", path, one + "/Tables/dbo/orders", one),
        TableLocation(lakehouse, "Other", (), "sales", path, one + "/Tables/sales", one),
    ]
    data, _ = collect_bytes(LocalStorage(str(FIXTURE)), tables, checkpoint_reader=None)
    assert_valid(data)
    doc = open_bytes(data)
    table_rows = rows(doc, "tables")
    assert len(table_rows) == len(tables)
    assert table_rows[0]["source"] == table_rows[1]["source"]
    assert len({row["source"] for row in table_rows}) == 5
    sources = {source["id"]: source for source in rows(doc, "sources")}
    assert len(sources) == 5
    for location, row in zip(tables, table_rows):
        source = sources[row["source"]]
        assert (source["kind"], source["name"], source.get("location")) == \
            (location.source_kind, location.source_name, location.source_location)
        assert row.get("location") == location.location
        assert row["fileCount"] == len(ORDERS.files)
