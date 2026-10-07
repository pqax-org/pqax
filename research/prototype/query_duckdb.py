"""Reads the tables of a PQAX file with SQL, to show that they load as they are and that the references join.

Usage: python query_duckdb.py <file.pqax>     (needs the duckdb package, and a file with column chunks)

It prints identifiers and numbers, never a name.
"""
import pathlib
import sys
import tempfile
import time
import zipfile

import duckdb

with tempfile.TemporaryDirectory() as folder:
    # DuckDB reads JSON files, not the entries of a zip, so the parts are extracted first.
    zipfile.ZipFile(sys.argv[1]).extractall(folder)
    path = pathlib.Path(folder).as_posix()
    con = duckdb.connect()
    start = time.perf_counter()
    # Each table of the file becomes a SQL table: one row for each position of the arrays.
    con.execute(f"""
        CREATE TABLE tables AS
            SELECT unnest(tables.id) AS id, unnest(tables.compressedSize) AS compressedSize
            FROM read_json('{path}/summary.json', maximum_object_size = 1000000000);
        CREATE TABLE physicalSchemas AS
            SELECT unnest(physicalSchemas.id) AS id, unnest(physicalSchemas."table") AS "table"
            FROM read_json('{path}/summary.json', maximum_object_size = 1000000000);
        CREATE TABLE physicalColumns AS
            SELECT unnest(physicalColumns.id) AS id, unnest(physicalColumns.physicalSchema) AS physicalSchema
            FROM read_json('{path}/summary.json', maximum_object_size = 1000000000);
        CREATE TABLE columnChunks AS
            SELECT unnest(columnChunks.physicalColumn) AS physicalColumn, unnest(columnChunks.codec) AS codec,
                   unnest(columnChunks.compressedSize) AS compressedSize
            FROM read_json('{path}/columnChunks*.json', maximum_object_size = 1000000000);""")
    rows = con.execute("""
        SELECT s."table", count(*) AS chunks, sum(c.compressedSize) AS bytes, any_value(t.compressedSize) AS total
        FROM columnChunks AS c
        JOIN physicalColumns AS p ON p.id = c.physicalColumn
        JOIN physicalSchemas AS s ON s.id = p.physicalSchema
        JOIN tables AS t ON t.id = s."table"
        GROUP BY ALL ORDER BY bytes DESC""").fetchall()
    elapsed = time.perf_counter() - start
    print(f"loaded and joined in {elapsed:.2f} s: {sum(r[1] for r in rows):,} column chunks, {len(rows)} tables")
    print(f"tables whose total equals the sum over their column chunks: {sum(1 for r in rows if r[2] == r[3])} of {len(rows)}")
    for table, chunks, size, total in rows[:3]:
        print(f"   table {table}: {chunks:,} column chunks, {size:,} bytes")
    print("by codec:", con.execute("SELECT codec, count(*), sum(compressedSize) FROM columnChunks GROUP BY codec ORDER BY 2 DESC").fetchall())
