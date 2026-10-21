using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// A storage that lists the files under a folder at any depth in one request, or in the pages of one listing, so that
/// the collector does not list each folder in turn. <see cref="LocalStorage"/> and <see cref="OneLakeStorage"/>
/// implement it, and <see cref="CountingStorage"/> forwards it. For a storage that does not,
/// <see cref="StorageListing.ListFilesAsync"/> walks the folders with <see cref="IStorage.ListAsync"/>.
/// </summary>
internal interface IRecursiveStorage
{
    /// <summary>Lists the files under a folder, at any depth.</summary>
    /// <param name="path">The folder, relative to the root of the storage.</param>
    /// <param name="excludedFolders">
    /// The names of folders directly inside <paramref name="path"/> whose content is left out, such as <c>_delta_log</c>.
    /// A storage that cannot leave them out of the request drops their files from the result.
    /// </param>
    /// <param name="cancellation">Cancels the request.</param>
    /// <returns>The files, with paths relative to the root of the storage, sorted by path. Empty when the folder does not exist.</returns>
    /// <exception cref="StorageException">Access is denied, or the request failed.</exception>
    Task<IReadOnlyList<StorageEntry>> ListFilesAsync(string path, IReadOnlyCollection<string> excludedFolders, CancellationToken cancellation);
}
