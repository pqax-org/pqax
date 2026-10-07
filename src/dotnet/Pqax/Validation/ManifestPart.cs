namespace Pqax;

/// <summary>A part as the manifest lists it, for the validator. A row count that is not an integer is null.</summary>
internal sealed class ManifestPart
{
    public ManifestPart(string name, OrderedMap<long?> tables, bool hasTables)
    {
        Name = name;
        Tables = tables;
        HasTables = hasTables;
    }

    public string Name { get; }

    public OrderedMap<long?> Tables { get; }

    /// <summary>Whether the manifest gives the tables of the part as an object.</summary>
    public bool HasTables { get; }
}
