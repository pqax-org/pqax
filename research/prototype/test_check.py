"""Makes changed copies of a PQAX file and runs the checker on each, to show which rules the checker enforces.

Usage: python test_check.py <file.pqax>

The file must have files, row groups, and column chunks. The copies go to a temporary folder.
"""
import json
import os
import subprocess
import sys
import tempfile
import zipfile

BOM = bytes([0xEF, 0xBB, 0xBF])
compact = lambda value: json.dumps(value, separators=(",", ":")).encode("utf-8")


def with_bom(d):
    return {"summary.json": BOM + compact(d["summary.json"])}


def short_array(d):
    d["files.json"]["files"]["fileSize"].pop()


def dangling_reference(d):
    d["files.json"]["files"]["table"][0] = 999


def repeated_id(d):
    d["summary.json"]["columns"]["id"][1] = d["summary.json"]["columns"]["id"][0]


def timestamp_in_seconds(d):
    d["files.json"]["files"]["modifiedAt"][0] = "2026-10-05T08:15:30Z"


def fraction(d):
    d["rowGroups.json"]["rowGroups"]["rowCount"][0] += 0.5


def wrong_row_count(d):
    d["manifest.json"]["parts"][-1]["tables"]["commits"] += 1


def rows_beyond_depth(d):
    d["summary.json"]["tables"]["detailDepth"][0] = "Summary"


def wrong_total(d):
    d["summary.json"]["tables"]["fileCount"][0] += 1


def missing_required_value(d):
    d["summary.json"]["columns"]["name"][0] = None


def extensions(d):
    """Everything that a later version or a producer may add. The file stays valid."""
    rows = len(d["summary.json"]["tables"]["id"])
    d["manifest.json"]["acme_note"] = "added by a producer"
    d["manifest.json"]["parts"][0]["acme_hint"] = 1
    d["manifest.json"]["parts"][0]["tables"]["acme_reviews"] = 1
    d["summary.json"]["acme_reviews"] = {"table": [1], "text": ["a table that this version does not define"]}
    d["summary.json"]["tables"]["acme_score"] = [1] * rows
    d["summary.json"]["tables"]["laterField"] = {"encoding": "a form of field that this version does not define"}
    d["summary.json"]["tables"]["fileSizes"][0]["ninetiethPercentile"] = 1
    return {"acme_readme.txt": b"an entry that the manifest does not list"}


def later_depth(d):
    d["summary.json"]["tables"]["detailDepth"] = ["Pages"] * len(d["summary.json"]["tables"]["id"])


CASES = [("unchanged", True, lambda d: None), ("extensions of a producer and of a later version", True, extensions),
         ("a depth that a later version adds", True, later_depth), ("byte order mark", False, with_bom),
         ("array shorter than the others", False, short_array), ("reference to a missing row", False, dangling_reference),
         ("repeated id", False, repeated_id), ("timestamp without milliseconds", False, timestamp_in_seconds),
         ("number with a fraction", False, fraction), ("row count of the manifest", False, wrong_row_count),
         ("rows beyond the depth of the table", False, rows_beyond_depth), ("total that differs from the detail", False, wrong_total),
         ("required field without a value", False, missing_required_value)]

checker = os.path.join(os.path.dirname(os.path.abspath(__file__)), "check_pqax.py")
with zipfile.ZipFile(sys.argv[1]) as archive:
    original = {info.filename: archive.read(info.filename) for info in archive.infolist()}
wrong = 0
with tempfile.TemporaryDirectory() as folder:
    for title, valid, change in CASES:
        documents = {name: json.loads(data) for name, data in original.items()}
        raw = change(documents) or {}
        path = os.path.join(folder, "case.pqax")
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, document in documents.items():
                archive.writestr(name, raw.pop(name, None) or compact(document))
            for name, data in raw.items():
                archive.writestr(name, data)
        result = subprocess.run([sys.executable, checker, path], capture_output=True, text=True)
        accepted = result.returncode == 0
        failures = [line.split(" x ", 1)[1] for line in result.stdout.splitlines() if " x " in line]
        wrong += accepted != valid
        print(f"{'ok   ' if accepted == valid else 'WRONG'} {title}: {'accepted' if accepted else 'rejected: ' + '; '.join(failures)}")
        if result.returncode not in (0, 1):
            print(result.stderr)
sys.exit(1 if wrong else 0)
