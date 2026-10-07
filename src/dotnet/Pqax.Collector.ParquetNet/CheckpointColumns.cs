using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace Pqax.Collector.ParquetNet;

/// <summary>
/// Reads the columns of one row group of a checkpoint and lines their values up with the rows. Parquet.Net returns a
/// required field inside an optional struct without the rows where the struct is missing, as the Fabric warehouse
/// writes them; the definition levels say which rows have a value. Lists and maps are rebuilt row by row from the
/// repetition levels.
/// </summary>
internal sealed class CheckpointColumns
{
    private readonly ParquetRowGroupReader group;
    private readonly Dictionary<string, DataField> fields;
    private readonly CancellationToken cancellation;

    public CheckpointColumns(ParquetRowGroupReader group, Dictionary<string, DataField> fields, CancellationToken cancellation)
    {
        this.group = group;
        this.fields = fields;
        this.cancellation = cancellation;
        Rows = checked((int)group.RowCount);
    }

    public int Rows { get; }

    /// <summary>The paths of the data fields under a prefix, with the number of parts given.</summary>
    public List<string> Children(string prefix, int parts)
    {
        var children = new List<string>();
        foreach (string path in fields.Keys)
        {
            if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) && path.Split('/').Length == parts)
            {
                children.Add(path);
            }
        }

        return children;
    }

    /// <summary>One value for each row of a scalar field. Null when the checkpoint has no such field.</summary>
    /// <exception cref="InvalidDataException">The values cannot be lined up with the rows.</exception>
    public async Task<object?[]?> ScalarAsync(string path)
    {
        if (!fields.TryGetValue(path, out DataField? field))
        {
            return null;
        }

        DataColumn column = await group.ReadColumnAsync(field, cancellation).ConfigureAwait(false);
        Array data = column.Data;
        var values = new object?[Rows];
        if (data.Length == Rows)
        {
            for (int row = 0; row < Rows; row++)
            {
                values[row] = data.GetValue(row);
            }

            return values;
        }

        int[]? levels = column.DefinitionLevels;
        if (levels != null && levels.Length == Rows)
        {
            int next = 0;
            for (int row = 0; row < Rows && next < data.Length; row++)
            {
                if (levels[row] >= field.MaxDefinitionLevel)
                {
                    values[row] = data.GetValue(next++);
                }
            }

            if (next == data.Length)
            {
                return values;
            }
        }

        throw new InvalidDataException("A column of the checkpoint does not line up with its rows: " + path + ".");
    }

    /// <summary>One list for each row of a list field: null when the row has no element. Null when the checkpoint has no such field.</summary>
    public async Task<List<string>?[]?> ListAsync(string prefix)
    {
        string? path = Leaf(prefix, null);
        if (path == null || !fields.TryGetValue(path, out DataField? field))
        {
            return null;
        }

        DataColumn column = await group.ReadColumnAsync(field, cancellation).ConfigureAwait(false);
        int[]? repetition = column.RepetitionLevels;
        int[]? definition = column.DefinitionLevels;
        if (repetition == null || definition == null || repetition.Length != definition.Length)
        {
            throw new InvalidDataException("A list of the checkpoint has no levels: " + prefix + ".");
        }

        Array data = column.Data;
        bool bySlot = data.Length == repetition.Length;
        var lists = new List<string>?[Rows];
        int row = -1;
        int next = 0;
        for (int slot = 0; slot < repetition.Length; slot++)
        {
            if (repetition[slot] == 0 && ++row >= Rows)
            {
                throw new InvalidDataException("A list of the checkpoint has more rows than its row group: " + prefix + ".");
            }

            if (row < 0 || definition[slot] < field.MaxDefinitionLevel)
            {
                continue;
            }

            object? value = bySlot ? data.GetValue(slot) : data.GetValue(next++);
            if (value != null)
            {
                (lists[row] ??= new List<string>()).Add(value.ToString() ?? string.Empty);
            }
        }

        if (row + 1 != Rows)
        {
            throw new InvalidDataException("A list of the checkpoint does not line up with its rows: " + prefix + ".");
        }

        return lists;
    }

    /// <summary>One map for each row of a map field: null when the row has no entry. Null when the checkpoint has no such field.</summary>
    public async Task<Dictionary<string, string?>?[]?> MapAsync(string prefix)
    {
        string? keyPath = Leaf(prefix, "key");
        string? valuePath = Leaf(prefix, "value");
        if (keyPath == null || valuePath == null || !fields.TryGetValue(keyPath, out DataField? keyField) || !fields.TryGetValue(valuePath, out DataField? valueField))
        {
            return null;
        }

        DataColumn keys = await group.ReadColumnAsync(keyField, cancellation).ConfigureAwait(false);
        DataColumn values = await group.ReadColumnAsync(valueField, cancellation).ConfigureAwait(false);
        int[]? repetition = keys.RepetitionLevels;
        int[]? definition = keys.DefinitionLevels;
        int[]? valueDefinition = values.DefinitionLevels;
        if (repetition == null || definition == null || repetition.Length != definition.Length)
        {
            throw new InvalidDataException("A map of the checkpoint has no levels: " + prefix + ".");
        }

        bool keysBySlot = keys.Data.Length == repetition.Length;
        bool valuesBySlot = values.Data.Length == repetition.Length;
        var maps = new Dictionary<string, string?>?[Rows];
        int row = -1;
        int keyIndex = 0;
        int valueIndex = 0;
        for (int slot = 0; slot < repetition.Length; slot++)
        {
            if (repetition[slot] == 0 && ++row >= Rows)
            {
                throw new InvalidDataException("A map of the checkpoint has more rows than its row group: " + prefix + ".");
            }

            if (row < 0 || definition[slot] < keyField.MaxDefinitionLevel)
            {
                continue;
            }

            string key = (keysBySlot ? keys.Data.GetValue(slot) : keys.Data.GetValue(keyIndex++))?.ToString() ?? string.Empty;
            object? value;
            if (valuesBySlot)
            {
                value = values.Data.GetValue(slot);
            }
            else if (valueDefinition != null && slot < valueDefinition.Length && valueDefinition[slot] >= valueField.MaxDefinitionLevel)
            {
                value = values.Data.GetValue(valueIndex++);
            }
            else
            {
                value = null;
            }

            (maps[row] ??= new Dictionary<string, string?>(StringComparer.Ordinal))[key] = value?.ToString();
        }

        if (row + 1 != Rows)
        {
            throw new InvalidDataException("A map of the checkpoint does not line up with its rows: " + prefix + ".");
        }

        return maps;
    }

    /// <summary>
    /// The leaf of a list or a map under a prefix: the field two levels down whose last part is the name given, or
    /// the only such field when the name is null. Writers name the levels between differently: <c>list/element</c>,
    /// <c>key_value/key</c>, <c>map/key</c>.
    /// </summary>
    private string? Leaf(string prefix, string? name)
    {
        int parts = prefix.Split('/').Length + 2;
        string? found = null;
        foreach (string path in Children(prefix, parts))
        {
            if (name == null || path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))
            {
                if (found != null)
                {
                    return null;
                }

                found = path;
            }
        }

        return found;
    }
}
