"""Rebuilds the expected measures of the PQAX conformance kit from the storage views.

Usage, from the root of the repository:

    python conformance/make_measures.py [--check] [--no-build]

Input: the valid samples, samples/*.pqax and samples/*.vpax. Output, which replaces everything in the folder:

    expected/measures/<sample>.json     the derived measures of the tables, the columns, and the collection

The measures are those of the view model of @pqax/views, src/typescript/views/src/model.ts: the script runs
buildModel(await readFootprint(doc)) through node on each sample and keeps every measure of section 10 of the object
model that the model computes: for each table, the storage stack with the share of each component, the small files, the
file size figures and the V-Order shares, the ratios, the history with VACUUM and OPTIMIZE, and the partitions; for
each column, its six measures; and the totals and the depths of the collection. The two measures over a set of
columns, which only the views offer, are left out. The C# and Python implementations of the measures are tested against
these files, and cross_check.py compares the three.

--check writes nothing and exits with 1 when a file differs from what the views compute, or is missing or extra.
--no-build uses the dist/ folders of the two TypeScript packages as they are; otherwise the script runs npm run build
at the root of the repository when a dist/ folder is missing or older than its sources.

Exit code 0 when the files are written or up to date, 1 when --check finds a difference, 2 when node or the
packages are missing. Standard library only.
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any, Dict, List, Optional

KIT = Path(__file__).resolve().parent
ROOT = KIT.parent
SAMPLES = KIT / "samples"
MEASURES = KIT / "expected" / "measures"
PACKAGES = (ROOT / "src" / "typescript" / "pqax", ROOT / "src" / "typescript" / "views")
TIMEOUT = 600

# The node side: opens each file with the reader, builds the model of the views, and prints one JSON array with the
# measures of each file. A component that the views mark absent is null.
NODE_SCRIPT = r"""// The measures of the storage views for conformance/make_measures.py and conformance/cross_check.py.
const pqax = await import(__PACKAGE__);
const { fromFile } = await import(__NODE__);
const views = await import(__MODEL__);
const tableJson = (t) => {
  const component = (key) => t.stack.components.find((c) => c.key === key);
  const stack = {};
  for (const key of views.STACK_COMPONENTS) stack[key] = component(key).absent ? null : component(key).bytes;
  stack.deletedSource = component("deleted").source ?? null;
  stack.listed = t.stack.listed;
  stack.data = t.stack.data;
  stack.total = t.stack.total;
  stack.waste = t.stack.waste;
  stack.overhead = t.stack.overhead;
  stack.wasteShare = t.stack.wasteShare;
  stack.overheadShare = t.stack.overheadShare;
  stack.shares = {};
  for (const key of views.STACK_COMPONENTS) stack.shares[key] = component(key).share;
  const small = (s) => ({ threshold: s.threshold, count: s.count, shareAtLeast: s.shareAtLeast });
  const m = t.measures;
  const f = t.files;
  const h = t.maintenance;
  return {
    id: t.id, name: t.name, label: t.label, rowCount: t.rowCount, fileCount: t.fileCount, fileSize: t.fileSize, stack,
    smallFiles: small(t.files.smallFiles), tinyFiles: small(t.files.tinyFiles),
    files: {
      count: f.count, sum: f.sum, minimum: f.minimum, firstQuartile: f.firstQuartile, median: f.median,
      thirdQuartile: f.thirdQuartile, maximum: f.maximum,
      vOrder: f.vOrder === null ? null : { share: f.vOrder.share, bytesShare: f.vOrder.bytesShare },
    },
    measures: {
      compressionRatio: m.compressionRatio, deletedRowShare: m.deletedRowShare, averageFileSize: m.averageFileSize,
      averageRowGroupRows: m.averageRowGroupRows, footerCoverage: m.footerCoverage,
      staleColumnShare: m.staleColumnShare, nonLiveShare: m.nonLiveShare, averagePartitionSize: m.averagePartitionSize,
      dictionaryFallbackShare: m.dictionaryFallbackShare,
    },
    maintenance: {
      lastCommitAt: h.lastCommitAt, commitsPerDay: h.commitsPerDay, daysSinceLastCommit: h.daysSinceLastCommit,
      hasVacuum: h.hasVacuum, lastVacuumAt: h.lastVacuumAt, hasOptimize: h.hasOptimize, lastOptimizeAt: h.lastOptimizeAt,
    },
    partitions: {
      count: t.partitions.count, kept: t.partitions.kept, oldestModifiedAt: t.partitions.oldestModifiedAt,
      newestModifiedAt: t.partitions.newestModifiedAt,
    },
  };
};
// The model of a column has its label only; the name is the field of the file, a column model for each row.
const columnJson = (c, row, names, tableIds) => ({
  id: c.id, table: tableIds.get(c.tableKey) ?? null, name: typeof names?.[row] === "string" ? names[row] : null,
  label: c.label,
  nullShare: c.nullShare, compressionRatio: c.compressionRatio, dictionaryShare: c.dictionaryShare,
  dictionaryFallbackShare: c.dictionaryFallbackShare, averageValueLength: c.averageValueLength,
  filesWithoutColumnShare: c.filesWithoutColumnShare,
});
const results = [];
for (const file of process.argv.slice(2)) {
  try {
    const source = await fromFile(file);
    const doc = file.toLowerCase().endsWith(".vpax") ? await pqax.openEmbedded(source) : await pqax.openPqax(source);
    const model = views.buildModel(await views.readFootprint(doc));
    const tableIds = new Map(model.tables.filter((t) => t.id !== null).map((t) => [t.key, t.id]));
    const names = doc.summary.columns?.name;
    results.push({
      file: file.split(/[\\/]/).pop(), embeddedPart: doc.embeddedPart ?? null,
      smallFileSize: views.DEFAULT_SMALL_FILE_SIZE, tinyFileSize: views.DEFAULT_TINY_FILE_SIZE,
      filesRead: model.hasFiles, warnings: model.warnings, tables: model.tables.map(tableJson),
      columns: model.columns.map((c, row) => columnJson(c, row, names, tableIds)),
      collection: { totals: { ...model.collection.totals }, depths: { ...model.collection.depths } },
    });
  } catch (error) {
    results.push({ file, error: String(error) });
  }
}
process.stdout.write(JSON.stringify(results));
"""


class Unavailable(Exception):
    """Node or the TypeScript packages are not there."""


def prepare(work: Path, build: bool) -> List[str]:
    """The command that runs the node script on files: node, and the script written in work."""
    node = shutil.which("node")
    if not node:
        raise Unavailable("node is not on the PATH")
    for package in PACKAGES:
        sources = list((package / "src").glob("**/*.ts"))
        entry = package / "dist" / "index.js"
        if not sources:
            raise Unavailable(f"{package.relative_to(ROOT)} has no sources")
        if entry.is_file() and entry.stat().st_mtime >= max(path.stat().st_mtime for path in sources):
            continue
        if not build:
            raise Unavailable(f"{package.relative_to(ROOT)}/dist is missing or older than src; run npm run build at "
                              f"the root, or leave out --no-build")
        npm = shutil.which("npm")
        if not npm or not (ROOT / "node_modules").is_dir():
            raise Unavailable("dist/ is missing or older than src/, and npm or node_modules is missing to build it; "
                              "run npm ci at the root")
        completed = subprocess.run([npm, "run", "build"], cwd=ROOT, capture_output=True, timeout=TIMEOUT)
        if completed.returncode != 0 or not entry.is_file():
            lines = completed.stdout.decode("utf-8", "replace").splitlines()[-6:]
            raise Unavailable("npm run build failed: " + " | ".join(lines))
    reader, views = PACKAGES
    script = NODE_SCRIPT.replace("__PACKAGE__", json.dumps((reader / "dist" / "index.js").as_uri()))
    script = script.replace("__NODE__", json.dumps((reader / "dist" / "node.js").as_uri()))
    script = script.replace("__MODEL__", json.dumps((views / "dist" / "model.js").as_uri()))
    path = work / "measures.mjs"
    path.write_text(script, encoding="utf-8")
    return [node, str(path)]


def node_measures(command: List[str], paths: List[Path]) -> List[Dict[str, Any]]:
    """The measures of the views for each file, in the order of the paths."""
    if not paths:
        return []
    completed = subprocess.run([*command, *map(str, paths)], cwd=ROOT, capture_output=True, timeout=TIMEOUT)
    if completed.returncode != 0:
        lines = (completed.stdout + completed.stderr).decode("utf-8", "replace").splitlines()[-6:]
        raise RuntimeError("node failed: " + " | ".join(lines))
    return json.loads(completed.stdout.decode("utf-8"))


def samples() -> List[Path]:
    """The valid samples of the kit, sorted by name."""
    return sorted([*SAMPLES.glob("*.pqax"), *SAMPLES.glob("*.vpax")], key=lambda path: path.name)


def pretty(value: Any, indent: int = 0, width: int = 120) -> str:
    """JSON with two spaces of indentation, with short objects and arrays on one line, as make_kit.py writes it."""
    flat = json.dumps(value, ensure_ascii=False)
    if len(flat) + indent <= width or not isinstance(value, (dict, list)) or not value:
        return flat
    pad = " " * (indent + 2)
    if isinstance(value, dict):
        items = [f"{pad}{json.dumps(k)}: {pretty(v, indent + 2, width)}" for k, v in value.items()]
        return "{\n" + ",\n".join(items) + "\n" + " " * indent + "}"
    return "[\n" + ",\n".join(f"{pad}{pretty(v, indent + 2, width)}" for v in value) + "\n" + " " * indent + "]"


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Rebuild conformance/expected/measures from the storage views.")
    parser.add_argument("--check", action="store_true", help="write nothing; exit 1 when a file is not up to date")
    parser.add_argument("--no-build", action="store_true", help="do not build the TypeScript packages")
    arguments = parser.parse_args(argv)
    work = Path(tempfile.mkdtemp(prefix="pqax-measures-"))
    try:
        command = prepare(work, not arguments.no_build)
        paths = samples()
        results = node_measures(command, paths)
    except Unavailable as reason:
        print(f"make_measures: {reason}")
        return 2
    finally:
        shutil.rmtree(work, ignore_errors=True)
    failed = [result for result in results if "error" in result]
    if failed:
        for result in failed:
            print(f"make_measures: the views cannot read {result['file']}: {result['error']}")
        return 2
    texts = {f"{path.stem}.json": pretty(result) + "\n" for path, result in zip(paths, results)}
    if arguments.check:
        present = {path.name for path in MEASURES.glob("*.json")} if MEASURES.is_dir() else set()
        problems = [f"{name} is missing" for name in sorted(set(texts) - present)]
        problems += [f"{name} has no sample" for name in sorted(present - set(texts))]
        problems += [f"{name} differs from what the views compute" for name in sorted(set(texts) & present)
                     if (MEASURES / name).read_text(encoding="utf-8") != texts[name]]
        for problem in problems:
            print(f"make_measures: {problem}")
        if problems:
            print("Run python conformance/make_measures.py to rebuild them.")
            return 1
        print(f"The {len(texts)} files of expected/measures are up to date.")
        return 0
    MEASURES.mkdir(parents=True, exist_ok=True)
    for path in MEASURES.glob("*.json"):
        path.unlink()
    for name, text in texts.items():
        with open(MEASURES / name, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
    print(f"Wrote {len(texts)} files in {MEASURES.relative_to(ROOT).as_posix()}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
