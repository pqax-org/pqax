namespace Pqax;

/// <summary>An entry of the central directory of a zip archive.</summary>
internal sealed class ZipEntry
{
    public ZipEntry(string name, int method, int flags, long compressedSize, long uncompressedSize, long localHeaderOffset, bool needsZip64, bool hasZip64Extra)
    {
        Name = name;
        Method = method;
        Flags = flags;
        CompressedSize = compressedSize;
        UncompressedSize = uncompressedSize;
        LocalHeaderOffset = localHeaderOffset;
        NeedsZip64 = needsZip64;
        HasZip64Extra = hasZip64Extra;
    }

    public string Name { get; }

    /// <summary>The compression method: 0 is stored and 8 is deflate.</summary>
    public int Method { get; }

    /// <summary>The general purpose bit flags.</summary>
    public int Flags { get; }

    public long CompressedSize { get; }

    public long UncompressedSize { get; }

    public long LocalHeaderOffset { get; }

    /// <summary>Whether a size or the offset of the entry is in a ZIP64 extra field, so that the entry cannot be read without ZIP64.</summary>
    public bool NeedsZip64 { get; }

    /// <summary>
    /// Whether the central directory gives the entry a ZIP64 extra field, header 0x0001. A reader can still read an
    /// entry whose 32-bit sizes are real, but the archive uses ZIP64, which the format does not allow.
    /// </summary>
    public bool HasZip64Extra { get; }

    /// <summary>Whether bit 0 (encrypted) or bit 6 (strong encryption) is set.</summary>
    public bool IsEncrypted => (Flags & 0x41) != 0;

    public bool IsStored => Method == 0;

    public bool IsSupportedMethod => Method == 0 || Method == 8;
}
