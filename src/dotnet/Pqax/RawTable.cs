using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>
/// A table read as JSON values: every field, including the ones that this version of the format does not know.
/// It is what <see cref="PqaxDocument.ReadRaw(string)"/> returns.
/// </summary>
public sealed class RawTable
{
    internal RawTable(string name, int count, IReadOnlyDictionary<string, IReadOnlyList<JsonElement?>> fields)
    {
        Name = name;
        Count = count;
        Fields = fields;
    }

    /// <summary>The name of the table in the file.</summary>
    public string Name { get; }

    /// <summary>The number of rows.</summary>
    public int Count { get; }

    /// <summary>
    /// Every field by name, in the order of the file. Each list has one value for each row, and a value is null when
    /// the row has none or the JSON value is null. A member of the table that is not an array is not a field and is not here.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<JsonElement?>> Fields { get; }
}
