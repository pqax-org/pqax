using System;
using System.Collections.Generic;

namespace Pqax;

/// <summary>A table of the format, or a nested object: its name and its fields.</summary>
internal sealed class TableInfo
{
    private readonly Dictionary<string, FieldInfo> byName;

    public TableInfo(string name, bool isSummary, IReadOnlyList<FieldInfo> fields)
    {
        Name = name;
        IsSummary = isSummary;
        Fields = fields;
        byName = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        foreach (FieldInfo field in fields)
        {
            byName[field.Name] = field;
            if (field.Kind == FieldKind.Id)
            {
                HasId = true;
            }
        }
    }

    /// <summary>The name of the table in the file, such as <c>columnChunks</c>.</summary>
    public string Name { get; }

    /// <summary>Whether the table belongs in <c>summary.json</c>.</summary>
    public bool IsSummary { get; }

    /// <summary>Whether the table has an <c>id</c> field.</summary>
    public bool HasId { get; }

    public IReadOnlyList<FieldInfo> Fields { get; }

    public FieldInfo? Find(string name) => byName.TryGetValue(name, out FieldInfo? field) ? field : null;
}
