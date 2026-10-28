"""The parts of a rewrite: Builder.from_document and write keep the names of the parts of the source, their order,
their rows, and the members of their descriptors, and lay out the other tables as a builder does (the contract of the
hardening before 1.0, section 1)."""
from __future__ import annotations

import json
import re
import zipfile

import pytest

import pqax
from pqax_kit import INVALID, SAMPLES, compact, row_total, stated_rows

DESCRIPTOR = {"name", "tables"}  # the members of a part descriptor that the format defines
COLLECTION = {"collectorApp": "pqax-tests", "level": 1}


def assert_valid(path):
    result = pqax.validate(str(path))
    assert [f"{issue.code}: {issue.message}" for issue in result.issues] == []
    assert result.is_valid


def rewrite(source, target, *, rows_per_part=None, change=None):
    """Writes target with Builder.from_document of source. rows_per_part is set, and change(builder) runs, after
    from_document and before the write."""
    with pqax.open(str(source)) as original:
        builder = pqax.Builder.from_document(original)
        if rows_per_part is not None:
            builder.rows_per_part = rows_per_part
        if change is not None:
            change(builder)
        builder.write(str(target))


def parts_of(path):
    """The part descriptors as manifest.json holds them, read with zipfile and json only."""
    with zipfile.ZipFile(path) as archive:
        return json.loads(archive.read("manifest.json"))["parts"]


def names_of(parts):
    return [part["name"] for part in parts]


def by_name(parts):
    return {part["name"]: part for part in parts}


def assert_same_tables(source, target):
    """Every table of the source is in the target, row by row, as the reader returns them."""
    with pqax.open(str(source)) as original, pqax.open(str(target)) as copy:
        for name in sorted(set(stated_rows(original.manifest)) | set(stated_rows(copy.manifest))):
            assert copy.table(name) == original.table(name), name


def copy_zip(source, target, rename=None, change_manifest=None):
    """Copies a PQAX file entry by entry: rename maps an old name of an entry and of its part to a new one, and
    change_manifest(manifest) changes the manifest in place."""
    rename = rename or {}
    with zipfile.ZipFile(source) as original, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as copy:
        for info in original.infolist():
            data = original.read(info.filename)
            if info.filename == "manifest.json":
                manifest = json.loads(data)
                for part in manifest["parts"]:
                    part["name"] = rename.get(part["name"], part["name"])
                if change_manifest is not None:
                    change_manifest(manifest)
                data = compact(manifest)
            copy.writestr(rename.get(info.filename, info.filename), data)


def add_two_files(builder):
    """Adds a table at the Files depth with two files, whose totals agree, so that the file stays valid."""
    table = builder.add_table(source=1, name="added", format="Parquet", detailDepth="Files", fileCount=2, fileSize=30)
    assert table == 3
    assert builder.add_file(table=table, fileSize=10) == 6
    assert builder.add_file(table=table, fileSize=20) == 7


# ---- the descriptors of the kit ----

def test_extensions_has_members_of_a_producer_in_three_part_descriptors():
    with pqax.open(str(SAMPLES / "extensions.pqax")) as doc:
        parts = by_name(doc.manifest["parts"])
    assert parts["summary.json"]["acme_checksum"] == "c3a1"
    assert set(parts["summary.json"]) == DESCRIPTOR | {"acme_checksum"}
    assert parts["files.json"] == {"name": "files.json", "tables": {"files": 5}, "acme_checksum": "5f0c"}
    assert parts["acme_extra.json"] == {"name": "acme_extra.json", "tables": {"acme_details": 3}, "acme_version": 2}
    for name in ("partitions.json", "rowGroups.json", "columnChunks.json", "commits.json"):
        assert set(parts[name]) == DESCRIPTOR, name


# ---- a rewrite keeps the parts ----

@pytest.mark.parametrize("name", ["extensions.pqax", "multipart.pqax"])
def test_a_rewrite_has_exactly_the_parts_of_the_source_and_so_has_a_second_one(name, tmp_path):
    source, first, second = SAMPLES / name, tmp_path / "first.pqax", tmp_path / "second.pqax"
    rewrite(source, first)
    rewrite(first, second)
    expected = parts_of(source)
    for target in (first, second):
        # The names, in order, the rows stated for each table, and every member of each descriptor.
        assert parts_of(target) == expected, target.name
        with pqax.open(str(target)) as copy:
            assert copy.manifest["parts"] == expected
        assert_same_tables(source, target)
        assert_valid(target)


def test_multipart_keeps_its_parts_of_files_and_column_chunks(tmp_path):
    target = tmp_path / "multipart.pqax"
    rewrite(SAMPLES / "multipart.pqax", target)
    parts = parts_of(target)
    assert [(part["name"], part["tables"]) for part in parts[1:]] == [
        ("partitions.json", {"partitions": 2}), ("files.1.json", {"files": 3}), ("files.2.json", {"files": 2}),
        ("rowGroups.json", {"rowGroups": 13}), ("columnChunks.1.json", {"columnChunks": 20}),
        ("columnChunks.2.json", {"columnChunks": 20}), ("columnChunks.3.json", {"columnChunks": 12}),
        ("commits.json", {"commits": 6})]
    with pqax.open(str(target)) as doc:
        files = doc.table("files")
        assert files["deletionVectorSize"] == [40, None, None, None, None]
        assert files["partition"] == [1, 1, 2, None, None]


def test_rows_added_after_from_document_go_to_a_new_part_after_the_kept_one(tmp_path):
    source, target = SAMPLES / "sample.pqax", tmp_path / "added.pqax"
    rewrite(source, target, change=add_two_files)
    before, after = parts_of(source), parts_of(target)
    written = names_of(after)
    assert written[0] == "summary.json"
    # Every part of the source is kept, in the order of the source, and the added rows are in files.2.json.
    assert [name for name in written if name != "files.2.json"] == names_of(before)
    assert written.index("files.2.json") > written.index("files.json")
    parts = by_name(after)
    for part in before[1:]:
        assert parts[part["name"]] == part, part["name"]
    assert parts["files.2.json"] == {"name": "files.2.json", "tables": {"files": 2}}
    with pqax.open(str(source)) as original, pqax.open(str(target)) as copy:
        files, old = copy.table("files"), original.table("files")
        assert files["id"] == [1, 2, 3, 4, 5, 6, 7]
        for field, values in old.items():
            assert files[field][:5] == values, field
        assert files["table"][5:] == [3, 3]
        assert files["fileSize"][5:] == [10, 20]
    assert_valid(target)


def test_a_new_part_whose_name_is_taken_without_case_gets_a_suffix(tmp_path):
    source, target = tmp_path / "renamed.pqax", tmp_path / "added.pqax"
    copy_zip(SAMPLES / "sample.pqax", source, rename={"commits.json": "FILES.2.json"})
    assert_valid(source)
    rewrite(source, target, change=add_two_files)
    before, after = parts_of(source), parts_of(target)
    written = names_of(after)
    assert [name for name in written if name != "files.2_2.json"] == names_of(before)
    assert written.index("files.2_2.json") > written.index("files.json")
    parts = by_name(after)
    assert parts["FILES.2.json"] == {"name": "FILES.2.json", "tables": {"commits": 6}}
    assert parts["files.2_2.json"] == {"name": "files.2_2.json", "tables": {"files": 2}}
    assert len({name.lower() for name in written}) == len(written)
    assert_valid(target)


def test_a_part_listed_twice_is_kept_once_with_its_first_descriptor(tmp_path):
    def list_commits_twice(manifest):
        commits = by_name(manifest["parts"])["commits.json"]
        commits["acme_note"] = "first"
        manifest["parts"].append({"name": "commits.json", "tables": {"commits": 6}, "acme_note": "second"})

    source, target = tmp_path / "twice.pqax", tmp_path / "copy.pqax"
    copy_zip(SAMPLES / "sample.pqax", source, change_manifest=list_commits_twice)
    rewrite(source, target)
    parts = parts_of(target)
    assert names_of(parts) == names_of(parts_of(SAMPLES / "sample.pqax"))
    assert by_name(parts)["commits.json"] == {"name": "commits.json", "tables": {"commits": 6}, "acme_note": "first"}
    assert_same_tables(source, target)
    assert_valid(target)


# ---- the tables that keep no part ----

def test_a_smaller_part_size_keeps_every_part_that_fits_and_lays_out_the_others_after_them(tmp_path):
    source, target = SAMPLES / "sample.pqax", tmp_path / "parts.pqax"
    rewrite(source, target, rows_per_part=20)
    parts = parts_of(target)
    assert parts[0]["name"] == "summary.json"
    assert [(part["name"], part["tables"]) for part in parts[1:]] == [
        ("partitions.json", {"partitions": 2}), ("files.json", {"files": 5}), ("rowGroups.json", {"rowGroups": 13}),
        ("commits.json", {"commits": 6}), ("columnChunks.1.json", {"columnChunks": 20}),
        ("columnChunks.2.json", {"columnChunks": 20}), ("columnChunks.3.json", {"columnChunks": 12})]
    assert all(set(part) == DESCRIPTOR for part in parts)
    assert stated_rows({"parts": parts}) == stated_rows({"parts": parts_of(source)})
    assert_same_tables(source, target)
    assert_valid(target)


def test_a_table_that_keeps_no_part_drops_the_members_of_its_descriptors(tmp_path):
    source, target = SAMPLES / "extensions.pqax", tmp_path / "small.pqax"
    with pqax.open(str(source)) as original:
        pqax.Builder.from_document(original, rows_per_part=4).write(str(target))
    before, after = by_name(parts_of(source)), parts_of(target)
    # summary.json keeps its members, whatever the size of the parts.
    assert after[0] == before["summary.json"]
    # The two parts that hold at most 4 rows are kept, in the order of the source, right after summary.json.
    assert after[1:3] == [before["partitions.json"], before["acme_extra.json"]]
    # The other tables are laid out by the writer, with no member in their descriptors.
    written = by_name(after)
    assert "files.json" not in written
    assert written["files.1.json"] == {"name": "files.1.json", "tables": {"files": 4}}
    assert written["files.2.json"] == {"name": "files.2.json", "tables": {"files": 1}}
    for part in after[3:]:
        assert set(part) == DESCRIPTOR, part["name"]
        (table, rows), = part["tables"].items()
        assert rows <= 4, part["name"]
        assert re.fullmatch(re.escape(table) + r"\.[0-9]+\.json", part["name"]), part["name"]
    for table in ("files", "rowGroups", "columnChunks", "commits"):
        listing = [part["name"] for part in after if table in part["tables"]]
        assert listing == [f"{table}.{number}.json" for number in range(1, len(listing) + 1)], table
    assert stated_rows({"parts": after}) == stated_rows({"parts": list(before.values())})
    assert_same_tables(source, target)
    assert_valid(target)


# For each invalid sample: the parts that a rewrite keeps, in order after summary.json, and the parts that the writer
# lays out after them, in any order. Every table of each sample is read whole, so the rewrite is valid.
LAYOUTS = {
    # The rows stated for commits, 7, are not the rows of the table, 6.
    "part.rowCount.manifest.pqax": (["partitions.json", "files.json", "rowGroups.json", "columnChunks.json"],
                                    {"commits.json"}),
    # files.json lists two tables.
    "part.singleTable.pqax": (["partitions.json", "columnChunks.json", "commits.json"], {"files.json", "rowGroups.json"}),
    # commits is in summary.json, which a detail table never keeps.
    "part.kind.detail-in-summary.pqax": (["partitions.json", "files.json", "rowGroups.json", "columnChunks.json"],
                                         {"commits.json"}),
    # operations is a summary table, which goes to summary.json.
    "part.kind.summary-in-detail.pqax": (["partitions.json", "files.json", "rowGroups.json", "columnChunks.json",
                                          "commits.json"], set()),
    # The part of commits is named "commits 1.json", which is not a valid name.
    "container.partName.pqax": (["partitions.json", "files.json", "rowGroups.json", "columnChunks.json"],
                                {"commits.json"}),
    # files.json is listed before summary.json, which a rewrite writes first.
    "manifest.firstPart.pqax": (["files.json", "partitions.json", "rowGroups.json", "columnChunks.json", "commits.json"],
                                set()),
}


@pytest.mark.parametrize("name", sorted(LAYOUTS))
def test_a_part_that_cannot_be_kept_is_laid_out_by_the_writer(name, tmp_path):
    source, target = INVALID / name, tmp_path / "rewritten.pqax"
    kept, laid_out = LAYOUTS[name]
    rewrite(source, target)
    parts = parts_of(target)
    written = names_of(parts)
    assert written[0] == "summary.json"
    assert written[1:1 + len(kept)] == kept
    assert set(written[1 + len(kept):]) == laid_out
    assert len(written) == 1 + len(kept) + len(laid_out)
    with pqax.open(str(source)) as original:
        for part in parts[1:]:
            (table, rows), = part["tables"].items()
            assert table not in pqax.SUMMARY_TABLES, part["name"]
            if part["name"] in laid_out:
                assert part["name"] == f"{table}.json"
        # The rows that the rewrite states are the rows of each table, whatever the source stated.
        for table, rows in stated_rows({"parts": parts}).items():
            assert rows == row_total(original.table(table)), table
    assert_same_tables(source, target)
    assert_valid(target)


def test_the_summary_tables_of_a_detail_part_go_to_summary_json(tmp_path):
    target = tmp_path / "rewritten.pqax"
    rewrite(INVALID / "part.kind.summary-in-detail.pqax", target)
    parts = parts_of(target)
    assert parts[0]["tables"]["operations"] == 5
    assert "operations.json" not in names_of(parts)


# ---- a builder built from nothing ----

def test_a_builder_built_from_nothing_writes_no_member_in_the_descriptors(tmp_path):
    target = tmp_path / "built.pqax"
    builder = pqax.Builder(COLLECTION, rows_per_part=2)
    builder.add_source(kind="Storage folder")
    builder.add_table(source=1, name="a", format="Parquet", detailDepth="Files", fileCount=3, fileSize=6)
    for size in (1, 2, 3):
        builder.add_file(table=1, fileSize=size)
    builder.write(str(target))
    parts = parts_of(target)
    assert names_of(parts) == ["summary.json", "files.1.json", "files.2.json"]
    assert [set(part) for part in parts] == [DESCRIPTOR] * 3
    assert_valid(target)
    empty = tmp_path / "empty.pqax"
    pqax.Builder(COLLECTION).write(str(empty))
    assert parts_of(empty) == [{"name": "summary.json", "tables": {}}]
    assert_valid(empty)
