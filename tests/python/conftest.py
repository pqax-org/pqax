"""pytest setup for the tests of the Python package pqax.

Run from the root of the repository: python -m pytest tests/python -q
Leave out the test of 600,000 rows with: python -m pytest tests/python -q -m "not large"
"""
from __future__ import annotations

import os
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import pqax_kit  # noqa: E402

try:
    import pqax  # noqa: F401
except ImportError:  # not installed: use the sources of the repository
    SOURCES = str(pqax_kit.ROOT / "src" / "python")
    sys.path.insert(0, SOURCES)
    # The processes of test_cli.py, python -m pqax, find the same sources.
    os.environ["PYTHONPATH"] = os.pathsep.join(filter(None, (SOURCES, os.environ.get("PYTHONPATH"))))


def pytest_configure(config):
    config.addinivalue_line("markers", "large: a footprint of 600,000 rows, which takes a few seconds")
