using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// Reads the footer of a Parquet file, and nothing else. The size of the file comes from the table format, so the end
/// of the file is read in one request: the last <see cref="TailBytes"/> bytes, which hold the footer in the common case.
/// A second request reads the start of a longer footer. Nothing is read from the start of the file.
/// </summary>
public sealed class FooterReader
{
    /// <summary>The bytes read from the end of a file in the first request, unless the options say otherwise: 32 KB.</summary>
    public const int DefaultTailBytes = 32768;

    /// <summary>The longest footer that the reader accepts: 64 MB.</summary>
    public const int MaxFooterLength = 64 * 1024 * 1024;

    /// <summary>
    /// The most elements of a list or a map of a footer that the parser accepts, such as the schema, the row groups, or
    /// the key-value metadata: 1,000,000, the limit of a Thrift container in the Parquet reader of Arrow C++, so a file
    /// that pyarrow reads is never refused for it.
    /// </summary>
    public const int MaxListLength = 1_000_000;

    /// <summary>
    /// The most column chunks of a footer that the parser accepts, over all its row groups, and the most sorting columns:
    /// 4,194,304. A footer of 64 MB from a writer holds fewer, about 40 bytes each; the limit bounds the memory of a
    /// footer of empty chunks or empty sorting columns, one byte each on the wire and up to some 300 bytes in memory.
    /// </summary>
    public const int MaxColumnChunks = 4_194_304;

    private readonly IStorage storage;

    /// <summary>Creates a reader.</summary>
    /// <param name="storage">The storage to read from.</param>
    /// <param name="tailBytes">The bytes to read from the end of a file in the first request, at least 12.</param>
    /// <param name="includeDataValues">Whether to keep the bounds of the statistics.</param>
    public FooterReader(IStorage storage, int tailBytes = DefaultTailBytes, bool includeDataValues = false)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        if (tailBytes < 12)
        {
            throw new ArgumentOutOfRangeException(nameof(tailBytes), tailBytes, "The tail must hold at least the 12 bytes around a footer.");
        }

        TailBytes = tailBytes;
        IncludeDataValues = includeDataValues;
    }

    /// <summary>The bytes read from the end of a file in the first request.</summary>
    public int TailBytes { get; }

    /// <summary>Whether the bounds of the statistics are kept.</summary>
    public bool IncludeDataValues { get; }

    /// <summary>Reads and parses the footer of a file.</summary>
    /// <param name="path">The file, relative to the root of the storage.</param>
    /// <param name="fileSize">The size of the file, from the table format or a listing.</param>
    /// <param name="cancellation">Cancels the reads.</param>
    /// <returns>The footer, with <see cref="ParquetFooter.FooterLength"/> and <see cref="ParquetFooter.RequestCount"/> set.</returns>
    /// <exception cref="ParquetFooterException">The file is not Parquet, its footer is encrypted, too large, or invalid.</exception>
    /// <exception cref="StorageException">A read failed.</exception>
    public Task<ParquetFooter> ReadAsync(string path, long fileSize, CancellationToken cancellation)
        => ReadAsync(path, fileSize, TailBytes, cancellation);

    /// <summary>Reads and parses the footer of a data file, with a tail chosen by the caller for the first request.</summary>
    /// <param name="path">The path of the file in the storage.</param>
    /// <param name="fileSize">The size of the file, from the table format.</param>
    /// <param name="tailBytes">The bytes to read from the end of the file in the first request, at least 12.</param>
    /// <param name="cancellation">Stops the read.</param>
    /// <returns>The footer, with <see cref="ParquetFooter.FooterLength"/> and <see cref="ParquetFooter.RequestCount"/> set.</returns>
    public async Task<ParquetFooter> ReadAsync(string path, long fileSize, int tailBytes, CancellationToken cancellation)
    {
        if (tailBytes < 12)
        {
            throw new ArgumentOutOfRangeException(nameof(tailBytes), tailBytes, "The tail must hold at least the 12 bytes around a footer.");
        }

        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (fileSize < 12)
        {
            throw new ParquetFooterException(IssueCodes.FooterInvalid, "The file is too small to be a Parquet file: " + Text(fileSize) + " bytes.");
        }

        int first = (int)Math.Min(tailBytes, fileSize);
        byte[] tail = await storage.ReadRangeAsync(path, fileSize - first, first, cancellation).ConfigureAwait(false);
        if (tail.Length != first)
        {
            throw new ParquetFooterException(IssueCodes.FooterInvalid, "The file is shorter than the size that the table format gives.");
        }

        int requests = 1;
        if (tail[first - 4] == (byte)'P' && tail[first - 3] == (byte)'A' && tail[first - 2] == (byte)'R')
        {
            if (tail[first - 1] == (byte)'E')
            {
                throw new ParquetFooterException(IssueCodes.FooterEncrypted, "The footer is encrypted.");
            }

            if (tail[first - 1] != (byte)'1')
            {
                throw new ParquetFooterException(IssueCodes.FooterInvalid, "The file does not end with the Parquet magic bytes.");
            }
        }
        else
        {
            throw new ParquetFooterException(IssueCodes.FooterInvalid, "The file does not end with the Parquet magic bytes.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(tail, first - 8, 4));
        if (length <= 0 || length > fileSize - 12)
        {
            throw new ParquetFooterException(IssueCodes.FooterInvalid, "The length of the footer, " + Text(length) + ", does not fit the file.");
        }

        if (length > MaxFooterLength)
        {
            throw new ParquetFooterException(IssueCodes.FooterTooLarge, "The footer takes " + Text(length) + " bytes, more than the limit of " + Text(MaxFooterLength) + ".");
        }

        byte[] buffer;
        int offset;
        if (length + 8 <= first)
        {
            buffer = tail;
            offset = first - 8 - length;
        }
        else
        {
            // The footer starts before the tail: read the missing part, and put the two together.
            int missing = length + 8 - first;
            byte[] head = await storage.ReadRangeAsync(path, fileSize - 8 - length, missing, cancellation).ConfigureAwait(false);
            requests++;
            if (head.Length != missing)
            {
                throw new ParquetFooterException(IssueCodes.FooterInvalid, "The file is shorter than the size that the table format gives.");
            }

            buffer = new byte[length];
            Buffer.BlockCopy(head, 0, buffer, 0, missing);
            Buffer.BlockCopy(tail, 0, buffer, missing, length - missing);
            offset = 0;
        }

        ParquetFooter footer = FooterParser.Parse(buffer, offset, length, IncludeDataValues);
        footer.FooterLength = length;
        footer.RequestCount = requests;
        return footer;
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
