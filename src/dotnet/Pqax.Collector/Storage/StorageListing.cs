using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>The recursive listing of a folder, over any <see cref="IStorage"/>.</summary>
internal static class StorageListing
{
    /// <summary>
    /// Lists the files under a folder, at any depth: with one recursive listing when the storage implements
    /// <see cref="IRecursiveStorage"/>, otherwise by listing each folder with <see cref="IStorage.ListAsync"/>.
    /// </summary>
    /// <param name="storage">The storage.</param>
    /// <param name="path">The folder, relative to the root of the storage.</param>
    /// <param name="excludedFolders">The names of folders directly inside <paramref name="path"/> whose content is left out.</param>
    /// <param name="cancellation">Cancels the requests.</param>
    /// <returns>The files, with paths relative to the root of the storage, sorted by path.</returns>
    /// <exception cref="StorageException">Access is denied, or a request failed.</exception>
    public static Task<IReadOnlyList<StorageEntry>> ListFilesAsync(IStorage storage, string path, IReadOnlyCollection<string> excludedFolders, CancellationToken cancellation) =>
        storage is IRecursiveStorage recursive
            ? recursive.ListFilesAsync(path, excludedFolders, cancellation)
            : WalkAsync(storage, path, excludedFolders, cancellation);

    /// <summary>Lists the files under a folder by listing each folder in turn: one request for each folder, and none for an excluded one.</summary>
    /// <param name="storage">The storage.</param>
    /// <param name="path">The folder, relative to the root of the storage.</param>
    /// <param name="excludedFolders">The names of folders directly inside <paramref name="path"/> that are not listed.</param>
    /// <param name="cancellation">Cancels the requests.</param>
    /// <returns>The files, with paths relative to the root of the storage, sorted by path.</returns>
    public static async Task<IReadOnlyList<StorageEntry>> WalkAsync(IStorage storage, string path, IReadOnlyCollection<string> excludedFolders, CancellationToken cancellation)
    {
        string root = path.Trim('/');
        var files = new List<StorageEntry>();
        var folders = new Queue<string>();
        folders.Enqueue(root);
        while (folders.Count > 0)
        {
            string folder = folders.Dequeue();
            foreach (StorageEntry entry in await storage.ListAsync(folder, cancellation).ConfigureAwait(false))
            {
                if (!entry.IsDirectory)
                {
                    files.Add(entry);
                }
                else if (!(folder == root && Contains(excludedFolders, entry.Name)))
                {
                    folders.Enqueue(entry.Path.Trim('/'));
                }
            }
        }

        return Sorted(files);
    }

    /// <summary>Whether a file is inside one of the excluded folders of the folder listed.</summary>
    /// <param name="root">The folder listed, relative to the root of the storage, without a leading or trailing slash.</param>
    /// <param name="filePath">The path of the file, relative to the root of the storage.</param>
    /// <param name="excludedFolders">The names of folders directly inside <paramref name="root"/>.</param>
    /// <returns>True when the first segment of the path of the file below <paramref name="root"/> is an excluded folder.</returns>
    public static bool IsExcluded(string root, string filePath, IReadOnlyCollection<string> excludedFolders)
    {
        if (root.Length > 0 && !(filePath.Length > root.Length && filePath[root.Length] == '/' && filePath.StartsWith(root, StringComparison.Ordinal)))
        {
            return false;
        }

        string relative = root.Length == 0 ? filePath : filePath.Substring(root.Length + 1);
        int slash = relative.IndexOf('/');
        return slash > 0 && Contains(excludedFolders, relative.Substring(0, slash));
    }

    /// <summary>Sorts the files by path, ordinal.</summary>
    /// <param name="files">The files.</param>
    /// <returns>The same list, sorted.</returns>
    public static IReadOnlyList<StorageEntry> Sorted(List<StorageEntry> files)
    {
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return files;
    }

    private static bool Contains(IReadOnlyCollection<string> names, string name)
    {
        foreach (string excluded in names)
        {
            if (string.Equals(excluded, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
