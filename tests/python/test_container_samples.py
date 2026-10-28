"""The three container samples of the kit, which make_kit.py crafts byte by byte from the entries of sample.pqax: an
encrypted entry, and two uses of ZIP64 that a reader does not need. The validator reports their codes, the reader opens
them, and their bytes are what the kit says, read here with struct and zipfile (the contract of the hardening before
1.0, section 5)."""
from __future__ import annotations

import io
import struct
import zipfile

import pytest

import pqax
from pqax_kit import (DETAIL_TABLES, FORMAT_TABLES, INVALID, SAMPLES, SUMMARY_TABLES, compute_shape, expected_by_name,
                      invalid_cases, shape_differences)

EXPECTED = expected_by_name()
CONTAINER = {
    "container.encrypted.pqax": "container.encrypted",
    "container.zip64.pqax": "container.zip64",
    "container.zip64.extraField.pqax": "container.zip64",
}
ZIP64 = ["container.zip64.pqax", "container.zip64.extraField.pqax"]

LOCAL_HEADER = 0x04034B50
CENTRAL_HEADER = 0x02014B50
END_RECORD = 0x06054B50
ZIP64_END_RECORD = 0x06064B50
ZIP64_LOCATOR = 0x07064B50
ZIP64_EXTRA = 0x0001


def read_table(doc, name):
    if name in SUMMARY_TABLES:
        return doc.summary.get(name, {})
    return doc.table(name)


# ---- a small zip parser, with struct ----

def end_record(data):
    """Where the end of central directory record is, and its fields."""
    offset = data.rfind(struct.pack("<I", END_RECORD))
    assert offset >= 0, "no end of central directory record"
    disk, directory_disk, disk_entries, entries, size, start, _ = struct.unpack_from("<HHHHIIH", data, offset + 4)
    return offset, {"disk": disk, "directoryDisk": directory_disk, "diskEntries": disk_entries, "entries": entries,
                    "size": size, "start": start}


def extra_fields(extra):
    """The fields of an extra block, as (header id, data)."""
    fields, position = [], 0
    while position + 4 <= len(extra):
        identifier, length = struct.unpack_from("<HH", extra, position)
        fields.append((identifier, extra[position + 4:position + 4 + length]))
        position += 4 + length
    assert position == len(extra), "an extra block that does not end with its last field"
    return fields


def central_directory(data):
    """The entries of the central directory, by name, with their flags, method, sizes, local header, and extra fields."""
    _, end = end_record(data)
    entries, position = {}, end["start"]
    for _ in range(end["entries"]):
        (signature, _, _, flags, method, _, _, _, compressed, size, name_length, extra_length, comment_length, _, _, _,
         local) = struct.unpack_from("<IHHHHHHIIIHHHHHII", data, position)
        assert signature == CENTRAL_HEADER, position
        name = data[position + 46:position + 46 + name_length].decode("utf-8")
        extra = data[position + 46 + name_length:position + 46 + name_length + extra_length]
        entries[name] = {"flags": flags, "method": method, "compressed": compressed, "size": size, "local": local,
                         "extra": extra_fields(extra)}
        position += 46 + name_length + extra_length + comment_length
    assert position == end["start"] + end["size"]
    return entries


def local_header(data, offset):
    """The fields of a local header."""
    signature, _, flags, method, _, _, _, compressed, size, name_length, extra_length = struct.unpack_from(
        "<IHHHHHIIIHH", data, offset)
    assert signature == LOCAL_HEADER, offset
    return {"flags": flags, "method": method, "compressed": compressed, "size": size,
            "name": data[offset + 30:offset + 30 + name_length].decode("utf-8")}


# ---- the kit, the validator, and the reader ----

def test_the_kit_lists_the_three_samples_with_their_codes():
    cases = invalid_cases()
    for name, code in CONTAINER.items():
        assert (INVALID / name).is_file(), name
        assert code in cases[name]["codes"], name


@pytest.mark.parametrize("name", sorted(CONTAINER))
def test_the_validator_reports_the_code(name):
    path = INVALID / name
    for source in (str(path), path.read_bytes(), io.BytesIO(path.read_bytes())):
        result = pqax.validate(source)
        assert CONTAINER[name] in {issue.code for issue in result.issues}, [issue.code for issue in result.issues]
        assert not result.is_valid


@pytest.mark.parametrize("name", sorted(CONTAINER))
def test_the_reader_opens_the_sample(name):
    path = INVALID / name
    for source in (str(path), path.read_bytes(), io.BytesIO(path.read_bytes())):
        with pqax.open(source) as doc:
            assert doc.manifest["format"] == "pqax"
            assert doc.row_count("commits") == 6


def test_an_encrypted_part_fails_only_the_read_of_its_table():
    with pqax.open(str(INVALID / "container.encrypted.pqax")) as doc, \
            pqax.open(str(SAMPLES / "sample.pqax")) as sample:
        assert doc.manifest == sample.manifest
        assert doc.summary == sample.summary
        for name in DETAIL_TABLES:
            if name != "commits":
                assert doc.table(name) == sample.table(name), name
        assert doc.row_count("commits") == sample.row_count("commits") == 6
        for attempt in range(2):
            with pytest.raises(pqax.PqaxError) as raised:
                doc.table("commits")
            assert raised.value.code == "container.encrypted", attempt


@pytest.mark.parametrize("name", ZIP64)
def test_a_sample_with_zip64_records_reads_as_sample(name):
    with pqax.open(str(INVALID / name)) as doc, pqax.open(str(SAMPLES / "sample.pqax")) as sample:
        shape = compute_shape(doc.manifest, doc.row_count, lambda table: read_table(doc, table))
        assert not shape_differences(shape, EXPECTED["sample"])
        assert doc.manifest == sample.manifest
        for table in FORMAT_TABLES:
            assert doc.table(table) == sample.table(table), table


# ---- the bytes of the samples ----

@pytest.mark.parametrize("name", sorted(CONTAINER))
def test_every_entry_is_an_entry_of_sample(name):
    data = (INVALID / name).read_bytes()
    with zipfile.ZipFile(io.BytesIO(data)) as archive, zipfile.ZipFile(SAMPLES / "sample.pqax") as sample:
        assert archive.namelist() == sample.namelist()
        for entry in sample.namelist():
            assert archive.read(entry, pwd=b"pqax") == sample.read(entry), entry


def test_commits_json_is_encrypted_with_the_password_pqax():
    data = (INVALID / "container.encrypted.pqax").read_bytes()
    entries = central_directory(data)
    for name, entry in entries.items():
        local = local_header(data, entry["local"])
        assert local["name"] == name
        encrypted = name == "commits.json"
        assert bool(entry["flags"] & 1) == encrypted, f"{name}: central directory"
        assert bool(local["flags"] & 1) == encrypted, f"{name}: local header"
    commits = entries["commits.json"]
    assert commits["method"] == 8
    # The data is the 12-byte encryption header, then the encrypted deflated data.
    assert commits["compressed"] > 12
    with zipfile.ZipFile(io.BytesIO(data)) as archive, zipfile.ZipFile(SAMPLES / "sample.pqax") as sample:
        with pytest.raises(RuntimeError):
            archive.read("commits.json")
        assert archive.read("commits.json", pwd=b"pqax") == sample.read("commits.json")
        info = archive.getinfo("commits.json")
        assert info.compress_type == zipfile.ZIP_DEFLATED
        assert info.compress_size == commits["compressed"]


def test_container_zip64_has_a_zip64_end_record_and_locator_consistent_with_the_end_record():
    data = (INVALID / "container.zip64.pqax").read_bytes()
    end_offset, end = end_record(data)
    # The end record keeps the real counts, size, and offset, so that a reader does not need ZIP64.
    assert end["entries"] == end["diskEntries"] == 7
    assert end["size"] != 0xFFFFFFFF and end["start"] != 0xFFFFFFFF
    assert (end["disk"], end["directoryDisk"]) == (0, 0)
    # The locator comes right before the end record and points to the ZIP64 end record.
    locator = end_offset - 20
    signature, record_disk, record_offset, disks = struct.unpack_from("<IIQI", data, locator)
    assert signature == ZIP64_LOCATOR
    assert (record_disk, disks) == (0, 1)
    (signature, record_size, _, _, disk, directory_disk, disk_entries, entries, size, start) = struct.unpack_from(
        "<IQHHIIQQQQ", data, record_offset)
    assert signature == ZIP64_END_RECORD
    # The record follows the central directory and ends where the locator starts.
    assert record_offset == end["start"] + end["size"]
    assert record_offset + 12 + record_size == locator
    assert record_size >= 44
    assert (disk, directory_disk) == (0, 0)
    assert (disk_entries, entries, size, start) == (end["diskEntries"], end["entries"], end["size"], end["start"])
    assert len(central_directory(data)) == 7


def test_container_zip64_extra_field_gives_commits_json_a_zip64_extra_field_with_no_data():
    data = (INVALID / "container.zip64.extraField.pqax").read_bytes()
    entries = central_directory(data)
    commits = entries["commits.json"]
    assert (ZIP64_EXTRA, b"") in commits["extra"]
    # Its sizes and offset fit in 32 bits, so the field has nothing to hold.
    assert 0xFFFFFFFF not in (commits["compressed"], commits["size"], commits["local"])
    for name, entry in entries.items():
        if name != "commits.json":
            assert ZIP64_EXTRA not in [identifier for identifier, _ in entry["extra"]], name


@pytest.mark.parametrize("name", ["container.encrypted.pqax", "container.zip64.extraField.pqax"])
def test_the_other_samples_have_no_zip64_locator(name):
    data = (INVALID / name).read_bytes()
    end_offset, end = end_record(data)
    # A locator would be the 20 bytes right before the end record, after the central directory.
    assert end_offset == end["start"] + end["size"]
    assert struct.unpack_from("<I", data, end_offset - 20)[0] != ZIP64_LOCATOR
