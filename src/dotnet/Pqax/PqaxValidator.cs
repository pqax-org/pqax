using System;
using System.IO;

namespace Pqax;

/// <summary>
/// Checks a PQAX file against every rule of the format and reports each issue with the code of its rule, as the
/// conformance kit lists them: <c>container.*</c>, <c>json.*</c>, <c>manifest.*</c>, <c>part.*</c>, <c>table.*</c>,
/// <c>depth.*</c>, and <c>totals.*</c>, and for a footprint inside a VPAX file, <c>embedded.partMissing</c>.
/// </summary>
/// <remarks>
/// The validator reads what a reader reads: the parts that the manifest lists. It reports every issue instead of
/// stopping at the first, and skips a rule that cannot be evaluated because an earlier rule failed. A file that is
/// not a zip archive is reported as <c>container.manifestMissing</c>, and so is a VPAX file given to <see cref="Validate(string)"/>
/// instead of <see cref="ValidateEmbedded(string, string)"/>. The validator throws only when the file cannot
/// be read at all, as an <see cref="IOException"/>.
/// </remarks>
public static class PqaxValidator
{
    /// <summary>Validates a PQAX file.</summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The issues found, and how many checks ran.</returns>
    public static ValidationResult Validate(string path)
    {
        using Stream stream = OpenFile(path);
        return Validate(stream);
    }

    /// <summary>Validates a PQAX file in a stream. The stream is left open; one that cannot seek is copied into memory first.</summary>
    /// <param name="stream">The stream, from its start.</param>
    /// <returns>The issues found, and how many checks ran.</returns>
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

    /// <summary>Validates the PQAX file that a VPAX file embeds as one of its parts.</summary>
    /// <param name="vpaxPath">The path of the VPAX file.</param>
    /// <param name="partName">The name of the part, <c>Footprint.pqax</c> by default.</param>
    /// <returns>The issues found, and how many checks ran. A package without the part gives the issue <c>embedded.partMissing</c>.</returns>
    public static ValidationResult ValidateEmbedded(string vpaxPath, string partName = PqaxDocument.FootprintPartName)
    {
        using Stream stream = OpenFile(vpaxPath);
        return ValidateEmbedded(stream, partName);
    }

    /// <summary>Validates the PQAX file that a VPAX file embeds as one of its parts. The stream is left open.</summary>
    /// <param name="vpaxStream">The stream of the VPAX file, from its start.</param>
    /// <param name="partName">The name of the part, <c>Footprint.pqax</c> by default.</param>
    /// <returns>The issues found, and how many checks ran. A package without the part gives the issue <c>embedded.partMissing</c>.</returns>
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
