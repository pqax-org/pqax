using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Pqax;

/// <summary>
/// The central directory of a zip archive, read from the end of the archive, so that an entry is read only when it is
/// needed. It handles what a PQAX file may use: stored and deflated entries, without ZIP64. The position and the size
/// of an entry come from the central directory, and the local header is skipped by its name and extra lengths.
/// </summary>
internal sealed class ZipDirectory
{
    private const int EndRecordLength = 22;
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint LocalSignature = 0x04034b50;

    private readonly ByteRange range;
    private readonly Dictionary<string, ZipEntry> byName;

    private ZipDirectory(ByteRange range, IReadOnlyList<ZipEntry> entries, bool needsZip64, bool hasZip64Locator)
    {
        this.range = range;
        Entries = entries;
        NeedsZip64 = needsZip64;
        HasZip64Locator = hasZip64Locator;
        byName = new Dictionary<string, ZipEntry>(StringComparer.Ordinal);
        foreach (ZipEntry entry in entries)
        {
            // When a name repeats, the last entry wins, as in the zip readers of Python and of the JavaScript prototype.
            byName[entry.Name] = entry;
        }
    }

    /// <summary>The entries in the order of the central directory.</summary>
    public IReadOnlyList<ZipEntry> Entries { get; }

    /// <summary>Whether the end record of the archive defers to ZIP64 records, so that its entries cannot be read without ZIP64.</summary>
    public bool NeedsZip64 { get; }

    /// <summary>
    /// Whether a ZIP64 end of central directory locator precedes the end record. When the end record still has its
    /// real values, the archive is read from them, and only the validator reports the locator.
    /// </summary>
    public bool HasZip64Locator { get; }

    public ByteRange Range => range;

    /// <summary>Reads the central directory, or throws <see cref="InvalidDataException"/> when the bytes are not a zip archive.</summary>
    public static ZipDirectory Read(ByteRange range)
    {
        long size = range.Length;
        if (size < EndRecordLength)
        {
            throw new InvalidDataException("The file is not a zip archive: it is too small.");
        }

        // An archive without a comment ends with its end record. The 20 bytes before it would hold a ZIP64 locator.
        // Reading only these bytes first keeps the open from touching the data of the parts.
        int tailLength = (int)Math.Min(size, EndRecordLength + 20);
        long tailStart = size - tailLength;
        byte[] tail = range.Read(tailStart, tailLength);
        int end = tailLength - EndRecordLength;
        if (U32(tail, end) != EndSignature || U16(tail, end + 20) != 0)
        {
            // The archive has a comment, of up to 65,535 bytes: search the end record backwards.
            tailLength = (int)Math.Min(size, EndRecordLength + 0xFFFF + 20);
            tailStart = size - tailLength;
            tail = range.Read(tailStart, tailLength);
            end = -1;
            for (int i = tailLength - EndRecordLength; i >= 0; i--)
            {
                if (U32(tail, i) == EndSignature && i + EndRecordLength + U16(tail, i + 20) <= tailLength)
                {
                    end = i;
                    break;
                }
            }

            if (end < 0)
            {
                throw new InvalidDataException("The file is not a zip archive: it has no end of central directory record.");
            }
        }

        int diskEntries = U16(tail, end + 8);
        int totalEntries = U16(tail, end + 10);
        long directorySize = U32(tail, end + 12);
        long directoryOffset = U32(tail, end + 16);
        bool locator = end >= 20 && U32(tail, end - 20) == Zip64LocatorSignature;
        if (diskEntries == 0xFFFF || totalEntries == 0xFFFF || directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF)
        {
            return new ZipDirectory(range, Array.Empty<ZipEntry>(), true, locator);
        }

        if (directoryOffset + directorySize > tailStart + end)
        {
            throw new InvalidDataException("The central directory of the zip archive is outside the file.");
        }

        byte[] directory = range.Read(directoryOffset, (int)directorySize);
        var entries = new List<ZipEntry>(totalEntries);
        int p = 0;
        for (int n = 0; n < totalEntries; n++)
        {
            if (p + 46 > directory.Length || U32(directory, p) != CentralSignature)
            {
                throw new InvalidDataException("The central directory of the zip archive is damaged.");
            }

            int flags = U16(directory, p + 8);
            int method = U16(directory, p + 10);
            long compressedSize = U32(directory, p + 20);
            long uncompressedSize = U32(directory, p + 24);
            int nameLength = U16(directory, p + 28);
            int extraLength = U16(directory, p + 30);
            int commentLength = U16(directory, p + 32);
            int diskStart = U16(directory, p + 34);
            long localOffset = U32(directory, p + 42);
            if (p + 46 + nameLength > directory.Length)
            {
                throw new InvalidDataException("The central directory of the zip archive is damaged.");
            }

            string name = (flags & 0x800) != 0
                ? Encoding.UTF8.GetString(directory, p + 46, nameLength)
                : Latin1(directory, p + 46, nameLength);
            bool needsZip64 = compressedSize == 0xFFFFFFFF || uncompressedSize == 0xFFFFFFFF || localOffset == 0xFFFFFFFF || diskStart == 0xFFFF;
            bool hasZip64Extra = HasExtraField(directory, p + 46 + nameLength, Math.Min(extraLength, directory.Length - (p + 46 + nameLength)), 0x0001);
            entries.Add(new ZipEntry(name, method, flags, compressedSize, uncompressedSize, localOffset, needsZip64, hasZip64Extra));
            p += 46 + nameLength + extraLength + commentLength;
        }

        return new ZipDirectory(range, entries, false, locator);
    }

    public ZipEntry? Find(string name) => byName.TryGetValue(name, out ZipEntry? entry) ? entry : null;

    /// <summary>Finds an entry by name, and when no name is equal, by a name that differs only in case.</summary>
    public ZipEntry? FindLoosely(string name)
    {
        ZipEntry? entry = Find(name);
        if (entry != null)
        {
            return entry;
        }

        foreach (ZipEntry candidate in Entries)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The position of the first byte of the data of an entry, after its local header.</summary>
    public long DataOffset(ZipEntry entry)
    {
        byte[] header = range.Read(entry.LocalHeaderOffset, 30);
        if (U32(header, 0) != LocalSignature)
        {
            throw new InvalidDataException($"The local header of the entry {entry.Name} is damaged.");
        }

        return entry.LocalHeaderOffset + 30 + U16(header, 26) + U16(header, 28);
    }

    /// <summary>The range of the bytes of a stored entry, which can be read in place.</summary>
    public ByteRange StoredRange(ZipEntry entry) => range.Slice(DataOffset(entry), entry.CompressedSize);

    /// <summary>
    /// The content of an entry, inflated when it is deflated. Throws <see cref="NotSupportedException"/> for an entry
    /// that this reader cannot read, and <see cref="InvalidDataException"/> for damaged data or data beyond <paramref name="maxSize"/>.
    /// </summary>
    public byte[] ReadBytes(ZipEntry entry, long maxSize)
    {
        if (entry.NeedsZip64)
        {
            throw new NotSupportedException($"The entry {entry.Name} needs ZIP64.");
        }

        if (entry.IsEncrypted)
        {
            throw new NotSupportedException($"The entry {entry.Name} is encrypted.");
        }

        if (!entry.IsSupportedMethod)
        {
            throw new NotSupportedException($"The entry {entry.Name} uses compression method {entry.Method}, which is neither stored nor deflate.");
        }

        if (entry.CompressedSize > maxSize || entry.CompressedSize > int.MaxValue)
        {
            throw new InvalidDataException($"The entry {entry.Name} is larger than {maxSize} bytes.");
        }

        long start = DataOffset(entry);
        byte[] data = range.Read(start, (int)entry.CompressedSize);
        return entry.IsStored ? data : Inflate(data, entry.UncompressedSize, maxSize, entry.Name);
    }

    private static byte[] Inflate(byte[] compressed, long expected, long maxSize, string name)
    {
        int capacity = (int)Math.Min(Math.Min(expected, maxSize), int.MaxValue - 64);
        using var input = new MemoryStream(compressed, false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(capacity);
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > maxSize)
                {
                    throw new InvalidDataException($"The entry {name} inflates to more than {maxSize} bytes.");
                }

                output.Write(buffer, 0, read);
            }
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            throw new InvalidDataException($"The deflated data of the entry {name} is damaged: {e.Message}", e);
        }

        // The buffer is returned without a copy when the size in the directory was right, which is the common case.
        return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
    }

    /// <summary>Whether the extra data of an entry has a block with the header id <paramref name="headerId"/>.</summary>
    private static bool HasExtraField(byte[] data, int start, int length, int headerId)
    {
        int p = start;
        int end = start + length;
        while (p + 4 <= end)
        {
            if (U16(data, p) == headerId)
            {
                return true;
            }

            p += 4 + U16(data, p + 2);
        }

        return false;
    }

    private static int U16(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8);

    private static uint U32(byte[] data, int offset) => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static string Latin1(byte[] data, int offset, int length)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = (char)data[offset + i];
        }

        return new string(chars);
    }
}
