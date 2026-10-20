"""Checks of the conformance kit itself, with the standard library only and without pqax.

When a test of a reader fails, these tests tell whether the kit agrees with its own samples: if they pass, the
expected results are what the files hold, and the reader is the one to look at.
"""
from __future__ import annotations

import io
import zipfile

import pytest

from pqax_kit import (EXPECTED, INVALID, PLAN_CODES, SAMPLES, SHAPE_KEYS, compute_shape, expected_paths,
                      footprint_bytes, invalid_cases, load_json, read_plain, row_total, rule_codes, schema_errors,
                      schema_validators, shape_differences)

EXPECTED_FILES = expected_paths()


def test_the_kit_has_samples_and_expected_results():
    assert EXPECTED_FILES, f"no expected results in {EXPECTED}"
    samples = sorted(path.name for path in SAMPLES.iterdir() if path.suffix in (".pqax", ".vpax"))
    described = sorted(load_json(path)["file"] for path in EXPECTED_FILES)
    assert samples == described, "every valid sample has one expected file, and every expected file one sample"


@pytest.mark.parametrize("path", EXPECTED_FILES, ids=lambda path: path.stem)
def test_expected_results_agree_with_the_sample(path):
    expected = load_json(path)
    sample = SAMPLES / expected["file"]
    assert sample.is_file()
    manifest, tables = read_plain(footprint_bytes(sample))

    def table(name):
        return tables.get(name, {})

    def row_count(name):
        return row_total(table(name))

    shape = compute_shape(manifest, row_count, table)
    assert not shape_differences(shape, expected, SHAPE_KEYS)
    for name, count in expected["rows"].items():
        assert row_total(table(name)) == count, name


@pytest.mark.parametrize("path", [path for path in EXPECTED_FILES if load_json(path)["embeddedPart"]],
                         ids=lambda path: path.stem)
def test_embedded_part_is_written_as_expected(path):
    expected = load_json(path)
    with zipfile.ZipFile(SAMPLES / expected["file"]) as archive:
        info = archive.getinfo(expected["embeddedPart"])
        method = {zipfile.ZIP_STORED: "stored", zipfile.ZIP_DEFLATED: "deflated"}[info.compress_type]
        assert method == expected["embeddedCompression"]
        assert archive.read(info) == (SAMPLES / "sample.pqax").read_bytes()


def test_invalid_expectations_cover_every_invalid_file_with_known_codes():
    cases = invalid_cases()
    files = sorted(path.name for path in INVALID.iterdir() if path.suffix in (".pqax", ".vpax"))
    assert sorted(cases) == files
    codes = rule_codes()
    for name, case in cases.items():
        assert case["codes"], name
        assert set(case["codes"]) <= codes, name
        assert case["change"], name


def test_rules_list_the_codes_of_the_plan():
    assert rule_codes() == PLAN_CODES


def test_valid_samples_match_the_schema():
    validators = schema_validators()
    if validators is None:
        pytest.skip("jsonschema 4.18 or later, with referencing, is not installed")
    for path in sorted(SAMPLES.iterdir()):
        if path.suffix in (".pqax", ".vpax"):
            assert schema_errors(footprint_bytes(path), validators) == [], path.name


def test_samples_are_zip_archives_with_a_manifest():
    for path in sorted(SAMPLES.iterdir()):
        if path.suffix in (".pqax", ".vpax"):
            with zipfile.ZipFile(io.BytesIO(footprint_bytes(path))) as archive:
                assert "manifest.json" in archive.namelist(), path.name
