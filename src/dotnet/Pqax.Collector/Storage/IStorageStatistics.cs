namespace Pqax.Collector;

/// <summary>
/// The requests and the bytes that a storage has served. A collection takes its <c>requestCount</c> and
/// <c>bytesRead</c> from here when the storage implements this interface, because only the storage sees retries and
/// the pages of a listing. Otherwise the collector counts the calls it makes.
/// </summary>
public interface IStorageStatistics
{
    /// <summary>The requests made, including retries and the pages of a listing.</summary>
    long RequestCount { get; }

    /// <summary>The bytes received: the ranges read, and for a remote storage the bodies of the listings.</summary>
    long BytesRead { get; }
}
