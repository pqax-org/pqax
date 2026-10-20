"""pqax.to_dataframe: pandas when installed, else polars, and ImportError when neither is (plan 4.5)."""
from __future__ import annotations

import importlib.util
import sys
import types

import pytest

import pqax
from pqax_kit import SAMPLES, expected_by_name

HAS_PANDAS = importlib.util.find_spec("pandas") is not None
HAS_POLARS = importlib.util.find_spec("polars") is not None
LIBRARIES = [pytest.param("pandas", marks=pytest.mark.skipif(not HAS_PANDAS, reason="pandas is not installed")),
             pytest.param("polars", marks=pytest.mark.skipif(not HAS_POLARS, reason="polars is not installed"))]


@pytest.fixture(scope="module")
def files():
    with pqax.open(str(SAMPLES / "sample.pqax")) as doc:
        return doc.table("files")


@pytest.mark.skipif(not (HAS_PANDAS or HAS_POLARS), reason="neither pandas nor polars is installed")
def test_the_default_library_gives_a_frame_with_every_row_and_field(files):
    frame = pqax.to_dataframe(files)
    assert frame.shape == (5, len(files))
    assert list(frame.columns) == list(files)
    module = type(frame).__module__.split(".")[0]
    assert module == ("pandas" if HAS_PANDAS else "polars")


@pytest.mark.skipif(not HAS_PANDAS, reason="pandas is not installed")
def test_pandas_keeps_integers_with_missing_values_as_integers(files):
    import pandas

    frame = pqax.to_dataframe(files, library="pandas")
    assert isinstance(frame, pandas.DataFrame)
    assert frame["fileSize"].tolist() == files["fileSize"]
    sizes = frame["deletionVectorSize"]
    assert pandas.api.types.is_integer_dtype(sizes.dtype), sizes.dtype
    assert sizes.isna().tolist() == [False, True, True, True, True]
    assert int(sizes.iloc[0]) == 40
    assert pandas.api.types.is_bool_dtype(frame["isVOrdered"].dtype), frame["isVOrdered"].dtype
    assert frame["path"].tolist() == files["path"]


@pytest.mark.skipif(not HAS_POLARS, reason="polars is not installed")
def test_polars_gives_a_polars_frame(files):
    import polars

    frame = pqax.to_dataframe(files, library="polars")
    assert isinstance(frame, polars.DataFrame)
    assert frame.height == 5
    assert frame["fileSize"].to_list() == files["fileSize"]
    assert frame["deletionVectorSize"].to_list() == [40, None, None, None, None]


def test_without_pandas_and_polars_to_dataframe_raises_import_error(files, monkeypatch):
    for name in list(sys.modules):
        if name.split(".")[0] in ("pandas", "polars"):
            monkeypatch.delitem(sys.modules, name)
    monkeypatch.setitem(sys.modules, "pandas", None)
    monkeypatch.setitem(sys.modules, "polars", None)
    with pytest.raises(ImportError):
        pqax.to_dataframe(files)


@pytest.mark.parametrize("library", LIBRARIES)
def test_every_table_of_every_sample_gives_a_frame_and_its_rows_the_same_frame(library):
    for name, expected in sorted(expected_by_name().items()):
        path = str(SAMPLES / expected["file"])
        part = expected["embeddedPart"]
        with (pqax.open_embedded(path, part) if part else pqax.open(path)) as doc:
            for table_name in doc.table_names:
                where = f"{name}, {table_name}"
                table = doc.table(table_name)
                rows = list(doc.rows(table_name))
                frame = pqax.to_dataframe(table, library=library)
                assert frame.shape == (len(rows), len(table)), where
                assert list(frame.columns) == list(table), where
                from_rows = pqax.to_dataframe(rows, library=library)
                if library == "pandas":
                    import pandas

                    pandas.testing.assert_frame_equal(from_rows, frame, obj=where)
                else:
                    assert from_rows.equals(frame), where


def test_a_polars_before_1_0_is_refused(files, monkeypatch):
    monkeypatch.setitem(sys.modules, "polars", types.SimpleNamespace(__version__="0.20.31"))
    with pytest.raises(ImportError, match="polars 1.0 or later"):
        pqax.to_dataframe(files, library="polars")


def test_a_row_that_is_not_a_mapping_is_refused(monkeypatch):
    monkeypatch.setitem(sys.modules, "pandas", types.SimpleNamespace())  # refused before pandas is used
    with pytest.raises(TypeError):
        pqax.to_dataframe([(1, 2)], library="pandas")
