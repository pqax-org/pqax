using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>Helpers over <see cref="IStorage"/>.</summary>
public static class StorageExtensions
{
    /// <summary>The bytes asked for in one request when the size of a file is not known: 64 MB.</summary>
    public const int ReadAllChunk = 64 * 1024 * 1024;

    /// <summary>
    /// Reads a whole file whose size is not known. It asks for <see cref="ReadAllChunk"/> bytes at a time and stops at the
    /// first read that returns fewer, so a file smaller than that takes one request.
    /// </summary>
    /// <param name="storage">The storage.</param>
    /// <param name="path">The file, relative to the root of the storage.</param>
    /// <param name="cancellation">Cancels the requests.</param>
    /// <returns>The content of the file.</returns>
    /// <exception cref="StorageException">The file does not exist, access is denied, or a request failed.</exception>
    public static async Task<byte[]> ReadAllAsync(this IStorage storage, string path, CancellationToken cancellation)
    {
        if (storage == null)
        {
            throw new ArgumentNullException(nameof(storage));
        }

        byte[] first = await storage.ReadRangeAsync(path, 0, ReadAllChunk, cancellation).ConfigureAwait(false);
        if (first.Length < ReadAllChunk)
        {
            return first;
        }

        var chunks = new List<byte[]> { first };
        long total = first.Length;
        while (true)
        {
            byte[] next = await storage.ReadRangeAsync(path, total, ReadAllChunk, cancellation).ConfigureAwait(false);
            if (next.Length > 0)
            {
                chunks.Add(next);
                total += next.Length;
            }

            if (next.Length < ReadAllChunk)
            {
                break;
            }

            if (total > int.MaxValue - ReadAllChunk)
            {
                throw new StorageException("A file is too large to be read whole.");
            }
        }

        var all = new byte[total];
        int position = 0;
        foreach (byte[] chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, all, position, chunk.Length);
            position += chunk.Length;
        }

        return all;
    }

    /// <summary>Reads a whole file whose size is known, in one request.</summary>
    /// <param name="storage">The storage.</param>
    /// <param name="path">The file, relative to the root of the storage.</param>
    /// <param name="size">The size of the file, as a listing or the table format gives it.</param>
    /// <param name="cancellation">Cancels the request.</param>
    /// <returns>The content of the file. Shorter than <paramref name="size"/> when the file is shorter.</returns>
    /// <exception cref="StorageException">The file does not exist, access is denied, or the request failed.</exception>
    public static Task<byte[]> ReadAllAsync(this IStorage storage, string path, long size, CancellationToken cancellation)
    {
        if (storage == null)
        {
            throw new ArgumentNullException(nameof(storage));
        }

        if (size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The size is negative.");
        }

        if (size > int.MaxValue)
        {
            throw new StorageException("A file is too large to be read whole.");
        }

        return size == 0 ? Task.FromResult(new byte[0]) : storage.ReadRangeAsync(path, 0, (int)size, cancellation);
    }
}
