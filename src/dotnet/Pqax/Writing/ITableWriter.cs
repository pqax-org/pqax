using System.Text.Json;

namespace Pqax;

/// <summary>A table that the builder writes, typed or raw.</summary>
internal interface ITableWriter
{
    string Name { get; }

    bool IsSummary { get; }

    int Count { get; }

    /// <summary>
    /// Throws <see cref="PqaxWriteException"/> when a row breaks a rule that the builder checks: <c>table.idUnique</c>
    /// for an id that is not positive or that two rows share, <c>table.required</c> for a required field without a
    /// value, and <c>json.integerRange</c> for an integer beyond 2^53 - 1. A table that this version does not know passes.
    /// </summary>
    void Check();

    /// <summary>Writes the rows from <paramref name="start"/> as a table object: one array for each field that has a value in those rows.</summary>
    void Write(Utf8JsonWriter writer, int start, int count);
}
