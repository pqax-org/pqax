"""The writer of pqax, Builder: round trips of the kit samples, a footprint built from nothing, and parts of 250,000."""
from __future__ import annotations

import io
import json
import re
import zipfile
from datetime import datetime, timedelta, timezone

import pytest

import pqax
from pqax_kit import (DETAIL_TABLES, SAMPLES, SHAPE_KEYS, SUMMARY_TABLES, compact, compute_shape, expected_by_name,
                      schema_errors, schema_validators, shape_differences, stated_rows)

EXPECTED = expected_by_name()
UTC = timezone.utc
PART_NAME = re.compile(r"^[A-Za-z0-9._-]+$")
MANIFEST_MEMBERS = {"format", "formatVersion", "collection", "parts"}


def open_sample(expected):
    path = SAMPLES / expected["file"]
    if expected["embeddedPart"] is not None:
        return pqax.open_embedded(str(path), expected["embeddedPart"])
    return pqax.open(str(path))


def read_table(doc, name):
    if name in SUMMARY_TABLES:
        return doc.summary.get(name, {})
    return doc.table(name)


def assert_schema_valid(path):
    validators = schema_validators()
    if validators is None:
        return
    assert schema_errors(path.read_bytes(), validators) == []


def assert_valid(path):
    result = pqax.validate(str(path))
    assert [f"{issue.code}: {issue.message}" for issue in result.issues] == []
    assert result.is_valid


def timestamp(value):
    """The text of a timestamp of the format, computed here and not by the package."""
    value = value.astimezone(UTC)
    return value.strftime("%Y-%m-%dT%H:%M:%S.") + f"{value.microsecond // 1000:03d}Z"


# ---- round trips of the samples ----

@pytest.mark.parametrize("name", sorted(EXPECTED))
def test_round_trip_through_from_document(name, tmp_path):
    expected = EXPECTED[name]
    target = tmp_path / "copy.pqax"
    with open_sample(expected) as original:
        pqax.Builder.from_document(original).write(str(target))
        with pqax.open(str(target)) as copy:
            assert copy.manifest["format"] == "pqax"
            assert copy.manifest["formatVersion"] == "0.1"
            assert copy.manifest["parts"][0]["name"] == "summary.json"
            for member, value in original.manifest.items():
                if member not in ("parts", "formatVersion"):
                    assert copy.manifest.get(member) == value, member
            assert stated_rows(copy.manifest) == stated_rows(original.manifest)
            for table in stated_rows(original.manifest):
                assert copy.table(table) == original.table(table), table
            shape = compute_shape(copy.manifest, copy.row_count, lambda table: read_table(copy, table))
    assert not shape_differences(shape, expected, [key for key in SHAPE_KEYS if key not in ("parts", "formatVersion")])
    assert_valid(target)
    assert_schema_valid(target)


def test_round_trip_into_smaller_parts(tmp_path):
    target = tmp_path / "parts.pqax"
    with pqax.open(str(SAMPLES / "sample.pqax")) as original:
        builder = pqax.Builder.from_document(original)
        builder.rows_per_part = 20
        builder.write(str(target))
        with pqax.open(str(target)) as copy:
            parts = copy.manifest["parts"]
            chunk_parts = [part["tables"]["columnChunks"] for part in parts if "columnChunks" in part["tables"]]
            assert chunk_parts == [20, 20, 12]
            for part in parts[1:]:
                assert len(part["tables"]) == 1
                assert sum(part["tables"].values()) <= 20
            for table in stated_rows(original.manifest):
                assert copy.table(table) == original.table(table), table
    assert_valid(target)
    assert_schema_valid(target)


def test_the_same_footprint_gives_the_same_bytes(tmp_path):
    with pqax.open(str(SAMPLES / "sample.pqax")) as original:
        first, second = io.BytesIO(), io.BytesIO()
        builder = pqax.Builder.from_document(original)
        builder.write(first)
        builder.write(second)
        pqax.Builder.from_document(original).write(str(tmp_path / "third.pqax"))
    assert first.getvalue() == second.getvalue() == (tmp_path / "third.pqax").read_bytes()


def test_the_written_json_is_compact_utf8_with_no_byte_order_mark(tmp_path):
    target = tmp_path / "copy.pqax"
    with pqax.open(str(SAMPLES / "extensions.pqax")) as original:
        pqax.Builder.from_document(original).write(str(target))
    with zipfile.ZipFile(target) as archive:
        names = archive.namelist()
        assert "manifest.json" in names and "summary.json" in names
        assert len(names) == len({name.lower() for name in names})
        for info in archive.infolist():
            assert info.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED), info.filename
            assert not info.flag_bits & 1, info.filename
            assert PART_NAME.match(info.filename), info.filename
            data = archive.read(info)
            assert not data.startswith(b"\xef\xbb\xbf"), info.filename
            content = json.loads(data.decode("utf-8"))
            assert isinstance(content, dict), info.filename
            assert data == compact(content), f"{info.filename} has whitespace between tokens"


# ---- a footprint built from nothing ----

def build_everything():
    """A small footprint with rows in all 21 tables, totals that agree with the detail, and every kind of value.

    Returns the builder and, for each table, the rows that were added, with their ids.
    """
    started = datetime(2026, 10, 7, 8, 15, 30, 123456, tzinfo=UTC)
    builder = pqax.Builder({"collectorApp": "pqax-tests", "collectorAppVersion": "0.1", "level": 2,
                            "startedAt": started, "completedAt": started + timedelta(seconds=30),
                            "includesDataValues": True, "includesPartitionValues": True,
                            "requestCount": 12, "bytesRead": 34567})
    rows = {name: [] for name in SUMMARY_TABLES + DETAIL_TABLES}
    ids = {}
    ids["source"] = builder.add_source(kind="Storage folder", name="folder one", location="abfss://example/one",
                                       identifiers={"workspaceId": "w-1", "itemId": "i-1"})
    rows["sources"].append({"id": ids["source"], "kind": "Storage folder", "name": "folder one",
                            "location": "abfss://example/one", "identifiers": {"workspaceId": "w-1", "itemId": "i-1"}})

    chunk_sizes = {(group, physical): 100 * group + 10 * physical for group in (1, 2, 3) for physical in (1, 2)}
    total_chunks = sum(chunk_sizes.values())
    distribution = {"count": 2, "sum": 3000, "minimum": 1000, "firstQuartile": 1000, "median": 2000,
                    "thirdQuartile": 2000, "maximum": 2000}
    table_one = dict(source=ids["source"], namespace=["dbo"], name="trips", location="Tables/trips", format="Iceberg",
                     tableId="t-1", description="the trips", createdAt=datetime(2025, 1, 2, 3, 4, 5, tzinfo=UTC),
                     properties={"write.format.default": "parquet"}, snapshotId="42",
                     snapshotTimestamp=datetime(2026, 1, 2, 3, 4, 5, 6000, tzinfo=UTC),
                     iceberg={"formatVersion": 2, "currentSchemaId": 0, "schemaCount": 1, "snapshotCount": 3,
                              "refCount": 1, "manifestCount": 2, "manifestSize": 9000, "statisticsFileCount": 0},
                     collectedLevel=2, detailDepth="Column chunks", fileCount=2, fileSize=3000, rowCount=150,
                     deletedRowCount=5, lastModifiedAt=datetime(2026, 1, 2, 3, 0, 0, tzinfo=UTC),
                     vOrderedFileCount=0, partitionCount=1, deleteFileCount=1, deleteFileSize=100, commitCount=2,
                     commitsRead=2, oldestCommitAt=datetime(2026, 1, 1, 0, 0, 0, tzinfo=UTC), nonLiveFileCount=0,
                     nonLiveFileSize=0, metadataFileCount=4, metadataSize=4096, footerCount=2, rowGroupCount=3,
                     compressedSize=total_chunks, uncompressedSize=2 * total_chunks, footerSize=500,
                     staleColumnSize=0, columnTotalsSource="Footers", fileSizes=distribution,
                     rowGroupRowCounts={"count": 3, "sum": 150, "minimum": 40, "firstQuartile": 40, "median": 50,
                                        "thirdQuartile": 60, "maximum": 60},
                     partitionSizes=distribution)
    ids["table1"] = builder.add_table(**table_one)
    rows["tables"].append({"id": ids["table1"], **table_one})
    table_two = dict(source=ids["source"], namespace=[], name="keys", format="Delta",
                     delta={"minReaderVersion": 1, "minWriterVersion": 2, "readerFeatures": [], "writerFeatures": [],
                            "checkpointVersion": 3, "commitsAfterCheckpoint": 1, "domains": []},
                     properties={}, collectedLevel=1, detailDepth="Summary", fileCount=10, fileSize=99999,
                     rowCount=1000, clusteringColumns=[3])
    ids["table2"] = builder.add_table(**table_two)
    rows["tables"].append({"id": ids["table2"], **table_two})

    columns = [dict(table=ids["table1"], fieldId=1, name="id", dataType="long", isNullable=False,
                    isPartitionColumn=False, compressedSize=sum(chunk_sizes[(g, 1)] for g in (1, 2, 3)),
                    uncompressedSize=2 * sum(chunk_sizes[(g, 1)] for g in (1, 2, 3)), valueCount=150, nullCount=0,
                    chunkCount=3, codecs=["ZSTD"], encodings=["PLAIN"]),
               dict(table=ids["table1"], fieldId=2, name="amount", dataType="decimal(18,2)", isNullable=True,
                    compressedSize=sum(chunk_sizes[(g, 2)] for g in (1, 2, 3)), chunkCount=3,
                    dictionaryChunkCount=3, fallbackChunkCount=0, dictionaryPageSize=30, largestChunkDistinctCount=9,
                    distinctCount=12, distinctCountIsEstimate=True, distinctCountSource="Scan"),
               dict(table=ids["table2"], name="key", dataType="string", physicalName="col-1",
                    description="the key")]
    for column in columns:
        identifier = builder.add_column(**column)
        rows["columns"].append({"id": identifier, **column})
    assert [row["id"] for row in rows["columns"]] == [1, 2, 3]

    spec = builder.add_partition_spec(table=ids["table1"], specId=0, isDefault=True)
    rows["partitionSpecs"].append({"id": spec, "table": ids["table1"], "specId": 0, "isDefault": True})
    assert builder.add_partition_field(partitionSpec=spec, position=0, name="id_bucket", sourceColumns=[1],
                                       transform="bucket[4]") == 0
    rows["partitionFields"].append({"partitionSpec": spec, "position": 0, "name": "id_bucket", "sourceColumns": [1],
                                    "transform": "bucket[4]"})
    order = builder.add_sort_order(table=ids["table1"], orderId=1, isDefault=True)
    rows["sortOrders"].append({"id": order, "table": ids["table1"], "orderId": 1, "isDefault": True})
    assert builder.add_sort_field(sortOrder=order, position=0, sourceColumn=2, transform="identity", direction="asc",
                                  nullOrder="nulls-first") == 0
    rows["sortFields"].append({"sortOrder": order, "position": 0, "sourceColumn": 2, "transform": "identity",
                               "direction": "asc", "nullOrder": "nulls-first"})
    schema = builder.add_physical_schema(table=ids["table1"], fileCount=2)
    rows["physicalSchemas"].append({"id": schema, "table": ids["table1"], "fileCount": 2})
    for ordinal, (name, kind) in enumerate((("id", "INT64"), ("amount", "FIXED_LEN_BYTE_ARRAY"))):
        physical = dict(physicalSchema=schema, ordinal=ordinal, path=[name], physicalType=kind,
                        repetition="OPTIONAL", column=ordinal + 1,
                        compressedSize=sum(chunk_sizes[(g, ordinal + 1)] for g in (1, 2, 3)), chunkCount=3)
        if kind == "FIXED_LEN_BYTE_ARRAY":
            physical.update(typeLength=9, logicalType="DECIMAL(18,2)", fieldId=2)
        identifier = builder.add_physical_column(**physical)
        rows["physicalColumns"].append({"id": identifier, **physical})
    writer = builder.add_writer(table=ids["table1"], createdBy="writer-x 1.0", isVOrdered=False, fileCount=2,
                                fileSize=3000)
    rows["writers"].append({"id": writer, "table": ids["table1"], "createdBy": "writer-x 1.0", "isVOrdered": False,
                            "fileCount": 2, "fileSize": 3000})
    assert builder.add_operation(table=ids["table1"], name="append", commitCount=2,
                                 lastAt=datetime(2026, 1, 2, 3, 4, 5, tzinfo=UTC)) == 0
    rows["operations"].append({"table": ids["table1"], "name": "append", "commitCount": 2,
                               "lastAt": datetime(2026, 1, 2, 3, 4, 5, tzinfo=UTC)})
    assert builder.add_issue(table=ids["table1"], path="data/f2.parquet", code="FooterNotRead",
                             message="an example") == 0
    assert builder.add_issue(code="AccessDenied", message="no single table") == 1
    rows["issues"] += [{"table": ids["table1"], "path": "data/f2.parquet", "code": "FooterNotRead",
                        "message": "an example"}, {"code": "AccessDenied", "message": "no single table"}]
    model = builder.add_model_binding(id=7, modelName="Model", serverName="server")
    assert model == 7
    rows["modelBindings"].append({"id": 7, "modelName": "Model", "serverName": "server"})
    binding = builder.add_table_binding(modelBinding=model, modelTable="Trips", table=ids["table1"])
    rows["tableBindings"].append({"id": binding, "modelBinding": model, "modelTable": "Trips", "table": ids["table1"]})
    for position, (name, column) in enumerate((("Id", 1), ("Amount", 2))):
        assert builder.add_column_binding(tableBinding=binding, modelColumn=name, column=column) == position
        rows["columnBindings"].append({"tableBinding": binding, "modelColumn": name, "column": column})

    partition = builder.add_partition(table=ids["table1"], partitionSpec=spec, values=["3"], fileCount=2,
                                      fileSize=3000, rowCount=150, deletedRowCount=5)
    rows["partitions"].append({"id": partition, "table": ids["table1"], "partitionSpec": spec, "values": ["3"],
                               "fileCount": 2, "fileSize": 3000, "rowCount": 150, "deletedRowCount": 5})
    group_sizes = {group: sum(chunk_sizes[(group, physical)] for physical in (1, 2)) for group in (1, 2, 3)}
    files = [dict(table=ids["table1"], path="data/f1.parquet", format="Parquet", fileSize=2000,
                  modifiedAt=datetime(2026, 1, 2, 3, 0, 0, 999000, tzinfo=UTC), rowCount=100, deletedRowCount=5,
                  deletionVectorSize=40, partition=partition, sortOrder=order, clusteringProvider="liquid",
                  isVOrdered=False, footerSize=300, physicalSchema=schema, writer=writer, rowGroupCount=2,
                  compressedSize=group_sizes[1] + group_sizes[2], uncompressedSize=2 * (group_sizes[1] + group_sizes[2])),
             dict(table=ids["table1"], path="data/f2.parquet", format="Parquet", fileSize=1000,
                  modifiedAt=datetime(2026, 1, 2, 4, 0, 0, tzinfo=timezone(timedelta(hours=2))), rowCount=50,
                  deletedRowCount=0, partition=partition, sortOrder=order, isVOrdered=False, footerSize=200,
                  physicalSchema=schema, writer=writer, rowGroupCount=1, compressedSize=group_sizes[3],
                  uncompressedSize=2 * group_sizes[3])]
    for file in files:
        identifier = builder.add_file(**file)
        rows["files"].append({"id": identifier, **file})
    for group, (file, ordinal, count) in enumerate(((1, 0, 60), (1, 1, 40), (2, 0, 50)), 1):
        values = dict(file=file, ordinal=ordinal, rowCount=count, uncompressedSize=2 * group_sizes[group],
                      compressedSize=group_sizes[group])
        assert builder.add_row_group(**values) == group
        rows["rowGroups"].append({"id": group, **values})
    for group in (1, 2, 3):
        for physical in (1, 2):
            size = chunk_sizes[(group, physical)]
            chunk = dict(rowGroup=group, physicalColumn=physical, codec="ZSTD",
                         encodings=["PLAIN"] if physical == 1 else ["RLE_DICTIONARY", "PLAIN"], valueCount=50,
                         nullCount=0, compressedSize=size, uncompressedSize=2 * size,
                         hasDictionaryPage=physical == 2, dictionaryPageSize=10 if physical == 2 else None,
                         dataPageCount=1, dictionaryEncodedPageCount=1 if physical == 2 else 0,
                         bloomFilterSize=None, minValue="1", maxValue="99",
                         distinctCount=3 if physical == 2 else None, dictionaryEntryCount=None)
            builder.add_column_chunk(**chunk)
            rows["columnChunks"].append(chunk)
    delete = dict(table=ids["table1"], kind="Position deletes", path="data/d1.parquet", format="Parquet",
                  fileSize=100, recordCount=5, referencedFile=1, equalityColumns=[1], partition=partition)
    identifier = builder.add_delete_file(**delete)
    rows["deleteFiles"].append({"id": identifier, **delete})
    for commit in (dict(table=ids["table1"], commitId="41", parentId="40",
                        timestamp=datetime(2026, 1, 1, 0, 0, 0, tzinfo=UTC), operation="append", engine="engine 1",
                        isBlindAppend=True, metrics={"added-files": "1"}, parameters={"mode": "append"}),
                   dict(table=ids["table1"], commitId="42", parentId="41",
                        timestamp=datetime(2026, 1, 2, 3, 4, 5, tzinfo=UTC), operation="append", metrics={})):
        builder.add_commit(**commit)
        rows["commits"].append(commit)
    return builder, rows


def columnar(rows):
    """The table that a reader must return for rows: a list for each field that has a value in some row."""
    names = []
    for row in rows:
        names += [name for name, value in row.items() if value is not None and name not in names]
    result = {}
    for name in names:
        values = [row.get(name) for row in rows]
        result[name] = [timestamp(value) if isinstance(value, datetime) else value for value in values]
    return result


def test_a_footprint_with_every_table_is_written_read_and_valid(tmp_path):
    builder, rows = build_everything()
    target = tmp_path / "everything.pqax"
    builder.write(str(target))
    with pqax.open(str(target)) as doc:
        collection = doc.manifest["collection"]
        assert collection["startedAt"] == "2026-10-07T08:15:30.123Z"
        assert collection["completedAt"] == "2026-10-07T08:16:00.123Z"
        assert collection["level"] == 2
        for name, added in rows.items():
            assert doc.row_count(name) == len(added), name
            assert read_table(doc, name) == columnar(added), name
        assert doc.table("files")["modifiedAt"] == ["2026-01-02T03:00:00.999Z", "2026-01-02T02:00:00.000Z"]
        assert doc.table("commits")["metrics"][1] == {}
        assert doc.summary["tables"]["properties"][1] == {}
        assert doc.summary["tables"]["delta"][1]["readerFeatures"] == []
        assert [part["name"] for part in doc.manifest["parts"]][0] == "summary.json"
        for part in doc.manifest["parts"][1:]:
            assert len(part["tables"]) == 1
    assert_valid(target)
    assert_schema_valid(target)


def test_ids_and_positions_returned_by_the_add_methods():
    builder = pqax.Builder({"level": 1})
    assert builder.add_source(kind="Storage folder") == 1
    assert builder.add_source(kind="Storage folder") == 2
    assert builder.add_source(id=10, kind="Storage folder") == 10
    assert builder.add_source(kind="Storage folder") == 11
    assert builder.add_table(source=1, name="a", format="Parquet", detailDepth="Summary") == 1
    assert builder.add_operation(table=1, name="WRITE") == 0
    assert builder.add_operation(table=1, name="MERGE") == 1


def test_from_document_keeps_the_ids_and_the_references(tmp_path):
    builder = pqax.Builder({"level": 1})
    builder.add_source(id=10, kind="Storage folder")
    builder.add_source(id=20, kind="Storage folder")
    builder.add_table(id=5, source=20, name="a", format="Parquet", detailDepth="Files", fileCount=1, fileSize=7)
    builder.add_file(id=30, table=5, fileSize=7)
    first, second = tmp_path / "first.pqax", tmp_path / "second.pqax"
    builder.write(str(first))
    with pqax.open(str(first)) as doc:
        pqax.Builder.from_document(doc).write(str(second))
    with pqax.open(str(second)) as doc:
        assert doc.summary["sources"]["id"] == [10, 20]
        assert doc.summary["tables"]["source"] == [20]
        assert doc.table("files") == {"id": [30], "table": [5], "fileSize": [7]}
    assert_valid(second)


def test_an_empty_footprint_has_a_summary_part(tmp_path):
    target = tmp_path / "empty.pqax"
    pqax.Builder({"level": 1}).write(str(target))
    with zipfile.ZipFile(target) as archive:
        assert json.loads(archive.read("summary.json")) == {}
        manifest = json.loads(archive.read("manifest.json"))
    assert [part["name"] for part in manifest["parts"]] == ["summary.json"]
    assert manifest["parts"][0]["tables"] == {}
    with pqax.open(str(target)) as doc:
        assert doc.row_count("tables") == 0
        assert doc.table("files") == {}
    assert_valid(target)


def test_a_field_or_a_table_without_values_is_left_out(tmp_path):
    builder = pqax.Builder({"level": 1}, rows_per_part=2)
    builder.add_source(kind="Storage folder")
    builder.add_table(source=1, name="a", format="Parquet", detailDepth="Files", fileCount=3, fileSize=6)
    builder.add_file(table=1, fileSize=1, path=None, deletionVectorSize=8)
    builder.add_file(table=1, fileSize=2)
    builder.add_file(table=1, fileSize=3)
    target = tmp_path / "sparse.pqax"
    builder.write(str(target))
    with zipfile.ZipFile(target) as archive:
        manifest = json.loads(archive.read("manifest.json"))
        parts = {part["name"]: json.loads(archive.read(part["name"])) for part in manifest["parts"]}
    summary = parts.pop("summary.json")
    assert set(summary) == {"sources", "tables"}
    assert len(parts) == 2
    pieces = [content["files"] for content in parts.values()]
    assert [len(piece["id"]) for piece in pieces] == [2, 1]
    assert all("path" not in piece for piece in pieces)
    assert "deletionVectorSize" in pieces[0] and "deletionVectorSize" not in pieces[1]
    with pqax.open(str(target)) as doc:
        assert doc.table("files")["deletionVectorSize"] == [8, None, None]
        assert "path" not in doc.table("files")
    assert_valid(target)


def test_a_producer_field_and_a_producer_table_are_written(tmp_path):
    builder = pqax.Builder({"level": 1})
    builder.manifest_extensions["acme_build"] = {"number": 1}
    builder.add_source(kind="Storage folder", acme_note="first")
    builder.add_table(source=1, name="a", format="Parquet", detailDepth="Summary")
    builder.add("acme_reviews", table=1, note="checked")
    builder.extend("acme_events", {"table": [1, 1], "kind": ["x", "y"]}, detail=True)
    target = tmp_path / "producer.pqax"
    builder.write(str(target))
    with pqax.open(str(target)) as doc:
        assert doc.manifest["acme_build"] == {"number": 1}
        assert doc.summary["sources"]["acme_note"] == ["first"]
        assert doc.summary["acme_reviews"] == {"table": [1], "note": ["checked"]}
        assert doc.row_count("acme_events") == 2
        assert "acme_events" not in doc.summary
        assert doc.table("acme_events") == {"table": [1, 1], "kind": ["x", "y"]}
    assert_valid(target)


def test_a_field_that_the_table_does_not_have_is_refused():
    builder = pqax.Builder({"level": 1})
    with pytest.raises((TypeError, ValueError)):
        builder.add_source(kind="Storage folder", colour="red")


def test_update_sets_the_fields_of_a_row(tmp_path):
    builder = pqax.Builder({"level": 1})
    builder.add_source(kind="Storage folder")
    table = builder.add_table(source=1, name="a", format="Parquet", detailDepth="Files")
    builder.add_file(table=table, fileSize=5)
    builder.update("tables", table, fileCount=1, fileSize=5)
    target = tmp_path / "updated.pqax"
    builder.write(str(target))
    with pqax.open(str(target)) as doc:
        assert doc.summary["tables"]["fileCount"] == [1]
        assert doc.summary["tables"]["fileSize"] == [5]
    assert_valid(target)


def test_parts_of_more_than_250000_rows_are_refused():
    builder = pqax.Builder({"level": 1})
    builder.add_source(kind="Storage folder")
    with pytest.raises(ValueError):
        builder.rows_per_part = 250_001
        builder.write(io.BytesIO())


# ---- the large test ----

@pytest.mark.large
def test_600000_column_chunks_are_split_in_parts_of_250000_and_read_back_whole(tmp_path):
    file_count, groups_per_file, physical_count = 100, 100, 60
    group_count = file_count * groups_per_file
    chunk_count = group_count * physical_count
    assert chunk_count == 600_000
    builder = pqax.Builder({"collectorApp": "pqax-tests", "level": 2})
    builder.add_source(kind="Storage folder")
    table = builder.add_table(source=1, name="large", format="Parquet", detailDepth="Column chunks")
    builder.add_physical_schema(table=table, fileCount=file_count)
    column_ids = list(range(1, physical_count + 1))
    builder.extend("columns", {"id": column_ids, "table": [table] * physical_count,
                               "name": [f"c{index}" for index in column_ids], "dataType": ["long"] * physical_count})
    builder.extend("physicalColumns", {"id": column_ids, "physicalSchema": [1] * physical_count,
                                       "ordinal": [index - 1 for index in column_ids],
                                       "path": [[f"c{index}"] for index in column_ids],
                                       "physicalType": ["INT64"] * physical_count, "column": column_ids})
    file_sizes = [1000 + index for index in range(file_count)]
    builder.extend("files", {"id": list(range(1, file_count + 1)), "table": [table] * file_count,
                             "fileSize": file_sizes})
    builder.extend("rowGroups", {"id": list(range(1, group_count + 1)),
                                 "file": [1 + index // groups_per_file for index in range(group_count)],
                                 "ordinal": [index % groups_per_file for index in range(group_count)],
                                 "rowCount": [1000] * group_count})
    sizes = [1 + (index * 7919) % 1000 for index in range(chunk_count)]
    groups = [1 + index // physical_count for index in range(chunk_count)]
    physicals = [1 + index % physical_count for index in range(chunk_count)]
    builder.extend("columnChunks", {"rowGroup": groups, "physicalColumn": physicals, "compressedSize": sizes,
                                    "uncompressedSize": [2 * size for size in sizes]})
    builder.update("tables", table, fileCount=file_count, fileSize=sum(file_sizes), rowGroupCount=group_count,
                   compressedSize=sum(sizes))
    target = tmp_path / "large.pqax"
    builder.write(str(target))
    with pqax.open(str(target)) as doc:
        parts = doc.manifest["parts"]
        assert [part["tables"]["columnChunks"] for part in parts if "columnChunks" in part["tables"]] == \
               [250_000, 250_000, 100_000]
        assert all(sum(part["tables"].values()) <= 250_000 for part in parts[1:])
        assert doc.row_count("columnChunks") == chunk_count
        chunks = doc.table("columnChunks")
        assert chunks["compressedSize"] == sizes
        assert chunks["rowGroup"] == groups
        assert chunks["physicalColumn"] == physicals
        assert all(len(values) == chunk_count for values in chunks.values())
    result = pqax.validate(str(target))
    assert result.is_valid, [f"{issue.code}: {issue.message}" for issue in result.issues[:5]]
