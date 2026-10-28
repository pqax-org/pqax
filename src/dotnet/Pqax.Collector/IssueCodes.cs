namespace Pqax.Collector;

/// <summary>
/// The codes of the issues that the collector records in a footprint. A collection goes on after an issue, and the
/// issue says what is missing. The codes are stable; the messages are not.
/// </summary>
public static class IssueCodes
{
    /// <summary>
    /// The storage denied access to a folder or a file: a <see cref="StorageException"/> with status 401 or 403. On the
    /// footer of a data file, the issue has the path of the file and the footer counts as not read.
    /// </summary>
    public const string AccessDenied = "AccessDenied";

    /// <summary>The table could not be read at all, for a reason that no other code covers.</summary>
    public const string TableNotRead = "TableNotRead";

    /// <summary>The table folder has no <c>_delta_log</c>, or it is empty.</summary>
    public const string DeltaLogMissing = "DeltaLogMissing";

    /// <summary>The log lacks commits that the state of the table needs: a gap after the checkpoint, or no checkpoint and no first commit.</summary>
    public const string DeltaLogIncomplete = "DeltaLogIncomplete";

    /// <summary>
    /// The checkpoint was not read: no checkpoint reader, a form that needs one, a failure, or a size over the limit.
    /// When the log still has every commit up to the checkpoint, the collector replays them instead and the table is
    /// complete, as the message says; this happens with no issue when the only reason is a missing reader. Otherwise
    /// the files of the table are unknown, and the table has the <c>Summary</c> depth.
    /// </summary>
    public const string DeltaCheckpointNotRead = "DeltaCheckpointNotRead";

    /// <summary><c>_last_checkpoint</c> names a checkpoint that the listing of the log does not have.</summary>
    public const string DeltaCheckpointMissing = "DeltaCheckpointMissing";

    /// <summary>A commit file of the log could not be read or parsed.</summary>
    public const string DeltaCommitNotRead = "DeltaCommitNotRead";

    /// <summary>The schema of the table could not be parsed, so the table has no columns.</summary>
    public const string DeltaSchemaNotParsed = "DeltaSchemaNotParsed";

    /// <summary>The statistics of a data file are not valid JSON.</summary>
    public const string DeltaStatsNotParsed = "DeltaStatsNotParsed";

    /// <summary>
    /// The table folder could not be listed for its data files, for another reason than a denied access, so the
    /// unreferenced files of the table are unknown.
    /// </summary>
    public const string DataFilesNotListed = "DataFilesNotListed";

    /// <summary>A data file is outside the table folder, so its footer was not read.</summary>
    public const string FileOutsideTable = "FileOutsideTable";

    /// <summary>
    /// The footer of a data file could not be read: the read failed for another reason than a denied access, such as a
    /// missing file, another status, or a timeout, or the table format gives no size.
    /// </summary>
    public const string FooterNotRead = "FooterNotRead";

    /// <summary>The footer of a data file is encrypted.</summary>
    public const string FooterEncrypted = "FooterEncrypted";

    /// <summary>The footer of a data file is not a valid Parquet footer, or the file is shorter than the table format says.</summary>
    public const string FooterInvalid = "FooterInvalid";

    /// <summary>
    /// The footer of a data file is larger than <see cref="FooterReader.MaxFooterLength"/>, has a list longer than
    /// <see cref="FooterReader.MaxListLength"/>, or holds more column chunks or sorting columns than <see cref="FooterReader.MaxColumnChunks"/>.
    /// </summary>
    public const string FooterTooLarge = "FooterTooLarge";

    /// <summary>Columns of a data file are encrypted, so their column chunks have no sizes.</summary>
    public const string FooterColumnsEncrypted = "FooterColumnsEncrypted";

    /// <summary>A table had more issues than the collector keeps; this one counts the rest.</summary>
    public const string IssuesTruncated = "IssuesTruncated";
}
