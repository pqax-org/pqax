using System;
using System.IO;

namespace Pqax;

/// <summary>
/// A range of bytes that holds a zip archive: a whole stream, or the piece of a VPAX file where a stored part is.
/// Reads are serialized on a lock that every range over the same stream shares.
/// </summary>
internal sealed class ByteRange
{
    private readonly Stream stream;
    private readonly long offset;
    private readonly object sync;

    public ByteRange(Stream stream, long offset, long length, object sync)
    {
        this.stream = stream;
        this.offset = offset;
        this.sync = sync;
        Length = length;
    }

    public long Length { get; }

    /// <summary>The range that starts at <paramref name="start"/> and has <paramref name="length"/> bytes, on the same stream.</summary>
    public ByteRange Slice(long start, long length)
    {
        if (start < 0 || length < 0 || start + length > Length)
        {
            throw new InvalidDataException("The range is outside the archive.");
        }

        return new ByteRange(stream, offset + start, length, sync);
    }

    /// <summary>Reads <paramref name="count"/> bytes at <paramref name="position"/>, or throws when the range ends before.</summary>
    public byte[] Read(long position, int count)
    {
        if (position < 0 || count < 0 || position + count > Length)
        {
            throw new InvalidDataException("The archive ends before the data that its directory points to.");
        }

        var buffer = new byte[count];
        lock (sync)
        {
            stream.Seek(offset + position, SeekOrigin.Begin);
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0)
                {
                    throw new InvalidDataException("The archive ends before the data that its directory points to.");
                }

                total += read;
            }
        }

        return buffer;
    }
}
