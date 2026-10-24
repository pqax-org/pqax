"""A footer that cannot be read becomes an issue of its file, with the code of the failure, and the footer counts as not
read: the file keeps what the log says, and footerCount and the totals of level 2 leave it out (README of the C#
collector, Footer read failures). And the tail of a footer: one request in the common case, learned from the footers
read before. The table is synthetic, with a JSON log, and its data files are copies of the data files of the lakehouse
table of the fixture, so that the dump gives every expected value without a checkpoint: standard library only.
"""
from __future__ import annotations

import math
import posixpath

import pytest

from collect_kit import (SALES, ORDERS, Lake, RecordingStorage, add, all_text, assert_valid, chunk_sum, collect_bytes,
                         commit_info, data_files_of, deletion_vector, discover_all, dump_file_of_size, file_bytes,
                         footer, is_vordered, metadata, of_table, open_bytes, parquet_ending_with, protocol,
                         row_groups, rows, size_of, tail, timestamp, virtual_slice, vorder_tag)
from pqax.collect import MAX_FOOTER_LENGTH, CollectorOptions, LocalStorage, StorageError

TABLE = "Synth.Lakehouse/Tables/files"
TOKEN = "test-token-not-a-secret"


def copied_files():
    """The data files of the lakehouse table of the fixture, by the name that the synthetic table gives them."""
    return {posixpath.basename(path): path for path in data_files_of(SALES)}


def copied_lake(root, files=None):
    """A table whose log adds copies of the data files of the lakehouse table, with what the fixture's log says of them."""
    lake = Lake(root)
    actions = [protocol(), metadata(schema_string=SALES.delta["schemaString"]), commit_info(0)]
    for name, source in (files or copied_files()).items():
        data = file_bytes(source)
        lake.write(f"{TABLE}/{name}", data)
        _, dumped = dump_file_of_size(len(data))
        dv = deletion_vector(dumped["deletionVectorSize"], dumped["deletedRows"]) if "deletionVectorSize" in dumped else None
        actions.append(add(name, len(data), dumped.get("numRecords"), dumped["modificationTime"], deletion_vector=dv,
                           tags=dumped.get("tags")))
    lake.commit(TABLE, 0, *actions)
    return lake


@pytest.fixture
def lake(tmp_path):
    return copied_lake(tmp_path)


LARGEST = max(copied_files(), key=lambda name: size_of(copied_files()[name]))
TAGGED = next(name for name, path in copied_files().items() if vorder_tag(dump_file_of_size(size_of(path))[1]) is True)


def collect(storage, **options):
    options.setdefault("checkpoint_reader", None)
    data, builder = collect_bytes(storage, **options)
    assert_valid(data)
    return data, builder


def dumped(name):
    return dump_file_of_size(size_of(copied_files()[name]))[1]


def assert_footer_not_read(data, target, code):
    """One issue, with the code and the path of the file; the file with what the log says and nothing of its footer;
    footerCount and the totals of level 2 over the other files, which are read."""
    doc = open_bytes(data)
    [table] = rows(doc, "tables")
    files = of_table(doc, "files", table["id"])
    size = size_of(copied_files()[target])
    [file] = [row for row in files if row["fileSize"] == size]
    failed = dumped(target)

    [issue] = of_table(doc, "issues", table["id"])
    assert issue["code"] == code
    assert issue["path"] == file["path"] == target
    assert issue["message"]
    assert TOKEN not in issue["message"]

    others = [dumped(name) for name in copied_files() if name != target]
    assert table["footerCount"] == len(others) == 2
    assert table["rowGroupCount"] == sum(len(row_groups(other)) for other in others)
    assert table["compressedSize"] == sum(chunk_sum(other, "compressed") for other in others)
    assert table["uncompressedSize"] == sum(chunk_sum(other, "uncompressed") for other in others)
    assert table["footerSize"] == sum(other["footer"]["footerSize"] for other in others)
    assert table["rowGroupRowCounts"]["count"] == sum(len(row_groups(other)) for other in others)
    assert sum(writer["fileCount"] for writer in of_table(doc, "writers", table["id"])) == len(others)
    assert table["collectedLevel"] == 2
    assert table["detailDepth"] == "Column chunks"
    assert table["fileCount"] == 3
    assert table["fileSize"] == sum(size_of(path) for path in copied_files().values())
    marks = [vorder_tag(failed)] + [is_vordered(other) for other in others]
    assert table.get("vOrderedFileCount") == (None if None in marks else sum(1 for mark in marks if mark))

    # The file keeps what the log says, and nothing of its footer.
    assert file["modifiedAt"] == timestamp(failed["modificationTime"])
    assert file.get("rowCount") == failed.get("numRecords")
    assert file["deletedRowCount"] == failed.get("deletedRows", 0)
    assert file.get("deletionVectorSize") == failed.get("deletionVectorSize")
    assert file.get("isVOrdered") == vorder_tag(failed)
    for name in ("footerSize", "rowGroupCount", "compressedSize", "uncompressedSize", "physicalSchema", "writer"):
        assert file.get(name) is None, name
    assert all(group["file"] != file["id"] for group in rows(doc, "rowGroups"))

    # The other files are read.
    assert all(row.get("footerSize") is not None for row in files if row["id"] != file["id"])


# ---- the storage fails ----

@pytest.mark.parametrize("failure, code", [
    (lambda: StorageError("Access was denied.", 403), "AccessDenied"),
    (lambda: StorageError("Not signed in.", 401), "AccessDenied"),
    (lambda: StorageError("Not found.", 404), "FooterNotRead"),
    (lambda: StorageError("Bad request.", 400), "FooterNotRead"),
    (lambda: StorageError("Server error.", 500), "FooterNotRead"),
    (lambda: StorageError("The connection was reset."), "FooterNotRead"),
    (lambda: OSError("A local error."), "FooterNotRead"),
    (lambda: RuntimeError("Something that no storage expects."), "FooterNotRead"),
    (lambda: TimeoutError("A request that the storage gave up."), "FooterNotRead"),
], ids=["403", "401", "404", "400", "500", "no status", "OSError", "RuntimeError", "TimeoutError"])
def test_a_footer_read_that_fails_becomes_an_issue_with_the_code_of_the_failure(lake, failure, code):
    target = f"{TABLE}/{LARGEST}"
    storage = RecordingStorage(LocalStorage(str(lake.root)),
                               fail=lambda kind, path: failure() if kind == "read" and path == target else None)
    data, _ = collect(storage)
    assert_footer_not_read(data, LARGEST, code)


def test_a_403_on_the_largest_file_takes_its_footer_out_of_the_totals(lake):
    target = f"{TABLE}/{LARGEST}"
    storage = RecordingStorage(LocalStorage(str(lake.root)),
                               fail=lambda kind, path: StorageError("Denied.", 403) if path == target and kind == "read" else None)
    data, _ = collect(storage)
    assert_footer_not_read(data, LARGEST, "AccessDenied")
    [table] = rows(open_bytes(data), "tables")
    largest = dumped(LARGEST)
    every = [dumped(name) for name in copied_files()]
    assert table["compressedSize"] == sum(chunk_sum(file, "compressed") for file in every) - chunk_sum(largest, "compressed")
    assert table["rowGroupCount"] == sum(len(row_groups(file)) for file in every) - len(row_groups(largest))


def test_a_file_whose_footer_is_not_read_keeps_the_v_order_of_its_log_tag(lake):
    target = f"{TABLE}/{TAGGED}"
    storage = RecordingStorage(LocalStorage(str(lake.root)),
                               fail=lambda kind, path: StorageError("Denied.", 403) if path == target and kind == "read" else None)
    data, _ = collect(storage)
    assert_footer_not_read(data, TAGGED, "AccessDenied")
    doc = open_bytes(data)
    [file] = [row for row in rows(doc, "files") if row["path"] == TAGGED]
    assert file["isVOrdered"] is True


def test_when_no_footer_is_read_the_table_has_level_1_and_at_most_the_files_depth(lake):
    storage = RecordingStorage(LocalStorage(str(lake.root)),
                               fail=lambda kind, path: StorageError("Denied.", 403) if kind == "read" and path.endswith(".parquet") else None)
    data, builder = collect(storage)
    doc = open_bytes(data)
    [table] = rows(doc, "tables")
    assert [issue["code"] for issue in rows(doc, "issues")] == ["AccessDenied"] * 3
    assert table["collectedLevel"] == 1
    assert table["detailDepth"] == "Files"
    assert table["footerCount"] == 0
    assert table["fileCount"] == 3
    assert rows(doc, "physicalSchemas") == [] and rows(doc, "writers") == [] and rows(doc, "rowGroups") == []
    assert len(rows(doc, "files")) == 3
    assert builder.collection["level"] == 2


# ---- the bytes are not a readable footer ----

def changed_bytes(name, fault):
    original = file_bytes(copied_files()[name])
    data = bytearray(original)
    if fault == "encrypted":
        data[-1] = ord("E")
    elif fault == "length":
        data[-8:-4] = len(data).to_bytes(4, "little")
    elif fault == "short":
        data = data[:len(data) // 2]
    elif fault == "not Thrift":
        length = int.from_bytes(original[-8:-4], "little")
        data[len(data) - 8 - length:len(data) - 8] = b"\xff" * length
    elif fault == "no chunk metadata":
        data = bytearray(parquet_ending_with(footer(meta_data=False), len(original)))
    return bytes(data)


@pytest.mark.parametrize("fault, code", [("encrypted", "FooterEncrypted"), ("length", "FooterInvalid"),
                                         ("short", "FooterInvalid"), ("not Thrift", "FooterInvalid"),
                                         ("no chunk metadata", "FooterColumnsEncrypted")])
def test_a_footer_that_cannot_be_parsed_gets_its_code(lake, fault, code):
    target = f"{TABLE}/{LARGEST}"
    changed = changed_bytes(LARGEST, fault)
    storage = RecordingStorage(LocalStorage(str(lake.root)), content=lambda path: changed if path == target else None)
    data, _ = collect(storage)
    assert_footer_not_read(data, LARGEST, code)


def test_a_file_shorter_on_disk_than_the_log_says_is_invalid(lake):
    target = lake.full(f"{TABLE}/{LARGEST}")
    target.write_bytes(target.read_bytes()[:1000])
    data, _ = collect(LocalStorage(str(lake.root)))
    assert_footer_not_read(data, LARGEST, "FooterInvalid")


def test_a_missing_file_becomes_footer_not_read(lake):
    lake.full(f"{TABLE}/{LARGEST}").unlink()
    data, _ = collect(LocalStorage(str(lake.root)))
    assert_footer_not_read(data, LARGEST, "FooterNotRead")


# ---- what the log says ----

def table_with(root, action):
    """A table with two files: good.parquet, a copy of the smallest data file of the warehouse table, whose footer is
    read, and the file of a test, with its add action."""
    source = min(data_files_of(ORDERS), key=size_of)
    good = file_bytes(source)
    rows_ = dump_file_of_size(len(good))[1]["footer"]["numRows"]
    lake = Lake(root)
    lake.write(f"{TABLE}/good.parquet", good)
    lake.commit(TABLE, 0, protocol(), metadata(), add("good.parquet", len(good), rows_), action, commit_info(0))
    return lake


def assert_not_read_in_table(data, path, code):
    doc = open_bytes(data)
    [table] = rows(doc, "tables")
    [issue] = rows(doc, "issues")
    assert issue["code"] == code
    assert issue["path"] == path
    files = rows(doc, "files")
    assert len(files) == 2
    [file] = [row for row in files if row["path"] == path]
    assert file["rowCount"] == 10
    for name in ("footerSize", "rowGroupCount", "compressedSize", "uncompressedSize"):
        assert file.get(name) is None, name
    [good] = [row for row in files if row["path"] == "good.parquet"]
    assert good["footerSize"] is not None
    assert table["footerCount"] == 1
    assert table["footerSize"] == good["footerSize"]
    assert table["rowGroupCount"] == good["rowGroupCount"]
    assert table["compressedSize"] == good["compressedSize"]
    assert table["uncompressedSize"] == good["uncompressedSize"]
    assert table["collectedLevel"] == 2


def data_reads(storage):
    return [request.path for request in storage.reads(lambda path: "/_delta_log/" not in path)]


def test_a_file_without_a_size_in_the_log_becomes_footer_not_read_with_no_request(tmp_path):
    lake = table_with(tmp_path, add("unsized.parquet", None, 10))
    storage = RecordingStorage(LocalStorage(str(lake.root)))
    data, _ = collect(storage)
    assert_not_read_in_table(data, "unsized.parquet", "FooterNotRead")
    assert data_reads(storage) == [f"{TABLE}/good.parquet"]


@pytest.mark.parametrize("path", ["abfss://Elsewhere@onelake.test/Other.Lakehouse/Files/outside.parquet",
                                  "/elsewhere/outside.parquet"])
def test_a_file_outside_the_table_becomes_file_outside_table_with_no_request(tmp_path, path):
    lake = table_with(tmp_path, add(path, 5000, 10))
    storage = RecordingStorage(LocalStorage(str(lake.root)))
    data, _ = collect(storage)
    assert_not_read_in_table(data, path, "FileOutsideTable")
    assert data_reads(storage) == [f"{TABLE}/good.parquet"]


def test_a_footer_over_64_mb_becomes_footer_too_large_after_one_request(tmp_path):
    size = 100 * 1024 * 1024
    end = tail(MAX_FOOTER_LENGTH + 1)
    lake = table_with(tmp_path, add("large.parquet", size, 10))
    large = f"{TABLE}/large.parquet"
    storage = RecordingStorage(LocalStorage(str(lake.root)),
                               ranges=lambda path, offset, length: virtual_slice(size, end, offset, length) if path == large else None)
    data, _ = collect(storage)
    assert_not_read_in_table(data, "large.parquet", "FooterTooLarge")
    [read] = storage.reads(lambda path: path == large)
    assert read.offset + read.length == size
    assert read.length <= 32768


# ---- the footers that are read ----

def test_a_limit_of_one_footer_reads_one_footer(lake):
    storage = RecordingStorage(LocalStorage(str(lake.root)))
    data, _ = collect(storage, max_footers=1)
    doc = open_bytes(data)
    [table] = rows(doc, "tables")
    assert table["footerCount"] == 1
    assert table["fileCount"] == 3
    files = rows(doc, "files")
    [read] = [file for file in files if file.get("footerSize") is not None]
    assert len(data_reads(storage)) == 1
    groups = rows(doc, "rowGroups")
    assert all(group["file"] == read["id"] for group in groups)
    assert table["rowGroupCount"] == read["rowGroupCount"] == len(groups)
    assert table["footerSize"] == dump_file_of_size(read["fileSize"])[1]["footer"]["footerSize"]
    assert rows(doc, "issues") == []


def expected_tail(initial, floor, margin, largest):
    """The tail after footers whose largest needs this many bytes: the largest times the margin, never below the floor
    and never above the initial tail; the initial tail before any footer."""
    if largest == 0:
        return initial
    return int(min(initial, max(min(floor, initial), math.ceil(largest * margin))))


def assert_tails_follow_the_rule(storage, options):
    """In the order in which the files were read, the first request of each asks for the tail that the rule gives after
    the footers read before it, and a footer takes a second request only when it does not fit."""
    reads = storage.reads(lambda path: path.endswith(".parquet") and "/_delta_log/" not in path)
    order = list(dict.fromkeys(request.path for request in reads))
    assert len(order) == 3
    largest = 0
    result = []
    for path in order:
        size = size_of(copied_files()[posixpath.basename(path)])
        footer_size = dump_file_of_size(size)[1]["footer"]["footerSize"]
        first = min(expected_tail(options.footer_tail_bytes, options.footer_tail_floor_bytes, options.footer_tail_margin,
                                  largest), size)
        mine = [request for request in reads if request.path == path]
        assert mine[0].length == first, f"{path}: {mine[0].length} bytes, expected {first}"
        assert mine[0].offset == size - first
        assert len(mine) == (1 if footer_size + 8 <= first else 2), path
        result.append((path, first, len(mine)))
        largest = max(largest, footer_size + 8)
    return result


def test_the_first_file_is_read_with_the_initial_tail_and_the_next_with_the_learned_tail(lake):
    options = CollectorOptions(parallelism=1)
    storage = RecordingStorage(LocalStorage(str(lake.root)))
    adaptive, _ = collect(storage, options=options)
    files = assert_tails_follow_the_rule(storage, options)
    assert all(requests == 1 for _, _, requests in files)
    # The footers are about 1 KB: the learned tail is the floor, 8,192 bytes.
    assert files[0][1] == min(32768, size_of(copied_files()[posixpath.basename(files[0][0])]))
    assert all(first == 8192 for _, first, _ in files[1:])

    fixed_storage = RecordingStorage(LocalStorage(str(lake.root)))
    fixed, _ = collect(fixed_storage, options=CollectorOptions(parallelism=1, footer_tail_floor_bytes=32768))
    for request in fixed_storage.reads(lambda path: path.endswith(".parquet")):
        assert request.length == min(32768, size_of(copied_files()[posixpath.basename(request.path)]))
    a, b = open_bytes(adaptive), open_bytes(fixed)
    assert a.manifest["collection"]["bytesRead"] < b.manifest["collection"]["bytesRead"]
    assert a.manifest["collection"]["requestCount"] == b.manifest["collection"]["requestCount"]
    from collect_kit import differences
    assert differences(a, b) == []


def test_with_a_tiny_floor_and_no_margin_a_larger_footer_costs_a_second_request_and_raises_the_tail(lake):
    options = CollectorOptions(parallelism=1, footer_tail_floor_bytes=12, footer_tail_margin=1.0)
    storage = RecordingStorage(LocalStorage(str(lake.root)))
    collect(storage, options=options)
    files = assert_tails_follow_the_rule(storage, options)
    for index in range(1, len(files)):
        path, first, _ = files[index]
        assert first < size_of(copied_files()[posixpath.basename(path)])
        if files[index - 1][2] == 2:
            assert first > files[index - 1][1]


def test_a_floor_above_the_tail_reads_with_the_tail(lake):
    options = CollectorOptions(parallelism=1, footer_tail_bytes=4096, footer_tail_floor_bytes=8192)
    storage = RecordingStorage(LocalStorage(str(lake.root)))
    collect(storage, options=options)
    assert_tails_follow_the_rule(storage, options)
    assert all(request.length <= 4096 for request in storage.reads(lambda path: path.endswith(".parquet")))


def test_a_short_tail_reads_each_footer_in_two_requests_and_gives_the_same_footprint(lake):
    once = RecordingStorage(LocalStorage(str(lake.root)))
    first, _ = collect(once)
    twice = RecordingStorage(LocalStorage(str(lake.root)))
    second, _ = collect(twice, options=CollectorOptions(footer_tail_bytes=12))
    for name in copied_files():
        path = f"{TABLE}/{name}"
        assert len(once.reads(lambda p: p == path)) == 1
        assert len(twice.reads(lambda p: p == path)) == 2
    from collect_kit import differences
    assert differences(open_bytes(second), open_bytes(first)) == []


def test_the_collection_counts_every_request_of_the_storage(lake):
    storage = RecordingStorage(LocalStorage(str(lake.root)))
    tables = discover_all(storage)
    requests, read = storage.request_count, storage.bytes_read
    _, builder = collect_bytes(storage, tables, checkpoint_reader=None)
    assert builder.collection["requestCount"] == storage.request_count - requests
    assert builder.collection["bytesRead"] == storage.bytes_read - read
    assert builder.collection["bytesRead"] > 0
    assert builder.collection["level"] == 2


def test_a_copied_table_has_the_footers_of_the_dump(lake):
    data, _ = collect(LocalStorage(str(lake.root)))
    doc = open_bytes(data)
    [table] = rows(doc, "tables")
    assert rows(doc, "issues") == []
    every = [dumped(name) for name in copied_files()]
    assert table["footerCount"] == 3
    assert table["footerSize"] == sum(file["footer"]["footerSize"] for file in every)
    assert table["compressedSize"] == sum(chunk_sum(file, "compressed") for file in every)
    assert table["rowCount"] == sum(file["numRecords"] for file in every)
    assert table["deletedRowCount"] == sum(file.get("deletedRows", 0) for file in every)
    assert table["vOrderedFileCount"] == sum(1 for file in every if is_vordered(file))
    assert "someone@example.com" not in all_text(data)
