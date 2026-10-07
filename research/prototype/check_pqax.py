"""Checks a PQAX file against the rules of docs/serialization.md.

Usage: python check_pqax.py <file.pqax>

It needs only the Python standard library. It prints table names of the format, counts, and identifiers, never a name
that comes from the data.
"""
import collections
import json
import re
import sys
import zipfile

SUMMARY_TABLES = {"sources", "tables", "columns", "partitionSpecs", "partitionFields", "sortOrders", "sortFields", "physicalSchemas",
                  "physicalColumns", "writers", "operations", "issues", "modelBindings", "tableBindings", "columnBindings"}
DETAIL_TABLES = {"partitions", "files", "rowGroups", "columnChunks", "deleteFiles", "commits"}
REQUIRED = {"sources": ["id", "kind"], "tables": ["id", "name", "format", "detailDepth"], "columns": ["id", "table", "name", "dataType"],
            "partitionSpecs": ["id", "table"], "partitionFields": ["partitionSpec", "position"], "sortOrders": ["id", "table"],
            "sortFields": ["sortOrder", "position"], "physicalSchemas": ["id", "table"],
            "physicalColumns": ["id", "physicalSchema", "ordinal", "path", "physicalType"], "writers": ["id", "table"],
            "operations": ["table", "name"], "issues": ["code"], "modelBindings": ["id", "modelName"],
            "tableBindings": ["id", "modelBinding", "modelTable", "table"], "columnBindings": ["tableBinding", "modelColumn", "column"],
            "partitions": ["id", "table"], "files": ["id", "table"], "rowGroups": ["id", "file", "ordinal", "rowCount"],
            "columnChunks": ["rowGroup", "physicalColumn", "compressedSize", "uncompressedSize"],
            "deleteFiles": ["id", "table", "kind"], "commits": ["table", "commitId"]}
REFERENCES = [("tables", "source", "sources"), ("tables", "clusteringColumns", "columns"), ("columns", "table", "tables"),
              ("partitionSpecs", "table", "tables"), ("partitionFields", "partitionSpec", "partitionSpecs"),
              ("partitionFields", "sourceColumns", "columns"), ("sortOrders", "table", "tables"), ("sortFields", "sortOrder", "sortOrders"),
              ("sortFields", "sourceColumn", "columns"), ("physicalSchemas", "table", "tables"),
              ("physicalColumns", "physicalSchema", "physicalSchemas"), ("physicalColumns", "column", "columns"),
              ("writers", "table", "tables"), ("operations", "table", "tables"), ("issues", "table", "tables"),
              ("tableBindings", "modelBinding", "modelBindings"), ("tableBindings", "table", "tables"),
              ("columnBindings", "tableBinding", "tableBindings"), ("columnBindings", "column", "columns"),
              ("partitions", "table", "tables"), ("partitions", "partitionSpec", "partitionSpecs"), ("files", "table", "tables"),
              ("files", "partition", "partitions"), ("files", "sortOrder", "sortOrders"), ("files", "physicalSchema", "physicalSchemas"),
              ("files", "writer", "writers"), ("rowGroups", "file", "files"), ("columnChunks", "rowGroup", "rowGroups"),
              ("columnChunks", "physicalColumn", "physicalColumns"), ("deleteFiles", "table", "tables"),
              ("deleteFiles", "referencedFile", "files"), ("deleteFiles", "equalityColumns", "columns"),
              ("deleteFiles", "partition", "partitions"), ("commits", "table", "tables")]
TIMESTAMPS = [("tables", "createdAt"), ("tables", "snapshotTimestamp"), ("tables", "lastModifiedAt"), ("tables", "oldestCommitAt"),
              ("operations", "lastAt"), ("files", "modifiedAt"), ("commits", "timestamp")]
TIMESTAMP = re.compile("[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}[.][0-9]{3}Z")
DEPTHS = ["Summary", "Files", "Row groups", "Column chunks"]
LARGEST_INTEGER = 2 ** 53 - 1
ROWS_PER_PART = 250_000
PART_NAME_CHARACTERS = set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_")

problems = collections.Counter()
checks = 0


def check(condition, message):
    global checks
    checks += 1
    if not condition:
        problems[message] += 1


def unique(pairs):
    names = [name for name, _ in pairs]
    check(len(names) == len(set(names)), "an object repeats a member name")
    return dict(pairs)


def load(archive, name):
    data = archive.read(name)
    check(data[:3] != bytes([0xEF, 0xBB, 0xBF]), f"{name} starts with a byte order mark")
    return json.loads(data.decode("utf-8-sig"), object_pairs_hook=unique)


def numbers(value):
    """Every number inside a value."""
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        yield value
    elif isinstance(value, list):
        for item in value:
            yield from numbers(item)
    elif isinstance(value, dict):
        for item in value.values():
            yield from numbers(item)


archive = zipfile.ZipFile(sys.argv[1])
entries = archive.infolist()

# ---- the container ----
check(len(entries) < 0xFFFF and all(max(e.file_size, e.compress_size, e.header_offset) < 0xFFFFFFFF for e in entries), "the file needs ZIP64")
for entry in entries:
    check(entry.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED), f"{entry.filename} is neither stored nor deflated")
    check(not entry.flag_bits & 1, f"{entry.filename} is encrypted")

# ---- the manifest and the parts ----
manifest = load(archive, "manifest.json")
check(manifest.get("format") == "pqax", "the manifest does not say that the format is pqax")
check(re.fullmatch("[0-9]+[.][0-9]+", str(manifest.get("formatVersion"))) is not None, "formatVersion is not two numbers")
check(manifest["parts"][0]["name"] == "summary.json", "the first part is not summary.json")
check(len({part["name"] for part in manifest["parts"]}) == len(manifest["parts"]), "a part is listed twice")
check(all(set(part["name"]) <= PART_NAME_CHARACTERS for part in manifest["parts"]), "the name of a part has a character that the format does not allow")
check(len({entry.filename.lower() for entry in entries}) == len(entries), "two entry names differ only in case")

tables = collections.defaultdict(dict)
rows = collections.Counter()
for position, part in enumerate(manifest["parts"]):
    content = load(archive, part["name"])
    check(set(content) == set(part["tables"]), f"{part['name']} does not hold the tables that the manifest states")
    check(position == 0 or len(content) == 1, f"{part['name']} is a detail part with more than one table")
    check(position == 0 or sum(part["tables"].values()) <= ROWS_PER_PART, f"{part['name']} is a detail part with more than {ROWS_PER_PART:,} rows")
    for name, table in content.items():
        expected = part["tables"].get(name, 0)
        if name in SUMMARY_TABLES | DETAIL_TABLES:
            check((name in SUMMARY_TABLES) == (position == 0), f"{name} is in the wrong kind of part")
        arrays = {field: values for field, values in table.items() if isinstance(values, list)}
        check(all(len(values) == expected for values in arrays.values()), f"{part['name']}: an array of {name} has not the rows of the manifest")
        for field, values in arrays.items():
            tables[name].setdefault(field, [None] * rows[name]).extend(values)
        rows[name] += expected
        for values in tables[name].values():
            values.extend([None] * (rows[name] - len(values)))

# ---- values ----
for name, fields in REQUIRED.items():
    for field in fields if name in tables else []:
        check(field in tables[name] and None not in tables[name][field], f"{name}.{field} is required and has no value in some row")
for name, table in tables.items():
    for field, values in table.items():
        found = list(numbers(values))
        check(all(type(v) is int for v in found), f"{name}.{field} has a number that is not an integer")
        check(all(abs(v) <= LARGEST_INTEGER for v in found), f"{name}.{field} has an integer beyond 2^53 - 1")
for name, field in TIMESTAMPS:
    values = [v for v in tables.get(name, {}).get(field, []) if v is not None]
    check(all(isinstance(v, str) and TIMESTAMP.fullmatch(v) for v in values), f"{name}.{field} has a timestamp in another form")

# ---- identifiers and references ----
ids = {}
for name, table in tables.items():
    if "id" in table:
        ids[name] = set(table["id"])
        check(all(type(v) is int and v > 0 for v in table["id"]) and len(ids[name]) == rows[name], f"{name}.id is not a distinct positive integer")
for name, field, target in REFERENCES:
    values = tables.get(name, {}).get(field, [])
    flat = [v for value in values for v in (value if isinstance(value, list) else [value]) if v is not None]
    check(all(v in ids.get(target, ()) for v in flat), f"{name}.{field} refers to a row that {target} does not have")

# ---- the depth of each table, and its totals against the detail ----
t = tables["tables"]
get = lambda name, field: tables.get(name, {}).get(field, [None] * rows[name])
file_table = dict(zip(get("files", "id"), get("files", "table")))
group_table = {group: file_table[file] for group, file in zip(get("rowGroups", "id"), get("rowGroups", "file"))}
schema_table = dict(zip(get("physicalSchemas", "id"), get("physicalSchemas", "table")))
physical_table = {p: schema_table[s] for p, s in zip(get("physicalColumns", "id"), get("physicalColumns", "physicalSchema"))}
physical_column = dict(zip(get("physicalColumns", "id"), get("physicalColumns", "column")))
files = collections.Counter(file_table.values())
file_size = collections.Counter()
for table, size in zip(get("files", "table"), get("files", "fileSize")):
    file_size[table] += size or 0
groups = collections.Counter(group_table.values())
chunks = collections.Counter()
chunk_size = collections.Counter()
column_size = collections.Counter()
for physical, size in zip(get("columnChunks", "physicalColumn"), get("columnChunks", "compressedSize")):
    chunks[physical_table[physical]] += 1
    chunk_size[physical_table[physical]] += size
    column_size[physical_column[physical]] += size
unknown_depths = set()
for i, table in enumerate(t.get("id", [])):
    depth = t["detailDepth"][i]
    level = DEPTHS.index(depth) if depth in DEPTHS else len(DEPTHS)
    unknown_depths.update([] if depth in DEPTHS else [depth])
    total = lambda field: t.get(field, [None] * rows["tables"])[i]
    if level >= 1:
        check(files[table] == total("fileCount"), "fileCount differs from the files kept")
        check(file_size[table] == total("fileSize"), "fileSize differs from the sum over the files kept")
    else:
        check(files[table] == 0, "a table at the Summary depth has files")
    if level >= 2:
        check(total("rowGroupCount") in (None, groups[table]), "rowGroupCount differs from the row groups kept")
    else:
        check(groups[table] == 0, "a table has row groups beyond its depth")
    if level >= 3:
        check(total("compressedSize") in (None, chunk_size[table]), "compressedSize differs from the sum over the column chunks kept")
    else:
        check(chunks[table] == 0, "a table has column chunks beyond its depth")
if rows["columnChunks"]:
    deepest = {table for table, depth in zip(t["id"], t["detailDepth"]) if depth not in DEPTHS[:3]}
    for column, table, size in zip(get("columns", "id"), get("columns", "table"), get("columns", "compressedSize")):
        if table in deepest and size is not None:
            check(size == column_size[column], "the compressedSize of a column differs from the sum over its column chunks")

print(f"{sys.argv[1]}: format {manifest.get('format')} {manifest.get('formatVersion')}, {len(manifest['parts'])} parts")
print("   rows: " + ", ".join(f"{name} {count:,}" for name, count in rows.items()))
if unknown_depths:
    print(f"   depths that this checker does not know, read as deeper than Column chunks: {len(unknown_depths)}")
print(f"   {checks:,} checks, {sum(problems.values()):,} failed")
for message, count in problems.most_common():
    print(f"   {count:6,} x {message}")
sys.exit(1 if problems else 0)
