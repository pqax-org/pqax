"""The footer parsers of the two collectors on the same bytes: the Parquet files of other writers, the files of
mutate_footers.py, and the samples of samples/footers.

Usage, from the root of the repository:

    python conformance/footer_check.py FOLDER [--data-values] [--no-collect] [--keep DIR]

FOLDER holds Parquet files, as make_footers.py writes them. Every file is read with the FooterReader of each collector
over the same bytes, and the two must give the same outcome: the same footer in every member of the dump below, or the
same code and message of the footer exception. Nothing else may escape, and no read may hang. When pyarrow imports,
every file that it reads is also compared with ParquetFile.metadata: the rows, the row groups, the schema of every leaf
with its levels, and every column chunk. Then both collectors collect FOLDER as a storage folder at level 2 and the
Column chunks depth, and the two footprints must be equal in every table and field, as cross_check.py compares the
collections of the fixture.

The C# side is a runner that the .NET 10 SDK runs as a file-based app, on the projects of src/dotnet. The Python side
runs the sources of src/python in a process of its own. Each reads a pack: one record for each file, with the bytes
at its end and the size that the reader is given, so that a mutated file needs no file on disk. A process that dies or
stops answering is restarted after the record that it was reading, and the record counts as a crash or a hang.

cross_check.py runs check_kit() on samples/footers when both collectors run. Standard library only, except pyarrow
for the comparison with its metadata, which is skipped without it.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any, Callable, Dict, Iterable, List, Optional, Tuple

KIT = Path(__file__).resolve().parent
ROOT = KIT.parent
FOOTER_SAMPLES = KIT / "samples" / "footers"
PYTHON_SOURCES = ROOT / "src" / "python"
COLLECTOR_PROJECT = ROOT / "src" / "dotnet" / "Pqax.Collector" / "Pqax.Collector.csproj"
CHECKPOINT_PROJECT = ROOT / "src" / "dotnet" / "Pqax.Collector.ParquetNet" / "Pqax.Collector.ParquetNet.csproj"
PACK_MAGIC = b"PQFP"
# The end of a file that a pack keeps: enough for the first read of the reader and the footer.
KEPT_TAIL = 65536
# A record that takes longer than this, in seconds, counts as a hang, and its process is restarted.
HANG_SECONDS = 60.0


# ---- the pack: what both runners read ----

class Record:
    """A file as the reader sees it: actual_size bytes, of which the last len(data) are data and the others zeros, read
    with stated_size as the size that the table format gives."""

    __slots__ = ("name", "stated_size", "actual_size", "data_values", "data")

    def __init__(self, name: str, stated_size: int, actual_size: int, data: bytes, data_values: bool = False) -> None:
        self.name = name
        self.stated_size = stated_size
        self.actual_size = actual_size
        self.data = data
        self.data_values = data_values


def record_of_file(path: Path, name: str, data_values: bool = False, stated_size: Optional[int] = None) -> Record:
    """The record of a file on disk: its end, with the footer whole when the last 8 bytes give a length that fits."""
    size = path.stat().st_size
    with open(path, "rb") as handle:
        keep = min(size, KEPT_TAIL)
        if size >= 8:
            handle.seek(size - 8)
            length = struct.unpack("<i", handle.read(4))[0]
            if 0 < length < size:
                keep = min(size, max(keep, length + 8 + 16))
        handle.seek(size - keep)
        data = handle.read(keep)
    return Record(name, size if stated_size is None else stated_size, size, data, data_values)


def write_pack(records: Iterable[Record], path: Path) -> int:
    count = 0
    with open(path, "wb") as handle:
        handle.write(PACK_MAGIC)
        for record in records:
            name = record.name.encode("utf-8")
            handle.write(struct.pack("<I", len(name)) + name)
            handle.write(struct.pack("<qqBI", record.stated_size, record.actual_size, 1 if record.data_values else 0,
                                     len(record.data)))
            handle.write(record.data)
            count += 1
    return count


def read_pack(path: Path) -> Iterable[Record]:
    with open(path, "rb") as handle:
        if handle.read(4) != PACK_MAGIC:
            raise ValueError(f"{path} is not a pack of footer_check.py")
        while True:
            head = handle.read(4)
            if not head:
                return
            name = handle.read(struct.unpack("<I", head)[0]).decode("utf-8")
            stated, actual, flags, length = struct.unpack("<qqBI", handle.read(21))
            yield Record(name, stated, actual, handle.read(length), bool(flags & 1))


class VirtualStorage:
    """Serves the one file of a record, whatever its path; the bytes past actual_size do not exist."""

    def __init__(self, record: Record) -> None:
        self.record = record

    def read_range(self, path: str, offset: int, length: int) -> bytes:
        return virtual_slice(self.record, offset, length)

    def list(self, path: str):  # pragma: no cover - the footer reader does not list
        raise NotImplementedError


def virtual_slice(record: Record, offset: int, length: int) -> bytes:
    end = min(offset + length, record.actual_size)
    if offset < 0 or length < 0 or end <= offset:
        return b""
    start_of_data = record.actual_size - len(record.data)
    zeros = max(0, min(end, start_of_data) - offset)
    first = max(offset, start_of_data) - start_of_data
    return b"\x00" * zeros + record.data[first:end - start_of_data] if end > start_of_data else b"\x00" * zeros


# ---- the dump of a footer, the same in both runners ----

def dump(footer: Any) -> Dict[str, Any]:
    """Every value of a parsed footer of the Python collector, as the C# runner writes them."""
    def chunk(c: Any) -> Dict[str, Any]:
        return {
            "hasMetadata": c.has_metadata, "isEncrypted": c.is_encrypted, "filePath": c.file_path,
            "physicalType": c.physical_type, "codec": c.codec, "encodings": list(c.encodings),
            "numValues": c.num_values, "totalUncompressedSize": c.total_uncompressed_size,
            "totalCompressedSize": c.total_compressed_size, "dataPageOffset": c.data_page_offset,
            "indexPageOffset": c.index_page_offset, "dictionaryPageOffset": c.dictionary_page_offset,
            "hasStatistics": c.has_statistics, "nullCount": c.null_count, "distinctCount": c.distinct_count,
            "hasMinMax": c.has_min_max, "minValue": c.min_value, "maxValue": c.max_value,
            "encodingStats": None if c.encoding_stats is None else [[s.page_type, s.encoding, s.count]
                                                                   for s in c.encoding_stats],
            "bloomFilterOffset": c.bloom_filter_offset, "bloomFilterLength": c.bloom_filter_length,
            "unencodedByteArrayDataBytes": c.unencoded_byte_array_data_bytes,
            "offsetIndexOffset": c.offset_index_offset, "offsetIndexLength": c.offset_index_length,
            "columnIndexOffset": c.column_index_offset, "columnIndexLength": c.column_index_length,
            "hasDictionaryPage": c.has_dictionary_page, "dictionaryPageSize": c.dictionary_page_size,
        }

    return {
        "version": footer.version, "numRows": footer.num_rows, "createdBy": footer.created_by,
        "hasEncryptionAlgorithm": footer.has_encryption_algorithm, "hasEncryptedColumns": footer.has_encrypted_columns,
        "footerLength": footer.footer_length, "requestCount": footer.request_count,
        "keyValueMetadata": [[kv.key, kv.value_length, kv.value] for kv in footer.key_value_metadata],
        "schema": [{"name": e.name, "type": e.type, "typeLength": e.type_length, "repetition": e.repetition,
                    "numChildren": e.num_children, "convertedType": e.converted_type, "scale": e.scale,
                    "precision": e.precision, "fieldId": e.field_id, "logicalType": e.logical_type}
                   for e in footer.schema],
        "leaves": [{"ordinal": leaf.ordinal, "path": list(leaf.path), "physicalType": leaf.physical_type,
                    "typeLength": leaf.type_length, "repetition": leaf.repetition, "logicalType": leaf.logical_type,
                    "convertedType": leaf.converted_type, "scale": leaf.scale, "precision": leaf.precision,
                    "fieldId": leaf.field_id, "topLevelFieldId": leaf.top_level_field_id,
                    "typeAnnotation": leaf.type_annotation} for leaf in footer.leaves],
        "rowGroups": [{"numRows": g.num_rows, "totalByteSize": g.total_byte_size,
                       "totalCompressedSize": g.total_compressed_size, "fileOffset": g.file_offset,
                       "ordinal": g.ordinal,
                       "sortingColumns": None if g.sorting_columns is None else [
                           [s.column_index, s.descending, s.nulls_first] for s in g.sorting_columns],
                       "columns": [chunk(c) for c in g.columns]} for g in footer.row_groups],
    }


def python_worker(pack: Path, output: Path, skip: int) -> int:
    """The Python runner: reads the records of a pack after the first skip, one line of JSON for each."""
    sys.path.insert(0, str(PYTHON_SOURCES))
    from pqax.collect.footer import FooterError, FooterReader

    with open(output, "a", encoding="utf-8", newline="\n") as out:
        for index, record in enumerate(read_pack(pack)):
            if index < skip:
                continue
            started = time.perf_counter()
            try:
                footer = FooterReader(VirtualStorage(record), include_data_values=record.data_values).read(
                    "file.parquet", record.stated_size)
                result: Dict[str, Any] = dump(footer)
            except FooterError as error:
                result = {"error": {"code": error.code, "message": str(error)}}
            except BaseException as error:  # noqa: BLE001 - anything else is what the check looks for
                result = {"crash": f"{type(error).__name__}: {error}"}
            elapsed = (time.perf_counter() - started) * 1000.0
            out.write(json.dumps({"index": index, "name": record.name, "ms": round(elapsed, 3), "result": result},
                                 ensure_ascii=False) + "\n")
            out.flush()
    return 0


RUNNER = r"""#:project __COLLECTOR__
#:project __CHECKPOINT__
#:property Nullable=enable
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Pqax.Collector;
using Pqax.Collector.ParquetNet;

// The C# runner of footer_check.py: "read <pack> <output> <skip>" writes one line of JSON for each record of the pack,
// and "collect <folder> <output.pqax> <level> <depth> <data values 0|1>" collects a storage folder.
if (args[0] == "collect")
{
    var storage = new LocalStorage(args[1]);
    IReadOnlyList<TableLocation> tables = await TableDiscovery.DiscoverFolderAsync(storage, string.Empty, CancellationToken.None);
    var options = new CollectorOptions
    {
        Level = int.Parse(args[3]),
        Depth = Enum.Parse<DetailDepth>(args[4]),
        IncludeDataValues = args[5] == "1",
    };
    var collector = new PqaxCollector(storage, options, new ParquetNetCheckpointReader());
    var builder = await collector.CollectAsync(tables, null, CancellationToken.None);
    builder.Write(args[2]);
    Console.WriteLine(tables.Count);
    return 0;
}

int skip = int.Parse(args[3]);
using var input = File.OpenRead(args[1]);
using var reader = new BinaryReader(input);
if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "PQFP")
{
    throw new InvalidDataException("not a pack");
}

using var output = new FileStream(args[2], FileMode.Append, FileAccess.Write);
var jsonOptions = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
for (int index = 0; input.Position < input.Length; index++)
{
    string name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
    long stated = reader.ReadInt64();
    long actual = reader.ReadInt64();
    bool dataValues = (reader.ReadByte() & 1) != 0;
    byte[] data = reader.ReadBytes(reader.ReadInt32());
    if (index < skip)
    {
        continue;
    }

    // Straight to the file: the dump of a footer of millions of column chunks passes the 2 GB of a MemoryStream.
    using (var w = new Utf8JsonWriter(output, jsonOptions))
    {
        w.WriteStartObject();
        w.WriteNumber("index", index);
        w.WriteString("name", name);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        ParquetFooter? footer = null;
        string? code = null, message = null, crash = null;
        try
        {
            footer = await new FooterReader(new RecordStorage(actual, data), FooterReader.DefaultTailBytes, dataValues)
                .ReadAsync("file.parquet", stated, CancellationToken.None);
        }
        catch (ParquetFooterException e)
        {
            code = e.Code;
            message = e.Message;
        }
        catch (Exception e)
        {
            crash = e.GetType().Name + ": " + e.Message;
        }

        watch.Stop();
        w.WriteNumber("ms", Math.Round(watch.Elapsed.TotalMilliseconds, 3));
        w.WriteNumber("allocated", GC.GetAllocatedBytesForCurrentThread() - allocated);
        w.WritePropertyName("result");
        if (footer != null)
        {
            Dump.Footer(w, footer);
        }
        else if (code != null)
        {
            w.WriteStartObject();
            w.WriteStartObject("error");
            w.WriteString("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        else
        {
            w.WriteStartObject();
            w.WriteString("crash", crash);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    output.WriteByte((byte)'\n');
    output.Flush();
}

return 0;

/// <summary>The one file of a record: actual bytes, the last of which are the data, the others zeros.</summary>
sealed class RecordStorage(long actual, byte[] data) : IStorage
{
    public Task<IReadOnlyList<StorageEntry>> ListAsync(string path, CancellationToken cancellation) => throw new NotSupportedException();

    public Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellation)
    {
        long end = Math.Min(offset + length, actual);
        if (offset < 0 || length < 0 || end <= offset)
        {
            return Task.FromResult(Array.Empty<byte>());
        }

        var result = new byte[end - offset];
        long startOfData = actual - data.Length;
        for (long p = Math.Max(offset, startOfData); p < end; p++)
        {
            result[p - offset] = data[p - startOfData];
        }

        return Task.FromResult(result);
    }
}

static class Dump
{
    public static void Footer(Utf8JsonWriter w, ParquetFooter f)
    {
        w.WriteStartObject();
        w.WriteNumber("version", f.Version);
        w.WriteNumber("numRows", f.NumRows);
        Text(w, "createdBy", f.CreatedBy);
        w.WriteBoolean("hasEncryptionAlgorithm", f.HasEncryptionAlgorithm);
        w.WriteBoolean("hasEncryptedColumns", f.HasEncryptedColumns);
        w.WriteNumber("footerLength", f.FooterLength);
        w.WriteNumber("requestCount", f.RequestCount);
        w.WriteStartArray("keyValueMetadata");
        foreach (ParquetKeyValue kv in f.KeyValueMetadata)
        {
            w.WriteStartArray();
            w.WriteStringValue(kv.Key);
            Number(w, kv.ValueLength);
            Text(w, kv.Value);
            w.WriteEndArray();
        }

        w.WriteEndArray();
        w.WriteStartArray("schema");
        foreach (ParquetSchemaElement e in f.Schema)
        {
            w.WriteStartObject();
            w.WriteString("name", e.Name);
            Text(w, "type", e.Type);
            Number(w, "typeLength", e.TypeLength);
            Text(w, "repetition", e.Repetition);
            Number(w, "numChildren", e.NumChildren);
            Text(w, "convertedType", e.ConvertedType);
            Number(w, "scale", e.Scale);
            Number(w, "precision", e.Precision);
            Number(w, "fieldId", e.FieldId);
            Text(w, "logicalType", e.LogicalType);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("leaves");
        foreach (ParquetLeafColumn leaf in f.Leaves)
        {
            w.WriteStartObject();
            w.WriteNumber("ordinal", leaf.Ordinal);
            w.WriteStartArray("path");
            foreach (string name in leaf.Path)
            {
                w.WriteStringValue(name);
            }

            w.WriteEndArray();
            w.WriteString("physicalType", leaf.PhysicalType);
            Number(w, "typeLength", leaf.TypeLength);
            Text(w, "repetition", leaf.Repetition);
            Text(w, "logicalType", leaf.LogicalType);
            Text(w, "convertedType", leaf.ConvertedType);
            Number(w, "scale", leaf.Scale);
            Number(w, "precision", leaf.Precision);
            Number(w, "fieldId", leaf.FieldId);
            Number(w, "topLevelFieldId", leaf.TopLevelFieldId);
            Text(w, "typeAnnotation", leaf.TypeAnnotation);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("rowGroups");
        foreach (ParquetRowGroup g in f.RowGroups)
        {
            w.WriteStartObject();
            w.WriteNumber("numRows", g.NumRows);
            w.WriteNumber("totalByteSize", g.TotalByteSize);
            Number(w, "totalCompressedSize", g.TotalCompressedSize);
            Number(w, "fileOffset", g.FileOffset);
            Number(w, "ordinal", g.Ordinal);
            if (g.SortingColumns == null)
            {
                w.WriteNull("sortingColumns");
            }
            else
            {
                w.WriteStartArray("sortingColumns");
                foreach (ParquetSortingColumn s in g.SortingColumns)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(s.ColumnIndex);
                    w.WriteBooleanValue(s.Descending);
                    w.WriteBooleanValue(s.NullsFirst);
                    w.WriteEndArray();
                }

                w.WriteEndArray();
            }

            w.WriteStartArray("columns");
            foreach (ParquetColumnChunk c in g.Columns)
            {
                Chunk(w, c);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void Chunk(Utf8JsonWriter w, ParquetColumnChunk c)
    {
        w.WriteStartObject();
        w.WriteBoolean("hasMetadata", c.HasMetadata);
        w.WriteBoolean("isEncrypted", c.IsEncrypted);
        Text(w, "filePath", c.FilePath);
        Text(w, "physicalType", c.PhysicalType);
        Text(w, "codec", c.Codec);
        w.WriteStartArray("encodings");
        foreach (string encoding in c.Encodings)
        {
            w.WriteStringValue(encoding);
        }

        w.WriteEndArray();
        w.WriteNumber("numValues", c.NumValues);
        w.WriteNumber("totalUncompressedSize", c.TotalUncompressedSize);
        w.WriteNumber("totalCompressedSize", c.TotalCompressedSize);
        w.WriteNumber("dataPageOffset", c.DataPageOffset);
        Number(w, "indexPageOffset", c.IndexPageOffset);
        Number(w, "dictionaryPageOffset", c.DictionaryPageOffset);
        w.WriteBoolean("hasStatistics", c.HasStatistics);
        Number(w, "nullCount", c.NullCount);
        Number(w, "distinctCount", c.DistinctCount);
        w.WriteBoolean("hasMinMax", c.HasMinMax);
        Text(w, "minValue", c.MinValue);
        Text(w, "maxValue", c.MaxValue);
        if (c.EncodingStats == null)
        {
            w.WriteNull("encodingStats");
        }
        else
        {
            w.WriteStartArray("encodingStats");
            foreach (ParquetPageEncodingStats s in c.EncodingStats)
            {
                w.WriteStartArray();
                w.WriteStringValue(s.PageType);
                w.WriteStringValue(s.Encoding);
                w.WriteNumberValue(s.Count);
                w.WriteEndArray();
            }

            w.WriteEndArray();
        }

        Number(w, "bloomFilterOffset", c.BloomFilterOffset);
        Number(w, "bloomFilterLength", c.BloomFilterLength);
        Number(w, "unencodedByteArrayDataBytes", c.UnencodedByteArrayDataBytes);
        Number(w, "offsetIndexOffset", c.OffsetIndexOffset);
        Number(w, "offsetIndexLength", c.OffsetIndexLength);
        Number(w, "columnIndexOffset", c.ColumnIndexOffset);
        Number(w, "columnIndexLength", c.ColumnIndexLength);
        w.WriteBoolean("hasDictionaryPage", c.HasDictionaryPage);
        Number(w, "dictionaryPageSize", c.DictionaryPageSize);
        w.WriteEndObject();
    }

    static void Text(Utf8JsonWriter w, string name, string? value)
    {
        if (value == null) w.WriteNull(name); else w.WriteString(name, value);
    }

    static void Text(Utf8JsonWriter w, string? value)
    {
        if (value == null) w.WriteNullValue(); else w.WriteStringValue(value);
    }

    static void Number(Utf8JsonWriter w, string name, long? value)
    {
        if (value == null) w.WriteNull(name); else w.WriteNumber(name, value.Value);
    }

    static void Number(Utf8JsonWriter w, long? value)
    {
        if (value == null) w.WriteNullValue(); else w.WriteNumberValue(value.Value);
    }
}
"""


class Runners:
    """The two runners over packs, and the two collectors over folders."""

    def __init__(self, work: Path, python: Optional[str] = None, dotnet: Optional[str] = None,
                 python_env: Optional[Dict[str, str]] = None) -> None:
        self.work = work
        self.python = python or sys.executable
        self.python_env = python_env
        self.dotnet = dotnet or shutil.which("dotnet")
        self.runner: Optional[Path] = None

    def prepare(self) -> None:
        """Builds the C# runner. Raises RuntimeError when it does not build."""
        if not self.dotnet:
            raise RuntimeError("dotnet is not on the PATH")
        folder = self.work / "footer-runner"
        folder.mkdir(parents=True, exist_ok=True)
        self.runner = folder / "footers.cs"
        self.runner.write_text(RUNNER.replace("__COLLECTOR__", COLLECTOR_PROJECT.as_posix())
                               .replace("__CHECKPOINT__", CHECKPOINT_PROJECT.as_posix()), encoding="utf-8")
        completed = subprocess.run([self.dotnet, "build", str(self.runner), "-nologo", "-v", "q"], cwd=folder,
                                   capture_output=True, timeout=900)
        if completed.returncode != 0:
            raise RuntimeError("the C# footer runner does not build: "
                               + completed.stdout.decode("utf-8", "replace")[-2000:])

    def _command(self, language: str, pack: Path, output: Path, skip: int) -> List[str]:
        if language == "python":
            return [self.python, "-I", str(Path(__file__).resolve()), "--worker", str(pack), str(output), str(skip)]
        if language == "pyarrow":
            return [sys.executable, str(Path(__file__).resolve()), "--pyarrow-worker", str(pack), str(output),
                    str(skip)]
        assert self.runner is not None
        return [self.dotnet, "run", "--no-build", str(self.runner), "--", "read", str(pack), str(output), str(skip)]

    def read(self, language: str, pack: Path, count: int) -> Dict[int, Dict[str, Any]]:
        """The results of every record of a pack, by index. A process that dies or hangs gives the record it was on a
        crash or a hang result, and starts again after it."""
        output = self.work / f"{pack.stem}.{language}.jsonl"
        if output.exists():
            output.unlink()
        results: Dict[int, Dict[str, Any]] = {}
        skip = 0
        while skip < count:
            process = subprocess.Popen(self._command(language, pack, output, skip), cwd=str(self.work),
                                       stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                                       env=self.python_env if language == "python" else None)
            last_size, last_change = -1, time.monotonic()
            killed = False
            while process.poll() is None:
                time.sleep(0.2)
                size = output.stat().st_size if output.exists() else 0
                if size != last_size:
                    last_size, last_change = size, time.monotonic()
                elif time.monotonic() - last_change > HANG_SECONDS + (60 if skip == 0 else 0):
                    process.kill()
                    process.wait()
                    killed = True
                    break
            stderr = process.stderr.read().decode("utf-8", "replace") if process.stderr else ""
            process.stderr.close() if process.stderr else None
            done = self._load(output, results)
            if done >= count:
                break
            # The process stopped before the end: the record after the last one written crashed or hung.
            failed = done
            reason = (f"no answer for {HANG_SECONDS:.0f} s" if killed else f"exit code {process.returncode}"
                      + (f": {stderr.strip().splitlines()[-1][:300]}" if stderr.strip() else ""))
            results[failed] = {"index": failed, "name": None, "ms": None,
                               "result": {"hang" if killed else "crash": f"the {language} runner stopped: {reason}"}}
            with open(output, "a", encoding="utf-8", newline="\n") as handle:
                handle.write(json.dumps(results[failed]) + "\n")
            skip = failed + 1
        return results

    @staticmethod
    def _load(output: Path, results: Dict[int, Dict[str, Any]]) -> int:
        if not output.exists():
            return len(results)
        with open(output, encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if line:
                    try:
                        item = json.loads(line)
                    except json.JSONDecodeError:  # a line cut by a crash
                        continue
                    results[item["index"]] = item
        index = 0
        while index in results:
            index += 1
        return index

    def collect_python(self, folder: Path, output: Path, data_values: bool) -> None:
        script = ("import sys; sys.path.insert(0, sys.argv[1]); from pqax.collect import LocalStorage, collect, "
                  "discover_folder; storage = LocalStorage(sys.argv[2]); "
                  "builder = collect(storage, discover_folder(storage, ''), include_data_values=sys.argv[4] == '1'); "
                  "builder.write(sys.argv[3])")
        completed = subprocess.run([self.python, "-I", "-c", script, str(PYTHON_SOURCES), str(folder), str(output),
                                    "1" if data_values else "0"], capture_output=True, timeout=1800,
                                   env=self.python_env)
        if completed.returncode != 0:
            raise RuntimeError("the Python collection failed: " + completed.stderr.decode("utf-8", "replace")[-2000:])

    def collect_dotnet(self, folder: Path, output: Path, data_values: bool) -> None:
        assert self.runner is not None
        completed = subprocess.run([self.dotnet, "run", "--no-build", str(self.runner), "--", "collect", str(folder),
                                    str(output), "2", "ColumnChunks", "1" if data_values else "0"],
                                   capture_output=True, timeout=1800, cwd=str(self.work))
        if completed.returncode != 0:
            raise RuntimeError("the C# collection failed: " + (completed.stdout + completed.stderr)
                               .decode("utf-8", "replace")[-2000:])


# ---- the comparisons ----

def differences(a: Any, b: Any, path: str = "") -> List[str]:
    """Where two values of JSON differ, as paths."""
    if isinstance(a, dict) and isinstance(b, dict):
        found: List[str] = []
        for key in list(a) + [key for key in b if key not in a]:
            if key not in a or key not in b:
                found.append(f"{path}.{key}: only in {'C#' if key not in a else 'Python'}")
            else:
                found += differences(a[key], b[key], f"{path}.{key}")
        return found
    if isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            return [f"{path}: {len(a)} elements in Python, {len(b)} in C#"]
        found = []
        for index, (x, y) in enumerate(zip(a, b)):
            found += differences(x, y, f"{path}[{index}]")
        return found
    if a != b or type(a) is not type(b) and not (isinstance(a, (int, float)) and isinstance(b, (int, float))):
        return [f"{path}: Python {a!r}, C# {b!r}"]
    return []


def outcome(result: Dict[str, Any]) -> str:
    if "error" in result:
        return result["error"]["code"]
    if "crash" in result:
        return "crash"
    if "hang" in result:
        return "hang"
    return "footer"


_LOGICAL_UNITS = {"milliseconds": "MILLIS", "microseconds": "MICROS", "nanoseconds": "NANOS"}


def pyarrow_annotation(logical: Any) -> Optional[str]:
    """The text of the logical type of a pyarrow ColumnSchema in the form of the parser, or None when the file has
    none of its own (pyarrow derives one from the converted type)."""
    try:
        info = json.loads(logical.to_json())
    except Exception:  # noqa: BLE001
        return "?"
    kind = info.get("Type")
    if kind in (None, "None") or info.get("is_from_converted_type"):
        return None
    if kind in ("String", "Map", "List", "Enum", "Date", "JSON", "BSON", "UUID", "Float16"):
        return {"String": "STRING", "Map": "MAP", "List": "LIST", "Enum": "ENUM", "Date": "DATE", "JSON": "JSON",
                "BSON": "BSON", "UUID": "UUID", "Float16": "FLOAT16"}[kind]
    if kind == "Null":
        return "UNKNOWN"
    if kind == "Decimal":
        return f"DECIMAL({info.get('precision')},{info.get('scale')})"
    if kind in ("Time", "Timestamp"):
        unit = _LOGICAL_UNITS.get(info.get("timeUnit"), "?")
        return f"{kind.upper()}({unit}, {'UTC' if info.get('isAdjustedToUTC') else 'LOCAL'})"
    if kind == "Int":
        return f"INTEGER({info.get('bitWidth')}, {'SIGNED' if info.get('isSigned') else 'UNSIGNED'})"
    if kind == "Variant":
        return "VARIANT" if info.get("specification_version") is None else f"VARIANT({info['specification_version']})"
    return kind.upper()


def levels(dump_: Dict[str, Any]) -> List[Tuple[int, int]]:
    """The maximum definition and repetition levels of each leaf, from the schema of a dump."""
    schema = dump_["schema"]
    found: List[Tuple[int, int]] = []

    def visit(index: int, children: int, definition: int, repetition: int) -> int:
        for _ in range(children):
            element = schema[index]
            index += 1
            d = definition + (1 if element["repetition"] in ("OPTIONAL", "REPEATED") else 0)
            r = repetition + (1 if element["repetition"] == "REPEATED" else 0)
            if element["numChildren"] is not None:
                index = visit(index, element["numChildren"], d, r)
            else:
                found.append((d, r))
        return index

    visit(1, schema[0]["numChildren"] or 0, 0, 0)
    return found


# pyarrow names codec 7 of parquet.thrift, LZ4_RAW, as LZ4, and codec 5, the LZ4 of Hadoop, as UNKNOWN.
_CODECS = {"UNCOMPRESSED": "UNCOMPRESSED", "SNAPPY": "SNAPPY", "GZIP": "GZIP", "LZO": "LZO", "BROTLI": "BROTLI",
           "LZ4": "LZ4_RAW", "LZ4_HADOOP": "LZ4", "UNKNOWN": "LZ4", "ZSTD": "ZSTD", "LZ4_RAW": "LZ4_RAW"}
# The logical type that pyarrow derives from a converted type when a column has no logical type of its own.
_FROM_CONVERTED = {"UTF8": "STRING", "MAP": "MAP", "MAP_KEY_VALUE": "MAP", "LIST": "LIST", "ENUM": "ENUM",
                   "DATE": "DATE", "TIME_MILLIS": "TIME(MILLIS, UTC)", "TIME_MICROS": "TIME(MICROS, UTC)",
                   "TIMESTAMP_MILLIS": "TIMESTAMP(MILLIS, UTC)", "TIMESTAMP_MICROS": "TIMESTAMP(MICROS, UTC)",
                   "UINT_8": "INTEGER(8, UNSIGNED)", "UINT_16": "INTEGER(16, UNSIGNED)",
                   "UINT_32": "INTEGER(32, UNSIGNED)", "UINT_64": "INTEGER(64, UNSIGNED)",
                   "INT_8": "INTEGER(8, SIGNED)", "INT_16": "INTEGER(16, SIGNED)", "INT_32": "INTEGER(32, SIGNED)",
                   "INT_64": "INTEGER(64, SIGNED)", "JSON": "JSON", "BSON": "BSON", "INTERVAL": "INTERVAL"}


def pyarrow_facts(path: Path) -> Dict[str, Any]:
    """What pyarrow reads in the footer of a file: ParquetFile.metadata, in the names of the dump."""
    import pyarrow.parquet as pq

    try:
        metadata = pq.ParquetFile(str(path)).metadata
    except Exception as error:  # noqa: BLE001 - pyarrow cannot read every file, such as an encrypted one
        return {"error": f"{type(error).__name__}: {str(error)[:200]}"}
    schema = metadata.schema
    facts: Dict[str, Any] = {
        "numRows": metadata.num_rows, "createdBy": metadata.created_by, "rowGroups": metadata.num_row_groups,
        "footerLength": metadata.serialized_size, "columns": metadata.num_columns,
        "keys": sorted(key.decode("utf-8", "replace") for key in (metadata.metadata or {})),
        "leaves": [], "groups": []}
    for i in range(metadata.num_columns):
        column = schema.column(i)
        converted = column.converted_type
        facts["leaves"].append({"path": column.path, "physicalType": column.physical_type,
                                "convertedType": None if converted == "NONE" else converted,
                                "logicalType": pyarrow_annotation(column.logical_type),
                                "levels": [column.max_definition_level, column.max_repetition_level],
                                "typeLength": column.length})
    for g in range(metadata.num_row_groups):
        group = metadata.row_group(g)
        sorting = getattr(group, "sorting_columns", None)
        chunks = []
        for c in range(group.num_columns):
            other = group.column(c)
            chunk = {"numValues": other.num_values, "totalCompressedSize": other.total_compressed_size,
                     "totalUncompressedSize": other.total_uncompressed_size, "dataPageOffset": other.data_page_offset,
                     "physicalType": other.physical_type, "filePath": other.file_path or "",
                     "codec": _CODECS.get(other.compression, other.compression),
                     "encodings": sorted(set(other.encodings)),
                     "dictionaryPageOffset": other.dictionary_page_offset if other.has_dictionary_page else None,
                     "hasOffsetIndex": other.has_offset_index, "hasColumnIndex": other.has_column_index,
                     "bloomFilterOffset": getattr(other, "bloom_filter_offset", None)}
            if other.is_stats_set:
                statistics = other.statistics
                chunk["nullCount"] = statistics.null_count if statistics.has_null_count else None
                chunk["distinctCount"] = statistics.distinct_count if statistics.has_distinct_count else None
                chunk["hasMinMax"] = statistics.has_min_max
            chunks.append(chunk)
        facts["groups"].append({"numRows": group.num_rows, "totalByteSize": group.total_byte_size,
                                "sortingColumns": None if sorting is None else [
                                    [s.column_index, s.descending, s.nulls_first] for s in sorting],
                                "chunks": chunks})
    return facts


def pyarrow_worker(listing: Path, output: Path, skip: int) -> int:
    """The pyarrow runner: the facts of each file of a listing after the first skip, one line of JSON for each, in a
    process of its own, since pyarrow aborts on some footers."""
    with open(listing, encoding="utf-8") as handle:
        items = [json.loads(line) for line in handle if line.strip()]
    with open(output, "a", encoding="utf-8", newline="\n") as out:
        for index, item in enumerate(items):
            if index >= skip:
                facts = pyarrow_facts(Path(item["path"])) if item["path"] else {"error": "not a file"}
                out.write(json.dumps({"index": index, "name": item["name"], "result": facts}, ensure_ascii=False)
                          + "\n")
                out.flush()
    return 0


def compare_with_pyarrow(footer: Dict[str, Any], facts: Dict[str, Any]) -> List[str]:
    """Where the dump of a footer differs from what pyarrow reads in the same file."""
    if "error" in facts or "crash" in facts or "hang" in facts:
        if footer.get("hasEncryptionAlgorithm") or footer.get("hasEncryptedColumns"):
            return []
        return [f"pyarrow does not read it: {facts.get('error') or facts.get('crash') or facts.get('hang')}"]
    found: List[str] = []

    def same(label: str, mine: Any, theirs: Any) -> None:
        if mine != theirs:
            found.append(f"{label}: the parser {mine!r}, pyarrow {theirs!r}")

    same("numRows", footer["numRows"], facts["numRows"])
    same("createdBy", footer["createdBy"], facts["createdBy"])
    same("rowGroups", len(footer["rowGroups"]), facts["rowGroups"])
    same("footerLength", footer["footerLength"], facts["footerLength"])
    same("columns", len(footer["leaves"]), facts["columns"])
    same("keys", sorted({kv[0] for kv in footer["keyValueMetadata"]}), facts["keys"])
    leaf_levels = levels(footer)
    for i, (leaf, theirs) in enumerate(zip(footer["leaves"], facts["leaves"])):
        label = f"leaf {i} {'.'.join(leaf['path'])}"
        same(f"{label} path", ".".join(leaf["path"]), theirs["path"])
        same(f"{label} physicalType", leaf["physicalType"], theirs["physicalType"])
        if leaf["logicalType"] is None:
            # pyarrow gives the converted type as the file has it, and a logical type derived from it.
            same(f"{label} convertedType", leaf["convertedType"], theirs["convertedType"])
            derived = leaf["typeAnnotation"] if leaf["convertedType"] == "DECIMAL" else _FROM_CONVERTED.get(
                leaf["convertedType"])
            if theirs["logicalType"] is not None:
                same(f"{label} logicalType from the converted type", derived, theirs["logicalType"])
        else:
            # pyarrow gives a converted type derived from the logical type, which may differ from the one written.
            same(f"{label} logicalType", leaf["logicalType"], theirs["logicalType"])
        same(f"{label} levels", list(leaf_levels[i]), theirs["levels"])
        if leaf["physicalType"] == "FIXED_LEN_BYTE_ARRAY":
            same(f"{label} typeLength", leaf["typeLength"], theirs["typeLength"])
    for g, (group, theirs) in enumerate(zip(footer["rowGroups"], facts["groups"])):
        same(f"row group {g} numRows", group["numRows"], theirs["numRows"])
        same(f"row group {g} totalByteSize", group["totalByteSize"], theirs["totalByteSize"])
        if theirs["sortingColumns"] is not None:
            same(f"row group {g} sortingColumns", group["sortingColumns"] or [], theirs["sortingColumns"])
        for c, (chunk, other) in enumerate(zip(group["columns"], theirs["chunks"])):
            label = f"row group {g} chunk {c}"
            for key in ("numValues", "totalCompressedSize", "totalUncompressedSize", "dataPageOffset", "physicalType",
                        "codec"):
                same(f"{label} {key}", chunk[key], other[key])
            same(f"{label} filePath", chunk["filePath"] or "", other["filePath"])
            same(f"{label} encodings", sorted(set(chunk["encodings"])), other["encodings"])
            if other["dictionaryPageOffset"] is not None:
                same(f"{label} dictionaryPageOffset", chunk["dictionaryPageOffset"], other["dictionaryPageOffset"])
            same(f"{label} offset index", chunk["offsetIndexOffset"] is not None, other["hasOffsetIndex"])
            same(f"{label} column index", chunk["columnIndexOffset"] is not None, other["hasColumnIndex"])
            if other["bloomFilterOffset"] is not None:
                same(f"{label} bloomFilterOffset", chunk["bloomFilterOffset"], other["bloomFilterOffset"])
            if other.get("nullCount") is not None:
                same(f"{label} nullCount", chunk["nullCount"], other["nullCount"])
            if other.get("distinctCount") is not None:
                same(f"{label} distinctCount", chunk["distinctCount"], other["distinctCount"])
            if other.get("hasMinMax"):
                same(f"{label} hasMinMax", chunk["hasMinMax"], True)
    return found


def compare_footers(records: List[Record], runners: Runners, pyarrow_paths: Optional[Dict[str, Path]] = None,
                    label: str = "footers") -> Tuple[List[str], Dict[str, Any]]:
    """Runs both runners over the records and compares them; returns the failures and the counts."""
    pack = runners.work / f"{label}.pack"
    count = write_pack(records, pack)
    python = runners.read("python", pack, count)
    dotnet = runners.read("dotnet", pack, count)
    facts = None
    if pyarrow_paths:
        listing = runners.work / f"{label}.pyarrow.list"
        with open(listing, "w", encoding="utf-8", newline="\n") as handle:
            for record in records:
                path = pyarrow_paths.get(record.name)
                handle.write(json.dumps({"name": record.name, "path": str(path) if path else ""}) + "\n")
        facts = runners.read("pyarrow", listing, count)
    failures: List[str] = []
    stats: Dict[str, Any] = {"records": count, "outcomes": {}, "pythonMs": 0.0, "dotnetMs": 0.0, "slowest": [],
                             "largestAllocation": (0, None), "pyarrowCompared": 0}
    for index, record in enumerate(records):
        a = python.get(index, {"result": {"crash": "no result"}})
        b = dotnet.get(index, {"result": {"crash": "no result"}})
        stats["pythonMs"] += a.get("ms") or 0.0
        stats["dotnetMs"] += b.get("ms") or 0.0
        stats["slowest"].append((max(a.get("ms") or 0.0, b.get("ms") or 0.0), record.name))
        if (b.get("allocated") or 0) > stats["largestAllocation"][0]:
            stats["largestAllocation"] = (b["allocated"], record.name)
        key = f"{outcome(a['result'])}/{outcome(b['result'])}"
        stats["outcomes"][key] = stats["outcomes"].get(key, 0) + 1
        for language, result in (("Python", a["result"]), ("C#", b["result"])):
            if "crash" in result or "hang" in result:
                failures.append(f"{record.name}: {language} {outcome(result)}: {result.get('crash') or result.get('hang')}")
        found = differences(a["result"], b["result"])
        if found:
            failures.append(f"{record.name}: the two parsers differ: " + "; ".join(found[:5])
                            + (f"; and {len(found) - 5} more" if len(found) > 5 else ""))
        if facts is not None and index in facts and outcome(a["result"]) == "footer":
            found = compare_with_pyarrow(a["result"], facts[index]["result"])
            stats["pyarrowCompared"] += 1
            if found:
                failures.append(f"{record.name}: the parser and pyarrow differ: " + "; ".join(found[:5])
                                + (f"; and {len(found) - 5} more" if len(found) > 5 else ""))
    stats["slowest"] = sorted(stats["slowest"], reverse=True)[:5]
    return failures, stats


def compare_collections_of(folder: Path, runners: Runners, compare: Callable, data_values: bool,
                           label: str = "folder") -> Tuple[List[str], int]:
    """Both collectors on a storage folder, and the differences of their footprints."""
    python_output = runners.work / f"{label}.python.pqax"
    dotnet_output = runners.work / f"{label}.dotnet.pqax"
    runners.collect_python(folder, python_output, data_values)
    runners.collect_dotnet(folder, dotnet_output, data_values)
    return compare(dotnet_output, "C#", python_output, "Python")


def print_stats(stats: Dict[str, Any], indent: str = "  ") -> None:
    outcomes = ", ".join(f"{key} {value}" for key, value in sorted(stats["outcomes"].items()))
    print(f"{indent}{stats['records']} records: {outcomes} (Python outcome/C# outcome)")
    print(f"{indent}time: Python {stats['pythonMs'] / 1000:.1f} s, C# {stats['dotnetMs'] / 1000:.1f} s; slowest "
          + ", ".join(f"{name} {ms:.0f} ms" for ms, name in stats["slowest"][:3]))
    allocated, name = stats["largestAllocation"]
    if name is not None:
        print(f"{indent}largest allocation of one C# read: {allocated / 1048576:.1f} MiB, {name}")


# ---- the kit samples, for cross_check.py ----

def kit_expected() -> Dict[str, Dict[str, Any]]:
    return json.loads((FOOTER_SAMPLES / "expected.json").read_text(encoding="utf-8"))


def check_kit(work: Path, python_env: Optional[Dict[str, str]], dotnet: str, compare: Callable) -> List[str]:
    """The samples of samples/footers read by both parsers, against each other and against expected.json, and the
    samples with a footer collected by both collectors. Returns the failures and prints a few lines."""
    work.mkdir(parents=True, exist_ok=True)
    expected = kit_expected()
    runners = Runners(work, dotnet=dotnet, python_env=python_env)
    runners.prepare()
    records = []
    for name, member in expected.items():
        records.append(record_of_file(FOOTER_SAMPLES / name, name, bool(member.get("includeDataValues")),
                                      member.get("statedSize")))
    failures, stats = compare_footers(records, runners, label="kit")
    results: Dict[int, Dict[str, Any]] = {}
    runners._load(work / "kit.python.jsonl", results)
    for index, (name, member) in enumerate(expected.items()):
        result = results.get(index, {}).get("result", {})
        if member.get("error") is not None:
            if result.get("error") != member["error"]:
                failures.append(f"samples/footers/{name}: expected the error {member['error']}, got {result}")
        else:
            facts = member["footer"]
            if "error" in result or "crash" in result:
                failures.append(f"samples/footers/{name}: expected a footer, got {result}")
                continue
            got = {"numRows": result["numRows"], "rowGroups": len(result["rowGroups"]),
                   "createdBy": result["createdBy"],
                   "leaves": [{"path": leaf["path"], "physicalType": leaf["physicalType"],
                               "typeAnnotation": leaf["typeAnnotation"], "repetition": leaf["repetition"]}
                              for leaf in result["leaves"]],
                   "keyValues": [[key, value] for key, _, value in result["keyValueMetadata"]]}
            if "bounds" in facts:
                got["bounds"] = [[chunk["minValue"], chunk["maxValue"]] for chunk in result["rowGroups"][0]["columns"]]
            for key, value in got.items():
                if facts.get(key) != value:
                    failures.append(f"samples/footers/{name}: {key} is {value!r}, expected.json has {facts.get(key)!r}")
    # Each sample with a footer, alone in a folder of its own, collected by both collectors.
    folder = work / "kit-folders"
    for name, member in expected.items():
        if member.get("footer") is not None and member.get("statedSize") is None:
            target = folder / Path(name).stem / "part-0.parquet"
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(FOOTER_SAMPLES / name, target)
    found, compared = compare_collections_of(folder, runners, compare, False, "kit-collection")
    failures += [f"samples/footers collection: {problem}" for problem in found]
    print(f"  footers: {len(expected)} samples of samples/footers read by both parsers, "
          f"{'equal' if not failures else 'FAIL'}; their collections, {compared} values compared, "
          + ("equal" if not found else f"{len(found)} differences"))
    return failures


# ---- the command line ----

def main(argv: Optional[List[str]] = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if argv and argv[0] == "--worker":
        return python_worker(Path(argv[1]), Path(argv[2]), int(argv[3]))
    if argv and argv[0] == "--pyarrow-worker":
        return pyarrow_worker(Path(argv[1]), Path(argv[2]), int(argv[3]))
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("folder", help="a folder of Parquet files, as make_footers.py writes them")
    parser.add_argument("--data-values", action="store_true", help="keep the bounds of the statistics")
    parser.add_argument("--no-collect", action="store_true", help="compare the footers only, not the collections")
    parser.add_argument("--keep", metavar="DIR", help="keep the packs and the results in DIR")
    arguments = parser.parse_args(argv)
    folder = Path(arguments.folder).resolve()
    work = Path(arguments.keep).resolve() if arguments.keep else Path(tempfile.mkdtemp(prefix="pqax-footers-"))
    work.mkdir(parents=True, exist_ok=True)
    try:
        runners = Runners(work)
        runners.prepare()
        files = sorted(path for path in folder.rglob("*.parquet") if "_delta_log" not in path.parts)
        records = [record_of_file(path, path.relative_to(folder).as_posix(), arguments.data_values) for path in files]
        try:
            import pyarrow.parquet  # noqa: F401
            pyarrow_paths = {path.relative_to(folder).as_posix(): path for path in files}
        except ImportError:
            pyarrow_paths = None
            print("pyarrow does not import: the files are not compared with its metadata")
        failures, stats = compare_footers(records, runners, pyarrow_paths, "files")
        print(f"{len(files)} files of {folder}; {stats['pyarrowCompared']} compared with pyarrow")
        print_stats(stats)
        if not arguments.no_collect:
            sys.path.insert(0, str(KIT))
            sys.path.insert(0, str(PYTHON_SOURCES))
            from cross_check import compare_collections

            found, compared = compare_collections_of(folder, runners, compare_collections, arguments.data_values)
            print(f"  collections of the folder by both collectors: {compared} values compared, "
                  + ("equal" if not found else f"{len(found)} differences"))
            failures += [f"collection: {problem}" for problem in found]
        if failures:
            print(f"{len(failures)} failure(s):")
            for failure in failures[:200]:
                print(f"  {failure}")
            if len(failures) > 200:
                print(f"  and {len(failures) - 200} more")
            return 1
        print("Every check passed.")
        return 0
    finally:
        if not arguments.keep:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
