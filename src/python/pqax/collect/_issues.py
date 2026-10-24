"""The codes of the issues that the collector records, the same as those of the C# collector."""
from __future__ import annotations

import threading
from typing import List, NamedTuple, Optional


class IssueCodes:
    """The codes of the issues that the collector records in a footprint. A collection goes on after an issue, and the
    issue says what is missing. The codes are stable; the messages are not."""

    #: The storage denied access to a folder or a file, with status 401 or 403. On a footer, it counts as not read.
    ACCESS_DENIED = "AccessDenied"
    #: The table could not be read at all, for a reason that no other code covers.
    TABLE_NOT_READ = "TableNotRead"
    #: The table folder has no _delta_log, or it is empty.
    DELTA_LOG_MISSING = "DeltaLogMissing"
    #: The log lacks commits that the state of the table needs.
    DELTA_LOG_INCOMPLETE = "DeltaLogIncomplete"
    #: The checkpoint was not read: no checkpoint reader, a failure, or a size over the limit.
    DELTA_CHECKPOINT_NOT_READ = "DeltaCheckpointNotRead"
    #: _last_checkpoint names a checkpoint that the listing of the log does not have.
    DELTA_CHECKPOINT_MISSING = "DeltaCheckpointMissing"
    #: A commit file of the log could not be read or parsed.
    DELTA_COMMIT_NOT_READ = "DeltaCommitNotRead"
    #: The schema of the table could not be parsed, so the table has no columns.
    DELTA_SCHEMA_NOT_PARSED = "DeltaSchemaNotParsed"
    #: The statistics of a data file are not valid JSON.
    DELTA_STATS_NOT_PARSED = "DeltaStatsNotParsed"
    #: The table folder could not be listed for its data files, for another reason than a denied access.
    DATA_FILES_NOT_LISTED = "DataFilesNotListed"
    #: A data file is outside the table folder, so its footer was not read.
    FILE_OUTSIDE_TABLE = "FileOutsideTable"
    #: The footer of a data file could not be read, or the table format gives no size.
    FOOTER_NOT_READ = "FooterNotRead"
    #: The footer of a data file is encrypted.
    FOOTER_ENCRYPTED = "FooterEncrypted"
    #: The footer is not a valid Parquet footer, or the file is shorter than the table format says.
    FOOTER_INVALID = "FooterInvalid"
    #: The footer is larger than 64 MB.
    FOOTER_TOO_LARGE = "FooterTooLarge"
    #: Columns of a data file are encrypted, so their column chunks have no sizes.
    FOOTER_COLUMNS_ENCRYPTED = "FooterColumnsEncrypted"
    #: A table had more issues than the collector keeps; this one counts the rest.
    ISSUES_TRUNCATED = "IssuesTruncated"


#: Every code, in the order of IssueCodes.cs.
ISSUE_CODES = (
    IssueCodes.ACCESS_DENIED, IssueCodes.TABLE_NOT_READ, IssueCodes.DELTA_LOG_MISSING, IssueCodes.DELTA_LOG_INCOMPLETE,
    IssueCodes.DELTA_CHECKPOINT_NOT_READ, IssueCodes.DELTA_CHECKPOINT_MISSING, IssueCodes.DELTA_COMMIT_NOT_READ,
    IssueCodes.DELTA_SCHEMA_NOT_PARSED, IssueCodes.DELTA_STATS_NOT_PARSED, IssueCodes.DATA_FILES_NOT_LISTED,
    IssueCodes.FILE_OUTSIDE_TABLE, IssueCodes.FOOTER_NOT_READ, IssueCodes.FOOTER_ENCRYPTED, IssueCodes.FOOTER_INVALID,
    IssueCodes.FOOTER_TOO_LARGE, IssueCodes.FOOTER_COLUMNS_ENCRYPTED, IssueCodes.ISSUES_TRUNCATED,
)


class Issue(NamedTuple):
    """An issue of a table: its code, its message, and the path of the file it is about, relative to the table."""

    code: str
    message: str
    path: Optional[str] = None


class IssueList:
    """The issues of one table: the first 100, and a count of the rest. Safe to use from several threads."""

    LIMIT = 100

    def __init__(self) -> None:
        self._issues: List[Issue] = []
        self._dropped = 0
        self._lock = threading.Lock()

    def add(self, code: str, message: str, path: Optional[str] = None) -> None:
        with self._lock:
            if len(self._issues) < self.LIMIT:
                self._issues.append(Issue(code, message, path))
            else:
                self._dropped += 1

    def to_list(self) -> List[Issue]:
        with self._lock:
            issues = list(self._issues)
            if self._dropped:
                issues.append(Issue(IssueCodes.ISSUES_TRUNCATED,
                                    f"{self._dropped} more issues of this table were not kept."))
            return issues
