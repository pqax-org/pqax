using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// Read-only access to a storage that holds tables: a OneLake workspace, or a folder with the same layout. Paths are
/// relative to the root of the storage, use forward slashes, and have no leading slash.
/// </summary>
/// <remarks>
/// Every read of the collector goes through this interface, so that a collection can count its requests and the bytes
/// it read. An implementation never writes.
/// </remarks>
public interface IStorage
{
    /// <summary>Lists the entries of a folder, without recursion.</summary>
    /// <param name="path">The folder, relative to the root. An empty string is the root.</param>
    /// <param name="cancellation">Cancels the request.</param>
    /// <returns>The files and folders directly inside the folder, sorted by name. Empty when the folder does not exist.</returns>
    /// <exception cref="StorageException">Access is denied, or the request failed.</exception>
    Task<IReadOnlyList<StorageEntry>> ListAsync(string path, CancellationToken cancellation);

    /// <summary>Reads a range of bytes of a file.</summary>
    /// <param name="path">The file, relative to the root.</param>
    /// <param name="offset">The position of the first byte.</param>
    /// <param name="length">The number of bytes to read.</param>
    /// <param name="cancellation">Cancels the request.</param>
    /// <returns>
    /// The bytes. Fewer than <paramref name="length"/> only when the file ends before, and none when
    /// <paramref name="offset"/> is at or past the end of the file.
    /// </returns>
    /// <exception cref="StorageException">The file does not exist, access is denied, or the request failed.</exception>
    Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellation);
}
