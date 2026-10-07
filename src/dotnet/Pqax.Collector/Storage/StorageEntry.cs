using System;

namespace Pqax.Collector;

/// <summary>A file or a folder that a listing of an <see cref="IStorage"/> returned.</summary>
public sealed class StorageEntry
{
    /// <summary>Creates an entry.</summary>
    /// <param name="path">The path relative to the root of the storage, with forward slashes.</param>
    /// <param name="isDirectory">Whether the entry is a folder.</param>
    /// <param name="size">The size of a file in bytes. Null for a folder, or when the storage does not say.</param>
    /// <param name="lastModified">When the entry was last modified, when the storage says.</param>
    public StorageEntry(string path, bool isDirectory, long? size, DateTimeOffset? lastModified)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        IsDirectory = isDirectory;
        Size = size;
        LastModified = lastModified;
        int slash = path.LastIndexOf('/');
        Name = slash < 0 ? path : path.Substring(slash + 1);
    }

    /// <summary>The path relative to the root of the storage, with forward slashes.</summary>
    public string Path { get; }

    /// <summary>The last segment of the path.</summary>
    public string Name { get; }

    /// <summary>Whether the entry is a folder.</summary>
    public bool IsDirectory { get; }

    /// <summary>The size of a file in bytes. Null for a folder, or when the storage does not say.</summary>
    public long? Size { get; }

    /// <summary>When the entry was last modified, in UTC, when the storage says.</summary>
    public DateTimeOffset? LastModified { get; }

    /// <inheritdoc/>
    public override string ToString() => IsDirectory ? Path + "/" : Path;
}
