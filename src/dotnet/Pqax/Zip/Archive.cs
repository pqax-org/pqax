using System;
using System.IO;

namespace Pqax;

/// <summary>Opens zip archives for the reader and the validator, and finds a PQAX file embedded in a VPAX file.</summary>
internal static class Archive
{
    /// <summary>The largest part that is read into memory: 1 GiB. A part of 250,000 column chunks is about 20 MB of JSON.</summary>
    public const long MaxPartSize = 1L << 30;

    /// <summary>
    /// A seekable stream with the content of <paramref name="stream"/>, and the stream that the caller of the library
    /// hands over and must be disposed with the document. A stream that cannot seek is copied into memory.
    /// </summary>
    public static Stream Seekable(Stream stream, bool leaveOpen, out Stream? owned)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (stream.CanSeek)
        {
            owned = leaveOpen ? null : stream;
            return stream;
        }

        var memory = new MemoryStream();
        stream.CopyTo(memory);
        if (!leaveOpen)
        {
            stream.Dispose();
        }

        memory.Position = 0;
        owned = memory;
        return memory;
    }

    /// <summary>The central directory, or <see cref="PqaxFormatException"/> when the bytes are not a zip archive that this library reads.</summary>
    public static ZipDirectory OpenDirectory(ByteRange range, string what)
    {
        ZipDirectory directory;
        try
        {
            directory = ZipDirectory.Read(range);
        }
        catch (InvalidDataException e)
        {
            throw new PqaxFormatException("container.manifestMissing", $"The {what} is not a zip archive that can be read: {e.Message}", e);
        }

        if (directory.NeedsZip64)
        {
            throw new PqaxFormatException("container.zip64", $"The {what} is a ZIP64 archive, which the format does not allow.");
        }

        return directory;
    }

    /// <summary>The content of an entry, or <see cref="PqaxFormatException"/> when it cannot be read.</summary>
    public static byte[] ReadEntry(ZipDirectory directory, ZipEntry entry, long maxSize)
    {
        if (entry.NeedsZip64)
        {
            throw new PqaxFormatException("container.zip64", $"The entry {entry.Name} needs ZIP64, which the format does not allow.");
        }

        if (entry.IsEncrypted)
        {
            throw new PqaxFormatException("container.encrypted", $"The entry {entry.Name} is encrypted.");
        }

        if (!entry.IsSupportedMethod)
        {
            throw new PqaxFormatException("container.method", $"The entry {entry.Name} uses compression method {entry.Method}, which is neither stored nor deflate.");
        }

        try
        {
            return directory.ReadBytes(entry, maxSize);
        }
        catch (InvalidDataException e)
        {
            throw new PqaxFormatException("json.invalid", $"The part {entry.Name} cannot be read: {e.Message}", e);
        }
    }

    /// <summary>The error <c>embedded.partMissing</c> for a package without the part <paramref name="partName"/>.</summary>
    public static PqaxFormatException PartMissing(string partName)
        => new PqaxFormatException("embedded.partMissing", $"The package has no part named {partName.TrimStart('/')}, so it holds no footprint.");

    /// <summary>As <see cref="FindEmbedded"/>, and <c>embedded.partMissing</c> when the package has no such part.</summary>
    public static ByteRange LocateEmbedded(ByteRange package, string partName, out string name, out MemoryStream? inflated)
        => FindEmbedded(package, partName, out name, out inflated) ?? throw PartMissing(partName);

    /// <summary>
    /// The range of the PQAX file that a VPAX file embeds as the part <paramref name="partName"/>, or null when the
    /// package has no such part. A stored part is a range of the VPAX file, read in place. A deflated part is inflated
    /// into memory, and <paramref name="inflated"/> is then the stream that holds it. <paramref name="name"/> is the name
    /// of the entry: the part name without a leading slash, or the entry whose name differs from it only in case.
    /// </summary>
    public static ByteRange? FindEmbedded(ByteRange package, string partName, out string name, out MemoryStream? inflated)
    {
        ZipDirectory directory = OpenDirectory(package, "VPAX file");
        string wanted = partName.TrimStart('/');
        ZipEntry? entry = directory.FindLoosely(wanted);
        if (entry == null)
        {
            name = wanted;
            inflated = null;
            return null;
        }

        name = entry.Name;
        if (entry.IsStored && !entry.IsEncrypted && !entry.NeedsZip64)
        {
            try
            {
                ByteRange range = directory.StoredRange(entry);
                inflated = null;
                return range;
            }
            catch (InvalidDataException e)
            {
                throw new PqaxFormatException("container.manifestMissing", $"The part {entry.Name} cannot be read: {e.Message}", e);
            }
        }

        byte[] bytes;
        try
        {
            bytes = ReadEntry(directory, entry, int.MaxValue - 64);
        }
        catch (PqaxFormatException e) when (e.Code == "json.invalid")
        {
            throw new PqaxFormatException("container.manifestMissing", $"The part {entry.Name} cannot be inflated: {e.InnerException?.Message}", e);
        }

        inflated = new MemoryStream(bytes, 0, bytes.Length, false, true);
        return new ByteRange(inflated, 0, bytes.Length, new object());
    }
}
