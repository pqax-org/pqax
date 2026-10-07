"""The command line, python -m pqax check|info, through a process: exit codes 0, 1, and 2, and what it prints."""
from __future__ import annotations

import re
import subprocess
import sys

import pytest

from pqax_kit import INVALID, ROOT, SAMPLES, code_pattern, expected_by_name, invalid_cases

EXPECTED = expected_by_name()
CASES = invalid_cases()


def run(*arguments):
    completed = subprocess.run([sys.executable, "-m", "pqax", *map(str, arguments)], cwd=ROOT, capture_output=True,
                               text=True, encoding="utf-8", errors="replace", timeout=120)
    return completed.returncode, completed.stdout, completed.stderr


@pytest.mark.parametrize("name", sorted(EXPECTED))
def test_check_exits_with_0_on_a_valid_sample(name):
    code, out, err = run("check", SAMPLES / EXPECTED[name]["file"])
    assert code == 0, out + err
    for issue_code in {code for case in CASES.values() for code in case["codes"]}:
        assert not code_pattern(issue_code).search(out), issue_code


@pytest.mark.parametrize("name", sorted(CASES))
def test_check_exits_with_1_and_prints_the_codes_of_an_invalid_sample(name):
    code, out, err = run("check", INVALID / name)
    assert code == 1, out + err
    lines = out.splitlines()
    for issue_code in CASES[name]["codes"]:
        assert any(code_pattern(issue_code).search(line) for line in lines), f"{issue_code} not in:\n{out}"


def test_check_prints_one_line_for_each_issue():
    code, out, _ = run("check", INVALID / "totals.fileSize.pqax")
    assert code == 1
    assert len([line for line in out.splitlines() if code_pattern("totals.fileSize").search(line)]) == 1


@pytest.mark.parametrize("arguments", [(), ("check",), ("unknown", "file.pqax"), ("check", "--no-such-option", "x")],
                         ids=["nothing", "no file", "unknown command", "unknown option"])
def test_a_wrong_command_line_exits_with_2(arguments):
    code, _, _ = run(*arguments)
    assert code == 2


def test_a_file_that_does_not_exist_exits_with_2(tmp_path):
    code, _, err = run("check", tmp_path / "missing.pqax")
    assert code == 2
    assert err.strip()
    code, _, _ = run("info", tmp_path / "missing.pqax")
    assert code == 2


@pytest.mark.parametrize("name", ["sample", "multipart", "extensions", "summary-only", "embedded-deflated"])
def test_info_prints_the_parts_and_the_rows(name):
    expected = EXPECTED[name]
    code, out, err = run("info", SAMPLES / expected["file"])
    assert code == 0, out + err
    for part in expected["parts"]:
        assert re.search(r"(?<![\w.])" + re.escape(part) + r"(?![\w.])", out), part
    for table, rows in expected["rows"].items():
        pattern = r"(?<![\w.])" + re.escape(table) + r"\W+" + f"{rows:,}".replace(",", ",?") + r"(?!\d|,\d)"
        assert re.search(pattern, out), f"{table} {rows} not in:\n{out}"


def test_info_on_a_file_that_a_reader_refuses_fails():
    code, _, err = run("info", INVALID / "manifest.versionUnknown.pqax")
    assert code != 0
    assert err.strip()


def test_check_of_a_vpax_checks_the_embedded_footprint():
    code, out, _ = run("check", SAMPLES / "embedded-stored.vpax", "--part", "Footprint.pqax")
    assert code == 0, out
