"""Read-only access to the storage that holds the tables: a folder in the OneLake layout, the mounted lakehouse of a
notebook, or OneLake (and ADLS Gen2) over the DFS REST API.

Paths are relative to the root of the storage, use forward slashes, and have no leading slash. Every read of the
collector goes through a storage, so that a collection can count its requests and the bytes it read. A storage never
writes. A StorageError never holds a path in its message; the collector records the path apart, in the issue.
"""
from __future__ import annotations

import base64
import dataclasses
import datetime
import email.utils
import http.client
import json
import os
import re
import threading
import time
import urllib.parse
import urllib.request
from typing import Callable, Dict, Iterable, List, Optional, Sequence

__all__ = [
    "DEFAULT_ENDPOINT", "READ_ALL_CHUNK", "SCOPE", "LocalStorage", "OneLakeStorage", "StorageEntry", "StorageError",
    "list_files", "read_all",
]

#: The scope of the access token that a token provider of OneLakeStorage returns.
SCOPE = "https://storage.azure.com/.default"
#: The DFS endpoint of OneLake.
DEFAULT_ENDPOINT = "https://onelake.dfs.fabric.microsoft.com"
#: The bytes asked for in one request when the size of a file is not known: 64 MB.
READ_ALL_CHUNK = 64 * 1024 * 1024

_UTC = datetime.timezone.utc
_API_VERSION = "2018-11-09"
_GUID = re.compile(r"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}")


class StorageError(Exception):
    """A storage request that failed. ``status`` is the HTTP status, or the status that a local storage maps its error
    to: 404 for a missing file, 403 for denied access. None for a timeout or a broken connection."""

    def __init__(self, message: str, status: Optional[int] = None) -> None:
        super().__init__(message)
        self.status = status

    @property
    def is_not_found(self) -> bool:
        """Whether the file or folder does not exist."""
        return self.status == 404

    @property
    def is_access_denied(self) -> bool:
        """Whether access was denied."""
        return self.status == 401 or self.status == 403


@dataclasses.dataclass(frozen=True)
class StorageEntry:
    """A file or a folder that a listing returned."""

    #: The path relative to the root of the storage, with forward slashes.
    path: str
    is_directory: bool
    #: The size of a file in bytes. None for a folder, or when the storage does not say.
    size: Optional[int] = None
    #: When the entry was last modified, in UTC, when the storage says.
    last_modified: Optional[datetime.datetime] = None

    @property
    def name(self) -> str:
        """The last segment of the path."""
        return self.path.rsplit("/", 1)[-1]

    def __str__(self) -> str:
        return self.path + "/" if self.is_directory else self.path


def read_all(storage, path: str, size: Optional[int] = None) -> bytes:
    """Reads a whole file: in one request when its size is known, otherwise READ_ALL_CHUNK bytes at a time until a
    read returns fewer."""
    if size is not None:
        if size < 0:
            raise ValueError("The size is negative.")
        return b"" if size == 0 else storage.read_range(path, 0, size)
    first = storage.read_range(path, 0, READ_ALL_CHUNK)
    if len(first) < READ_ALL_CHUNK:
        return first
    chunks = [first]
    total = len(first)
    while True:
        following = storage.read_range(path, total, READ_ALL_CHUNK)
        if following:
            chunks.append(following)
            total += len(following)
        if len(following) < READ_ALL_CHUNK:
            break
    return b"".join(chunks)


def _excluded(root: str, path: str, excluded: Iterable[str]) -> bool:
    """Whether a file is inside one of the excluded folders directly under root."""
    if root and not (len(path) > len(root) and path[len(root)] == "/" and path.startswith(root)):
        return False
    relative = path if not root else path[len(root) + 1:]
    slash = relative.find("/")
    return slash > 0 and relative[:slash] in excluded


def _walk(storage, path: str, excluded: Sequence[str]) -> List[StorageEntry]:
    """The files under a folder, by listing each folder in turn: one request for each folder."""
    root = path.strip("/")
    files: List[StorageEntry] = []
    folders = [root]
    while folders:
        folder = folders.pop(0)
        for entry in storage.list(folder):
            if not entry.is_directory:
                files.append(entry)
            elif not (folder == root and entry.name in excluded):
                folders.append(entry.path.strip("/"))
    files.sort(key=lambda entry: _ordinal(entry.path))
    return files


def _ordinal(text: str):
    """A sort key that orders strings as C# string.CompareOrdinal does: by UTF-16 code units."""
    return text.encode("utf-16-be", "surrogatepass")


def list_files(storage, path: str, excluded: Sequence[str] = ()) -> List[StorageEntry]:
    """The files under a folder, at any depth, sorted by path, leaving out the content of the folders named in excluded
    directly under it: with one recursive listing when the storage has list_files, otherwise folder by folder."""
    recursive = getattr(storage, "list_files", None)
    if recursive is not None:
        return recursive(path, excluded)
    return _walk(storage, path, excluded)


class _Counter:
    """The requests and the bytes of a storage, safe from several threads."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._requests = 0
        self._bytes = 0

    @property
    def request_count(self) -> int:
        """The requests made, including retries and the pages of a listing."""
        with self._lock:
            return self._requests

    @property
    def bytes_read(self) -> int:
        """The bytes received."""
        with self._lock:
            return self._bytes

    def _count(self, requests: int = 1, read: int = 0) -> None:
        with self._lock:
            self._requests += requests
            self._bytes += read


def full_path(path: str) -> str:
    """The full path of a folder, as C# Path.GetFullPath gives it: absolute, and on Windows with the short 8.3 names
    of its segments expanded, such as C:\\Users\\MARCOR~1 into the long name."""
    full = os.path.abspath(os.fspath(path))
    if os.name == "nt" and "~" in full:
        try:
            import ctypes
            buffer = ctypes.create_unicode_buffer(32768)
            length = ctypes.windll.kernel32.GetLongPathNameW(full, buffer, len(buffer))
            if 0 < length < len(buffer):
                full = buffer.value
        except (AttributeError, OSError, ValueError):
            pass
    return full


def _file_time(stat: os.stat_result) -> datetime.datetime:
    return datetime.datetime(1970, 1, 1, tzinfo=_UTC) + datetime.timedelta(microseconds=stat.st_mtime_ns // 1000)


class LocalStorage(_Counter):
    """A folder on disk with the layout of a OneLake workspace: <item>.<Kind>/Tables/[<schema>/]<table>/..., as
    conformance/fixtures/lakehouse. It reads only, and it counts its requests and bytes as a remote storage would.

    The discovery records the full path of an item folder as the location of its source, and the full path of a table
    folder as the location of the table, with the separators of the platform.
    """

    def __init__(self, root: str, *, item: Optional[str] = None) -> None:
        super().__init__()
        if root is None:
            raise TypeError("The root folder is missing.")
        #: The full path of the folder: the workspace, or the item folder of a mount.
        self.root = full_path(root)
        #: The one item of a mount, whose folder is the root. None for a workspace folder.
        self.item = item.strip("/") if item is not None else None
        if self.item is not None and (not self.item or "/" in self.item or self.item in (".", "..")):
            raise ValueError("The item of a mount is one folder name, such as Sales.Lakehouse.")

    @classmethod
    def mount(cls, folder: str, item: Optional[str] = None) -> "LocalStorage":
        """A storage over the folder of one item, which holds Tables, as the mounted lakehouse /lakehouse/default of a
        Fabric notebook. The storage has that one item at its root, named item, such as Sales.Lakehouse, or the name
        of the folder with .Lakehouse added when no item is given."""
        if item is None:
            name = os.path.basename(os.path.normpath(full_path(folder)))
            item = name if "." in name.strip(".") else name + ".Lakehouse"
        return cls(folder, item=item)

    def _resolve(self, path: str) -> Optional[str]:
        """The full path of a path of the storage: "" for the root of a mount, None for another item of a mount."""
        normalized = path.strip("/")
        segments = normalized.split("/") if normalized else []
        for segment in segments:
            if (not segment or segment in (".", "..") or "\\" in segment or os.path.isabs(segment)
                    or os.path.splitdrive(segment)[0]):
                raise ValueError("A path of the storage has an empty, relative, or rooted segment.")
        if self.item is not None:
            if not segments:
                return ""  # the root of a mount holds only the item, which list() adds
            if segments[0] != self.item:
                return None
            segments = segments[1:]
        full = os.path.join(self.root, *segments) if segments else self.root
        if not full.startswith(self.root):
            raise ValueError("A path of the storage leads outside its root.")
        return full

    def list(self, path: str) -> List[StorageEntry]:
        """The files and folders directly inside a folder, sorted by name. Empty when the folder does not exist."""
        full = self._resolve(path)
        self._count()
        prefix = path.strip("/")
        if full == "":
            try:
                stat = os.stat(self.root)
            except OSError:
                return []
            return [StorageEntry(self.item, True, None, _file_time(stat))] if os.path.isdir(self.root) else []
        if full is None or not os.path.isdir(full):
            return []
        entries = []
        try:
            with os.scandir(full) as scan:
                for item in scan:
                    entry_path = f"{prefix}/{item.name}" if prefix else item.name
                    stat = item.stat()
                    if item.is_dir():
                        entries.append(StorageEntry(entry_path, True, None, _file_time(stat)))
                    else:
                        entries.append(StorageEntry(entry_path, False, stat.st_size, _file_time(stat)))
        except PermissionError as error:
            raise StorageError("Access to a local folder was denied.", 403) from error
        except OSError as error:
            raise StorageError(f"A local folder could not be listed: {type(error).__name__}.") from error
        entries.sort(key=lambda entry: _ordinal(entry.name))
        return entries

    def list_files(self, path: str, excluded: Sequence[str] = ()) -> List[StorageEntry]:
        """The files under a folder at any depth, as one request, sorted by path; the folders named in excluded
        directly under it are not walked. Empty when the folder does not exist."""
        full = self._resolve(path)
        self._count()
        prefix = path.strip("/")
        if full == "":
            full, prefix = self.root, self.item or ""
        if full is None or not os.path.isdir(full):
            return []
        files: List[StorageEntry] = []
        folders = [(full, prefix, True)]
        try:
            while folders:
                folder, relative, top = folders.pop()
                with os.scandir(folder) as scan:
                    for item in scan:
                        entry_path = f"{relative}/{item.name}" if relative else item.name
                        if item.is_dir():
                            if not (top and item.name in excluded):
                                folders.append((item.path, entry_path, False))
                        else:
                            stat = item.stat()
                            files.append(StorageEntry(entry_path, False, stat.st_size, _file_time(stat)))
        except PermissionError as error:
            raise StorageError("Access to a local folder was denied.", 403) from error
        except OSError as error:
            raise StorageError(f"A local folder could not be listed: {type(error).__name__}.") from error
        files.sort(key=lambda entry: _ordinal(entry.path))
        return files

    def read_range(self, path: str, offset: int, length: int) -> bytes:
        """The bytes of a range of a file: fewer only when the file ends before, none past its end."""
        if offset < 0:
            raise ValueError("The offset is negative.")
        if length < 0:
            raise ValueError("The length is negative.")
        full = self._resolve(path)
        self._count()
        if not full:
            raise StorageError("A local file does not exist.", 404)
        try:
            with open(full, "rb") as stream:
                stream.seek(0, os.SEEK_END)
                available = stream.tell() - offset
                if available <= 0 or length == 0:
                    return b""
                stream.seek(offset)
                data = stream.read(min(length, available))
        except (FileNotFoundError, NotADirectoryError) as error:
            raise StorageError("A local file does not exist.", 404) from error
        except PermissionError as error:  # also a folder, as C# reports it: IsADirectoryError is one
            raise StorageError("Access to a local file was denied.", 403) from error
        except OSError as error:
            raise StorageError(f"A local file could not be read: {type(error).__name__}.") from error
        self._count(0, len(data))
        return data

    def location_of(self, path: str) -> str:
        """The full path of a folder of the storage, with the separators of the platform."""
        normalized = path.strip("/")
        if self.item is not None:
            segments = normalized.split("/") if normalized else []
            if segments and segments[0] == self.item:
                segments = segments[1:]
            return os.path.join(self.root, *segments) if segments else self.root
        return self._resolve(path)

    def identifiers_of(self, item: str) -> Optional[Dict[str, str]]:
        """A folder on disk has no identifiers."""
        return None


class OneLakeStorage(_Counter):
    """A OneLake workspace through its DFS endpoint, or a container of an ADLS Gen2 account through the same API.

    The storage sends GET requests only, so it cannot change anything. Paths are relative to the workspace:
    <item>.<Kind>/Tables/... The token comes from token_provider(), called before every request; it returns an access
    token for SCOPE, and it should cache the token, as identity.TokenCache does. The token is sent in the Authorization
    header and is never stored, traced, or written anywhere. Each thread keeps one connection open to the endpoint.

    The discovery records abfss://<workspace>@<host>/<item> as the location of a source, with the host of the endpoint
    and the workspace and item as the caller gave them, and the identifiers workspaceId and itemId for those given as
    ids.
    """

    def __init__(self, workspace: str, token_provider: Callable[[], str], *, endpoint: str = DEFAULT_ENDPOINT,
                 max_retries: int = 4, timeout: float = 300.0,
                 trace: Optional[Callable[[str], None]] = None) -> None:
        super().__init__()
        if not workspace:
            raise ValueError("The workspace is missing.")
        if token_provider is None:
            raise TypeError("The token provider is missing.")
        if max_retries < 0:
            raise ValueError("max_retries is negative.")
        parsed = urllib.parse.urlsplit(endpoint)
        if parsed.scheme not in ("http", "https") or not parsed.hostname:
            raise ValueError("The endpoint is an http or https URL, such as https://onelake.dfs.fabric.microsoft.com.")
        #: The name or the id of the workspace, or the container of an ADLS Gen2 account.
        self.workspace = workspace
        self.endpoint = endpoint.rstrip("/")
        self.max_retries = max_retries
        self.timeout = timeout
        self._token_provider = token_provider
        self._trace = trace
        self._scheme = parsed.scheme
        self._netloc = parsed.netloc
        self._authority = parsed.netloc.rsplit("@", 1)[-1]
        self._base_path = parsed.path.rstrip("/") + "/" + urllib.parse.quote(workspace, safe="")
        self._local = threading.local()

    # ---- listing ----

    def list(self, path: str) -> List[StorageEntry]:
        """The files and folders directly inside a folder, sorted by name. Empty when the folder does not exist."""
        entries = self._list(path.strip("/"), False)
        if not entries:
            return []
        entries.sort(key=lambda entry: _ordinal(entry.name))
        return entries

    def list_files(self, path: str, excluded: Sequence[str] = ()) -> List[StorageEntry]:
        """The files under a folder at any depth, with one listing with recursive=true in as many pages as it takes.
        The endpoint cannot leave a folder out, so the files of the excluded folders are dropped here."""
        directory = path.strip("/")
        entries = self._list(directory, True)
        files = [entry for entry in entries if not entry.is_directory and not _excluded(directory, entry.path, excluded)]
        files.sort(key=lambda entry: _ordinal(entry.path))
        return files

    def _list(self, directory: str, recursive: bool) -> List[StorageEntry]:
        entries: List[StorageEntry] = []
        continuation: Optional[str] = None
        while True:
            # The root of the workspace is listed without the directory parameter: OneLake answers 400 to an empty one.
            target = f"{self._base_path}?resource=filesystem&recursive={'true' if recursive else 'false'}"
            if directory:
                target += "&directory=" + urllib.parse.quote(directory, safe="")
            if continuation is not None:
                target += "&continuation=" + urllib.parse.quote(continuation, safe="")
            status, reason, headers, body = self._send(target)
            if status == 404:
                return entries
            self._ensure_success(status, reason, body)
            self._count(0, len(body))
            continuation = headers.get("x-ms-continuation") or None
            self._parse_listing(body, entries)
            if continuation is None:
                return entries

    @staticmethod
    def _parse_listing(body: bytes, entries: List[StorageEntry]) -> None:
        try:
            document = json.loads(body)
        except ValueError:
            return
        paths = document.get("paths") if isinstance(document, dict) else None
        if not isinstance(paths, list):
            return
        for item in paths:
            if not isinstance(item, dict) or not isinstance(item.get("name"), str):
                continue
            flag = item.get("isDirectory")
            is_directory = flag is True or (isinstance(flag, str) and flag.lower() == "true")
            size: Optional[int] = None
            length = item.get("contentLength")
            if not is_directory:
                if type(length) is int:
                    size = length
                elif isinstance(length, str) and length.isascii() and length.isdigit():
                    size = int(length)
            modified: Optional[datetime.datetime] = None
            text = item.get("lastModified")
            if isinstance(text, str):
                try:
                    moment = email.utils.parsedate_to_datetime(text)
                    modified = moment.replace(tzinfo=_UTC) if moment.tzinfo is None else moment.astimezone(_UTC)
                except (TypeError, ValueError, IndexError):
                    modified = None
            entries.append(StorageEntry(item["name"].strip("/"), is_directory, size, modified))

    # ---- reading ----

    def read_range(self, path: str, offset: int, length: int) -> bytes:
        """The bytes of a range of a file: fewer only when the file ends before, none past its end."""
        if offset < 0:
            raise ValueError("The offset is negative.")
        if length < 0:
            raise ValueError("The length is negative.")
        if length == 0:
            return b""
        target = self._base_path + "/" + "/".join(urllib.parse.quote(segment, safe="")
                                                    for segment in path.strip("/").split("/"))
        status, reason, _, body = self._send(target, (offset, offset + length - 1))
        if status == 416:
            return b""
        self._ensure_success(status, reason, body)
        self._count(0, len(body))
        if status == 200:
            # The whole file came back: keep the range that was asked for.
            return body[offset:offset + length] if offset < len(body) else b""
        return body[:length]

    # ---- locations ----

    def location_of(self, path: str) -> str:
        """The ABFSS URI of a folder of the workspace, with the workspace as the caller gave it, name or id."""
        folder = path.strip("/")
        return f"abfss://{self.workspace}@{self._authority}" + (f"/{folder}" if folder else "")

    def identifiers_of(self, item: str) -> Optional[Dict[str, str]]:
        """workspaceId when the workspace was given as an id, and itemId when the item was. A name gives none: the
        listing of the DFS endpoint has no id, and the collector does not guess one."""
        identifiers: Dict[str, str] = {}
        folder = item.strip("/")
        if _GUID.fullmatch(self.workspace):
            identifiers["workspaceId"] = self.workspace
        if _GUID.fullmatch(folder):
            identifiers["itemId"] = folder
        return dict(sorted(identifiers.items())) or None

    # ---- requests ----

    def close(self) -> None:
        """Closes the connection of the calling thread. The others close when their threads end."""
        connection = getattr(self._local, "connection", None)
        if connection is not None:
            connection.close()
            self._local.connection = None

    def __enter__(self) -> "OneLakeStorage":
        return self

    def __exit__(self, *exc_info) -> None:
        self.close()

    def _connection(self) -> http.client.HTTPConnection:
        connection = getattr(self._local, "connection", None)
        if connection is None:
            kind = http.client.HTTPSConnection if self._scheme == "https" else http.client.HTTPConnection
            proxy = self._proxy()
            if proxy is None:
                connection = kind(self._netloc, timeout=self.timeout)
            else:
                # Through the proxy of HTTPS_PROXY or HTTP_PROXY, with a tunnel to the endpoint.
                connection = kind(proxy.hostname, proxy.port or (443 if proxy.scheme == "https" else 80),
                                  timeout=self.timeout)
                headers = {}
                if proxy.username:
                    credentials = f"{urllib.parse.unquote(proxy.username)}:{urllib.parse.unquote(proxy.password or '')}"
                    headers["Proxy-Authorization"] = "Basic " + base64.b64encode(credentials.encode()).decode()
                connection.set_tunnel(self._authority, headers=headers)
            self._local.connection = connection
        return connection

    def _proxy(self):
        """The proxy of the environment for the endpoint, as urllib finds it, or None."""
        proxies = urllib.request.getproxies()
        address = proxies.get(self._scheme)
        host = urllib.parse.urlsplit(f"//{self._authority}").hostname or ""
        if not address or urllib.request.proxy_bypass(host):
            return None
        proxy = urllib.parse.urlsplit(address if "://" in address else "http://" + address)
        return proxy if proxy.hostname else None

    def _drop_connection(self) -> None:
        connection = getattr(self._local, "connection", None)
        if connection is not None:
            try:
                connection.close()
            finally:
                self._local.connection = None

    def _send(self, target: str, byte_range: Optional[tuple] = None):
        """One GET with its retries: the status, the reason, the headers in lower case, and the body."""
        attempt = 0
        while True:
            headers = {"Authorization": "Bearer " + self._token_provider(), "x-ms-version": _API_VERSION}
            if byte_range is not None:
                headers["Range"] = f"bytes={byte_range[0]}-{byte_range[1]}"
            try:
                connection = self._connection()
                connection.request("GET", target, headers=headers)
                response = connection.getresponse()
                body = response.read()
                status, reason = response.status, response.reason
                answer = {name.lower(): value for name, value in response.getheaders()}
                if answer.get("connection", "").lower() == "close":
                    self._drop_connection()
            except TimeoutError as error:
                self._drop_connection()
                self._count()
                if attempt < self.max_retries:
                    time.sleep(self._backoff(attempt, None))
                    attempt += 1
                    continue
                raise StorageError("A request to OneLake timed out.") from error
            except (OSError, http.client.HTTPException) as error:
                self._drop_connection()
                self._count()
                if attempt < self.max_retries:
                    time.sleep(self._backoff(attempt, None))
                    attempt += 1
                    continue
                raise StorageError(f"A request to OneLake failed: {type(error).__name__}.") from error
            self._count()
            if self._trace is not None:
                self._trace(f"GET {self._scheme}://{self._netloc}{target} -> {status}")
            if status in (429, 500, 502, 503, 504) and attempt < self.max_retries:
                time.sleep(self._backoff(attempt, answer.get("retry-after")))
                attempt += 1
                continue
            return status, reason, answer, body

    @staticmethod
    def _backoff(attempt: int, retry_after: Optional[str]) -> float:
        if retry_after:
            text = retry_after.strip()
            if text.isascii() and text.isdigit():
                seconds = int(text)
                if seconds > 0:
                    return min(seconds, 60.0)
            else:
                try:
                    moment = email.utils.parsedate_to_datetime(text)
                    wait = (moment - datetime.datetime.now(_UTC)).total_seconds()
                    if wait > 0:
                        return min(wait, 60.0)
                except (TypeError, ValueError, IndexError):
                    pass
        return float(min(30, 2 ** attempt))

    @staticmethod
    def _ensure_success(status: int, reason: str, body: bytes) -> None:
        if 200 <= status < 300:
            return
        code = ""
        try:
            document = json.loads(body)
            error = document.get("error") if isinstance(document, dict) else None
            if isinstance(error, dict) and isinstance(error.get("code"), str):
                code = f" ({error['code']})"  # never the message, which can name a path
        except ValueError:
            pass
        raise StorageError(f"OneLake answered {status} {reason}{code}.", status)
