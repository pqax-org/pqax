using System.Text.Json;

namespace Pqax;

/// <summary>A table that the builder writes, typed or raw.</summary>
internal interface ITableWriter
{
    string Name { get; }

    bool IsSummary { get; }

    int Count { get; }

    /// <summary>
    /// Throws <see cref="System.InvalidOperationException"/>, naming the table and the id, when two rows have the same
    /// id or a row has an id that is not positive. A table without ids, or one that this version does not know, passes.
    /// </summary>
    void CheckIds();

    /// <summary>Writes the rows from <paramref name="start"/> as a table object: one array for each field that has a value in those rows.</summary>
    void Write(Utf8JsonWriter writer, int start, int count);
}
