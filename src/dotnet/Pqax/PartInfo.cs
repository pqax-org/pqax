using System.Collections.Generic;

namespace Pqax;

/// <summary>A part that the manifest lists: the name of its entry and the rows that it holds for each table.</summary>
public sealed class PartInfo
{
    internal PartInfo(string name, IReadOnlyDictionary<string, int> tables)
    {
        Name = name;
        Tables = tables;
    }

    /// <summary>The name of the entry, such as <c>columnChunks.2.json</c>.</summary>
    public string Name { get; }

    /// <summary>The number of rows that the part holds for each table, in the order of the manifest.</summary>
    public IReadOnlyDictionary<string, int> Tables { get; }
}
