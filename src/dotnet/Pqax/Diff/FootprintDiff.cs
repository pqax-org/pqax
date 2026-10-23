using System;
using System.Collections.Generic;
using System.Linq;

namespace Pqax;

/// <summary>
/// The comparison of two footprints of the same lakehouse, the older and the newer, as section 13 of the object model
/// defines it: the tables added, removed, and kept, and for every kept table the change of each total and of each
/// derived measure. <see cref="StorageDiff.Compare"/> returns it.
/// </summary>
public sealed class FootprintDiff
{
    internal FootprintDiff(FootprintMeasures older, FootprintMeasures newer, long smallFileSize, long tinyFileSize, IReadOnlyList<ValueChange> collection,
        IReadOnlyList<TableEntry> added, IReadOnlyList<TableEntry> removed, IReadOnlyList<KeptTable> kept)
    {
        Older = older;
        Newer = newer;
        SmallFileSize = smallFileSize;
        TinyFileSize = tinyFileSize;
        Collection = collection;
        Added = added;
        Removed = removed;
        Kept = kept;
        ChangedCount = kept.Count(table => table.IsChanged);
    }

    /// <summary>The measures of the older footprint: its tables, whether its files were read, and its warnings.</summary>
    public FootprintMeasures Older { get; }

    /// <summary>The measures of the newer footprint.</summary>
    public FootprintMeasures Newer { get; }

    /// <summary>The size below which a file is small, in bytes, for the measures of both footprints.</summary>
    public long SmallFileSize { get; }

    /// <summary>The size below which a file is very small, in bytes.</summary>
    public long TinyFileSize { get; }

    /// <summary>The measures of the collection, section 10.7, then the tables of each depth as <c>depths.&lt;DetailDepth&gt;</c>.</summary>
    public IReadOnlyList<ValueChange> Collection { get; }

    /// <summary>The tables of the newer footprint that the older one does not have, in the order of the newer file.</summary>
    public IReadOnlyList<TableEntry> Added { get; }

    /// <summary>The tables of the older footprint that the newer one does not have, in the order of the older file.</summary>
    public IReadOnlyList<TableEntry> Removed { get; }

    /// <summary>The tables of both footprints, in the order of the newer file.</summary>
    public IReadOnlyList<KeptTable> Kept { get; }

    /// <summary>The kept tables that changed.</summary>
    public int ChangedCount { get; }

    /// <summary>The change of the collection with a name, such as <c>footprint</c>, or null.</summary>
    /// <param name="name">The name of the value.</param>
    /// <returns>The change, or null when there is none with that name.</returns>
    public ValueChange? CollectionValue(string name) => Collection.FirstOrDefault(value => value.Name == name);
}

/// <summary>
/// One compared value: the older, the newer, and the delta. A value is a <see cref="long"/>, a <see cref="double"/>, a
/// <see cref="bool"/>, a <see cref="string"/>, a <see cref="DateTimeOffset"/>, a list of strings, or null when the
/// footprint lacks it.
/// </summary>
public sealed class ValueChange
{
    internal ValueChange(string name, object? older, object? newer, double? delta, bool isChanged)
    {
        Name = name;
        Older = older;
        Newer = newer;
        Delta = delta;
        IsChanged = isChanged;
    }

    /// <summary>The name: the field of section 6, such as <c>fileSize</c> or <c>fileSizes.median</c>, or the path of the measure, such as <c>stack.waste</c>.</summary>
    public string Name { get; }

    /// <summary>The value in the older footprint, or null.</summary>
    public object? Older { get; }

    /// <summary>The value in the newer footprint, or null.</summary>
    public object? Newer { get; }

    /// <summary>The newer minus the older for numbers, in milliseconds for times; null for a boolean, a text, or a list, and when either value is null.</summary>
    public double? Delta { get; }

    /// <summary>Whether the two values differ. A value differs from null.</summary>
    public bool IsChanged { get; }
}

/// <summary>A table as one footprint has it: what identifies it, and its size.</summary>
public sealed class TableSide
{
    internal TableSide(long? id, int row, string label, string? name, IReadOnlyList<string>? @namespace, string? tableId, string? snapshotId, string? detailDepth,
        long columnCount, long? fileCount, long? fileSize, long? rowCount, double? total, double? waste)
    {
        Id = id;
        Row = row;
        Label = label;
        Name = name;
        Namespace = @namespace;
        TableId = tableId;
        SnapshotId = snapshotId;
        DetailDepth = detailDepth;
        ColumnCount = columnCount;
        FileCount = fileCount;
        FileSize = fileSize;
        RowCount = rowCount;
        Total = total;
        Waste = waste;
    }

    /// <summary>The id of the table in its footprint, null when it is not a positive integer or repeats an earlier row.</summary>
    public long? Id { get; }

    /// <summary>The position of the table among the tables of its footprint, from 0.</summary>
    public int Row { get; }

    /// <summary>The label of the measures: the namespace and the name joined by dots.</summary>
    public string Label { get; }

    /// <summary>The name of the table.</summary>
    public string? Name { get; }

    /// <summary>The namespace of the table.</summary>
    public IReadOnlyList<string>? Namespace { get; }

    /// <summary>The identifier that the table format assigns.</summary>
    public string? TableId { get; }

    /// <summary>The version that the footprint describes.</summary>
    public string? SnapshotId { get; }

    /// <summary>The depth at which the footprint keeps the table.</summary>
    public string? DetailDepth { get; }

    /// <summary>The columns of the table in its footprint.</summary>
    public long ColumnCount { get; }

    /// <summary>FileCount of the table.</summary>
    public long? FileCount { get; }

    /// <summary>FileSize of the table.</summary>
    public long? FileSize { get; }

    /// <summary>RowCount of the table.</summary>
    public long? RowCount { get; }

    /// <summary>The Total of its storage stack.</summary>
    public double? Total { get; }

    /// <summary>The Waste of its storage stack.</summary>
    public double? Waste { get; }
}

/// <summary>A table added or removed.</summary>
public sealed class TableEntry
{
    internal TableEntry(TableSide table, bool recreated)
    {
        Table = table;
        Recreated = recreated;
    }

    /// <summary>The table, as the footprint that has it has it.</summary>
    public TableSide Table { get; }

    /// <summary>Whether a table with the same source, namespace, and name was removed while this one was added, or the reverse: a table dropped and created again with a new TableId.</summary>
    public bool Recreated { get; }
}

/// <summary>A table that both footprints have, with what changed.</summary>
public sealed class KeptTable
{
    internal KeptTable(string matchedBy, bool renamed, bool isChanged, TableSide older, TableSide newer, IReadOnlyList<ValueChange> totals, IReadOnlyList<ValueChange> measures,
        ColumnChanges columns, DetailChanges? files, DetailChanges? partitions, DetailChanges? commits)
    {
        MatchedBy = matchedBy;
        Renamed = renamed;
        IsChanged = isChanged;
        Older = older;
        Newer = newer;
        Totals = totals;
        Measures = measures;
        Columns = columns;
        Files = files;
        Partitions = partitions;
        Commits = commits;
    }

    /// <summary><c>tableId</c> when the two tables have the same TableId, <c>name</c> when they are the same by their source, namespace, and name.</summary>
    public string MatchedBy { get; }

    /// <summary>Whether the name or the namespace changed.</summary>
    public bool Renamed { get; }

    /// <summary>Whether the table changed: its name, its SnapshotId, a total, a column, a file, a partition, or a commit. The measures alone do not count.</summary>
    public bool IsChanged { get; }

    /// <summary>The table in the older footprint.</summary>
    public TableSide Older { get; }

    /// <summary>The table in the newer footprint.</summary>
    public TableSide Newer { get; }

    /// <summary>Each total of section 6.4, with the members of its distributions, such as <c>fileSizes.median</c>.</summary>
    public IReadOnlyList<ValueChange> Totals { get; }

    /// <summary>Each measure of section 10 for a table, by its path in the measures, such as <c>stack.waste</c>.</summary>
    public IReadOnlyList<ValueChange> Measures { get; }

    /// <summary>The columns added, removed, and kept.</summary>
    public ColumnChanges Columns { get; }

    /// <summary>The files, when both footprints keep them for the table; else null.</summary>
    public DetailChanges? Files { get; }

    /// <summary>The partitions, when both footprints keep them with their values; else null.</summary>
    public DetailChanges? Partitions { get; }

    /// <summary>The commits, when both footprints keep them; else null.</summary>
    public DetailChanges? Commits { get; }

    /// <summary>The total with a name, or null.</summary>
    /// <param name="name">The name, such as <c>fileCount</c>.</param>
    /// <returns>The change, or null.</returns>
    public ValueChange? Total(string name) => Totals.FirstOrDefault(value => value.Name == name);

    /// <summary>The measure with a path, or null.</summary>
    /// <param name="name">The path, such as <c>stack.total</c>.</param>
    /// <returns>The change, or null.</returns>
    public ValueChange? Measure(string name) => Measures.FirstOrDefault(value => value.Name == name);
}

/// <summary>A column as one footprint has it.</summary>
public sealed class ColumnSide
{
    internal ColumnSide(long? id, int row, string label, string? name, long? fieldId, string? dataType, bool? isNullable, long? compressedSize)
    {
        Id = id;
        Row = row;
        Label = label;
        Name = name;
        FieldId = fieldId;
        DataType = dataType;
        IsNullable = isNullable;
        CompressedSize = compressedSize;
    }

    /// <summary>The id of the column in its footprint, null when it is not a positive integer or repeats an earlier row.</summary>
    public long? Id { get; }

    /// <summary>The position of the column among the columns of its footprint, from 0.</summary>
    public int Row { get; }

    /// <summary>The label of the measures of the column.</summary>
    public string Label { get; }

    /// <summary>The name of the column.</summary>
    public string? Name { get; }

    /// <summary>The identifier that the table format assigns, which survives a rename.</summary>
    public long? FieldId { get; }

    /// <summary>The type that the table format declares.</summary>
    public string? DataType { get; }

    /// <summary>Whether the column accepts nulls.</summary>
    public bool? IsNullable { get; }

    /// <summary>The compressed size of the column.</summary>
    public long? CompressedSize { get; }
}

/// <summary>A column that both footprints have in a kept table, with what changed.</summary>
public sealed class KeptColumn
{
    internal KeptColumn(string matchedBy, bool renamed, bool isChanged, ColumnSide older, ColumnSide newer, IReadOnlyList<ValueChange> totals, IReadOnlyList<ValueChange> measures)
    {
        MatchedBy = matchedBy;
        Renamed = renamed;
        IsChanged = isChanged;
        Older = older;
        Newer = newer;
        Totals = totals;
        Measures = measures;
    }

    /// <summary><c>fieldId</c> when the two columns have the same FieldId, <c>name</c> when they have the same name.</summary>
    public string MatchedBy { get; }

    /// <summary>Whether the name changed.</summary>
    public bool Renamed { get; }

    /// <summary>Whether the column changed: its name, its DataType, IsNullable, or a total.</summary>
    public bool IsChanged { get; }

    /// <summary>The column in the older footprint.</summary>
    public ColumnSide Older { get; }

    /// <summary>The column in the newer footprint.</summary>
    public ColumnSide Newer { get; }

    /// <summary>Each total of section 6.5.</summary>
    public IReadOnlyList<ValueChange> Totals { get; }

    /// <summary>Each measure of section 10.6 for a column.</summary>
    public IReadOnlyList<ValueChange> Measures { get; }
}

/// <summary>The columns of a kept table.</summary>
public sealed class ColumnChanges
{
    internal ColumnChanges(IReadOnlyList<ColumnSide> added, IReadOnlyList<ColumnSide> removed, IReadOnlyList<KeptColumn> kept)
    {
        Added = added;
        Removed = removed;
        Kept = kept;
    }

    /// <summary>The columns of the newer table that the older one does not have, in the order of the newer file.</summary>
    public IReadOnlyList<ColumnSide> Added { get; }

    /// <summary>The columns of the older table that the newer one does not have, in the order of the older file.</summary>
    public IReadOnlyList<ColumnSide> Removed { get; }

    /// <summary>The columns of both, in the order of the newer file.</summary>
    public IReadOnlyList<KeptColumn> Kept { get; }
}

/// <summary>The files, the partitions, or the commits of a kept table: how many were added, removed, kept, and changed.</summary>
public sealed class DetailChanges
{
    internal DetailChanges(long added, long removed, long kept, long? changed, long? addedFileSize, long? removedFileSize)
    {
        Added = added;
        Removed = removed;
        Kept = kept;
        Changed = changed;
        AddedFileSize = addedFileSize;
        RemovedFileSize = removedFileSize;
    }

    /// <summary>The objects of the newer table that the older one does not have.</summary>
    public long Added { get; }

    /// <summary>The objects of the older table that the newer one does not have.</summary>
    public long Removed { get; }

    /// <summary>The objects of both.</summary>
    public long Kept { get; }

    /// <summary>The kept objects whose totals differ; null for the commits.</summary>
    public long? Changed { get; }

    /// <summary>The FileSize of the objects added: 0 when none was added, null when none of them has one; null for the commits.</summary>
    public long? AddedFileSize { get; }

    /// <summary>The FileSize of the objects removed, as <see cref="AddedFileSize"/>.</summary>
    public long? RemovedFileSize { get; }
}

/// <summary>One table change in an order of <see cref="StorageDiff.Rank"/>: a table added, removed, or kept.</summary>
public sealed class TableChange
{
    internal TableChange(string kind, TableEntry? entry, KeptTable? kept)
    {
        Kind = kind;
        Entry = entry;
        Kept = kept;
    }

    /// <summary><c>added</c>, <c>removed</c>, or <c>kept</c>.</summary>
    public string Kind { get; }

    /// <summary>The table added or removed; null for a kept table.</summary>
    public TableEntry? Entry { get; }

    /// <summary>The kept table; null for a table added or removed.</summary>
    public KeptTable? Kept { get; }

    /// <summary>The label of the table: the newer one for a kept table.</summary>
    public string Label => Kept?.Newer.Label ?? Entry!.Table.Label;
}
