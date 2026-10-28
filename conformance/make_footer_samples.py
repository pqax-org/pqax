"""Rebuilds samples/footers: Parquet files that the footer parsers of the collectors once read wrong, and nested
columns of several writers, with expected.json.

Usage, from the root of the repository, with a folder that make_footers.py wrote in the environments of its README
section, and with pyarrow installed:

    python conformance/make_footer_samples.py FILES

The samples are of two kinds. Some are files of FILES, copied under a name that says the writer, its version, and
the case. The others are crafted here from one of them, byte by byte, with the defect they caught. expected.json has
one member for each, as section 5 of the contract of the footer work says and samples/footers/README.md... the
conformance README describes: the size that the reader is given, whether the data values are kept, and the error
or the facts of the footer and the columns of a folder that holds the file alone. The facts come from the Python
collector; footer_check.py checks that the C# collector finds the same.
"""
from __future__ import annotations

import json
import shutil
import struct
import sys
import tempfile
from pathlib import Path
from typing import Any, Dict, List, Optional

import mutate_footers as m

KIT = Path(__file__).resolve().parent
SAMPLES = KIT / "samples" / "footers"
sys.path.insert(0, str(KIT.parent / "src" / "python"))

# The files of FILES that the kit keeps: (the name in the kit, the path in FILES, the writer, what it tests).
COPIED = (
    ("fastparquet-2026.9.0-empty.parquet", "latest/fastparquet/empty/part-0.parquet", "fastparquet 2026.9.0",
     "A file without rows, whose row groups fastparquet writes as an empty list with the element type 0. Both parsers "
     "refused it as not a list of structures."),
    ("pyarrow-26.0.0-nested.parquet", "latest/pyarrow/nested/part-0.parquet", "pyarrow 26.0.0",
     "Lists, large lists, fixed-size lists, maps, a list of structures, a map of lists, structures four levels deep, "
     "with nulls at every level: each leaf under its top-level column."),
    ("pyarrow-8.0.0-nested.parquet", "pa8/pyarrow/nested/part-0.parquet", "pyarrow 8.0.0",
     "The same columns, with the names of older pyarrow: list and item for a list."),
    ("duckdb-2.0.0-nested.parquet", "latest/duckdb/nested/part-0.parquet", "DuckDB 2.0.0",
     "Lists, structures, maps, and an array of DuckDB, nested."),
    ("polars-2.1.0-nested.parquet", "latest/polars/nested/part-0.parquet", "polars 2.1.0",
     "Lists, structures, and a fixed-size array of the native writer of polars, nested."),
    ("parquet.net-6.1.0-nested.parquet", "parquet.net-6.1.0/parquet.net/nested/part-0.parquet", "Parquet.Net 6.1.0",
     "Lists, dictionaries, and classes of Parquet.Net, nested, with a dictionary of structures."),
    ("datafusion-55.0.0-nested.parquet", "latest/datafusion/nested/part-0.parquet", "datafusion 55.0.0 (arrow-rs)",
     "Lists, a map, and structures nested three levels deep, written by arrow-rs."),
)
BASE = "latest/pyarrow/base/part-0.parquet"


def footer_of(data: bytes) -> bytes:
    length = struct.unpack("<i", data[-8:-4])[0]
    return data[len(data) - 8 - length:len(data) - 8]


def with_footer(data: bytes, footer: bytes) -> bytes:
    """The file with another footer: the data before the old footer, the new one, its length, and the magic."""
    length = struct.unpack("<i", data[-8:-4])[0]
    return data[:len(data) - 8 - length] + footer + struct.pack("<i", len(footer)) + b"PAR1"


def crafted(files: Path) -> List[Dict[str, Any]]:
    """The crafted samples, from the base file of pyarrow 26.0.0."""
    base = (files / BASE).read_bytes()
    footer = footer_of(base)
    root = m.Decoder(footer).struct()
    samples = []

    def add(name: str, data: bytes, what: str, data_values: bool = False) -> None:
        samples.append({"name": name, "data": data, "what": what, "includeDataValues": data_values})

    # The stop of the footer written as a header of type 0 with a delta, which the Thrift libraries read as a stop.
    add("stop-with-delta.parquet", with_footer(base, footer[:-1] + b"\x10"),
        "The footer of pyarrow-26 base, with its last stop written as 0x10: a field header of type 0 and delta 1, which "
        "the Thrift libraries of C++, Java, and Rust read as the end of the structure. C# read a field of an unknown "
        "wire type.")
    # 100,000 lists nested in an unknown field of the footer: C# overflowed its stack.
    deep = b"\x02"
    kind = m.I32
    for _ in range(100_000):
        deep = bytes([0x10 | kind]) + deep
        kind = m.LIST
    add("nested-lists-100000.parquet", with_footer(base, footer[:-1] + bytes([m.LIST]) + b"\x3c" + deep + b"\x00"),
        "An unknown field 30 of the footer that holds 100,000 lists nested in one another. C# overflowed its stack and "
        "the process ended; Python refused it with another message past about 500 levels.")
    # The values nested 64 levels counted from the footer itself: accepted, then one more level: refused.
    for levels, verb in ((63, "is skipped"), (64, "is refused")):
        nested = b"\x02"
        kind = m.I32
        for _ in range(levels):
            nested = bytes([0x10 | kind]) + nested
            kind = m.LIST
        add(f"nested-lists-{levels}.parquet", with_footer(base, footer[:-1] + bytes([m.LIST]) + b"\x3c" + nested + b"\x00"),
            f"{levels} lists nested in an unknown field of the footer, which is the first level: {levels + 1} levels "
            f"in all, which {verb}.")
    # A row group whose list of column chunks appears twice, the second shorter.
    mutated = m.copy_fields(root)
    group = next(f for f in mutated if f.id == 4).value[1][0]
    columns = next(i for i, f in enumerate(group) if f.id == 1)
    element, items = group[columns].value
    group.insert(columns + 1, m.Field(1, m.LIST, (element, [m.copy_value(element, items[0])])))
    add("columns-repeated-shorter.parquet", with_footer(base, m.encode_struct(mutated)),
        "The first row group repeats its field 1, the column chunks, with one chunk: the last value wins, and the row "
        "group has one chunk for 12 leaves. C# appended the second list to the first, or threw "
        "ArgumentOutOfRangeException when the second was shorter.")
    mutated = m.copy_fields(root)
    group = next(f for f in mutated if f.id == 4).value[1][0]
    columns = next(i for i, f in enumerate(group) if f.id == 1)
    group.insert(columns + 1, m.Field(1, m.LIST, m.copy_value(m.LIST, group[columns].value)))
    add("columns-repeated.parquet", with_footer(base, m.encode_struct(mutated)),
        "The first row group repeats its field 1, the column chunks, with the same chunks: the last value wins, and the "
        "footer is read. C# read twice as many chunks as leaves.")

    def one_leaf(leaf: List[m.Field], statistics: List[m.Field], physical: int) -> bytes:
        meta = [m.Field(1, m.I32, m.zigzag(physical)), m.Field(2, m.LIST, (m.I32, [0])), m.Field(4, m.I32, 0),
                m.Field(5, m.I64, m.zigzag(1)), m.Field(6, m.I64, m.zigzag(10)), m.Field(7, m.I64, m.zigzag(10)),
                m.Field(9, m.I64, m.zigzag(4)), m.Field(12, m.STRUCT, statistics)]
        group = [m.Field(1, m.LIST, (m.STRUCT, [[m.Field(3, m.STRUCT, meta)]])), m.Field(2, m.I64, m.zigzag(10)),
                 m.Field(3, m.I64, m.zigzag(1))]
        schema = [[m.Field(4, m.BINARY, b"schema"), m.Field(5, m.I32, m.zigzag(1))], leaf]
        return m.encode_struct([m.Field(1, m.I32, m.zigzag(1)), m.Field(2, m.LIST, (m.STRUCT, schema)),
                                m.Field(3, m.I64, m.zigzag(1)), m.Field(4, m.LIST, (m.STRUCT, [group])),
                                m.Field(6, m.BINARY, b"pqax kit")])

    long_bound = bytes([0x7F]) + bytes(range(256)) * 7 + bytes(7)  # 1,800 bytes, a positive number
    decimal_leaf = [m.Field(1, m.I32, m.zigzag(6)), m.Field(3, m.I32, m.zigzag(1)), m.Field(4, m.BINARY, b"amount"),
                    m.Field(10, m.STRUCT, [m.Field(5, m.STRUCT, [m.Field(1, m.I32, m.zigzag(10)),
                                                                 m.Field(2, m.I32, m.zigzag(38))])])]
    add("decimal-bound-1800-bytes.parquet",
        m.file_of(one_leaf(decimal_leaf, [m.Field(5, m.BINARY, long_bound), m.Field(6, m.BINARY, long_bound[:16])], 6)),
        "A DECIMAL(38,10) column of BYTE_ARRAY whose maximum takes 1,800 bytes, read with the data values: the bound "
        "is longer than 32 bytes and has no text, and the minimum of 16 bytes has one. Python raised ValueError past "
        "4,300 digits.", True)
    scale_leaf = [m.Field(1, m.I32, m.zigzag(2)), m.Field(3, m.I32, m.zigzag(1)), m.Field(4, m.BINARY, b"amount"),
                  m.Field(10, m.STRUCT, [m.Field(5, m.STRUCT, [m.Field(1, m.I32, m.zigzag(0x7FFFFFFF)),
                                                               m.Field(2, m.I32, m.zigzag(18))])])]
    eight = struct.pack("<q", 123456789)
    add("decimal-scale-2147483647.parquet",
        m.file_of(one_leaf(scale_leaf, [m.Field(5, m.BINARY, eight), m.Field(6, m.BINARY, eight)], 2)),
        "A DECIMAL of INT64 with the scale 2,147,483,647, read with the data values: the scale is above 76 and the "
        "bounds have no text. Python built a string of 2 GB, and took seconds.", True)

    # A schema 200 levels deep.
    elements = [[m.Field(4, m.BINARY, b"schema"), m.Field(5, m.I32, m.zigzag(1))]]
    for level in range(199):
        elements.append([m.Field(3, m.I32, m.zigzag(1)), m.Field(4, m.BINARY, f"g{level}".encode()),
                         m.Field(5, m.I32, m.zigzag(1))])
    elements.append([m.Field(1, m.I32, m.zigzag(1)), m.Field(3, m.I32, m.zigzag(1)), m.Field(4, m.BINARY, b"leaf")])
    meta = [m.Field(1, m.I32, m.zigzag(1)), m.Field(2, m.LIST, (m.I32, [0])), m.Field(4, m.I32, 0),
            m.Field(5, m.I64, m.zigzag(1)), m.Field(6, m.I64, m.zigzag(10)), m.Field(7, m.I64, m.zigzag(10)),
            m.Field(9, m.I64, m.zigzag(4))]
    group = [m.Field(1, m.LIST, (m.STRUCT, [[m.Field(3, m.STRUCT, meta)]])), m.Field(2, m.I64, m.zigzag(10)),
             m.Field(3, m.I64, m.zigzag(1))]
    add("schema-200-levels.parquet",
        m.file_of(m.encode_struct([m.Field(1, m.I32, m.zigzag(1)), m.Field(2, m.LIST, (m.STRUCT, elements)),
                                   m.Field(3, m.I64, m.zigzag(1)), m.Field(4, m.LIST, (m.STRUCT, [group]))])),
        "A schema of 200 levels below its root, which the parser reads: the column of a folder of Parquet files gets "
        "its type. The type of the columns stopped at 64 levels, and the table was not read, with an issue message "
        "that differed between C# and Python.")

    # A key of the allow-list with a character outside ASCII whose lower case is an ASCII letter, and one in upper case.
    mutated = m.copy_fields(root)
    mutated = [f for f in mutated if f.id != 5]
    keys = (m.STRUCT, [[m.Field(1, m.BINARY, "org.apache.sparK.version".encode()), m.Field(2, m.BINARY, b"3.5.1")],
                       [m.Field(1, m.BINARY, "com.microsoft.parquet.VORDER.enabled".encode()),
                        m.Field(2, m.BINARY, b"True")]])
    at = next(i for i, f in enumerate(mutated) if f.id > 5)
    mutated.insert(at, m.Field(5, m.LIST, keys))
    add("keys-folded-case.parquet", with_footer(base, m.encode_struct(mutated)),
        "The keys org.apache.sparK.version, with the Kelvin sign for its k, and com.microsoft.parquet.VORDER.enabled "
        "= True. Both compare the keys of the allow-list and of V-Order as .NET OrdinalIgnoreCase does, where a "
        "character outside ASCII never folds into ASCII: the first value is not kept, and the second is, with V-Order. "
        "Python kept the first, since str.lower() makes the Kelvin sign a k.")
    return samples


def facts(path: Path, data_values: bool, stated: Optional[int]) -> Dict[str, Any]:
    """The member of expected.json of a file, from the Python collector."""
    from pqax.collect import FooterError, FooterReader, LocalStorage, collect, discover_folder

    reader = FooterReader(LocalStorage(str(path.parent)), include_data_values=data_values)
    size = path.stat().st_size if stated is None else stated
    try:
        footer = reader.read(path.name, size)
    except FooterError as error:
        return {"error": {"code": error.code, "message": str(error)}, "footer": None}
    member: Dict[str, Any] = {"error": None, "footer": {
        "numRows": footer.num_rows, "rowGroups": len(footer.row_groups), "createdBy": footer.created_by,
        "leaves": [{"path": list(leaf.path), "physicalType": leaf.physical_type, "typeAnnotation": leaf.type_annotation,
                    "repetition": leaf.repetition} for leaf in footer.leaves],
        "keyValues": [[kv.key, kv.value] for kv in footer.key_value_metadata]}}
    if data_values and footer.row_groups:
        member["footer"]["bounds"] = [[chunk.min_value, chunk.max_value] for chunk in footer.row_groups[0].columns]
    with tempfile.TemporaryDirectory(prefix="pqax-kit-") as folder:
        table = Path(folder) / "table"
        table.mkdir()
        shutil.copyfile(path, table / "part-0.parquet")
        storage = LocalStorage(folder)
        builder = collect(storage, discover_folder(storage, ""), include_data_values=data_values)
        output = Path(folder) / "out.pqax"
        builder.write(str(output))
        import pqax

        with pqax.open(str(output)) as document:
            columns = document.table("columns")
            physical = document.table("physicalColumns")
            member["footer"]["columns"] = [{
                "name": columns["name"][i], "dataType": columns["dataType"][i], "isNullable": columns["isNullable"][i],
                "leaves": [physical["ordinal"][j] for j in range(len(physical["id"]))
                           if physical["column"][j] == columns["id"][i]]} for i in range(len(columns["id"]))]
    return member


def main(argv: Optional[List[str]] = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if len(argv) != 1:
        print(__doc__)
        return 2
    files = Path(argv[0]).resolve()
    SAMPLES.mkdir(parents=True, exist_ok=True)
    for path in SAMPLES.glob("*.parquet"):
        path.unlink()
    expected: Dict[str, Dict[str, Any]] = {}
    for name, source, writer, what in COPIED:
        shutil.copyfile(files / source, SAMPLES / name)
        expected[name] = {"writer": writer, "what": what, "statedSize": None, "includeDataValues": False}
    for sample in crafted(files):
        (SAMPLES / sample["name"]).write_bytes(sample["data"])
        expected[sample["name"]] = {"writer": "make_footer_samples.py", "what": sample["what"], "statedSize": None,
                                    "includeDataValues": sample["includeDataValues"]}
    for name, member in expected.items():
        member.update(facts(SAMPLES / name, member["includeDataValues"], member["statedSize"]))
        print(f"{name:40} {(SAMPLES / name).stat().st_size:>8,} bytes  "
              + (member["error"]["code"] if member["error"] else f"{len(member['footer']['leaves'])} leaves"))
    text = json.dumps(dict(sorted(expected.items())), indent=1, ensure_ascii=False)
    (SAMPLES / "expected.json").write_text(text + "\n", encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
