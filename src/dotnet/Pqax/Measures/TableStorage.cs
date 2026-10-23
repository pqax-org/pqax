using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Pqax;

/// <summary>The files of a table below a size.</summary>
public sealed class SmallFiles
{
    internal SmallFiles(long threshold, long? count, double? shareAtLeast)
    {
        Threshold = threshold;
        Count = count;
        ShareAtLeast = shareAtLeast;
    }

    /// <summary>The size in bytes.</summary>
    public long Threshold { get; }

    /// <summary>The exact count of the files below it, from the <c>files</c> table only.</summary>
    public long? Count { get; }

    /// <summary>The share of the files below it: exact from the <c>files</c> table, otherwise a lower bound from the quartiles of <c>fileSizes</c>.</summary>
    public double? ShareAtLeast { get; }
}

/// <summary>The ratios of a table of section 10.5 of the object model. A ratio is null when a field is missing or its denominator is not positive.</summary>
public sealed class TableMeasures
{
    internal TableMeasures(double? compressionRatio, double? deletedRowShare, double? averageFileSize, double? averageRowGroupRows, double? footerCoverage, double? staleColumnShare, double? nonLiveShare, double? averagePartitionSize, double? dictionaryFallbackShare)
    {
        CompressionRatio = compressionRatio;
        DeletedRowShare = deletedRowShare;
        AverageFileSize = averageFileSize;
        AverageRowGroupRows = averageRowGroupRows;
        FooterCoverage = footerCoverage;
        StaleColumnShare = staleColumnShare;
        NonLiveShare = nonLiveShare;
        AveragePartitionSize = averagePartitionSize;
        DictionaryFallbackShare = dictionaryFallbackShare;
    }

    /// <summary><c>uncompressedSize / compressedSize</c>.</summary>
    public double? CompressionRatio { get; }

    /// <summary><c>deletedRowCount / rowCount</c>.</summary>
    public double? DeletedRowShare { get; }

    /// <summary><c>fileSize / fileCount</c>.</summary>
    public double? AverageFileSize { get; }

    /// <summary><c>rowGroupRowCounts.sum / rowGroupRowCounts.count</c>, else <c>rowCount / rowGroupCount</c> when <c>footerCount</c> equals <c>fileCount</c>.</summary>
    public double? AverageRowGroupRows { get; }

    /// <summary><c>footerCount / fileCount</c>.</summary>
    public double? FooterCoverage { get; }

    /// <summary><c>staleColumnSize / compressedSize</c>.</summary>
    public double? StaleColumnShare { get; }

    /// <summary><c>nonLiveFileSize / (fileSize + nonLiveFileSize)</c>.</summary>
    public double? NonLiveShare { get; }

    /// <summary><c>partitionSizes.sum / partitionSizes.count</c>, else <c>fileSize / partitionCount</c>.</summary>
    public double? AveragePartitionSize { get; }

    /// <summary>The sum of <c>fallbackChunkCount</c> over the sum of <c>dictionaryChunkCount</c>, over the columns of the table that have both.</summary>
    public double? DictionaryFallbackShare { get; }
}

/// <summary>
/// The file sizes of a table and its V-Order shares, of section 10.3 of the object model: from the <c>files</c> table when
/// it has files of the table with a size, otherwise from <c>fileSizes</c>, with <c>fileCount</c> and <c>fileSize</c> for
/// a missing count and sum.
/// </summary>
public sealed class FileMeasures
{
    internal FileMeasures(long? count, double? sum, double? minimum, double? firstQuartile, double? median, double? thirdQuartile, double? maximum, double? vOrderShare, double? vOrderBytesShare)
    {
        Count = count;
        Sum = sum;
        Minimum = minimum;
        FirstQuartile = firstQuartile;
        Median = median;
        ThirdQuartile = thirdQuartile;
        Maximum = maximum;
        VOrderShare = vOrderShare;
        VOrderBytesShare = vOrderBytesShare;
    }

    /// <summary>The number of files.</summary>
    public long? Count { get; }

    /// <summary>The sum of their sizes.</summary>
    public double? Sum { get; }

    /// <summary>The smallest file.</summary>
    public double? Minimum { get; }

    /// <summary>The first quartile, taken by position.</summary>
    public double? FirstQuartile { get; }

    /// <summary>The median, taken by position.</summary>
    public double? Median { get; }

    /// <summary>The third quartile, taken by position.</summary>
    public double? ThirdQuartile { get; }

    /// <summary>The largest file.</summary>
    public double? Maximum { get; }

    /// <summary>
    /// VOrderShare: the V-Ordered files over the files whose <c>isVOrdered</c> is known, when one file of the <c>files</c>
    /// table has it; otherwise <c>min(1, vOrderedFileCount / fileCount)</c>.
    /// </summary>
    public double? VOrderShare { get; }

    /// <summary>VOrderBytesShare: the bytes of the V-Ordered files over those of the files whose <c>isVOrdered</c> is known. From the <c>files</c> table only.</summary>
    public double? VOrderBytesShare { get; }
}

/// <summary>The partitions of a table, of section 10.5 of the object model.</summary>
public sealed class PartitionMeasures
{
    internal PartitionMeasures(long? count, long kept, DateTimeOffset? oldestModifiedAt, DateTimeOffset? newestModifiedAt)
    {
        Count = count;
        Kept = kept;
        OldestModifiedAt = oldestModifiedAt;
        NewestModifiedAt = newestModifiedAt;
    }

    /// <summary>Partitions: <c>partitionCount</c>, else the partitions kept. Null when the table has neither.</summary>
    public long? Count { get; }

    /// <summary>PartitionsKept: the rows of the <c>partitions</c> table for the table, 0 when there are none.</summary>
    public long Kept { get; }

    /// <summary>PartitionsOldestModifiedAt: the earliest <c>lastModifiedAt</c> of the partitions kept.</summary>
    public DateTimeOffset? OldestModifiedAt { get; }

    /// <summary>PartitionsNewestModifiedAt: the latest <c>lastModifiedAt</c> of the partitions kept.</summary>
    public DateTimeOffset? NewestModifiedAt { get; }
}

/// <summary>What the history of a table says about its pace and its maintenance.</summary>
public sealed class MaintenanceMeasures
{
    internal MaintenanceMeasures(DateTimeOffset? lastCommitAt, double? commitsPerDay, long? daysSinceLastCommit, bool? hasVacuum, DateTimeOffset? lastVacuumAt, bool? hasOptimize, DateTimeOffset? lastOptimizeAt)
    {
        LastCommitAt = lastCommitAt;
        CommitsPerDay = commitsPerDay;
        DaysSinceLastCommit = daysSinceLastCommit;
        HasVacuum = hasVacuum;
        LastVacuumAt = lastVacuumAt;
        HasOptimize = hasOptimize;
        LastOptimizeAt = lastOptimizeAt;
    }

    /// <summary>The newest commit read: <c>snapshotTimestamp</c>, else the latest <c>lastAt</c> of the operations of the table, else <c>lastModifiedAt</c>.</summary>
    public DateTimeOffset? LastCommitAt { get; }

    /// <summary>Commits per day, from <see cref="StorageMeasures.CommitsPerDay"/> with <c>commitsRead</c>.</summary>
    public double? CommitsPerDay { get; }

    /// <summary>Whole days from <see cref="LastCommitAt"/> to the end of the collection, else its start, at least 0.</summary>
    public long? DaysSinceLastCommit { get; }

    /// <summary>Whether the name of an operation of the table contains <c>VACUUM</c>, in any case. Null when the table has no operation.</summary>
    public bool? HasVacuum { get; }

    /// <summary>The latest <c>lastAt</c> of those operations.</summary>
    public DateTimeOffset? LastVacuumAt { get; }

    /// <summary>Whether the name of an operation of the table contains <c>OPTIMIZE</c>, in any case. Null when the table has no operation.</summary>
    public bool? HasOptimize { get; }

    /// <summary>The latest <c>lastAt</c> of those operations.</summary>
    public DateTimeOffset? LastOptimizeAt { get; }
}

/// <summary>The derived measures of one row of <c>tables</c>.</summary>
public sealed class TableStorage
{
    internal TableStorage(long? id, int row, string? name, string label, long? rowCount, long? fileCount, long? fileSize, StorageStack stack, SmallFiles smallFiles, SmallFiles tinyFiles, FileMeasures files, TableMeasures measures, MaintenanceMeasures maintenance, PartitionMeasures partitions)
    {
        Files = files;
        Partitions = partitions;
        Id = id;
        Row = row;
        Name = name;
        Label = label;
        RowCount = rowCount;
        FileCount = fileCount;
        FileSize = fileSize;
        Stack = stack;
        SmallFiles = smallFiles;
        TinyFiles = tinyFiles;
        Measures = measures;
        Maintenance = maintenance;
    }

    /// <summary>The <c>id</c> of the table. Null for a row whose id is not positive or repeats an earlier row.</summary>
    public long? Id { get; }

    /// <summary>The position of the row in <c>tables</c>, from 0.</summary>
    public int Row { get; }

    /// <summary><c>name</c> of the table.</summary>
    public string? Name { get; }

    /// <summary>The namespace and the name joined by dots, else <c>Table &lt;id&gt;</c>, else <c>Table at row &lt;n&gt;</c>.</summary>
    public string Label { get; }

    /// <summary><c>rowCount</c>.</summary>
    public long? RowCount { get; }

    /// <summary><c>fileCount</c>.</summary>
    public long? FileCount { get; }

    /// <summary><c>fileSize</c>.</summary>
    public long? FileSize { get; }

    /// <summary>The storage stack.</summary>
    public StorageStack Stack { get; }

    /// <summary>The files below <see cref="MeasureOptions.SmallFileSize"/>, 128 MiB by default.</summary>
    public SmallFiles SmallFiles { get; }

    /// <summary>The files below <see cref="MeasureOptions.TinyFileSize"/>, 8 MiB by default.</summary>
    public SmallFiles TinyFiles { get; }

    /// <summary>The file size figures and the V-Order shares.</summary>
    public FileMeasures Files { get; }

    /// <summary>The ratios of the table.</summary>
    public TableMeasures Measures { get; }

    /// <summary>The pace of the commits, and the VACUUM and OPTIMIZE among them.</summary>
    public MaintenanceMeasures Maintenance { get; }

    /// <summary>The partitions.</summary>
    public PartitionMeasures Partitions { get; }

    /// <summary>
    /// Writes the table as one JSON object of <c>pqax tables --json</c>, with the names of the storage views: <c>id</c>,
    /// <c>name</c>, <c>label</c>, <c>rowCount</c>, <c>fileCount</c>, <c>fileSize</c>, <c>stack</c>, <c>smallFiles</c>,
    /// <c>tinyFiles</c>, <c>measures</c>, and <c>maintenance</c> with the pace of the commits. A missing value is
    /// <c>null</c>. The other measures, such as <see cref="Files"/> and <see cref="Partitions"/>, are not written.
    /// </summary>
    /// <param name="writer">The writer, positioned where a value goes.</param>
    public void WriteTo(Utf8JsonWriter writer)
    {
        if (writer == null)
        {
            throw new ArgumentNullException(nameof(writer));
        }

        writer.WriteStartObject();
        Number(writer, "id", Id);
        String(writer, "name", Name);
        writer.WriteString("label", Label);
        Number(writer, "rowCount", RowCount);
        Number(writer, "fileCount", FileCount);
        Number(writer, "fileSize", FileSize);
        writer.WriteStartObject("stack");
        foreach (string key in StorageMeasures.StackComponents)
        {
            Number(writer, key, Stack.Component(key));
        }

        String(writer, "deletedSource", Stack.DeletedSource);
        writer.WriteBoolean("listed", Stack.Listed);
        Number(writer, "data", Stack.Data);
        Number(writer, "total", Stack.Total);
        Number(writer, "waste", Stack.Waste);
        Number(writer, "overhead", Stack.Overhead);
        Number(writer, "wasteShare", Stack.WasteShare);
        Number(writer, "overheadShare", Stack.OverheadShare);
        writer.WriteEndObject();
        foreach (KeyValuePair<string, SmallFiles> pair in new[] { new KeyValuePair<string, SmallFiles>("smallFiles", SmallFiles), new KeyValuePair<string, SmallFiles>("tinyFiles", TinyFiles) })
        {
            writer.WriteStartObject(pair.Key);
            writer.WriteNumber("threshold", pair.Value.Threshold);
            Number(writer, "count", pair.Value.Count);
            Number(writer, "shareAtLeast", pair.Value.ShareAtLeast);
            writer.WriteEndObject();
        }

        writer.WriteStartObject("measures");
        Number(writer, "compressionRatio", Measures.CompressionRatio);
        Number(writer, "deletedRowShare", Measures.DeletedRowShare);
        Number(writer, "averageFileSize", Measures.AverageFileSize);
        Number(writer, "averageRowGroupRows", Measures.AverageRowGroupRows);
        Number(writer, "footerCoverage", Measures.FooterCoverage);
        Number(writer, "staleColumnShare", Measures.StaleColumnShare);
        Number(writer, "nonLiveShare", Measures.NonLiveShare);
        Number(writer, "averagePartitionSize", Measures.AveragePartitionSize);
        Number(writer, "dictionaryFallbackShare", Measures.DictionaryFallbackShare);
        writer.WriteEndObject();
        writer.WriteStartObject("maintenance");
        String(writer, "lastCommitAt", Maintenance.LastCommitAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        Number(writer, "commitsPerDay", Maintenance.CommitsPerDay);
        Number(writer, "daysSinceLastCommit", Maintenance.DaysSinceLastCommit);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void Number(Utf8JsonWriter writer, string name, long? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void String(Utf8JsonWriter writer, string name, string? value)
    {
        if (value != null)
        {
            writer.WriteString(name, value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}

/// <summary>The measures of one row of <c>columns</c>, of section 10.6 of the object model.</summary>
public sealed class ColumnStorage
{
    internal ColumnStorage(long? id, int row, long? table, string? name, string label, double? nullShare, double? compressionRatio, double? dictionaryShare, double? dictionaryFallbackShare, double? averageValueLength, double? filesWithoutColumnShare)
    {
        Id = id;
        Row = row;
        Table = table;
        Name = name;
        Label = label;
        NullShare = nullShare;
        CompressionRatio = compressionRatio;
        DictionaryShare = dictionaryShare;
        DictionaryFallbackShare = dictionaryFallbackShare;
        AverageValueLength = averageValueLength;
        FilesWithoutColumnShare = filesWithoutColumnShare;
    }

    /// <summary>The <c>id</c> of the column. Null for a row whose id is not positive or repeats an earlier row.</summary>
    public long? Id { get; }

    /// <summary>The position of the row in <c>columns</c>, from 0.</summary>
    public int Row { get; }

    /// <summary>The <c>id</c> of its table, null when no table of the file has it.</summary>
    public long? Table { get; }

    /// <summary><c>name</c> of the column.</summary>
    public string? Name { get; }

    /// <summary>The name, else <c>Column &lt;id&gt;</c>, else <c>Column at row &lt;n&gt;</c>.</summary>
    public string Label { get; }

    /// <summary><c>min(1, nullCount / valueCount)</c>.</summary>
    public double? NullShare { get; }

    /// <summary><c>uncompressedSize / compressedSize</c>.</summary>
    public double? CompressionRatio { get; }

    /// <summary><c>dictionaryChunkCount / chunkCount</c>.</summary>
    public double? DictionaryShare { get; }

    /// <summary><c>fallbackChunkCount / dictionaryChunkCount</c>.</summary>
    public double? DictionaryFallbackShare { get; }

    /// <summary><c>unencodedByteArrayDataBytes / (valueCount - nullCount)</c>: the average length of a variable-length value.</summary>
    public double? AverageValueLength { get; }

    /// <summary><c>fileCountWithoutColumn</c> over <c>footerCount</c> of its table.</summary>
    public double? FilesWithoutColumnShare { get; }
}

/// <summary>The measures of the collection, of section 10.7 of the object model. A sum is null when no table has the value.</summary>
public sealed class CollectionMeasures
{
    internal CollectionMeasures(long sources, long tables, long columns, long? files, long? fileSize, long? rowCount, double? footprint, double? waste, double? overhead, IReadOnlyList<KeyValuePair<string, long>> depths)
    {
        Sources = sources;
        Tables = tables;
        Columns = columns;
        Files = files;
        FileSize = fileSize;
        RowCount = rowCount;
        Footprint = footprint;
        Waste = waste;
        Overhead = overhead;
        var byDepth = new Dictionary<string, long>();
        foreach (KeyValuePair<string, long> pair in depths)
        {
            byDepth.Add(pair.Key, pair.Value);
        }

        Depths = byDepth;
    }

    /// <summary>The sources with a distinct, positive <c>id</c>.</summary>
    public long Sources { get; }

    /// <summary>The rows of <c>tables</c>.</summary>
    public long Tables { get; }

    /// <summary>The rows of <c>columns</c>.</summary>
    public long Columns { get; }

    /// <summary>The sum of <c>fileCount</c> over the tables that have it.</summary>
    public long? Files { get; }

    /// <summary>The sum of <c>fileSize</c> over the tables that have it.</summary>
    public long? FileSize { get; }

    /// <summary>The sum of <c>rowCount</c> over the tables that have it.</summary>
    public long? RowCount { get; }

    /// <summary>The sum of <see cref="StorageStack.Total"/> over the tables whose total is not null.</summary>
    public double? Footprint { get; }

    /// <summary>The sum of <see cref="StorageStack.Waste"/> over the tables whose waste is not null.</summary>
    public double? Waste { get; }

    /// <summary>The sum of <see cref="StorageStack.Overhead"/> over the tables whose overhead is not null.</summary>
    public double? Overhead { get; }

    /// <summary>The number of tables for each <c>detailDepth</c>, in the order the depths first appear; <c>Unknown</c> for a table without one.</summary>
    public IReadOnlyDictionary<string, long> Depths { get; }
}

/// <summary>The derived measures of a footprint: one <see cref="TableStorage"/> for each row of <c>tables</c>, one <see cref="ColumnStorage"/> for each row of <c>columns</c>, and the collection.</summary>
public sealed class FootprintMeasures
{
    internal FootprintMeasures(IReadOnlyList<TableStorage> tables, IReadOnlyList<ColumnStorage> columns, CollectionMeasures collection, bool filesRead, IReadOnlyList<string> warnings)
    {
        Tables = tables;
        Columns = columns;
        Collection = collection;
        FilesRead = filesRead;
        Warnings = warnings;
    }

    /// <summary>The tables, in the order of the file.</summary>
    public IReadOnlyList<TableStorage> Tables { get; }

    /// <summary>The columns, in the order of the file.</summary>
    public IReadOnlyList<ColumnStorage> Columns { get; }

    /// <summary>The measures of the collection.</summary>
    public CollectionMeasures Collection { get; }

    /// <summary>Whether the <c>files</c> table was read and has rows.</summary>
    public bool FilesRead { get; }

    /// <summary>Problems met while reading, such as a <c>files</c> table that could not be read.</summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>The thresholds of the small files.</summary>
public sealed class MeasureOptions
{
    /// <summary>The size below which a file is small, in bytes. A value that is not positive takes <see cref="StorageMeasures.DefaultSmallFileSize"/>.</summary>
    public long SmallFileSize { get; set; } = StorageMeasures.DefaultSmallFileSize;

    /// <summary>The size below which a file is very small, in bytes. A value that is not positive takes <see cref="StorageMeasures.DefaultTinyFileSize"/>.</summary>
    public long TinyFileSize { get; set; } = StorageMeasures.DefaultTinyFileSize;
}
