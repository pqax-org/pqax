"""pqax.try_open_embedded: the footprint that a package holds, or None exactly when the package has no part of that
name, and every other failure raised as pqax.open_embedded raises it (the contract of the hardening before 1.0,
section 3)."""
from __future__ import annotations

import io
import pathlib
import zipfile

import pytest

import pqax
from pqax_kit import EMBEDDED_PART, FORMAT_TABLES, INVALID, SAMPLES, write_package_without_footprint

PACKAGES = {"embedded-stored.vpax": "stored", "embedded-deflated.vpax": "deflated"}
KINDS = ("str", "pathlib.Path", "bytes", "file object")


def source_of(path, kind):
    """The package in one of the forms that pqax.open_embedded takes."""
    if kind == "str":
        return str(path)
    if kind == "pathlib.Path":
        return pathlib.Path(path)
    if kind == "bytes":
        return path.read_bytes()
    return io.BytesIO(path.read_bytes())


def write_package(target, footprint):
    """A VPAX-like package with [Content_Types].xml, DaxModel.json, and these bytes as Footprint.pqax, stored."""
    with zipfile.ZipFile(SAMPLES / "embedded-stored.vpax") as original, zipfile.ZipFile(target, "w") as copy:
        for info in original.infolist():
            data = footprint if info.filename == EMBEDDED_PART else original.read(info.filename)
            copy.writestr(info.filename, data, compress_type=zipfile.ZIP_STORED)


def raised_by(function, *args):
    with pytest.raises(Exception) as raised:
        function(*args)
    return raised.value


@pytest.fixture
def without_footprint(tmp_path):
    package = tmp_path / "no-footprint.vpax"
    write_package_without_footprint(package)
    return package


def test_try_open_embedded_is_exported_by_the_package():
    assert "try_open_embedded" in pqax.__all__
    assert callable(pqax.try_open_embedded)


# ---- a package with the footprint ----

@pytest.mark.parametrize("kind", KINDS)
@pytest.mark.parametrize("name", sorted(PACKAGES))
def test_a_package_with_the_footprint_gives_the_document_of_sample(name, kind):
    doc = pqax.try_open_embedded(source_of(SAMPLES / name, kind))
    assert isinstance(doc, pqax.PqaxDocument)
    with doc, pqax.open(str(SAMPLES / "sample.pqax")) as sample:
        assert doc.embedded_part == EMBEDDED_PART
        assert doc.embedded_compression == PACKAGES[name]
        assert doc.manifest == sample.manifest
        for table in FORMAT_TABLES:
            assert doc.table(table) == sample.table(table), table


@pytest.mark.parametrize("part_name", ["Footprint.pqax", "/Footprint.pqax", "footprint.PQAX", "/FOOTPRINT.pqax"])
def test_the_part_is_found_as_open_embedded_finds_it(part_name):
    path = str(SAMPLES / "embedded-deflated.vpax")
    with pqax.try_open_embedded(path, part_name) as doc, pqax.open_embedded(path, part_name) as expected:
        assert doc.embedded_part == expected.embedded_part == EMBEDDED_PART
        assert doc.row_count("columnChunks") == 52
    with pqax.try_open_embedded(path, part_name=part_name) as doc:
        assert doc.embedded_part == EMBEDDED_PART


# ---- a package without the part ----

@pytest.mark.parametrize("kind", KINDS)
def test_a_package_without_the_part_gives_none(kind, without_footprint):
    assert pqax.try_open_embedded(source_of(without_footprint, kind)) is None
    assert pqax.try_open_embedded(source_of(SAMPLES / "embedded-stored.vpax", kind), "Nothing.pqax") is None


def test_none_leaves_the_package_closed_and_a_file_object_open(without_footprint):
    with open(without_footprint, "rb") as handle:
        assert pqax.try_open_embedded(handle) is None
        assert not handle.closed
    assert pqax.try_open_embedded(str(without_footprint)) is None
    # No handle is left on the file: it can be deleted at once, which Windows refuses for an open file.
    without_footprint.unlink()
    assert not without_footprint.exists()


def test_open_embedded_still_raises_embedded_partMissing(without_footprint):
    for source in (str(without_footprint), without_footprint.read_bytes()):
        error = raised_by(pqax.open_embedded, source)
        assert isinstance(error, pqax.PqaxError)
        assert error.code == "embedded.partMissing"
    error = raised_by(pqax.open_embedded, str(SAMPLES / "embedded-stored.vpax"), "Nothing.pqax")
    assert error.code == "embedded.partMissing"


# ---- every other failure raises ----

def failures(tmp_path):
    """Packages that open_embedded refuses for a reason other than a missing part, by what is wrong."""
    sources = {}
    not_a_zip = tmp_path / "not-a-zip.vpax"
    not_a_zip.write_bytes(b"not a zip archive")
    sources["a package that is not a zip archive, as a path"] = str(not_a_zip)
    sources["a package that is not a zip archive, as bytes"] = b"not a zip archive"
    for name, footprint in (("a footprint that is not a zip archive", b"not a zip archive"),
                            ("a footprint with the format vpax", (INVALID / "manifest.format.pqax").read_bytes()),
                            ("a footprint of version 9.0", (INVALID / "manifest.versionUnknown.pqax").read_bytes()),
                            ("a footprint without manifest.json",
                             (INVALID / "container.manifestMissing.pqax").read_bytes())):
        package = tmp_path / f"{len(sources)}.vpax"
        write_package(package, footprint)
        sources[name] = str(package)
    sources["a path that does not exist"] = str(tmp_path / "missing.vpax")
    return sources


def test_every_other_failure_raises_as_open_embedded_raises_it(tmp_path):
    for case, source in failures(tmp_path).items():
        expected = raised_by(pqax.open_embedded, source)
        error = raised_by(pqax.try_open_embedded, source)
        assert type(error) is type(expected), case
        assert getattr(error, "code", None) == getattr(expected, "code", None), case
        assert getattr(error, "code", None) != "embedded.partMissing", case


@pytest.mark.parametrize("footprint,code", [
    ("manifest.format.pqax", "manifest.format"),
    ("manifest.versionUnknown.pqax", "manifest.versionUnknown"),
    ("container.manifestMissing.pqax", "container.manifestMissing"),
])
def test_a_footprint_that_is_not_a_pqax_file_raises_its_code(footprint, code, tmp_path):
    package = tmp_path / "package.vpax"
    write_package(package, (INVALID / footprint).read_bytes())
    for source in (str(package), package.read_bytes(), io.BytesIO(package.read_bytes())):
        error = raised_by(pqax.try_open_embedded, source)
        assert isinstance(error, pqax.PqaxError)
        assert error.code == code


def test_a_package_or_a_footprint_that_is_not_a_zip_archive_raises_container_manifestMissing(tmp_path):
    package = tmp_path / "not-a-zip.vpax"
    package.write_bytes(b"not a zip archive")
    holder = tmp_path / "holder.vpax"
    write_package(holder, b"not a zip archive")
    for source in (str(package), b"not a zip archive", io.BytesIO(b"not a zip archive"), str(holder)):
        error = raised_by(pqax.try_open_embedded, source)
        assert isinstance(error, pqax.PqaxError)
        assert error.code == "container.manifestMissing"
