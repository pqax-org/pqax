using System;
using System.Collections.Generic;

namespace Pqax;

/// <summary>
/// What the validator keeps of a table across its parts, for the rules that join tables: the ids, the references,
/// and the fields that the depth and totals rules compare. A value that is not an integer is kept as null.
/// </summary>
internal sealed class TableStore
{
    public TableStore(string name, TableInfo? info)
    {
        Name = name;
        Info = info;
        if (info == null)
        {
            return;
        }

        foreach (FieldInfo field in info.Fields)
        {
            if (field.Kind == FieldKind.Id || field.Kind == FieldKind.Reference || IsTotal(name, field.Name))
            {
                Integers[field.Name] = new List<long?>();
            }
            else if (field.Kind == FieldKind.ReferenceList)
            {
                Lists[field.Name] = new List<long>();
            }
        }

        if (name == "tables")
        {
            Strings["detailDepth"] = new List<string?>();
        }
    }

    public string Name { get; }

    public TableInfo? Info { get; }

    /// <summary>The rows across all parts.</summary>
    public int Rows { get; set; }

    /// <summary>True when the arrays of a part have different lengths, so that the fields of a row cannot be paired.</summary>
    public bool Broken { get; set; }

    /// <summary>True when a part that the manifest states for the table cannot be read, so that the table is not whole.</summary>
    public bool Incomplete { get; set; }

    /// <summary>True when the rows can be joined with other tables: none of its parts is missing and the fields of each row pair up.</summary>
    public bool Usable => !Broken && !Incomplete;

    /// <summary>True unless the ids are not positive integers or repeat.</summary>
    public bool IdsUnique { get; set; } = true;

    public List<string> Parts { get; } = new List<string>();

    /// <summary>The part, when the table is in a single part.</summary>
    public string? SinglePart => Parts.Count == 1 ? Parts[0] : null;

    public Dictionary<string, List<long?>> Integers { get; } = new Dictionary<string, List<long?>>(StringComparer.Ordinal);

    public Dictionary<string, List<long>> Lists { get; } = new Dictionary<string, List<long>>(StringComparer.Ordinal);

    public Dictionary<string, List<string?>> Strings { get; } = new Dictionary<string, List<string?>>(StringComparer.Ordinal);

    public bool Keeps(string field) => Integers.ContainsKey(field) || Strings.ContainsKey(field);

    public void SetInteger(string field, int row, long? value)
    {
        if (Integers.TryGetValue(field, out List<long?>? list))
        {
            while (list.Count <= row)
            {
                list.Add(null);
            }

            list[row] = value;
        }
    }

    public void SetString(string field, int row, string? value)
    {
        if (Strings.TryGetValue(field, out List<string?>? list))
        {
            while (list.Count <= row)
            {
                list.Add(null);
            }

            list[row] = value;
        }
    }

    public void AddListValue(string field, long value)
    {
        if (Lists.TryGetValue(field, out List<long>? list))
        {
            list.Add(value);
        }
    }

    /// <summary>Gives every kept field one value for each row, so that a part that lacks a field leaves nulls.</summary>
    public void PadTo(int rows)
    {
        foreach (List<long?> list in Integers.Values)
        {
            while (list.Count < rows)
            {
                list.Add(null);
            }
        }

        foreach (List<string?> list in Strings.Values)
        {
            while (list.Count < rows)
            {
                list.Add(null);
            }
        }
    }

    public long? Integer(string field, int row) => Integers.TryGetValue(field, out List<long?>? list) && row < list.Count ? list[row] : null;

    public string? String(string field, int row) => Strings.TryGetValue(field, out List<string?>? list) && row < list.Count ? list[row] : null;

    /// <summary>The totals that the depth and totals rules compare with the detail.</summary>
    private static bool IsTotal(string table, string field)
    {
        switch (table)
        {
            case "tables":
                return field == "fileCount" || field == "fileSize" || field == "rowGroupCount" || field == "compressedSize";
            case "files":
                return field == "fileSize";
            case "columns":
            case "columnChunks":
                return field == "compressedSize";
            default:
                return false;
        }
    }
}
