using System;
using System.IO;

namespace Pqax;

/// <summary>
/// Passes writes to the stream of a zip entry and ignores <see cref="Flush"/>. The JSON writer flushes its buffer often
/// while it writes a large part, and a flush of a deflate stream would end a block each time.
/// </summary>
internal sealed class NonFlushingStream : Stream
{
    private readonly Stream inner;

    public NonFlushingStream(Stream inner)
    {
        this.inner = inner;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

#if NET8_0_OR_GREATER
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
#endif
}
