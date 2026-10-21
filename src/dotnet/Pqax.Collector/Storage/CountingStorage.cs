using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// Wraps the storage of a collection: it limits the requests in flight to the parallelism of the options, and it
/// counts the calls and the bytes, for a storage that does not count them itself. A recursive listing is one call when
/// the inner storage implements <see cref="IRecursiveStorage"/>, and one call for each folder otherwise.
/// </summary>
internal sealed class CountingStorage : IStorage, IRecursiveStorage, IDisposable
{
    private readonly IStorage inner;
    private readonly SemaphoreSlim gate;
    private long requests;
    private long bytes;

    public CountingStorage(IStorage inner, int parallelism)
    {
        this.inner = inner;
        gate = new SemaphoreSlim(parallelism, parallelism);
    }

    public long RequestCount => Interlocked.Read(ref requests);

    public long BytesRead => Interlocked.Read(ref bytes);

    public async Task<IReadOnlyList<StorageEntry>> ListAsync(string path, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref requests);
            return await inner.ListAsync(path, cancellation).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<StorageEntry>> ListFilesAsync(string path, IReadOnlyCollection<string> excludedFolders, CancellationToken cancellation)
    {
        if (!(inner is IRecursiveStorage recursive))
        {
            // Each folder goes through ListAsync, so each one is a call, counted and limited as any other.
            return await StorageListing.WalkAsync(this, path, excludedFolders, cancellation).ConfigureAwait(false);
        }

        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref requests);
            return await recursive.ListFilesAsync(path, excludedFolders, cancellation).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref requests);
            byte[] result = await inner.ReadRangeAsync(path, offset, length, cancellation).ConfigureAwait(false);
            Interlocked.Add(ref bytes, result.Length);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
