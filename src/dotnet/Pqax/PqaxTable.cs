using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

namespace Pqax;

/// <summary>
/// A table of a PQAX file: its rows, typed, and the fields that this version of the format does not know.
/// The rows of all the parts of the table are concatenated in the order of the manifest.
/// </summary>
/// <typeparam name="TRow">The row type, such as <see cref="ColumnRow"/>.</typeparam>
public sealed class PqaxTable<TRow> : IReadOnlyList<TRow>
    where TRow : class
{
    private readonly Func<TRow, long>? idOf;
    private Dictionary<long, TRow>? index;

    internal PqaxTable(string name, IReadOnlyList<TRow> rows, IReadOnlyDictionary<string, IReadOnlyList<JsonElement?>> extraFields, Func<TRow, long>? idOf)
    {
        Name = name;
        Rows = rows;
        ExtraFields = extraFields;
        this.idOf = idOf;
    }

    /// <summary>The name of the table in the file, such as <c>columns</c>.</summary>
    public string Name { get; }

    /// <summary>The number of rows.</summary>
    public int Count => Rows.Count;

    /// <summary>The rows, in the order of the file.</summary>
    public IReadOnlyList<TRow> Rows { get; }

    /// <summary>
    /// The fields of the table that are not in the object model of this version, by name, in the order of the file.
    /// Each list has one value for each row, and a value is null when the row has none or the JSON value is null.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<JsonElement?>> ExtraFields { get; }

    /// <summary>The row at a position.</summary>
    /// <param name="index">The position of the row, from 0.</param>
    /// <returns>The row.</returns>
    public TRow this[int index] => Rows[index];

    /// <summary>
    /// The row whose <c>id</c> is <paramref name="id"/>, or null when the table has none or has no ids. This is how a
    /// reference is resolved: a reference to a row that the file does not have gives null. When two rows have the same
    /// id, the first one is returned. The index is built on the first call.
    /// </summary>
    /// <param name="id">The id to look for.</param>
    /// <returns>The row, or null.</returns>
    public TRow? FindById(long id)
    {
        if (idOf == null)
        {
            return null;
        }

        Dictionary<long, TRow>? map = index;
        if (map == null)
        {
            map = new Dictionary<long, TRow>(Rows.Count);
            foreach (TRow row in Rows)
            {
                long key = idOf(row);
                if (!map.ContainsKey(key))
                {
                    map.Add(key, row);
                }
            }

            Interlocked.CompareExchange(ref index, map, null);
            map = index!;
        }

        return map.TryGetValue(id, out TRow? found) ? found : null;
    }

    /// <summary>The row whose <c>id</c> is the reference, or null when the reference is null or points to no row.</summary>
    /// <param name="id">The reference, as a field of another row holds it.</param>
    /// <returns>The row, or null.</returns>
    public TRow? FindById(long? id) => id.HasValue ? FindById(id.Value) : null;

    /// <summary>Enumerates the rows.</summary>
    /// <returns>An enumerator of the rows.</returns>
    public IEnumerator<TRow> GetEnumerator() => Rows.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
