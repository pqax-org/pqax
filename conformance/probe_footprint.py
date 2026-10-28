"""The footprint of the fixture, built from its probe dump with pqax.Builder, for make_kit.py.

footprint(dumps, depth, collection, version) maps the fields of the probe dump to the tables and fields of the format,
as the section The probe dump of README.md describes them, and adds the rows to a pqax.Builder, the writer of the
Python package. Every row brings its id, numbered from 1 in the order of the tables of the dump. make_kit.py then
takes each table from the builder with Builder.table and lays the samples out itself, part by part.

The mapping comes from build_pqax.py, the builder of the prototype in research/prototype, without the options that
only measured sizes. The kit used to import that builder, the one exception to the rule that it depends on nothing
outside conformance/, src/, and tests/.
"""
import collections
import datetime
import json

DEPTHS = {"summary": "Summary", "files": "Files", "rowGroups": "Row groups", "columnChunks": "Column chunks"}


def iso(milliseconds):
    """A timestamp of the format: UTC, with exactly three decimals."""
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


def type_text(delta_type):
    return delta_type if isinstance(delta_type, str) else json.dumps(delta_type, separators=(",", ":"))


def rows_of(dump_tables, depth, version):
    """The rows of each table of the footprint of the dumped tables at a depth, as dictionaries, in the order of the
    file. version 0.1 leaves out the fields that format 0.2 added. A row is complete only when every table is done,
    since the totals of a column, a physical column, a partition, or a writer grow with the files that follow."""
    t = collections.defaultdict(list)
    source_ids = {}
    level = list(DEPTHS).index(depth)
    since_0_2 = version != "0.1"          # the fields that format 0.2 added

    for dump in dump_tables:
        delta = dump.get("delta") or {}
        key = (dump["itemKind"], dump["item"])
        if key not in source_ids:
            source_ids[key] = len(source_ids) + 1
            t["sources"].append(dict(id=source_ids[key], kind=f"OneLake {dump['itemKind'].lower()}", name=dump["item"]))
        files = dump["files"]
        read = [f for f in files if f.get("footer")]
        table_id = len(t["tables"]) + 1

        # ---- columns ----
        fields = json.loads(delta["schemaString"])["fields"] if delta.get("schemaString") else []
        partition_columns = delta.get("partitionColumns") or []
        column_by_physical = {}
        column_rows = []
        for field in fields:
            metadata = field.get("metadata") or {}
            physical = metadata.get("delta.columnMapping.physicalName")
            row = dict(id=len(t["columns"]) + len(column_rows) + 1, table=table_id,
                       fieldId=metadata.get("delta.columnMapping.id"), name=field["name"],
                       dataType=type_text(field["type"]), isNullable=field.get("nullable"),
                       physicalName=physical if physical != field["name"] else None,
                       isPartitionColumn=field["name"] in partition_columns, description=metadata.get("comment"))
            column_rows.append(row)
            column_by_physical[physical or field["name"]] = row

        # ---- physical schemas and their columns ----
        schema_ids = []
        physical_rows = {}
        for schema_index, schema in enumerate(dump.get("schemas", [])):
            schema_id = len(t["physicalSchemas"]) + 1
            t["physicalSchemas"].append(dict(id=schema_id, table=table_id,
                                             fileCount=sum(1 for f in read if f["footer"]["schema"] == schema_index)))
            schema_ids.append(schema_id)
            for ordinal, leaf in enumerate(schema["columns"]):
                column = column_by_physical.get(leaf["path"][0])
                row = dict(id=len(t["physicalColumns"]) + 1, physicalSchema=schema_id, ordinal=ordinal,
                           path=leaf["path"], physicalType=leaf["type"], typeLength=leaf.get("typeLength"),
                           logicalType=leaf.get("logical") or leaf.get("converted"), repetition=leaf.get("repetition"),
                           fieldId=leaf.get("fieldId"), column=column["id"] if column else None, compressedSize=0,
                           uncompressedSize=0, chunkCount=0)
                t["physicalColumns"].append(row)
                physical_rows[(schema_index, ordinal)] = (row, column)

        # ---- partitions ----
        partition_ids = {}
        partition_rows = {}
        spec_id = None
        if partition_columns:
            spec_id = len(t["partitionSpecs"]) + 1
            t["partitionSpecs"].append(dict(id=spec_id, table=table_id, isDefault=True))
            for position, name in enumerate(partition_columns):
                source = next((c["id"] for c in column_rows if c["name"] == name), None)
                t["partitionFields"].append(dict(partitionSpec=spec_id, position=position, name=name,
                                                 sourceColumns=[source] if source else None, transform="identity"))

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
                    partition_ids[signature] = len(t["partitions"]) + len(partition_rows) + 1
                    partition_rows[signature] = dict(id=partition_ids[signature], table=table_id,
                                                     partitionSpec=spec_id, fileCount=0, fileSize=0, rowCount=0,
                                                     deletedRowCount=0)
                partition = partition_ids[signature]
                totals = partition_rows[signature]
                totals["fileCount"] += 1
                totals["fileSize"] += file["size"]
                totals["rowCount"] += rows or 0
                totals["deletedRowCount"] += file.get("deletedRows") or 0
                modified = iso(file.get("modificationTime"))
                if since_0_2 and modified:
                    totals["lastModifiedAt"] = max(filter(None, [totals.get("lastModifiedAt"), modified]))

            file_row = dict(id=len(t["files"]) + 1, table=table_id, path=file["path"], fileSize=file["size"],
                            modifiedAt=iso(file.get("modificationTime")), rowCount=rows,
                            deletedRowCount=file.get("deletedRows") or 0,
                            deletionVectorSize=file.get("deletionVectorSize"), partition=partition,
                            clusteringProvider=file.get("clusteringProvider"), isVOrdered=vordered)
            if footer:
                level_text = next((v for k, v in keys.items() if k.endswith("vorder.level")), None)
                writer_key = (footer.get("createdBy"), vordered, level_text)
                writer = writers.setdefault(writer_key, dict(id=len(t["writers"]) + len(writers) + 1, table=table_id,
                                                             createdBy=writer_key[0], isVOrdered=vordered,
                                                             vOrderLevel=level_text, fileCount=0, fileSize=0))
                writer["fileCount"] += 1
                writer["fileSize"] += file["size"]
                compressed = uncompressed = 0
                for ordinal, group in enumerate(footer["rowGroups"]):
                    group_id = len(t["rowGroups"]) + len(row_group_rows) + 1
                    row_group_rows.append(dict(id=group_id, file=file_row["id"], ordinal=ordinal,
                                               rowCount=group["numRows"], uncompressedSize=group["totalByteSize"],
                                               compressedSize=group.get("totalCompressedSize")
                                               or sum(group["compressed"])))
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
                                     encodings=sorted(group["encodings"][position].split(",")),
                                     valueCount=group["numValues"][position], nullCount=group["nullCount"][position],
                                     compressedSize=size, uncompressedSize=group["uncompressed"][position],
                                     hasDictionaryPage=offset is not None,
                                     dictionaryPageSize=group["dataPageOffset"][position] - offset
                                     if offset is not None else None,
                                     dataPageCount=sum(int(p[2]) for p in data_pages) if pages else None,
                                     dictionaryEncodedPageCount=dictionary_pages if pages else None,
                                     distinctCount=group["distinctCount"][position],
                                     bloomFilterSize=(group.get("bloomFilterLength")
                                                      or [None] * len(group["codec"]))[position] or None)
                        if level >= 3:
                            t["columnChunks"].append(chunk)
                        if column is None:
                            stale += size
                            continue
                        if chunk["nullCount"] is None:
                            unknown_nulls.add(column["id"])
                        fallback = offset is not None and bool(pages) and dictionary_pages < chunk["dataPageCount"]
                        for name, value in (("compressedSize", size), ("uncompressedSize", chunk["uncompressedSize"]),
                                            ("valueCount", chunk["valueCount"]), ("nullCount", chunk["nullCount"] or 0),
                                            ("chunkCount", 1), ("dictionaryChunkCount", int(offset is not None)),
                                            ("fallbackChunkCount", int(fallback)),
                                            ("dictionaryPageSize", chunk["dictionaryPageSize"] or 0)):
                            column[name] = column.get(name, 0) + value
                        column["codecs"] = sorted(set(column.get("codecs", [])) | {chunk["codec"]})
                        column["encodings"] = sorted(set(column.get("encodings", [])) | set(chunk["encodings"]))
                        if chunk["distinctCount"] is not None:
                            column["largestChunkDistinctCount"] = max(column.get("largestChunkDistinctCount", 0),
                                                                      chunk["distinctCount"])
                file_row.update(footerSize=footer["footerSize"], physicalSchema=schema_ids[footer["schema"]],
                                writer=writer["id"], rowGroupCount=len(footer["rowGroups"]),
                                compressedSize=compressed, uncompressedSize=uncompressed)
            if level >= 1:
                t["files"].append(file_row)
        if level >= 2:
            t["rowGroups"].extend(row_group_rows)
        if level >= 1:
            t["partitions"].extend(partition_rows.values())
        t["writers"].extend(writers.values())

        for column in column_rows:
            if column["id"] in unknown_nulls:
                column["nullCount"] = None
            if read and not column["isPartitionColumn"]:
                with_column = {schema_ids[s] for (s, _), (row, c) in physical_rows.items() if c is column}
                column["fileCountWithoutColumn"] = sum(1 for f in read
                                                       if schema_ids[f["footer"]["schema"]] not in with_column)
        t["columns"].extend(column_rows)

        # ---- history ----
        operations = {}
        times = []
        for commit in dump.get("commits", []):
            when = iso(commit.get("timestamp"))
            if when:
                times.append(when)
            if level >= 1:
                t["commits"].append(dict(table=table_id, commitId=str(commit["version"]), timestamp=when,
                                         operation=commit.get("operation"), engine=commit.get("engine"),
                                         isBlindAppend=commit.get("isBlindAppend"), metrics=commit.get("metrics")))
            if commit.get("operation"):
                operation = operations.setdefault(commit["operation"], dict(table=table_id, name=commit["operation"],
                                                                            commitCount=0, lastAt=None))
                operation["commitCount"] += 1
                operation["lastAt"] = max(filter(None, [operation["lastAt"], when]), default=None)
        t["operations"].extend(operations.values())
        for issue in dump.get("issues", []):
            code, _, message = issue.partition(": ")
            t["issues"].append(dict(table=table_id, code=code, message=message))

        # ---- the table and its totals ----
        domains = delta.get("domains") or {}
        clustering = json.loads(domains.get("delta.clustering", "{}")).get("clusteringColumns", [])
        groups = [g for f in read for g in f["footer"]["rowGroups"]]
        snapshot = delta.get("version")
        checkpoint = delta.get("checkpointVersion")
        file_rows = [f.get("numRecords") if f.get("numRecords") is not None else (f.get("footer") or {}).get("numRows")
                     for f in files]
        t["tables"].append(dict(
            id=table_id, source=source_ids[key], namespace=dump["namespace"], name=dump["name"], format="Delta",
            tableId=delta.get("tableId"), createdAt=iso(delta.get("createdTime")), properties=delta.get("configuration"),
            snapshotId=str(snapshot) if snapshot is not None else None,
            snapshotTimestamp=next((iso(c.get("timestamp")) for c in dump.get("commits", [])
                                    if c.get("version") == snapshot), None),
            delta=known(minReaderVersion=delta.get("minReaderVersion"), minWriterVersion=delta.get("minWriterVersion"),
                        readerFeatures=delta.get("readerFeatures"), writerFeatures=delta.get("writerFeatures"),
                        checkpointVersion=checkpoint,
                        commitsAfterCheckpoint=snapshot - checkpoint if None not in (snapshot, checkpoint) else None,
                        domains=sorted(domains)),
            clusteringColumns=[column_by_physical[path[0]]["id"] for path in clustering
                               if path and path[0] in column_by_physical] or None,
            collectedLevel=2 if read else 1, detailDepth=DEPTHS[depth],
            fileCount=len(files), fileSize=sum(f["size"] for f in files),
            rowCount=sum(file_rows) if file_rows_known else None,
            deletedRowCount=sum(f.get("deletedRows") or 0 for f in files),
            lastModifiedAt=iso(max((f.get("modificationTime") or 0 for f in files), default=None) or None),
            vOrderedFileCount=sum(1 for f in files if _vordered(f)),
            partitionCount=len(partition_ids) if partition_columns else None,
            commitCount=delta.get("commitFileCount"), commitsRead=len(dump.get("commits", [])),
            oldestCommitAt=min(times, default=None),
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
            fileSizes=distribution([f["size"] for f in files]),
            rowGroupRowCounts=distribution([g["numRows"] for g in groups]),
            partitionSizes=distribution([p["fileSize"] for p in partition_rows.values()])))
    return t


def _vordered(file):
    """Whether a file of the dump is V-Ordered: its tag VORDER, else a key of its footer that ends with vorder.enabled."""
    if {k.lower(): v for k, v in (file.get("tags") or {}).items()}.get("vorder") == "true":
        return True
    keys = {kv["key"].lower(): kv.get("value") for kv in (file.get("footer") or {}).get("keyValues", [])}
    return any(k.endswith("vorder.enabled") and v == "true" for k, v in keys.items())


def footprint(dump_tables, depth, collection, version, builder_class):
    """A pqax.Builder (builder_class) with the footprint of the dumped tables at a depth, written as format version.
    Every row brings its id, so the builder keeps the numbering of the dump."""
    builder = builder_class(collection)
    for name, rows in rows_of(dump_tables, depth, version).items():
        for row in rows:
            builder.add(name, **row)
    return builder
