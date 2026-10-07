using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// A folder on disk with the layout of a OneLake workspace: <c>&lt;item&gt;.&lt;Kind&gt;/Tables/[&lt;schema&gt;/]&lt;table&gt;/...</c>,
/// as <c>conformance/fixtures/lakehouse</c>. It reads only, and it counts its requests and bytes as a remote storage would.
/// </summary>
public sealed class LocalStorage : IStorage, IStorageStatistics
{
    private static readonly IReadOnlyList<StorageEntry> NoEntries = new StorageEntry[0];

    private long requests;
    private long bytes;

    /// <summary>Creates a storage over a folder, which plays the role of the workspace.</summary>
    /// <param name="root">The folder. It does not have to exist: a listing of a missing folder is empty.</param>
    public LocalStorage(string root)
    {
        if (root == null)
        {
            throw new ArgumentNullException(nameof(root));
        }

        Root = System.IO.Path.GetFullPath(root);
    }

    /// <summary>The full path of the folder.</summary>
    public string Root { get; }

    /// <inheritdoc/>
    public long RequestCount => Interlocked.Read(ref requests);

    /// <inheritdoc/>
    public long BytesRead => Interlocked.Read(ref bytes);

    /// <inheritdoc/>
    public Task<IReadOnlyList<StorageEntry>> ListAsync(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string full = Resolve(path);
        Interlocked.Increment(ref requests);
        var directory = new DirectoryInfo(full);
        if (!directory.Exists)
        {
            return Task.FromResult(NoEntries);
        }

        string prefix = Normalize(path);
        var entries = new List<StorageEntry>();
        try
        {
            foreach (FileSystemInfo info in directory.EnumerateFileSystemInfos())
            {
                string entryPath = prefix.Length == 0 ? info.Name : prefix + "/" + info.Name;
                if (info is FileInfo file)
                {
                    entries.Add(new StorageEntry(entryPath, false, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
                }
                else
                {
                    entries.Add(new StorageEntry(entryPath, true, null, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
                }
            }
        }
        catch (UnauthorizedAccessException e)
        {
            throw new StorageException("Access to a local folder was denied.", 403, e);
        }
        catch (IOException e)
        {
            throw new StorageException("A local folder could not be listed: " + e.GetType().Name + ".", null, e);
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return Task.FromResult<IReadOnlyList<StorageEntry>>(entries);
    }

    /// <inheritdoc/>
    public async Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellation)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset is negative.");
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length is negative.");
        }

        cancellation.ThrowIfCancellationRequested();
        string full = Resolve(path);
        Interlocked.Increment(ref requests);
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            long available = stream.Length - offset;
            if (available <= 0 || length == 0)
            {
                return new byte[0];
            }

            int count = (int)Math.Min(length, available);
            var buffer = new byte[count];
            stream.Position = offset;
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer, read, count - read, cancellation).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (read < count)
            {
                Array.Resize(ref buffer, read);
            }

            Interlocked.Add(ref bytes, read);
            return buffer;
        }
        catch (FileNotFoundException e)
        {
            throw new StorageException("A local file does not exist.", 404, e);
        }
        catch (DirectoryNotFoundException e)
        {
            throw new StorageException("A local file does not exist.", 404, e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new StorageException("Access to a local file was denied.", 403, e);
        }
        catch (IOException e)
        {
            throw new StorageException("A local file could not be read: " + e.GetType().Name + ".", null, e);
        }
    }

    private static string Normalize(string path)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        return path.Trim('/');
    }

    private string Resolve(string path)
    {
        string normalized = Normalize(path);
        if (normalized.Length == 0)
        {
            return Root;
        }

        string[] segments = normalized.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.IndexOf('\\') >= 0 || System.IO.Path.IsPathRooted(segment))
            {
                throw new ArgumentException("A path of the storage has an empty, relative, or rooted segment.", nameof(path));
            }
        }

        string full = System.IO.Path.Combine(Root, string.Join(System.IO.Path.DirectorySeparatorChar.ToString(), segments));
        if (!full.StartsWith(Root, StringComparison.Ordinal))
        {
            throw new ArgumentException("A path of the storage leads outside its root.", nameof(path));
        }

        return full;
    }
}
