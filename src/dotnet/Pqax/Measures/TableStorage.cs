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

/// <summary>The ratios of a table that section 9 of the object model names. A ratio is null when a field is missing or its denominator is not positive.</summary>
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

/// <summary>What the history of a table says about its pace.</summary>
public sealed class MaintenanceMeasures
{
    internal MaintenanceMeasures(DateTimeOffset? lastCommitAt, double? commitsPerDay, long? daysSinceLastCommit)
    {
        LastCommitAt = lastCommitAt;
        CommitsPerDay = commitsPerDay;
        DaysSinceLastCommit = daysSinceLastCommit;
    }

    /// <summary>The newest commit read: <c>snapshotTimestamp</c>, else the latest <c>lastAt</c> of the operations of the table, else <c>lastModifiedAt</c>.</summary>
    public DateTimeOffset? LastCommitAt { get; }

    /// <summary>Commits per day, from <see cref="StorageMeasures.CommitsPerDay"/> with <c>commitsRead</c>, else <c>commitCount</c>.</summary>
    public double? CommitsPerDay { get; }

    /// <summary>Whole days from <see cref="LastCommitAt"/> to the end of the collection, else its start, at least 0.</summary>
    public long? DaysSinceLastCommit { get; }
}

/// <summary>The derived measures of one row of <c>tables</c>.</summary>
public sealed class TableStorage
{
    internal TableStorage(long? id, int row, string? name, string label, long? rowCount, long? fileCount, long? fileSize, StorageStack stack, SmallFiles smallFiles, SmallFiles tinyFiles, TableMeasures measures, MaintenanceMeasures maintenance)
    {
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

    /// <summary>The ratios of the table.</summary>
    public TableMeasures Measures { get; }

    /// <summary>The pace of the commits.</summary>
    public MaintenanceMeasures Maintenance { get; }

    /// <summary>
    /// Writes the table as one JSON object with the names of the storage views: <c>id</c>, <c>name</c>, <c>label</c>,
    /// <c>rowCount</c>, <c>fileCount</c>, <c>fileSize</c>, <c>stack</c>, <c>smallFiles</c>, <c>tinyFiles</c>,
    /// <c>measures</c>, and <c>maintenance</c>. A missing value is <c>null</c>.
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
        writer.WriteNumber("data", Stack.Data);
        writer.WriteNumber("total", Stack.Total);
        writer.WriteNumber("waste", Stack.Waste);
        writer.WriteNumber("overhead", Stack.Overhead);
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

/// <summary>The derived measures of a footprint: one <see cref="TableStorage"/> for each row of <c>tables</c>.</summary>
public sealed class FootprintMeasures
{
    internal FootprintMeasures(IReadOnlyList<TableStorage> tables, bool filesRead, IReadOnlyList<string> warnings)
    {
        Tables = tables;
        FilesRead = filesRead;
        Warnings = warnings;
    }

    /// <summary>The tables, in the order of the file.</summary>
    public IReadOnlyList<TableStorage> Tables { get; }

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
