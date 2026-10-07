using System.Collections.Generic;

namespace Pqax;

/// <summary>A field of a table or of a nested object: its name in the file, its type, and its rules.</summary>
internal sealed class FieldInfo
{
    public FieldInfo(string name, FieldKind kind, bool required, string? target, IReadOnlyList<FieldInfo>? members)
    {
        Name = name;
        Kind = kind;
        Required = required;
        Target = target;
        Members = members;
    }

    /// <summary>The name in the file, in lower camel case.</summary>
    public string Name { get; }

    public FieldKind Kind { get; }

    /// <summary>Whether the object model requires a value in every row.</summary>
    public bool Required { get; }

    /// <summary>For a reference or a list of references, the table it points to.</summary>
    public string? Target { get; }

    /// <summary>For a nested object, its fields.</summary>
    public IReadOnlyList<FieldInfo>? Members { get; }

    /// <summary>Whether the values are integers: an id, an integer, or a reference.</summary>
    public bool IsInteger => Kind == FieldKind.Id || Kind == FieldKind.Integer || Kind == FieldKind.Reference;

    /// <summary>Whether the field is a reference or a list of references.</summary>
    public bool IsReference => Kind == FieldKind.Reference || Kind == FieldKind.ReferenceList;

    /// <summary>Finds a member of a nested object by name.</summary>
    public FieldInfo? FindMember(string name)
    {
        if (Members == null)
        {
            return null;
        }

        foreach (FieldInfo member in Members)
        {
            if (member.Name == name)
            {
                return member;
            }
        }

        return null;
    }
}
