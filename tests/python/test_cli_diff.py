"""python -m pqax diff: the JSON of the kit against conformance/expected/diff/diff-before.diff-after.json, value for
value and key for key, the text report of the kit line by line, --sort, --top, a .vpax that embeds a footprint, the
warnings, and the exit codes. The command runs in the process, through pqax.__main__.main, with one run in a process of
its own."""
from __future__ import annotations

import json
import subprocess
import sys
import zipfile

import pytest

from pqax.__main__ import main
from pqax_kit import EXPECTED, INVALID, ROOT, SAMPLES, load_json

BEFORE, AFTER = SAMPLES / "diff-before.pqax", SAMPLES / "diff-after.pqax"
EXPECTED_DIFF = load_json(EXPECTED / "diff" / "diff-before.diff-after.json")
ORDERS = ["total", "waste", "rows", "files", "name"]


@pytest.fixture
def run(capsys):
    """Runs pqax diff in the process and returns the exit code, the output, and the error output."""
    def run_(*arguments):
        try:
            code = main(["diff", *map(str, arguments)])
        except SystemExit as error:
            code = error.code
        out, err = capsys.readouterr()
        return code, out, err
    return run_


@pytest.fixture
def as_json(run):
    def run_(*arguments):
        code, out, err = run(*arguments, "--json")
        assert code == 0, out + err
        return json.loads(out)
    return run_


def lines(text):
    return text.rstrip("\n").split("\n")


def differences(actual, expected, path="$"):
    """Where two parsed JSON values differ: objects with the same keys in the same order, lists item by item, and every
    other value by its type and value, so that 3 and 3.0, or 1 and true, differ. Numbers compare exactly."""
    if isinstance(expected, dict):
        if not isinstance(actual, dict):
            return [f"{path}: got {actual!r}, expected an object"]
        if list(actual) != list(expected):
            return [f"{path}: keys {list(actual)}, expected {list(expected)}"]
        return [problem for key in expected for problem in differences(actual[key], expected[key], f"{path}.{key}")]
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


def layout(rows, left):
    """Rows of cells in columns two spaces apart, the first `left` columns left-aligned and the others right-aligned,
    each line without its trailing spaces."""
    widths = [max(len(row[i]) for row in rows) for i in range(len(rows[0]))]
    return ["  ".join(cell.ljust(widths[i]) if i < left else cell.rjust(widths[i]) for i, cell in enumerate(row)).rstrip()
            for row in rows]


# ---- the text report of the kit, from the interface of 24 October 2026 ----

# The interface writes "<n> table(s)": "table" for one, "tables" otherwise, as pqax tables writes its first line.
TABLES_WORD = "tables"
HEADER = ["Change", "Table", "Total before", "Total after", "Total change", "Waste change", "Files change",
          "Rows change", "Files added", "Files removed"]
COLUMNS_HEADER = ["Table", "Column", "Change", "Detail"]

# One row per change of the kit, by (kind, label), with the numbers of the expected diff written as pqax tables writes
# bytes: legacy 181,000 B is 177 KB; the waste change of a removed table with no waste is 0 B; customers renamed clients.
ROWS = {
    ("removed", "legacy"): ["removed", "legacy", "177 KB", "-", "-177 KB", "0 B", "-2", "-15,000", "-", "-"],
    ("removed", "products"): ["replaced", "products", "108 KB", "-", "-108 KB", "-29.3 KB", "-3", "-6,000", "-", "-"],
    ("kept", "orders"): ["changed", "orders", "138 KB", "72.8 KB", "-64.9 KB", "-66.4 KB", "-3", "0", "1", "4"],
    ("kept", "sales"): ["changed", "sales", "122 KB", "166 KB", "+44.4 KB", "+18.6 KB", "+1", "+3,000", "2", "1"],
    ("added", "events"): ["added", "events", "-", "27.3 KB", "+27.3 KB", "0 B", "+2", "+5,000", "-", "-"],
    ("added", "products"): ["recreated", "products", "-", "21.5 KB", "+21.5 KB", "0 B", "+1", "+2,000", "-", "-"],
    ("kept", "raw.staging"): ["changed", "raw.staging", "9.96 KB", "15.2 KB", "+5.27 KB", "0 B", "+1", "+540", "1", "0"],
    ("kept", "audit"): ["unchanged", "audit", "17.6 KB", "17.6 KB", "0 B", "0 B", "0", "0", "n/a", "n/a"],
    ("kept", "clients"): ["renamed", "clients (was customers)", "62.5 KB", "62.5 KB", "0 B", "0 B", "0", "0", "n/a",
                          "n/a"],
}

# The columns of each kept table that changed: added, then removed, then the kept ones that changed, in their order.
COLUMN_ROWS = {
    "orders": [["orders", "order_id", "changed", "size -1,000 B"], ["orders", "qty", "changed", "size -500 B"],
               ["orders", "price", "changed", "type int to bigint; size +1,000 B"],
               ["orders", "customer", "changed", "size -1,000 B"]],
    "sales": [["sales", "discount", "added", "double"], ["sales", "region", "added", "string"],
              ["sales", "discount", "removed", "double"], ["sales", "note", "removed", "string"],
              ["sales", "id", "changed", "size +8.30 KB"], ["sales", "amount", "renamed", "was amt; size +6.84 KB"]],
    "raw.staging": [["raw.staging", "source_file", "added", "string"],
                    ["raw.staging", "batch_id", "changed", "size +1.17 KB"],
                    ["raw.staging", "payload", "changed", "size +2.25 KB"]],
}

ORDER = {
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


def expected_report(sort="total", top=None):
    shown = ORDER[sort][:top] if top else ORDER[sort]
    text = [
        f"Older: diff-before.pqax, 7 {TABLES_WORD}, collected 2026-10-01T06:00:00.000Z",
        f"Newer: diff-after.pqax, 7 {TABLES_WORD}, collected 2026-10-08T06:00:00.000Z",
        f"Tables: 2 added, 2 removed, 5 kept, 4 changed; by {sort}" + (f", the first {top}" if top else ""),
        "Footprint: 634 KB to 383 KB, -252 KB; waste: 136 KB to 58.6 KB, -77.1 KB",
        "",
    ]
    text += layout([HEADER] + [ROWS[change] for change in shown], 2)
    columns = [row for kind, label in shown if kind == "kept" for row in COLUMN_ROWS.get(label, [])]
    if columns:
        text += [""] + layout([COLUMNS_HEADER] + columns, 4)
    return text


def labels(document):
    """The changes of the JSON as (kind, label), each list in its order."""
    return {kind: [item["newer"]["label"] if kind == "kept" else item["label"] for item in document[kind]]
            for kind in ("added", "removed", "kept")}


def expected_labels(sort, top=None):
    shown = ORDER[sort][:top] if top else ORDER[sort]
    return {kind: [label for k, label in shown if k == kind] for kind in ("added", "removed", "kept")}


# ---- the kit ----

def test_the_json_of_the_kit_is_the_expected_diff(as_json):
    assert_same(as_json(BEFORE, AFTER), EXPECTED_DIFF)


def test_the_json_writes_an_integer_without_a_decimal_point(run):
    code, out, err = run(BEFORE, AFTER, "--json")
    assert code == 0, out + err
    assert '"smallFileSize": 134217728,' in out
    assert '"delta": -257600' in out
    assert ".0," not in out and ".0\n" not in out
    # Two spaces of indent, as json.dumps(indent=2) writes it.
    assert out.startswith('{\n  "older": {\n    "file": "diff-before.pqax",\n')


def test_the_text_report_of_the_kit(run):
    code, out, err = run(BEFORE, AFTER)
    assert code == 0, out + err
    assert lines(out) == expected_report()


def test_the_rows_of_the_text_report_of_the_kit(run):
    """A few cells, written out, independently of the layout."""
    code, out, _ = run(BEFORE, AFTER)
    assert code == 0
    text = lines(out)
    assert text[0] == f"Older: diff-before.pqax, 7 {TABLES_WORD}, collected 2026-10-01T06:00:00.000Z"
    assert text[2] == "Tables: 2 added, 2 removed, 5 kept, 4 changed; by total"
    assert text[3] == "Footprint: 634 KB to 383 KB, -252 KB; waste: 136 KB to 58.6 KB, -77.1 KB"
    assert text[4] == ""
    assert text[5].split() == ["Change", "Table", "Total", "before", "Total", "after", "Total", "change", "Waste",
                               "change", "Files", "change", "Rows", "change", "Files", "added", "Files", "removed"]
    assert text[6].split() == ["removed", "legacy", "177", "KB", "-", "-177", "KB", "0", "B", "-2", "-15,000", "-", "-"]
    assert text[7].split()[:2] == ["replaced", "products"]
    assert text[9].split() == ["changed", "sales", "122", "KB", "166", "KB", "+44.4", "KB", "+18.6", "KB", "+1",
                               "+3,000", "2", "1"]
    assert text[11].split()[:2] == ["recreated", "products"]
    assert text[13].split()[-2:] == ["n/a", "n/a"]
    assert text[14].startswith("renamed    clients (was customers)  ")
    assert text[15] == ""
    assert text[16].split() == ["Table", "Column", "Change", "Detail"]
    assert text[19].split() == ["orders", "price", "changed", "type", "int", "to", "bigint;", "size", "+1,000", "B"]
    assert [row.split()[:3] for row in text[21:25]] == [["sales", "discount", "added"], ["sales", "region", "added"],
                                                         ["sales", "discount", "removed"], ["sales", "note", "removed"]]
    assert text[26].split() == ["sales", "amount", "renamed", "was", "amt;", "size", "+6.84", "KB"]
    assert len(text) == 30


@pytest.mark.parametrize("sort", ORDERS)
def test_each_sort(sort, run, as_json):
    document = as_json(BEFORE, AFTER, "--sort", sort)
    assert (document["sort"], document["top"]) == (sort, None)
    assert labels(document) == expected_labels(sort)
    assert document["counts"] == {"added": 2, "removed": 2, "kept": 5, "changed": 4}
    code, out, err = run(BEFORE, AFTER, "--sort", sort)
    assert code == 0, out + err
    assert lines(out) == expected_report(sort)


@pytest.mark.parametrize("top", [1, 2, 3, 9, 100])
def test_top_keeps_the_first_changes_and_the_counts_of_all(top, run, as_json):
    document = as_json(BEFORE, AFTER, "--top", top)
    assert document["top"] == top
    assert document["counts"] == {"added": 2, "removed": 2, "kept": 5, "changed": 4}
    assert labels(document) == expected_labels("total", top)
    code, out, err = run(BEFORE, AFTER, "--top", top)
    assert code == 0, out + err
    assert lines(out) == expected_report("total", top)


def test_top_2_shows_no_kept_table_and_no_columns_table(run):
    code, out, _ = run(BEFORE, AFTER, "--top", "2")
    assert code == 0
    text = lines(out)
    assert len(text) == 8
    assert "Column" not in out


def test_top_with_another_sort(as_json):
    document = as_json(BEFORE, AFTER, "--top", "3", "--sort", "name")
    assert labels(document) == {"added": ["events"], "removed": [], "kept": ["audit", "clients"]}


def test_a_vpax_gives_its_embedded_footprint_and_the_same_footprint_has_no_change(run, as_json):
    document = as_json(SAMPLES / "embedded-stored.vpax", SAMPLES / "sample.pqax")
    assert (document["older"]["file"], document["older"]["embeddedPart"]) == ("embedded-stored.vpax", "Footprint.pqax")
    assert (document["newer"]["file"], document["newer"]["embeddedPart"]) == ("sample.pqax", None)
    assert document["counts"] == {"added": 0, "removed": 0, "kept": 2, "changed": 0}
    assert all(item["status"] == "unchanged" and not item["renamed"] for item in document["kept"])
    assert all(value["delta"] in (0, None) for value in document["collection"].values())
    code, out, err = run(SAMPLES / "embedded-stored.vpax", SAMPLES / "sample.pqax")
    assert code == 0, out + err
    assert lines(out)[2] == "Tables: 0 added, 0 removed, 2 kept, 0 changed; by total"
    assert "Column" not in out


def test_part_applies_to_both_files(as_json):
    document = as_json(SAMPLES / "embedded-stored.vpax", SAMPLES / "embedded-deflated.vpax", "--part", "Footprint.pqax")
    assert document["older"]["embeddedPart"] == document["newer"]["embeddedPart"] == "Footprint.pqax"
    assert document["counts"] == {"added": 0, "removed": 0, "kept": 2, "changed": 0}


def test_a_footprint_against_itself(as_json):
    document = as_json(AFTER, AFTER)
    assert document["counts"] == {"added": 0, "removed": 0, "kept": 7, "changed": 0}
    assert all(item["matchedBy"] in ("tableId", "name") for item in document["kept"])


def test_the_warnings_of_the_measures_go_to_the_error_output(tmp_path, run):
    broken = tmp_path / "broken.pqax"
    with zipfile.ZipFile(AFTER) as source, zipfile.ZipFile(broken, "w", zipfile.ZIP_DEFLATED) as target:
        for info in source.infolist():
            data = b'{"files": {"id": [1, 2,' if info.filename == "files.json" else source.read(info.filename)
            target.writestr(info.filename, data)
    code, out, err = run(BEFORE, broken, "--json")
    assert code == 0, out + err
    assert err.strip()
    assert all(line.startswith("pqax diff: broken.pqax: ") for line in lines(err))
    document = json.loads(out)
    assert (document["newer"]["filesRead"], document["older"]["filesRead"]) == (False, True)
    assert document["newer"]["warnings"]
    assert all(item["files"] is None for item in document["kept"])
    code, out, err = run(BEFORE, broken)
    assert code == 0
    assert err.strip()


def test_the_command_runs_in_a_process_of_its_own():
    completed = subprocess.run([sys.executable, "-m", "pqax", "diff", str(BEFORE), str(AFTER), "--json"], cwd=ROOT,
                               capture_output=True, text=True, encoding="utf-8", timeout=120)
    assert completed.returncode == 0, completed.stdout + completed.stderr
    assert_same(json.loads(completed.stdout), EXPECTED_DIFF)


# ---- exit codes ----

@pytest.mark.parametrize("arguments", [
    [], ["{before}"], ["{before}", "{after}", "{after}"], ["--json"], ["{before}", "{after}", "--sort", "size"],
    ["{before}", "{after}", "--sort"], ["{before}", "{after}", "--top", "0"], ["{before}", "{after}", "--top", "-1"],
    ["{before}", "{after}", "--top", "x"], ["{before}", "{after}", "--top", "1.5"], ["{before}", "{after}", "--top"],
    ["{before}", "{after}", "--no-such-option"],
], ids=["nothing", "one file", "three files", "json only", "unknown sort", "sort without value", "top 0", "top -1",
        "top x", "top 1.5", "top without value", "unknown option"])
def test_a_wrong_command_line_exits_with_2(arguments, run):
    resolved = [str(BEFORE) if a == "{before}" else str(AFTER) if a == "{after}" else a for a in arguments]
    code, out, err = run(*resolved)
    assert code == 2, out + err
    assert err.strip()


@pytest.mark.parametrize("missing", ["older", "newer"])
def test_a_file_that_does_not_exist_exits_with_2(missing, tmp_path, run):
    absent = tmp_path / "missing.pqax"
    code, _, err = run(*((absent, AFTER) if missing == "older" else (BEFORE, absent)))
    assert code == 2
    assert err.strip()


@pytest.mark.parametrize("name", ["manifest.format.pqax", "manifest.versionUnknown.pqax", "container.manifestMissing.pqax"])
@pytest.mark.parametrize("side", ["older", "newer"])
def test_a_file_that_the_reader_refuses_exits_with_1(name, side, run):
    import pqax
    with pytest.raises(pqax.PqaxError):
        pqax.open(INVALID / name).close()
    code, out, err = run(*((INVALID / name, AFTER) if side == "older" else (BEFORE, INVALID / name)))
    assert code == 1, out + err
    assert err.strip()
