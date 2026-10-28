"""Mutates the footers of Parquet files and checks that the footer readers of both collectors refuse them the same way.

Usage, from the root of the repository:

    python conformance/mutate_footers.py FOLDER [--seed N] [--flips N] [--keep DIR]

FOLDER holds Parquet files, as make_footers.py writes them; the files whose footer takes at most 16 KB are the bases.
Every mutation is a record of a pack of footer_check.py: the end of a file, and the size that the reader is given.
The families, all from one seed:

- truncations: the file cut short, and a file shorter than the size that the table format gives;
- the length before the magic: zero, negative, one byte off, past the file, past 64 MiB, the largest integers;
- the magic: PARE, other bytes, lower case, nothing;
- sizes: below 12, larger than the file, very large;
- counts: list headers of the schema, the row groups, the column chunks, and the encodings that claim 2^31 - 1
  elements, or more than the bytes left;
- nesting: schema trees and Thrift structures, lists, sets, and maps nested from 60 to 100,000 levels deep;
- fields: each field of the footer, of a schema element, of a row group, of a column chunk, of its metadata, and of
  its statistics left out, repeated, given another wire type, and the fields of a structure in reverse order;
- strings: names, keys, and values with bytes that are not UTF-8;
- bounds: the statistics with data values kept, bounds of every length, decimal bounds longer than 32 bytes and
  scales above 76;
- random bytes: one to four bytes of the footer changed, inserted, or removed, with the length kept right, --flips
  times for each base.

Both readers must give the same outcome for every record, and the outcome must be a footer or the footer exception
with FooterInvalid, FooterEncrypted, or FooterTooLarge: nothing else may escape, no read may hang, and no read may
allocate more than a fixed multiple of the footer. The same seed gives the same records.
"""
from __future__ import annotations

import argparse
import random
import shutil
import struct
import sys
import tempfile
from pathlib import Path
from typing import Any, Iterator, List, Optional, Tuple

import footer_check
from footer_check import Record

STOP, TRUE, FALSE, BYTE, I16, I32, I64, DOUBLE, BINARY, LIST, SET, MAP, STRUCT, UUID = range(14)
MAX_BASE_FOOTER = 16384


# ---- the compact protocol, generic: a structure is a list of fields ----

class Field:
    __slots__ = ("id", "type", "value")

    def __init__(self, field_id: int, kind: int, value: Any) -> None:
        self.id = field_id
        self.type = kind
        self.value = value


def varint(value: int) -> bytes:
    out = bytearray()
    value &= (1 << 64) - 1
    while True:
        byte = value & 0x7F
        value >>= 7
        if value:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            return bytes(out)


def zigzag(value: int) -> int:
    return (value << 1) ^ (value >> 63)


class Decoder:
    """Reads a footer into fields; the values keep the wire form, so that a structure writes back the same bytes."""

    def __init__(self, data: bytes) -> None:
        self.data = data
        self.position = 0

    def byte(self) -> int:
        value = self.data[self.position]
        self.position += 1
        return value

    def varint(self) -> int:
        result = shift = 0
        while True:
            byte = self.byte()
            result |= (byte & 0x7F) << shift
            if not byte & 0x80:
                return result
            shift += 7

    def struct(self) -> List[Field]:
        fields: List[Field] = []
        last = 0
        while True:
            header = self.byte()
            if header == 0:
                return fields
            kind, delta = header & 0x0F, header >> 4
            if delta:
                field_id = last + delta
            else:
                n = self.varint()
                field_id = (n >> 1) ^ -(n & 1)
            last = field_id
            fields.append(Field(field_id, kind, self.value(kind, in_field=True)))

    def value(self, kind: int, in_field: bool) -> Any:
        if kind in (TRUE, FALSE):
            return kind == TRUE if in_field else self.byte() == TRUE
        if kind == BYTE:
            return self.byte()
        if kind in (I16, I32, I64):
            return self.varint()
        if kind == DOUBLE:
            value = self.data[self.position:self.position + 8]
            self.position += 8
            return value
        if kind == BINARY:
            length = self.varint()
            value = self.data[self.position:self.position + length]
            self.position += length
            return value
        if kind in (LIST, SET):
            header = self.byte()
            size, element = header >> 4, header & 0x0F
            if size == 15:
                size = self.varint()
            return (element, [self.value(element, in_field=False) for _ in range(size)])
        if kind == MAP:
            size = self.varint()
            if size == 0:
                return (0, 0, [])
            types = self.byte()
            return (types >> 4, types & 0x0F, [(self.value(types >> 4, False), self.value(types & 0x0F, False))
                                                for _ in range(size)])
        if kind == STRUCT:
            return self.struct()
        if kind == UUID:
            value = self.data[self.position:self.position + 16]
            self.position += 16
            return value
        raise ValueError(f"wire type {kind}")


def encode_struct(fields: List[Field]) -> bytes:
    out = bytearray()
    last = 0
    for field in fields:
        kind = field.type
        if kind in (TRUE, FALSE):
            kind = TRUE if field.value else FALSE
        delta = field.id - last
        if 0 < delta <= 15:
            out.append((delta << 4) | kind)
        else:
            out.append(kind)
            out += varint(zigzag(field.id) & 0xFFFFFFFF)
        last = field.id
        out += encode_value(field.type, field.value, in_field=True)
    out.append(0)
    return bytes(out)


def encode_value(kind: int, value: Any, in_field: bool) -> bytes:
    if kind in (TRUE, FALSE):
        return b"" if in_field else bytes([TRUE if value else FALSE])
    if kind == BYTE:
        return bytes([value & 0xFF])
    if kind in (I16, I32, I64):
        return varint(value)
    if kind in (DOUBLE, UUID):
        return bytes(value)
    if kind == BINARY:
        return varint(len(value)) + bytes(value)
    if kind in (LIST, SET):
        element, items = value
        head = bytes([(len(items) << 4) | element]) if len(items) < 15 else bytes([0xF0 | element]) + varint(len(items))
        return head + b"".join(encode_value(element, item, in_field=False) for item in items)
    if kind == MAP:
        key, item, pairs = value
        if not pairs:
            return b"\x00"
        return varint(len(pairs)) + bytes([(key << 4) | item]) + b"".join(
            encode_value(key, k, False) + encode_value(item, v, False) for k, v in pairs)
    if kind == STRUCT:
        return encode_struct(value)
    raise ValueError(f"wire type {kind}")


def structures(fields: List[Field], path: str = "") -> Iterator[Tuple[str, List[Field]]]:
    """Every structure inside a structure, with a path such as /4[0]/1[2]/3, the structure first."""
    yield path or "/", fields
    for field in fields:
        if field.type == STRUCT:
            yield from structures(field.value, f"{path}/{field.id}")
        elif field.type in (LIST, SET) and field.value[0] == STRUCT:
            for index, item in enumerate(field.value[1][:3]):
                yield from structures(item, f"{path}/{field.id}[{index}]")


def copy_fields(fields: List[Field]) -> List[Field]:
    return [Field(f.id, f.type, copy_value(f.type, f.value)) for f in fields]


def copy_value(kind: int, value: Any) -> Any:
    if kind == STRUCT:
        return copy_fields(value)
    if kind in (LIST, SET):
        return (value[0], [copy_value(value[0], item) for item in value[1]])
    return value


def find(root: List[Field], path: str) -> List[Field]:
    for found, fields in structures(root):
        if found == (path or "/"):
            return fields
    raise KeyError(path)


# ---- the file around a footer ----

class Whole(bytes):
    """The bytes of a synthetic file, which a record keeps whole."""


def file_of(footer: bytes, before: int = 4, magic: bytes = b"PAR1", length: Optional[int] = None) -> bytes:
    """A Parquet file of before bytes of data (the leading magic first), the footer, its length, and the magic."""
    head = (b"PAR1" + b"\x00" * max(0, before - 4))[:before]
    return Whole(head + footer + struct.pack("<i", len(footer) if length is None else length) + magic)


def record(name: str, data: bytes, stated: Optional[int] = None, data_values: bool = False) -> Record:
    """A file of the bytes of data, read with stated as its size, or its length. The record of a base, or of a part of
    one, keeps its last 20 KB: the footers of the bases take at most 16 KB, and the reader finds zeros before the end
    it keeps. The record of a synthetic file of file_of() keeps it whole."""
    keep = data if isinstance(data, Whole) else data[max(0, len(data) - 20480):]
    return Record(name, len(data) if stated is None else stated, len(data), bytes(keep), data_values)


# ---- the families ----

def truncations(name: str, data: bytes, footer_length: int, rng: random.Random) -> Iterator[Record]:
    size = len(data)
    cuts = sorted({0, 1, 4, 7, 8, 11, 12, 13, size // 2, size - 1, size - 4, size - 8, size - 9,
                   size - 8 - footer_length // 2, size - 8 - footer_length + 1, *(rng.randrange(1, size) for _ in range(6))})
    for cut in cuts:
        if 0 <= cut < size:
            yield record(f"{name}/cut-{cut}", data[:cut])
    for missing in (1, 8, footer_length, size // 2):
        if 0 < missing < size:
            # The table format gives the full size, and the storage has fewer bytes.
            yield Record(f"{name}/short-by-{missing}", size, size - missing, data[:size - missing])


def lengths(name: str, data: bytes, footer_length: int) -> Iterator[Record]:
    size = len(data)
    body = data[:-8]
    for value in (0, -1, -footer_length, 1, 7, footer_length - 1, footer_length + 1, footer_length + 7, size - 12,
                  size - 11, size - 8, size, size + 1, 0x7FFFFFFF, -0x80000000, 64 * 1024 * 1024,
                  64 * 1024 * 1024 + 1):
        yield record(f"{name}/length-{value}", body + struct.pack("<i", value) + b"PAR1")
    # A length over 64 MiB in a file large enough for it: the stated size, with zeros before the end.
    big = 64 * 1024 * 1024 + 100
    yield Record(f"{name}/length-over-64-mib", big + 100, big + 100, body + struct.pack("<i", big) + b"PAR1")
    yield Record(f"{name}/length-64-mib", big + 100, big + 100, body + struct.pack("<i", 64 * 1024 * 1024) + b"PAR1")


def magics(name: str, data: bytes) -> Iterator[Record]:
    body = data[:-4]
    for magic in (b"PARE", b"PAR0", b"par1", b"PAR\x00", b"\x00\x00\x00\x00", b"1RAP", b"PAR"):
        yield record(f"{name}/magic-{magic.hex()}", body + magic)


def sizes(name: str, data: bytes) -> Iterator[Record]:
    size = len(data)
    for stated in (0, 1, 11, 12, -1, size - 1, size + 1, size + 4096, 1 << 40, (1 << 62)):
        yield Record(f"{name}/size-{stated}", stated, size, data)


def counts(name: str, footer: bytes, root: List[Field]) -> Iterator[Record]:
    """List headers that claim a huge number of elements, at the places of the footer that hold lists."""
    places = []
    for path, fields in structures(root):
        for index, field in enumerate(fields):
            if field.type in (LIST, SET):
                places.append((path, index, field.id))
    encoded = encode_struct(root)
    for path, index, field_id in places[:12]:
        target = find(root, path)[index]
        element, items = target.value
        original = encode_value(target.type, (element, items), in_field=True)
        header_length = 1 if len(items) < 15 else 1 + len(varint(len(items)))
        at = encoded.find(original)
        if at < 0:
            continue
        for claimed in (0x7FFFFFFF, 0xFFFFFFFF, 1 << 40, 100_000_000, len(items) + 1, 15):
            # The list again, with the claimed count in its header and the same elements after it.
            replaced = bytes([0xF0 | element]) + varint(claimed) + original[header_length:]
            yield record(f"{name}/count{path}.{field_id}-{claimed}",
                         file_of(encoded[:at] + replaced + encoded[at + len(original):]))


def nesting(rng: random.Random) -> Iterator[Record]:
    """Schemas and Thrift values nested deep, around a footer of one leaf."""
    def schema_footer(depth: int) -> bytes:
        elements = [[Field(4, BINARY, b"schema"), Field(5, I32, zigzag(1))]]
        for level in range(depth):
            elements.append([Field(3, I32, zigzag(1)), Field(4, BINARY, f"g{level}".encode()), Field(5, I32, zigzag(1))])
        elements.append([Field(1, I32, zigzag(1)), Field(3, I32, zigzag(1)), Field(4, BINARY, b"leaf")])
        chunk = [Field(3, STRUCT, [Field(1, I32, zigzag(1)), Field(2, LIST, (I32, [0])), Field(4, I32, 0),
                                   Field(5, I64, zigzag(1)), Field(6, I64, zigzag(10)), Field(7, I64, zigzag(10)),
                                   Field(9, I64, zigzag(4))])]
        group = [Field(1, LIST, (STRUCT, [chunk])), Field(2, I64, zigzag(10)), Field(3, I64, zigzag(1))]
        return encode_struct([Field(1, I32, zigzag(1)), Field(2, LIST, (STRUCT, elements)), Field(3, I64, zigzag(1)),
                              Field(4, LIST, (STRUCT, [group]))])

    for depth in (60, 63, 64, 65, 66, 128, 254, 255, 256, 257, 1000, 20000):
        yield record(f"nesting/schema-{depth}", file_of(schema_footer(depth)))
    base = schema_footer(1)
    tail = base[:-1]  # the footer without its final stop, to add an unknown field 30

    def wrap(depth: int, kind: str) -> bytes:
        """An unknown field 30 that holds a value nested depth levels deep, from the inside out."""
        if kind == "struct":
            value, value_type = b"\x15\x02\x00", STRUCT  # a structure that holds one i32
        else:
            value, value_type = b"\x02", I32
        for level in range(depth - (1 if kind == "struct" else 0)):
            outer = kind if kind != "mixed" else ("list", "map", "set")[level % 3]
            if outer == "struct":
                value = b"\x1c" + value + b"\x00"  # field 1 holds the structure inside
            elif outer in ("list", "set"):
                value = bytes([0x10 | value_type]) + value  # one element
                value_type = LIST if outer == "list" else SET
                continue
            else:
                value = b"\x01" + bytes([(I32 << 4) | value_type]) + b"\x02" + value  # one pair, an i32 key
                value_type = MAP
                continue
        return bytes([value_type]) + b"\x3c" + value  # the long form header of field 30

    for kind in ("struct", "list", "set", "map", "mixed"):
        for depth in (10, 62, 63, 64, 65, 66, 200, 1000, 10000, 100000):
            yield record(f"nesting/{kind}-{depth}", file_of(tail + wrap(depth, kind) + b"\x00"))
    del rng


def fields(name: str, root: List[Field]) -> Iterator[Record]:
    """Each field left out, repeated, and given another wire type; and the fields of a structure in reverse order."""
    for path, fields_ in list(structures(root)):
        for index, field in enumerate(fields_):
            dropped = copy_fields(root)
            del find(dropped, path)[index]
            yield record(f"{name}/drop{path}.{field.id}", file_of(encode_struct(dropped)))
            repeated = copy_fields(root)
            target = find(repeated, path)
            target.insert(index + 1, Field(field.id, field.type, copy_value(field.type, field.value)))
            yield record(f"{name}/repeat{path}.{field.id}", file_of(encode_struct(repeated)))
            if field.type in (LIST, SET) and field.value[1]:
                shorter = copy_fields(root)
                target = find(shorter, path)
                element, items = field.value
                target.insert(index + 1, Field(field.id, field.type, (element, [copy_value(element, items[0])])))
                yield record(f"{name}/repeat-shorter{path}.{field.id}", file_of(encode_struct(shorter)))
                longer = copy_fields(root)
                target = find(longer, path)
                target.insert(index + 1, Field(field.id, field.type, (element, [copy_value(element, item)
                                                                               for item in items + items])))
                yield record(f"{name}/repeat-longer{path}.{field.id}", file_of(encode_struct(longer)))
            for kind in (I32, BINARY, STRUCT, LIST, TRUE):
                if kind == field.type:
                    continue
                retyped = copy_fields(root)
                value = {I32: zigzag(7), BINARY: b"x", STRUCT: [], LIST: (I32, [2]), TRUE: True}[kind]
                find(retyped, path)[index] = Field(field.id, kind, value)
                yield record(f"{name}/retype{path}.{field.id}-{kind}", file_of(encode_struct(retyped)))
        reverse = copy_fields(root)
        target = find(reverse, path)
        target.reverse()
        yield record(f"{name}/reverse{path}", file_of(encode_struct(reverse)))


def strings(name: str, root: List[Field], rng: random.Random) -> Iterator[Record]:
    """Binaries of the footer with bytes that are not UTF-8."""
    targets = []
    for path, fields_ in structures(root):
        for index, field in enumerate(fields_):
            if field.type == BINARY:
                targets.append((path, index))
    bad = (b"\xff", b"\xc3", b"\xe2\x82", b"\xed\xa0\x80", b"\xf4\x90\x80\x80", b"\xc0\xaf", b"a\x00b",
           "ſ".encode(), "ı".encode())
    for count, (path, index) in enumerate(targets[:40]):
        for piece in bad:
            mutated = copy_fields(root)
            field = find(mutated, path)[index]
            value = bytes(field.value)
            at = rng.randrange(0, len(value) + 1)
            field.value = value[:at] + piece + value[at:]
            yield record(f"{name}/string{path}.{field.id}-{piece.hex()}", file_of(encode_struct(mutated)), data_values=True)
        del count


def bounds(rng: random.Random) -> Iterator[Record]:
    """Footers of one leaf of each type with statistics, read with the data values kept."""
    def footer(physical: int, logical: Optional[List[Field]], converted: Optional[int], minimum: bytes,
               maximum: bytes, scale: Optional[int] = None, precision: Optional[int] = None,
               length: Optional[int] = None) -> bytes:
        leaf = [Field(1, I32, zigzag(physical))]
        if length is not None:
            leaf.append(Field(2, I32, zigzag(length)))
        leaf += [Field(3, I32, zigzag(1)), Field(4, BINARY, b"v")]
        if converted is not None:
            leaf.append(Field(6, I32, zigzag(converted)))
        if scale is not None:
            leaf.append(Field(7, I32, zigzag(scale)))
        if precision is not None:
            leaf.append(Field(8, I32, zigzag(precision)))
        if logical is not None:
            leaf.append(Field(10, STRUCT, logical))
        statistics = [Field(1, BINARY, maximum), Field(2, BINARY, minimum), Field(3, I64, zigzag(0)),
                      Field(5, BINARY, maximum), Field(6, BINARY, minimum)]
        meta = [Field(1, I32, zigzag(physical)), Field(2, LIST, (I32, [0])), Field(4, I32, 0), Field(5, I64, zigzag(1)),
                Field(6, I64, zigzag(10)), Field(7, I64, zigzag(10)), Field(9, I64, zigzag(4)),
                Field(12, STRUCT, statistics)]
        group = [Field(1, LIST, (STRUCT, [[Field(3, STRUCT, meta)]])), Field(2, I64, zigzag(10)), Field(3, I64, zigzag(1))]
        schema = [[Field(4, BINARY, b"schema"), Field(5, I32, zigzag(1))], leaf]
        return encode_struct([Field(1, I32, zigzag(1)), Field(2, LIST, (STRUCT, schema)), Field(3, I64, zigzag(1)),
                              Field(4, LIST, (STRUCT, [group]))])

    def decimal(precision: int, scale: int) -> List[Field]:
        return [Field(5, STRUCT, [Field(1, I32, zigzag(scale)), Field(2, I32, zigzag(precision))])]

    def timestamp(unit: int, utc: bool) -> List[Field]:
        return [Field(8, STRUCT, [Field(1, TRUE, utc), Field(2, STRUCT, [Field(unit, STRUCT, [])])])]

    def integer(width: int, signed: bool) -> List[Field]:
        return [Field(10, STRUCT, [Field(1, BYTE, width), Field(2, TRUE, signed)])]

    cases = []
    for length in (0, 1, 2, 3, 4, 5, 7, 8, 9, 12, 16, 17, 31, 32, 33, 64, 1786, 1800, 5000, 100000):
        value = bytes(rng.randrange(256) for _ in range(length))
        cases += [
            (f"decimal-bytes-{length}", footer(6, decimal(38, 10), None, value, value[::-1])),
            (f"decimal-fixed-{length}", footer(7, decimal(38, 10), None, value, value, length=length)),
            (f"decimal-converted-{length}", footer(6, None, 5, value, value, scale=4, precision=30)),
            (f"string-{length}", footer(6, [Field(1, STRUCT, [])], None, value, value)),
            (f"binary-{length}", footer(6, None, None, value, value)),
            (f"int32-{length}", footer(1, None, None, value, value)),
            (f"int64-{length}", footer(2, None, None, value, value)),
            (f"float-{length}", footer(4, None, None, value, value)),
            (f"double-{length}", footer(5, None, None, value, value)),
            (f"boolean-{length}", footer(0, None, None, value, value)),
            (f"int96-{length}", footer(3, None, None, value, value)),
        ]
    for scale in (-5, 0, 1, 38, 76, 77, 100, 1000, 100000, 0x7FFFFFFF, -0x80000000):
        eight = struct.pack("<q", rng.randrange(-(1 << 63), 1 << 63))
        cases += [(f"decimal-int64-scale-{scale}", footer(2, decimal(18, scale), None, eight, eight)),
                  (f"decimal-int32-scale-{scale}", footer(1, None, 5, eight[:4], eight[:4], scale=scale, precision=9)),
                  (f"decimal-bytes-scale-{scale}", footer(6, decimal(38, scale), None, eight, eight))]
    for unit in (1, 2, 3, 4):
        for value in (0, 1, -1, (1 << 63) - 1, -(1 << 63), 253402300799999, -62135596800001, 1 << 60):
            raw = struct.pack("<q", value)
            cases += [(f"timestamp-{unit}-{value}", footer(2, timestamp(unit, True), None, raw, raw)),
                      (f"timestamp-local-{unit}-{value}", footer(2, timestamp(unit, False), None, raw, raw))]
    for converted in (9, 10, 6, 11, 12, 13, 14, 15, 16, 17, 18):
        for value in (0, -1, 2932896, 2932897, -719162, -719163, 0x7FFFFFFF, -0x80000000):
            cases.append((f"converted-{converted}-{value}", footer(1, None, converted, struct.pack("<i", value),
                                                                  struct.pack("<i", value))))
            cases.append((f"converted64-{converted}-{value}", footer(2, None, converted, struct.pack("<q", value),
                                                                    struct.pack("<q", value))))
    for width in (8, 16, 32, 64, 0, 7, 127, -1):
        for signed in (True, False):
            raw = struct.pack("<q", -12345)
            cases += [(f"integer-{width}-{signed}", footer(2, integer(width, signed), None, raw, raw)),
                      (f"integer32-{width}-{signed}", footer(1, integer(width, signed), None, raw[:4], raw[:4]))]
    for bits in (0x7F800000, 0xFF800000, 0x7FC00000, 0xFFC00001, 0x00000001, 0x80000000, 0x3F800000, 0x7F7FFFFF,
                 0x33D6BF95, 0x4B189680, 0x5A0E1BCA):
        raw = struct.pack("<I", bits)
        cases.append((f"float-bits-{bits:08x}", footer(4, None, None, raw, raw)))
    for value in (0.1, 1e-5, 1e15, 1e16, 1e17, 123456789012345680.0, 5e-324, 1.7976931348623157e308, -0.0,
                  float("nan"), 0.3, 2.5e-5, 1e21, 9007199254740993.0):
        raw = struct.pack("<d", value)
        cases.append((f"double-{value!r}", footer(5, None, None, raw, raw)))
    for _ in range(300):
        raw = bytes(rng.randrange(256) for _ in range(8))
        cases += [(f"float-random-{raw.hex()}", footer(4, None, None, raw[:4], raw[4:])),
                  (f"double-random-{raw.hex()}", footer(5, None, None, raw, raw))]
    for name, meta in cases:
        yield record(f"bounds/{name}", file_of(meta), data_values=True)


def random_bytes(name: str, data: bytes, footer_length: int, flips: int, rng: random.Random) -> Iterator[Record]:
    """flips mutations of the footer: bytes changed, inserted, or removed, with the length kept right."""
    start = len(data) - 8 - footer_length
    footer = data[start:len(data) - 8]
    for round_ in range(flips):
        changed = bytearray(footer)
        kind = round_ % 4
        for _ in range(1 + rng.randrange(4)):
            if not changed:
                break
            at = rng.randrange(len(changed))
            if kind in (0, 1):
                changed[at] = rng.randrange(256)
            elif kind == 2:
                changed[at:at] = bytes([rng.randrange(256)])
            else:
                del changed[at]
        if kind == 1:
            # A byte of a field header or a length changed into a value that is often special.
            at = rng.randrange(len(changed))
            changed[at] = rng.choice((0x00, 0x0F, 0x19, 0x1C, 0x29, 0xF9, 0xFC, 0xFF, 0x80, 0x7F))
        yield record(f"{name}/random-{round_}", data[:start] + bytes(changed) + struct.pack("<i", len(changed)) + b"PAR1",
                     data_values=round_ % 2 == 0)


# ---- the run ----

def footer_check_limit(record: Record) -> int:
    """The longest footer that the reader reads from a record: the stated size, less the 12 bytes around a footer."""
    return max(0, min(record.stated_size, record.actual_size) - 12)


def bases(folder: Path) -> List[Tuple[str, bytes, int]]:
    """The files of a folder whose footer is at most 16 KB, with their bytes and the length of their footer."""
    found = []
    for path in sorted(folder.rglob("*.parquet")):
        if "_delta_log" in path.parts:
            continue
        data = path.read_bytes()
        if len(data) < 12 or data[-4:] != b"PAR1":
            continue
        length = struct.unpack("<i", data[-8:-4])[0]
        if 0 < length <= MAX_BASE_FOOTER and length <= len(data) - 12:
            try:
                Decoder(data[len(data) - 8 - length:len(data) - 8]).struct()
            except Exception:  # noqa: BLE001 - a base must decode
                continue
            found.append((path.relative_to(folder).as_posix(), data, length))
    return found


def mutations(folder: Path, seed: int, flips: int) -> Iterator[Record]:
    rng = random.Random(seed)
    files = bases(folder)
    # The structural families on a spread of the bases, the random bytes on all of them.
    for index, (name, data, length) in enumerate(files):
        footer = data[len(data) - 8 - length:len(data) - 8]
        root = Decoder(footer).struct()
        if index % 4 == 0:
            yield from truncations(name, data, length, rng)
            yield from lengths(name, data, length)
            yield from magics(name, data)
            yield from sizes(name, data)
            yield from counts(name, footer, root)
            yield from strings(name, root, rng)
        if index % 8 == 0 and length <= 4096:
            yield from fields(name, root)
        yield from random_bytes(name, data, length, flips, rng)
    yield from nesting(rng)
    yield from bounds(rng)


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("folder", help="a folder of Parquet files, as make_footers.py writes them")
    parser.add_argument("--seed", type=int, default=20261028, help="the seed of every random choice")
    parser.add_argument("--flips", type=int, default=40, help="the random mutations of each base")
    parser.add_argument("--keep", metavar="DIR", help="keep the pack and the results in DIR")
    arguments = parser.parse_args(argv)
    folder = Path(arguments.folder).resolve()
    work = Path(arguments.keep).resolve() if arguments.keep else Path(tempfile.mkdtemp(prefix="pqax-mutations-"))
    work.mkdir(parents=True, exist_ok=True)
    try:
        runners = footer_check.Runners(work)
        runners.prepare()
        records = list(mutations(folder, arguments.seed, arguments.flips))
        print(f"{len(records)} mutations of {len(bases(folder))} footers of {folder}, seed {arguments.seed}")
        failures, stats = footer_check.compare_footers(records, runners, None, "mutations")
        footer_check.print_stats(stats)
        heavy = []
        results: dict = {}
        footer_check.Runners._load(work / "mutations.dotnet.jsonl", results)
        for index, item in results.items():
            # The reader reads the footer that the length states, up to 64 MiB, into two buffers: the end of the file
            # and the whole footer. Beyond them, a read takes at most a fixed multiple of the bytes that it parses.
            data = records[index].data
            stated = struct.unpack("<i", data[-8:-4])[0] if len(data) >= 8 else 0
            footer = max(0, min(stated, footer_check_limit(records[index]), 64 << 20))
            allocated = item.get("allocated") or 0
            if allocated > 2 * footer + 64 * len(data) + (16 << 20):
                heavy.append(f"{records[index].name}: C# allocated {allocated:,} bytes for a footer of {footer:,} "
                             f"bytes and {len(data):,} bytes kept")
        failures += heavy
        if failures:
            print(f"{len(failures)} failure(s):")
            for failure in failures[:300]:
                print(f"  {failure}")
            if len(failures) > 300:
                print(f"  and {len(failures) - 300} more")
            return 1
        print("Every mutation gave the same outcome in both readers, a footer or a footer exception.")
        return 0
    finally:
        if not arguments.keep:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
