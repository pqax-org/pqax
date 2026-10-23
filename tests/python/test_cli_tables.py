"""python -m pqax tables: the JSON against conformance/expected/measures, the sort and its ties, --top, the text table
with its aligned columns and the numbers written as the views write them, --wide, and the exit codes. The command runs
in the process, through pqax.__main__.main, with one run in a process of its own."""
from __future__ import annotations

import functools
import json
import locale
import re
import subprocess
import sys
import zipfile
from datetime import datetime
from decimal import ROUND_HALF_UP, Decimal

import pytest

from pqax.__main__ import main
from pqax_kit import EXPECTED, INVALID, ROOT, SAMPLES, expected_by_name, load_json

KIT = expected_by_name()
MEASURES = EXPECTED / "measures"
COLUMNS = ["Table", "Rows", "Files", "Live", "Non-live", "Unreferenced", "Deleted", "Stale", "Waste", "Waste share"]
WIDE_COLUMNS = ["Under 8 MiB", "Under 128 MiB", "Metadata", "Footers"]
SORTS = ["waste", "total", "rows", "files", "name"]
MIB = 1024 * 1024
GIB = 1024 * MIB


@pytest.fixture
def tables(capsys):
    """Runs pqax tables in the process and returns the exit code, the output, and the error output."""
    def run(*arguments):
        try:
            code = main(["tables", *map(str, arguments)])
        except SystemExit as error:
            code = error.code
        out, err = capsys.readouterr()
        return code, out, err
    return run


@pytest.fixture
def as_json(tables):
    def run(*arguments):
        code, out, err = tables(*arguments, "--json")
        assert code == 0, out + err
        return json.loads(out)
    return run


def sample(name):
    return SAMPLES / KIT[name]["file"]


def split(line):
    return re.split(r" {2,}", line.strip())


def lines(text):
    return text.rstrip("\n").split("\n")


# ---- the numbers as the views write them, with the locale en-US ----

def fixed(value, digits):
    """Rounds as Intl.NumberFormat does: the shortest decimal of the float, then half away from zero, with commas."""
    rounded = Decimal(repr(float(value))).quantize(Decimal(1).scaleb(-digits), rounding=ROUND_HALF_UP)
    return f"{rounded:,.{digits}f}"


def fmt_bytes(value):
    if value is None:
        return "n/a"
    units = ["B", "KB", "MB", "GB", "TB", "PB"]
    scaled = abs(value)
    unit = 0
    while scaled >= 1024 and unit < len(units) - 1:
        scaled /= 1024
        unit += 1
    digits = 0 if unit == 0 else 2 if scaled < 10 else 1 if scaled < 100 else 0
    return ("-" if value < 0 else "") + fixed(scaled, digits) + " " + units[unit]


def fmt_count(value):
    return "n/a" if value is None else f"{int(Decimal(repr(float(value))).quantize(Decimal(1), rounding=ROUND_HALF_UP)):,}"


def fmt_share(value):
    if value is None:
        return "n/a"
    if 0 < value < 0.0005:
        return "<0.1%"
    return fixed(value * 100, 1) + "%"


def small_share(files):
    """A lower bound from the quartiles with >= when it is strictly between 0 and 1; exact otherwise."""
    share = files["shareAtLeast"]
    if share is None:
        return "n/a"
    return (">=" if files["count"] is None and 0 < share < 1 else "") + fmt_share(share)


def cells(table, wide):
    stack = table["stack"]
    row = [table["label"], fmt_count(table["rowCount"]), fmt_count(table["fileCount"]), fmt_bytes(stack["live"]),
           fmt_bytes(stack["nonLive"]), fmt_bytes(stack["unreferenced"]), fmt_bytes(stack["deleted"]),
           fmt_bytes(stack["stale"]), fmt_bytes(stack["waste"]), fmt_share(stack["wasteShare"])]
    if wide:
        row += [small_share(table["tinyFiles"]), small_share(table["smallFiles"]), fmt_bytes(stack["metadata"]),
                fmt_bytes(stack["footers"])]
    return row


def assert_text_table(path, text, document, wide, sort, top=None):
    """The text table against the JSON of the same run: the first line, the header, and each row with its columns
    aligned: the label left-aligned, every number right-aligned, two spaces between the columns."""
    rows_of_text = lines(text)
    first = f"{path.name}: {document['tableCount']} tables, by {sort}" + (f", the first {top}" if top else "")
    assert rows_of_text[0] == first
    header = COLUMNS + (WIDE_COLUMNS if wide else [])
    assert split(rows_of_text[1]) == header
    rows = [cells(table, wide) for table in document["tables"]]
    assert len(rows_of_text) == 2 + len(rows)
    widths = [max([len(name)] + [len(row[i]) for row in rows]) for i, name in enumerate(header)]
    starts = [0]
    for width in widths[:-1]:
        starts.append(starts[-1] + width + 2)
    for index, row in enumerate(rows):
        expected = "  ".join(cell.ljust(widths[i]) if i == 0 else cell.rjust(widths[i]) for i, cell in enumerate(row))
        assert rows_of_text[2 + index].rstrip() == expected.rstrip()
    position = 0
    for i, name in enumerate(header):
        at = rows_of_text[1].index(name, position)
        assert starts[i] <= at and at + len(name) <= starts[i] + widths[i], f"{name} is outside its column:\n{text}"
        position = at + len(name)


# ---- the sort of the interface, on the expected tables ----

def sorted_tables(tables_, sort):
    indexed = list(enumerate(tables_))

    def ties(a, b):
        ta, tb = a[1]["stack"]["total"], b[1]["stack"]["total"]
        if ta != tb:
            return -1 if ta > tb else 1
        la, lb = a[1]["label"], b[1]["label"]
        if la != lb:
            return -1 if la < lb else 1
        return a[0] - b[0]

    def descending(key):
        def compare(a, b):
            va, vb = key(a[1]), key(b[1])
            if va != vb:
                if va is None:
                    return 1
                if vb is None:
                    return -1
                return -1 if va > vb else 1
            return ties(a, b)
        return compare

    def by_name(a, b):
        la, lb = a[1]["label"], b[1]["label"]
        return (-1 if la < lb else 1) if la != lb else a[0] - b[0]

    compare = {
        "waste": descending(lambda t: t["stack"]["waste"]), "total": ties,
        "rows": descending(lambda t: t["rowCount"]), "files": descending(lambda t: t["fileCount"]), "name": by_name,
    }[sort]
    return [table for _, table in sorted(indexed, key=functools.cmp_to_key(compare))]


def instant(text):
    return datetime.fromisoformat(text.replace("Z", "+00:00"))


# The keys of a table that pqax tables --json prints: those of the expected measures less stack.shares, files,
# partitions, and the keys of maintenance after the first three, which the kit gained with the other measures of
# section 10 and the command does not print.
CLI_TABLE_KEYS = ["id", "name", "label", "rowCount", "fileCount", "fileSize", "stack", "smallFiles", "tinyFiles",
                  "measures", "maintenance"]
CLI_STACK_KEYS = ["live", "nonLive", "unreferenced", "deleted", "stale", "metadata", "footers", "deletedSource",
                  "listed", "data", "total", "waste", "overhead", "wasteShare", "overheadShare"]
CLI_MAINTENANCE_KEYS = ["lastCommitAt", "commitsPerDay", "daysSinceLastCommit"]


def cli_shape(tables_):
    """The expected tables restricted to the keys that the command prints."""
    def table(expected):
        shaped = {key: expected[key] for key in CLI_TABLE_KEYS}
        shaped["stack"] = {key: expected["stack"][key] for key in CLI_STACK_KEYS}
        shaped["maintenance"] = {key: expected["maintenance"][key] for key in CLI_MAINTENANCE_KEYS}
        return shaped
    return [table(expected) for expected in tables_]


def same_tables(actual, expected):
    """Exact equality, with lastCommitAt as an instant."""
    def normal(tables_):
        result = json.loads(json.dumps(tables_))
        for table in result:
            if table["maintenance"]["lastCommitAt"] is not None:
                table["maintenance"]["lastCommitAt"] = instant(table["maintenance"]["lastCommitAt"]).isoformat()
        return result
    return normal(actual) == normal(expected)


# ---- the kit ----

@pytest.mark.parametrize("name", sorted(KIT))
def test_the_json_has_the_header_and_the_tables_of_the_expected_measures(name, as_json):
    import pqax
    path = sample(name)
    document = as_json(path)
    expected = load_json(MEASURES / f"{name}.json")
    part = KIT[name]["embeddedPart"]
    with (pqax.open_embedded(path, part) if part else pqax.open(path)) as doc:
        collection = doc.collection
    assert document["file"] == path.name
    assert document["embeddedPart"] == part
    assert document["format"] == KIT[name]["format"]
    assert document["formatVersion"] == KIT[name]["formatVersion"]
    assert document["collector"] == f"{collection['collectorApp']} {collection['collectorAppVersion']}"
    assert document["library"] is None
    assert instant(document["collectedAt"]) == instant("2026-10-05T20:55:12.749Z")
    assert document["level"] == 2
    assert (document["smallFileSize"], document["tinyFileSize"]) == (134217728, 8388608)
    assert document["sort"] == "waste"
    assert "top" in document and document["top"] is None
    assert document["tableCount"] == len(expected["tables"])
    assert document["filesRead"] == expected["filesRead"]
    assert len(document["warnings"]) == len(expected["warnings"])
    assert same_tables(document["tables"], cli_shape(sorted_tables(expected["tables"], "waste")))
    # The command prints only the keys it printed before the other measures of section 10.
    assert "columns" not in document and "collection" not in document
    for table in document["tables"]:
        assert list(table) == CLI_TABLE_KEYS
        assert list(table["stack"]) == CLI_STACK_KEYS
        assert list(table["maintenance"]) == CLI_MAINTENANCE_KEYS


@pytest.mark.parametrize("sort", SORTS)
def test_each_sort_orders_the_tables_of_a_sample(sort, as_json):
    document = as_json(sample("sample"), "--sort", sort)
    assert document["sort"] == sort
    labels = [table["label"] for table in document["tables"]]
    assert labels == (["dbo.orders", "sales"] if sort == "name" else ["sales", "dbo.orders"])


@pytest.mark.parametrize("name", sorted(KIT))
@pytest.mark.parametrize("wide", [False, True], ids=["narrow", "wide"])
def test_the_text_table_has_aligned_columns_and_the_numbers_of_the_json(name, wide, tables, as_json):
    path = sample(name)
    options = ["--wide"] if wide else []
    code, out, err = tables(path, *options)
    assert code == 0, out + err
    assert_text_table(path, out, as_json(path, *options), wide, "waste")


def test_the_text_table_of_a_sample_has_the_numbers_of_the_views(tables):
    code, out, err = tables(sample("sample"))
    assert code == 0, out + err
    text = lines(out)
    assert text[0] == "sample.pqax: 2 tables, by waste"
    assert split(text[2]) == ["sales", "17,000", "3", "151 KB", "737 B", "0 B", "239 B", "0 B", "976 B", "0.6%"]
    assert split(text[3]) == ["dbo.orders", "4,100", "2", "37.7 KB", "0 B", "0 B", "0 B", "0 B", "0 B", "0.0%"]


def test_wide_writes_an_exact_share_from_the_files_and_from_a_maximum_below_the_threshold(tables):
    code, out, _ = tables(sample("sample"), "--wide")
    assert code == 0
    assert split(lines(out)[2])[-4:] == ["100.0%", "100.0%", "12.2 KB", "3.76 KB"]
    code, out, _ = tables(sample("summary-only"), "--wide")
    assert code == 0
    assert split(lines(out)[2])[-4:] == ["100.0%", "100.0%", "12.2 KB", "3.76 KB"]


def test_a_format_0_1_sample_has_no_unreferenced_files(tables):
    code, out, _ = tables(sample("format-0.1"))
    assert code == 0
    row = split(lines(out)[2])
    assert (row[0], row[4], row[5]) == ("sales", "12.1 KB", "n/a")


def test_a_vpax_gives_the_tables_of_its_embedded_footprint(as_json):
    document = as_json(SAMPLES / "embedded-deflated.vpax")
    assert document["embeddedPart"] == "Footprint.pqax"
    assert document["file"] == "embedded-deflated.vpax"
    assert same_tables(document["tables"], as_json(sample("sample"))["tables"])
    assert as_json(SAMPLES / "embedded-stored.vpax", "--part", "Footprint.pqax")["tableCount"] == 2


def test_top_keeps_the_first_tables_and_the_count_of_all(tables, as_json):
    path = sample("sample")
    document = as_json(path, "--top", "1")
    assert (document["top"], document["tableCount"]) == (1, 2)
    assert [table["label"] for table in document["tables"]] == ["sales"]
    code, out, _ = tables(path, "--top", "1", "--sort", "name")
    assert code == 0
    assert_text_table(path, out, as_json(path, "--top", "1", "--sort", "name"), False, "name", 1)
    assert lines(out)[2].startswith("dbo.orders ")
    assert len(as_json(path, "--top", "5")["tables"]) == 2


def test_the_output_does_not_depend_on_the_locale(tables):
    path = sample("sample")
    english = [tables(path)[1], tables(path, "--wide")[1], tables(path, "--json")[1]]
    saved = locale.setlocale(locale.LC_ALL)
    try:
        try:
            locale.setlocale(locale.LC_ALL, "de-DE" if sys.platform == "win32" else "de_DE.UTF-8")
        except locale.Error:
            pytest.skip("no German locale on this machine")
        assert [tables(path)[1], tables(path, "--wide")[1], tables(path, "--json")[1]] == english
    finally:
        locale.setlocale(locale.LC_ALL, saved)


def test_the_command_runs_in_a_process_of_its_own():
    completed = subprocess.run([sys.executable, "-m", "pqax", "tables", str(sample("sample")), "--json"], cwd=ROOT,
                               capture_output=True, text=True, encoding="utf-8", timeout=120)
    assert completed.returncode == 0, completed.stdout + completed.stderr
    assert json.loads(completed.stdout)["tableCount"] == 2


# ---- exit codes ----

@pytest.mark.parametrize("arguments", [
    [], ["--json"], ["{sample}", "--sort", "size"], ["{sample}", "--sort"], ["{sample}", "--top", "0"],
    ["{sample}", "--top", "-1"], ["{sample}", "--top", "x"], ["{sample}", "--top", "1.5"], ["{sample}", "--top"],
    ["{sample}", "--no-such-option"], ["{sample}", "{sample}"],
], ids=["nothing", "json only", "unknown sort", "sort without value", "top 0", "top -1", "top x", "top 1.5",
        "top without value", "unknown option", "two files"])
def test_a_wrong_command_line_exits_with_2(arguments, tables):
    resolved = [str(sample("sample")) if argument == "{sample}" else argument for argument in arguments]
    code, out, err = tables(*resolved)
    assert code == 2, out + err
    assert err.strip()


def test_a_file_that_does_not_exist_exits_with_2(tables, tmp_path):
    code, _, err = tables(tmp_path / "missing.pqax")
    assert code == 2
    assert err.strip()


@pytest.mark.parametrize("name", ["manifest.versionUnknown.pqax", "manifest.format.pqax", "container.manifestMissing.pqax"])
def test_a_file_that_the_reader_refuses_exits_with_1(name, tables):
    import pqax
    with pytest.raises(pqax.PqaxError):
        pqax.open(INVALID / name).close()
    code, out, err = tables(INVALID / name)
    assert code == 1, out + err
    assert err.strip()


# ---- synthetic footprints ----

def write_footprint(path, rows):
    keys = []
    for row in rows:
        keys += [key for key in row if key not in keys]
    summary = {"tables": {key: [row.get(key) for row in rows] for key in keys}}
    manifest = {"format": "pqax", "formatVersion": "0.1",
                "collection": {"collectorApp": "pqax-tests", "collectorAppVersion": "0.0",
                               "completedAt": "2026-10-11T00:00:00.000Z", "level": 2},
                "parts": [{"name": "summary.json", "tables": {"tables": len(rows)}}]}
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("manifest.json", json.dumps(manifest))
        archive.writestr("summary.json", json.dumps(summary))
    return path


def row(identifier, name, file_size, non_live, row_count, file_count, **more):
    result = {"id": identifier, "name": name, "fileSize": file_size, "nonLiveFileSize": non_live, "metadataSize": 0,
              "rowCount": row_count, "deletedRowCount": 0, "fileCount": file_count}
    result.update(more)
    return result


@pytest.fixture
def ties(tmp_path):
    """Six tables: rows 1 and 4 have the same label, waste, and total; Zeta sorts first in ordinal order."""
    return write_footprint(tmp_path / "ties.pqax", [
        row(1, "beta", 1000, 100, 10, 1), row(2, "alpha", 1000, 100, None, 5), row(3, "gamma", 2000, 100, 10, None),
        row(4, "delta", 500, 300, 99, 2), row(5, "alpha", 1000, 100, 10, 1), row(6, "Zeta", 10, 0, 1, 1)])


@pytest.mark.parametrize("sort, ids", [
    ("waste", [4, 3, 2, 5, 1, 6]), ("total", [3, 2, 5, 1, 4, 6]), ("rows", [4, 3, 5, 1, 6, 2]),
    ("files", [2, 4, 5, 1, 6, 3]), ("name", [6, 2, 5, 1, 4, 3]),
])
def test_the_ties_go_by_total_then_label_then_the_order_of_the_file(sort, ids, ties, tables, as_json):
    arguments = [ties] if sort == "waste" else [ties, "--sort", sort]
    document = as_json(*arguments)
    assert [table["id"] for table in document["tables"]] == ids
    assert document["tableCount"] == 6
    code, out, err = tables(*arguments)
    assert code == 0, out + err
    assert_text_table(ties, out, document, False, sort)
    assert_text_table(ties, tables(*arguments, "--wide")[1], as_json(*arguments, "--wide"), True, sort)


def test_top_after_the_sort_with_ties(ties, tables, as_json):
    document = as_json(ties, "--top", "3", "--sort", "files")
    assert [table["id"] for table in document["tables"]] == [2, 4, 5]
    assert (document["tableCount"], document["top"]) == (6, 3)
    code, out, _ = tables(ties, "--top", "3", "--sort", "files")
    assert code == 0
    assert_text_table(ties, out, document, False, "files", 3)


def test_the_numbers_are_rounded_as_the_views_round_them(tmp_path, tables, as_json):
    path = write_footprint(tmp_path / "rounding.pqax", [
        row(1, "ties", 1152, 102912, 1234567, 1), row(2, "sixteenth", 15, 1, 10, 1), row(3, "quarter", 12544, 0, 10, 1),
        row(4, "tiny", 1000000, 1, 10, 1), row(5, "big", 22544384, 1536, 10, 1), row(6, "huge", 5 * GIB + GIB // 2, 0, 10, 1),
        row(7, "thousand", 1000, 0, 10, 1)])
    code, out, err = tables(path, "--sort", "name")
    assert code == 0, out + err
    rows = {cells_[0]: cells_ for cells_ in map(split, lines(out)[2:])}
    # 1152 B is 1.125 KB and 102912 B 100.5 KB: Intl.NumberFormat rounds half away from zero.
    assert rows["ties"] == ["ties", "1,234,567", "1", "1.13 KB", "101 KB", "n/a", "0 B", "n/a", "101 KB", "98.9%"]
    assert rows["sixteenth"][9] == "6.3%"
    assert rows["sixteenth"][3] == "15 B"
    assert rows["quarter"][3] == "12.3 KB"
    assert rows["quarter"][9] == "0.0%"
    assert rows["tiny"][9] == "<0.1%"
    assert rows["big"][3] == "21.5 MB"
    assert rows["big"][4] == "1.50 KB"
    assert rows["huge"][3] == "5.50 GB"
    assert rows["thousand"][3] == "1,000 B"
    assert_text_table(path, out, as_json(path, "--sort", "name"), False, "name")


def test_a_tie_of_the_shortest_decimal_rounds_up_although_the_float_is_below_it(tmp_path, tables, as_json):
    # The texts are those of formatBytes and formatShare of the views, run in node with the locale en.
    path = write_footprint(tmp_path / "shortest.pqax", [
        row(1, "shortbytes", 1312, 0, 5, 1, deletedRowCount=1), row(2, "shortshare", 1997, 3, 10, 1),
        row(3, "shortshare2", 1977, 23, 10, 1)])
    code, out, err = tables(path, "--sort", "name")
    assert code == 0, out + err
    rows = {cells_[0]: cells_ for cells_ in map(split, lines(out)[2:])}
    # The deleted rows are 1312 * 1 / 5 = 262.4 B, the live bytes 1049.6 B: 1.025 KB as the shortest decimal,
    # 1.02499999999999991... as the float.
    assert rows["shortbytes"][3] == "1.03 KB"
    assert rows["shortbytes"][6] == "262 B"
    assert rows["shortbytes"][8] == "262 B"
    assert rows["shortbytes"][9] == "20.0%"
    # 3 / 2000 * 100 is 0.15 as the shortest decimal, 0.14999999999999999... as the float.
    assert rows["shortshare"][9] == "0.2%"
    # 23 / 2000 * 100 is 1.15 as the shortest decimal, 1.14999999999999991... as the float.
    assert rows["shortshare2"][9] == "1.2%"
    assert_text_table(path, out, as_json(path, "--sort", "name"), False, "name")


def test_wide_writes_a_lower_bound_from_the_quartiles_with_its_sign(tmp_path, tables, as_json):
    sizes = {"count": 4, "sum": 400 * MIB, "minimum": 1 * MIB, "firstQuartile": 4 * MIB, "median": 9 * MIB,
             "thirdQuartile": 100 * MIB, "maximum": 200 * MIB}
    path = write_footprint(tmp_path / "quartiles.pqax", [row(1, "quartiles", 400 * MIB, 0, 10, 4, fileSizes=sizes),
                                                          row(2, "none", 10, 0, 1, 1)])
    code, out, err = tables(path, "--wide", "--sort", "name")
    assert code == 0, out + err
    rows = {cells_[0]: cells_ for cells_ in map(split, lines(out)[2:])}
    assert rows["quartiles"][-4:] == [">=25.0%", ">=75.0%", "0 B", "n/a"]
    assert rows["none"][-4:] == ["n/a", "n/a", "0 B", "n/a"]
    assert_text_table(path, out, as_json(path, "--wide", "--sort", "name"), True, "name")


def test_a_files_table_that_cannot_be_read_is_a_warning_and_the_summary_is_used(tmp_path, tables, as_json):
    path = tmp_path / "broken.pqax"
    with zipfile.ZipFile(sample("sample")) as source, zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as target:
        for info in source.infolist():
            data = b'{"files": {"id": [1, 2,' if info.filename == "files.json" else source.read(info.filename)
            target.writestr(info.filename, data)
    code, out, err = tables(path, "--json")
    assert code == 0, out + err
    assert err.strip()
    document = json.loads(out)
    assert document["filesRead"] is False
    assert document["warnings"]
    expected = load_json(MEASURES / "summary-only.json")["tables"]
    assert same_tables(document["tables"], cli_shape(sorted_tables(expected, "waste")))
    code, out, err = tables(path)
    assert code == 0
    assert err.strip()
    assert len(lines(out)) == 4
