"""The Thrift compact protocol, the encoding of a Parquet footer, read from a buffer.

It reads field headers with short and long deltas, booleans in the header, zigzag varints, doubles, binaries,
lists, sets, maps, and structs, and it skips a value of any type without keeping it. Every length is checked against
the bytes left, so a damaged footer fails with a FooterError instead of a large allocation.
"""
from __future__ import annotations

import struct
from typing import Tuple

from ._issues import IssueCodes

STOP = 0
BOOLEAN_TRUE = 1
BOOLEAN_FALSE = 2
BYTE = 3
I16 = 4
I32 = 5
I64 = 6
DOUBLE = 7
BINARY = 8
LIST = 9
SET = 10
MAP = 11
STRUCT = 12
UUID = 13

INTEGERS = (BYTE, I16, I32, I64)
_MAX_DEPTH = 64
_DOUBLE = struct.Struct("<d")


class FooterError(Exception):
    """A footer that cannot be read: not Parquet, encrypted, too large, or invalid. ``code`` is the issue code."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


def invalid(message: str) -> FooterError:
    """An error for bytes that do not follow the protocol."""
    return FooterError(IssueCodes.FOOTER_INVALID, message)


def _truncated() -> FooterError:
    return FooterError(IssueCodes.FOOTER_INVALID, "The footer ends in the middle of a value.")


def is_boolean(kind: int) -> bool:
    """Whether a wire type is a boolean, which a field header carries with no payload."""
    return kind == BOOLEAN_TRUE or kind == BOOLEAN_FALSE


class Reader:
    """Reads the compact protocol from a range of a buffer."""

    __slots__ = ("buffer", "position", "end", "depth")

    def __init__(self, buffer: bytes, offset: int = 0, count: int = -1) -> None:
        self.buffer = buffer
        self.position = offset
        self.end = len(buffer) if count < 0 else offset + count
        self.depth = 0

    @property
    def remaining(self) -> int:
        """The bytes left."""
        return self.end - self.position

    def enter_struct(self) -> None:
        """Starts a struct: checks the nesting depth."""
        self.depth += 1
        if self.depth > _MAX_DEPTH:
            raise invalid(f"The footer nests structures more than {_MAX_DEPTH} levels deep.")

    def exit_struct(self) -> None:
        """Ends a struct."""
        self.depth -= 1

    def field_header(self, last_id: int) -> Tuple[int, int]:
        """The id and the wire type of the next field of a struct; the type is STOP at the end of the struct."""
        header = self.raw_byte()
        if header == STOP:
            return 0, STOP
        kind = header & 0x0F
        delta = header >> 4
        if delta == 0:
            field_id = self.i16()
        else:
            field_id = (last_id + delta + 0x8000) % 0x10000 - 0x8000  # a short, as in C#
        return field_id, kind

    def raw_byte(self) -> int:
        """Reads one byte."""
        if self.position >= self.end:
            raise _truncated()
        value = self.buffer[self.position]
        self.position += 1
        return value

    def varint32(self) -> int:
        """Reads an unsigned varint of up to 32 bits."""
        result = 0
        for shift in range(0, 35, 7):
            b = self.raw_byte()
            result |= (b & 0x7F) << shift
            if not b & 0x80:
                return result & 0xFFFFFFFF
        raise invalid("A varint of the footer is longer than 32 bits.")

    def varint64(self) -> int:
        """Reads an unsigned varint of up to 64 bits."""
        result = 0
        for shift in range(0, 70, 7):
            b = self.raw_byte()
            result |= (b & 0x7F) << shift
            if not b & 0x80:
                return result & 0xFFFFFFFFFFFFFFFF
        raise invalid("A varint of the footer is longer than 64 bits.")

    def i16(self) -> int:
        """Reads an i16 as a zigzag varint."""
        value = self.i32()
        if value < -0x8000 or value > 0x7FFF:
            raise invalid("An i16 of the footer is out of range.")
        return value

    def i32(self) -> int:
        """Reads an i32 as a zigzag varint."""
        n = self.varint32()
        value = (n >> 1) ^ -(n & 1)
        return value - 0x100000000 if value >= 0x80000000 else value

    def i64(self) -> int:
        """Reads an i64 as a zigzag varint."""
        n = self.varint64()
        return (n >> 1) ^ -(n & 1)

    def integer(self, kind: int) -> int:
        """Reads an integer field of any integer wire type: byte, i16, i32, or i64."""
        if kind == BYTE:
            value = self.raw_byte()
            return value - 256 if value > 127 else value
        if kind == I16 or kind == I32:
            return self.i32()
        if kind == I64:
            return self.i64()
        raise invalid(f"A field of the footer has wire type {kind} where an integer is expected.")

    def double(self) -> float:
        """Reads a double: 8 bytes, little endian."""
        self._need(8)
        value = _DOUBLE.unpack_from(self.buffer, self.position)[0]
        self.position += 8
        return value

    def binary_length(self) -> int:
        """Reads the length of a binary and checks it against the bytes left."""
        length = self.varint32()
        if length > self.remaining:
            raise _truncated()
        return length

    def string(self) -> str:
        """Reads a binary as UTF-8 text, with U+FFFD for bytes that are not UTF-8."""
        return self.utf8(self.binary_length())

    def binary(self) -> bytes:
        """Reads a binary as a copy of its bytes."""
        length = self.binary_length()
        value = bytes(self.buffer[self.position:self.position + length])
        self.position += length
        return value

    def skip_binary(self) -> int:
        """Skips a binary and returns its length."""
        length = self.binary_length()
        self.position += length
        return length

    def utf8(self, length: int) -> str:
        """Reads the bytes of a binary whose length was read with binary_length, as UTF-8 text."""
        self._need(length)
        value = bytes(self.buffer[self.position:self.position + length]).decode("utf-8", "replace")
        self.position += length
        return value

    def skip_bytes(self, length: int) -> None:
        """Skips bytes whose length was read with binary_length."""
        self._need(length)
        self.position += length

    def list_header(self) -> Tuple[int, int]:
        """The number of elements and their wire type, for a list or a set. The number is checked against the bytes left."""
        header = self.raw_byte()
        size = header >> 4
        element = header & 0x0F
        if size == 15:
            size = self.varint32()
            if size > 0x7FFFFFFF:
                raise invalid("A list of the footer is too long.")
        if size > self.remaining:  # every element takes at least one byte
            raise _truncated()
        return size, element

    def boolean_element(self) -> bool:
        """Reads a boolean element of a list, which takes one byte."""
        return self.raw_byte() == BOOLEAN_TRUE

    def skip(self, kind: int) -> None:
        """Skips a value of a field, of any wire type."""
        if kind == BOOLEAN_TRUE or kind == BOOLEAN_FALSE:
            return  # in a field header, a boolean has no payload
        if kind == BYTE:
            self._advance(1)
        elif kind == I16 or kind == I32 or kind == I64:
            self.varint64()
        elif kind == DOUBLE:
            self._advance(8)
        elif kind == BINARY:
            self.skip_binary()
        elif kind == LIST or kind == SET:
            size, element = self.list_header()
            for _ in range(size):
                self.skip_element(element)
        elif kind == MAP:
            size = self.varint32()
            if size == 0:
                return
            if size > self.remaining:
                raise _truncated()
            types = self.raw_byte()
            key, value = types >> 4, types & 0x0F
            for _ in range(size):
                self.skip_element(key)
                self.skip_element(value)
        elif kind == STRUCT:
            self.skip_struct()
        elif kind == UUID:
            self._advance(16)
        else:
            raise invalid(f"The footer has a value of unknown wire type {kind}.")

    def skip_element(self, kind: int) -> None:
        """Skips an element of a list, a set, or a map: a boolean element takes one byte there."""
        if is_boolean(kind):
            self._advance(1)
        else:
            self.skip(kind)

    def skip_struct(self) -> None:
        """Skips a struct, all its fields included."""
        self.enter_struct()
        last = 0
        while True:
            last, kind = self.field_header(last)
            if kind == STOP:
                break
            self.skip(kind)
        self.exit_struct()

    def _advance(self, count: int) -> None:
        self._need(count)
        self.position += count

    def _need(self, count: int) -> None:
        if count > self.remaining:
            raise _truncated()
