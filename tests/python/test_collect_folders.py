"""The folders of a storage account: discover_folder and the tables of the format Parquet, from AdlsDiscoveryTests.cs
and ParquetTableTests.cs of the C# collector tests, the sections "Folders of a storage account" and "Folders of Parquet
files" of the README of the C# collector, and section 4.6 of docs/contracts.md. Which folders of a container are
tables and of which format, the walk and its depth, the names, the locations, and the source of the tables; a folder
of Parquet files collected from one listing and the footers of its files, its columns from the footer of the most
recent file with the type names of the Delta schema; and the wording of the errors of a storage account.

The container is a folder that the fake DFS endpoint serves as the filesystem lake of the account pqaxtest, with the
HTTPS connections of OneLakeStorage sent to it, or that LocalStorage reads. Nothing leaves the machine. The collections
of the Delta tables copied from the fixture need pyarrow for their checkpoint; everything else runs on the standard
library.
"""
from __future__ import annotations

import datetime
import os

import pytest

from collect_kit import (ACCOUNT_ENDPOINT, ACCOUNT_HOST, CONTAINER, DATA_TABLES, DEEPER, FILESYSTEM, HAS_PYARROW,
                         NEWER, NEWER_FILE, NOT_PARQUET, OLDER, OLDER_FILE, SALES, TABLE_ID, Container, FakeDfs,
                         HttpsRedirect, RecordingStorage, SizelessStorage, assert_valid, chunk_sum, collect_bytes,
                         container, dump_of, footer, group, leaf, listed_directory, of_table, open_bytes,
                         parquet_file, read_path, row_groups, rows, schema_footer, table_row)
from pqax.collect import (DEFAULT_FOLDER_DEPTH, IssueCodes, LocalStorage, OneLakeStorage, SourceKinds, StorageEntry,
                          StorageError, TableFormats, TableLocation, discover_folder)
from test_collect_fixture import assert_complete

TOKEN = "test-token-not-a-secret-4F9"
GUID_1 = "aaaaaaaa-bbbb-4ccc-8ddd-000000000002"
GUID_2 = "aaaaaaaa-bbbb-4ccc-8ddd-000000000003"
#: The columns of the Parquet files of the fixture, in the order of their footer: name, type of Delta, nullable.
FIXTURE_COLUMNS = [("id", "long", True), ("category", "integer", True), ("name", "string", True),
                   ("amount", "double", True)]
needs_pyarrow = pytest.mark.skipif(not HAS_PYARROW, reason="the checkpoint of the fixture needs pyarrow, pqax[collect]")


@pytest.fixture
def lake(tmp_path):
    return container(tmp_path / "lake")


@pytest.fixture
def fake(lake):
    with FakeDfs(lake.root, FILESYSTEM) as served:
        yield served


@pytest.fixture
def adls(fake, monkeypatch):
    """The storage of the container: a OneLakeStorage on the endpoint of the account, with the filesystem as its
    workspace, whose requests go to the fake endpoint."""
    HttpsRedirect(fake, monkeypatch)
    return account_storage()


def account_storage(endpoint=ACCOUNT_ENDPOINT, **options):
    options.setdefault("max_retries", 0)
    return OneLakeStorage(FILESYSTEM, lambda: TOKEN, endpoint=endpoint, **options)


def paths_of(tables):
    return [table.path for table in tables]


def last_segment(path):
    return path.rsplit("/", 1)[-1]


# ---- the tables of a folder ----

def test_the_tables_of_a_folder_are_its_delta_and_parquet_folders_in_ordinal_path_order(adls):
    tables = discover_folder(adls, "data")
    assert paths_of(tables) == [path for path, _, _ in DATA_TABLES]
    for table, (path, namespace, table_format) in zip(tables, DATA_TABLES):
        assert table.format == table_format
        assert table.source_kind == SourceKinds.STORAGE_FOLDER == "Storage folder"
        assert table.source_name == "data"
        assert table.namespace == namespace
        assert table.name == last_segment(path)
        assert table.source_location == CONTAINER + "/data"
        assert table.location == CONTAINER + "/" + path
        assert table.source_identifiers is None


def test_the_walk_lists_one_folder_at_a_time_and_skips_logs_partitions_and_hidden_folders(adls, fake):
    discover_folder(adls, "data")
    calls = fake.recorded()
    assert calls
    for call in calls:
        assert call.method == "GET"
        assert call.authorization == "Bearer " + TOKEN
        assert call.raw_path.startswith("/lake?resource=filesystem&recursive=false")
        assert call.range is None
    listed = {listed_directory(call) for call in calls}
    assert {"data", "data/a/b/c"} <= listed
    # A folder at the maximum depth is tested for being a table, and not walked further.
    assert "data/a/b/c/d" in listed
    assert DEEPER not in listed
    for skipped in ("_delta_log", "year=2024", "year=2025", "_temporary", "_staging", ".hidden"):
        assert not [directory for directory in listed if skipped in directory.split("/")], skipped


@pytest.mark.parametrize("root", ["", "/"])
def test_at_the_root_the_source_is_the_filesystem_and_the_namespace_starts_with_the_first_folder(adls, root):
    tables = discover_folder(adls, root)
    # One level deeper than under data: the table at depth 4 under data is at depth 5 here.
    assert paths_of(tables) == [path for path, _, _ in DATA_TABLES[1:]]
    for table in tables:
        assert (table.source_kind, table.source_name) == (SourceKinds.STORAGE_FOLDER, FILESYSTEM)
        assert table.source_location == CONTAINER
        assert table.location == CONTAINER + "/" + table.path
        assert table.source_identifiers is None
    assert tables[0].namespace == ("data", "eu")
    assert [table.namespace for table in tables if table.path == "data/raw"] == [("data",)]


@pytest.mark.parametrize("folder, table_format", [("data/sales", "Delta"), ("data/raw", "Parquet")])
@pytest.mark.parametrize("depth", [None, 0, 3])
def test_a_folder_that_is_a_table_gives_that_table_with_no_namespace_and_the_name_of_the_source(adls, folder,
                                                                                              table_format, depth):
    tables = discover_folder(adls, folder) if depth is None else discover_folder(adls, folder, depth)
    [table] = tables
    name = last_segment(folder)
    assert (table.format, table.name, table.source_name, table.namespace, table.path) == (table_format, name, name, (),
                                                                                        folder)
    assert table.source_location == table.location == CONTAINER + "/" + folder


def test_the_maximum_depth_bounds_the_walk(adls):
    assert DEFAULT_FOLDER_DEPTH == 4
    assert paths_of(discover_folder(adls, "data")) == paths_of(discover_folder(adls, "data", DEFAULT_FOLDER_DEPTH))
    assert discover_folder(adls, "data", 0) == []
    assert paths_of(discover_folder(adls, "data", 1)) == ["data/raw", "data/sales", "data/upper"]
    assert paths_of(discover_folder(adls, "data", 3)) == ["data/eu/sales", "data/mixed/inner", "data/raw", "data/sales",
                                                          "data/upper"]
    five = discover_folder(adls, "data", 5)
    assert paths_of(five) == [DEEPER] + [path for path, _, _ in DATA_TABLES]
    assert (five[0].namespace, five[0].name, five[0].format) == (("a", "b", "c", "d"), "deeper", TableFormats.DELTA)


@pytest.mark.parametrize("folder", ["data/empty", "data/other", "data/events", "data/no_such_folder", "nothing/at/all"])
def test_a_folder_with_no_table_or_that_does_not_exist_gives_no_table_and_no_exception(adls, folder):
    assert discover_folder(adls, folder) == []
    assert discover_folder(adls, folder, 0) == []


def test_a_folder_named_like_a_guid_gives_no_identifier(tmp_path, monkeypatch):
    lake = Container(tmp_path / "guids")
    lake.log_only(f"{GUID_1}/{GUID_2}")
    lake.copy_file(OLDER_FILE, f"{GUID_1}/files/a.parquet", OLDER)
    with FakeDfs(lake.root, FILESYSTEM) as fake:
        HttpsRedirect(fake, monkeypatch)
        storage = account_storage()
        tables = discover_folder(storage, GUID_1)
        assert paths_of(tables) == [f"{GUID_1}/{GUID_2}", f"{GUID_1}/files"]
        for table in tables:
            assert (table.source_kind, table.source_name) == (SourceKinds.STORAGE_FOLDER, GUID_1)
            assert table.source_location == f"{CONTAINER}/{GUID_1}"
            assert table.source_identifiers is None
        [itself] = discover_folder(storage, f"{GUID_1}/{GUID_2}")
        assert (itself.source_name, itself.name, itself.source_identifiers) == (GUID_2, GUID_2, None)
        assert all(table.source_identifiers is None for table in discover_folder(storage, ""))


@pytest.mark.parametrize("endpoint, host", [(ACCOUNT_ENDPOINT, ACCOUNT_HOST), ("https://onelake.test", "onelake.test"),
                                            (ACCOUNT_ENDPOINT + ":8443", ACCOUNT_HOST + ":8443")])
def test_the_locations_are_abfss_uris_with_the_host_of_the_endpoint(fake, monkeypatch, endpoint, host):
    HttpsRedirect(fake, monkeypatch)
    [sales] = discover_folder(account_storage(endpoint), "data/eu")
    assert sales.source_location == f"abfss://lake@{host}/data/eu"
    assert sales.location == f"abfss://lake@{host}/data/eu/sales"
    assert (sales.source_kind, sales.source_identifiers) == (SourceKinds.STORAGE_FOLDER, None)


# ---- other storages ----

def test_on_local_storage_the_tables_are_the_same_and_the_locations_are_full_paths(lake, adls):
    storage = LocalStorage(str(lake.root))

    def names(tables):
        return [(t.path, t.namespace, t.name, t.format, t.source_kind, t.source_name) for t in tables]

    local = discover_folder(storage, "data")
    assert names(local) == names(discover_folder(adls, "data"))
    for table in local:
        assert table.source_location == os.path.join(storage.root, "data")
        assert table.location == os.path.join(storage.root, *table.path.split("/"))
        assert table.source_identifiers is None
    # At the root, the source is named after the folder of the storage.
    root = discover_folder(storage, "")
    assert paths_of(root) == paths_of(discover_folder(adls, ""))
    for table in root:
        assert table.source_name == os.path.basename(storage.root) == "lake"
        assert table.source_location == storage.root
    assert discover_folder(storage, "data/no_such_folder") == []
    assert paths_of(discover_folder(storage, "data", 5)) == paths_of(discover_folder(adls, "data", 5))


def test_a_storage_that_wraps_another_gives_the_tables_with_no_location(lake):
    storage = RecordingStorage(LocalStorage(str(lake.root)))
    tables = discover_folder(storage, "data")
    assert paths_of(tables) == [path for path, _, _ in DATA_TABLES]
    for table in tables:
        assert (table.source_name, table.location, table.source_location, table.source_identifiers) == ("data", None,
                                                                                                         None, None)
    assert all(table.source_name == "" for table in discover_folder(storage, ""))
    assert storage.reads() == []
    assert {request.kind for request in storage.requests} == {"list"}


def test_wrong_arguments_are_refused(tmp_path):
    storage = LocalStorage(str(tmp_path))
    with pytest.raises(TypeError):
        discover_folder(storage, None)
    with pytest.raises(TypeError):
        discover_folder(storage, None, 2)
    with pytest.raises(TypeError):
        discover_folder(None, "data")
    with pytest.raises(ValueError):
        discover_folder(storage, "data", -1)


# ---- TableLocation and its format ----

def test_a_table_location_without_a_format_is_a_delta_table():
    table = TableLocation(SourceKinds.ONELAKE_LAKEHOUSE, "Sales", ("dbo",), "orders",
                          "Sales.Lakehouse/Tables/dbo/orders")
    assert table.format == TableFormats.DELTA == "Delta"
    assert TableFormats.PARQUET == "Parquet"


@pytest.mark.parametrize("table_format", ["Delta", "Parquet"])
def test_a_table_location_takes_delta_or_parquet(table_format):
    table = TableLocation(SourceKinds.STORAGE_FOLDER, "data", ("eu",), "sales", "data/eu/sales",
                          CONTAINER + "/data/eu/sales", CONTAINER + "/data", {"key": "value"}, table_format)
    assert table.format == table_format
    assert (table.source_kind, table.source_name, table.namespace, table.name, table.path) == (
        SourceKinds.STORAGE_FOLDER, "data", ("eu",), "sales", "data/eu/sales")
    assert (table.location, table.source_location) == (CONTAINER + "/data/eu/sales", CONTAINER + "/data")
    assert table.source_identifiers == {"key": "value"}
    bare = TableLocation(SourceKinds.STORAGE_FOLDER, "data", None, "sales", "data/sales", format=table_format)
    assert (bare.format, bare.location) == (table_format, None)


@pytest.mark.parametrize("table_format", ["Iceberg", "delta", "PARQUET", "", None])
def test_a_table_location_refuses_another_format(table_format):
    with pytest.raises(ValueError):
        TableLocation(SourceKinds.STORAGE_FOLDER, "data", (), "sales", "data/sales", None, None, None, table_format)


# ---- the wording of the storage errors ----

def denied(call):
    return 403, {}, b""


@pytest.mark.parametrize("endpoint, start", [(ACCOUNT_ENDPOINT, "The storage account answered 403"),
                                             ("https://pqaxtest.dfs.core.chinacloudapi.cn",
                                              "The storage account answered 403"),
                                             ("https://onelake.test", "OneLake answered 403"),
                                             (None, "OneLake answered 403")])
def test_a_storage_error_names_the_storage_account_on_an_adls_endpoint_and_onelake_otherwise(lake, monkeypatch,
                                                                                            endpoint, start):
    with FakeDfs(lake.root, FILESYSTEM, answer=denied) as fake:
        HttpsRedirect(fake, monkeypatch)
        storage = (account_storage(endpoint) if endpoint is not None
                   else OneLakeStorage(FILESYSTEM, lambda: TOKEN, max_retries=0))
        with pytest.raises(StorageError) as listing:
            storage.list("data")
        with pytest.raises(StorageError) as reading:
            storage.read_range("data/raw/" + OLDER_FILE, 0, 10)
    for error in (listing.value, reading.value):
        assert str(error).startswith(start), str(error)
        assert error.status == 403 and error.is_access_denied
        assert TOKEN not in str(error)
        assert "data" not in str(error)


def test_a_storage_error_of_an_account_says_so_for_other_codes_too(lake, monkeypatch):
    with FakeDfs(lake.root, FILESYSTEM, answer=lambda call: (500, {}, b"")) as fake:
        HttpsRedirect(fake, monkeypatch)
        with pytest.raises(StorageError) as raised:
            account_storage().list("data")
    assert str(raised.value).startswith("The storage account answered 500")
    assert raised.value.status == 500


def test_a_request_to_an_account_that_times_out_names_the_storage_account(lake, monkeypatch):
    with FakeDfs(lake.root, FILESYSTEM, answer=lambda call: 3) as fake:
        HttpsRedirect(fake, monkeypatch)
        with pytest.raises(StorageError) as raised:
            account_storage(timeout=0.5).list("data")
    assert str(raised.value) == "A request to the storage account timed out."
    assert raised.value.status is None


# ---- a folder of Parquet files ----

def storage_of(kind, lake):
    return account_storage() if kind == "adls" else LocalStorage(str(lake.root))


def location_of(kind, storage, path):
    return CONTAINER + "/" + path if kind == "adls" else os.path.join(storage.root, *path.split("/"))


def collect_parquet(storage, tables, **options):
    """Collects folders of Parquet files, which need no checkpoint reader, and returns the open footprint."""
    options.setdefault("checkpoint_reader", None)
    data, _ = collect_bytes(storage, tables, **options)
    assert_valid(data)
    return open_bytes(data)


def assert_no_table_format(doc, table):
    """What a Parquet table never has: the fields of a table format, of its log, and of its history."""
    assert table["format"] == TableFormats.PARQUET
    for name in ("tableId", "snapshotId", "snapshotTimestamp", "createdAt", "description", "properties", "delta",
                 "iceberg", "commitCount", "commitsRead", "oldestCommitAt", "metadataFileCount", "metadataSize",
                 "nonLiveFileCount", "nonLiveFileSize", "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt"):
        assert table.get(name) is None, name
    assert of_table(doc, "operations", table["id"]) == []
    assert of_table(doc, "commits", table["id"]) == []
    assert of_table(doc, "partitionSpecs", table["id"]) == []


def assert_listing_totals(table):
    """The totals that come from the listing of the folder of Parquet files of the container."""
    older, newer = dump_of(OLDER_FILE)["size"], dump_of(NEWER_FILE)["size"]
    assert (table["fileCount"], table["fileSize"]) == (2, older + newer)
    assert table["lastModifiedAt"] == NEWER.strftime("%Y-%m-%dT%H:%M:%S.000Z")
    assert (table["unreferencedFileCount"], table["unreferencedFileSize"]) == (0, 0)
    sizes = table["fileSizes"]
    assert (sizes["count"], sizes["sum"], sizes["minimum"], sizes["maximum"]) == (2, older + newer, min(older, newer),
                                                                                 max(older, newer))


def columns_of(doc, table):
    return [(column["name"], column["dataType"], column["isNullable"])
            for column in of_table(doc, "columns", table["id"])]


def assert_fixture_columns(doc, table):
    assert columns_of(doc, table) == FIXTURE_COLUMNS
    for column in of_table(doc, "columns", table["id"]):
        assert (column.get("fieldId"), column.get("physicalName"), column.get("description")) == (None, None, None)


@pytest.mark.parametrize("kind", ["adls", "local"])
def test_at_level_2_a_folder_of_parquet_files_is_a_table_of_its_files_with_the_totals_of_their_footers(
        lake, fake, monkeypatch, kind):
    HttpsRedirect(fake, monkeypatch)
    storage = storage_of(kind, lake)
    [raw] = discover_folder(storage, "data/raw")
    before = len(fake.recorded())
    doc = collect_parquet(storage, [raw])
    [table] = rows(doc, "tables")
    assert (table["name"], table.get("namespace") or []) == ("raw", [])
    assert table["location"] == location_of(kind, storage, "data/raw")
    assert_no_table_format(doc, table)
    assert_listing_totals(table)
    assert_fixture_columns(doc, table)
    older, newer = dump_of(OLDER_FILE), dump_of(NEWER_FILE)
    assert (table["collectedLevel"], table["detailDepth"], table["columnTotalsSource"]) == (2, "Column chunks",
                                                                                           "Footers")
    assert table["footerCount"] == 2
    assert table["rowCount"] == older["footer"]["numRows"] + newer["footer"]["numRows"]
    assert table["rowGroupCount"] == len(row_groups(older)) + len(row_groups(newer))
    assert table["footerSize"] == older["footer"]["footerSize"] + newer["footer"]["footerSize"]
    assert table["compressedSize"] == chunk_sum(older, "compressed") + chunk_sum(newer, "compressed")
    assert table["uncompressedSize"] == chunk_sum(older, "uncompressed") + chunk_sum(newer, "uncompressed")
    assert all(column["chunkCount"] == table["rowGroupCount"] for column in of_table(doc, "columns", table["id"]))
    assert of_table(doc, "issues", table["id"]) == []
    # The data files only, by their names, with the size and the time of the listing and what their footer says.
    files = sorted(of_table(doc, "files", table["id"]), key=lambda file: file["path"])
    assert [file["path"] for file in files] == [OLDER_FILE, NEWER_FILE]
    for file, dump, moment in ((files[0], older, OLDER), (files[1], newer, NEWER)):
        assert file["fileSize"] == dump["size"]
        assert file["modifiedAt"] == moment.strftime("%Y-%m-%dT%H:%M:%S.000Z")
        assert file["footerSize"] == dump["footer"]["footerSize"]
        assert file["rowGroupCount"] == len(row_groups(dump))
        assert file["rowCount"] == dump["footer"]["numRows"]
    # The source is the folder that the discovery was given.
    [source] = rows(doc, "sources")
    assert table["source"] == source["id"]
    assert (source["kind"], source["name"], source["location"]) == (SourceKinds.STORAGE_FOLDER, "raw",
                                                                    location_of(kind, storage, "data/raw"))
    assert source.get("identifiers") is None
    if kind == "adls":
        calls = fake.recorded()[before:]
        # One listing of the folder, not recursive, in pages; no request goes below it.
        listings = [call for call in calls if listed_directory(call) is not None]
        assert listings
        assert all(listed_directory(call) == "data/raw" and call.query["recursive"] == "false" for call in listings)
        assert len([call for call in listings if "continuation" not in call.query]) == 1
        # The footers of the two data files, and each range once: the footer of the columns is not read twice.
        reads = [call for call in calls if read_path(call) is not None]
        assert sorted({read_path(call) for call in reads}) == ["data/raw/" + OLDER_FILE, "data/raw/" + NEWER_FILE]
        assert len(reads) == len({(read_path(call), call.range) for call in reads})


@pytest.mark.parametrize("depth, detail", [("columnChunks", "Files"), ("files", "Files"), ("summary", "Summary")])
def test_at_level_1_only_the_footer_of_the_most_recent_file_is_read_for_the_columns(adls, fake, depth, detail):
    [raw] = discover_folder(adls, "data/raw")
    before = len(fake.recorded())
    doc = collect_parquet(adls, [raw], level=1, depth=depth)
    [table] = rows(doc, "tables")
    assert_no_table_format(doc, table)
    assert_listing_totals(table)
    assert_fixture_columns(doc, table)
    assert (table["collectedLevel"], table["detailDepth"], table["footerCount"]) == (1, detail, 0)
    assert table.get("rowCount") is None
    assert rows(doc, "rowGroups") == [] and rows(doc, "columnChunks") == []
    assert of_table(doc, "issues", table["id"]) == []
    files = of_table(doc, "files", table["id"])
    assert len(files) == (0 if depth == "summary" else 2)
    assert all(file.get("footerSize") is None for file in files)
    reads = [call for call in fake.recorded()[before:] if read_path(call) is not None]
    assert reads
    assert {read_path(call) for call in reads} == {"data/raw/" + NEWER_FILE}


def test_a_limit_of_footers_reads_that_many_at_level_2(lake):
    storage = LocalStorage(str(lake.root))
    [raw] = discover_folder(storage, "data/raw")
    doc = collect_parquet(storage, [raw], max_footers=1)
    [table] = rows(doc, "tables")
    assert (table["fileCount"], table["collectedLevel"]) == (2, 2)
    assert_fixture_columns(doc, table)
    # The limit holds: the footer of the columns takes the place of a file of the sample, and is not read twice.
    assert table["footerCount"] == 1
    read = [file for file in of_table(doc, "files", table["id"]) if file.get("footerSize") is not None]
    assert [file["path"] for file in read] == [NEWER_FILE]


def test_the_footer_of_the_columns_is_read_once_with_the_tail_and_the_others_with_the_tail_it_gives(lake):
    storage = RecordingStorage(LocalStorage(str(lake.root)))
    [raw] = discover_folder(storage, "data/raw")
    collect_parquet(storage, [raw])
    reads = storage.reads()
    assert [request.path for request in reads] == ["data/raw/" + NEWER_FILE, "data/raw/" + OLDER_FILE]
    newer, older = dump_of(NEWER_FILE), dump_of(OLDER_FILE)
    assert reads[0].length == min(32768, newer["size"])
    # The largest footer seen times 1.25, between the floor of 8192 bytes and the first tail.
    planned = min(32768, max(8192, -(-(newer["footer"]["footerSize"] + 8) * 5 // 4)))
    assert reads[1].length == min(planned, older["size"])


# ---- the columns ----

def collect_one_folder(lake, folder, **options):
    storage = LocalStorage(str(lake.root))
    [location] = discover_folder(storage, folder)
    assert location.format == TableFormats.PARQUET
    doc = collect_parquet(storage, [location], **options)
    [table] = rows(doc, "tables")
    return doc, table


TYPES = [
    (leaf("flag", "BOOLEAN", "REQUIRED"), "boolean", False),
    (leaf("count", "INT32"), "integer", True),
    (leaf("tiny", "INT32", logical=("INTEGER", 8, True)), "byte", True),
    (leaf("small", "INT32", logical=("INTEGER", 16, True)), "short", True),
    (leaf("int32", "INT32", logical=("INTEGER", 32, True)), "integer", True),
    (leaf("u8", "INT32", logical=("INTEGER", 8, False)), "short", True),
    (leaf("u16", "INT32", logical=("INTEGER", 16, False)), "integer", True),
    (leaf("u32", "INT32", logical=("INTEGER", 32, False)), "long", True),
    (leaf("c8", "INT32", converted="INT_8"), "byte", True),
    (leaf("c16", "INT32", converted="INT_16"), "short", True),
    (leaf("cu8", "INT32", converted="UINT_8"), "short", True),
    (leaf("cu16", "INT32", converted="UINT_16"), "integer", True),
    (leaf("cu32", "INT32", converted="UINT_32"), "long", True),
    (leaf("day", "INT32", logical="DATE"), "date", True),
    (leaf("cday", "INT32", converted="DATE"), "date", True),
    (leaf("d32", "INT32", logical=("DECIMAL", 9, 2)), "decimal(9,2)", True),
    (leaf("cd32", "INT32", converted="DECIMAL", precision=5, scale=1), "decimal(5,1)", True),
    (leaf("id", "INT64", "REQUIRED"), "long", False),
    (leaf("i64", "INT64", logical=("INTEGER", 64, True)), "long", True),
    (leaf("u64", "INT64", logical=("INTEGER", 64, False)), "decimal(20,0)", True),
    (leaf("cu64", "INT64", converted="UINT_64"), "decimal(20,0)", True),
    (leaf("at", "INT64", logical=("TIMESTAMP", "MICROS", True)), "timestamp", True),
    (leaf("at_ms", "INT64", logical=("TIMESTAMP", "MILLIS", True)), "timestamp", True),
    (leaf("local", "INT64", logical=("TIMESTAMP", "MICROS", False)), "timestamp_ntz", True),
    (leaf("local_ns", "INT64", logical=("TIMESTAMP", "NANOS", False)), "timestamp_ntz", True),
    (leaf("cms", "INT64", converted="TIMESTAMP_MILLIS"), "timestamp", True),
    (leaf("cus", "INT64", converted="TIMESTAMP_MICROS"), "timestamp", True),
    (leaf("d64", "INT64", logical=("DECIMAL", 18, 4)), "decimal(18,4)", True),
    (leaf("legacy", "INT96"), "timestamp", True),
    (leaf("ratio", "FLOAT", "REQUIRED"), "float", False),
    (leaf("amount", "DOUBLE"), "double", True),
    (leaf("blob", "BYTE_ARRAY"), "binary", True),
    (leaf("name", "BYTE_ARRAY", logical="STRING"), "string", True),
    (leaf("cname", "BYTE_ARRAY", converted="UTF8"), "string", True),
    (leaf("kind", "BYTE_ARRAY", logical="ENUM"), "string", True),
    (leaf("ckind", "BYTE_ARRAY", converted="ENUM"), "string", True),
    (leaf("doc", "BYTE_ARRAY", logical="JSON"), "string", True),
    (leaf("cdoc", "BYTE_ARRAY", converted="JSON"), "string", True),
    (leaf("bson", "BYTE_ARRAY", logical="BSON"), "binary", True),
    (leaf("dba", "BYTE_ARRAY", logical=("DECIMAL", 30, 6)), "decimal(30,6)", True),
    (leaf("fixed", "FIXED_LEN_BYTE_ARRAY", type_length=4), "binary", True),
    (leaf("uuid", "FIXED_LEN_BYTE_ARRAY", type_length=16, logical="UUID"), "binary", True),
    (leaf("half", "FIXED_LEN_BYTE_ARRAY", type_length=2, logical="FLOAT16"), "float", True),
    (leaf("price", "FIXED_LEN_BYTE_ARRAY", type_length=16, logical=("DECIMAL", 38, 18)), "decimal(38,18)", True),
    (leaf("cprice", "FIXED_LEN_BYTE_ARRAY", type_length=8, converted="DECIMAL", precision=18), "decimal(18,0)", True),
    (leaf("no_repetition", "INT64", None), "long", True),
]


def test_the_columns_are_those_of_the_most_recent_file_with_the_type_names_of_delta(tmp_path):
    lake = Container(tmp_path / "typed")
    # The most recent file comes first by name, so that its time, not its name, chooses it.
    lake.write("data/typed/a-newest.parquet", parquet_file(schema_footer([field for field, _, _ in TYPES])))
    lake.touch("data/typed/a-newest.parquet", NEWER)
    lake.write("data/typed/b-older.parquet", parquet_file(schema_footer([leaf("retired", "INT32")])))
    lake.touch("data/typed/b-older.parquet", OLDER)
    doc, table = collect_one_folder(lake, "data/typed")
    assert of_table(doc, "issues", table["id"]) == []
    assert columns_of(doc, table) == [(field.name, data_type, nullable) for field, data_type, nullable in TYPES]
    for column in of_table(doc, "columns", table["id"]):
        assert (column.get("fieldId"), column.get("physicalName"), column.get("description")) == (None, None, None)
    # The columns of the older file are not columns of the table: its leaf is stale.
    assert table["footerCount"] == 2
    assert table["staleColumnSize"] > 0


NESTED = [
    # The three levels of the specification: a LIST group, a repeated group, and the element.
    (group("tags", group("list", leaf("element", "INT32", "REQUIRED"), repetition="REPEATED"), logical="LIST"),
     "array<integer>", True),
    (group("names", group("list", leaf("element", "BYTE_ARRAY", logical="STRING"), repetition="REPEATED"),
           converted="LIST", repetition="REQUIRED"), "array<string>", False),
    # The two levels of older writers: the repeated field is the element, also when it is a group named array or
    # <name>_tuple, or a group of more than one field.
    (group("legacy", leaf("value", "INT64", "REPEATED"), converted="LIST"), "array<long>", True),
    (group("points", group("array", leaf("x", "DOUBLE"), repetition="REPEATED"), converted="LIST"),
     "array<struct<x:double>>", True),
    (group("pairs", group("pairs_tuple", leaf("a", "INT32"), repetition="REPEATED"), converted="LIST"),
     "array<struct<a:integer>>", True),
    (group("spans", group("item", leaf("a", "INT32"), leaf("b", "INT32"), repetition="REPEATED"), logical="LIST"),
     "array<struct<a:integer,b:integer>>", True),
    (group("props", group("key_value", leaf("key", "BYTE_ARRAY", "REQUIRED", logical="STRING"), leaf("value", "INT64"),
                          repetition="REPEATED"), logical="MAP"), "map<string,long>", True),
    (group("old_map", group("map", leaf("key", "BYTE_ARRAY", "REQUIRED", converted="UTF8"),
                            leaf("value", "DOUBLE"), repetition="REPEATED"), converted="MAP_KEY_VALUE"),
     "map<string,double>", True),
    (group("map_of_lists", group("key_value", leaf("key", "INT32", "REQUIRED"),
                                 group("value", group("list", leaf("element", "BOOLEAN"), repetition="REPEATED"),
                                       logical="LIST"), repetition="REPEATED"), logical="MAP"),
     "map<integer,array<boolean>>", True),
    (group("where", leaf("a", "INT32", "REQUIRED"), leaf("b", "BYTE_ARRAY", logical="STRING")),
     "struct<a:integer,b:string>", True),
    (group("outer", group("inner", leaf("c", "INT64"), leaf("d", "FLOAT")), leaf("e", "INT96")),
     "struct<inner:struct<c:long,d:float>,e:timestamp>", True),
    # A repeated field outside a list annotation is a list of its values.
    (leaf("numbers", "INT32", "REPEATED"), "array<integer>", False),
    (group("records", leaf("a", "INT32"), repetition="REPEATED"), "array<struct<a:integer>>", False),
    (group("payload", leaf("metadata", "BYTE_ARRAY", "REQUIRED"), leaf("value", "BYTE_ARRAY", "REQUIRED"),
           logical=("VARIANT",), repetition="REQUIRED"), "variant", False),
    (leaf("after", "INT32"), "integer", True),
]


def test_a_nested_column_is_one_column_with_the_complete_type(tmp_path):
    lake = Container(tmp_path / "nested")
    lake.write("data/nested/part-0001.parquet", parquet_file(schema_footer([field for field, _, _ in NESTED])))
    doc, table = collect_one_folder(lake, "data/nested")
    assert of_table(doc, "issues", table["id"]) == []
    assert columns_of(doc, table) == [(field.name, data_type, nullable) for field, data_type, nullable in NESTED]


def nested_groups(levels):
    """A top-level group with groups nested inside it, levels in all, and one leaf at the bottom."""
    field = leaf("bottom", "INT32")
    for level in reversed(range(levels)):
        field = group(f"g{level}", field)
    return field


def test_a_schema_nested_64_levels_has_its_type_and_one_more_level_fails_the_table(tmp_path):
    lake = Container(tmp_path / "deep")
    lake.write("data/deep/ok/part-0001.parquet", parquet_file(schema_footer([nested_groups(65 - 1)])))
    lake.write("data/deep/too/part-0001.parquet", parquet_file(schema_footer([nested_groups(65)])))
    doc, table = collect_one_folder(lake, "data/deep/ok")
    [(name, data_type, _)] = columns_of(doc, table)
    assert name == "g0" and data_type.count("struct<") == 64 and data_type.endswith("bottom:integer" + ">" * 64)
    doc, table = collect_one_folder(lake, "data/deep/too")
    [issue] = of_table(doc, "issues", table["id"])
    assert issue["code"] == IssueCodes.TABLE_NOT_READ
    assert issue["message"].startswith("The table could not be collected: ") and "nested too deep" in issue["message"]
    assert (table["detailDepth"], columns_of(doc, table), of_table(doc, "files", table["id"])) == ("Summary", [], [])


@pytest.mark.skipif(not HAS_PYARROW, reason="pyarrow writes the files of the test, pqax[collect]")
def test_the_columns_of_files_that_pyarrow_writes_have_the_types_that_spark_reads(tmp_path):
    import decimal

    import pyarrow
    import pyarrow.parquet

    schema = pyarrow.schema([
        pyarrow.field("flag", pyarrow.bool_(), nullable=False), pyarrow.field("tiny", pyarrow.int8()),
        pyarrow.field("small", pyarrow.int16()), pyarrow.field("count", pyarrow.int32()),
        pyarrow.field("id", pyarrow.int64(), nullable=False), pyarrow.field("u8", pyarrow.uint8()),
        pyarrow.field("u16", pyarrow.uint16()), pyarrow.field("u32", pyarrow.uint32()),
        pyarrow.field("u64", pyarrow.uint64()), pyarrow.field("ratio", pyarrow.float32()),
        pyarrow.field("amount", pyarrow.float64()), pyarrow.field("name", pyarrow.string()),
        pyarrow.field("large", pyarrow.large_string()), pyarrow.field("blob", pyarrow.binary()),
        pyarrow.field("fixed", pyarrow.binary(4)), pyarrow.field("day", pyarrow.date32()),
        pyarrow.field("at", pyarrow.timestamp("us", tz="UTC")), pyarrow.field("local", pyarrow.timestamp("us")),
        pyarrow.field("cost", pyarrow.decimal128(10, 2)), pyarrow.field("price", pyarrow.decimal128(38, 18)),
        pyarrow.field("tags", pyarrow.list_(pyarrow.int32())),
        pyarrow.field("props", pyarrow.map_(pyarrow.string(), pyarrow.int64())),
        pyarrow.field("where", pyarrow.struct([pyarrow.field("a", pyarrow.int32()),
                                               pyarrow.field("b", pyarrow.string())])),
    ])
    moment = datetime.datetime(2026, 1, 1, tzinfo=datetime.timezone.utc)
    values = [[True], [1], [1], [1], [1], [1], [1], [1], [1], [0.5], [1.5], ["synthetic"], ["synthetic"], [b"\x01"],
              [b"abcd"], [datetime.date(2026, 1, 1)], [moment], [moment.replace(tzinfo=None)],
              [decimal.Decimal("3.50")], [decimal.Decimal("1.25")], [[1, 2]], [[("k", 1)]], [{"a": 1, "b": "x"}]]
    folder = tmp_path / "arrow" / "data" / "written"
    folder.mkdir(parents=True)
    pyarrow.parquet.write_table(pyarrow.Table.from_arrays([pyarrow.array(v, f.type) for v, f in zip(values, schema)],
                                                          schema=schema), str(folder / "part-0001.parquet"))
    doc, table = collect_one_folder(Container(tmp_path / "arrow"), "data/written")
    assert of_table(doc, "issues", table["id"]) == []
    assert columns_of(doc, table) == [
        ("flag", "boolean", False), ("tiny", "byte", True), ("small", "short", True), ("count", "integer", True),
        ("id", "long", False), ("u8", "short", True), ("u16", "integer", True), ("u32", "long", True),
        ("u64", "decimal(20,0)", True), ("ratio", "float", True), ("amount", "double", True), ("name", "string", True),
        ("large", "string", True), ("blob", "binary", True), ("fixed", "binary", True), ("day", "date", True),
        ("at", "timestamp", True), ("local", "timestamp_ntz", True), ("cost", "decimal(10,2)", True),
        ("price", "decimal(38,18)", True), ("tags", "array<integer>", True), ("props", "map<string,long>", True),
        ("where", "struct<a:integer,b:string>", True)]
    assert table["rowCount"] == 1


def test_between_files_of_the_same_time_the_last_name_gives_the_columns(tmp_path):
    lake = Container(tmp_path / "tie")
    lake.write("data/tie/x.parquet", parquet_file(schema_footer([leaf("first", "INT32")])))
    lake.write("data/tie/y.parquet", parquet_file(schema_footer([leaf("second", "BYTE_ARRAY", logical="STRING")])))
    for name in ("x", "y"):
        lake.touch(f"data/tie/{name}.parquet", OLDER)
    doc, table = collect_one_folder(lake, "data/tie")
    assert [name for name, _, _ in columns_of(doc, table)] == ["second"]


class TimelessStorage(SizelessStorage):
    """A storage whose listings give no modification time for the files that a test chooses."""

    def list(self, path):
        return [StorageEntry(entry.path, False, entry.size, None)
                if not entry.is_directory and self.sizeless(entry.path) else entry for entry in self.inner.list(path)]


def test_a_file_without_a_time_is_older_than_every_other(lake):
    lake.write("data/times/a.parquet", parquet_file(schema_footer([leaf("dated", "INT32")])))
    lake.touch("data/times/a.parquet", OLDER)
    lake.write("data/times/b.parquet", parquet_file(schema_footer([leaf("undated", "INT32")])))
    storage = TimelessStorage(LocalStorage(str(lake.root)), lambda path: path.endswith("/b.parquet"))
    [location] = discover_folder(storage, "data/times")
    doc = collect_parquet(storage, [location])
    [table] = rows(doc, "tables")
    assert [name for name, _, _ in columns_of(doc, table)] == ["dated"]
    # A file without a time leaves the time of the table unknown, as for a Delta table.
    assert table.get("lastModifiedAt") is None


@pytest.mark.parametrize("level", [1, 2])
def test_a_footer_of_the_columns_that_cannot_be_read_is_an_issue_and_the_table_has_no_columns(lake, level):
    lake.copy_file(OLDER_FILE, "data/bad/good.parquet", OLDER)
    lake.write("data/bad/broken.parquet", NOT_PARQUET)
    lake.touch("data/bad/broken.parquet", NEWER)
    doc, table = collect_one_folder(lake, "data/bad", level=level)
    assert table["format"] == TableFormats.PARQUET
    assert columns_of(doc, table) == []
    [issue] = of_table(doc, "issues", table["id"])
    assert issue["code"] == IssueCodes.FOOTER_INVALID
    assert issue["path"] == "broken.parquet"
    assert (table["fileCount"], table["collectedLevel"]) == (2, level)
    assert table["footerCount"] == (1 if level == 2 else 0)


def test_a_file_without_a_size_in_the_listing_is_an_issue_and_its_footer_is_not_read(lake):
    storage = SizelessStorage(LocalStorage(str(lake.root)), lambda path: path.endswith(NEWER_FILE))
    [raw] = discover_folder(storage, "data/raw")
    doc = collect_parquet(storage, [raw])
    [table] = rows(doc, "tables")
    [issue] = of_table(doc, "issues", table["id"])
    assert (issue["code"], issue["path"]) == (IssueCodes.FOOTER_NOT_READ, NEWER_FILE)
    assert issue["message"] == "The listing gives no size for the file, so its footer was not read."
    assert columns_of(doc, table) == []
    assert (table["fileCount"], table.get("fileSize"), table["footerCount"]) == (2, None, 1)


def test_column_chunks_without_metadata_are_an_issue_at_level_2(lake):
    lake.write("data/hidden/part-0001.parquet", parquet_file(footer(leaves=("id",), meta_data=False)))
    doc, table = collect_one_folder(lake, "data/hidden")
    [issue] = of_table(doc, "issues", table["id"])
    assert (issue["code"], issue["path"]) == (IssueCodes.FOOTER_COLUMNS_ENCRYPTED, "part-0001.parquet")
    assert [name for name, _, _ in columns_of(doc, table)] == ["id"]
    assert (table["footerCount"], table["collectedLevel"], table["detailDepth"]) == (0, 1, "Files")


@pytest.mark.parametrize("status, code", [(403, IssueCodes.ACCESS_DENIED), (401, IssueCodes.ACCESS_DENIED),
                                          (500, IssueCodes.TABLE_NOT_READ)])
def test_a_listing_of_the_folder_that_fails_is_an_issue_at_the_summary_depth(lake, adls, monkeypatch, status, code):
    [raw] = discover_folder(adls, "data/raw")
    refusal = (lambda call: (status, {}, b"") if call.query.get("resource") == "filesystem" else None)
    with FakeDfs(lake.root, FILESYSTEM, answer=refusal) as failing:
        HttpsRedirect(failing, monkeypatch)
        doc = collect_parquet(account_storage(), [raw])
        calls = failing.recorded()
    [table] = rows(doc, "tables")
    assert (table["format"], table["detailDepth"]) == (TableFormats.PARQUET, "Summary")
    assert code in [issue["code"] for issue in of_table(doc, "issues", table["id"])]
    assert of_table(doc, "files", table["id"]) == []
    assert [call for call in calls if read_path(call) is not None] == []
    assert all(TOKEN not in (issue.get("message") or "") for issue in rows(doc, "issues"))


# ---- the tables of a container ----

@needs_pyarrow
def test_the_tables_of_a_folder_are_collected_with_their_format_and_one_source(adls):
    tables = discover_folder(adls, "data")
    data, _ = collect_bytes(adls, tables)
    assert_valid(data)
    doc = open_bytes(data)
    assert rows(doc, "issues") == []
    [source] = rows(doc, "sources")
    assert (source["kind"], source["name"], source["location"]) == (SourceKinds.STORAGE_FOLDER, "data",
                                                                    CONTAINER + "/data")
    assert source.get("identifiers") is None
    assert len(rows(doc, "tables")) == len(DATA_TABLES)
    for path, namespace, table_format in DATA_TABLES:
        table = table_row(doc, last_segment(path), namespace)
        assert (table["source"], table["format"], table["location"]) == (source["id"], table_format,
                                                                         CONTAINER + "/" + path)
        assert table["collectedLevel"] == 2
    # The Delta table at depth 1 and its copy at depth 2 are collected as the table of the fixture.
    assert_complete(doc, SALES)
    sales, copy = table_row(doc, "sales"), table_row(doc, "sales", ("eu",))
    for table in (sales, copy):
        assert table["format"] == TableFormats.DELTA
        assert table["fileCount"] == len(SALES.files)
        assert table["fileSize"] == sum(file["size"] for file in SALES.files)
        for name in ("tableId", "snapshotId", "delta", "commitCount", "metadataFileCount"):
            assert table.get(name) is not None, name
    for name in ("tableId", "snapshotId", "rowCount"):
        assert sales[name] == copy[name], name
    assert ([(c["name"], c["dataType"]) for c in of_table(doc, "columns", sales["id"])]
            == [(c["name"], c["dataType"]) for c in of_table(doc, "columns", copy["id"])])
    # The table at depth 4, with a log and no file.
    deep = table_row(doc, "deep", ("a", "b", "c"))
    assert (deep["format"], deep["tableId"], deep["fileCount"]) == (TableFormats.DELTA, TABLE_ID, 0)
    # The folders of Parquet files of the container are copies of the files of the fixture.
    upper = table_row(doc, "upper")
    assert (upper["fileCount"], upper["rowCount"]) == (1, dump_of(OLDER_FILE)["footer"]["numRows"])
    assert [file["path"] for file in of_table(doc, "files", upper["id"])] == ["DATA.PARQUET"]
    inner = table_row(doc, "inner", ("mixed",))
    assert [file["path"] for file in of_table(doc, "files", inner["id"])] == ["y.parquet"]
    assert_listing_totals(table_row(doc, "raw"))
