"""The errors that the package raises."""
from __future__ import annotations


class PqaxError(Exception):
    """The base class of every error of this package.

    `code` is the validation rule that the error matches, such as ``manifest.format``, or None when no rule matches.
    """

    code: str | None = None

    def __init__(self, message: str, *, code: str | None = None) -> None:
        super().__init__(message)
        if code is not None:
            self.code = code


class ContainerError(PqaxError):
    """The file is not a zip archive, or an entry of the archive cannot be read."""


class ManifestMissingError(PqaxError):
    """The archive has no ``manifest.json`` entry."""

    code = "container.manifestMissing"


class FormatError(PqaxError):
    """The manifest does not say that the format is ``pqax``."""

    code = "manifest.format"


class UnknownVersionError(PqaxError):
    """The first number of ``formatVersion`` is not one that this package knows."""

    code = "manifest.versionUnknown"


class JsonError(PqaxError):
    """A part is not valid JSON."""

    code = "json.invalid"


class PartMissingError(PqaxError):
    """A detail part that the manifest lists is not in the archive, with the code manifest.partMissing, or the
    embedded part is not in the package, with the code embedded.partMissing."""

    code = "manifest.partMissing"


class WriteError(PqaxError, ValueError):
    """Builder.write refuses a footprint that breaks a rule of the format, before it writes anything.

    `code` is the code of the rule, as validate() reports it: ``manifest.required``, ``table.idUnique``,
    ``table.required``, ``json.integer``, or ``json.integerRange``. `table` is the table, None for the collection, and
    `field` the field: for a member of a nested object, the field that holds it, such as ``fileSizes``; for the
    collection, ``collection.level`` and the like. It is a ValueError, as the error of a repeated id was before it had a
    code.
    """

    def __init__(self, message: str, *, code: str, table: str | None = None, field: str | None = None) -> None:
        super().__init__(message, code=code)
        self.table = table
        self.field = field
