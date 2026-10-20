using System;
using System.IO;

namespace Pqax;

/// <summary>
/// Checks a PQAX file against every rule of the format and reports each issue with the code of its rule, as the
/// conformance kit lists them: <c>container.*</c>, <c>json.*</c>, <c>manifest.*</c>, <c>part.*</c>, <c>table.*</c>,
/// <c>depth.*</c>, and <c>totals.*</c>, and for a footprint inside a VPAX file, <c>embedded.partMissing</c>.
/// </summary>
/// <remarks>
/// The validator reads what a reader reads: the parts that the manifest lists, all of them, one at a time. It keeps
/// only the ids, the references, and the totals that the rules across tables compare. It reports every issue instead
/// of stopping at the first, and skips a rule that cannot be evaluated because an earlier rule failed. A file that is
/// not a zip archive is reported as <c>container.manifestMissing</c>, and so is a VPAX file given to
/// <see cref="Validate(string)"/> instead of <see cref="ValidateEmbedded(string, string)"/>. The validator never throws
/// <see cref="PqaxFormatException"/>: it throws only for a null argument, and when the file or the stream cannot be read
/// at all. It is safe to call from several threads.
/// </remarks>
public static class PqaxValidator
{
    /// <summary>Validates a PQAX file. The file is opened for reading, shared with other readers, and closed before this method returns.</summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The issues found, and how many checks ran.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="IOException">The file cannot be opened or read, such as <see cref="FileNotFoundException"/> when it does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not read the file.</exception>
    public static ValidationResult Validate(string path)
    {
        using Stream stream = OpenFile(path);
        return Validate(stream);
    }

    /// <summary>Validates a PQAX file in a stream. The stream is left open; one that cannot seek is copied into memory first.</summary>
    /// <param name="stream">A readable stream. A stream that can seek is read from its first byte, whatever its position.</param>
    /// <returns>The issues found, and how many checks ran.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="NotSupportedException">The stream cannot read.</exception>
    /// <exception cref="IOException">The stream cannot be read.</exception>
    public static ValidationResult Validate(Stream stream)
    {
        Stream source = Archive.Seekable(stream, true, out Stream? owned);
        try
        {
            return new ValidationRun(new ByteRange(source, 0, source.Length, new object())).Run();
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// Validates the PQAX file that a VPAX file embeds as one of its parts. The part is found as
    /// <see cref="PqaxDocument.OpenEmbedded(string, string)"/> finds it. The file is closed before this method returns.
    /// </summary>
    /// <param name="vpaxPath">The path of the VPAX file.</param>
    /// <param name="partName">The name of the part, <c>Footprint.pqax</c> by default.</param>
    /// <returns>The issues found, and how many checks ran. A package without the part gives the issue <c>embedded.partMissing</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="vpaxPath"/> or <paramref name="partName"/> is null.</exception>
    /// <exception cref="IOException">The file cannot be opened or read, such as <see cref="FileNotFoundException"/> when it does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not read the file.</exception>
    public static ValidationResult ValidateEmbedded(string vpaxPath, string partName = PqaxDocument.FootprintPartName)
    {
        using Stream stream = OpenFile(vpaxPath);
        return ValidateEmbedded(stream, partName);
    }

    /// <summary>
    /// Validates the PQAX file that a VPAX file embeds as one of its parts. The part is found as
    /// <see cref="PqaxDocument.OpenEmbedded(Stream, string, bool)"/> finds it. The stream is left open.
    /// </summary>
    /// <param name="vpaxStream">A readable stream of the VPAX file. A stream that can seek is read from its first byte, whatever its position.</param>
    /// <param name="partName">The name of the part, <c>Footprint.pqax</c> by default.</param>
    /// <returns>The issues found, and how many checks ran. A package without the part gives the issue <c>embedded.partMissing</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="vpaxStream"/> or <paramref name="partName"/> is null.</exception>
    /// <exception cref="NotSupportedException">The stream cannot read.</exception>
    /// <exception cref="IOException">The stream cannot be read.</exception>
    public static ValidationResult ValidateEmbedded(Stream vpaxStream, string partName = PqaxDocument.FootprintPartName)
    {
        if (partName == null)
        {
            throw new ArgumentNullException(nameof(partName));
        }

        Stream source = Archive.Seekable(vpaxStream, true, out Stream? owned);
        MemoryStream? inflated = null;
        try
        {
            ByteRange range;
            try
            {
                range = Archive.LocateEmbedded(new ByteRange(source, 0, source.Length, new object()), partName, out _, out inflated);
            }
            catch (PqaxFormatException e)
            {
                var log = new IssueLog();
                log.Fail(e.Code, e.Message, partName);
                return log.Result();
            }

            return new ValidationRun(range).Run();
        }
        finally
        {
            inflated?.Dispose();
            owned?.Dispose();
        }
    }

    private static Stream OpenFile(string path)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
    }
}
