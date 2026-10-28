"""Rebuilds the samples and the expected results of the PQAX conformance kit.

Usage: python make_kit.py

Input: the probe dump of the synthetic fixture, in fixtures/probe-dump.
Output, which replaces everything in these folders:

    samples/*.pqax, samples/*.vpax      valid files, which every reader reads and every validator accepts
    samples/invalid/*.pqax              files that break one rule each, which every validator rejects
    samples/invalid/expected.json       the codes that a validator must report for each invalid file
    expected/<sample>.json              what a reader must get from each valid file

It also writes samples/diff-before.pqax and samples/diff-after.pqax, the two footprints of the comparison test, and
their expected results, with make_diff.py and the writer of the Python package. make_diff.py writes their expected diff.

The script uses the Python standard library and the Python package in src/python. probe_footprint.py maps the probe
dump to the tables of the format and adds their rows to pqax.Builder, the writer of the package, from which the script
takes each table and lays out the parts of each sample itself. The mapping comes from build_pqax.py, the builder of the
prototype in research/prototype, which the script used to import.

The output is deterministic. Every zip entry has the same date and attributes, and the entries are written in a
fixed order, so two runs with the same Python write the same bytes. Another zlib can compress differently, which
changes the bytes of a sample but not its content or its expected results.

zipfile cannot write an encrypted entry or ZIP64 records in a small archive, so the samples of container.encrypted and
container.zip64 are crafted byte by byte from sample.pqax, and zipfile then reads them back to check them.
"""
import collections
import copy
import glob
import io
import json
import os
import re
import struct
import sys
import zipfile

KIT = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(KIT, "..", "src", "python")))
import pqax  # noqa: E402
import probe_footprint  # noqa: E402

FORMAT_VERSION = pqax.FORMAT_VERSION
ROWS_PER_PART = pqax.ROWS_PER_PART
ZIP_DATE = (2026, 10, 7, 0, 0, 0)
STORED, DEFLATED, BZIP2 = zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED, zipfile.ZIP_BZIP2
BOM = b"\xef\xbb\xbf"
SUMMARY_TABLES = pqax.SUMMARY_TABLES
DETAIL_TABLES = pqax.DETAIL_TABLES
CONTENT_TYPES = (b'<?xml version="1.0" encoding="utf-8"?>'
                 b'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                 b'<Default Extension="json" ContentType="application/json" />'
                 b'<Default Extension="pqax" ContentType="application/zip" /></Types>')


# ---- input ----

def load_input():
    """The tables of the probe dump, sorted by their id, which numbers them, and the collection of the manifest."""
    folder = os.path.join(KIT, "fixtures", "probe-dump")
    dumps = {}
    for path in glob.glob(os.path.join(folder, "[LW]*.json")):
        with open(path, encoding="utf-8") as handle:
            table = json.load(handle)
        dumps[table["id"]] = table
    with open(os.path.join(folder, "index.json"), encoding="utf-8") as handle:
        index = json.load(handle)
    # The probe writes the time with seven decimals. The format wants three.
    moment = re.fullmatch(r"([0-9-]+T[0-9:]+)(?:[.]([0-9]+))?Z", index["collectedAtUtc"])
    collection = {"collectorApp": "pqax-conformance-kit", "collectorAppVersion": FORMAT_VERSION,
                  "completedAt": f"{moment[1]}.{(moment[2] or '').ljust(3, '0')[:3]}Z", "level": 2,
                  "includesDataValues": False, "includesPartitionValues": False,
                  "requestCount": index["requests"], "bytesRead": index["bytesRead"]}
    return [dumps[key] for key in sorted(dumps)], collection


DUMPS, COLLECTION = load_input()


# ---- writing ----
# A file is a list of entries: [name, payload, method]. The payload is a JSON value, or bytes written as they are.

def compact(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode("utf-8")


def zip_bytes(entries):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        for name, payload, method in entries:
            info = zipfile.ZipInfo(name, date_time=ZIP_DATE)
            info.create_system = 3
            info.external_attr = 0o644 << 16
            data = payload if isinstance(payload, bytes) else compact(payload)
            archive.writestr(info, data, compress_type=method, compresslevel=None if method == STORED else 9)
    return buffer.getvalue()


# ---- archives crafted byte by byte ----
# zipfile cannot write an encrypted entry, or ZIP64 records in a small archive. These functions read an archive that
# zip_bytes wrote, which has no comment, no data descriptor, and no ZIP64, and write it again record by record, so that
# a sample can differ from it in one structure of the container. write_records(zip_records(data)) gives data back.

LOCAL_HEADER = struct.Struct("<IHHHHHIIIHH")
CENTRAL_HEADER = struct.Struct("<IHHHHHHIIIHHHHHII")
END_RECORD = struct.Struct("<IHHHHIIH")
ZIP64_END_RECORD = struct.Struct("<IQHHIIQQQQ")
ZIP64_LOCATOR = struct.Struct("<IIQI")
ZIP64_EXTRA_FIELD = 0x0001


def zip_records(data):
    """The entries of an archive, in the order of its central directory: the fields of the central directory record of
    each, its name, its extra fields in the directory and in the local header, and its data as stored."""
    signature, _, _, entries, count, size, offset, comment = END_RECORD.unpack_from(data, len(data) - END_RECORD.size)
    assert signature == 0x06054B50 and entries == count and comment == 0 and offset + size == len(data) - END_RECORD.size
    records = []
    position = offset
    for _ in range(count):
        (signature, made_by, needed, flags, method, time, date, crc, compressed, size, name_length, extra_length,
         comment_length, _, internal, external, local) = CENTRAL_HEADER.unpack_from(data, position)
        assert signature == 0x02014B50 and comment_length == 0
        start = position + CENTRAL_HEADER.size
        header = LOCAL_HEADER.unpack_from(data, local)
        assert header[0] == 0x04034B50 and not flags & 0x8
        data_start = local + LOCAL_HEADER.size + header[9] + header[10]
        records.append({"madeBy": made_by, "needed": needed, "flags": flags, "method": method, "time": time,
                        "date": date, "crc": crc, "size": size, "name": data[start:start + name_length],
                        "extra": data[start + name_length:start + name_length + extra_length],
                        "localExtra": data[local + LOCAL_HEADER.size + header[9]:data_start],
                        "internal": internal, "external": external,
                        "data": data[data_start:data_start + compressed]})
        position = start + name_length + extra_length
    return records


def write_records(records, zip64_end=False):
    """An archive of the records: the local headers and the data, the central directory, and the end record, which
    zip64_end precedes with a ZIP64 end of central directory record and its locator. The end record keeps the real
    counts, size, and offset, so the archive can still be read without ZIP64."""
    out = bytearray()
    offsets = []
    for record in records:
        offsets.append(len(out))
        out += LOCAL_HEADER.pack(0x04034B50, record["needed"], record["flags"], record["method"], record["time"],
                                 record["date"], record["crc"], len(record["data"]), record["size"], len(record["name"]),
                                 len(record["localExtra"]))
        out += record["name"] + record["localExtra"] + record["data"]
    directory = len(out)
    for record, offset in zip(records, offsets):
        out += CENTRAL_HEADER.pack(0x02014B50, record["madeBy"], record["needed"], record["flags"], record["method"],
                                   record["time"], record["date"], record["crc"], len(record["data"]), record["size"],
                                   len(record["name"]), len(record["extra"]), 0, 0, record["internal"],
                                   record["external"], offset)
        out += record["name"] + record["extra"]
    size = len(out) - directory
    if zip64_end:
        end64 = len(out)
        out += ZIP64_END_RECORD.pack(0x06064B50, ZIP64_END_RECORD.size - 12, 3 << 8 | 45, 45, 0, 0, len(records),
                                     len(records), size, directory)
        out += ZIP64_LOCATOR.pack(0x07064B50, 0, end64, 1)
    out += END_RECORD.pack(0x06054B50, 0, 0, len(records), len(records), size, directory, 0)
    return bytes(out)


def record_of(records, name):
    return next(record for record in records if record["name"] == name.encode("ascii"))


def zip_crypto(data, password, crc):
    """data encrypted with the traditional PKWARE encryption of section 6.1 of the zip specification, APPNOTE.TXT,
    after its 12-byte encryption header. The last byte of the header is the high byte of the CRC-32 of the entry, which
    a reader compares to check the password; the 11 others are random in a real archive, and fixed here, so that the
    sample is the same at every run."""
    table = []
    for byte in range(256):
        value = byte
        for _ in range(8):
            value = (value >> 1) ^ 0xEDB88320 if value & 1 else value >> 1
        table.append(value)
    keys = [0x12345678, 0x23456789, 0x34567890]

    def update(byte):
        keys[0] = table[(keys[0] ^ byte) & 0xFF] ^ (keys[0] >> 8)
        keys[1] = ((keys[1] + (keys[0] & 0xFF)) * 134775813 + 1) & 0xFFFFFFFF
        keys[2] = table[(keys[2] ^ (keys[1] >> 24)) & 0xFF] ^ (keys[2] >> 8)

    for byte in password:
        update(byte)
    out = bytearray()
    for byte in b"pqax-kit-v1" + bytes([crc >> 24]) + data:
        key = keys[2] | 2
        out.append(byte ^ (((key * (key ^ 1)) >> 8) & 0xFF))
        update(byte)
    return bytes(out)


def same_entries(crafted, original, password=None):
    """Whether zipfile reads every entry of a crafted archive as the entry of the archive it comes from."""
    with zipfile.ZipFile(io.BytesIO(crafted)) as left, zipfile.ZipFile(io.BytesIO(original)) as right:
        return left.namelist() == right.namelist() and all(
            left.read(name, pwd=password) == right.read(name) for name in right.namelist())


def footprint(depth, rows_per_part=None, version=FORMAT_VERSION):
    """The entries of a PQAX file at a depth of probe_footprint, with the parts of section 7.2 of the serialization.

    The tables come from the pqax.Builder that probe_footprint fills from the probe dump, after the checks of its
    writer. rows_per_part maps a detail table to a limit lower than 250,000, to split it in several parts. version is
    the format version that the file is written in: 0.1 leaves out the fields that 0.2 added."""
    collection = dict(COLLECTION, collectorAppVersion=version)
    builder = probe_footprint.footprint(copy.deepcopy(DUMPS), depth, collection, version, pqax.Builder)
    builder.write(io.BytesIO())  # raises pqax.WriteError for an id, a required field, or an integer that is wrong
    counts = {name: builder.row_count(name) for name in SUMMARY_TABLES + DETAIL_TABLES}
    tables = {name: builder.table(name) for name in counts}
    parts = [("summary.json", [(name, 0, counts[name]) for name in SUMMARY_TABLES if counts[name]])]
    for name in DETAIL_TABLES:
        limit = (rows_per_part or {}).get(name, ROWS_PER_PART)
        for start in range(0, counts[name], limit):
            part = f"{name}.json" if counts[name] <= limit else f"{name}.{start // limit + 1}.json"
            parts.append((part, [(name, start, min(counts[name], start + limit))]))
    manifest = {"format": "pqax", "formatVersion": version, "collection": collection,
                "parts": [{"name": part, "tables": {name: end - start for name, start, end in content}}
                          for part, content in parts]}
    entries = [["manifest.json", manifest, DEFLATED]]
    for part, content in parts:
        entries.append([part, {name: columnar(tables[name], start, end) for name, start, end in content}, DEFLATED])
    return entries


def columnar(table, start, end):
    """The rows of a table from start to end, as a table of the format: one array for each field that has a value in
    those rows, in the order of the fields of the table."""
    return {field: values[start:end] for field, values in table.items()
            if any(value is not None for value in values[start:end])}


def entry(entries, name):
    return next(e for e in entries if e[0] == name)


def doc(entries, name):
    return entry(entries, name)[1]


def descriptor(entries, part):
    """The member of the manifest that lists a part."""
    return next(p for p in doc(entries, "manifest.json")["parts"] if p["name"] == part)


def drop(entries, part):
    """Removes a part from the archive and from the manifest."""
    manifest = doc(entries, "manifest.json")
    manifest["parts"] = [p for p in manifest["parts"] if p["name"] != part]
    entries[:] = [e for e in entries if e[0] != part]


def append_row(table, **values):
    """Adds a row to a table of a part: a value for each field, null for the fields not given."""
    for field, column in table.items():
        if isinstance(column, list):
            column.append(values.get(field))


# ---- valid samples ----

def extensions(entries):
    """What a producer or a later version may add. Every reader of this version reads the file as sample.pqax."""
    manifest = doc(entries, "manifest.json")
    summary = doc(entries, "summary.json")
    manifest["acme_build"] = {"tool": "acme-writer", "number": 7}                  # a member of the manifest
    descriptor(entries, "summary.json")["acme_checksum"] = "c3a1"                   # members of parts in the manifest
    descriptor(entries, "files.json")["acme_checksum"] = "5f0c"
    summary["acme_reviews"] = {"table": [1, 2], "note": ["checked", "checked"], "score": [4, 3]}    # a table of a producer
    descriptor(entries, "summary.json")["tables"]["acme_reviews"] = 2
    summary["tables"]["acme_score"] = [7, 9]                                        # a field of a producer, in the summary
    files = doc(entries, "files.json")["files"]
    files["acme_tier"] = ["hot"] * len(files["id"])                                 # a field of a producer, in a detail part
    chunks = doc(entries, "columnChunks.json")["columnChunks"]
    lists = []
    for value in chunks["encodings"]:
        if value not in lists:
            lists.append(value)
    # A member that is not an array: a later form of the field encodings. A reader of this version ignores it,
    # so for that reader columnChunks has no encodings.
    chunks["encodings"] = {"dictionary": lists, "indexes": [lists.index(value) for value in chunks["encodings"]]}
    summary["tables"]["fileSizes"][0]["ninetiethPercentile"] = summary["tables"]["fileSizes"][0]["maximum"]   # in a Distribution
    entries.append(["acme_extra.json", {"acme_details": {"table": [1, 1, 2], "label": ["a", "b", "c"]}}, DEFLATED])  # a part
    doc(entries, "manifest.json")["parts"].append({"name": "acme_extra.json", "tables": {"acme_details": 3}, "acme_version": 2})
    entries.append(["acme_readme.txt", b"Added by a producer. A reader ignores an entry that the manifest does not list.\n", DEFLATED])
    return entries


def later_depth(entries):
    tables = doc(entries, "summary.json")["tables"]
    tables["detailDepth"] = ["Pages"] * len(tables["id"])
    return entries


def later_version(entries):
    doc(entries, "manifest.json")["formatVersion"] = "0.9"
    return entries


def vpax(pqax, method):
    """A zip shaped like a VPAX package, with a placeholder model. It is not a real VPAX."""
    return zip_bytes([["[Content_Types].xml", CONTENT_TYPES, DEFLATED], ["DaxModel.json", b"{}", DEFLATED],
                      ["Footprint.pqax", pqax, method]])


def valid_samples(bases):
    sample = bases["columnChunks"]
    files = {
        "sample.pqax": zip_bytes(sample),
        "format-0.1.pqax": zip_bytes(footprint("columnChunks", version="0.1")),
        "summary-only.pqax": zip_bytes(bases["summary"]),
        "files.pqax": zip_bytes(bases["files"]),
        "row-groups.pqax": zip_bytes(bases["rowGroups"]),
        "multipart.pqax": zip_bytes(footprint("columnChunks", {"files": 3, "columnChunks": 20})),
        "extensions.pqax": zip_bytes(extensions(copy.deepcopy(sample))),
        "later-depth.pqax": zip_bytes(later_depth(copy.deepcopy(sample))),
        "later-version.pqax": zip_bytes(later_version(copy.deepcopy(sample))),
    }
    files["embedded-stored.vpax"] = vpax(files["sample.pqax"], STORED)
    files["embedded-deflated.vpax"] = vpax(files["sample.pqax"], DEFLATED)
    return files


# ---- invalid samples ----

def invalid_samples(bases):
    """Each case: the file name, the codes that a validator must report at least, the change, and the entries, or the
    bytes of an archive crafted byte by byte."""
    cases = []

    def case(name, codes, change, base="columnChunks"):
        def register(mutate):
            entries = copy.deepcopy(bases[base])
            mutate(entries)
            cases.append((name, codes, change, entries))
            return mutate
        return register

    def crafted(name, codes, change):
        """A case whose change is in the bytes of the container, which zipfile cannot write: craft gets the bytes of
        sample.pqax and returns those of the sample."""
        def register(craft):
            original = zip_bytes(bases["columnChunks"])
            assert write_records(zip_records(original)) == original
            cases.append((name, codes, change, craft(original)))
            return craft
        return register

    @case("container.method.pqax", ["container.method"], "commits.json is compressed with bzip2, method 12.")
    def _(e):
        entry(e, "commits.json")[2] = BZIP2

    @crafted("container.encrypted.pqax", ["container.encrypted"],
             "commits.json is encrypted with the traditional PKWARE encryption, with the password pqax: bit 0 of its "
             "flags is set, and its data is the 12-byte encryption header followed by the encrypted deflated data.")
    def _(original):
        records = zip_records(original)
        commits = record_of(records, "commits.json")
        commits["flags"] |= 0x1
        commits["data"] = zip_crypto(commits["data"], b"pqax", commits["crc"])
        data = write_records(records)
        assert same_entries(data, original, password=b"pqax")
        return data

    @crafted("container.zip64.pqax", ["container.zip64"],
             "A ZIP64 end of central directory record and its locator precede the end record, which keeps the real "
             "counts, size, and offset, so the archive can be read without ZIP64.")
    def _(original):
        data = write_records(zip_records(original), zip64_end=True)
        assert same_entries(data, original)
        return data

    @crafted("container.zip64.extraField.pqax", ["container.zip64"],
             "The central directory gives commits.json a ZIP64 extra field, header 0x0001, with no data, since its "
             "sizes and offset fit in 32 bits.")
    def _(original):
        records = zip_records(original)
        record_of(records, "commits.json")["extra"] = struct.pack("<HH", ZIP64_EXTRA_FIELD, 0)
        data = write_records(records)
        assert same_entries(data, original)
        return data

    @case("container.caseCollision.pqax", ["container.caseCollision"],
          "An entry Summary.json, a copy of summary.json that the manifest does not list, differs from summary.json only in case.")
    def _(e):
        e.append(["Summary.json", copy.deepcopy(doc(e, "summary.json")), DEFLATED])

    @case("container.partName.pqax", ["container.partName"],
          "The part commits.json is named commits 1.json, in the archive and in the manifest. A space is not allowed.")
    def _(e):
        entry(e, "commits.json")[0] = "commits 1.json"
        descriptor(e, "commits.json")["name"] = "commits 1.json"

    @case("container.manifestMissing.pqax", ["container.manifestMissing"], "The archive has no manifest.json entry.")
    def _(e):
        e[:] = [x for x in e if x[0] != "manifest.json"]

    @case("json.invalid.pqax", ["json.invalid"], "commits.json is cut in the middle, so it is not valid JSON.")
    def _(e):
        text = compact(doc(e, "commits.json"))
        entry(e, "commits.json")[1] = text[:len(text) // 2]

    @case("json.topLevel.pqax", ["json.topLevel"], "The top level of commits.json is an array that holds the object of the part.")
    def _(e):
        entry(e, "commits.json")[1] = [doc(e, "commits.json")]

    @case("json.bom.pqax", ["json.bom"], "summary.json starts with the byte order mark EF BB BF.")
    def _(e):
        entry(e, "summary.json")[1] = BOM + compact(doc(e, "summary.json"))

    @case("json.duplicateMember.pqax", ["json.duplicateMember"],
          "The commits table in commits.json has the member table twice, with the same array.")
    def _(e):
        text = compact(doc(e, "commits.json")).decode("utf-8")
        table = json.dumps(doc(e, "commits.json")["commits"]["table"], separators=(",", ":"))
        prefix = '{"commits":{"table":' + table
        assert text.startswith(prefix)
        entry(e, "commits.json")[1] = (prefix + ',"table":' + table + text[len(prefix):]).encode("utf-8")

    @case("json.integer.pqax", ["json.integer"], "rowGroups.rowCount of the first row group is 2048.5.")
    def _(e):
        doc(e, "rowGroups.json")["rowGroups"]["rowCount"][0] += 0.5

    @case("json.integerRange.pqax", ["json.integerRange"],
          "rowGroups.uncompressedSize of the first row group is 9007199254740993, which is 2^53 + 1.")
    def _(e):
        doc(e, "rowGroups.json")["rowGroups"]["uncompressedSize"][0] = 2 ** 53 + 1

    @case("json.timestamp.pqax", ["json.timestamp"], "files.modifiedAt of the first file has no decimals: 2023-11-14T22:15:00Z.")
    def _(e):
        files = doc(e, "files.json")["files"]
        files["modifiedAt"][0] = files["modifiedAt"][0][:-5] + "Z"

    @case("json.timestamp.nonLiveOldestRemovedAt.pqax", ["json.timestamp"],
          "tables.nonLiveOldestRemovedAt of the first table has no decimals: 2023-11-14T22:14:10Z.")
    def _(e):
        tables = doc(e, "summary.json")["tables"]
        tables["nonLiveOldestRemovedAt"][0] = tables["nonLiveOldestRemovedAt"][0][:-5] + "Z"

    @case("json.timestamp.partitionLastModifiedAt.pqax", ["json.timestamp"],
          "partitions.lastModifiedAt of the first partition has no decimals: 2023-11-14T22:15:00Z.")
    def _(e):
        partitions = doc(e, "partitions.json")["partitions"]
        partitions["lastModifiedAt"][0] = partitions["lastModifiedAt"][0][:-5] + "Z"

    @case("json.type.pqax", ["json.type"], "files.footerSize of the first file is the string \"1139\", where the model says integer.")
    def _(e):
        files = doc(e, "files.json")["files"]
        files["footerSize"][0] = str(files["footerSize"][0])

    @case("json.type.unreferencedFileCount.pqax", ["json.type"],
          "tables.unreferencedFileCount of the first table is the string \"1\", where the model says integer.")
    def _(e):
        tables = doc(e, "summary.json")["tables"]
        tables["unreferencedFileCount"][0] = str(tables["unreferencedFileCount"][0])

    @case("json.type.map.pqax", ["json.type"],
          "tables.properties of the first table is an array of key and value pairs, where the model says map.")
    def _(e):
        properties = doc(e, "summary.json")["tables"]["properties"]
        properties[0] = [[key, value] for key, value in properties[0].items()]

    @case("manifest.required.pqax", ["manifest.required"], "The manifest has no collection.")
    def _(e):
        del doc(e, "manifest.json")["collection"]

    @case("manifest.required.level.pqax", ["manifest.required"], "collection has no level.")
    def _(e):
        del doc(e, "manifest.json")["collection"]["level"]

    @case("manifest.required.partTables.pqax", ["manifest.required"], "The element of parts for commits.json has no tables.")
    def _(e):
        del descriptor(e, "commits.json")["tables"]

    @case("manifest.format.pqax", ["manifest.format"], "format is vpax.")
    def _(e):
        doc(e, "manifest.json")["format"] = "vpax"

    @case("manifest.formatVersion.pqax", ["manifest.formatVersion"], "formatVersion is 0.1.0, three numbers.")
    def _(e):
        doc(e, "manifest.json")["formatVersion"] = "0.1.0"

    @case("manifest.versionUnknown.pqax", ["manifest.versionUnknown"], "formatVersion is 9.0.")
    def _(e):
        doc(e, "manifest.json")["formatVersion"] = "9.0"

    @case("manifest.firstPart.pqax", ["manifest.firstPart"], "The manifest lists files.json first and summary.json second.")
    def _(e):
        parts = doc(e, "manifest.json")["parts"]
        parts.insert(0, parts.pop(next(i for i, p in enumerate(parts) if p["name"] == "files.json")))

    @case("manifest.duplicatePart.pqax", ["manifest.duplicatePart"], "The manifest lists commits.json twice, at the end.")
    def _(e):
        doc(e, "manifest.json")["parts"].append(copy.deepcopy(descriptor(e, "commits.json")))

    @case("manifest.partMissing.pqax", ["manifest.partMissing"], "The manifest lists commits.json, and the archive has no such entry.")
    def _(e):
        e[:] = [x for x in e if x[0] != "commits.json"]

    @case("part.tables.pqax", ["part.tables"],
          "The manifest states 5 rows of operations in summary.json, and summary.json has no operations.")
    def _(e):
        del doc(e, "summary.json")["operations"]

    @case("part.kind.detail-in-summary.pqax", ["part.kind"],
          "The commits table is in summary.json, and the manifest says so. There is no commits.json.")
    def _(e):
        doc(e, "summary.json")["commits"] = doc(e, "commits.json")["commits"]
        descriptor(e, "summary.json")["tables"]["commits"] = descriptor(e, "commits.json")["tables"]["commits"]
        drop(e, "commits.json")

    @case("part.kind.summary-in-detail.pqax", ["part.kind"],
          "The operations table is in a part of its own, operations.json, and the manifest says so.")
    def _(e):
        operations = doc(e, "summary.json").pop("operations")
        count = descriptor(e, "summary.json")["tables"].pop("operations")
        e.append(["operations.json", {"operations": operations}, DEFLATED])
        doc(e, "manifest.json")["parts"].append({"name": "operations.json", "tables": {"operations": count}})

    @case("part.singleTable.pqax", ["part.singleTable"],
          "files.json holds the files and the rowGroups tables, and the manifest says so. There is no rowGroups.json.")
    def _(e):
        doc(e, "files.json")["rowGroups"] = doc(e, "rowGroups.json")["rowGroups"]
        descriptor(e, "files.json")["tables"]["rowGroups"] = descriptor(e, "rowGroups.json")["tables"]["rowGroups"]
        drop(e, "rowGroups.json")

    @case("part.tooLarge.pqax", ["part.tooLarge"],
          "commits.json holds 250,001 commits, each with only table 1 and commitId 1, and the manifest says so.")
    def _(e):
        rows = ROWS_PER_PART + 1
        entry(e, "commits.json")[1] = {"commits": {"table": [1] * rows, "commitId": ["1"] * rows}}
        descriptor(e, "commits.json")["tables"]["commits"] = rows

    @case("part.rowCount.manifest.pqax", ["part.rowCount"], "The manifest states 7 commits in commits.json, which holds 6.")
    def _(e):
        descriptor(e, "commits.json")["tables"]["commits"] += 1

    @case("part.rowCount.array.pqax", ["part.rowCount"], "commits.engine has 5 values, and the other arrays of commits have 6.")
    def _(e):
        doc(e, "commits.json")["commits"]["engine"].pop()

    @case("table.required.null.pqax", ["table.required"], "columns.name is null in the first row.")
    def _(e):
        doc(e, "summary.json")["columns"]["name"][0] = None

    @case("table.required.missing.pqax", ["table.required"], "columns has no dataType field.")
    def _(e):
        del doc(e, "summary.json")["columns"]["dataType"]

    @case("table.required.position.pqax", ["table.required"], "partitionFields has no position field, which serialization 5.3 requires.")
    def _(e):
        del doc(e, "summary.json")["partitionFields"]["position"]

    @case("table.required.operationName.pqax", ["table.required"], "operations.name is null in the second row.")
    def _(e):
        doc(e, "summary.json")["operations"]["name"][1] = None

    @case("table.idUnique.repeated.pqax", ["table.idUnique"],
          "sources has a third row whose id is 2, as the second. The manifest states 3 sources.")
    def _(e):
        append_row(doc(e, "summary.json")["sources"], id=2, kind="OneLake lakehouse", name="Fix_Copy")
        descriptor(e, "summary.json")["tables"]["sources"] += 1

    @case("table.idUnique.nonPositive.pqax", ["table.idUnique"], "sources has a third row whose id is 0. The manifest states 3 sources.")
    def _(e):
        append_row(doc(e, "summary.json")["sources"], id=0, kind="OneLake lakehouse", name="Fix_Copy")
        descriptor(e, "summary.json")["tables"]["sources"] += 1

    @case("table.reference.single.pqax", ["table.reference"], "files.writer of the first file is 99, and writers has no row 99.")
    def _(e):
        doc(e, "files.json")["files"]["writer"][0] = 99

    @case("table.reference.list.pqax", ["table.reference"], "tables.clusteringColumns of the first table is [99], and columns has no row 99.")
    def _(e):
        doc(e, "summary.json")["tables"]["clusteringColumns"][0] = [99]

    @case("depth.files.pqax", ["depth.files"], "In a file at the Files depth, the second table has detailDepth Summary.", base="files")
    def _(e):
        doc(e, "summary.json")["tables"]["detailDepth"][1] = "Summary"

    @case("depth.rowGroups.pqax", ["depth.rowGroups"], "In a file at the Row groups depth, the second table has detailDepth Files.",
          base="rowGroups")
    def _(e):
        doc(e, "summary.json")["tables"]["detailDepth"][1] = "Files"

    @case("depth.columnChunks.pqax", ["depth.columnChunks"],
          "In a file at the Column chunks depth, the second table has detailDepth Row groups.")
    def _(e):
        doc(e, "summary.json")["tables"]["detailDepth"][1] = "Row groups"

    for field, code in (("fileCount", "totals.fileCount"), ("fileSize", "totals.fileSize"), ("rowGroupCount", "totals.rowGroupCount"),
                        ("compressedSize", "totals.compressedSize")):
        @case(f"{code}.pqax", [code], f"tables.{field} of the first table is 1 more than the detail says.")
        def _(e, field=field):
            doc(e, "summary.json")["tables"][field][0] += 1

    @case("totals.columnCompressedSize.pqax", ["totals.columnCompressedSize"],
          "columns.compressedSize of the first column is 1 more than the sum over its column chunks.")
    def _(e):
        doc(e, "summary.json")["columns"]["compressedSize"][0] += 1

    return cases


# ---- expected results ----

def read_footprint(data):
    """Reads a PQAX file as a reader does: the manifest, then every part that it lists, in order.

    Returns the manifest, the rows of each table from the manifest, and each table as its fields with their values,
    concatenated over the parts. A field that a part leaves out has null in the rows of that part."""
    archive = zipfile.ZipFile(io.BytesIO(data))
    manifest = json.loads(archive.read("manifest.json"))
    rows = {}
    tables = {}
    for part in manifest["parts"]:
        content = json.loads(archive.read(part["name"]))
        for name, count in part["tables"].items():
            before = rows.get(name, 0)
            table = tables.setdefault(name, {})
            for field, values in content[name].items():
                if isinstance(values, list):
                    assert len(values) == count, (part["name"], name, field)
                    table.setdefault(field, [None] * before).extend(values)
            rows[name] = before + count
            for values in table.values():
                values.extend([None] * (rows[name] - len(values)))
    return manifest, rows, tables


def expected_results(file_name, data):
    """The expected results of a sample, as conformance/README.md defines them."""
    embedded_part = compression = None
    archive = zipfile.ZipFile(io.BytesIO(data))
    if "manifest.json" not in archive.namelist():
        embedded_part = "Footprint.pqax"
        compression = {STORED: "stored", DEFLATED: "deflated"}[archive.getinfo(embedded_part).compress_type]
        data = archive.read(embedded_part)
    manifest, rows, tables = read_footprint(data)

    def field(table, name):
        return tables.get(table, {}).get(name) or [None] * rows.get(table, 0)

    schema_table = dict(zip(field("physicalSchemas", "id"), field("physicalSchemas", "table")))
    physical_table = {p: schema_table[s] for p, s in zip(field("physicalColumns", "id"), field("physicalColumns", "physicalSchema"))}
    physical_column = dict(zip(field("physicalColumns", "id"), field("physicalColumns", "column")))
    chunk_count = collections.Counter()
    chunk_size = collections.Counter()
    column_size = collections.Counter()
    for physical, size in zip(field("columnChunks", "physicalColumn"), field("columnChunks", "compressedSize")):
        chunk_count[physical_table[physical]] += 1
        chunk_size[physical_table[physical]] += size
        if physical_column[physical] is not None:
            column_size[physical_column[physical]] += size
    largest = sorted(column_size.items(), key=lambda item: (-item[1], item[0]))[:3]
    return {
        "file": file_name,
        "embeddedPart": embedded_part,
        "embeddedCompression": compression,
        "format": manifest["format"],
        "formatVersion": manifest["formatVersion"],
        "parts": [part["name"] for part in manifest["parts"]],
        "rows": rows,
        "tablesFileSize": sum(v for v in field("tables", "fileSize") if v is not None),
        "tablesRowCount": sum(v for v in field("tables", "rowCount") if v is not None),
        "columnChunksCompressedSize": sum(field("columnChunks", "compressedSize")),
        "tables": [{"id": table, "fileCount": count, "columnChunkCount": chunk_count[table], "columnChunkCompressedSize": chunk_size[table]}
                   for table, count in zip(field("tables", "id"), field("tables", "fileCount"))],
        "largestColumns": [{"id": column, "compressedSize": size} for column, size in largest],
    }


def pretty(value, indent=0, width=120):
    """JSON with two spaces of indentation, with short objects and arrays on one line."""
    flat = json.dumps(value, ensure_ascii=False)
    if len(flat) + indent <= width or not isinstance(value, (dict, list)) or not value:
        return flat
    pad = " " * (indent + 2)
    if isinstance(value, dict):
        items = [f"{pad}{json.dumps(k)}: {pretty(v, indent + 2, width)}" for k, v in value.items()]
        return "{\n" + ",\n".join(items) + "\n" + " " * indent + "}"
    return "[\n" + ",\n".join(f"{pad}{pretty(v, indent + 2, width)}" for v in value) + "\n" + " " * indent + "]"


# ---- main ----

def clear(folder):
    os.makedirs(folder, exist_ok=True)
    for name in os.listdir(folder):
        path = os.path.join(folder, name)
        if os.path.isfile(path):
            os.remove(path)


def write_text(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text + "\n")


def main():
    samples_folder = os.path.join(KIT, "samples")
    invalid_folder = os.path.join(samples_folder, "invalid")
    expected_folder = os.path.join(KIT, "expected")
    for folder in (samples_folder, invalid_folder, expected_folder):
        clear(folder)

    bases = {depth: footprint(depth) for depth in probe_footprint.DEPTHS}
    for name, data in valid_samples(bases).items():
        with open(os.path.join(samples_folder, name), "wb") as handle:
            handle.write(data)
        result = expected_results(name, data)
        write_text(os.path.join(expected_folder, os.path.splitext(name)[0] + ".json"), pretty(result))
        print(f"{name:32} {len(data):>9,} bytes   {len(result['parts'])} parts, {sum(result['rows'].values()):,} rows")

    expected_codes = {}
    for name, codes, change, entries in invalid_samples(bases):
        data = entries if isinstance(entries, bytes) else zip_bytes(entries)
        with open(os.path.join(invalid_folder, name), "wb") as handle:
            handle.write(data)
        expected_codes[name] = {"codes": codes, "change": change}
        print(f"invalid/{name:40} {len(data):>9,} bytes   {', '.join(codes)}")
    write_text(os.path.join(invalid_folder, "expected.json"), pretty(dict(sorted(expected_codes.items()))))

    # The two footprints of the comparison test, written with pqax.Builder. make_diff.py writes their expected diff.
    import make_diff
    make_diff.write_samples()


if __name__ == "__main__":
    main()
