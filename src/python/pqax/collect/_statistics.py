"""The bounds of the statistics of a column chunk as text, by the physical type and the annotation of its leaf.

Used only when a collection keeps data values. The text is the one of the C# collector: invariant numbers, dates as
yyyy-MM-dd, timestamps with six decimals (seven for nanoseconds), decimals with their scale, text for strings, and
0x and hexadecimal digits for other bytes.
"""
from __future__ import annotations

import datetime
import decimal
import math
import struct
from typing import Optional, Tuple

_EPOCH = datetime.datetime(1970, 1, 1)
_TICKS_EPOCH = 621355968000000000  # the ticks of 1970-01-01 since 0001-01-01, in units of 100 ns
_TICKS_MAX = 3155378975999999999  # the ticks of 9999-12-31 23:59:59.9999999
_FLOAT = struct.Struct("<f")
_DOUBLE = struct.Struct("<d")
_INT32 = struct.Struct("<i")
_INT64 = struct.Struct("<q")


def _wrap64(value: int) -> int:
    value &= 0xFFFFFFFFFFFFFFFF
    return value - (1 << 64) if value >= (1 << 63) else value


def _ticks_text(ticks: int, decimals: int) -> Optional[str]:
    """A time as ticks since 1970, as C# DateTime.AddTicks formats it; None outside the years 1 to 9999."""
    absolute = _TICKS_EPOCH + ticks
    if absolute < 0 or absolute > _TICKS_MAX:
        return None
    days, rest = divmod(absolute, 864000000000)
    moment = datetime.datetime(1, 1, 1) + datetime.timedelta(days=days)
    seconds, fraction = divmod(rest, 10000000)
    hours, seconds = divmod(seconds, 3600)
    minutes, seconds = divmod(seconds, 60)
    text = f"{moment.year:04d}-{moment.month:02d}-{moment.day:02d}T{hours:02d}:{minutes:02d}:{seconds:02d}"
    return f"{text}.{fraction:07d}" if decimals == 7 else f"{text}.{fraction // 10:06d}"


def _shortest(value: float, single: bool) -> Tuple[str, int]:
    """The shortest digits that read back as the value, and the decimal exponent of the first digit."""
    if single:
        target = _FLOAT.pack(value)
        for precision in range(1, 10):
            text = f"{value:.{precision - 1}e}"
            try:
                if _FLOAT.pack(float(text)) == target:
                    break
            except OverflowError:  # rounded beyond the largest single: more digits are needed
                continue
    else:
        text = repr(value)
    sign, digits, exponent = decimal.Decimal(text).normalize().as_tuple()
    text_digits = "".join(str(d) for d in digits).rstrip("0") or "0"
    return text_digits, exponent + len(digits) - 1


def format_float(value: float, single: bool = False) -> str:
    """A float as C# formats it with "R" and the invariant culture: the shortest digits that read back, in fixed
    notation from 1E-04 up to the digits of a round trip, 17 (9 for a single), and in scientific notation with at least
    two exponent digits outside."""
    if math.isnan(value):
        return "NaN"
    if math.isinf(value):
        return "Infinity" if value > 0 else "-Infinity"
    negative = math.copysign(1.0, value) < 0
    if value == 0:
        return "-0" if negative else "0"
    digits, exponent = _shortest(abs(value), single)
    limit = max(len(digits), 9 if single else 17)  # as Number.Formatting: a scale over the round-trip digits
    if exponent + 1 > limit or exponent < -4:
        mantissa = digits[0] + ("." + digits[1:] if len(digits) > 1 else "")
        text = f"{mantissa}E{'+' if exponent >= 0 else '-'}{abs(exponent):02d}"
    elif exponent < 0:
        text = "0." + "0" * (-exponent - 1) + digits
    elif len(digits) <= exponent + 1:
        text = digits + "0" * (exponent + 1 - len(digits))
    else:
        text = digits[:exponent + 1] + "." + digits[exponent + 1:]
    return "-" + text if negative else text


def _decimal_text(unscaled: int, scale: int) -> str:
    if scale <= 0:
        return str(unscaled)
    digits = str(abs(unscaled)).rjust(scale + 1, "0")
    text = digits[:-scale] + "." + digits[-scale:]
    return "-" + text if unscaled < 0 else text


def _scale(leaf) -> int:
    annotation = leaf.type_annotation
    if annotation is not None:
        comma = annotation.find(",")
        close = annotation.find(")")
        if 0 < comma < close:
            try:
                return int(annotation[comma + 1:close].strip())
            except ValueError:
                pass
    return leaf.scale or 0


def _integer_text(value: int, annotation: Optional[str], leaf) -> Optional[str]:
    if annotation is None:
        return str(value)
    if annotation.startswith("DECIMAL("):
        return _decimal_text(value, _scale(leaf))
    if annotation == "DATE":
        if not -719162 <= value <= 2932896:  # 0001-01-01 to 9999-12-31
            return None
        day = _EPOCH + datetime.timedelta(days=value)
        return f"{day.year:04d}-{day.month:02d}-{day.day:02d}"
    ticks_per_unit = 0
    if annotation.startswith("TIMESTAMP(MILLIS") or annotation == "TIMESTAMP_MILLIS":
        ticks_per_unit = 10000
    elif annotation.startswith("TIMESTAMP(MICROS") or annotation == "TIMESTAMP_MICROS":
        ticks_per_unit = 10
    elif annotation.startswith("TIMESTAMP(NANOS"):
        ticks = abs(value) // 100  # C# divides toward zero
        return _ticks_text(-ticks if value < 0 else ticks, 7)
    if ticks_per_unit:
        return _ticks_text(_wrap64(value * ticks_per_unit), 6)
    if (annotation.startswith("INTEGER(") and "UNSIGNED" in annotation) or annotation.startswith("UINT_"):
        return str(value & 0xFFFFFFFF) if leaf.physical_type == "INT32" else str(value & 0xFFFFFFFFFFFFFFFF)
    return str(value)


def format_bound(value: Optional[bytes], leaf) -> Optional[str]:
    """A bound of the statistics as text, or None when it cannot be read with the type of its leaf."""
    if value is None:
        return None
    annotation = leaf.type_annotation
    physical = leaf.physical_type
    if physical == "BOOLEAN":
        return ("true" if value[0] != 0 else "false") if len(value) >= 1 else None
    if physical == "INT32":
        return _integer_text(_INT32.unpack_from(value)[0], annotation, leaf) if len(value) >= 4 else None
    if physical == "INT64":
        return _integer_text(_INT64.unpack_from(value)[0], annotation, leaf) if len(value) >= 8 else None
    if physical == "FLOAT":
        return format_float(_FLOAT.unpack_from(value)[0], True) if len(value) >= 4 else None
    if physical == "DOUBLE":
        return format_float(_DOUBLE.unpack_from(value)[0]) if len(value) >= 8 else None
    if physical in ("BYTE_ARRAY", "FIXED_LEN_BYTE_ARRAY"):
        if annotation is not None and annotation.startswith("DECIMAL("):
            return _decimal_text(int.from_bytes(value, "big", signed=True) if value else 0, _scale(leaf))
        if annotation in ("STRING", "UTF8", "ENUM", "JSON"):
            return value.decode("utf-8", "replace")
        if annotation is None and physical == "BYTE_ARRAY":
            try:
                return value.decode("utf-8")
            except UnicodeDecodeError:
                pass
    return "0x" + value.hex()
