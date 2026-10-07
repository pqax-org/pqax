using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// Finds the tables of the items of a OneLake workspace, or of a folder with the same layout:
/// <c>&lt;item&gt;.&lt;Kind&gt;/Tables/&lt;table&gt;</c> or <c>&lt;item&gt;.&lt;Kind&gt;/Tables/&lt;schema&gt;/&lt;table&gt;</c>.
/// </summary>
public static class TableDiscovery
{
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
                tables.Add(new TableLocation(kind, name, null, entry.Name, entry.Path));
                continue;
            }

            foreach (StorageEntry child in children)
            {
                if (child.IsDirectory)
                {
                    tables.Add(new TableLocation(kind, name, new[] { entry.Name }, child.Name, child.Path));
                }
            }
        }

        return tables;
    }

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
