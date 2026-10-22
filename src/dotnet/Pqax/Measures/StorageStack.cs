using System;

namespace Pqax;

/// <summary>The inputs of a storage stack, as <see cref="StorageMeasures.StackOf"/> takes them. A null input is a component that is absent.</summary>
public sealed class StackInput
{
    /// <summary><c>fileSize</c> of the table: the bytes of its live data files.</summary>
    public double? FileSize { get; set; }

    /// <summary>The bytes of the deleted rows, from <see cref="StorageMeasures.DeletedEstimate"/>.</summary>
    public double? Deleted { get; set; }

    /// <summary><c>footerSize</c>: the footers, inside the data files.</summary>
    public double? FooterSize { get; set; }

    /// <summary><c>staleColumnSize</c>: the stale columns, inside the data files.</summary>
    public double? StaleColumnSize { get; set; }

    /// <summary><c>nonLiveFileSize</c>: the files that left the table and that the log still tracks.</summary>
    public double? NonLiveFileSize { get; set; }

    /// <summary><c>unreferencedFileSize</c>, from format 0.2: the data files of the storage that the version does not reference.</summary>
    public double? UnreferencedFileSize { get; set; }

    /// <summary><c>metadataSize</c>: the files of the table format.</summary>
    public double? MetadataSize { get; set; }

    /// <summary>Where the deleted rows come from: <c>files</c> or <c>table</c>. Kept only when <see cref="Deleted"/> is known.</summary>
    public string? DeletedSource { get; set; }
}

/// <summary>
/// The storage stack of a table: where its bytes go, in seven components. A component is null when the file lacks the
/// field it needs, never 0. The sums count an absent component as 0, so they are never null.
/// </summary>
public sealed class StorageStack
{
    internal StorageStack(double? live, double? nonLive, double? unreferenced, double? deleted, double? stale, double? metadata, double? footers, string? deletedSource, bool listed, double data, double waste, double overhead)
    {
        Live = live;
        NonLive = nonLive;
        Unreferenced = unreferenced;
        Deleted = deleted;
        Stale = stale;
        Metadata = metadata;
        Footers = footers;
        DeletedSource = deletedSource;
        Listed = listed;
        Data = data;
        Waste = waste;
        Overhead = overhead;
        Total = data + overhead;
        WasteShare = data > 0 ? waste / data : null;
        OverheadShare = data > 0 ? overhead / data : null;
    }

    /// <summary><c>live</c>: <c>max(0, fileSize - deleted - footerSize - staleColumnSize)</c>, a missing term counting as 0. Null when <c>fileSize</c> is missing.</summary>
    public double? Live { get; }

    /// <summary><c>nonLive</c>: <c>min(nonLiveFileSize, unreferencedFileSize)</c> when the unreferenced files are known, else <c>nonLiveFileSize</c>, an upper bound.</summary>
    public double? NonLive { get; }

    /// <summary><c>unreferenced</c>: <c>unreferencedFileSize</c> minus <see cref="NonLive"/>. Null without <c>unreferencedFileSize</c>.</summary>
    public double? Unreferenced { get; }

    /// <summary><c>deleted</c>: the estimate of the bytes of the deleted rows.</summary>
    public double? Deleted { get; }

    /// <summary><c>stale</c>: <c>staleColumnSize</c>.</summary>
    public double? Stale { get; }

    /// <summary><c>metadata</c>: <c>metadataSize</c>.</summary>
    public double? Metadata { get; }

    /// <summary><c>footers</c>: <c>footerSize</c>.</summary>
    public double? Footers { get; }

    /// <summary>Where <see cref="Deleted"/> comes from: <c>files</c>, the <c>files</c> table, or <c>table</c>, the totals of the table. Null when it is null.</summary>
    public string? DeletedSource { get; }

    /// <summary>Whether a listing of the storage gave the unreferenced files, so that the non-live component holds only what is still stored.</summary>
    public bool Listed { get; }

    /// <summary>The live data and the waste: the denominator of the shares.</summary>
    public double Data { get; }

    /// <summary>The data and the overhead.</summary>
    public double Total { get; }

    /// <summary><c>waste</c>: non-live + unreferenced + deleted + stale.</summary>
    public double Waste { get; }

    /// <summary><c>overhead</c>: metadata + footers, outside the waste.</summary>
    public double Overhead { get; }

    /// <summary><see cref="Waste"/> over <see cref="Data"/>, null when the data is 0.</summary>
    public double? WasteShare { get; }

    /// <summary><see cref="Overhead"/> over <see cref="Data"/>, null when the data is 0.</summary>
    public double? OverheadShare { get; }

    /// <summary>A component by its name in <see cref="StorageMeasures.StackComponents"/>, such as <c>nonLive</c>.</summary>
    /// <param name="key">The name of the component.</param>
    /// <returns>Its bytes, null when it is absent.</returns>
    /// <exception cref="ArgumentException">The name is not one of a component.</exception>
    public double? Component(string key) => key switch
    {
        "live" => Live,
        "nonLive" => NonLive,
        "unreferenced" => Unreferenced,
        "deleted" => Deleted,
        "stale" => Stale,
        "metadata" => Metadata,
        "footers" => Footers,
        _ => throw new ArgumentException($"'{key}' is not a component of a stack.", nameof(key)),
    };
}
