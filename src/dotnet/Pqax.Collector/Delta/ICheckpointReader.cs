using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// Reads the actions of a Delta checkpoint written in Parquet: a classic checkpoint, one part of a multi-part
/// checkpoint, a v2 checkpoint in Parquet, or a sidecar file. A checkpoint holds nested data, which needs a complete
/// Parquet reader, so this lives outside the collector: <c>Pqax.Collector.ParquetNet</c> implements it with Parquet.Net.
/// </summary>
/// <remarks>
/// An implementation returns the <see cref="AddAction"/>, <see cref="RemoveAction"/>, <see cref="MetadataAction"/>,
/// <see cref="ProtocolAction"/>, <see cref="DomainMetadataAction"/>, <see cref="SidecarAction"/>, and
/// <see cref="CheckpointMetadataAction"/> rows of the file, in any order, and reads the file through the storage it is
/// given, so that the collection counts the requests. It throws when it cannot read the file; the collector turns the
/// exception into an issue and goes on.
/// </remarks>
public interface ICheckpointReader
{
    /// <summary>Reads the actions of one checkpoint file.</summary>
    /// <param name="storage">The storage to read the file from.</param>
    /// <param name="path">The path of the file, relative to the root of the storage.</param>
    /// <param name="cancellation">Cancels the reads.</param>
    /// <returns>The actions of the file.</returns>
    Task<IReadOnlyList<DeltaAction>> ReadAsync(IStorage storage, string path, CancellationToken cancellation);
}
