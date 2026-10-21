"""The validator of pqax against the conformance kit: conformance/README.md, "A validator test", and plan 4.1."""
from __future__ import annotations

import json

import pytest

import pqax
from pqax_kit import (EMBEDDED_PART, INVALID, SAMPLES, compact, drop_entry, expected_by_name, invalid_cases,
                      rewrite_zip, rule_codes, write_package_without_footprint)

EXPECTED = expected_by_name()
CASES = invalid_cases()
CODES = rule_codes()


def validate_sample(path):
    """Validates a sample: a VPAX through the footprint inside it, which is the PQAX file."""
    if path.suffix == ".vpax":
        return pqax.validate_embedded(str(path), EMBEDDED_PART)
    return pqax.validate(str(path))


def codes_of(result):
    return {issue.code for issue in result.issues}


@pytest.mark.parametrize("name", sorted(EXPECTED))
def test_a_valid_sample_has_no_issue(name):
    result = validate_sample(SAMPLES / EXPECTED[name]["file"])
    assert [f"{issue.code}: {issue.message}" for issue in result.issues] == []
    assert result.is_valid
    assert result.checks > 0


@pytest.mark.parametrize("name", sorted(CASES))
def test_an_invalid_sample_reports_at_least_its_codes(name):
    result = validate_sample(INVALID / name)
    missing = set(CASES[name]["codes"]) - codes_of(result)
    assert not missing, f"{name}: missing {sorted(missing)}, reported {sorted(codes_of(result))}"
    assert not result.is_valid


@pytest.mark.parametrize("name", sorted(CASES))
def test_every_code_reported_is_a_code_of_the_kit(name):
    result = validate_sample(INVALID / name)
    assert codes_of(result) <= CODES, sorted(codes_of(result) - CODES)


def test_an_issue_has_a_code_a_message_and_a_place():
    result = pqax.validate(str(INVALID / "part.rowCount.array.pqax"))
    assert result.issues
    for issue in result.issues:
        assert isinstance(issue.message, str) and issue.message
        for place in (issue.part, issue.table, issue.field):
            assert place is None or isinstance(place, str)
    issue = next(issue for issue in result.issues if issue.code == "part.rowCount")
    assert issue.part == "commits.json"
    assert issue.table == "commits"


def test_a_file_object_gives_the_same_issues_as_a_path():
    path = INVALID / "totals.fileSize.pqax"
    with open(path, "rb") as handle:
        from_file = pqax.validate(handle)
    from_path = pqax.validate(str(path))
    assert codes_of(from_file) == codes_of(from_path) == {"totals.fileSize"}


def test_the_validator_reports_every_issue_and_not_only_the_first(tmp_path):
    def change(name, data):
        content = json.loads(data)
        if name == "summary.json":
            content["tables"]["fileCount"][0] += 1
            return compact(content)
        if name == "files.json":
            content["files"]["modifiedAt"][0] = "2023-11-14T22:15:00Z"
            return compact(content)
        if name == "commits.json":
            content["commits"]["commitId"][2] = None
            return compact(content)
        return None

    target = tmp_path / "three-issues.pqax"
    rewrite_zip(SAMPLES / "sample.pqax", target, change)
    codes = codes_of(pqax.validate(str(target)))
    assert {"totals.fileCount", "json.timestamp", "table.required"} <= codes


def test_a_byte_order_mark_is_reported_and_the_part_is_read_on():
    codes = codes_of(pqax.validate(str(INVALID / "json.bom.pqax")))
    assert "json.bom" in codes
    assert "json.invalid" not in codes


def test_a_file_that_is_not_a_zip_archive_is_reported(tmp_path):
    target = tmp_path / "text.pqax"
    target.write_text("not a zip archive", encoding="utf-8")
    result = pqax.validate(str(target))
    assert not result.is_valid
    assert "container.manifestMissing" in codes_of(result)


def test_a_package_without_the_footprint_is_reported(tmp_path):
    package = tmp_path / "no-footprint.vpax"
    write_package_without_footprint(package)
    result = pqax.validate_embedded(str(package), EMBEDDED_PART)
    assert not result.is_valid
    assert "embedded.partMissing" in codes_of(result)


def test_a_summary_part_that_is_missing_is_reported(tmp_path):
    target = tmp_path / "no-summary.pqax"
    drop_entry(SAMPLES / "sample.pqax", target, "summary.json")
    result = pqax.validate(str(target))
    assert "manifest.partMissing" in codes_of(result)


def test_a_rewritten_valid_sample_stays_valid(tmp_path):
    """A copy whose entries are written again by another zip writer, with the same content, is still valid."""
    target = tmp_path / "copy.pqax"
    rewrite_zip(SAMPLES / "sample.pqax", target, lambda name, data: None)
    result = pqax.validate(str(target))
    assert result.is_valid, [issue.code for issue in result.issues]


def test_validating_twice_gives_the_same_result():
    first = pqax.validate(str(INVALID / "table.reference.list.pqax"))
    second = pqax.validate(str(INVALID / "table.reference.list.pqax"))
    assert [(issue.code, issue.message) for issue in first.issues] == \
           [(issue.code, issue.message) for issue in second.issues]
    assert first.checks == second.checks


# ---- the fields of format 0.2 ----

@pytest.mark.parametrize("name", ["sample.pqax", "format-0.1.pqax"])
def test_a_sample_of_format_0_2_and_one_of_format_0_1_are_valid(name):
    result = pqax.validate(str(SAMPLES / name))
    assert [f"{issue.code}: {issue.message}" for issue in result.issues] == []
    assert result.is_valid


@pytest.mark.parametrize("name", ["json.type.unreferencedFileCount.pqax", "json.timestamp.nonLiveOldestRemovedAt.pqax",
                                  "json.timestamp.partitionLastModifiedAt.pqax"])
def test_the_invalid_samples_of_the_fields_of_format_0_2_report_their_codes(name):
    assert name in CASES
    result = pqax.validate(str(INVALID / name))
    assert set(CASES[name]["codes"]) <= codes_of(result)


WRONG_VALUES = [
    ("summary.json", "tables", "unreferencedFileCount", "1", "json.type"),
    ("summary.json", "tables", "unreferencedFileCount", True, "json.type"),
    ("summary.json", "tables", "unreferencedFileSize", "737", "json.type"),
    ("summary.json", "tables", "unreferencedFileSize", [737], "json.type"),
    ("summary.json", "tables", "nonLiveOldestRemovedAt", 1700000050000, "json.type"),
    ("summary.json", "tables", "nonLiveNewestRemovedAt", {"at": "2023-11-14T22:14:10.000Z"}, "json.type"),
    ("partitions.json", "partitions", "lastModifiedAt", 1700000100000, "json.type"),
    ("summary.json", "tables", "nonLiveOldestRemovedAt", "2023-11-14T22:14:10Z", "json.timestamp"),
    ("summary.json", "tables", "nonLiveNewestRemovedAt", "2023-11-14 22:14:10.000", "json.timestamp"),
    ("partitions.json", "partitions", "lastModifiedAt", "2023-11-14T22:15:00.000+00:00", "json.timestamp"),
]


@pytest.mark.parametrize("part,table,field,value,code", WRONG_VALUES,
                         ids=[f"{field}-{code}-{index}" for index, (_, _, field, _, code) in enumerate(WRONG_VALUES)])
def test_a_wrong_value_in_a_field_of_format_0_2_is_reported_with_its_table_and_field(tmp_path, part, table, field,
                                                                                        value, code):
    def change(name, data):
        if name != part:
            return None
        content = json.loads(data)
        content[table][field][0] = value
        return compact(content)

    target = tmp_path / "wrong.pqax"
    rewrite_zip(SAMPLES / "sample.pqax", target, change)
    result = pqax.validate(str(target))
    assert not result.is_valid
    matching = [issue for issue in result.issues if issue.code == code]
    assert matching, [f"{issue.code}: {issue.message}" for issue in result.issues]
    assert any(issue.table == table and issue.field == field and issue.part == part for issue in matching),         [(issue.part, issue.table, issue.field) for issue in matching]
