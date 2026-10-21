"""Builds a PQAX file from a dump of the probe that preceded the collector, following docs/serialization.md.

Usage: python build_pqax.py <output.pqax> <dump folder> [<dump folder> ...]
                            [--depth summary|files|rowGroups|columnChunks] [--rows-per-part N]
                            [--fill-footers] [--repeat N] [--layout columnar|rows|objects] [--dictionary]
                            [--format-version 0.1|0.2]

It needs only the Python standard library, which is the point: a PQAX file is a zip of JSON.

--format-version 0.1 writes the file as format 0.1, without the fields that 0.2 added: the unreferenced files and
the removal times of a table, and the last modification of a partition. The default is 0.2.

The last four options exist to measure sizes and alternatives. A collector would not have them.
--fill-footers gives the files whose footer was not read a copy of a footer that was read, with its
numbers changed at random, to estimate the size of a complete file. --repeat writes every table
several times. --layout and --dictionary write the tables in encodings that the draft rejects.
The output holds real names when the dump does, so it belongs in a folder that git ignores.
"""
import argparse
import collections
import copy
import datetime
import glob
import json
import os
import random
import zipfile

FORMAT_VERSION = "0.2"
FORMAT_VERSIONS = ["0.1", "0.2"]
DEPTHS = {"summary": "Summary", "files": "Files", "rowGroups": "Row groups", "columnChunks": "Column chunks"}
SUMMARY_TABLES = ["sources", "tables", "columns", "partitionSpecs", "partitionFields", "sortOrders", "sortFields",
                  "physicalSchemas", "physicalColumns", "writers", "operations", "issues",
                  "modelBindings", "tableBindings", "columnBindings"]
DETAIL_TABLES = ["partitions", "files", "rowGroups", "columnChunks", "deleteFiles", "commits"]
ROWS_PER_PART = 250_000


def iso(milliseconds):
    """A timestamp as the draft writes it: UTC, with exactly three decimals."""
    if milliseconds is None:
        return None
    seconds, fraction = divmod(int(milliseconds), 1000)
    moment = datetime.datetime.fromtimestamp(seconds, datetime.timezone.utc)
    return f"{moment:%Y-%m-%dT%H:%M:%S}.{fraction:03}Z"


def known(**fields):
    """An object of the model, without the fields that have no value."""
    return {name: value for name, value in fields.items() if value is not None} or None


def distribution(values):
    """The Distribution of the object model for a list of numbers."""
    values = sorted(values)
    if not values:
        return None
    n = len(values)
    return {"count": n, "sum": sum(values), "minimum": values[0], "firstQuartile": values[n // 4],
            "median": values[n // 2], "thirdQuartile": values[3 * n // 4], "maximum": values[-1]}


def estimated_footer(footer, rows, rng):
    """A copy of a footer with its numbers changed at random, so that an estimate does not compress too well."""
    footer = copy.deepcopy(footer)
    for group in footer["rowGroups"]:
        scale = rng.uniform(0.5, 1.5)
        if rows and len(footer["rowGroups"]) == 1:
            group["numRows"] = rows
        for i in range(len(group["codec"])):
            factor = scale * rng.uniform(0.8, 1.2)
            group["compressed"][i] = max(1, int(group["compressed"][i] * factor))
            group["uncompressed"][i] = max(1, int(group["uncompressed"][i] * factor))
            group["numValues"][i] = group["numRows"]
            if group["distinctCount"][i] is not None:
                group["distinctCount"][i] = max(1, int(group["distinctCount"][i] * factor))
            if group["nullCount"][i]:
                group["nullCount"][i] = int(group["nullCount"][i] * factor)
            if group["dictionaryPageOffset"][i] is not None:
                page = group["dataPageOffset"][i] - group["dictionaryPageOffset"][i]
                group["dataPageOffset"][i] = group["dictionaryPageOffset"][i] + max(1, int(page * factor))
        group["totalByteSize"] = sum(group["uncompressed"])
        group["totalCompressedSize"] = sum(group["compressed"])
    footer["footerSize"] = int(footer["footerSize"] * rng.uniform(0.9, 1.1))
    footer["numRows"] = sum(group["numRows"] for group in footer["rowGroups"])
    return footer


def type_text(delta_type):
    return delta_type if isinstance(delta_type, str) else json.dumps(delta_type, separators=(",", ":"))


class Table:
    """A table of the footprint: a list of rows, written as one array per field."""

    def __init__(self):
        self.rows = []

    def add(self, **row):
        self.rows.append(row)
        return len(self.rows)

    @staticmethod
    def encode(rows, layout, dictionary):
        names = []
        for row in rows:
            for name, value in row.items():
                if value is not None and name not in names:
                    names.append(name)
        if layout == "objects":
            return [{n: r[n] for n in names if r.get(n) is not None} for r in rows]
        if layout == "rows":
            return {"fields": names, "rows": [[r.get(n) for n in names] for r in rows]}
        encoded = {}
        for name in names:
            values = [r.get(name) for r in rows]
            if dictionary and len(values) > 100 and all(v is None or isinstance(v, (str, list)) for v in values):
                keys = {}
                indexes = [None if v is None else keys.setdefault(json.dumps(v), len(keys)) for v in values]
                if len(keys) <= len(values) // 10:
                    encoded[name] = {"dictionary": [json.loads(k) for k in keys], "indexes": indexes}
                    continue
            encoded[name] = values
        return encoded


def build(dump_tables, depth, fill_footers, repeat, version=FORMAT_VERSION):
    t = collections.defaultdict(Table)
    source_ids = {}
    level = list(DEPTHS).index(depth)
    since_0_2 = version != "0.1"          # the fields that format 0.2 added
    rng = random.Random(42)

    for dump in dump_tables * repeat:
        delta = dump.get("delta") or {}
        key = (dump["itemKind"], dump["item"])
        if key not in source_ids:
            source_ids[key] = t["sources"].add(id=len(source_ids) + 1, kind=f"OneLake {dump['itemKind'].lower()}", name=dump["item"])
        files = dump["files"]
        read = [f for f in files if f.get("footer")]
        if fill_footers and read:
            for index, file in enumerate(files):
                if not file.get("footer"):
                    file["footer"] = estimated_footer(read[index % len(read)]["footer"], file.get("numRecords"), rng)
            read = files
        table_id = len(t["tables"].rows) + 1

        # ---- columns ----
        fields = json.loads(delta["schemaString"])["fields"] if delta.get("schemaString") else []
        partition_columns = delta.get("partitionColumns") or []
        column_by_physical = {}
        column_rows = []
        for field in fields:
            metadata = field.get("metadata") or {}
            physical = metadata.get("delta.columnMapping.physicalName")
            row = dict(id=len(t["columns"].rows) + len(column_rows) + 1, table=table_id, fieldId=metadata.get("delta.columnMapping.id"),
                       name=field["name"], dataType=type_text(field["type"]), isNullable=field.get("nullable"),
                       physicalName=physical if physical != field["name"] else None,
                       isPartitionColumn=field["name"] in partition_columns, description=metadata.get("comment"))
            column_rows.append(row)
            column_by_physical[physical or field["name"]] = row

        # ---- physical schemas and their columns ----
        schema_ids = []
        physical_rows = {}
        for schema_index, schema in enumerate(dump.get("schemas", [])):
            schema_id = t["physicalSchemas"].add(id=len(t["physicalSchemas"].rows) + 1, table=table_id,
                                                 fileCount=sum(1 for f in read if f["footer"]["schema"] == schema_index))
            schema_ids.append(schema_id)
            for ordinal, leaf in enumerate(schema["columns"]):
                column = column_by_physical.get(leaf["path"][0])
                row = dict(id=len(t["physicalColumns"].rows) + 1, physicalSchema=schema_id, ordinal=ordinal, path=leaf["path"],
                           physicalType=leaf["type"], typeLength=leaf.get("typeLength"), logicalType=leaf.get("logical") or leaf.get("converted"),
                           repetition=leaf.get("repetition"), fieldId=leaf.get("fieldId"), column=column["id"] if column else None,
                           compressedSize=0, uncompressedSize=0, chunkCount=0)
                t["physicalColumns"].rows.append(row)
                physical_rows[(schema_index, ordinal)] = (row, column)

        # ---- partitions ----
        partition_ids = {}
        partition_rows = {}
        spec_id = None
        if partition_columns:
            spec_id = t["partitionSpecs"].add(id=len(t["partitionSpecs"].rows) + 1, table=table_id, isDefault=True)
            for position, name in enumerate(partition_columns):
                source = next((c["id"] for c in column_rows if c["name"] == name), None)
                t["partitionFields"].add(partitionSpec=spec_id, position=position, name=name,
                                         sourceColumns=[source] if source else None, transform="identity")

        # ---- files, row groups, column chunks ----
        writers = {}
        row_group_rows = []
        unknown_nulls = set()
        file_rows_known = True
        stale = 0
        for file in files:
            footer = file.get("footer")
            tags = {k.lower(): v for k, v in (file.get("tags") or {}).items()}
            keys = {kv["key"].lower(): kv.get("value") for kv in (footer or {}).get("keyValues", [])}
            vordered = True if tags.get("vorder") == "true" else None
            if vordered is None and footer:
                vordered = any(k.endswith("vorder.enabled") and v == "true" for k, v in keys.items())
            rows = file.get("numRecords")
            if rows is None and footer:
                rows = footer["numRows"]
            file_rows_known = file_rows_known and rows is not None
            partition = None
            if partition_columns and file.get("partition") is not None:
                signature = tuple(file["partition"].get(name) for name in partition_columns)
                if signature not in partition_ids:
                    partition_ids[signature] = len(t["partitions"].rows) + len(partition_rows) + 1
                    partition_rows[signature] = dict(id=partition_ids[signature], table=table_id, partitionSpec=spec_id,
                                                     fileCount=0, fileSize=0, rowCount=0, deletedRowCount=0)
                partition = partition_ids[signature]
                totals = partition_rows[signature]
                totals["fileCount"] += 1
                totals["fileSize"] += file["size"]
                totals["rowCount"] += rows or 0
                totals["deletedRowCount"] += file.get("deletedRows") or 0
                modified = iso(file.get("modificationTime"))
                if since_0_2 and modified:
                    totals["lastModifiedAt"] = max(filter(None, [totals.get("lastModifiedAt"), modified]))

            file_row = dict(id=len(t["files"].rows) + 1, table=table_id, path=file["path"], fileSize=file["size"],
                            modifiedAt=iso(file.get("modificationTime")), rowCount=rows,
                            deletedRowCount=file.get("deletedRows") or 0, deletionVectorSize=file.get("deletionVectorSize"),
                            partition=partition, clusteringProvider=file.get("clusteringProvider"), isVOrdered=vordered)
            file["_vordered"] = vordered
            if footer:
                level_text = next((v for k, v in keys.items() if k.endswith("vorder.level")), None)
                writer_key = (footer.get("createdBy"), vordered, level_text)
                writer = writers.setdefault(writer_key, dict(id=len(t["writers"].rows) + len(writers) + 1, table=table_id,
                                                             createdBy=writer_key[0], isVOrdered=vordered, vOrderLevel=level_text,
                                                             fileCount=0, fileSize=0))
                writer["fileCount"] += 1
                writer["fileSize"] += file["size"]
                compressed = uncompressed = 0
                for ordinal, group in enumerate(footer["rowGroups"]):
                    group_id = len(t["rowGroups"].rows) + len(row_group_rows) + 1
                    row_group_rows.append(dict(id=group_id, file=file_row["id"], ordinal=ordinal, rowCount=group["numRows"],
                                               uncompressedSize=group["totalByteSize"],
                                               compressedSize=group.get("totalCompressedSize") or sum(group["compressed"])))
                    for position in range(len(group["codec"])):
                        physical, column = physical_rows[(footer["schema"], position)]
                        size = group["compressed"][position]
                        compressed += size
                        uncompressed += group["uncompressed"][position]
                        physical["compressedSize"] += size
                        physical["uncompressedSize"] += group["uncompressed"][position]
                        physical["chunkCount"] += 1
                        offset = group["dictionaryPageOffset"][position]
                        pages = [p.split(":") for p in (group["encodingStats"][position] or "").split(";") if p]
                        data_pages = [p for p in pages if p[0].startswith("DATA_PAGE")]
                        dictionary_pages = sum(int(p[2]) for p in data_pages if "DICTIONARY" in p[1])
                        chunk = dict(rowGroup=group_id, physicalColumn=physical["id"], codec=group["codec"][position],
                                     encodings=sorted(group["encodings"][position].split(",")), valueCount=group["numValues"][position],
                                     nullCount=group["nullCount"][position], compressedSize=size,
                                     uncompressedSize=group["uncompressed"][position], hasDictionaryPage=offset is not None,
                                     dictionaryPageSize=group["dataPageOffset"][position] - offset if offset is not None else None,
                                     dataPageCount=sum(int(p[2]) for p in data_pages) if pages else None,
                                     dictionaryEncodedPageCount=dictionary_pages if pages else None,
                                     distinctCount=group["distinctCount"][position],
                                     bloomFilterSize=(group.get("bloomFilterLength") or [None] * len(group["codec"]))[position] or None)
                        if level >= 3:
                            t["columnChunks"].rows.append(chunk)
                        if column is None:
                            stale += size
                            continue
                        if chunk["nullCount"] is None:
                            unknown_nulls.add(column["id"])
                        fallback = offset is not None and bool(pages) and dictionary_pages < chunk["dataPageCount"]
                        for name, value in (("compressedSize", size), ("uncompressedSize", chunk["uncompressedSize"]),
                                            ("valueCount", chunk["valueCount"]), ("nullCount", chunk["nullCount"] or 0), ("chunkCount", 1),
                                            ("dictionaryChunkCount", int(offset is not None)), ("fallbackChunkCount", int(fallback)),
                                            ("dictionaryPageSize", chunk["dictionaryPageSize"] or 0)):
                            column[name] = column.get(name, 0) + value
                        column["codecs"] = sorted(set(column.get("codecs", [])) | {chunk["codec"]})
                        column["encodings"] = sorted(set(column.get("encodings", [])) | set(chunk["encodings"]))
                        if chunk["distinctCount"] is not None:
                            column["largestChunkDistinctCount"] = max(column.get("largestChunkDistinctCount", 0), chunk["distinctCount"])
                file_row.update(footerSize=footer["footerSize"], physicalSchema=schema_ids[footer["schema"]], writer=writer["id"],
                                rowGroupCount=len(footer["rowGroups"]), compressedSize=compressed, uncompressedSize=uncompressed)
            if level >= 1:
                t["files"].rows.append(file_row)
        if level >= 2:
            t["rowGroups"].rows.extend(row_group_rows)
        if level >= 1:
            t["partitions"].rows.extend(partition_rows.values())
        t["writers"].rows.extend(writers.values())

        stored = {row["column"] for row, column in physical_rows.values() if column}
        for column in column_rows:
            if column["id"] in unknown_nulls:
                column["nullCount"] = None
            if read and not column["isPartitionColumn"]:
                with_column = {schema_ids[s] for (s, _), (row, c) in physical_rows.items() if c is column}
                column["fileCountWithoutColumn"] = sum(1 for f in read if schema_ids[f["footer"]["schema"]] not in with_column)
        t["columns"].rows.extend(column_rows)

        # ---- history ----
        operations = {}
        times = []
        for commit in dump.get("commits", []):
            when = iso(commit.get("timestamp"))
            if when:
                times.append(when)
            if level >= 1:
                t["commits"].add(table=table_id, commitId=str(commit["version"]), timestamp=when, operation=commit.get("operation"),
                                 engine=commit.get("engine"), isBlindAppend=commit.get("isBlindAppend"), metrics=commit.get("metrics"))
            if commit.get("operation"):
                operation = operations.setdefault(commit["operation"], dict(table=table_id, name=commit["operation"], commitCount=0, lastAt=None))
                operation["commitCount"] += 1
                operation["lastAt"] = max(filter(None, [operation["lastAt"], when]), default=None)
        t["operations"].rows.extend(operations.values())
        for issue in dump.get("issues", []):
            code, _, message = issue.partition(": ")
            t["issues"].add(table=table_id, code=code, message=message)

        # ---- the table and its totals ----
        domains = delta.get("domains") or {}
        clustering = json.loads(domains.get("delta.clustering", "{}")).get("clusteringColumns", [])
        groups = [g for f in read for g in f["footer"]["rowGroups"]]
        version = delta.get("version")
        checkpoint = delta.get("checkpointVersion")
        file_rows = [f.get("numRecords") if f.get("numRecords") is not None else (f.get("footer") or {}).get("numRows") for f in files]
        t["tables"].add(
            id=table_id, source=source_ids[key], namespace=dump["namespace"], name=dump["name"], format="Delta",
            tableId=delta.get("tableId"), createdAt=iso(delta.get("createdTime")), properties=delta.get("configuration"),
            snapshotId=str(version) if version is not None else None,
            snapshotTimestamp=next((iso(c.get("timestamp")) for c in dump.get("commits", []) if c.get("version") == version), None),
            delta=known(minReaderVersion=delta.get("minReaderVersion"), minWriterVersion=delta.get("minWriterVersion"),
                        readerFeatures=delta.get("readerFeatures"), writerFeatures=delta.get("writerFeatures"),
                        checkpointVersion=checkpoint,
                        commitsAfterCheckpoint=version - checkpoint if None not in (version, checkpoint) else None,
                        domains=sorted(domains)),
            clusteringColumns=[column_by_physical[path[0]]["id"] for path in clustering if path and path[0] in column_by_physical] or None,
            collectedLevel=2 if read else 1, detailDepth=DEPTHS[depth],
            fileCount=len(files), fileSize=sum(f["size"] for f in files),
            rowCount=sum(file_rows) if file_rows_known else None,
            deletedRowCount=sum(f.get("deletedRows") or 0 for f in files),
            lastModifiedAt=iso(max((f.get("modificationTime") or 0 for f in files), default=None) or None),
            vOrderedFileCount=sum(1 for f in files if f.get("_vordered")),
            partitionCount=len(partition_ids) if partition_columns else None,
            commitCount=delta.get("commitFileCount"), commitsRead=len(dump.get("commits", [])), oldestCommitAt=min(times, default=None),
            nonLiveFileCount=delta.get("removedFileCount"), nonLiveFileSize=delta.get("removedFileSize"),
            unreferencedFileCount=dump.get("unreferencedFileCount") if since_0_2 else None,
            unreferencedFileSize=dump.get("unreferencedFileSize") if since_0_2 else None,
            nonLiveOldestRemovedAt=iso(delta.get("removedOldestTimestamp")) if since_0_2 else None,
            nonLiveNewestRemovedAt=iso(delta.get("removedNewestTimestamp")) if since_0_2 else None,
            metadataFileCount=delta.get("logFileCount"), metadataSize=delta.get("logSize"),
            footerCount=len(read), rowGroupCount=len(groups) if read else None,
            compressedSize=sum(sum(g["compressed"]) for g in groups) if read else None,
            uncompressedSize=sum(sum(g["uncompressed"]) for g in groups) if read else None,
            footerSize=sum(f["footer"]["footerSize"] for f in read) if read else None,
            staleColumnSize=stale if read else None, columnTotalsSource="Footers" if read else None,
            fileSizes=distribution([f["size"] for f in files]), rowGroupRowCounts=distribution([g["numRows"] for g in groups]),
            partitionSizes=distribution([p["fileSize"] for p in partition_rows.values()]))
    return t


def write(path, tables, depth, layout, dictionary, rows_per_part, version=FORMAT_VERSION):
    dump = lambda value: json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    plan = [("summary.json", [(name, tables[name].rows) for name in SUMMARY_TABLES if tables[name].rows])]
    for name in DETAIL_TABLES:
        rows = tables[name].rows
        for start in range(0, len(rows), rows_per_part):
            part = f"{name}.json" if len(rows) <= rows_per_part else f"{name}.{start // rows_per_part + 1}.json"
            plan.append((part, [(name, rows[start:start + rows_per_part])]))
    manifest = {"format": "pqax", "formatVersion": version,
                "collection": {"collectorApp": "pqax-prototype", "collectorAppVersion": version, "level": 2,
                               "includesDataValues": False, "includesPartitionValues": False},
                "parts": [{"name": part, "tables": {name: len(rows) for name, rows in content}} for part, content in plan]}
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        archive.writestr("manifest.json", dump(manifest))
        for part, content in plan:
            archive.writestr(part, dump({name: Table.encode(rows, layout, dictionary) for name, rows in content}))
    print(f"{os.path.basename(path)}: depth {depth}, layout {layout}{', dictionary' if dictionary else ''}, {os.path.getsize(path):,} bytes")
    with zipfile.ZipFile(path) as archive:
        rows = {part: sum(len(r) for _, r in content) for part, content in plan}
        for info in archive.infolist():
            print(f"   {info.filename:22} {info.file_size:>12,} raw {info.compress_size:>11,} in the zip {rows.get(info.filename, ''):>10} rows")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("output")
    parser.add_argument("dumps", nargs="+")
    parser.add_argument("--depth", choices=list(DEPTHS), default="columnChunks")
    parser.add_argument("--rows-per-part", type=int, default=ROWS_PER_PART)
    parser.add_argument("--fill-footers", action="store_true")
    parser.add_argument("--repeat", type=int, default=1)
    parser.add_argument("--layout", choices=["columnar", "rows", "objects"], default="columnar")
    parser.add_argument("--dictionary", action="store_true")
    parser.add_argument("--format-version", choices=FORMAT_VERSIONS, default=FORMAT_VERSION)
    arguments = parser.parse_args()

    loaded = {}
    for folder in arguments.dumps:
        for file_path in glob.glob(os.path.join(folder, "[LW]*.json")):
            with open(file_path, encoding="utf-8") as handle:
                table = json.load(handle)
            loaded[table["id"]] = table
    footprint = build([loaded[k] for k in sorted(loaded)], arguments.depth, arguments.fill_footers, arguments.repeat,
                      arguments.format_version)
    write(arguments.output, footprint, arguments.depth, arguments.layout, arguments.dictionary, arguments.rows_per_part,
          arguments.format_version)
