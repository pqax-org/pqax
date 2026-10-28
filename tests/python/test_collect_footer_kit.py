"""The kit of footers of other writers, as conformance/footers.md and its README describe it: conformance/samples/footers holds
Parquet files and expected.json, one member per file, with writer, what, statedSize (null for the size of the file),
includeDataValues, and either error (code, message) or footer (numRows, rowGroups, createdBy, leaves, columns).

A member with error is read with FooterReader and its stated size, and must give that code and message; a member with
footer must give its rows, its row groups, its writer, and its leaves, and a folder of Parquet files that holds a copy
of the file alone must give its columns, with the ordinals of the leaves whose physical column refers to each.
FooterKitTests.cs of the C# tests reads the same members. When the kit is missing, the tests fail and say so: they do
not skip. Standard library only.
"""
from __future__ import annotations

import json
from typing import Any, Dict, List, Optional, Tuple

import pytest

from collect_kit import Container, assert_valid, collect_bytes, of_table, open_bytes, rows
from pqax.collect import FooterError, FooterReader, LocalStorage, discover_folder
from pqax_kit import KIT

FOLDER = KIT / "samples" / "footers"
EXPECTED = FOLDER / "expected.json"
NO_KIT = "(no kit)"


def load_expected() -> Tuple[Optional[Dict[str, Any]], str]:
    """expected.json, or None and the reason why it cannot be read."""
    if not FOLDER.is_dir():
        return None, f"The folder of the kit of footers, {FOLDER}, does not exist."
    if not EXPECTED.is_file():
        return None, f"The kit of footers has no expected.json: {EXPECTED}."
    try:
        expected = json.loads(EXPECTED.read_text(encoding="utf-8"))
    except ValueError as error:
        return None, f"expected.json of the kit of footers is not valid JSON: {error}"
    if not isinstance(expected, dict):
        return None, "expected.json of the kit of footers is not a JSON object with one member per file."
    return expected, ""


def members() -> List[str]:
    """The members of expected.json, or one that fails the test with the reason when the kit is missing: never none,
    which would skip."""
    expected, problem = load_expected()
    if expected is None:
        return [f"{NO_KIT} {problem}"]
    names = [name for name, member in expected.items() if isinstance(member, dict)]
    return names or [f"{NO_KIT} expected.json of the kit of footers has no member."]


def file_of(member: str) -> Optional[str]:
    """The file of a member: its name, or its name with .parquet."""
    for name in (member, member + ".parquet"):
        if name.lower().endswith(".parquet") and (FOLDER / name).is_file():
            return name
    return None


def test_the_kit_has_an_expected_json_with_one_member_for_each_parquet_file():
    expected, problem = load_expected()
    assert expected is not None, problem
    names = [name for name, member in expected.items() if isinstance(member, dict)]
    assert names, "expected.json of the kit of footers has no member."
    for name in names:
        assert file_of(name) is not None, f"The member {name} of expected.json has no file in {FOLDER}."
        # Either error or footer: the other is absent or null.
        assert isinstance(expected[name].get("error"), dict) != isinstance(expected[name].get("footer"), dict), \
            f"{name}: a member has either error or footer."
    for path in sorted(FOLDER.glob("*.parquet")):
        assert any(file_of(name) == path.name for name in names), f"{path.name} has no member in expected.json."


def only(items: Any, *names: str) -> Any:
    """The members of a list of objects that the kit compares, so that a member of the kit for people does not count."""
    if not isinstance(items, list):
        return items
    return [{name: item.get(name) for name in names} if isinstance(item, dict) else item for item in items]


def canonical(value: Any) -> str:
    return json.dumps(value, sort_keys=True, ensure_ascii=False)


def assert_same(what: str, member: str, expected: Any, actual: Any) -> None:
    assert canonical(actual) == canonical(expected), f"{what}, {member}"


@pytest.mark.parametrize("member", members())
def test_every_member_of_the_kit_reads_as_expected_json_says(tmp_path, member):
    assert not member.startswith(NO_KIT), member
    expected_all, _ = load_expected()
    entry = expected_all[member]
    name = file_of(member)
    assert name is not None, f"The member {member} of expected.json has no file in {FOLDER}."
    stated = entry.get("statedSize")
    size = stated if stated is not None else (FOLDER / name).stat().st_size
    include = bool(entry.get("includeDataValues") or False)
    reader = FooterReader(LocalStorage(str(FOLDER)), include_data_values=include)
    what = f"{member} ({entry.get('writer')}: {entry.get('what')})"

    error = entry.get("error")
    if error is not None:
        try:
            reader.read(name, size)
        except FooterError as refused:
            assert (refused.code, str(refused)) == (error.get("code"), error.get("message")), what
        else:
            pytest.fail(f"{what}: no footer error, expected {error.get('code')}: {error.get('message')}")
        return

    expected = entry["footer"]
    try:
        footer = reader.read(name, size)
    except FooterError as refused:
        pytest.fail(f"{what}: {refused.code}: {refused}")
    assert_same(what, "numRows", expected.get("numRows"), footer.num_rows)
    groups = expected.get("rowGroups")
    assert_same(what, "rowGroups", len(groups) if isinstance(groups, list) else groups, len(footer.row_groups))
    assert_same(what, "createdBy", expected.get("createdBy"), footer.created_by)
    leaves = [{"path": list(leaf.path), "physicalType": leaf.physical_type, "typeAnnotation": leaf.type_annotation,
               "repetition": leaf.repetition} for leaf in footer.leaves]
    assert_same(what, "leaves", only(expected.get("leaves"), "path", "physicalType", "typeAnnotation", "repetition"),
                leaves)

    # The columns of a folder of Parquet files that holds a copy of the file alone.
    lake = Container(tmp_path / "kit")
    lake.write("data/kit/part-00000.parquet", (FOLDER / name).read_bytes())
    storage = LocalStorage(str(lake.root))
    [location] = discover_folder(storage, "data/kit")
    data, _ = collect_bytes(storage, [location], checkpoint_reader=None, include_data_values=include)
    assert_valid(data)
    doc = open_bytes(data)
    [table] = rows(doc, "tables")
    schemas = {schema["id"] for schema in of_table(doc, "physicalSchemas", table["id"])}
    physical = [row for row in rows(doc, "physicalColumns") if row["physicalSchema"] in schemas]
    columns = [{"name": column["name"], "dataType": column["dataType"], "isNullable": column.get("isNullable"),
                "leaves": sorted(p["ordinal"] for p in physical if p.get("column") == column["id"])}
               for column in of_table(doc, "columns", table["id"])]
    wanted = only(expected.get("columns"), "name", "dataType", "isNullable", "leaves")
    if isinstance(wanted, list):
        for column in wanted:
            if isinstance(column, dict) and isinstance(column.get("leaves"), list):
                column["leaves"] = sorted(column["leaves"])
    assert_same(what, "columns", wanted, columns)
