"""The container check of cross_check.py: python -m pqax collect --adls and pqax collect --adls in C# on the same
synthetic container of Azure Data Lake Storage Gen2, which must give the same footprints.

The container is built in the work folder from the lakehouse table of fixtures/lakehouse and from synthetic logs and
footers:

    data/sales, data/eu/sales              copies of the lakehouse table of the fixture, at depths 1 and 2
    data/a/b/c/deep, data/a/b/c/d/deeper   Delta tables with one commit and no file, at depths 4 and 5
    data/raw                               two Parquet files of the fixture, _SUCCESS, _temporary, and files whose name
                                           starts with _ or . and ends with .parquet
    data/mixed/x.parquet, data/mixed/inner a folder of Parquet files with a subfolder, which is a table
    data/upper/DATA.PARQUET                a Parquet file whose extension is in upper case
    data/typed                             a footer with every physical type and annotation, lists, maps, structures,
                                           and a variant, and an older file with another schema
    data/bad                               a file that is no Parquet file, newer than a good one
    data/empty, data/other, data/events    an empty folder, other files, and the partitions year=2024 and year=2025
    data/_staging, data/.hidden            Delta tables in folders that the walk skips
    my data/raw                            a folder of Parquet files with a space in its name

A fake DFS endpoint on 127.0.0.1 serves the folder as the filesystem lake of the account pqaxtest: the listings of
the DFS API in pages of three entries, and the reads of ranges. It answers 403 to the filesystem denied and 401 to the
filesystem expired. Both command lines run in a process of their own, with URLs of the account,
https://pqaxtest.dfs.core.windows.net/lake/..., and an access token from a variable, and send their HTTPS requests to
the fake endpoint: C# through the HTTP client of CollectHost, given by a runner that the .NET 10 SDK runs as a
file-based app, and Python through http.client.HTTPSConnection, replaced in its process. So the URLs, the locations,
and the messages are those of the account.

For each case, the two command lines must end with the same exit code and print the same lines, apart from the order
of the lines of the tables done, which the threads choose, and their footprints must hold the same values in every
table and field, and in the collection except the members that name the collector and the times. A command line that
fails or that is refused must give the same first line of the error output.

Standard library only. cross_check.py runs it when both collectors can run.
"""
from __future__ import annotations

import datetime
import email.utils
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import urllib.parse
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Tuple

KIT = Path(__file__).resolve().parent
FIXTURE_TABLE = KIT / "fixtures" / "lakehouse" / "Fix_Lakehouse.Lakehouse" / "Tables" / "sales"
ACCOUNT = "https://pqaxtest.dfs.core.windows.net"
CONTAINER = "abfss://lake@pqaxtest.dfs.core.windows.net"
TOKEN_VARIABLE = "PQAX_CROSS_TOKEN"
TOKEN = "cross-check-token-not-a-secret"
OLDER = datetime.datetime(2026, 10, 1, 8, 0, tzinfo=datetime.timezone.utc).timestamp()
NEWER = OLDER + 3600
NOT_PARQUET = b"This text is not a Parquet file and must not be read."
TIMEOUT = 600
PROGRESS = re.compile(r"^  table \d+ of (\d+): (\d+) footers, (\d+) issues; \d+ requests so far$")

#: The collections: a name, the URL, and the options. Each command line writes <name>.pqax in a folder of its own.
COLLECTIONS = (
    ("folder", ACCOUNT + "/lake/data", []),
    ("root", CONTAINER + "/", []),
    ("deeper", ACCOUNT + "/lake/data/a/b/c/d", ["--level", "1", "--depth", "files"]),
    ("decoded", ACCOUNT + "/lake/my%20data", ["--max-footers", "1", "--parallelism", "1"]),
    ("rowgroups", CONTAINER + "/data/raw/../", ["--depth", "rowGroups", "--max-commits", "1"]),
)
#: The command lines that fail after the sign-in, or that are refused before it: the arguments after collect.
FAILURES = (
    ("no table", ["--adls", ACCOUNT + "/lake/data/other"]),
    ("no folder", ["--adls", ACCOUNT + "/lake/no-such-folder"]),
    ("403", ["--adls", ACCOUNT + "/denied/data"]),
    ("401", ["--adls", ACCOUNT + "/expired/data"]),
    ("a SAS token", ["--adls", ACCOUNT + "/lake/data?sv=2024-01-01&sig=SyntheticSignature0123"]),
    ("the blob host", ["--adls", "https://pqaxtest.blob.core.windows.net/lake/data"]),
    ("a OneLake host", ["--adls", "abfss://work@onelake.dfs.fabric.microsoft.com/Item.Lakehouse"]),
    ("http", ["--adls", "http://pqaxtest.dfs.core.windows.net/lake/data"]),
    ("a bad port", ["--adls", "https://pqaxtest.dfs.core.windows.net:99999/lake"]),
    ("a user", ["--adls", "https://someone@pqaxtest.dfs.core.windows.net/lake"]),
    ("no container", ["--adls", ACCOUNT + "/"]),
    ("no container left", ["--adls", ACCOUNT + "/lake/.."]),
    ("a .. after an encoded slash", ["--adls", ACCOUNT + "/lake/data%2F..%2Fother"]),
    ("an encoded backslash", ["--adls", ACCOUNT + "/lake/data%5Cother"]),
    ("an upper-case container", ["--adls", "abfss://Lake@pqaxtest.dfs.core.windows.net/data"]),
    ("a short container", ["--adls", ACCOUNT + "/ab/data"]),
    ("an item", ["--adls", ACCOUNT + "/lake/data", "--item", "Sales.Lakehouse"]),
    ("no value", ["--adls"]),
)


# ---- the container ----

LOG = (
    {"protocol": {"minReaderVersion": 1, "minWriterVersion": 2}},
    {"metaData": {"id": "00000000-0000-4000-8000-0000000000c1", "format": {"provider": "parquet", "options": {}},
                  "schemaString": json.dumps({"type": "struct", "fields": [
                      {"name": "id", "type": "long", "nullable": True, "metadata": {}}]}),
                  "partitionColumns": [], "configuration": {}, "createdTime": int(OLDER * 1000)}},
    {"commitInfo": {"timestamp": int(OLDER * 1000), "operation": "CREATE TABLE", "engineInfo": "Synthetic engine",
                    "isBlindAppend": True, "operationMetrics": {}}},
)


def build(root: Path) -> None:
    """Writes the container in root, which must not exist."""
    def write(path: str, data: bytes, moment: Optional[float] = None) -> None:
        target = root.joinpath(*path.split("/"))
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        if moment is not None:
            os.utime(target, (moment, moment))

    def delta(table: str) -> None:
        for path in sorted(FIXTURE_TABLE.rglob("*")):
            if path.is_file():
                write(f"{table}/{path.relative_to(FIXTURE_TABLE).as_posix()}", path.read_bytes())

    def log_only(table: str) -> None:
        write(f"{table}/_delta_log/{0:020d}.json", "".join(json.dumps(action) + "\n" for action in LOG).encode())

    def fixture_file(name: str, path: str, moment: float) -> None:
        write(path, (FIXTURE_TABLE / name).read_bytes(), moment)

    root.mkdir(parents=True)
    delta("data/sales")
    delta("data/eu/sales")
    log_only("data/a/b/c/deep")
    log_only("data/a/b/c/d/deeper")
    for folder in ("data/raw", "my data/raw"):
        fixture_file("part-0001.parquet", f"{folder}/part-0001.parquet", OLDER)
        fixture_file("part-0002.parquet", f"{folder}/part-0002.parquet", NEWER)
        write(f"{folder}/_SUCCESS", b"")
    for name in ("_temporary/0/part-0009.parquet", "_ignored.parquet", ".hidden.parquet"):
        write(f"data/raw/{name}", NOT_PARQUET, NEWER + 3600)
    fixture_file("part-0001.parquet", "data/mixed/x.parquet", OLDER)
    fixture_file("part-0001.parquet", "data/mixed/inner/y.parquet", OLDER)
    fixture_file("part-0002.parquet", "data/upper/DATA.PARQUET", OLDER)
    write("data/typed/a-newest.parquet", parquet_file(footer(TYPED)), NEWER)
    write("data/typed/b-older.parquet", parquet_file(footer([leaf("retired", "INT64")])), OLDER)
    fixture_file("part-0001.parquet", "data/bad/good.parquet", OLDER)
    write("data/bad/broken.parquet", NOT_PARQUET, NEWER)
    (root / "data" / "empty").mkdir()
    write("data/other/a.csv", b"id,name\n1,synthetic\n")
    write("data/other/b.json", b'{"id":1}\n')
    for partition in ("year=2024", "year=2025"):
        fixture_file("part-0001.parquet", f"data/events/{partition}/part-0001.parquet", OLDER)
    log_only("data/_staging/t")
    log_only("data/.hidden/t")
    write("data/readme.txt", b"A file next to the folders.")


# ---- a footer with every type ----

@dataclass(frozen=True)
class Field:
    """A field of a schema: a leaf with a physical type, or a group with children, and its annotations."""

    name: str
    type: Optional[str] = None
    repetition: Optional[str] = "OPTIONAL"
    children: Tuple["Field", ...] = ()
    converted: Optional[str] = None
    logical: Any = None
    type_length: Optional[int] = None
    scale: Optional[int] = None
    precision: Optional[int] = None


def leaf(name: str, physical: str, repetition: Optional[str] = "OPTIONAL", **annotations: Any) -> Field:
    return Field(name, physical, repetition, **annotations)


def group(name: str, *children: Field, repetition: Optional[str] = "OPTIONAL", **annotations: Any) -> Field:
    return Field(name, None, repetition, children, **annotations)


TYPED = (
    leaf("flag", "BOOLEAN", "REQUIRED"), leaf("count", "INT32"), leaf("tiny", "INT32", logical=("INTEGER", 8, True)),
    leaf("u16", "INT32", logical=("INTEGER", 16, False)), leaf("u32", "INT32", converted="UINT_32"),
    leaf("day", "INT32", logical="DATE"), leaf("d32", "INT32", converted="DECIMAL", precision=5, scale=1),
    leaf("time", "INT32", logical=("TIME", "MILLIS", True)), leaf("id", "INT64", "REQUIRED"),
    leaf("u64", "INT64", logical=("INTEGER", 64, False)), leaf("at", "INT64", logical=("TIMESTAMP", "MICROS", True)),
    leaf("local", "INT64", logical=("TIMESTAMP", "NANOS", False)), leaf("cms", "INT64", converted="TIMESTAMP_MILLIS"),
    leaf("d64", "INT64", logical=("DECIMAL", 18, 4)), leaf("legacy", "INT96"), leaf("ratio", "FLOAT"),
    leaf("amount", "DOUBLE"), leaf("name", "BYTE_ARRAY", logical="STRING"),
    leaf("cname", "BYTE_ARRAY", converted="UTF8"),
    leaf("kind", "BYTE_ARRAY", logical="ENUM"), leaf("doc", "BYTE_ARRAY", logical="JSON"),
    leaf("bson", "BYTE_ARRAY", logical="BSON"), leaf("blob", "BYTE_ARRAY"),
    leaf("dba", "BYTE_ARRAY", logical=("DECIMAL", 30, 6)),
    leaf("uuid", "FIXED_LEN_BYTE_ARRAY", type_length=16, logical="UUID"),
    leaf("half", "FIXED_LEN_BYTE_ARRAY", type_length=2, logical="FLOAT16"),
    leaf("price", "FIXED_LEN_BYTE_ARRAY", type_length=16, logical=("DECIMAL", 38, 18)),
    leaf("unannotated", "INT64", None),
    group("tags", group("list", leaf("element", "INT32", "REQUIRED"), repetition="REPEATED"), logical="LIST"),
    group("legacy_list", leaf("value", "INT64", "REPEATED"), converted="LIST"),
    group("points", group("array", leaf("x", "DOUBLE"), repetition="REPEATED"), converted="LIST"),
    group("pairs", group("pairs_tuple", leaf("a", "INT32"), repetition="REPEATED"), converted="LIST"),
    group("props", group("key_value", leaf("key", "BYTE_ARRAY", "REQUIRED", logical="STRING"), leaf("value", "INT64"),
                         repetition="REPEATED"), logical="MAP"),
    group("old_map", group("map", leaf("key", "BYTE_ARRAY", "REQUIRED", converted="UTF8"), leaf("value", "DOUBLE"),
                           repetition="REPEATED"), converted="MAP_KEY_VALUE"),
    group("where", leaf("a", "INT32", "REQUIRED"), group("inner", leaf("b", "BYTE_ARRAY", logical="STRING"))),
    leaf("numbers", "INT32", "REPEATED"),
    group("records", leaf("a", "INT32"), repetition="REPEATED"),
    group("payload", leaf("metadata", "BYTE_ARRAY", "REQUIRED"), leaf("value", "BYTE_ARRAY", "REQUIRED"),
          logical="VARIANT", repetition="REQUIRED"),
)

_PHYSICAL = ("BOOLEAN", "INT32", "INT64", "INT96", "FLOAT", "DOUBLE", "BYTE_ARRAY", "FIXED_LEN_BYTE_ARRAY")
_REPETITION = ("REQUIRED", "OPTIONAL", "REPEATED")
_CONVERTED = ("UTF8", "MAP", "MAP_KEY_VALUE", "LIST", "ENUM", "DECIMAL", "DATE", "TIME_MILLIS", "TIME_MICROS",
              "TIMESTAMP_MILLIS", "TIMESTAMP_MICROS", "UINT_8", "UINT_16", "UINT_32", "UINT_64", "INT_8", "INT_16",
              "INT_32", "INT_64", "JSON", "BSON", "INTERVAL")
_EMPTY_LOGICAL = {"STRING": 1, "MAP": 2, "LIST": 3, "ENUM": 4, "DATE": 6, "UNKNOWN": 11, "JSON": 12, "BSON": 13,
                  "UUID": 14, "FLOAT16": 15, "VARIANT": 16}
_UNITS = {"MILLIS": 1, "MICROS": 2, "NANOS": 3}


class Thrift:
    """Writes the Thrift compact protocol of parquet.thrift: enough of it for a FileMetaData."""

    TRUE, FALSE, BYTE, I32, I64, BINARY, LIST, STRUCT = 1, 2, 3, 5, 6, 8, 9, 12

    def __init__(self) -> None:
        self.out = bytearray()
        self.last = 0
        self.outer: List[int] = []

    def varint(self, value: int) -> "Thrift":
        while value >= 0x80:
            self.out.append((value & 0x7F) | 0x80)
            value >>= 7
        self.out.append(value)
        return self

    def header(self, field: int, kind: int) -> "Thrift":
        if 0 < field - self.last <= 15:
            self.out.append(((field - self.last) << 4) | kind)
        else:
            self.out.append(kind)
            self.varint((field << 1) ^ (field >> 63))
        self.last = field
        return self

    def i32(self, field: int, value: int) -> "Thrift":
        return self.header(field, self.I32).varint((value << 1) ^ (value >> 63))

    def i64(self, field: int, value: int) -> "Thrift":
        return self.header(field, self.I64).varint((value << 1) ^ (value >> 63))

    def byte(self, field: int, value: int) -> "Thrift":
        self.header(field, self.BYTE).out.append(value & 0xFF)
        return self

    def boolean(self, field: int, value: bool) -> "Thrift":
        return self.header(field, self.TRUE if value else self.FALSE)

    def text(self, value: str) -> "Thrift":
        data = value.encode("utf-8")
        self.varint(len(data)).out.extend(data)
        return self

    def binary(self, field: int, value: str) -> "Thrift":
        return self.header(field, self.BINARY).text(value)

    def list(self, field: int, element: int, count: int) -> "Thrift":
        self.header(field, self.LIST)
        if count < 15:
            self.out.append((count << 4) | element)
        else:
            self.out.append(0xF0 | element)
            self.varint(count)
        return self

    def begin(self, field: int) -> "Thrift":
        return self.header(field, self.STRUCT).element()

    def element(self) -> "Thrift":
        self.outer.append(self.last)
        self.last = 0
        return self

    def end(self) -> "Thrift":
        self.out.append(0)
        self.last = self.outer.pop() if self.outer else 0
        return self


def _logical(w: Thrift, logical: Any) -> None:
    w.begin(10)
    if isinstance(logical, str):
        w.begin(_EMPTY_LOGICAL[logical]).end()
    elif logical[0] == "DECIMAL":
        w.begin(5).i32(1, logical[2]).i32(2, logical[1]).end()
    elif logical[0] in ("TIME", "TIMESTAMP"):
        w.begin(7 if logical[0] == "TIME" else 8).boolean(1, logical[2])
        w.begin(2).begin(_UNITS[logical[1]]).end().end().end()
    else:
        w.begin(10).byte(1, logical[1]).boolean(2, logical[2]).end()
    w.end()


def footer(fields: Any) -> bytes:
    """A FileMetaData with these top-level fields and one row group of one row, with a column chunk for each leaf."""
    flat: List[Tuple[Field, Tuple[str, ...]]] = []

    def walk(field: Field, path: Tuple[str, ...]) -> None:
        flat.append((field, path))
        for child in field.children:
            walk(child, path + (child.name,))

    for field in fields:
        walk(field, (field.name,))
    leaves = [(field, path) for field, path in flat if field.type is not None]
    w = Thrift().i32(1, 2)
    w.list(2, Thrift.STRUCT, len(flat) + 1)
    w.element().binary(4, "schema").i32(5, len(fields)).end()
    for field, _ in flat:
        w.element()
        if field.type is not None:
            w.i32(1, _PHYSICAL.index(field.type))
        if field.type_length is not None:
            w.i32(2, field.type_length)
        if field.repetition is not None:
            w.i32(3, _REPETITION.index(field.repetition))
        w.binary(4, field.name)
        if field.type is None:
            w.i32(5, len(field.children))
        if field.converted is not None:
            w.i32(6, _CONVERTED.index(field.converted))
        if field.scale is not None:
            w.i32(7, field.scale)
        if field.precision is not None:
            w.i32(8, field.precision)
        if field.logical is not None:
            _logical(w, field.logical)
        w.end()
    w.i64(3, 1)
    w.list(4, Thrift.STRUCT, 1).element()
    w.list(1, Thrift.STRUCT, len(leaves))
    for index, (field, path) in enumerate(leaves):
        w.element().i64(2, 4).begin(3)
        w.i32(1, _PHYSICAL.index(field.type or "INT64"))
        w.list(2, Thrift.I32, 1).varint(0)
        w.list(3, Thrift.BINARY, len(path))
        for name in path:
            w.text(name)
        w.i32(4, 6).i64(5, 1).i64(6, 100 + index).i64(7, 50 + index).i64(9, 4)
        w.end().end()
    w.i64(2, 100 * len(leaves)).i64(3, 1).end()
    w.binary(6, "Synthetic writer 1.0").end()
    return bytes(w.out)


def parquet_file(meta: bytes) -> bytes:
    """A small Parquet file: the magic, 100 zero bytes, the footer, its length, and the magic."""
    return b"PAR1" + bytes(100) + meta + len(meta).to_bytes(4, "little") + b"PAR1"


# ---- the fake DFS endpoint ----

class FakeDfs:
    """Serves the container over the DFS API on 127.0.0.1 as the filesystem lake, and refuses the filesystems denied
    and expired."""

    PAGE = 3

    def __init__(self, folder: Path) -> None:
        self.folder = folder
        self.server: Optional[ThreadingHTTPServer] = None

    @property
    def port(self) -> int:
        assert self.server is not None
        return self.server.server_address[1]

    def __enter__(self) -> "FakeDfs":
        fake = self

        class Handler(BaseHTTPRequestHandler):
            # Connections stay open, as those of a storage account do, so that a client does not open one for each
            # request.
            protocol_version = "HTTP/1.1"

            def log_message(self, *args: Any) -> None:
                pass

            def do_GET(self) -> None:
                status, headers, body = fake.answer(self.path, self.headers.get("Range"))
                self.send_response(status)
                for name, value in headers.items():
                    self.send_header(name, value)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        class Server(ThreadingHTTPServer):
            # The requests of a collection come 16 at a time: a short queue of connections would refuse some of them,
            # and the retries would count as requests.
            request_queue_size = 128
            daemon_threads = True

        self.server = Server(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        return self

    def __exit__(self, *exc_info: Any) -> None:
        if self.server is not None:
            self.server.shutdown()
            self.server.server_close()

    @staticmethod
    def error(status: int, code: str) -> Tuple[int, Dict[str, str], bytes]:
        body = {"error": {"code": code, "message": "A message of the fake endpoint, which a collector does not show."}}
        return status, {"Content-Type": "application/json"}, json.dumps(body).encode()

    def answer(self, target: str, given_range: Optional[str]) -> Tuple[int, Dict[str, str], bytes]:
        split = urllib.parse.urlsplit(target)
        segments = [segment for segment in urllib.parse.unquote(split.path).split("/") if segment]
        query = dict(urllib.parse.parse_qsl(split.query, keep_blank_values=True))
        if segments[:1] == ["denied"]:
            return self.error(403, "AuthorizationPermissionMismatch")
        if segments[:1] == ["expired"]:
            return self.error(401, "InvalidAuthenticationInfo")
        if segments[:1] != ["lake"]:
            return self.error(404, "FilesystemNotFound")
        if len(segments) == 1 and query.get("resource") == "filesystem":
            return self.listing(query)
        return self.read("/".join(segments[1:]), given_range)

    def listing(self, query: Dict[str, str]) -> Tuple[int, Dict[str, str], bytes]:
        if query.get("directory") == "":
            return self.error(400, "InvalidQueryParameterValue")
        directory = query.get("directory", "").strip("/")
        full = self.folder.joinpath(*directory.split("/")) if directory else self.folder
        if not full.is_dir():
            return self.error(404, "PathNotFound")
        found = full.rglob("*") if query.get("recursive") == "true" else full.iterdir()
        entries = sorted(found, key=lambda entry: entry.relative_to(full).as_posix())
        start = int(query.get("continuation") or 0)
        paths = []
        for entry in entries[start:start + self.PAGE]:
            relative = entry.relative_to(full).as_posix()
            item: Dict[str, Any] = {"name": f"{directory}/{relative}" if directory else relative,
                                    "lastModified": email.utils.formatdate(entry.stat().st_mtime, usegmt=True)}
            if entry.is_dir():
                item.update(isDirectory="true", contentLength="0")
            else:
                item["contentLength"] = str(entry.stat().st_size)
            paths.append(item)
        headers = {"Content-Type": "application/json"}
        if start + self.PAGE < len(entries):
            headers["x-ms-continuation"] = str(start + self.PAGE)
        return 200, headers, json.dumps({"paths": paths}).encode()

    def read(self, path: str, given_range: Optional[str]) -> Tuple[int, Dict[str, str], bytes]:
        full = self.folder.joinpath(*path.split("/"))
        if not full.is_file():
            return self.error(404, "PathNotFound")
        data = full.read_bytes()
        if given_range is None:
            return 200, {}, data
        first, _, last = given_range.split("=", 1)[1].partition("-")
        start = int(first)
        if start >= len(data):
            return 416, {}, b""
        stop = min(int(last) if last else len(data) - 1, len(data) - 1)
        return 206, {"Content-Range": f"bytes {start}-{stop}/{len(data)}"}, data[start:stop + 1]


# ---- the two command lines ----

PYTHON_RUNNER = r"""# The Python side of the container check of conformance/cross_check.py: runs python -m pqax collect in
# the process, with its HTTPS connections sent to the fake DFS endpoint of the check.
#   python -c <this> <port> <cases.json> <results.json>
import contextlib
import http.client
import io
import json
import sys

PORT = int(sys.argv[1])


class Redirected(http.client.HTTPConnection):
    def __init__(self, host, port=None, timeout=60, **options):
        super().__init__("127.0.0.1", PORT, timeout=timeout)


http.client.HTTPSConnection = Redirected
from pqax.__main__ import main

results = []
with open(sys.argv[2], encoding="utf-8") as cases:
    for arguments in json.load(cases):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = main(arguments)
        results.append({"exit": code, "out": out.getvalue(), "err": err.getvalue()})
with open(sys.argv[3], "w", encoding="utf-8") as written:
    json.dump(results, written)
"""

DOTNET_RUNNER = r"""#:project __CLI__
// The C# side of the container check of conformance/cross_check.py: runs pqax collect in the process, with an HTTP
// client that sends the requests of the storage account to the fake DFS endpoint of the check.
//   <port> <cases.json> <results.json>
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pqax.Cli;

int port = int.Parse(args[0], CultureInfo.InvariantCulture);
using var client = new HttpClient(new Redirect(port));
var host = new CollectHost { HttpClient = client };
using JsonDocument cases = JsonDocument.Parse(File.ReadAllBytes(args[1]));
using (Stream stream = File.Create(args[2]))
using (var json = new Utf8JsonWriter(stream))
{
    json.WriteStartArray();
    foreach (JsonElement arguments in cases.RootElement.EnumerateArray())
    {
        string[] command = arguments.EnumerateArray().Select(argument => argument.GetString()!).ToArray();
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = CollectCommand.Run(command, output, error, host);
        json.WriteStartObject();
        json.WriteNumber("exit", exit);
        json.WriteString("out", output.ToString());
        json.WriteString("err", error.ToString());
        json.WriteEndObject();
    }
    json.WriteEndArray();
}
return 0;

// Sends every request to the fake endpoint on 127.0.0.1, with its path and its query.
sealed class Redirect(int port) : DelegatingHandler(new SocketsHttpHandler { UseProxy = false })
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        string address = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        request.RequestUri = new Uri(address + request.RequestUri!.PathAndQuery);
        return base.SendAsync(request, cancellation);
    }
}
"""


def _lines(text: str) -> List[str]:
    return [line.rstrip("\r") for line in text.splitlines() if line.strip()]


def _text(data: bytes) -> str:
    return data.decode("utf-8", errors="replace")


def _last_lines(completed: subprocess.CompletedProcess, count: int = 6) -> str:
    return " | ".join(_lines(_text(completed.stdout + completed.stderr))[-count:])


def _run_python(work: Path, port: int, cases: List[List[str]], env: Dict[str, str]) -> List[Dict[str, Any]]:
    (work / "python-cases.json").write_text(json.dumps(cases), encoding="utf-8")
    environment = {name: value for name, value in env.items()
                   if name.lower() not in ("https_proxy", "http_proxy", "all_proxy")}
    environment.update({TOKEN_VARIABLE: TOKEN, "NO_PROXY": "*"})
    completed = subprocess.run([sys.executable, "-c", PYTHON_RUNNER, str(port), str(work / "python-cases.json"),
                                str(work / "python-results.json")], cwd=work, env=environment, capture_output=True,
                               timeout=TIMEOUT)
    if completed.returncode != 0:
        raise RuntimeError(f"the Python runner failed: {_last_lines(completed)}")
    return json.loads((work / "python-results.json").read_text(encoding="utf-8"))


def _run_dotnet(work: Path, port: int, cases: List[List[str]], dotnet: str, cli: Path) -> List[Dict[str, Any]]:
    runner = work / "dotnet-runner" / "container.cs"
    if not runner.is_file():
        runner.parent.mkdir(parents=True, exist_ok=True)
        runner.write_text(DOTNET_RUNNER.replace("__CLI__", cli.as_posix()), encoding="utf-8")
        completed = subprocess.run([dotnet, "build", str(runner), "-nologo", "-v", "q"], cwd=runner.parent,
                                   capture_output=True, timeout=TIMEOUT)
        if completed.returncode != 0:
            raise RuntimeError(f"the C# runner does not build: {_last_lines(completed)}")
    (work / "dotnet-cases.json").write_text(json.dumps(cases), encoding="utf-8")
    environment = dict(os.environ)
    environment[TOKEN_VARIABLE] = TOKEN
    completed = subprocess.run([dotnet, "run", str(runner), "--", str(port), str(work / "dotnet-cases.json"),
                                str(work / "dotnet-results.json")], cwd=runner.parent, env=environment,
                               capture_output=True, timeout=TIMEOUT)
    if completed.returncode != 0:
        raise RuntimeError(f"the C# runner failed: {_last_lines(completed)}")
    return json.loads((work / "dotnet-results.json").read_text(encoding="utf-8"))


def _progress(lines: List[str]) -> List[Tuple[str, str, str]]:
    matches = [PROGRESS.match(line) for line in lines]
    return sorted(match.groups() for match in matches if match)


Compare = Callable[[Path, str, Path, str], Tuple[List[str], int]]


def check(work: Path, python_env: Dict[str, str], dotnet: str, cli: Path, compare: Compare) -> List[str]:
    """Runs every case with both command lines and returns what differs. compare(first, first label, second, second
    label) returns the differences of two footprints and the number of values compared."""
    if work.exists():
        shutil.rmtree(work)
    build(work / "container")
    outputs = {name: work / name for name in ("python", "dotnet")}
    for folder in outputs.values():
        folder.mkdir()

    def cases(side: str) -> List[List[str]]:
        made = [["collect", "--adls", url, "--output", str(outputs[side] / f"{name}.pqax"),
                 "--access-token-env", TOKEN_VARIABLE, *options] for name, url, options in COLLECTIONS]
        made += [["collect", *arguments, "--output", str(outputs[side] / "failed.pqax"), "--access-token-env",
                  TOKEN_VARIABLE] for _, arguments in FAILURES]
        return made

    with FakeDfs(work / "container") as fake:
        python = _run_python(work, fake.port, cases("python"), python_env)
        csharp = _run_dotnet(work, fake.port, cases("dotnet"), dotnet, cli)
    problems: List[str] = []
    compared = 0
    for (name, _, _), ours, theirs in zip(COLLECTIONS, python, csharp):
        label = f"container {name}"
        if ours["exit"] != 0 or theirs["exit"] != 0:
            problems.append(f"{label}: exit code {ours['exit']} in Python and {theirs['exit']} in C#: "
                            f"{' | '.join(_lines(ours['err'])[:2])} / {' | '.join(_lines(theirs['err'])[:2])}")
            continue
        first, second = _lines(ours["out"]), _lines(theirs["out"])
        if first[:2] != second[:2] or first[-1:] != second[-1:]:
            problems.append(f"{label}: Python printed {first[:2] + first[-1:]}, C# {second[:2] + second[-1:]}")
        if _progress(first) != _progress(second) or len(first) != len(second):
            problems.append(f"{label}: the lines of the tables differ: {_progress(first)} and {_progress(second)}")
        if _lines(ours["err"]) or _lines(theirs["err"]):
            problems.append(f"{label}: an error output: {ours['err']!r} / {theirs['err']!r}")
        found, count = compare(outputs["dotnet"] / f"{name}.pqax", f"dotnet/{name}.pqax",
                               outputs["python"] / f"{name}.pqax", f"python/{name}.pqax")
        problems += [f"{label}: {problem}" for problem in found]
        compared += count
        print(f"  container {name:<10} {first[1] if len(first) > 1 else ''} {'same footprint' if not found else 'FAIL'}"
              f" in Python and C#, {count} values compared")
    alike = 0
    for (name, _), ours, theirs in zip(FAILURES, python[len(COLLECTIONS):], csharp[len(COLLECTIONS):]):
        first, second = _lines(ours["err"])[:1], _lines(theirs["err"])[:1]
        if ours["exit"] != theirs["exit"] or first != second or _lines(ours["out"]) != _lines(theirs["out"]):
            problems.append(f"container {name}: exit code {ours['exit']} and {first} in Python, {theirs['exit']} and "
                            f"{second} in C#")
        else:
            alike += 1
    print(f"  container: {len(COLLECTIONS)} collections of the container by both command lines, {compared} values "
          f"compared; {alike} of {len(FAILURES)} failures and refusals alike")
    return problems
