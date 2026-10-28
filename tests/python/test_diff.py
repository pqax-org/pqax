"""pqax.diff: the comparison of two footprints, section 13 of docs/object-model.md (Proposed on 24 October 2026).

The kit: conformance/samples/diff-before.pqax and diff-after.pqax, whose changes SCENARIO in conformance/make_diff.py
declares, against conformance/expected/diff/diff-before.diff-after.json, value for value with no tolerance. Each case of
SCENARIO is checked on its own. Then the rules, each on small footprints written here with pqax.Builder: the pairing of
the tables and the columns in three passes, the recreated tables, the files, partitions, and commits, the compared
values, what makes a table changed, and the order of rank."""
from __future__ import annotations

import io
import json
import zipfile

import pytest

import pqax
from pqax import diff
from pqax_kit import EXPECTED, SAMPLES, load_json

BEFORE, AFTER = SAMPLES / "diff-before.pqax", SAMPLES / "diff-after.pqax"
EXPECTED_DIFF = load_json(EXPECTED / "diff" / "diff-before.diff-after.json")
# The keys of the header of each footprint that compare gives; the command line adds the others.
RESULT_HEADER = ("tableCount", "filesRead", "warnings")
ORDERS = ("total", "waste", "rows", "files", "name")

COLLECTION = {"collectorApp": "pqax-tests", "collectorAppVersion": "0.0", "startedAt": "2026-10-10T23:58:00.000Z",
              "completedAt": "2026-10-11T00:00:00.000Z", "level": 2}
SOURCE = {"kind": "OneLake lakehouse", "name": "Lakehouse"}


# ---- exact comparison ----

def differences(actual, expected, path="$"):
    """Where two values differ: objects key by key, lists item by item, and every other value by its type and value,
    so that 3 and 3.0, or 1 and True, differ. Numbers compare exactly."""
    if isinstance(expected, dict):
        if not isinstance(actual, dict):
            return [f"{path}: got {actual!r}, expected an object"]
        problems = []
        for key in list(expected) + [key for key in actual if key not in expected]:
            if key not in actual or key not in expected:
                problems.append(f"{path}.{key}: present in only one of the two")
            else:
                problems += differences(actual[key], expected[key], f"{path}.{key}")
        return problems
    if isinstance(expected, list):
        if not isinstance(actual, list) or len(actual) != len(expected):
            return [f"{path}: got {json.dumps(actual)}, expected {json.dumps(expected)}"]
        return [problem for index, (a, e) in enumerate(zip(actual, expected))
                for problem in differences(a, e, f"{path}[{index}]")]
    if type(actual) is not type(expected) or actual != expected:
        return [f"{path}: got {actual!r}, expected {expected!r}"]
    return []


def assert_same(actual, expected):
    problems = differences(actual, expected)
    assert not problems, "\n".join(problems[:40])


# ---- the kit ----

@pytest.fixture(scope="module")
def kit():
    with pqax.open(BEFORE) as older, pqax.open(AFTER) as newer:
        return diff.compare(older, newer)


def kept(result, newer_name):
    matches = [entry for entry in result["kept"] if entry["newer"]["name"] == newer_name]
    assert len(matches) == 1, [entry["newer"]["name"] for entry in result["kept"]]
    return matches[0]


def entry(entries, name, table_id=None):
    matches = [item for item in entries if item["name"] == name and (table_id is None or item["tableId"] == table_id)]
    assert len(matches) == 1, [item["name"] for item in entries]
    return matches[0]


def counts(detail):
    """files or partitions as (added, removed, kept, changed, addedFileSize, removedFileSize), commits as (added,
    removed, kept), None when not compared."""
    if detail is None:
        return None
    keys = ("added", "removed", "kept", "changed", "addedFileSize", "removedFileSize")
    if list(detail) == ["added", "removed", "kept"]:
        keys = keys[:3]
    assert list(detail) == list(keys)
    return tuple(detail[key] for key in keys)


def column_pairs(table):
    return [(c["older"]["name"], c["newer"]["name"], c["matchedBy"]) for c in table["columns"]["kept"]]


def kit_uuid(n):
    return f"6d1f0c2a-0000-4000-8000-{n:012d}"


def test_orders():
    assert diff.ORDERS == ORDERS


def test_the_result_of_the_kit_is_the_expected_diff_less_the_header_of_the_command(kit):
    # The JSON of the command adds the header of each file, sort, and top; it lists the changes in the order of rank,
    # where compare lists them in the order of the files.
    expected = {key: value for key, value in EXPECTED_DIFF.items() if key not in ("sort", "top")}
    for side in ("older", "newer"):
        expected[side] = {key: EXPECTED_DIFF[side][key] for key in RESULT_HEADER}
    ranked = diff.rank(kit)
    actual = dict(kit)
    for kind in ("added", "removed", "kept"):
        actual[kind] = [item for k, item in ranked if k == kind]
    assert list(kit) == ["older", "newer", "smallFileSize", "tinyFileSize", "counts", "collection", "added", "removed",
                         "kept"]
    assert_same(actual, expected)


def test_the_keys_of_the_compared_values_are_in_the_order_of_the_interface(kit):
    expected = {item["newer"]["name"]: item for item in EXPECTED_DIFF["kept"]}
    assert list(kit["collection"]) == list(EXPECTED_DIFF["collection"])
    for item in kit["kept"]:
        model = expected[item["newer"]["name"]]
        assert list(item) == list(model)
        assert list(item["older"]) == list(model["older"])
        assert list(item["totals"]) == list(model["totals"])
        assert list(item["measures"]) == list(model["measures"])
        for column, column_model in zip(item["columns"]["kept"], model["columns"]["kept"]):
            assert list(column) == list(column_model)
            assert list(column["older"]) == list(column_model["older"])
            assert list(column["totals"]) == list(column_model["totals"])
            assert list(column["measures"]) == list(column_model["measures"])
    for kind in ("added", "removed"):
        for item in kit[kind]:
            assert list(item) == list(EXPECTED_DIFF[kind][0])


def test_compare_lists_the_changes_in_the_order_of_the_files(kit):
    # Added and kept in the order of the newer file, removed in the order of the older one.
    assert [item["name"] for item in kit["added"]] == ["events", "products"]
    assert [item["name"] for item in kit["removed"]] == ["products", "legacy"]
    assert [item["newer"]["name"] for item in kit["kept"]] == ["sales", "clients", "orders", "staging", "audit"]


def test_the_header_and_the_counts_of_the_kit(kit):
    assert kit["older"] == {"tableCount": 7, "filesRead": True, "warnings": []}
    assert kit["newer"] == {"tableCount": 7, "filesRead": True, "warnings": []}
    assert (kit["smallFileSize"], kit["tinyFileSize"]) == (134217728, 8388608)
    assert kit["counts"] == {"added": 2, "removed": 2, "kept": 5, "changed": 4}


# The cases of SCENARIO in conformance/make_diff.py, one by one.

def test_sales_is_kept_by_its_table_id_and_changed(kit):
    sales = kept(kit, "sales")
    assert (sales["status"], sales["matchedBy"], sales["renamed"]) == ("changed", "tableId", False)
    assert (sales["older"]["tableId"], sales["newer"]["tableId"]) == (kit_uuid(1), kit_uuid(1))
    assert (sales["older"]["snapshotId"], sales["newer"]["snapshotId"]) == ("4", "9")


def test_sales_amt_is_renamed_amount_with_the_same_field_id(kit):
    sales = kept(kit, "sales")
    assert column_pairs(sales) == [("id", "id", "fieldId"), ("amt", "amount", "fieldId"), ("year", "year", "fieldId")]
    amount = sales["columns"]["kept"][1]
    assert (amount["status"], amount["renamed"]) == ("changed", True)
    assert (amount["older"]["fieldId"], amount["newer"]["fieldId"]) == (2, 2)
    assert (amount["older"]["label"], amount["newer"]["label"]) == ("amt", "amount")
    # year is a partition column that the files do not store: no totals, nothing changed.
    year = sales["columns"]["kept"][2]
    assert (year["status"], year["renamed"]) == ("unchanged", False)
    assert year["totals"]["compressedSize"] == {"older": None, "newer": None, "delta": None}


def test_sales_discount_dropped_and_added_again_is_removed_and_added(kit):
    # discount has FieldId 3 before and 6 after: two columns that differ, so neither the FieldId nor the name pairs them.
    sales = kept(kit, "sales")
    added = [(c["name"], c["fieldId"], c["dataType"]) for c in sales["columns"]["added"]]
    removed = [(c["name"], c["fieldId"], c["dataType"]) for c in sales["columns"]["removed"]]
    assert added == [("discount", 6, "double"), ("region", 7, "string")]
    assert removed == [("discount", 3, "double"), ("note", 4, "string")]
    assert "discount" not in [pair[1] for pair in column_pairs(sales)]


def test_sales_files_partitions_and_commits(kit):
    sales = kept(kit, "sales")
    # Files: part-00005 and part-00006 added, part-00001 removed with its partition year=2024, part-00003 changed (its
    # deletion vector grew).
    assert counts(sales["files"]) == (2, 1, 3, 1, 40000, 10000)
    # Partitions: 2027 added, 2024 removed, 2025 and 2026 kept and changed.
    assert counts(sales["partitions"]) == (1, 1, 2, 2, 15000, 10000)
    # Commits: 5 to 9 added, 0 and 1 out of the kept window, 2 to 4 kept.
    assert counts(sales["commits"]) == (5, 2, 3)


def test_sales_timestamps_have_their_delta_in_milliseconds(kit):
    sales = kept(kit, "sales")
    assert sales["totals"]["lastModifiedAt"] == {"older": "2026-09-28T08:00:00.000Z",
                                                 "newer": "2026-10-06T08:00:00.000Z", "delta": 8 * 86_400_000}
    assert sales["totals"]["nonLiveNewestRemovedAt"]["delta"] == 4 * 86_400_000
    assert sales["measures"]["maintenance.lastCommitAt"]["delta"] == 8 * 86_400_000


def test_sales_totals_and_measures(kit):
    sales = kept(kit, "sales")
    assert sales["totals"]["fileCount"] == {"older": 4, "newer": 5, "delta": 1}
    assert sales["totals"]["fileSizes.count"] == {"older": 4, "newer": 5, "delta": 1}
    assert sales["totals"]["staleColumnSize"] == {"older": 0, "newer": 7000, "delta": 7000}
    assert sales["measures"]["stack.total"] == {"older": 124500, "newer": 170000, "delta": 45500}
    assert sales["measures"]["stack.waste"] == {"older": 9000, "newer": 28000, "delta": 19000}


def test_orders_shrunk_by_an_optimize_and_a_vacuum(kit):
    orders = kept(kit, "orders")
    assert (orders["status"], orders["matchedBy"], orders["renamed"]) == ("changed", "tableId", False)
    # OPTIMIZE: 4 files removed, 1 added; no partitions; commit 0 out of the window, 5 to 8 added.
    assert counts(orders["files"]) == (1, 4, 0, 0, 30000, 32000)
    assert counts(orders["partitions"]) == (0, 0, 0, 0, 0, 0)
    assert counts(orders["commits"]) == (4, 1, 4)
    # A boolean has no delta.
    assert orders["measures"]["maintenance.hasVacuum"] == {"older": False, "newer": True, "delta": None}
    assert orders["measures"]["maintenance.lastVacuumAt"] == {"older": None, "newer": "2026-10-06T07:05:00.000Z",
                                                              "delta": None}
    assert orders["measures"]["stack.total"] == {"older": 141000, "newer": 74500, "delta": -66500}


def test_orders_has_no_field_ids_and_price_changed_from_int_to_bigint(kit):
    orders = kept(kit, "orders")
    assert column_pairs(orders) == [("order_id", "order_id", "name"), ("qty", "qty", "name"),
                                    ("price", "price", "name"), ("customer", "customer", "name")]
    assert orders["columns"]["added"] == [] and orders["columns"]["removed"] == []
    price = orders["columns"]["kept"][2]
    assert (price["older"]["dataType"], price["newer"]["dataType"]) == ("int", "bigint")
    assert (price["status"], price["renamed"]) == ("changed", False)
    assert (price["older"]["fieldId"], price["newer"]["fieldId"]) == (None, None)


def test_customers_renamed_clients_with_the_same_table_id(kit):
    clients = kept(kit, "clients")
    assert (clients["status"], clients["matchedBy"], clients["renamed"]) == ("changed", "tableId", True)
    assert (clients["older"]["name"], clients["older"]["label"]) == ("customers", "customers")
    assert (clients["newer"]["name"], clients["newer"]["label"]) == ("clients", "clients")
    # The rename alone changes it: the same snapshot, the same totals, the same columns.
    assert clients["older"]["snapshotId"] == clients["newer"]["snapshotId"] == "7"
    assert all(value["older"] == value["newer"] for value in clients["totals"].values())
    assert all(column["status"] == "unchanged" for column in clients["columns"]["kept"])
    # Summary on both sides: the files are not compared.
    assert clients["files"] is None
    assert counts(clients["commits"]) == (0, 0, 2)


def test_products_dropped_and_created_again_is_removed_and_added_both_recreated(kit):
    removed = entry(kit["removed"], "products")
    added = entry(kit["added"], "products")
    assert (removed["tableId"], removed["recreated"]) == (kit_uuid(4), True)
    assert (added["tableId"], added["recreated"]) == (kit_uuid(5), True)
    assert "products" not in [item["newer"]["name"] for item in kit["kept"]]


def test_legacy_is_removed_and_events_added_neither_recreated(kit):
    legacy = entry(kit["removed"], "legacy")
    events = entry(kit["added"], "events")
    assert (legacy["recreated"], events["recreated"]) == (False, False)
    assert legacy == {"id": 5, "label": "legacy", "name": "legacy", "namespace": None, "tableId": kit_uuid(6),
                      "snapshotId": "20", "detailDepth": "Files", "columnCount": 2, "fileCount": 2, "fileSize": 136000,
                      "rowCount": 15000, "total": 181000, "waste": 0, "recreated": False}


def test_raw_staging_has_no_table_id_and_is_kept_by_its_name(kit):
    staging = kept(kit, "staging")
    assert (staging["status"], staging["matchedBy"], staging["renamed"]) == ("changed", "name", False)
    assert (staging["older"]["tableId"], staging["newer"]["tableId"]) == (None, None)
    assert staging["newer"]["namespace"] == ["raw"]
    assert staging["newer"]["label"] == "raw.staging"
    assert counts(staging["files"]) == (1, 0, 2, 0, 5400, 0)
    assert [c["name"] for c in staging["columns"]["added"]] == ["source_file"]
    assert column_pairs(staging) == [("batch_id", "batch_id", "name"), ("payload", "payload", "name")]
    assert (staging["older"]["columnCount"], staging["newer"]["columnCount"]) == (2, 3)


def test_audit_is_kept_unchanged_and_its_files_are_not_compared(kit):
    audit = kept(kit, "audit")
    assert (audit["status"], audit["matchedBy"], audit["renamed"]) == ("unchanged", "tableId", False)
    assert (audit["older"]["detailDepth"], audit["newer"]["detailDepth"]) == ("Files", "Summary")
    assert audit["files"] is None
    assert counts(audit["partitions"]) == (0, 0, 0, 0, 0, 0)
    assert counts(audit["commits"]) == (0, 0, 4)


def test_the_collection_of_the_kit(kit):
    collection = kit["collection"]
    assert list(collection) == ["sources", "tables", "columns", "files", "fileSize", "rowCount", "footprint", "waste",
                                "overhead", "depths.Files", "depths.Summary"]
    assert collection["footprint"] == {"older": 649700, "newer": 392100, "delta": -257600}
    assert collection["depths.Files"] == {"older": 5, "newer": 4, "delta": -1}
    assert collection["depths.Summary"] == {"older": 2, "newer": 3, "delta": 1}


def test_a_list_and_a_boolean_have_no_delta(kit):
    identifier = kept(kit, "sales")["columns"]["kept"][0]
    assert identifier["totals"]["codecs"] == {"older": ["SNAPPY"], "newer": ["SNAPPY"], "delta": None}
    assert identifier["totals"]["encodings"] == {"older": ["PLAIN", "RLE"], "newer": ["PLAIN", "RLE"], "delta": None}
    assert kept(kit, "sales")["measures"]["stack.listed"] == {"older": True, "newer": True, "delta": None}


# ---- rank on the kit ----

# The changes as (kind, newer label for kept, label otherwise), in the order of each sort.
KIT_ORDER = {
    "total": [("removed", "legacy"), ("removed", "products"), ("kept", "orders"), ("kept", "sales"),
              ("added", "events"), ("added", "products"), ("kept", "raw.staging"), ("kept", "audit"),
              ("kept", "clients")],
    "waste": [("kept", "orders"), ("removed", "products"), ("kept", "sales"), ("kept", "audit"), ("kept", "clients"),
              ("added", "events"), ("removed", "legacy"), ("added", "products"), ("kept", "raw.staging")],
    "rows": [("removed", "legacy"), ("removed", "products"), ("added", "events"), ("kept", "sales"),
             ("added", "products"), ("kept", "raw.staging"), ("kept", "audit"), ("kept", "clients"),
             ("kept", "orders")],
    "files": [("kept", "orders"), ("removed", "products"), ("added", "events"), ("removed", "legacy"),
              ("added", "products"), ("kept", "raw.staging"), ("kept", "sales"), ("kept", "audit"),
              ("kept", "clients")],
    "name": [("kept", "audit"), ("kept", "clients"), ("added", "events"), ("removed", "legacy"), ("kept", "orders"),
             ("added", "products"), ("removed", "products"), ("kept", "raw.staging"), ("kept", "sales")],
}


def ranked_labels(ranked):
    return [(kind, item["newer"]["label"] if kind == "kept" else item["label"]) for kind, item in ranked]


@pytest.mark.parametrize("order", ORDERS)
def test_rank_orders_the_changes_of_the_kit(order, kit):
    assert ranked_labels(diff.rank(kit, order)) == KIT_ORDER[order]


def test_rank_by_total_is_the_default_and_the_order_of_the_expected_file(kit):
    ranked = diff.rank(kit)
    assert ranked_labels(ranked) == KIT_ORDER["total"]
    for kind in ("added", "removed", "kept"):
        assert_same([item for k, item in ranked if k == kind], EXPECTED_DIFF[kind])


@pytest.mark.parametrize("top, expected", [(1, 1), (3, 3), (9, 9), (100, 9), (None, 9)])
def test_rank_top_keeps_the_first_of_the_merged_order(top, expected, kit):
    for order in ORDERS:
        assert ranked_labels(diff.rank(kit, order, top=top)) == KIT_ORDER[order][:expected]


def test_rank_refuses_another_order(kit):
    with pytest.raises(ValueError):
        diff.rank(kit, "size")


# ---- small footprints ----

def table(name, table_id=None, **fields):
    """A table of a footprint, its totals constant unless given. columns, files, partitions, commits, and operations
    are lists of rows; source is the position of its source among the sources of the footprint, None for none."""
    row = {"name": name, "tableId": table_id, "format": "Delta", "detailDepth": "Summary", "fileCount": 1,
           "fileSize": 1000, "rowCount": 10, "source": 0}
    row.update(fields)
    return row


def footprint(*tables, sources=(SOURCE,), collection=None):
    """The bytes of a PQAX file with these tables, written by pqax.Builder."""
    builder = pqax.Builder(dict(collection or COLLECTION))
    source_ids = [builder.add_source(**source) for source in sources]
    for spec in tables:
        spec = dict(spec)
        details = {key: spec.pop(key, ()) for key in ("columns", "files", "partitions", "commits", "operations")}
        position = spec.pop("source")
        spec["source"] = source_ids[position] if position is not None else None
        identifier = builder.add_table(**spec)
        for row in details["columns"]:
            builder.add_column(table=identifier, **row)
        for row in details["partitions"]:
            builder.add_partition(table=identifier, **row)
        for row in details["files"]:
            builder.add_file(table=identifier, **row)
        for row in details["commits"]:
            builder.add_commit(table=identifier, **row)
        for row in details["operations"]:
            builder.add_operation(table=identifier, **row)
    buffer = io.BytesIO()
    builder.write(buffer)
    return buffer.getvalue()


def without(data, part, table, field, row):
    """The bytes of a PQAX file with one value set to null after the builder wrote it: what the builder refuses, such
    as a required field without a value, and a reader still reads."""
    target = io.BytesIO()
    with zipfile.ZipFile(io.BytesIO(data)) as source, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as copy:
        for info in source.infolist():
            content = source.read(info)
            if info.filename == part:
                document = json.loads(content)
                document[table][field][row] = None
                content = json.dumps(document).encode("utf-8")
            copy.writestr(info.filename, content)
    return target.getvalue()


def compare(older, newer, **options):
    with pqax.open(io.BytesIO(older)) as a, pqax.open(io.BytesIO(newer)) as b:
        return diff.compare(a, b, **options)


def table_pairs(result):
    """The kept tables as (older name, newer name, matchedBy), in the order of the newer file."""
    return [(item["older"]["name"], item["newer"]["name"], item["matchedBy"]) for item in result["kept"]]


def sides(entries):
    return [(item["name"], item["tableId"], item["recreated"]) for item in entries]


def only(result):
    assert len(result["kept"]) == 1, table_pairs(result)
    return result["kept"][0]


# ---- the identity of a table ----

def test_the_table_id_wins_over_the_name():
    # The names are swapped between two tables: each keeps its TableId, so each is kept and renamed.
    result = compare(footprint(table("x", "1"), table("y", "2")), footprint(table("x", "2"), table("y", "1")))
    assert table_pairs(result) == [("y", "x", "tableId"), ("x", "y", "tableId")]
    assert [(item["renamed"], item["status"]) for item in result["kept"]] == [(True, "changed"), (True, "changed")]
    assert result["added"] == [] and result["removed"] == []
    assert result["counts"] == {"added": 0, "removed": 0, "kept": 2, "changed": 2}


def test_two_table_ids_that_differ_never_pair_even_with_the_same_name():
    result = compare(footprint(table("t", "1")), footprint(table("t", "2")))
    assert result["kept"] == []
    assert sides(result["removed"]) == [("t", "1", True)]
    assert sides(result["added"]) == [("t", "2", True)]


@pytest.mark.parametrize("older_id, newer_id", [(None, "1"), ("1", None), (None, None)],
                         ids=["older without", "newer without", "both without"])
def test_the_name_is_the_fallback_when_a_side_has_no_table_id(older_id, newer_id):
    result = compare(footprint(table("t", older_id)), footprint(table("t", newer_id)))
    assert table_pairs(result) == [("t", "t", "name")]
    assert only(result)["renamed"] is False


def test_a_table_without_table_id_on_either_side_renamed_is_removed_and_added():
    result = compare(footprint(table("before")), footprint(table("after")))
    assert result["kept"] == []
    assert sides(result["removed"]) == [("before", None, False)]
    assert sides(result["added"]) == [("after", None, False)]


SOURCE_B = {"kind": "OneLake lakehouse", "name": "Other"}
SOURCE_KIND = {"kind": "ADLS Gen2 container", "name": "Lakehouse"}


@pytest.mark.parametrize("older, newer", [
    ((table("t"), (SOURCE,)), (table("t"), (SOURCE_B,))),
    ((table("t"), (SOURCE,)), (table("t"), (SOURCE_KIND,))),
    ((table("t"), (SOURCE,)), (table("t", source=None), (SOURCE,))),
    ((table("t", namespace=["raw"]), (SOURCE,)), (table("t", namespace=["clean"]), (SOURCE,))),
    ((table("t", namespace=["raw"]), (SOURCE,)), (table("t"), (SOURCE,))),
    ((table("t", namespace=["a", "b"]), (SOURCE,)), (table("t", namespace=["a.b"]), (SOURCE,))),
    ((table("Sales"), (SOURCE,)), (table("sales"), (SOURCE,))),
], ids=["source name", "source kind", "no source", "namespace", "namespace null", "namespace split", "case"])
def test_the_fallback_needs_the_same_source_namespace_and_name(older, newer):
    (older_table, older_sources), (newer_table, newer_sources) = older, newer
    result = compare(footprint(older_table, sources=older_sources), footprint(newer_table, sources=newer_sources))
    assert result["kept"] == []
    # Not the same name key, so not recreated either.
    assert [item["recreated"] for item in result["removed"] + result["added"]] == [False, False]


def test_the_fallback_compares_the_source_by_kind_and_name_not_by_its_id():
    older = footprint(table("t"), sources=({"id": 5, **SOURCE},))
    newer = footprint(table("t", source=1), sources=({"id": 1, **SOURCE_B}, {"id": 2, **SOURCE}))
    assert table_pairs(compare(older, newer)) == [("t", "t", "name")]


def test_tables_without_a_source_pair_by_name():
    older = footprint(table("t", source=None), sources=())
    newer = footprint(table("t", source=None), sources=())
    assert table_pairs(compare(older, newer)) == [("t", "t", "name")]


def test_a_new_namespace_with_the_same_table_id_is_renamed():
    result = compare(footprint(table("t", "1", namespace=["raw"])), footprint(table("t", "1", namespace=["clean"])))
    item = only(result)
    assert (item["matchedBy"], item["renamed"], item["status"]) == ("tableId", True, "changed")
    assert (item["older"]["label"], item["newer"]["label"]) == ("raw.t", "clean.t")


def test_the_first_pass_pairs_the_same_table_id_and_the_same_name():
    # Two tables with the same TableId on each side: the first pass pairs a with a and b with b, although the second
    # alone would pair the first older table with the first newer one.
    result = compare(footprint(table("a", "1"), table("b", "1")), footprint(table("b", "1"), table("a", "1")))
    assert table_pairs(result) == [("b", "b", "tableId"), ("a", "a", "tableId")]
    assert [item["renamed"] for item in result["kept"]] == [False, False]


def test_the_table_id_pass_comes_before_the_name_pass():
    # The older t has no TableId and the name of the newer t; u has its TableId. The TableId pairs u with t first, and
    # the older t is removed.
    result = compare(footprint(table("t", None, fileSize=100), table("u", "1")), footprint(table("t", "1")))
    assert table_pairs(result) == [("u", "t", "tableId")]
    assert sides(result["removed"]) == [("t", None, False)]
    assert result["added"] == []


def test_each_older_table_takes_the_first_unpaired_newer_table_in_the_order_of_the_file():
    result = compare(footprint(table("a", "1")), footprint(table("b", "1"), table("c", "1")))
    assert table_pairs(result) == [("a", "b", "tableId")]
    assert sides(result["added"]) == [("c", "1", False)]

    result = compare(footprint(table("a", "1"), table("b", "1")), footprint(table("c", "1")))
    assert table_pairs(result) == [("a", "c", "tableId")]
    assert sides(result["removed"]) == [("b", "1", False)]

    # By name: the first older t pairs with the newer t, the second is removed.
    result = compare(footprint(table("t", fileSize=100), table("t", fileSize=200)), footprint(table("t")))
    assert [item["older"]["fileSize"] for item in result["kept"]] == [100]
    assert [item["fileSize"] for item in result["removed"]] == [200]


def test_the_side_of_a_table():
    result = compare(footprint(table("t", "1", namespace=["raw"], snapshotId="3", detailDepth="Files",
                                     columns=[{"name": "a", "dataType": "string"},
                                              {"name": "b", "dataType": "string"}])),
                     footprint(table("t", "1", namespace=["raw"], snapshotId="3", detailDepth="Files")))
    item = only(result)
    assert item["older"] == {"id": 1, "label": "raw.t", "name": "t", "namespace": ["raw"], "tableId": "1",
                             "snapshotId": "3", "detailDepth": "Files", "columnCount": 2, "fileCount": 1,
                             "fileSize": 1000, "rowCount": 10, "total": 1000, "waste": None}
    assert item["newer"]["columnCount"] == 0


# ---- the identity of a column ----

def test_columns_pair_by_field_id_then_by_name():
    older = [{"name": "a", "fieldId": 1, "dataType": "int"}, {"name": "b", "fieldId": 2, "dataType": "int"},
             {"name": "c", "dataType": "int"}, {"name": "d", "fieldId": 4, "dataType": "int"},
             {"name": "e", "dataType": "int"}]
    newer = [{"name": "a", "fieldId": 2, "dataType": "int"}, {"name": "b", "fieldId": 1, "dataType": "int"},
             {"name": "c", "fieldId": 9, "dataType": "int"}, {"name": "d", "fieldId": 5, "dataType": "int"},
             {"name": "f", "dataType": "int"}]
    item = only(compare(footprint(table("t", "1", columns=older)), footprint(table("t", "1", columns=newer))))
    # The FieldIds swap the names of a and b; c has a FieldId on one side only, so its name pairs it; d has two
    # FieldIds that differ, so it is removed and added.
    assert column_pairs(item) == [("b", "a", "fieldId"), ("a", "b", "fieldId"), ("c", "c", "name")]
    assert [c["renamed"] for c in item["columns"]["kept"]] == [True, True, False]
    assert [(c["name"], c["fieldId"]) for c in item["columns"]["added"]] == [("d", 5), ("f", None)]
    assert [(c["name"], c["fieldId"]) for c in item["columns"]["removed"]] == [("d", 4), ("e", None)]
    assert item["status"] == "changed"


def test_the_first_pass_of_the_columns_pairs_the_same_field_id_and_the_same_name():
    older = [{"name": "a", "fieldId": 1, "dataType": "int"}, {"name": "b", "fieldId": 1, "dataType": "int"}]
    newer = [{"name": "b", "fieldId": 1, "dataType": "int"}, {"name": "a", "fieldId": 1, "dataType": "int"}]
    item = only(compare(footprint(table("t", "1", columns=older)), footprint(table("t", "1", columns=newer))))
    assert column_pairs(item) == [("b", "b", "fieldId"), ("a", "a", "fieldId")]
    assert item["status"] == "unchanged"


def test_a_column_renamed_without_field_id_is_removed_and_added():
    item = only(compare(footprint(table("t", "1", columns=[{"name": "a", "dataType": "int"}])),
                        footprint(table("t", "1", columns=[{"name": "b", "dataType": "int"}]))))
    assert item["columns"]["kept"] == []
    assert [c["name"] for c in item["columns"]["added"]] == ["b"]
    assert [c["name"] for c in item["columns"]["removed"]] == ["a"]


@pytest.mark.parametrize("change, status", [
    ({}, "unchanged"), ({"dataType": "bigint"}, "changed"), ({"isNullable": False}, "changed"),
    ({"compressedSize": 101}, "changed"), ({"codecs": ["ZSTD"]}, "changed"), ({"encodings": ["RLE", "PLAIN"]}, "changed"),
], ids=["nothing", "data type", "nullable", "a total", "a list", "the order of a list"])
def test_what_changes_a_column(change, status):
    column = {"name": "c", "dataType": "int", "isNullable": True, "compressedSize": 100, "codecs": ["SNAPPY"],
              "encodings": ["PLAIN", "RLE"]}
    item = only(compare(footprint(table("t", "1", columns=[column])),
                        footprint(table("t", "1", columns=[dict(column, **change)]))))
    [kept_column] = item["columns"]["kept"]
    assert kept_column["status"] == status
    assert item["status"] == status
    assert kept_column["older"] == {"id": 1, "label": "c", "name": "c", "fieldId": None, "dataType": "int",
                                    "isNullable": True, "compressedSize": 100}
    assert kept_column["totals"]["codecs"]["delta"] is None
    assert kept_column["totals"]["encodings"]["delta"] is None


# ---- the compared values ----

def test_the_delta_is_none_when_a_side_is_none():
    item = only(compare(footprint(table("t", "1")), footprint(table("t", "1", deletedRowCount=5))))
    assert item["totals"]["deletedRowCount"] == {"older": None, "newer": 5, "delta": None}
    assert item["totals"]["nonLiveFileSize"] == {"older": None, "newer": None, "delta": None}
    # A value against null is a change.
    assert item["status"] == "changed"


def test_the_same_values_on_both_sides_leave_a_table_unchanged():
    result = compare(footprint(table("t", "1", snapshotId="4")), footprint(table("t", "1", snapshotId="4")))
    assert only(result)["status"] == "unchanged"
    assert result["counts"]["changed"] == 0
    assert all(value["delta"] in (0, None) for value in only(result)["totals"].values())


def test_the_delta_of_a_timestamp_is_in_milliseconds():
    item = only(compare(footprint(table("t", "1", lastModifiedAt="2026-10-01T00:00:00.000Z",
                                        oldestCommitAt="2026-09-01T00:00:00.000Z")),
                        footprint(table("t", "1", lastModifiedAt="2026-10-02T00:00:01.500Z"))))
    assert item["totals"]["lastModifiedAt"] == {"older": "2026-10-01T00:00:00.000Z", "newer": "2026-10-02T00:00:01.500Z",
                                                "delta": 86_401_500}
    assert item["totals"]["oldestCommitAt"] == {"older": "2026-09-01T00:00:00.000Z", "newer": None, "delta": None}


def test_a_boolean_has_no_delta():
    # Listed is whether UnreferencedFileSize is present.
    item = only(compare(footprint(table("t", "1")), footprint(table("t", "1", unreferencedFileSize=0))))
    assert item["measures"]["stack.listed"] == {"older": False, "newer": True, "delta": None}


def test_a_snapshot_id_alone_changes_a_table():
    item = only(compare(footprint(table("t", "1", snapshotId="1")), footprint(table("t", "1", snapshotId="2"))))
    assert item["status"] == "changed"


def test_a_change_of_the_measures_alone_leaves_a_table_unchanged():
    # The same table collected ten days later: DaysSinceLastCommit changes with the time of the collection alone.
    spec = table("t", "1", lastModifiedAt="2026-10-01T00:00:00.000Z")
    later = dict(COLLECTION, startedAt="2026-10-21T00:00:00.000Z", completedAt="2026-10-21T00:00:00.000Z")
    result = compare(footprint(spec), footprint(spec, collection=later))
    item = only(result)
    assert item["measures"]["maintenance.daysSinceLastCommit"] == {"older": 10, "newer": 20, "delta": 10}
    assert item["status"] == "unchanged"
    assert result["counts"]["changed"] == 0


def test_the_depths_of_the_collection_count_0_where_a_footprint_has_none():
    result = compare(footprint(table("t", "1")), footprint(table("t", "1", detailDepth="Files")))
    assert list(result["collection"])[-2:] == ["depths.Files", "depths.Summary"]
    assert result["collection"]["depths.Files"] == {"older": 0, "newer": 1, "delta": 1}
    assert result["collection"]["depths.Summary"] == {"older": 1, "newer": 0, "delta": -1}


def test_the_thresholds_of_the_small_files():
    spec = table("t", "1", detailDepth="Files", fileCount=2, fileSize=2500,
                 files=[{"path": "a.parquet", "fileSize": 500}, {"path": "b.parquet", "fileSize": 2000}])
    result = compare(footprint(spec), footprint(spec), small_file_size=1000, tiny_file_size=600)
    assert (result["smallFileSize"], result["tinyFileSize"]) == (1000, 600)
    assert only(result)["measures"]["smallFiles.count"] == {"older": 1, "newer": 1, "delta": 0}
    result = compare(footprint(spec), footprint(spec))
    assert only(result)["measures"]["smallFiles.count"] == {"older": 2, "newer": 2, "delta": 0}


# ---- files ----

def files_table(files, **fields):
    return table("t", "1", **{"detailDepth": "Files", "files": files, **fields})


def test_the_files_pair_by_path():
    older = [{"path": "p1", "fileSize": 100}, {"path": "p2", "fileSize": 200}, {"path": "p3", "fileSize": 300}]
    newer = [{"path": "p2", "fileSize": 200}, {"path": "p3", "fileSize": 300, "deletedRowCount": 5},
             {"path": "p4", "fileSize": 400}, {"path": "p5"}]
    item = only(compare(footprint(files_table(older)), footprint(files_table(newer))))
    # p4 and p5 added, the size of p5 unknown; p1 removed; p2 and p3 kept, p3 changed.
    assert counts(item["files"]) == (2, 1, 2, 1, 400, 100)
    assert item["status"] == "changed"


def test_the_size_of_the_files_added_is_none_when_none_has_a_size_and_0_when_none_was_added():
    item = only(compare(footprint(files_table([{"path": "p1", "fileSize": 100}])),
                        footprint(files_table([{"path": "p1", "fileSize": 100}, {"path": "p2"}]))))
    assert counts(item["files"]) == (1, 0, 1, 0, None, 0)


@pytest.mark.parametrize("change, changed", [
    ({"fileSize": 101}, 1), ({"rowCount": 11}, 1), ({"deletedRowCount": 1}, 1), ({"deletionVectorSize": 40}, 1),
    ({"modifiedAt": "2026-10-02T00:00:00.000Z"}, 0), ({}, 0),
], ids=["size", "rows", "deleted rows", "deletion vector from null", "modified only", "nothing"])
def test_what_changes_a_file(change, changed):
    file = {"path": "p1", "fileSize": 100, "rowCount": 10, "deletedRowCount": 0,
            "modifiedAt": "2026-10-01T00:00:00.000Z"}
    item = only(compare(footprint(files_table([file])), footprint(files_table([dict(file, **change)]))))
    assert counts(item["files"]) == (0, 0, 1, changed, 0, 0)
    assert item["status"] == ("changed" if changed else "unchanged")


@pytest.mark.parametrize("older_depth, newer_depth", [("Files", "Summary"), ("Summary", "Files"), ("Files", None)])
def test_the_files_are_not_compared_when_a_side_does_not_keep_them(older_depth, newer_depth):
    files = [{"path": "p1", "fileSize": 100}]

    def side(depth):
        # The builder refuses a table without a detailDepth, which is required, so that side loses it after the write.
        if depth is None:
            written = footprint(files_table(files, detailDepth="Files"))
            return without(written, "summary.json", "tables", "detailDepth", 0)
        return footprint(files_table(files if depth != "Summary" else (), detailDepth=depth))

    assert only(compare(side(older_depth), side(newer_depth)))["files"] is None


def test_the_files_are_not_compared_when_a_file_has_no_path():
    item = only(compare(footprint(files_table([{"path": "p1", "fileSize": 100}, {"fileSize": 50}])),
                        footprint(files_table([{"path": "p1", "fileSize": 100}]))))
    assert item["files"] is None


def test_the_files_are_not_compared_when_the_files_table_cannot_be_read():
    data = footprint(files_table([{"path": "p1", "fileSize": 100}]))
    broken = io.BytesIO()
    with zipfile.ZipFile(io.BytesIO(data)) as source, zipfile.ZipFile(broken, "w", zipfile.ZIP_DEFLATED) as target:
        for info in source.infolist():
            content = b'{"files": {"id": [1, 2,' if info.filename == "files.json" else source.read(info.filename)
            target.writestr(info.filename, content)
    result = compare(data, broken.getvalue())
    assert only(result)["files"] is None
    assert result["older"]["filesRead"] is True
    assert result["newer"]["filesRead"] is False
    assert result["newer"]["warnings"]


# ---- partitions ----

def partition(values, size, **fields):
    return dict({"values": values, "fileCount": 1, "fileSize": size, "rowCount": 10, "deletedRowCount": 0}, **fields)


def test_the_partitions_pair_by_their_values_a_null_equal_to_a_null():
    older = [partition(["2024"], 100), partition(["2025"], 200), partition([None], 50), partition(["a", None], 70)]
    newer = [partition(["2025"], 250), partition([None], 50), partition(["2026"], 300), partition([None, "a"], 80)]
    item = only(compare(footprint(table("t", "1", partitions=older)), footprint(table("t", "1", partitions=newer))))
    # Kept: 2025 (changed) and [null]. Added: 2026 and [null, a]; removed: 2024 and [a, null].
    assert counts(item["partitions"]) == (2, 2, 2, 1, 380, 170)
    assert item["status"] == "changed"


@pytest.mark.parametrize("change, changed", [
    ({"fileCount": 2}, 1), ({"fileSize": 101}, 1), ({"rowCount": 11}, 1), ({"deletedRowCount": 1}, 1),
    ({"lastModifiedAt": "2026-10-02T00:00:00.000Z"}, 0),
], ids=["files", "size", "rows", "deleted rows", "modified only"])
def test_what_changes_a_partition(change, changed):
    before = partition(["2025"], 100, lastModifiedAt="2026-10-01T00:00:00.000Z")
    item = only(compare(footprint(table("t", "1", partitions=[before])),
                        footprint(table("t", "1", partitions=[dict(before, **change)]))))
    assert counts(item["partitions"]) == (0, 0, 1, changed, 0, 0)


def test_the_partitions_of_a_table_with_a_partition_without_values_are_not_compared():
    older = footprint(table("t", "1", partitions=[partition(["2025"], 100)]),
                      table("u", "2", partitions=[partition(["x"], 100), partition(None, 100)]))
    newer = footprint(table("t", "1", partitions=[partition(["2025"], 100)]),
                      table("u", "2", partitions=[partition(["x"], 100)]))
    result = compare(older, newer)
    by_name = {item["newer"]["name"]: item for item in result["kept"]}
    assert by_name["u"]["partitions"] is None
    assert counts(by_name["t"]["partitions"]) == (0, 0, 1, 0, 0, 0)


def test_the_partitions_are_not_compared_when_a_footprint_keeps_none():
    item = only(compare(footprint(table("t", "1", partitions=[partition(["2025"], 100)])), footprint(table("t", "1"))))
    assert item["partitions"] is None


def test_a_table_without_partitions_compares_zero_partitions_when_both_footprints_keep_some():
    older = footprint(table("t", "1", partitions=[partition(["2025"], 100)]), table("u", "2"))
    newer = footprint(table("t", "1", partitions=[partition(["2025"], 100)]), table("u", "2"))
    by_name = {item["newer"]["name"]: item for item in compare(older, newer)["kept"]}
    assert counts(by_name["u"]["partitions"]) == (0, 0, 0, 0, 0, 0)


# ---- commits ----

def commits(*ids):
    return [{"commitId": commit_id, "timestamp": f"2026-10-0{index + 1}T00:00:00.000Z"}
            for index, commit_id in enumerate(ids)]


def test_the_commits_pair_by_commit_id():
    item = only(compare(footprint(table("t", "1", commits=commits("1", "2", "3"))),
                        footprint(table("t", "1", commits=commits("2", "3", "4", "5")))))
    assert counts(item["commits"]) == (2, 1, 2)
    assert item["status"] == "changed"


def test_the_same_commits_leave_a_table_unchanged():
    item = only(compare(footprint(table("t", "1", commits=commits("1", "2"))),
                        footprint(table("t", "1", commits=commits("1", "2")))))
    assert counts(item["commits"]) == (0, 0, 2)
    assert item["status"] == "unchanged"


def test_the_commits_are_not_compared_with_a_commit_without_id_or_a_footprint_without_commits():
    # The builder refuses a commit without a commitId, which is required, so the second commit loses it after the write.
    second = {"commitId": "2", "timestamp": "2026-10-09T00:00:00.000Z"}
    with_null = without(footprint(table("t", "1", commits=commits("1") + [second])), "commits.json", "commits",
                        "commitId", 1)
    item = only(compare(with_null, footprint(table("t", "1", commits=commits("1")))))
    assert item["commits"] is None
    item = only(compare(footprint(table("t", "1", commits=commits("1"))), footprint(table("t", "1"))))
    assert item["commits"] is None


# ---- rank on small footprints ----

def test_rank_by_magnitude_then_label_then_kind_then_position_with_a_null_magnitude_last():
    # The Total of each table is its FileSize: no other component is present.
    older = footprint(table("m", "1", fileSize=500), table("k", "9", fileSize=900), table("n", "8", fileSize=None))
    newer = footprint(table("z", "2", fileSize=None), table("x", "7", fileSize=100), table("b", "3", fileSize=500),
                      table("m", "5", fileSize=500), table("x", "6", fileSize=100), table("a", "4", fileSize=100),
                      table("k", "9", fileSize=600), table("n", "8", fileSize=50))
    result = compare(older, newer)

    def key(kind, item):
        side = item["newer"] if kind == "kept" else item
        return kind, side["label"], side["tableId"]

    # m removed and m added: the same magnitude and label, added first. x twice: the order of the newer file. k kept
    # with a delta of -300, by its absolute value. n kept with a null delta and z added with a null total, last.
    assert [key(*change) for change in diff.rank(result, "total")] == [
        ("added", "b", "3"), ("added", "m", "5"), ("removed", "m", "1"), ("kept", "k", "9"), ("added", "a", "4"),
        ("added", "x", "7"), ("added", "x", "6"), ("kept", "n", "8"), ("added", "z", "2")]
    assert [key(*change) for change in diff.rank(result, "name")] == [
        ("added", "a", "4"), ("added", "b", "3"), ("kept", "k", "9"), ("added", "m", "5"), ("removed", "m", "1"),
        ("kept", "n", "8"), ("added", "x", "7"), ("added", "x", "6"), ("added", "z", "2")]
    assert [key(*change) for change in diff.rank(result, "total", top=2)] == [("added", "b", "3"), ("added", "m", "5")]


def test_rank_orders_the_labels_by_utf16_code_units():
    # Ordinal: Z (U+005A) before a (U+0061); U+FF41 before U+1F600, whose UTF-16 starts with the surrogate U+D83D.
    newer = footprint(table("a", "1"), table("Z", "2"), table("\U0001F600", "3"), table("ａ", "4"))
    ranked = diff.rank(compare(footprint(), newer), "name")
    assert [item["label"] for _, item in ranked] == ["Z", "a", "\U0001F600", "ａ"]


def test_rank_by_rows_and_files_uses_the_counts():
    older = footprint(table("a", "1", rowCount=100, fileCount=1), table("b", "2", rowCount=10, fileCount=9))
    newer = footprint(table("a", "1", rowCount=150, fileCount=1), table("c", "3", rowCount=60, fileCount=None))
    result = compare(older, newer)
    labels = [(kind, item["newer"]["label"] if kind == "kept" else item["label"]) for kind, item in diff.rank(result, "rows")]
    assert labels == [("added", "c"), ("kept", "a"), ("removed", "b")]
    labels = [(kind, item["newer"]["label"] if kind == "kept" else item["label"]) for kind, item in diff.rank(result, "files")]
    assert labels == [("removed", "b"), ("kept", "a"), ("added", "c")]
