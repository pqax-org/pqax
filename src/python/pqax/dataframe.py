"""Loading a table into a data frame of pandas or polars, which are optional: the package itself needs neither."""
from __future__ import annotations

from typing import Any, Dict, List, Mapping, Optional

_LIBRARIES = ("pandas", "polars")


def _import(library: str) -> Any:
    try:
        if library == "pandas":
            import pandas
            return pandas
        import polars
        return polars
    except ImportError as error:
        raise ImportError(f"to_dataframe(library={library!r}) needs {library}: pip install {library}") from error


def _pandas_column(pandas: Any, values: List[Any]) -> Any:
    """A column with the type of its values: a nullable integer or boolean when some values are null."""
    kinds = set(map(type, values))
    has_null = type(None) in kinds
    kinds.discard(type(None))
    if kinds == {int} and has_null:
        return pandas.array(values, dtype="Int64")
    if kinds == {bool} and has_null:
        return pandas.array(values, dtype="boolean")
    if kinds and kinds <= {int, float, bool, str}:
        return values
    return pandas.Series(values, dtype=object)


def _to_pandas(pandas: Any, table: Any) -> Any:
    if not isinstance(table, Mapping):
        return pandas.DataFrame.from_records(list(table))
    columns: Dict[str, Any] = {name: _pandas_column(pandas, list(values)) for name, values in table.items()}
    return pandas.DataFrame(columns)


def _to_polars(polars: Any, table: Any) -> Any:
    if not isinstance(table, Mapping):
        return polars.DataFrame(list(table), strict=False, infer_schema_length=None)
    return polars.DataFrame({name: list(values) for name, values in table.items()}, strict=False)


def to_dataframe(table: Any, library: Optional[str] = None) -> Any:
    """A data frame with the rows of a table, as doc.table() or doc.summary[name] return it.

    library is "pandas" or "polars". When it is None, pandas is used when it is installed, and polars otherwise.
    With pandas, an integer or boolean column with null values gets a nullable type, so that it does not turn into
    floats, and a column of lists or objects keeps them as they are. Timestamps stay strings: pqax.parse_timestamp
    reads one. The table can also be an iterable of rows, as doc.rows() returns them.
    """
    if library is None:
        for name in _LIBRARIES:
            try:
                module = _import(name)
            except ImportError:
                continue
            return _to_pandas(module, table) if name == "pandas" else _to_polars(module, table)
        raise ImportError("to_dataframe needs pandas or polars, and neither is installed: pip install pandas, or pip "
                          "install polars")
    if library not in _LIBRARIES:
        raise ValueError(f"library is 'pandas' or 'polars', not {library!r}")
    module = _import(library)
    return _to_pandas(module, table) if library == "pandas" else _to_polars(module, table)
