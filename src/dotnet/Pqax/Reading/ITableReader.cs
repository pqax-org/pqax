using System.Text.Json;

namespace Pqax;

/// <summary>Reads the rows of one table from its parts, typed or raw.</summary>
internal interface ITableReader
{
    /// <summary>Starts a new part.</summary>
    void BeginPart();

    /// <summary>
    /// Reads the table object at the current token. The table has, in this part, the rows of its longest array: the
    /// count that the manifest states is not used, so that a manifest cannot make the reader allocate rows.
    /// </summary>
    void ReadTable(ref Utf8JsonReader reader);
}
