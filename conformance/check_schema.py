"""Checks the samples of the kit against the JSON Schema in schema/.

Usage: python check_schema.py

It needs the jsonschema package (pip install jsonschema), which is a development dependency, not a dependency of
the kit. Every valid sample must pass. An invalid sample must fail when the rule that it breaks is one that JSON
Schema can express (SCHEMA_CODES), and its result is only reported otherwise. The exit code is 1 when a check
fails.
"""
import io
import json
import os
import pathlib
import sys
import zipfile

from jsonschema import Draft202012Validator
from referencing import Registry, Resource

KIT = os.path.dirname(os.path.abspath(__file__))
SCHEMA_CODES = {"json.topLevel", "json.integer", "json.integerRange", "json.timestamp", "json.type", "manifest.required", "manifest.format",
                "manifest.formatVersion", "manifest.versionUnknown", "manifest.firstPart", "container.partName", "part.kind",
                "part.singleTable", "table.required", "table.idUnique"}


def validators():
    registry = Registry()
    uris = {}
    for name in ("manifest", "part"):
        path = pathlib.Path(KIT, "schema", f"{name}.schema.json")
        schema = json.loads(path.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        uris[name] = path.as_uri()
        registry = registry.with_resource(uris[name], Resource.from_contents(schema))
    make = lambda reference: Draft202012Validator({"$ref": reference}, registry=registry)
    return {"manifest": make(uris["manifest"]), "summary": make(uris["part"] + "#/$defs/summaryPart"),
            "detail": make(uris["part"] + "#/$defs/detailPart")}


def footprint(path):
    """The bytes of the PQAX file: the file itself, or the part Footprint.pqax of a VPAX."""
    with open(path, "rb") as handle:
        data = handle.read()
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        if "manifest.json" not in archive.namelist() and "Footprint.pqax" in archive.namelist():
            return archive.read("Footprint.pqax")
    return data


def describe(error):
    where = "/".join(map(str, error.absolute_path)) or "top level"
    if error.validator == "not":
        return f"{where}: holds a table that this kind of part does not allow"
    if error.validator in ("maxProperties", "minProperties"):
        return f"{where}: holds {len(error.instance)} members, and a part other than summary.json holds one table"
    if error.validator == "type" and not error.absolute_path:
        return f"{where}: is not an object"
    return f"{where}: {error.message[:120]}"


def schema_errors(path, check):
    """Every error of the schema in the manifest and in the parts that it lists, as text."""
    errors = []
    with zipfile.ZipFile(io.BytesIO(footprint(path))) as archive:
        names = archive.namelist()
        if "manifest.json" not in names:
            return ["no manifest.json, nothing to validate"]
        load = lambda name: json.loads(archive.read(name).decode("utf-8-sig"))
        manifest = load("manifest.json")
        errors += [f"manifest.json: {describe(e)}" for e in check["manifest"].iter_errors(manifest)]
        for part in manifest.get("parts", []) if isinstance(manifest.get("parts"), list) else []:
            name = part.get("name") if isinstance(part, dict) else None
            if name not in names:
                continue
            try:
                content = load(name)
            except ValueError as error:
                errors.append(f"{name}: not JSON ({error})")
                continue
            validator = check["summary"] if name == "summary.json" else check["detail"]
            errors += [f"{name}: {describe(e)}" for e in validator.iter_errors(content)]
    return errors


def main():
    check = validators()
    failed = 0
    samples = [path for path in pathlib.Path(KIT, "samples").iterdir() if path.suffix in (".pqax", ".vpax")]
    for path in sorted(samples):
        errors = schema_errors(path, check)
        failed += bool(errors)
        print(f"{'ok  ' if not errors else 'FAIL'} valid   {path.name}")
        for error in errors:
            print(f"        {error}")
    with open(os.path.join(KIT, "samples", "invalid", "expected.json"), encoding="utf-8") as handle:
        expected = json.load(handle)
    for name, entry in expected.items():
        errors = schema_errors(pathlib.Path(KIT, "samples", "invalid", name), check)
        must_fail = bool(SCHEMA_CODES & set(entry["codes"]))
        wrong = must_fail and not errors
        failed += wrong
        verdict = "rejected" if errors else "accepted"
        note = "" if must_fail else " (the rule is not one that JSON Schema can express)"
        print(f"{'FAIL' if wrong else 'ok  '} invalid {name}: {verdict}{note}")
        if errors and must_fail:
            print(f"        {errors[0]}")
    print(f"{failed} failed")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
