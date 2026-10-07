namespace Pqax.Collector;

/// <summary>The progress of a collection, reported each time a table is done.</summary>
public sealed class CollectionProgress
{
    /// <summary>Creates a report.</summary>
    /// <param name="table">The table that is done.</param>
    /// <param name="tablesCompleted">The tables done so far, this one included.</param>
    /// <param name="tableCount">The tables of the collection.</param>
    /// <param name="footersRead">The footers read for this table.</param>
    /// <param name="issueCount">The issues of this table.</param>
    /// <param name="requestCount">The storage requests of the collection so far.</param>
    /// <param name="bytesRead">The bytes read by the collection so far.</param>
    public CollectionProgress(TableLocation table, int tablesCompleted, int tableCount, int footersRead, int issueCount, long requestCount, long bytesRead)
    {
        Table = table;
        TablesCompleted = tablesCompleted;
        TableCount = tableCount;
        FootersRead = footersRead;
        IssueCount = issueCount;
        RequestCount = requestCount;
        BytesRead = bytesRead;
    }

    /// <summary>The table that is done.</summary>
    public TableLocation Table { get; }

    /// <summary>The tables done so far, this one included.</summary>
    public int TablesCompleted { get; }

    /// <summary>The tables of the collection.</summary>
    public int TableCount { get; }

    /// <summary>The footers read for this table.</summary>
    public int FootersRead { get; }

    /// <summary>The issues of this table.</summary>
    public int IssueCount { get; }

    /// <summary>The storage requests of the collection so far.</summary>
    public long RequestCount { get; }

    /// <summary>The bytes read by the collection so far.</summary>
    public long BytesRead { get; }
}
