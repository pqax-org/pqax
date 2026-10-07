using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// Wraps the storage of a collection: it limits the requests in flight to the parallelism of the options, and it
/// counts the calls and the bytes, for a storage that does not count them itself.
/// </summary>
internal sealed class CountingStorage : IStorage, IDisposable
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
