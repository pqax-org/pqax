using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>A part that the manifest lists: the name of its entry and the rows that it holds for each table.</summary>
public sealed class PartInfo
{
    internal PartInfo(string name, IReadOnlyDictionary<string, int> tables, IReadOnlyDictionary<string, JsonElement> extensions)
    {
        Name = name;
        Tables = tables;
        Extensions = extensions;
    }

    /// <summary>The name of the entry, such as <c>columnChunks.2.json</c>.</summary>
    public string Name { get; }

    /// <summary>The number of rows that the part holds for each table, in the order of the manifest.</summary>
    public IReadOnlyDictionary<string, int> Tables { get; }

    /// <summary>
    /// The members of the descriptor of the part other than <c>name</c> and <c>tables</c>, such as those that a producer
    /// adds, in the order of the file. Empty when there are none. <see cref="FootprintBuilder.From(PqaxDocument)"/> keeps them.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; }
}
