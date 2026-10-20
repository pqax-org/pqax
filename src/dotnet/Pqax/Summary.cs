namespace Pqax;

/// <summary>
/// The fifteen summary tables of a PQAX file, read from <c>summary.json</c> when the document opens. A table that the
/// file does not have is empty. The six detail tables are not here: the Read methods of <see cref="PqaxDocument"/> read them.
/// </summary>
public sealed class Summary
{
    internal Summary(
        PqaxTable<SourceRow> sources,
        PqaxTable<TableRow> tables,
        PqaxTable<ColumnRow> columns,
        PqaxTable<PartitionSpecRow> partitionSpecs,
        PqaxTable<PartitionFieldRow> partitionFields,
        PqaxTable<SortOrderRow> sortOrders,
        PqaxTable<SortFieldRow> sortFields,
        PqaxTable<PhysicalSchemaRow> physicalSchemas,
        PqaxTable<PhysicalColumnRow> physicalColumns,
        PqaxTable<WriterRow> writers,
        PqaxTable<OperationRow> operations,
        PqaxTable<IssueRow> issues,
        PqaxTable<ModelBindingRow> modelBindings,
        PqaxTable<TableBindingRow> tableBindings,
        PqaxTable<ColumnBindingRow> columnBindings)
    {
        Sources = sources;
        Tables = tables;
        Columns = columns;
        PartitionSpecs = partitionSpecs;
        PartitionFields = partitionFields;
        SortOrders = sortOrders;
        SortFields = sortFields;
        PhysicalSchemas = physicalSchemas;
        PhysicalColumns = physicalColumns;
        Writers = writers;
        Operations = operations;
        Issues = issues;
        ModelBindings = modelBindings;
        TableBindings = tableBindings;
        ColumnBindings = columnBindings;
    }

    /// <summary>The <c>sources</c> table: lakehouses, warehouses, catalogs, or folders.</summary>
    public PqaxTable<SourceRow> Sources { get; }

    /// <summary>The <c>tables</c> table: every table, with its totals.</summary>
    public PqaxTable<TableRow> Tables { get; }

    /// <summary>The <c>columns</c> table: every top-level column, with its totals.</summary>
    public PqaxTable<ColumnRow> Columns { get; }

    /// <summary>The <c>partitionSpecs</c> table: how the rows of each table are assigned to partitions.</summary>
    public PqaxTable<PartitionSpecRow> PartitionSpecs { get; }

    /// <summary>The <c>partitionFields</c> table: the fields of each partition spec, in order.</summary>
    public PqaxTable<PartitionFieldRow> PartitionFields { get; }

    /// <summary>The <c>sortOrders</c> table: the sort orders that the tables declare.</summary>
    public PqaxTable<SortOrderRow> SortOrders { get; }

    /// <summary>The <c>sortFields</c> table: the fields of each sort order, in order.</summary>
    public PqaxTable<SortFieldRow> SortFields { get; }

    /// <summary>The <c>physicalSchemas</c> table: the distinct Parquet schemas found among the files of each table.</summary>
    public PqaxTable<PhysicalSchemaRow> PhysicalSchemas { get; }

    /// <summary>The <c>physicalColumns</c> table: the leaf columns of each physical schema, with their sizes.</summary>
    public PqaxTable<PhysicalColumnRow> PhysicalColumns { get; }

    /// <summary>The <c>writers</c> table: the writing engines and optimizations found in the files of each table.</summary>
    public PqaxTable<WriterRow> Writers { get; }

    /// <summary>The <c>operations</c> table: the kinds of operation in the history of each table.</summary>
    public PqaxTable<OperationRow> Operations { get; }

    /// <summary>The <c>issues</c> table: problems met while collecting.</summary>
    public PqaxTable<IssueRow> Issues { get; }

    /// <summary>The <c>modelBindings</c> table: the semantic models whose tables read the tables of the footprint.</summary>
    public PqaxTable<ModelBindingRow> ModelBindings { get; }

    /// <summary>The <c>tableBindings</c> table: which table of the footprint each table of a model reads.</summary>
    public PqaxTable<TableBindingRow> TableBindings { get; }

    /// <summary>The <c>columnBindings</c> table: which column of the footprint each column of a model reads.</summary>
    public PqaxTable<ColumnBindingRow> ColumnBindings { get; }
}
