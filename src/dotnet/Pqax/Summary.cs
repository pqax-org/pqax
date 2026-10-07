namespace Pqax;

/// <summary>
/// The fifteen summary tables of a PQAX file, read from <c>summary.json</c> when the document opens. A table that the
/// file does not have is empty.
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

    /// <summary>The <c>partitionSpecs</c> table.</summary>
    public PqaxTable<PartitionSpecRow> PartitionSpecs { get; }

    /// <summary>The <c>partitionFields</c> table.</summary>
    public PqaxTable<PartitionFieldRow> PartitionFields { get; }

    /// <summary>The <c>sortOrders</c> table.</summary>
    public PqaxTable<SortOrderRow> SortOrders { get; }

    /// <summary>The <c>sortFields</c> table.</summary>
    public PqaxTable<SortFieldRow> SortFields { get; }

    /// <summary>The <c>physicalSchemas</c> table.</summary>
    public PqaxTable<PhysicalSchemaRow> PhysicalSchemas { get; }

    /// <summary>The <c>physicalColumns</c> table.</summary>
    public PqaxTable<PhysicalColumnRow> PhysicalColumns { get; }

    /// <summary>The <c>writers</c> table.</summary>
    public PqaxTable<WriterRow> Writers { get; }

    /// <summary>The <c>operations</c> table.</summary>
    public PqaxTable<OperationRow> Operations { get; }

    /// <summary>The <c>issues</c> table: problems met while collecting.</summary>
    public PqaxTable<IssueRow> Issues { get; }

    /// <summary>The <c>modelBindings</c> table.</summary>
    public PqaxTable<ModelBindingRow> ModelBindings { get; }

    /// <summary>The <c>tableBindings</c> table.</summary>
    public PqaxTable<TableBindingRow> TableBindings { get; }

    /// <summary>The <c>columnBindings</c> table.</summary>
    public PqaxTable<ColumnBindingRow> ColumnBindings { get; }
}
