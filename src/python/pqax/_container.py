"""The zip container: opening a source, reading an entry, finding an embedded part, and finding ZIP64 structures."""
from __future__ import annotations

import builtins
import io
import os
import struct
import zipfile
import zlib
from typing import IO, Any, List, Optional, Tuple, Union

from ._errors import ContainerError, PartMissingError
from ._schema import MANIFEST_PART, MAX_PART_SIZE

# A path, the bytes of a file, or a binary file object.
Source = Union[str, os.PathLike, bytes, bytearray, memoryview, IO[bytes]]

_ZIP_ERRORS = (zipfile.BadZipFile, zipfile.LargeZipFile, EOFError, ValueError, struct.error, zlib.error)
_EOCD = b"PK\x05\x06"
_EOCD64_LOCATOR = b"PK\x06\x07"
_ZIP64_EXTRA = 0x0001


def open_file(source: Any) -> Tuple[IO[bytes], bool]:
    """A binary, seekable file object for a source, and whether the caller must close it."""
    if isinstance(source, (str, os.PathLike)):
        return builtins.open(source, "rb"), True
    if isinstance(source, (bytes, bytearray, memoryview)):
        return io.BytesIO(bytes(source)), True
    if isinstance(source, io.TextIOBase):
        raise TypeError("Expected a binary file object, not a text file object: open the file with mode 'rb'")
    if hasattr(source, "read"):
        seekable = getattr(source, "seekable", None)
        if seekable is not None and seekable():
            return source, False
        return io.BytesIO(source.read()), True
    raise TypeError(f"Expected a path, bytes, or a binary file object, not {type(source).__name__}")


def open_archive(source: Any) -> Tuple[zipfile.ZipFile, IO[bytes], bool]:
    """The zip archive of a source, its file object, and whether the caller must close that file object."""
    fp, owned = open_file(source)
    try:
        archive = zipfile.ZipFile(fp)
    except _ZIP_ERRORS + (OSError, NotImplementedError, RuntimeError) as error:
        if owned:
            fp.close()
        raise ContainerError(f"The file is not a zip archive ({error}).") from error
    except BaseException:
        if owned:
            fp.close()
        raise
    return archive, fp, owned


def compression_name(method: int) -> str:
    """The name of a compression method of a zip entry."""
    return {zipfile.ZIP_STORED: "stored", zipfile.ZIP_DEFLATED: "deflated"}.get(method, f"method {method}")


def read_entry(archive: zipfile.ZipFile, info: zipfile.ZipInfo, limit: Optional[int] = MAX_PART_SIZE) -> bytes:
    """The content of an entry, inflated when it is deflated."""
    if info.flag_bits & 0x1:
        raise ContainerError(f"The entry {info.filename} is encrypted.", code="container.encrypted")
    if limit is not None and info.file_size > limit:
        raise ContainerError(f"The entry {info.filename} holds {info.file_size:,} bytes, more than the limit of "
                             f"{limit:,} that this package reads.")
    try:
        return archive.read(info)
    except (RuntimeError, NotImplementedError) as error:
        raise ContainerError(f"The entry {info.filename} cannot be read: {error}") from error
    except _ZIP_ERRORS as error:
        raise ContainerError(f"The entry {info.filename} cannot be read: {error}") from error


def find_entry(archive: zipfile.ZipFile, name: str) -> Optional[zipfile.ZipInfo]:
    """The entry with a name, or with the same name in another case, as packages compare part names."""
    try:
        return archive.getinfo(name)
    except KeyError:
        pass
    wanted = name.lstrip("/").lower()
    for info in archive.infolist():
        if info.filename.lstrip("/").lower() == wanted:
            return info
    return None


def read_embedded(source: Any, part_name: str) -> Tuple[bytes, str, str]:
    """The bytes of a part of a package such as a VPAX, the name of its entry, and how it is compressed."""
    archive, fp, owned = open_archive(source)
    try:
        info = find_entry(archive, part_name)
        if info is None:
            hint = ""
            if MANIFEST_PART in archive.namelist():
                hint = " The archive has a manifest.json entry, so it may be a PQAX file itself: open it with pqax.open."
            raise PartMissingError(f"The package has no part named {part_name}.{hint}", code="embedded.partMissing")
        return read_entry(archive, info, limit=None), info.filename, compression_name(info.compress_type)
    finally:
        archive.close()
        if owned:
            fp.close()


def _has_extra(extra: bytes, header_id: int) -> bool:
    position = 0
    while position + 4 <= len(extra):
        identifier, length = struct.unpack_from("<HH", extra, position)
        if identifier == header_id:
            return True
        position += 4 + length
    return False


def zip64_findings(fp: IO[bytes], archive: zipfile.ZipFile) -> List[Tuple[Optional[str], str]]:
    """Where the archive uses ZIP64, as conformance/rules.md defines it: a ZIP64 end of central directory locator,
    or an entry of the central directory with a ZIP64 extra field. It returns the entry concerned, or None for the
    archive, and what was found. A ZIP64 field in a local header alone is not reported."""
    findings: List[Tuple[Optional[str], str]] = []
    try:
        fp.seek(0, io.SEEK_END)
        size = fp.tell()
        tail_size = min(size, 22 + 0xFFFF + 20)
        fp.seek(size - tail_size)
        tail = fp.read(tail_size)
    except (OSError, ValueError):
        tail = b""
    end = len(tail) - 22 if tail[-22:-18] == _EOCD else tail.rfind(_EOCD)
    if end >= 20 and tail[end - 20:end - 16] == _EOCD64_LOCATOR:
        findings.append((None, "The archive has a ZIP64 end of central directory locator."))
    for info in archive.infolist():
        if _has_extra(info.extra, _ZIP64_EXTRA):
            findings.append((info.filename, "The central directory gives the entry a ZIP64 extra field."))
    return findings
