"""pqax.to_dataframe: pandas when installed, else polars, and ImportError when neither is (plan 4.5)."""
from __future__ import annotations

import importlib.util
import sys

import pytest

import pqax
from pqax_kit import SAMPLES

HAS_PANDAS = importlib.util.find_spec("pandas") is not None
HAS_POLARS = importlib.util.find_spec("polars") is not None


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
