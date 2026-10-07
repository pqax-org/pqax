"""JSON and timestamps as the format writes them."""
from __future__ import annotations

import datetime
import json
from typing import Any

from ._errors import JsonError
from ._schema import TIMESTAMP_PATTERN

BOM = b"\xef\xbb\xbf"
_UTC = datetime.timezone.utc


def _reject_constant(name: str) -> Any:
    raise ValueError(f"{name} is not a JSON value")


def loads(data: bytes, part: str) -> Any:
    """Parses a part as a reader does: it skips a byte order mark, and raises JsonError when the text is not JSON."""
    if data.startswith(BOM):
        data = data[len(BOM):]
    try:
        return json.loads(data, parse_constant=_reject_constant)
    except (ValueError, RecursionError) as error:
        raise JsonError(f"The part {part} is not valid JSON: {error}") from error


def dumps(value: Any) -> bytes:
    """Compact JSON in UTF-8, with no byte order mark, as a writer writes a part."""
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")


def format_timestamp(value: datetime.datetime) -> str:
    """A timestamp in the form of the format: UTC with exactly three decimals, as in 2026-10-05T08:15:30.000Z.

    A naive datetime is taken as UTC. The microseconds are truncated to milliseconds.
    """
    if not isinstance(value, datetime.datetime):
        raise TypeError(f"Expected a datetime, not {type(value).__name__}")
    if value.tzinfo is None or value.utcoffset() is None:
        value = value.replace(tzinfo=_UTC)
    value = value.astimezone(_UTC)
    return (f"{value.year:04d}-{value.month:02d}-{value.day:02d}T{value.hour:02d}:{value.minute:02d}:"
            f"{value.second:02d}.{value.microsecond // 1000:03d}Z")


def _fields(text: str) -> tuple[int, int, int, int, int, int, int]:
    return (int(text[0:4]), int(text[5:7]), int(text[8:10]), int(text[11:13]), int(text[14:16]), int(text[17:19]),
            int(text[20:23]))


def is_timestamp(value: Any) -> bool:
    """Whether a value is a timestamp in the form of the format, and a real date and time."""
    if not isinstance(value, str) or TIMESTAMP_PATTERN.fullmatch(value) is None:
        return False
    year, month, day, hour, minute, second, _ = _fields(value)
    try:
        datetime.datetime(year, month, day, hour, minute, second)
    except ValueError:
        return False
    return True


def parse_timestamp(text: str) -> datetime.datetime:
    """A timestamp of the file as an aware datetime in UTC.

    It reads the form of the format, and also the other ISO 8601 forms that datetime.fromisoformat reads, with Z for
    UTC. A time with no offset is taken as UTC. It raises ValueError for anything else.
    """
    if is_timestamp(text):
        year, month, day, hour, minute, second, millisecond = _fields(text)
        return datetime.datetime(year, month, day, hour, minute, second, millisecond * 1000, tzinfo=_UTC)
    if not isinstance(text, str):
        raise TypeError(f"Expected a string, not {type(text).__name__}")
    candidate = text[:-1] + "+00:00" if text.endswith(("Z", "z")) else text
    try:
        moment = datetime.datetime.fromisoformat(candidate)
    except ValueError:
        raise ValueError(f"Not a timestamp: {text[:40]!r}") from None
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=_UTC)
    return moment.astimezone(_UTC)
