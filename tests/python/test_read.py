"""The reader of pqax against the conformance kit: conformance/README.md, "A reader test", and plan section 4.5."""
from __future__ import annotations

import io
import pathlib

import pytest

import pqax
from pqax_kit import (DETAIL_TABLES, EMBEDDED_PART, FORMAT_TABLES, INVALID, REFUSED, SAMPLES, SHAPE_KEYS,
                      SUMMARY_TABLES, CountingReader, compute_shape, drop_entry, entry_spans, expected_by_name,
                      invalid_cases, row_total, shape_differences, touched_entries, write_package_without_footprint)

EXPECTED = expected_by_name()
SHAPE_ONLY = ("tablesFileSize", "tablesRowCount", "columnChunksCompressedSize", "tables", "largestColumns")
PQAX_SAMPLES = sorted(name for name, expected in EXPECTED.items() if expected["embeddedPart"] is None)


def open_sample(expected):
    path = SAMPLES / expected["file"]
    if expected["embeddedPart"] is not None:
        return pqax.open_embedded(str(path), expected["embeddedPart"])
    return pqax.open(str(path))


def read_table(doc, name):
    """A table read whole: from the summary for a summary table, with doc.table for the others."""
    if name in SUMMARY_TABLES:
        return doc.summary.get(name, {})
    return doc.table(name)


def shape_of(doc):
    return compute_shape(doc.manifest, doc.row_count, lambda name: read_table(doc, name))


@pytest.fixture(params=sorted(EXPECTED), ids=str)
def expected(request):
    return EXPECTED[request.param]


# ---- every sample gives the expected results ----

def test_format_version_and_parts(expected):
    with open_sample(expected) as doc:
        assert doc.manifest["format"] == expected["format"]
        assert doc.manifest["formatVersion"] == expected["formatVersion"]
        assert [part["name"] for part in doc.manifest["parts"]] == expected["parts"]


def test_embedded_part_and_compression(expected):
    with open_sample(expected) as doc:
        assert doc.embedded_part == expected["embeddedPart"]
        assert doc.embedded_compression == expected["embeddedCompression"]


def test_rows_of_every_table(expected):
    with open_sample(expected) as doc:
        for name, count in expected["rows"].items():
            assert doc.row_count(name) == count, name
            table = doc.table(name)
            assert table, f"{name} has {count} rows and doc.table returns no field"
            for field_name, values in table.items():
                assert len(values) == count, f"{name}.{field_name}"
        for name in FORMAT_TABLES:
            if name not in expected["rows"]:
                assert doc.row_count(name) == 0, name
                assert doc.table(name) == {}, name


def test_totals_joins_and_largest_columns(expected):
    with open_sample(expected) as doc:
        shape = shape_of(doc)
    assert not shape_differences(shape, expected)


def test_summary_tables_are_the_same_through_table(expected):
    with open_sample(expected) as doc:
        for name in expected["rows"]:
            if name in doc.summary:
                assert doc.table(name) == doc.summary[name], name


def test_rows_gives_one_dictionary_for_each_row():
    with pqax.open(str(SAMPLES / "sample.pqax")) as doc:
        files = doc.table("files")
        rows = list(doc.rows("files"))
        assert len(rows) == doc.row_count("files") == 5
        for position, row in enumerate(rows):
            for field_name, values in files.items():
                assert row[field_name] == values[position], field_name
        assert list(doc.rows("deleteFiles")) == []


def test_every_way_to_name_a_file_gives_the_same_content():
    path = SAMPLES / "sample.pqax"
    data = path.read_bytes()
    shapes = []
    for source in (str(path), path, data, io.BytesIO(data)):
        with pqax.open(source) as doc:
            shapes.append(shape_of(doc))
    assert all(shape == shapes[0] for shape in shapes)
    assert not shape_differences(shapes[0], EXPECTED["sample"])


def test_a_file_object_stays_open():
    with open(SAMPLES / "sample.pqax", "rb") as handle:
        with pqax.open(handle) as doc:
            assert doc.row_count("files") == 5
        assert not handle.closed


# ---- what a reader reads ----

@pytest.mark.parametrize("name", PQAX_SAMPLES)
def test_opening_reads_the_manifest_and_the_summary_and_no_detail_part(name):
    data = (SAMPLES / EXPECTED[name]["file"]).read_bytes()
    spans = entry_spans(data)
    reader = CountingReader(data)
    doc = pqax.open(reader)
    try:
        assert touched_entries(spans, reader.ranges) == {"manifest.json", "summary.json"}
    finally:
        doc.close()


def test_a_detail_table_reads_its_parts_and_no_other():
    data = (SAMPLES / "multipart.pqax").read_bytes()
    spans = entry_spans(data)
    reader = CountingReader(data)
    with pqax.open(reader) as doc:
        reader.ranges.clear()
        assert len(doc.table("columnChunks")["compressedSize"]) == 52
        assert touched_entries(spans, reader.ranges) == {"columnChunks.1.json", "columnChunks.2.json",
                                                         "columnChunks.3.json"}
        reader.ranges.clear()
        assert len(doc.table("files")["id"]) == 5
        assert touched_entries(spans, reader.ranges) == {"files.1.json", "files.2.json"}


# ---- the facts of conformance/README.md that the expected files do not hold ----

def test_sample_known_empty_values_are_not_null():
    with pqax.open(str(SAMPLES / "sample.pqax")) as doc:
        assert doc.table("commits")["metrics"][1] == {}
        assert doc.summary["tables"]["delta"][1]["readerFeatures"] == []


TABLE_FIELDS_0_2 = ("unreferencedFileCount", "unreferencedFileSize", "nonLiveOldestRemovedAt", "nonLiveNewestRemovedAt")


def test_sample_has_the_fields_of_format_0_2():
    with pqax.open(str(SAMPLES / "sample.pqax")) as doc:
        tables = doc.summary["tables"]
        assert tables["id"] == [1, 2]
        assert tables["unreferencedFileCount"] == [1, 0]
        assert tables["unreferencedFileSize"] == [737, 0]
        assert tables["nonLiveOldestRemovedAt"] == ["2023-11-14T22:14:10.000Z", None]
        assert tables["nonLiveNewestRemovedAt"] == ["2023-11-14T22:14:10.000Z", None]
        assert doc.table("partitions")["lastModifiedAt"] == ["2023-11-14T22:15:00.000Z", "2023-11-14T22:15:10.000Z"]


def test_a_file_of_format_0_2_opens_and_is_valid():
    with pqax.open(str(SAMPLES / "sample.pqax")) as doc:
        assert doc.manifest["formatVersion"] == "0.2"
        assert row_total(doc.table("columnChunks")) == 52
    assert pqax.validate(str(SAMPLES / "sample.pqax")).is_valid


def test_a_file_of_format_0_1_opens_without_the_fields_of_format_0_2():
    with pqax.open(str(SAMPLES / "format-0.1.pqax")) as old, pqax.open(str(SAMPLES / "sample.pqax")) as new:
        assert old.manifest["formatVersion"] == "0.1"
        assert not shape_differences(shape_of(old), EXPECTED["sample"], [key for key in SHAPE_KEYS if key != "formatVersion"])
        rows = list(old.rows("tables"))
        assert len(rows) == 2
        for row in rows:
            for name in TABLE_FIELDS_0_2:
                assert row.get(name) is None, name
        partitions = list(old.rows("partitions"))
        assert len(partitions) == 2
        assert all(row.get("lastModifiedAt") is None for row in partitions)
        # Apart from the five fields, the tables are those of sample.pqax.
        for table in FORMAT_TABLES:
            expected = {name: values for name, values in new.table(table).items()
                        if not (table == "tables" and name in TABLE_FIELDS_0_2)
                        and not (table == "partitions" and name == "lastModifiedAt")}
            assert old.table(table) == expected, table


def test_multipart_concatenates_parts_and_fills_missing_fields_with_null():
    with pqax.open(str(SAMPLES / "multipart.pqax")) as doc:
        files = doc.table("files")
        assert files["deletionVectorSize"] == [40, None, None, None, None]
        assert files["partition"] == [1, 1, 2, None, None]
        assert files["id"] == [1, 2, 3, 4, 5]
    with pqax.open(str(SAMPLES / "sample.pqax")) as whole, pqax.open(str(SAMPLES / "multipart.pqax")) as split:
        for name in DETAIL_TABLES:
            assert split.table(name) == whole.table(name), name


def test_extensions_are_ignored_or_returned_as_they_are():
    with pqax.open(str(SAMPLES / "extensions.pqax")) as doc:
        assert "encodings" not in doc.table("columnChunks")
        assert doc.summary["tables"]["acme_score"] == [7, 9]
        assert doc.manifest["acme_build"] == {"tool": "acme-writer", "number": 7}
        assert doc.table("files")["acme_tier"] == ["hot"] * 5
        assert "acme_readme.txt" not in [part["name"] for part in doc.manifest["parts"]]
        assert doc.row_count("acme_reviews") == 2
        assert doc.row_count("acme_details") == 3
        assert doc.table("acme_details")["label"] == ["a", "b", "c"]
        first = doc.summary["tables"]["fileSizes"][0]
        assert (first["count"], first["sum"], first["median"]) == (3, 159121, 47820)
    with pqax.open(str(SAMPLES / "sample.pqax")) as sample, pqax.open(str(SAMPLES / "extensions.pqax")) as doc:
        for name in ("partitions", "rowGroups", "commits"):
            assert doc.table(name) == sample.table(name), name
        chunks = dict(sample.table("columnChunks"))
        del chunks["encodings"]
        assert doc.table("columnChunks") == chunks


def test_a_later_depth_is_kept_as_written():
    with pqax.open(str(SAMPLES / "later-depth.pqax")) as doc:
        assert doc.summary["tables"]["detailDepth"] == ["Pages", "Pages"]


def test_a_later_second_number_of_the_version_is_accepted():
    with pqax.open(str(SAMPLES / "later-version.pqax")) as doc:
        assert doc.manifest["formatVersion"] == "0.9"
        assert doc.row_count("columnChunks") == 52


@pytest.mark.parametrize("name", ["embedded-stored.vpax", "embedded-deflated.vpax"])
def test_an_embedded_footprint_has_the_tables_of_the_sample(name):
    with pqax.open_embedded(str(SAMPLES / name)) as embedded, pqax.open(str(SAMPLES / "sample.pqax")) as sample:
        assert embedded.embedded_part == EMBEDDED_PART
        assert embedded.manifest == sample.manifest
        for table in FORMAT_TABLES:
            assert embedded.table(table) == sample.table(table), table


def test_an_embedded_part_that_is_not_there_is_refused_with_its_code(tmp_path):
    with pytest.raises(pqax.PqaxError) as raised:
        pqax.open_embedded(str(SAMPLES / "embedded-stored.vpax"), "Nothing.pqax")
    assert raised.value.code == "embedded.partMissing"
    package = tmp_path / "no-footprint.vpax"
    write_package_without_footprint(package)
    with pytest.raises(pqax.PqaxError) as raised:
        pqax.open_embedded(str(package))
    assert raised.value.code == "embedded.partMissing"


# ---- the invalid samples, read by a reader ----

@pytest.mark.parametrize("name,code", sorted(REFUSED.items()))
def test_a_reader_refuses_three_invalid_files(name, code):
    with pytest.raises(pqax.PqaxError) as raised:
        pqax.open(str(INVALID / name))
    assert raised.value.code == code


@pytest.mark.parametrize("name", sorted(set(invalid_cases()) - set(REFUSED)))
def test_a_reader_opens_every_other_invalid_file(name):
    with pqax.open(str(INVALID / name)) as doc:
        assert isinstance(doc.summary, dict)
        assert isinstance(doc.row_count("tables"), int)


def test_a_byte_order_mark_is_skipped():
    with pqax.open(str(INVALID / "json.bom.pqax")) as doc:
        shape = shape_of(doc)
    assert not shape_differences(shape, EXPECTED["sample"])


def test_a_broken_detail_part_fails_only_the_read_of_its_table():
    with pqax.open(str(INVALID / "json.invalid.pqax")) as doc:
        assert doc.row_count("tables") == 2
        for name in ("partitions", "files", "rowGroups", "columnChunks"):
            assert row_total(doc.table(name)) == EXPECTED["sample"]["rows"][name], name
        with pytest.raises(pqax.PqaxError) as raised:
            doc.table("commits")
        assert raised.value.code == "json.invalid"


def test_a_reference_to_a_missing_row_does_not_stop_the_reader():
    with pqax.open(str(INVALID / "table.reference.single.pqax")) as doc:
        files = doc.table("files")
        assert len(files["id"]) == 5
        assert files["writer"][0] in (99, None)
        assert files["writer"][1:] == [1, 2, 3, 3]


def test_the_rows_of_a_part_come_from_its_longest_array():
    with pqax.open(str(INVALID / "part.rowCount.manifest.pqax")) as doc:
        assert doc.row_count("commits") == 7
        assert row_total(doc.table("commits")) == 6
    with pqax.open(str(INVALID / "part.rowCount.array.pqax")) as doc:
        commits = doc.table("commits")
        assert row_total(commits) == 6
        assert commits["engine"][5] is None
        assert commits["commitId"][0] == "9"


def test_a_detail_part_that_is_missing_fails_only_its_table():
    with pqax.open(str(INVALID / "manifest.partMissing.pqax")) as doc:
        assert row_total(doc.table("files")) == 5
        with pytest.raises(pqax.PqaxError) as raised:
            doc.table("commits")
        assert raised.value.code == "manifest.partMissing"


def test_a_summary_part_that_is_missing_reads_as_empty_summary_tables(tmp_path):
    target = tmp_path / "no-summary.pqax"
    drop_entry(SAMPLES / "sample.pqax", target, "summary.json")
    with pqax.open(str(target)) as doc:
        for name in SUMMARY_TABLES:
            assert row_total(doc.summary.get(name, {})) == 0, name
        assert row_total(doc.table("files")) == 5
        assert row_total(doc.table("columnChunks")) == 52
