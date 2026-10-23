using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// Finds the tables of the items of a OneLake workspace, or of a folder with the same layout:
/// <c>&lt;item&gt;.&lt;Kind&gt;/Tables/&lt;table&gt;</c> or <c>&lt;item&gt;.&lt;Kind&gt;/Tables/&lt;schema&gt;/&lt;table&gt;</c>;
/// and the tables under a folder of any other layout, such as a container of Azure Data Lake Storage.
/// </summary>
public static class TableDiscovery
{
    /// <summary>The levels below a folder that <see cref="DiscoverFolderAsync(IStorage, string, CancellationToken)"/> tests for a table.</summary>
    public const int DefaultFolderDepth = 4;

    private static readonly string[][] Kinds =
    {
        new[] { ".Lakehouse", SourceKinds.OneLakeLakehouse },
        new[] { ".Warehouse", SourceKinds.OneLakeWarehouse },
        new[] { ".MirroredDatabase", SourceKinds.OneLakeMirroredDatabase },
        new[] { ".SQLDatabase", SourceKinds.OneLakeSqlDatabase },
    };

    /// <summary>Lists the items of the root of a storage that can hold tables: lakehouses, warehouses, mirrored databases, and SQL databases. One request.</summary>
    /// <param name="storage">The storage, whose root is a workspace.</param>
    /// <param name="cancellation">Cancels the request.</param>
    /// <returns>The names of the item folders, such as <c>Sales.Lakehouse</c>, sorted.</returns>
    public static async Task<IReadOnlyList<string>> ListItemsAsync(IStorage storage, CancellationToken cancellation)
    {
        if (storage == null)
        {
            throw new ArgumentNullException(nameof(storage));
        }

        var items = new List<string>();
        foreach (StorageEntry entry in await storage.ListAsync(string.Empty, cancellation).ConfigureAwait(false))
        {
            if (entry.IsDirectory && KindIndex(entry.Name) >= 0)
            {
                items.Add(entry.Name);
            }
        }

        return items;
    }

    /// <summary>
    /// Finds the tables of an item. In a warehouse, every folder under <c>Tables</c> is a schema. In other items, a folder
    /// under <c>Tables</c> is a table when it holds <c>_delta_log</c>, and a schema otherwise. One request for
    /// <c>Tables</c> and one for each folder under it.
    /// </summary>
    /// <remarks>
    /// On a <see cref="OneLakeStorage"/> or a <see cref="LocalStorage"/>, each table gets the location of its folder, and
    /// the location and the identifiers of its source: the ABFSS URI of the item on OneLake, with <c>workspaceId</c> and
    /// <c>itemId</c> for what the caller gave as ids, and full paths on disk. Another storage gives no location.
    /// </remarks>
    /// <param name="storage">The storage, whose root is a workspace.</param>
    /// <param name="item">The item folder, such as <c>Sales.Lakehouse</c>, or the id of an item.</param>
    /// <param name="cancellation">Cancels the requests.</param>
    /// <returns>The tables, in the order of their folders. Empty when the item has no <c>Tables</c> folder.</returns>
    public static async Task<IReadOnlyList<TableLocation>> DiscoverAsync(IStorage storage, string item, CancellationToken cancellation)
    {
        if (storage == null)
        {
            throw new ArgumentNullException(nameof(storage));
        }

        if (string.IsNullOrEmpty(item))
        {
            throw new ArgumentException("The item is missing.", nameof(item));
        }

        item = item.Trim('/');
        int kindIndex = KindIndex(item);
        string kind = kindIndex >= 0 ? Kinds[kindIndex][1] : SourceKinds.OneLakeItem;
        string name = kindIndex >= 0 ? item.Substring(0, item.Length - Kinds[kindIndex][0].Length) : item;
        string tablesPath = item + "/Tables";

        // The locations start from the item as the caller gave it, so that a table location extends the source location.
        var located = storage as ILocatedStorage;
        string? sourceLocation = located?.LocationOf(item);
        IReadOnlyDictionary<string, string>? identifiers = located?.IdentifiersOf(item);
        var tables = new List<TableLocation>();
        foreach (StorageEntry entry in await storage.ListAsync(tablesPath, cancellation).ConfigureAwait(false))
        {
            if (!entry.IsDirectory)
            {
                continue;
            }

            IReadOnlyList<StorageEntry> children = await storage.ListAsync(entry.Path, cancellation).ConfigureAwait(false);
            bool isTable = false;
            if (kind != SourceKinds.OneLakeWarehouse)
            {
                foreach (StorageEntry child in children)
                {
                    if (child.IsDirectory && child.Name == "_delta_log")
                    {
                        isTable = true;
                        break;
                    }
                }
            }

            if (isTable)
            {
                string? location = located?.LocationOf(tablesPath + "/" + entry.Name);
                tables.Add(new TableLocation(kind, name, null, entry.Name, entry.Path, location, sourceLocation, identifiers));
                continue;
            }

            foreach (StorageEntry child in children)
            {
                if (child.IsDirectory)
                {
                    string? location = located?.LocationOf(tablesPath + "/" + entry.Name + "/" + child.Name);
                    tables.Add(new TableLocation(kind, name, new[] { entry.Name }, child.Name, child.Path, location, sourceLocation, identifiers));
                }
            }
        }

        return tables;
    }

    /// <summary>
    /// Finds the tables under a folder of a storage that has no item layout, such as a container of Azure Data Lake
    /// Storage, down to <see cref="DefaultFolderDepth"/> levels below the folder.
    /// </summary>
    /// <param name="storage">The storage.</param>
    /// <param name="folder">The folder, relative to the root of the storage. An empty string is the root.</param>
    /// <param name="cancellation">Cancels the requests.</param>
    /// <returns>The tables, sorted by path.</returns>
    public static Task<IReadOnlyList<TableLocation>> DiscoverFolderAsync(IStorage storage, string folder, CancellationToken cancellation) =>
        DiscoverFolderAsync(storage, folder, DefaultFolderDepth, cancellation);

    /// <summary>
    /// Finds the tables under a folder of a storage that has no item layout, such as a container of Azure Data Lake
    /// Storage. One request for each folder walked.
    /// </summary>
    /// <remarks>
    /// The walk starts at the folder, which may itself be a table, and goes down depth first. A folder that holds
    /// <c>_delta_log</c> is a Delta table, and the walk does not enter it. A folder with Parquet files and no subfolder is
    /// a Parquet table. Any other folder is walked while its depth is below <paramref name="maxDepth"/>. Folders and files
    /// whose name starts with <c>_</c> or <c>.</c> are hidden, as Spark treats them, and a folder whose name holds
    /// <c>=</c> is the partition of a folder of Parquet files, which this version does not read: none of them is
    /// walked. The tables share one source of the kind <see cref="SourceKinds.StorageFolder"/>, named after the folder,
    /// with the location of the folder and no identifiers; the folders between it and a table are the namespace.
    /// </remarks>
    /// <param name="storage">The storage.</param>
    /// <param name="folder">The folder, relative to the root of the storage. An empty string is the root.</param>
    /// <param name="maxDepth">The deepest level below the folder that is tested for a table. 0 tests the folder alone.</param>
    /// <param name="cancellation">Cancels the requests.</param>
    /// <returns>The tables, sorted by path. Empty when the folder does not exist.</returns>
    public static async Task<IReadOnlyList<TableLocation>> DiscoverFolderAsync(IStorage storage, string folder, int maxDepth, CancellationToken cancellation)
    {
        if (storage == null)
        {
            throw new ArgumentNullException(nameof(storage));
        }

        if (folder == null)
        {
            throw new ArgumentNullException(nameof(folder));
        }

        if (maxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "The depth is negative.");
        }

        folder = folder.Trim('/');
        int slash = folder.LastIndexOf('/');
        string sourceName = folder.Length > 0 ? folder.Substring(slash + 1) : RootName(storage);
        var located = storage as ILocatedStorage;
        string? sourceLocation = located?.LocationOf(folder);
        var tables = new List<TableLocation>();
        await WalkAsync(storage, folder, new List<string>(), maxDepth, sourceName, sourceLocation, tables, cancellation).ConfigureAwait(false);
        return tables;
    }

    /// <summary>Whether a file of a folder is a data file of a Parquet table: a visible file whose name ends with <c>.parquet</c>.</summary>
    internal static bool IsParquetFile(StorageEntry entry) =>
        !entry.IsDirectory && !IsHidden(entry.Name) && entry.Name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase);

    private static bool IsHidden(string name) => name.StartsWith("_", StringComparison.Ordinal) || name.StartsWith(".", StringComparison.Ordinal);

    private static async Task WalkAsync(
        IStorage storage,
        string path,
        List<string> segments,
        int depthLeft,
        string sourceName,
        string? sourceLocation,
        List<TableLocation> tables,
        CancellationToken cancellation)
    {
        IReadOnlyList<StorageEntry> entries = await storage.ListAsync(path, cancellation).ConfigureAwait(false);
        bool isDelta = false;
        bool hasParquet = false;
        var folders = new List<StorageEntry>();
        foreach (StorageEntry entry in entries)
        {
            if (entry.IsDirectory && entry.Name == "_delta_log")
            {
                isDelta = true;
            }
            else if (entry.IsDirectory && !IsHidden(entry.Name))
            {
                folders.Add(entry);
            }
            else if (IsParquetFile(entry))
            {
                hasParquet = true;
            }
        }

        string? format = isDelta ? TableFormats.Delta : hasParquet && folders.Count == 0 ? TableFormats.Parquet : null;
        if (format != null)
        {
            // The folder given is a table itself: its name is the table name, with no namespace.
            string name = segments.Count == 0 ? sourceName : segments[segments.Count - 1];
            string[] space = segments.Count == 0 ? new string[0] : segments.GetRange(0, segments.Count - 1).ToArray();
            string? location = (storage as ILocatedStorage)?.LocationOf(path);
            tables.Add(new TableLocation(SourceKinds.StorageFolder, sourceName, space, name, path, location, sourceLocation, null, format));
            return;
        }

        if (depthLeft == 0)
        {
            return;
        }

        folders.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (StorageEntry child in folders)
        {
            if (child.Name.IndexOf('=') >= 0)
            {
                continue;
            }

            segments.Add(child.Name);
            await WalkAsync(storage, child.Path.Trim('/'), segments, depthLeft - 1, sourceName, sourceLocation, tables, cancellation).ConfigureAwait(false);
            segments.RemoveAt(segments.Count - 1);
        }
    }

    /// <summary>The name of the root of a storage: the filesystem or workspace of a <see cref="OneLakeStorage"/>, the folder of a <see cref="LocalStorage"/>.</summary>
    private static string RootName(IStorage storage) => storage switch
    {
        OneLakeStorage oneLake => oneLake.Workspace,
        LocalStorage local => System.IO.Path.GetFileName(local.Root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
        _ => string.Empty,
    };

    private static int KindIndex(string item)
    {
        for (int i = 0; i < Kinds.Length; i++)
        {
            string suffix = Kinds[i][0];
            if (item.Length > suffix.Length && item.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
