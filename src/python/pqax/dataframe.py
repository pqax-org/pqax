"""Loading a table into a data frame of pandas or polars, which are optional: the package itself needs neither."""
from __future__ import annotations

import importlib
from typing import Any, Dict, Iterable, List, Mapping, Optional, Tuple

_LIBRARIES = ("pandas", "polars")
# polars 0.20 refuses strict=False, or, in its last releases, gives a column of integers and fractions, or of integers
# and strings, the type of its first values and loses the others. polars 1.0 keeps every value.
_POLARS_MAJOR = 1


def _import(library: str) -> Any:
    try:
        module = importlib.import_module(library)
    except ImportError as error:
        raise ImportError(f"to_dataframe(library={library!r}) needs {library}: pip install {library}") from error
    if library == "polars":
        version = str(getattr(module, "__version__", ""))
        major = version.split(".", 1)[0]
        if major.isdigit() and int(major) < _POLARS_MAJOR:
            raise ImportError(f"to_dataframe(library='polars') needs polars 1.0 or later, not {version}: pip install "
                              f"--upgrade polars")
    return module


def _choose(library: Optional[str]) -> Tuple[str, Any]:
    """The name and the module of the library to use."""
    if library is not None:
        if library not in _LIBRARIES:
            raise ValueError(f"library is 'pandas' or 'polars', not {library!r}")
        return library, _import(library)
    for name in _LIBRARIES:
        try:
            return name, _import(name)
        except ImportError:
            continue
    raise ImportError("to_dataframe needs pandas, or polars 1.0 or later, and neither is installed: pip install pandas, "
                      "or pip install polars")


def _columns(rows: Iterable[Any]) -> Dict[str, List[Any]]:
    """Rows as a table: each field in the order in which a row first has it, None in the rows that do not."""
    columns: Dict[str, List[Any]] = {}
    count = 0
    for row in rows:
        if not isinstance(row, Mapping):
            raise TypeError(f"A row is a mapping of fields to values, as doc.rows() returns it, not "
                            f"{type(row).__name__}.")
        for name in row:
            if name not in columns:
                columns[name] = [None] * count
        for name, values in columns.items():
            values.append(row.get(name))
        count += 1
    return columns


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


def _to_pandas(pandas: Any, table: Mapping[str, Any]) -> Any:
    columns: Dict[str, Any] = {name: _pandas_column(pandas, list(values)) for name, values in table.items()}
    return pandas.DataFrame(columns)


def _to_polars(polars: Any, table: Mapping[str, Any]) -> Any:
    return polars.DataFrame({name: list(values) for name, values in table.items()}, strict=False)


def to_dataframe(table: Any, library: Optional[str] = None) -> Any:
    """A data frame with the rows of a table, as doc.table() or doc.summary[name] return it.

    library is "pandas" or "polars". When it is None, pandas is used when it is installed, and polars otherwise.
    polars must be 1.0 or later. With pandas, an integer or boolean column with null values gets a nullable type, so
    that it does not turn into floats, and a column of lists or objects keeps them as they are. Timestamps stay
    strings: pqax.parse_timestamp reads one. The table can also be an iterable of rows, as doc.rows() returns them,
    and gives the same frame.
    """
    name, module = _choose(library)
    columns = table if isinstance(table, Mapping) else _columns(table)
    return _to_pandas(module, columns) if name == "pandas" else _to_polars(module, columns)
