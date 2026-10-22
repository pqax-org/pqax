"""Cross-implementation check of PQAX: files written by each writer, checked by every validator and every reader.

Usage, from the root of the repository:

    python conformance/cross_check.py [--keep DIR] [--skip python,dotnet,node] [--no-build]

Writers: Python (pqax.Builder) and C# (FootprintBuilder, through a small runner that the .NET 10 SDK runs as a
file-based app). The TypeScript package has no writer. Each writer rewrites three samples of the kit: sample.pqax,
sample.pqax again with at most 20 rows in a detail part, and extensions.pqax. When the C# command line builds, pqax
collect also collects conformance/fixtures/lakehouse, and that file must give the results of expected/sample.json.

Validators: python -m pqax check, the C# command line (pqax check, or the C# library when the command line does not
build), and validate() of the TypeScript package through node. Readers: pqax.open in Python, PqaxDocument in C#, and
openPqax in TypeScript. The valid samples of the kit are checked too, as a reference.

Every file must pass every validator, and every reader must get the expected row counts and totals: the rows of every
table, the length of every table read whole, and tablesFileSize, tablesRowCount, columnChunksCompressedSize, tables,
and largestColumns of conformance/expected. A written file has format version 0.2. What is not available is skipped
with a message.

Measures: pqax.measures in Python, StorageMeasures in C#, and the view model of @pqax/views through node compute the
derived measures of every file, which must equal conformance/expected/measures/<sample>.json, the measures of the
views, value for value. The file that pqax collect writes must give the measures of expected/measures/sample.json too,
except daysSinceLastCommit, which counts the days to the time of the collection.

Exit code 0 when every check that ran passed, 1 when one failed, 2 when nothing could run. Standard library only.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
from collections import defaultdict
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Tuple

import make_measures

KIT = Path(__file__).resolve().parent
ROOT = KIT.parent
SAMPLES = KIT / "samples"
EXPECTED = KIT / "expected"
MEASURES = EXPECTED / "measures"
PYTHON_PACKAGE = ROOT / "src" / "python"
DOTNET_LIBRARY = ROOT / "src" / "dotnet" / "Pqax" / "Pqax.csproj"
DOTNET_CLI = ROOT / "src" / "dotnet" / "Pqax.Cli" / "Pqax.Cli.csproj"
TYPESCRIPT_PACKAGE = ROOT / "src" / "typescript" / "pqax"
FIXTURE = KIT / "fixtures" / "lakehouse"

# What a writer writes: the name of the file, the sample it rewrites, its expected file, and the most rows of a part.
JOBS = (("sample", "sample.pqax", "sample", None),
        ("parts20", "sample.pqax", "sample", 20),
        ("extensions", "extensions.pqax", "extensions", None))
# The valid samples that every validator and reader also checks, as a reference. format-0.1.pqax is a file of
# format 0.1, which a reader of 0.2 opens.
REFERENCES = (("sample", "sample.pqax"), ("multipart", "multipart.pqax"), ("extensions", "extensions.pqax"),
              ("format-0.1", "format-0.1.pqax"))
# The format version that every writer writes.
WRITTEN_VERSION = "0.2"
SHAPE_KEYS = ("tablesFileSize", "tablesRowCount", "columnChunksCompressedSize", "tables", "largestColumns")
FIELDS = {"tables": ("id", "fileCount", "fileSize", "rowCount"),
          "columnChunks": ("physicalColumn", "compressedSize"),
          "physicalColumns": ("id", "physicalSchema", "column"),
          "physicalSchemas": ("id", "table")}
TIMEOUT = 600


class Skip(Exception):
    """An implementation, or a part of it, that cannot run here."""


def run(command: List[str], cwd: Optional[Path] = None, env: Optional[Dict[str, str]] = None,
        timeout: int = TIMEOUT) -> subprocess.CompletedProcess:
    return subprocess.run(command, cwd=cwd, env=env, capture_output=True, timeout=timeout)


def text(data: bytes) -> str:
    return data.decode("utf-8", errors="replace")


def last_lines(data: bytes, count: int = 6) -> str:
    lines = [line for line in text(data).splitlines() if line.strip()]
    return " | ".join(lines[-count:])


# ---- the numbers that a reader must get ----

def compute_shape(tables: Dict[str, Dict[str, List[Any]]]) -> Dict[str, Any]:
    """The totals and joins of conformance/README.md, from the fields of FIELDS as a reader returned them."""
    def field(table: str, name: str) -> List[Any]:
        fields = tables.get(table, {})
        if name in fields:
            return list(fields[name])
        length = max((len(values) for values in fields.values()), default=0)
        return [None] * length

    schema_table = dict(zip(field("physicalSchemas", "id"), field("physicalSchemas", "table")))
    column_schema = dict(zip(field("physicalColumns", "id"), field("physicalColumns", "physicalSchema")))
    column_column = dict(zip(field("physicalColumns", "id"), field("physicalColumns", "column")))
    count: Dict[Any, int] = defaultdict(int)
    size: Dict[Any, int] = defaultdict(int)
    by_column: Dict[Any, int] = defaultdict(int)
    for physical, compressed in zip(field("columnChunks", "physicalColumn"), field("columnChunks", "compressedSize")):
        compressed = compressed or 0
        owner = schema_table.get(column_schema.get(physical))
        if owner is not None:
            count[owner] += 1
            size[owner] += compressed
        column = column_column.get(physical)
        if column is not None:
            by_column[column] += compressed
    largest = sorted(by_column.items(), key=lambda item: (-item[1], item[0]))[:3]
    return {
        "tablesFileSize": sum(value for value in field("tables", "fileSize") if value is not None),
        "tablesRowCount": sum(value for value in field("tables", "rowCount") if value is not None),
        "columnChunksCompressedSize": sum(value for value in field("columnChunks", "compressedSize")
                                          if value is not None),
        "tables": [{"id": identifier, "fileCount": files, "columnChunkCount": count.get(identifier, 0),
                    "columnChunkCompressedSize": size.get(identifier, 0)}
                   for identifier, files in zip(field("tables", "id"), field("tables", "fileCount"))],
        "largestColumns": [{"id": column, "compressedSize": total} for column, total in largest],
    }


def check_read(read: Dict[str, Any], expected: Dict[str, Any], written: bool) -> List[str]:
    """What differs between a reader's result and the expected file. A written file has its own part names."""
    problems = []
    manifest = read.get("manifest", {})
    parts = [part.get("name") for part in manifest.get("parts", [])]
    if manifest.get("format") != expected["format"]:
        problems.append(f"format {manifest.get('format')!r}")
    version = WRITTEN_VERSION if written else expected["formatVersion"]
    if manifest.get("formatVersion") != version:
        problems.append(f"formatVersion {manifest.get('formatVersion')!r}, expected {version!r}")
    if written and parts[:1] != ["summary.json"]:
        problems.append(f"parts {parts}, summary.json is not first")
    if not written and parts != expected["parts"]:
        problems.append(f"parts {parts}, expected {expected['parts']}")
    if read.get("rowCounts") != expected["rows"]:
        problems.append(f"row counts {read.get('rowCounts')}, expected {expected['rows']}")
    if read.get("lengths") != expected["rows"]:
        problems.append(f"tables read whole have {read.get('lengths')} rows, expected {expected['rows']}")
    shape = compute_shape(read.get("tables", {}))
    for key in SHAPE_KEYS:
        if shape[key] != expected[key]:
            problems.append(f"{key} {shape[key]}, expected {expected[key]}")
    return problems


def stated_tables(manifest: Dict[str, Any]) -> List[str]:
    names: List[str] = []
    for part in manifest.get("parts", []):
        names += [name for name in part.get("tables", {}) if name not in names]
    return names


def instant(value: Any) -> Any:
    """A timestamp as a moment, so that 2023-11-14T22:15:20Z and 2023-11-14T22:15:20.000Z compare equal."""
    if not isinstance(value, str):
        return value
    from datetime import datetime
    try:
        return datetime.fromisoformat(value[:-1] + "+00:00" if value.endswith("Z") else value)
    except ValueError:
        return value


def differences(actual: Any, expected: Any, path: str) -> List[str]:
    """Where two measures differ, value for value; numbers compare exactly, and lastCommitAt as a moment."""
    if isinstance(expected, dict) and isinstance(actual, dict):
        problems = [f"{path}.{key} is missing" for key in expected if key not in actual]
        problems += [f"{path}.{key} is extra" for key in actual if key not in expected]
        for key in expected:
            if key in actual:
                problems += differences(actual[key], expected[key], f"{path}.{key}")
        return problems
    if isinstance(expected, list) and isinstance(actual, list):
        if len(actual) != len(expected):
            return [f"{path} has {len(actual)} items, expected {len(expected)}"]
        return [problem for index, (a, e) in enumerate(zip(actual, expected))
                for problem in differences(a, e, f"{path}[{index}]")]
    if path.endswith(".lastCommitAt"):
        actual, expected = instant(actual), instant(expected)
    same = (type(actual) is bool) == (type(expected) is bool) and actual == expected
    return [] if same else [f"{path} {actual!r}, expected {expected!r}"]


def check_measures(measured: Dict[str, Any], expected: Dict[str, Any], collected: bool = False) -> List[str]:
    """What differs between the measures of an implementation and expected/measures. For a file that pqax collect
    wrote, the days since the last commit are left out: they count to the time of the collection."""
    problems = differences(measured.get("tables"), expected["tables"], "tables")
    if collected:
        problems = [problem for problem in problems if ".maintenance.daysSinceLastCommit " not in problem]
    if measured.get("filesRead") != expected["filesRead"]:
        problems.append(f"filesRead {measured.get('filesRead')!r}, expected {expected['filesRead']!r}")
    return problems


# ---- Python ----

class Python:
    name = "python"
    validator = "python -m pqax check"

    def __init__(self) -> None:
        self.pqax: Any = None
        self.env = dict(os.environ)
        self.env["PYTHONPATH"] = os.pathsep.join(filter(None, [str(PYTHON_PACKAGE), os.environ.get("PYTHONPATH")]))

    def prepare(self, work: Path, build: bool) -> None:
        # The package of this repository, before any installed copy, as the validator gets through PYTHONPATH.
        sys.path.insert(0, str(PYTHON_PACKAGE))
        try:
            import pqax
        except ImportError as error:
            raise Skip(f"the package does not import: {error}")
        self.pqax = pqax

    def write(self, jobs: List[Tuple[Path, Path, Optional[int]]]) -> None:
        for source, target, rows_per_part in jobs:
            with self.pqax.open(str(source)) as document:
                builder = self.pqax.Builder.from_document(document)
                if rows_per_part:
                    builder.rows_per_part = rows_per_part
                builder.write(str(target))

    def validate(self, paths: List[Path]) -> Dict[Path, Tuple[bool, str]]:
        results = {}
        for path in paths:
            completed = run([sys.executable, "-m", "pqax", "check", str(path)], cwd=ROOT, env=self.env)
            results[path] = (completed.returncode == 0, f"exit code {completed.returncode}: "
                                                        f"{last_lines(completed.stdout + completed.stderr)}")
        return results

    def read(self, paths: List[Path]) -> Dict[Path, Dict[str, Any]]:
        results = {}
        for path in paths:
            try:
                with self.pqax.open(str(path)) as document:
                    names = stated_tables(document.manifest)
                    tables = {name: document.table(name) for name in names}
                    results[path] = {
                        "manifest": document.manifest,
                        "rowCounts": {name: document.row_count(name) for name in names},
                        "lengths": {name: max((len(values) for values in tables[name].values()), default=0)
                                    for name in names},
                        "tables": {table: {name: list(document.table(table)[name]) for name in fields
                                           if name in document.table(table)} for table, fields in FIELDS.items()},
                    }
            except Exception as error:  # noqa: BLE001 - a reader error is a result
                results[path] = {"error": f"{type(error).__name__}: {error}"}
        return results

    def measures(self, paths: List[Path]) -> Dict[Path, Dict[str, Any]]:
        results = {}
        for path in paths:
            try:
                with self.pqax.open(str(path)) as document:
                    results[path] = self.pqax.measures.measure(document)
            except Exception as error:  # noqa: BLE001 - an error is a result
                results[path] = {"error": f"{type(error).__name__}: {error}"}
        return results


# ---- C# ----

RUNNER = r"""#:project __LIBRARY__
// The C# side of conformance/cross_check.py, run by the .NET 10 SDK as a file-based app.
//   write (source target rowsPerPart)...   rewrites each source with FootprintBuilder.From; rowsPerPart 0 keeps the default
//   inspect file...                         validates and reads each file, and prints one JSON array
//   measures file...                        the derived measures of each file, as one JSON array
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Pqax;

if (args.Length > 0 && args[0] == "write")
{
    for (int i = 1; i + 2 < args.Length; i += 3)
    {
        using PqaxDocument document = PqaxDocument.Open(args[i]);
        FootprintBuilder builder = FootprintBuilder.From(document);
        int rows = int.Parse(args[i + 2], CultureInfo.InvariantCulture);
        if (rows > 0)
        {
            builder.Options.RowsPerPart = rows;
        }
        builder.Write(args[i + 1]);
    }
    return 0;
}
if (args.Length > 0 && args[0] == "measures")
{
    using Stream stream = Console.OpenStandardOutput();
    using var json = new Utf8JsonWriter(stream);
    json.WriteStartArray();
    foreach (string path in args.Skip(1))
    {
        json.WriteStartObject();
        try
        {
            using PqaxDocument document = PqaxDocument.Open(path);
            FootprintMeasures measures = StorageMeasures.Compute(document);
            json.WriteBoolean("filesRead", measures.FilesRead);
            json.WriteStartArray("tables");
            foreach (TableStorage table in measures.Tables)
            {
                table.WriteTo(json);
            }
            json.WriteEndArray();
        }
        catch (Exception error)
        {
            json.WriteString("error", error.GetType().Name + ": " + error.Message);
        }
        json.WriteEndObject();
    }
    json.WriteEndArray();
    return 0;
}
if (args.Length == 0 || args[0] != "inspect")
{
    Console.Error.WriteLine("usage: write (source target rowsPerPart)... | inspect file... | measures file...");
    return 2;
}
using (Stream output = Console.OpenStandardOutput())
using (var writer = new Utf8JsonWriter(output))
{
    writer.WriteStartArray();
    foreach (string path in args.Skip(1))
    {
        writer.WriteStartObject();
        writer.WriteString("file", path);
        try
        {
            ValidationResult result = PqaxValidator.Validate(path);
            writer.WriteStartObject("validation");
            writer.WriteBoolean("isValid", result.IsValid);
            writer.WriteStartArray("codes");
            foreach (ValidationIssue issue in result.Issues)
            {
                writer.WriteStringValue(issue.Code);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        catch (Exception error)
        {
            writer.WriteString("validationError", error.GetType().Name + ": " + error.Message);
        }
        try
        {
            byte[] read = Read(path);
            writer.WritePropertyName("read");
            writer.WriteRawValue(read);
        }
        catch (Exception error)
        {
            writer.WriteString("error", error.GetType().Name + ": " + error.Message);
        }
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
}
return 0;

static byte[] Read(string path)
{
    using PqaxDocument doc = PqaxDocument.Open(path);
    var names = new List<string>();
    foreach (PartInfo part in doc.Manifest.Parts)
    {
        names.AddRange(part.Tables.Keys.Where(name => !names.Contains(name)));
    }
    var lengths = names.ToDictionary(name => name, name => Length(doc, name));
    var chunks = doc.ReadColumnChunks();
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer))
    {
        writer.WriteStartObject();
        writer.WriteStartObject("manifest");
        writer.WriteString("format", doc.Manifest.Format);
        writer.WriteString("formatVersion", doc.Manifest.FormatVersion);
        writer.WriteStartArray("parts");
        foreach (PartInfo part in doc.Manifest.Parts)
        {
            writer.WriteStartObject();
            writer.WriteString("name", part.Name);
            writer.WriteStartObject("tables");
            foreach (KeyValuePair<string, int> pair in part.Tables)
            {
                writer.WriteNumber(pair.Key, pair.Value);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteStartObject("rowCounts");
        foreach (string name in names)
        {
            writer.WriteNumber(name, doc.RowCount(name));
        }
        writer.WriteEndObject();
        writer.WriteStartObject("lengths");
        foreach (string name in names)
        {
            writer.WriteNumber(name, lengths[name]);
        }
        writer.WriteEndObject();
        writer.WriteStartObject("tables");
        writer.WriteStartObject("tables");
        Numbers(writer, "id", doc.Summary.Tables.Select(row => (long?)row.Id));
        Numbers(writer, "fileCount", doc.Summary.Tables.Select(row => row.FileCount));
        Numbers(writer, "fileSize", doc.Summary.Tables.Select(row => row.FileSize));
        Numbers(writer, "rowCount", doc.Summary.Tables.Select(row => row.RowCount));
        writer.WriteEndObject();
        writer.WriteStartObject("columnChunks");
        Numbers(writer, "physicalColumn", chunks.Select(row => row.PhysicalColumn));
        Numbers(writer, "compressedSize", chunks.Select(row => row.CompressedSize));
        writer.WriteEndObject();
        writer.WriteStartObject("physicalColumns");
        Numbers(writer, "id", doc.Summary.PhysicalColumns.Select(row => (long?)row.Id));
        Numbers(writer, "physicalSchema", doc.Summary.PhysicalColumns.Select(row => row.PhysicalSchema));
        Numbers(writer, "column", doc.Summary.PhysicalColumns.Select(row => row.Column));
        writer.WriteEndObject();
        writer.WriteStartObject("physicalSchemas");
        Numbers(writer, "id", doc.Summary.PhysicalSchemas.Select(row => (long?)row.Id));
        Numbers(writer, "table", doc.Summary.PhysicalSchemas.Select(row => row.Table));
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
    return buffer.ToArray();
}

static void Numbers(Utf8JsonWriter writer, string name, IEnumerable<long?> values)
{
    writer.WriteStartArray(name);
    foreach (long? value in values)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
    writer.WriteEndArray();
}

static int Length(PqaxDocument doc, string name) => name switch
{
    "sources" => doc.Summary.Sources.Count,
    "tables" => doc.Summary.Tables.Count,
    "columns" => doc.Summary.Columns.Count,
    "partitionSpecs" => doc.Summary.PartitionSpecs.Count,
    "partitionFields" => doc.Summary.PartitionFields.Count,
    "sortOrders" => doc.Summary.SortOrders.Count,
    "sortFields" => doc.Summary.SortFields.Count,
    "physicalSchemas" => doc.Summary.PhysicalSchemas.Count,
    "physicalColumns" => doc.Summary.PhysicalColumns.Count,
    "writers" => doc.Summary.Writers.Count,
    "operations" => doc.Summary.Operations.Count,
    "issues" => doc.Summary.Issues.Count,
    "modelBindings" => doc.Summary.ModelBindings.Count,
    "tableBindings" => doc.Summary.TableBindings.Count,
    "columnBindings" => doc.Summary.ColumnBindings.Count,
    "partitions" => doc.ReadPartitions().Count,
    "files" => doc.ReadFiles().Count,
    "rowGroups" => doc.ReadRowGroups().Count,
    "columnChunks" => doc.ReadColumnChunks().Count,
    "deleteFiles" => doc.ReadDeleteFiles().Count,
    "commits" => doc.ReadCommits().Count,
    _ => doc.ReadRaw(name).Count,
};
"""


class DotNet:
    name = "dotnet"

    def __init__(self) -> None:
        self.dotnet: Optional[str] = None
        self.runner: Optional[Path] = None
        self.cli: Optional[Path] = None
        self.cli_note = ""
        self.inspected: Dict[Path, Dict[str, Any]] = {}

    @property
    def validator(self) -> str:
        return "pqax check (C#)" if self.cli else "PqaxValidator (C#)"

    def prepare(self, work: Path, build: bool) -> None:
        self.dotnet = shutil.which("dotnet")
        if not self.dotnet:
            raise Skip("dotnet is not on the PATH")
        if not DOTNET_LIBRARY.is_file():
            raise Skip(f"{DOTNET_LIBRARY.relative_to(ROOT)} does not exist")
        folder = work / "dotnet-runner"
        folder.mkdir(parents=True, exist_ok=True)
        self.runner = folder / "runner.cs"
        self.runner.write_text(RUNNER.replace("__LIBRARY__", DOTNET_LIBRARY.as_posix()), encoding="utf-8")
        completed = run([self.dotnet, "build", str(self.runner), "-nologo", "-v", "q"], cwd=folder)
        if completed.returncode != 0:
            raise Skip(f"the runner does not build (it needs the .NET 10 SDK): {last_lines(completed.stdout)}")
        if not build:
            self.cli_note = "the command line was not built (--no-build); the library validator is used"
            return
        completed = run([self.dotnet, "build", str(DOTNET_CLI), "-nologo", "-v", "q"], cwd=ROOT)
        if completed.returncode != 0:
            self.cli_note = f"the command line does not build, the library validator is used: " \
                            f"{last_lines(completed.stdout, 3)}"
            return
        def framework(config: Path) -> Tuple[int, ...]:
            digits = "".join(c if c.isdigit() else " " for c in config.parent.name).split()
            return tuple(int(d) for d in digits)

        for config in sorted((DOTNET_CLI.parent / "bin" / "Debug").glob("*/*.runtimeconfig.json"), key=framework):
            dll = config.with_name(config.name.replace(".runtimeconfig.json", ".dll"))
            if dll.is_file():
                self.cli = dll
        if self.cli is None:
            self.cli_note = "the command line built, but its assembly was not found; the library validator is used"

    def extra(self, folder: Path) -> List[Tuple[Path, str, str]]:
        """The fixture of the kit collected by pqax collect, which every validator and reader then checks as well."""
        if not self.cli:
            return []
        target = folder / "collected.pqax"
        completed = run([self.dotnet, str(self.cli), "collect", "--local", str(FIXTURE), "--output", str(target)], cwd=ROOT)
        if completed.returncode != 0 or not target.is_file():
            raise RuntimeError(f"pqax collect ended with exit code {completed.returncode}: "
                               f"{last_lines(completed.stdout + completed.stderr)}")
        return [(target, f"{self.name}/collected.pqax", "sample")]

    def call(self, arguments: List[str]) -> subprocess.CompletedProcess:
        assert self.dotnet and self.runner
        return run([self.dotnet, "run", str(self.runner), "--", *arguments], cwd=self.runner.parent)

    def write(self, jobs: List[Tuple[Path, Path, Optional[int]]]) -> None:
        arguments = ["write"]
        for source, target, rows_per_part in jobs:
            arguments += [str(source), str(target), str(rows_per_part or 0)]
        completed = self.call(arguments)
        if completed.returncode != 0:
            raise RuntimeError(f"the C# writer failed: {last_lines(completed.stdout + completed.stderr)}")

    def inspect(self, paths: List[Path]) -> None:
        missing = [path for path in paths if path not in self.inspected]
        if not missing:
            return
        completed = self.call(["inspect", *map(str, missing)])
        if completed.returncode != 0:
            raise RuntimeError(f"the C# runner failed: {last_lines(completed.stdout + completed.stderr)}")
        for path, entry in zip(missing, json.loads(text(completed.stdout))):
            self.inspected[path] = entry

    def validate(self, paths: List[Path]) -> Dict[Path, Tuple[bool, str]]:
        results = {}
        if self.cli:
            for path in paths:
                completed = run([self.dotnet, str(self.cli), "check", str(path)], cwd=ROOT)
                results[path] = (completed.returncode == 0, f"exit code {completed.returncode}: "
                                                            f"{last_lines(completed.stdout + completed.stderr)}")
            return results
        self.inspect(paths)
        for path in paths:
            entry = self.inspected[path]
            if "validation" not in entry:
                results[path] = (False, entry.get("validationError", "no result"))
            else:
                validation = entry["validation"]
                results[path] = (validation["isValid"], f"codes {validation['codes']}")
        return results

    def read(self, paths: List[Path]) -> Dict[Path, Dict[str, Any]]:
        self.inspect(paths)
        return {path: self.inspected[path].get("read") or {"error": self.inspected[path].get("error", "no result")}
                for path in paths}

    def measures(self, paths: List[Path]) -> Dict[Path, Dict[str, Any]]:
        if not paths:
            return {}
        completed = self.call(["measures", *map(str, paths)])
        if completed.returncode != 0:
            raise RuntimeError(f"the C# runner failed: {last_lines(completed.stdout + completed.stderr)}")
        return dict(zip(paths, json.loads(text(completed.stdout))))


# ---- TypeScript ----

NODE_SCRIPT = r"""// The TypeScript side of conformance/cross_check.py: validates and reads each file, and prints one JSON array.
const pqax = await import(__PACKAGE__);
const { fromFile } = await import(__NODE__);
const fields = __FIELDS__;
const summaryNames = new Set(pqax.summaryTableNames);
const detailNames = new Set(pqax.detailTableNames);
const results = [];
for (const file of process.argv.slice(2)) {
  const entry = { file };
  try {
    const result = await pqax.validate(await fromFile(file));
    entry.validation = { isValid: result.isValid, codes: result.issues.map((issue) => issue.code) };
  } catch (error) {
    entry.validationError = String(error);
  }
  try {
    const doc = await pqax.openPqax(await fromFile(file));
    const names = [];
    for (const part of doc.manifest.parts) {
      for (const name of Object.keys(part.tables)) if (!names.includes(name)) names.push(name);
    }
    const table = async (name) => {
      if (summaryNames.has(name)) return doc.summary[name] ?? { length: 0 };
      if (detailNames.has(name)) return await doc.readTable(name);
      const raw = await doc.readRaw(name);
      const arrays = Object.values(raw).filter(Array.isArray);
      return { length: Math.max(0, ...arrays.map((values) => values.length)) };
    };
    const read = { manifest: doc.manifest, rowCounts: {}, lengths: {}, tables: {} };
    for (const name of names) {
      read.rowCounts[name] = doc.rowCount(name);
      read.lengths[name] = (await table(name)).length;
    }
    for (const [name, wanted] of Object.entries(fields)) {
      const values = await table(name);
      read.tables[name] = {};
      for (const field of wanted) if (values[field] !== undefined) read.tables[name][field] = Array.from(values[field]);
    }
    entry.read = read;
  } catch (error) {
    entry.error = String(error);
  }
  results.push(entry);
}
process.stdout.write(JSON.stringify(results));
"""


class Node:
    name = "node"
    validator = "validate() (TypeScript)"

    def __init__(self) -> None:
        self.node: Optional[str] = None
        self.script: Optional[Path] = None
        self.inspected: Dict[Path, Dict[str, Any]] = {}
        self.measures_command: Optional[List[str]] = None
        self.measures_note = ""

    def prepare(self, work: Path, build: bool) -> None:
        self.node = shutil.which("node")
        if not self.node:
            raise Skip("node is not on the PATH")
        sources = list((TYPESCRIPT_PACKAGE / "src").glob("**/*.ts"))
        if not sources:
            raise Skip(f"{TYPESCRIPT_PACKAGE.relative_to(ROOT)} has no sources")
        entry = TYPESCRIPT_PACKAGE / "dist" / "index.js"
        newest = max(path.stat().st_mtime for path in sources)
        if not entry.is_file() or entry.stat().st_mtime < newest:
            npm = shutil.which("npm")
            if not build:
                raise Skip("dist/ is missing or older than src/; run npm run build in the package, or leave out "
                           "--no-build")
            # npm ci at the root of the repository installs the workspaces there, and in the package otherwise.
            installed = (ROOT / "node_modules").is_dir() or (TYPESCRIPT_PACKAGE / "node_modules").is_dir()
            if not npm or not installed:
                raise Skip("dist/ is missing or older than src/, and npm or node_modules is missing to build it")
            completed = run([npm, "run", "build"], cwd=TYPESCRIPT_PACKAGE)
            if completed.returncode != 0 or not entry.is_file():
                raise Skip(f"npm run build failed: {last_lines(completed.stdout + completed.stderr)}")
        try:
            self.measures_command = make_measures.prepare(work, build)
        except make_measures.Unavailable as reason:
            self.measures_note = f"the measures of the views are skipped: {reason}"
        script = NODE_SCRIPT.replace("__PACKAGE__", json.dumps(entry.as_uri()))
        script = script.replace("__NODE__", json.dumps((entry.parent / "node.js").as_uri()))
        script = script.replace("__FIELDS__", json.dumps(FIELDS))
        self.script = work / "node-inspect.mjs"
        self.script.write_text(script, encoding="utf-8")

    def inspect(self, paths: List[Path]) -> None:
        missing = [path for path in paths if path not in self.inspected]
        if not missing:
            return
        assert self.node and self.script
        completed = run([self.node, str(self.script), *map(str, missing)], cwd=ROOT)
        if completed.returncode != 0:
            raise RuntimeError(f"node failed: {last_lines(completed.stdout + completed.stderr)}")
        for path, entry in zip(missing, json.loads(text(completed.stdout))):
            self.inspected[path] = entry

    def validate(self, paths: List[Path]) -> Dict[Path, Tuple[bool, str]]:
        self.inspect(paths)
        results = {}
        for path in paths:
            entry = self.inspected[path]
            if "validation" not in entry:
                results[path] = (False, entry.get("validationError", "no result"))
            else:
                results[path] = (entry["validation"]["isValid"], f"codes {entry['validation']['codes']}")
        return results

    def read(self, paths: List[Path]) -> Dict[Path, Dict[str, Any]]:
        self.inspect(paths)
        return {path: self.inspected[path].get("read") or {"error": self.inspected[path].get("error", "no result")}
                for path in paths}

    def measures(self, paths: List[Path]) -> Dict[Path, Dict[str, Any]]:
        if self.measures_command is None:
            return {}
        return dict(zip(paths, make_measures.node_measures(self.measures_command, paths)))


# ---- the check ----

def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Cross-implementation check of the PQAX writers, validators, and "
                                                 "readers.")
    parser.add_argument("--keep", metavar="DIR", help="write the files in DIR and keep them")
    parser.add_argument("--skip", default="", help="implementations to leave out, separated by commas: "
                                                   "python, dotnet, node")
    parser.add_argument("--no-build", action="store_true",
                        help="do not build the C# command line or the TypeScript package; use what is built")
    arguments = parser.parse_args(argv)
    skip = {name.strip() for name in arguments.skip.split(",") if name.strip()}
    expected = {path.stem: json.loads(path.read_text(encoding="utf-8")) for path in EXPECTED.glob("*.json")}

    work = Path(arguments.keep).resolve() if arguments.keep else Path(tempfile.mkdtemp(prefix="pqax-cross-"))
    work.mkdir(parents=True, exist_ok=True)
    try:
        return check(work, skip, not arguments.no_build, expected)
    finally:
        if not arguments.keep:
            shutil.rmtree(work, ignore_errors=True)


def check(work: Path, skip: set, build: bool, expected: Dict[str, Dict[str, Any]]) -> int:
    implementations = []
    print("PQAX cross-implementation check")
    for implementation in (Python(), DotNet(), Node()):
        if implementation.name in skip:
            print(f"  {implementation.name:<7} skipped: --skip")
            continue
        try:
            implementation.prepare(work, build)
        except Skip as reason:
            print(f"  {implementation.name:<7} skipped: {reason}")
            continue
        implementations.append(implementation)
        note = "; ".join(filter(None, (getattr(implementation, "cli_note", ""),
                                       getattr(implementation, "measures_note", ""))))
        print(f"  {implementation.name:<7} ready; validator: {implementation.validator}" + (f"; {note}" if note else ""))
    if not implementations:
        print("Nothing to run.")
        return 2

    # (path, label, expected file name, written by a writer)
    files: List[Tuple[Path, str, str, bool]] = [(SAMPLES / sample, f"kit/{sample}", name, False)
                                                for name, sample in REFERENCES]
    failures: List[str] = []
    for writer in implementations:
        if not hasattr(writer, "write"):
            continue
        folder = work / writer.name
        folder.mkdir(exist_ok=True)
        jobs = [(SAMPLES / sample, folder / f"{name}.pqax", rows) for name, sample, _, rows in JOBS]
        try:
            writer.write(jobs)
        except Exception as error:  # noqa: BLE001 - a writer that fails is a result
            failures.append(f"{writer.name} writer: {type(error).__name__}: {error}")
            print(f"  {writer.name:<7} writer failed: {error}")
            continue
        files += [(folder / f"{name}.pqax", f"{writer.name}/{name}.pqax", expected_name, True)
                  for name, _, expected_name, _ in JOBS]
        if hasattr(writer, "extra"):
            try:
                files += [(path, label, expected_name, True) for path, label, expected_name in writer.extra(folder)]
            except Exception as error:  # noqa: BLE001 - a collector that fails is a result
                failures.append(f"{writer.name} collector: {type(error).__name__}: {error}")
                print(f"  {writer.name:<7} collector failed: {error}")
    print("  writers: " + ", ".join(implementation.name for implementation in implementations
                                    if hasattr(implementation, "write")) + "; the TypeScript package has no writer")
    if any(label.endswith("/collected.pqax") for _, label, _, _ in files):
        print("  collector: pqax collect on conformance/fixtures/lakehouse, checked against expected/sample.json")

    paths = [path for path, _, _, _ in files]
    expected_measures = {path.stem: json.loads(path.read_text(encoding="utf-8")) for path in MEASURES.glob("*.json")}
    measured_paths = [path for path, _, name, _ in files if name in expected_measures]
    validations: Dict[str, Dict[Path, Tuple[bool, str]]] = {}
    reads: Dict[str, Dict[Path, Dict[str, Any]]] = {}
    measured: Dict[str, Dict[Path, Dict[str, Any]]] = {}
    for implementation in implementations:
        try:
            validations[implementation.name] = implementation.validate(paths)
        except Exception as error:  # noqa: BLE001
            failures.append(f"{implementation.name} validator: {type(error).__name__}: {error}")
        try:
            reads[implementation.name] = implementation.read(paths)
        except Exception as error:  # noqa: BLE001
            failures.append(f"{implementation.name} reader: {type(error).__name__}: {error}")
        try:
            measured[implementation.name] = implementation.measures(measured_paths)
        except Exception as error:  # noqa: BLE001
            failures.append(f"{implementation.name} measures: {type(error).__name__}: {error}")

    names = [implementation.name for implementation in implementations]
    width = max(len(label) for _, label, _, _ in files)
    header = f"{'file':<{width}}  " + "".join(f"check:{name:<9}" for name in names) + \
             "".join(f"read:{name:<10}" for name in names) + "".join(f"measures:{name:<9}" for name in names)
    print()
    print(header)
    print("-" * len(header))
    for path, label, expected_name, written in files:
        cells = []
        for name in names:
            result = validations.get(name, {}).get(path)
            if result is None:
                cells.append(f"{'n/a':<15}")
            else:
                cells.append(f"{'ok' if result[0] else 'FAIL':<15}")
                if not result[0]:
                    failures.append(f"{label}: the {name} validator rejects it: {result[1]}")
        for name in names:
            read = reads.get(name, {}).get(path)
            if read is None:
                cells.append(f"{'n/a':<15}")
                continue
            if "error" in read:
                problems = [read["error"]]
            else:
                problems = check_read(read, expected[expected_name], written)
            cells.append(f"{'ok' if not problems else 'FAIL':<15}")
            failures += [f"{label}: the {name} reader: {problem}" for problem in problems]
        for name in names:
            result = measured.get(name, {}).get(path)
            if result is None:
                cells.append(f"{'n/a':<18}")
                continue
            collected = label.endswith("/collected.pqax")
            problems = ([result["error"]] if "error" in result
                        else check_measures(result, expected_measures[expected_name], collected))
            cells.append(f"{'ok' if not problems else 'FAIL':<18}")
            failures += [f"{label}: the {name} measures: {problem}" for problem in problems]
        print(f"{label:<{width}}  " + "".join(cells))
    print()
    if failures:
        print(f"{len(failures)} failure(s):")
        for failure in failures:
            print(f"  {failure}")
        return 1
    print(f"Every check passed: {len(files)} files, each checked by the validators and the readers of "
          f"{', '.join(names)}, and the measures of {len(measured_paths)} of them against expected/measures.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
