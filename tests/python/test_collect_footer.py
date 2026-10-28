"""The footer parser of pqax.collect on its own: parse_footer and FooterReader against the probe dump of every data file of
the fixture, and on bytes that are not a footer, written with the Thrift compact protocol (README of the C# collector,
Footer parser and Footer read failures). Standard library only.
"""
from __future__ import annotations

import random
import struct

import pytest

from collect_kit import (ORDERS, SALES, Thrift, VirtualFileStorage, RecordingStorage, data_files, dump_file_of_size,
                         encodings_of, every_type, file_bytes, footer, metadata_bytes, nested_lists, nested_structures,
                         row_groups, size_of, tail, unreferenced_data_files, FIXTURE)
from pqax.collect import (MAX_FOOTER_LENGTH, FooterError, FooterReader, IssueCodes, LocalStorage, ParquetFooter,
                          parse_footer)

DATA_FILES = data_files()


def local() -> LocalStorage:
    return LocalStorage(str(FIXTURE))


def assert_same_as_dump(parsed: ParquetFooter, table, file) -> None:
    expected = file["footer"]
    assert parsed.version == expected["version"]
    assert parsed.num_rows == expected["numRows"]
    assert parsed.created_by == expected["createdBy"]

    pairs = expected["keyValues"]
    assert [pair.key for pair in parsed.key_value_metadata] == [pair["key"] for pair in pairs]
    for actual, wanted in zip(parsed.key_value_metadata, pairs):
        assert actual.value_length == wanted.get("valueLength")
        if "value" in wanted:
            assert actual.value == wanted["value"]

    leaves = table.schemas[expected["schema"]]["columns"]
    assert len(parsed.leaves) == len(leaves)
    for index, (leaf, wanted) in enumerate(zip(parsed.leaves, leaves)):
        assert leaf.ordinal == index
        assert tuple(leaf.path) == tuple(wanted["path"])
        assert leaf.physical_type == wanted["type"]
        assert leaf.repetition == wanted.get("repetition")
        assert leaf.converted_type == wanted.get("converted")

    groups = row_groups(file)
    assert len(parsed.row_groups) == len(groups)
    for g, (actual, group) in enumerate(zip(parsed.row_groups, groups)):
        assert actual.num_rows == group["numRows"]
        assert actual.total_byte_size == group["totalByteSize"]
        assert actual.total_compressed_size == group["totalCompressedSize"]
        assert len(actual.columns) == len(group["codec"])
        for c, chunk in enumerate(actual.columns):
            where = f"row group {g}, column {c}"
            assert chunk.has_metadata, where
            assert not chunk.is_encrypted, where
            assert chunk.codec == group["codec"][c], where
            assert sorted(chunk.encodings) == encodings_of(group, c), where
            assert chunk.num_values == group["numValues"][c], where
            assert chunk.total_compressed_size == group["compressed"][c], where
            assert chunk.total_uncompressed_size == group["uncompressed"][c], where
            assert chunk.data_page_offset == group["dataPageOffset"][c], where
            assert chunk.dictionary_page_offset == group["dictionaryPageOffset"][c], where
            assert chunk.null_count == group["nullCount"][c], where
            assert chunk.distinct_count == group["distinctCount"][c], where
            assert chunk.has_min_max == group["hasMinMax"][c], where
            assert chunk.bloom_filter_length == group["bloomFilterLength"][c], where
            assert chunk.unencoded_byte_array_data_bytes == group["unencodedByteArrayBytes"][c], where
            assert chunk.column_index_length == group["columnIndexLength"][c], where
            assert chunk.offset_index_length == group["offsetIndexLength"][c], where
            if group["encodingStats"][c] is None:
                assert not chunk.encoding_stats, where
            dictionary = group["dictionaryPageOffset"][c]
            assert chunk.has_dictionary_page == (dictionary is not None), where
            assert chunk.dictionary_page_size == (None if dictionary is None else group["dataPageOffset"][c] - dictionary), where
            assert chunk.min_value is None and chunk.max_value is None, where


# ---- the fixture against the dump ----

def test_every_data_file_of_the_fixture_has_a_footer_in_the_dump_and_one_parquet_file_is_unreferenced():
    assert len(DATA_FILES) == sum(len(table.files) for table in (SALES, ORDERS))
    for path in DATA_FILES:
        dump_file_of_size(size_of(path))
    assert unreferenced_data_files() == [SALES.table_path + "/Year=2024/part-0004.parquet"]
    assert size_of(unreferenced_data_files()[0]) == 737


@pytest.mark.parametrize("path", DATA_FILES)
def test_the_footer_reader_agrees_with_the_dump(path):
    size = size_of(path)
    table, file = dump_file_of_size(size)
    parsed = FooterReader(local()).read(path, size)
    assert parsed.footer_length == file["footer"]["footerSize"]
    assert parsed.request_count == 1
    assert_same_as_dump(parsed, table, file)


@pytest.mark.parametrize("path", DATA_FILES)
def test_parsing_the_metadata_bytes_gives_the_same_footer(path):
    data = file_bytes(path)
    table, file = dump_file_of_size(len(data))
    meta = metadata_bytes(data)
    assert len(meta) == file["footer"]["footerSize"]
    parsed = parse_footer(meta)
    assert parsed.request_count == 0
    assert_same_as_dump(parsed, table, file)


@pytest.mark.parametrize("path", DATA_FILES)
def test_a_tail_shorter_than_the_footer_takes_a_second_request(path):
    size = size_of(path)
    table, file = dump_file_of_size(size)
    storage = RecordingStorage(local())
    parsed = FooterReader(storage, tail_bytes=12).read(path, size)
    assert parsed.request_count == 2
    assert [request.kind for request in storage.requests] == ["read", "read"]
    assert parsed.footer_length == file["footer"]["footerSize"]
    assert_same_as_dump(parsed, table, file)


@pytest.mark.parametrize("path", DATA_FILES)
def test_the_default_tail_reads_only_the_end_of_the_file_once(path):
    size = size_of(path)
    storage = RecordingStorage(local())
    FooterReader(storage).read(path, size)
    [request] = storage.requests
    assert request.offset + request.length == size
    assert request.length == min(size, 32768)


def test_the_reader_takes_the_tail_of_each_call():
    path = DATA_FILES[0]
    size = size_of(path)
    footer_size = dump_file_of_size(size)[1]["footer"]["footerSize"]
    storage = RecordingStorage(local())
    reader = FooterReader(storage)
    short = reader.read(path, size, tail_bytes=12)
    assert short.request_count == 2
    assert storage.requests[0].length == 12
    exact = reader.read(path, size, tail_bytes=footer_size + 8)
    assert exact.request_count == 1
    assert storage.requests[-1].length == footer_size + 8
    default = reader.read(path, size)
    assert default.request_count == 1
    assert storage.requests[-1].length == min(size, 32768)
    for parsed in (short, exact):
        assert parsed.footer_length == footer_size
        assert parsed.num_rows == default.num_rows
        assert [group.total_compressed_size for group in parsed.row_groups] == \
            [group.total_compressed_size for group in default.row_groups]


def test_data_values_are_kept_only_when_asked():
    for path in DATA_FILES:
        meta = metadata_bytes(file_bytes(path))
        without = parse_footer(meta)
        assert all(chunk.min_value is None and chunk.max_value is None
                   for group in without.row_groups for chunk in group.columns)
        with_values = parse_footer(meta, include_data_values=True)
        chunks = [chunk for group in with_values.row_groups for chunk in group.columns if chunk.has_min_max]
        assert chunks
        assert all(chunk.min_value is not None and chunk.max_value is not None for chunk in chunks)
        reader = FooterReader(local(), include_data_values=True).read(path, size_of(path))
        assert any(chunk.min_value is not None for group in reader.row_groups for chunk in group.columns)


def test_the_v_order_keys_of_the_fixture_keep_their_values():
    vordered = [path for path in DATA_FILES if dump_file_of_size(size_of(path))[1]["footer"]["keyValues"]]
    assert vordered
    for path in vordered:
        parsed = parse_footer(metadata_bytes(file_bytes(path)))
        values = {pair.key: pair.value for pair in parsed.key_value_metadata}
        assert values == {"com.microsoft.parquet.vorder.enabled": "true", "com.microsoft.parquet.vorder.level": "default"}


# ---- a synthetic footer ----

def summary(parsed: ParquetFooter):
    """What a test compares between two parses of footers that must give the same result."""
    return {
        "version": parsed.version, "num_rows": parsed.num_rows, "created_by": parsed.created_by,
        "keys": [(pair.key, pair.value_length, pair.value) for pair in parsed.key_value_metadata],
        "leaves": [(tuple(leaf.path), leaf.physical_type, leaf.repetition, leaf.converted_type) for leaf in parsed.leaves],
        "groups": [(group.num_rows, group.total_byte_size, group.total_compressed_size, group.file_offset,
                    [(chunk.has_metadata, chunk.codec, tuple(sorted(chunk.encodings)), chunk.num_values,
                      chunk.total_uncompressed_size, chunk.total_compressed_size, chunk.data_page_offset,
                      chunk.dictionary_page_offset, chunk.null_count, chunk.distinct_count, chunk.has_min_max)
                     for chunk in group.columns]) for group in parsed.row_groups],
    }


BASELINE = footer()


def test_a_synthetic_footer_is_parsed():
    parsed = parse_footer(BASELINE)
    assert parsed.version == 1
    assert parsed.num_rows == 100
    assert parsed.created_by == "Synthetic writer 1.0"
    assert list(parsed.key_value_metadata) == []
    [leaf] = parsed.leaves
    assert (tuple(leaf.path), leaf.physical_type, leaf.repetition, leaf.converted_type) == (("id",), "INT64", "OPTIONAL", "INT_64")
    [group] = parsed.row_groups
    assert (group.num_rows, group.total_byte_size, group.total_compressed_size, group.file_offset) == (100, 800, 400, 4)
    [chunk] = group.columns
    assert chunk.has_metadata and not chunk.is_encrypted
    assert chunk.codec == "ZSTD"
    assert sorted(chunk.encodings) == ["PLAIN", "RLE"]
    assert (chunk.num_values, chunk.total_uncompressed_size, chunk.total_compressed_size) == (100, 800, 400)
    assert chunk.data_page_offset == 4
    assert chunk.dictionary_page_offset is None and not chunk.has_dictionary_page and chunk.dictionary_page_size is None
    assert (chunk.null_count, chunk.distinct_count, chunk.has_min_max) == (0, 7, True)
    assert chunk.min_value is None and chunk.max_value is None
    assert not parsed.has_encryption_algorithm and not parsed.has_encrypted_columns
    assert parse_footer(BASELINE, include_data_values=True).row_groups[0].columns[0].min_value is not None


@pytest.mark.parametrize("where", ["file", "root", "leaf", "row_group", "chunk", "column", "statistics"])
def test_unknown_fields_of_every_type_are_skipped(where):
    changed = footer(extra={where: every_type})
    assert changed != BASELINE
    assert summary(parse_footer(changed)) == summary(parse_footer(BASELINE))


def test_unknown_fields_everywhere_at_once_are_skipped():
    changed = footer(extra={where: every_type for where in ("file", "root", "leaf", "row_group", "chunk", "column", "statistics")},
                     leaves=("id",))
    assert summary(parse_footer(changed)) == summary(parse_footer(BASELINE))


def test_long_field_headers_are_read():
    changed = footer(always_long=True)
    assert len(changed) > len(BASELINE)
    assert summary(parse_footer(changed)) == summary(parse_footer(BASELINE))


def test_an_unknown_field_with_a_large_id_and_a_negative_delta_is_skipped():
    def far(w: Thrift) -> None:
        w.i32(32000, 1)  # a delta far over 15
        w.binary(500, "back to a smaller id")  # a negative delta
        w.boolean(31999, True)
    assert summary(parse_footer(footer(extra={"file": far, "column": far}))) == summary(parse_footer(BASELINE))


@pytest.mark.parametrize("name, write, check", [
    ("file.created_by", lambda w: w.i32(6, 5), lambda parsed: parsed.created_by is None),
    ("file.key_value_metadata", lambda w: w.i64(5, 9), lambda parsed: not parsed.key_value_metadata),
    ("column.dictionary_page_offset", lambda w: w.binary(11, "not a number"),
     lambda parsed: parsed.row_groups[0].columns[0].dictionary_page_offset is None),
    ("statistics.null_count", lambda w: w.binary(3, "not a count"),
     lambda parsed: parsed.row_groups[0].columns[0].null_count is None),
], ids=["created_by as i32", "key_value_metadata as i64", "dictionary_page_offset as binary", "null_count as binary"])
def test_a_known_field_with_an_unexpected_type_is_skipped(name, write, check):
    parsed = parse_footer(footer(replace={name: write}))
    assert check(parsed)
    assert parsed.num_rows == 100
    assert parsed.row_groups[0].columns[0].total_compressed_size == 400


def test_booleans_in_field_headers_are_read():
    def flags(w: Thrift) -> None:
        for field in range(200, 210):
            w.boolean(field, field % 2 == 0)
    assert summary(parse_footer(footer(extra={"statistics": flags, "file": flags}))) == summary(parse_footer(BASELINE))


def invalid(data: bytes, code: str = "FooterInvalid") -> FooterError:
    with pytest.raises(FooterError) as caught:
        parse_footer(data)
    assert caught.value.code == code
    assert str(caught.value)
    return caught.value


@pytest.mark.parametrize("omitted", ["file.schema", "file.num_rows", "file.row_groups", "row_group.num_rows",
                                     "row_group.columns"])
def test_a_required_field_that_is_missing_is_invalid(omitted):
    invalid(footer(omit=[omitted]))


@pytest.mark.parametrize("data", [b"", b"\x00", b"\x15\x02", b"\xff" * 10], ids=["empty", "stop", "one field", "ff"])
def test_bytes_that_are_not_a_footer_are_invalid(data):
    invalid(data)


def test_every_truncation_of_a_synthetic_footer_is_invalid():
    for length in range(len(BASELINE)):
        invalid(BASELINE[:length])


def test_every_truncation_of_a_real_footer_is_invalid():
    path = min(DATA_FILES, key=lambda path: dump_file_of_size(size_of(path))[1]["footer"]["footerSize"])
    meta = metadata_bytes(file_bytes(path))
    for length in range(len(meta)):
        invalid(meta[:length])


def test_a_list_longer_than_the_bytes_left_is_invalid():
    def huge_list(w: Thrift) -> None:
        w.list_of(300, Thrift.I64, 1_000_000_000)
        w.varint(2)
    invalid(footer(extra={"file": huge_list}))

    def huge_structs(w: Thrift) -> None:
        w.list_of(300, Thrift.STRUCT, 2 ** 31 - 1)
    invalid(footer(extra={"column": huge_structs}))

    def huge_map(w: Thrift) -> None:
        w.map_of(300, Thrift.BINARY, Thrift.BINARY, 2 ** 40)
    invalid(footer(extra={"leaf": huge_map}))

    def huge_binary(w: Thrift) -> None:
        w.header(300, Thrift.BINARY).varint(2 ** 40)
    invalid(footer(extra={"file": huge_binary}))

    def known_list(w: Thrift) -> None:
        w.list_of(5, Thrift.STRUCT, 100_000_000)
    invalid(footer(replace={"file.key_value_metadata": known_list}))


def test_a_varint_without_an_end_is_invalid():
    def endless(w: Thrift) -> None:
        w.header(300, Thrift.I64).raw(b"\xff" * 20)
    invalid(footer(extra={"file": endless}))


@pytest.mark.parametrize("nest", [nested_structures, nested_lists], ids=["structures", "lists"])
def test_nesting_deeper_than_64_levels_is_invalid_and_shallow_nesting_is_skipped(nest):
    shallow = footer(extra={"file": lambda w: nest(w, 300, 20)})
    assert summary(parse_footer(shallow)) == summary(parse_footer(BASELINE))
    # Lists count as levels as structures do, in C# as in Python, so neither can exhaust the stack.
    invalid(footer(extra={"file": lambda w: nest(w, 300, 200)}))
    invalid(footer(extra={"column": lambda w: nest(w, 300, 5000)}))


@pytest.mark.parametrize("leaves, chunks", [(("a", "b"), 1), (("a",), 2), (("a", "b", "c"), 0)])
def test_a_row_group_whose_chunks_do_not_match_the_leaves_is_invalid(leaves, chunks):
    invalid(footer(leaves=leaves, chunks=chunks))
    assert len(parse_footer(footer(leaves=leaves)).leaves) == len(leaves)


def test_a_column_chunk_without_metadata_is_parsed_without_it():
    parsed = parse_footer(footer(meta_data=False))
    [chunk] = parsed.row_groups[0].columns
    assert not chunk.has_metadata
    assert not chunk.is_encrypted


def test_encrypted_columns_and_an_encryption_algorithm_are_reported():
    def crypto(w: Thrift) -> None:
        w.begin(8).begin(1).end().end()  # crypto_metadata: ENCRYPTION_WITH_FOOTER_KEY
        w.binary(9, b"\x01\x02\x03\x04")  # encrypted_column_metadata
    parsed = parse_footer(footer(meta_data=False, extra={"chunk": crypto}))
    [chunk] = parsed.row_groups[0].columns
    assert not chunk.has_metadata
    assert chunk.is_encrypted
    assert parsed.has_encrypted_columns

    def algorithm(w: Thrift) -> None:
        w.begin(8).begin(1).binary(1, b"aad").end().end()  # encryption_algorithm: AES_GCM_V1
    parsed = parse_footer(footer(extra={"file": algorithm}))
    assert parsed.has_encryption_algorithm
    assert parsed.num_rows == 100


def test_the_key_value_metadata_keeps_the_values_of_the_allow_list_only():
    pairs = [
        ("com.microsoft.parquet.vorder.enabled", "true"),
        ("com.microsoft.parquet.vorder.level", "default"),
        ("org.apache.spark.version", "3.5.1"),
        ("spark.sql.parquet.string_prefix_length", "32"),
        ("org.apache.spark.sql.parquet.row.metadata", '{"fields":[{"name":"secret_column"}]}'),
        ("com.microsoft.parquet.stats", "a data value 12345"),
        ("ARROW:schema", "/////base64"),
        ("no.value", None),
    ]
    parsed = parse_footer(footer(key_values=pairs))
    actual = [(pair.key, pair.value_length, pair.value) for pair in parsed.key_value_metadata]
    assert actual == [
        ("com.microsoft.parquet.vorder.enabled", 4, "true"),
        ("com.microsoft.parquet.vorder.level", 7, "default"),
        ("org.apache.spark.version", 5, "3.5.1"),
        ("spark.sql.parquet.string_prefix_length", 2, "32"),
        ("org.apache.spark.sql.parquet.row.metadata", len(pairs[4][1]), None),
        ("com.microsoft.parquet.stats", len(pairs[5][1]), None),
        ("ARROW:schema", len(pairs[6][1]), None),
        ("no.value", None, None),
    ]
    # Asking for data values does not keep the values of other keys.
    again = parse_footer(footer(key_values=pairs), include_data_values=True)
    assert [pair.value for pair in again.key_value_metadata][4:7] == [None, None, None]


def test_footer_error_carries_its_code():
    error = FooterError("FooterTooLarge", "A footer of 70 MB.")
    assert isinstance(error, Exception)
    assert error.code == "FooterTooLarge"
    assert IssueCodes.FOOTER_INVALID == "FooterInvalid"
    assert IssueCodes.FOOTER_ENCRYPTED == "FooterEncrypted"
    assert IssueCodes.FOOTER_TOO_LARGE == "FooterTooLarge"
    assert MAX_FOOTER_LENGTH == 64 * 1024 * 1024


# ---- the tail of a file ----

def read_code(storage, size: int, tail_bytes: int = 32768) -> str:
    with pytest.raises(FooterError) as caught:
        FooterReader(storage, tail_bytes=tail_bytes).read("file.parquet", size)
    assert str(caught.value)
    return caught.value.code


@pytest.mark.parametrize("magic", [b"XXXX", b"PAR2", b"par1"])
def test_a_file_that_does_not_end_with_the_magic_is_invalid(magic):
    assert read_code(VirtualFileStorage(1000, tail(100, magic)), 1000) == "FooterInvalid"


def test_an_encrypted_footer_is_reported_as_such():
    assert read_code(VirtualFileStorage(1000, tail(100, b"PARE")), 1000) == "FooterEncrypted"


@pytest.mark.parametrize("length", [0, -5, 989, 2 ** 31 - 1])
def test_a_footer_length_that_does_not_fit_the_file_is_invalid(length):
    assert read_code(VirtualFileStorage(1000, tail(length)), 1000) == "FooterInvalid"


def test_a_file_smaller_than_twelve_bytes_is_invalid():
    assert read_code(VirtualFileStorage(11, tail(1)), 11) == "FooterInvalid"


def test_a_file_shorter_than_its_stated_size_is_invalid():
    data = file_bytes(DATA_FILES[0])
    storage = VirtualFileStorage(len(data) - 100, data[:-100])
    assert read_code(storage, len(data)) == "FooterInvalid"


def test_a_footer_over_64_mb_is_too_large_and_is_not_read():
    size = 100 * 1024 * 1024
    storage = VirtualFileStorage(size, tail(MAX_FOOTER_LENGTH + 1))
    assert read_code(storage, size) == "FooterTooLarge"
    assert len(storage.reads) == 1
    assert storage.reads[0][1] <= 32768


def test_a_footer_longer_than_the_tail_is_read_with_a_second_request():
    meta = footer(created_by="x" * 5000)
    data = b"PAR1" + bytes(20000) + meta + tail(len(meta))
    storage = VirtualFileStorage(len(data), data)
    parsed = FooterReader(storage, tail_bytes=1024).read("file.parquet", len(data))
    assert parsed.request_count == 2
    assert len(storage.reads) == 2
    assert parsed.footer_length == len(meta)
    assert parsed.created_by == "x" * 5000
    assert storage.reads[0] == (len(data) - 1024, 1024)


def test_a_footer_that_fits_the_tail_is_read_with_one_request():
    meta = footer()
    data = b"PAR1" + bytes(20000) + meta + tail(len(meta))
    storage = VirtualFileStorage(len(data), data)
    parsed = FooterReader(storage, tail_bytes=1024).read("file.parquet", len(data))
    assert parsed.request_count == 1
    assert storage.reads == [(len(data) - 1024, 1024)]
    assert summary(parsed) == summary(parse_footer(meta))


# ---- only FooterError on bad bytes ----

def test_changed_bytes_in_a_footer_raise_only_a_footer_error():
    generator = random.Random(20261007)
    parsed = refused = 0
    sources = [metadata_bytes(file_bytes(path)) for path in DATA_FILES[:3]] + [BASELINE]
    for meta in sources:
        for round_ in range(120):
            changed = bytearray(meta)
            for _ in range(1 + generator.randrange(3)):
                changed[generator.randrange(len(changed))] = generator.randrange(256)
            try:
                parse_footer(bytes(changed), include_data_values=round_ % 2 == 0)
                parsed += 1
            except FooterError as error:
                assert error.code in ("FooterInvalid", "FooterEncrypted", "FooterTooLarge")
                refused += 1
    assert refused > 0
    assert parsed + refused == 120 * len(sources)


def test_random_truncations_and_insertions_raise_only_a_footer_error():
    generator = random.Random(7)
    meta = metadata_bytes(file_bytes(DATA_FILES[0]))
    for _ in range(150):
        start = generator.randrange(len(meta))
        stop = generator.randrange(start, len(meta) + 1)
        cut = meta[:start] + bytes(generator.randrange(256) for _ in range(generator.randrange(4))) + meta[stop:]
        try:
            parse_footer(cut)
        except FooterError:
            pass
