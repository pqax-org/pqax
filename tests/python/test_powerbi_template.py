"""The Power BI template of templates/powerbi, checked as text: no Power BI is needed.

The interface of the export (24 October 2026) says that the template is a PBIP project, PqaxFootprints.pbip, with a
semantic model in TMDL and a report in PBIR, and that four model tables load four output tables of the export with the
column names unchanged: each data column has the name of an output column and the same name as its sourceColumn. Every
column of those output tables is in the model, so that the model and pqax.export.COLUMNS cannot drift apart.

TMDL as read here: `table Name` or `table 'Name with spaces'` at the start of a line, then indented objects;
`column name` opens a column, whose own lines are indented deeper and include `sourceColumn: name`; a column declared
with `=` is calculated and has no source column; `measure 'Name' = ...` is a measure, not a column.
"""
from __future__ import annotations

import importlib
import json
import re

import pytest

from pqax_kit import ROOT

TEMPLATE = ROOT / "templates" / "powerbi"
if not TEMPLATE.is_dir():
    pytest.skip("templates/powerbi does not exist yet", allow_module_level=True)

PROJECT = TEMPLATE / "PqaxFootprints.pbip"
MODEL = TEMPLATE / "PqaxFootprints.SemanticModel"
REPORT = TEMPLATE / "PqaxFootprints.Report"
DEFINITION = MODEL / "definition"
EXPRESSIONS = DEFINITION / "expressions.tmdl"
PARAMETER = "PqaxExportFolder"

# The model tables and the output tables of the export that they load.
MODEL_TABLES = {"Footprints": "footprints", "Tables": "tableMeasures", "Columns": "columnMeasures",
                "Collection": "collectionMeasures"}

NAME = re.compile(r"\s*(?:'((?:[^']|'')*)'|([^\s='\"]+))\s*(=)?")


def object_name(text):
    """The name at the start of text, quoted or not, and whether an = follows it."""
    match = NAME.match(text)
    assert match, f"no name in {text!r}"
    quoted, plain, equals = match.groups()
    return (quoted.replace("''", "'") if quoted is not None else plain), equals is not None


def unquote(value):
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
        return value[1:-1].replace(value[0] * 2, value[0])
    return value


def indentation(line):
    return len(line) - len(line.lstrip(" \t"))


def parse_tables(text):
    """{table name: {"columns": [{"name", "sourceColumn", "calculated"}], "text": the lines of the table}}."""
    tables = {}
    table = None
    column = None
    for line in text.splitlines():
        if not line.strip() or line.lstrip().startswith("//"):
            continue
        depth = indentation(line)
        stripped = line.strip()
        if depth == 0:
            column = None
            table = None
            if stripped.startswith("table "):
                name, _ = object_name(stripped[len("table "):])
                table = tables.setdefault(name, {"columns": [], "text": []})
            continue
        if table is None:
            continue
        table["text"].append(line)
        if column is not None and depth <= column["depth"]:
            column = None
        if stripped.startswith("column "):
            name, calculated = object_name(stripped[len("column "):])
            column = {"name": name, "sourceColumn": None, "calculated": calculated, "depth": depth}
            table["columns"].append(column)
        elif column is not None and stripped.startswith("sourceColumn:"):
            column["sourceColumn"] = unquote(stripped[len("sourceColumn:"):])
    return tables


@pytest.fixture(scope="module")
def model():
    """Every table of every TMDL file of the model."""
    tables = {}
    files = sorted(DEFINITION.rglob("*.tmdl"))
    assert files, f"no TMDL file under {DEFINITION}"
    for path in files:
        for name, table in parse_tables(path.read_text(encoding="utf-8-sig")).items():
            assert name not in tables, f"the table {name} is defined twice"
            tables[name] = table
    return tables


@pytest.fixture(scope="module")
def columns():
    return importlib.import_module("pqax.export").COLUMNS


def data_columns(table):
    return [column for column in table["columns"] if not column["calculated"]]


def test_the_parser_reads_the_tmdl_syntax():
    tables = parse_tables(
        "table 'Table Measures'\n"
        "\tlineageTag: 1\n"
        "\n"
        "\tmeasure 'Waste share' = DIVIDE(SUM('Table Measures'[waste]), SUM('Table Measures'[total]))\n"
        "\t\tformatString: 0.0%\n"
        "\n"
        "\tcolumn footprintId\n"
        "\t\tdataType: string\n"
        "\t\tsourceColumn: footprintId\n"
        "\n"
        "\tcolumn 'it''s'\n"
        "\t\tsourceColumn: 'it''s'\n"
        "\n"
        "\tcolumn Year = YEAR([collectedAt])\n"
        "\n"
        "\tpartition 'Table Measures' = m\n"
        "\t\tmode: import\n"
        "\n"
        "table Date\n"
        "\tcolumn Date\n"
        "\t\tsourceColumn: [Date]\n")
    assert set(tables) == {"Table Measures", "Date"}
    assert [(c["name"], c["sourceColumn"], c["calculated"]) for c in tables["Table Measures"]["columns"]] == [
        ("footprintId", "footprintId", False), ("it's", "it's", False), ("Year", None, True)]
    assert tables["Date"]["columns"][0]["sourceColumn"] == "[Date]"


@pytest.mark.parametrize("path", [PROJECT, REPORT / "definition.pbir", MODEL / "definition.pbism"],
                         ids=["pbip", "pbir", "pbism"])
def test_the_files_of_the_project_are_valid_json(path):
    assert path.is_file(), f"{path.relative_to(TEMPLATE)} is missing"
    json.loads(path.read_text(encoding="utf-8-sig"))


def test_every_json_file_of_the_template_is_valid_json():
    files = sorted(TEMPLATE.rglob("*.json"))
    for path in files:
        try:
            json.loads(path.read_text(encoding="utf-8-sig"))
        except ValueError as error:
            pytest.fail(f"{path.relative_to(TEMPLATE)}: {error}")


def test_the_report_and_the_model_have_their_definition_folders():
    assert DEFINITION.is_dir(), "the semantic model has no definition folder"
    assert (REPORT / "definition").is_dir(), "the report has no definition folder"


@pytest.mark.parametrize("table", [*MODEL_TABLES, "Date"])
def test_the_model_has_the_table(table, model):
    assert table in model, f"the model has the tables {sorted(model)}"


@pytest.mark.parametrize("table", MODEL_TABLES)
def test_each_data_column_has_the_name_and_the_source_of_an_output_column(table, model, columns):
    output = set(columns[MODEL_TABLES[table]])
    found = data_columns(model[table])
    assert found, f"{table} has no data column"
    for column in found:
        assert column["sourceColumn"] is not None, f"{table}[{column['name']}] has no sourceColumn"
        assert column["name"] == column["sourceColumn"], f"{table}[{column['name']}] loads {column['sourceColumn']}"
        assert column["sourceColumn"] in output, f"{table}[{column['name']}] is not a column of {MODEL_TABLES[table]}"


@pytest.mark.parametrize("table", MODEL_TABLES)
def test_every_column_of_the_output_table_is_in_the_model(table, model, columns):
    loaded = {column["sourceColumn"] for column in data_columns(model[table])}
    missing = [name for name in columns[MODEL_TABLES[table]] if name not in loaded]
    assert not missing, f"{table} lacks the columns {missing} of {MODEL_TABLES[table]}"


@pytest.mark.parametrize("table", MODEL_TABLES)
def test_each_table_loads_its_output_table(table, model):
    text = "\n".join(model[table]["text"])
    assert re.search(r"^\s*partition\s", text, flags=re.MULTILINE), f"{table} has no partition"
    assert MODEL_TABLES[table] in text, f"the partition of {table} does not name {MODEL_TABLES[table]}"


def test_date_is_a_calculated_table(model):
    text = "\n".join(model["Date"]["text"])
    assert re.search(r"^\s*partition\s.*=\s*calculated\s*$", text, flags=re.MULTILINE), text


def test_the_parameter_of_the_folder_is_in_expressions_tmdl():
    assert EXPRESSIONS.is_file(), "expressions.tmdl is missing"
    text = EXPRESSIONS.read_text(encoding="utf-8-sig")
    match = re.search(rf"^expression\s+(?:'{PARAMETER}'|{PARAMETER})\s*=", text, flags=re.MULTILINE)
    assert match, f"expressions.tmdl does not define {PARAMETER}"
    following = re.search(r"^\S", text[match.end():], flags=re.MULTILINE)
    block = text[match.start():match.end() + (following.start() if following else len(text))]
    assert re.search(r"IsParameterQuery\s*=\s*true", block, flags=re.IGNORECASE), block


def test_the_parameter_is_used_by_the_queries():
    uses = 0
    for path in sorted(DEFINITION.rglob("*.tmdl")):
        text = path.read_text(encoding="utf-8-sig")
        text = re.sub(rf"^expression\s+(?:'{PARAMETER}'|{PARAMETER})\s*=.*$", "", text, flags=re.MULTILINE)
        uses += len(re.findall(rf"\b{PARAMETER}\b", text))
    assert uses, f"no query reads {PARAMETER}"
