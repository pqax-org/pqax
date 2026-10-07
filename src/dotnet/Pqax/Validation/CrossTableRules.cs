using System;
using System.Collections.Generic;
using System.Globalization;

namespace Pqax;

/// <summary>
/// The rules that join tables, after every part was read: unique ids, references to rows that exist, the depth of
/// each table, and its totals against the detail that the file keeps. The column chunks of a table are found through
/// their physical column and its physical schema. A rule that needs a table with a part that cannot be read, or whose
/// arrays do not pair up, is skipped, and a total that is null is not compared.
/// </summary>
internal sealed class CrossTableRules
{
    private static readonly string[] Depths = { "Summary", "Files", "Row groups", "Column chunks" };

    private readonly IssueLog log;
    private readonly Dictionary<string, TableStore> stores;
    private readonly Dictionary<string, HashSet<long>> ids = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);

    public CrossTableRules(IssueLog log, Dictionary<string, TableStore> stores)
    {
        this.log = log;
        this.stores = stores;
    }

    public void Run()
    {
        Identifiers();
        References();
        DepthAndTotals();
    }

    private static long Get(Dictionary<long, long> map, long key) => map.TryGetValue(key, out long value) ? value : 0;

    private static void AddTo(Dictionary<long, long> map, long key, long value) => map[key] = Get(map, key) + value;

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The stores of the tables of the format, in the order of the model.</summary>
    private IEnumerable<TableStore> KnownStores()
    {
        foreach (TableInfo table in Schemas.AllTables)
        {
            if (stores.TryGetValue(table.Name, out TableStore? store))
            {
                yield return store;
            }
        }
    }

    private TableStore? Find(string name) => stores.TryGetValue(name, out TableStore? store) ? store : null;

    /// <summary>Whether a detail table can be joined: absent, or whole with rows that pair up.</summary>
    private bool Usable(TableStore? store, bool needsUniqueIds = false) => store == null || (store.Usable && (!needsUniqueIds || store.IdsUnique));

    private void Identifiers()
    {
        foreach (TableStore store in KnownStores())
        {
            if (!store.Info!.HasId || store.Rows == 0)
            {
                continue;
            }

            var set = new HashSet<long>();
            int invalid = 0;
            int repeated = 0;
            for (int row = 0; row < store.Rows; row++)
            {
                long? id = store.Integer("id", row);
                if (!id.HasValue || id.Value <= 0)
                {
                    invalid++;
                }
                else if (!set.Add(id.Value))
                {
                    repeated++;
                }
            }

            ids[store.Name] = set;
            store.IdsUnique = invalid == 0 && repeated == 0;
            log.Check(invalid == 0, "table.idUnique", $"{store.Name}.id has {invalid} values that are not positive integers.", store.SinglePart, store.Name, "id");
            log.Check(repeated == 0, "table.idUnique", $"{store.Name}.id repeats {repeated} values.", store.SinglePart, store.Name, "id");
        }
    }

    private void References()
    {
        foreach (TableStore store in KnownStores())
        {
            if (store.Rows == 0 || store.Incomplete)
            {
                continue;
            }

            foreach (FieldInfo field in store.Info!.Fields)
            {
                if (!field.IsReference || (Find(field.Target!)?.Incomplete ?? false))
                {
                    continue;
                }

                ids.TryGetValue(field.Target!, out HashSet<long>? targets);
                int missing = 0;
                long example = 0;
                IEnumerable<long> values = field.Kind == FieldKind.Reference ? Values(store, field.Name) : Lists(store, field.Name);
                foreach (long value in values)
                {
                    if (targets == null || !targets.Contains(value))
                    {
                        example = missing == 0 ? value : example;
                        missing++;
                    }
                }

                log.Check(
                    missing == 0,
                    "table.reference",
                    $"{store.Name}.{field.Name} has {missing} references to rows that {field.Target} does not have, such as {Text(example)}.",
                    store.SinglePart,
                    store.Name,
                    field.Name);
            }
        }
    }

    private IEnumerable<long> Values(TableStore store, string field)
    {
        if (store.Integers.TryGetValue(field, out List<long?>? values))
        {
            foreach (long? value in values)
            {
                if (value.HasValue)
                {
                    yield return value.Value;
                }
            }
        }
    }

    private IEnumerable<long> Lists(TableStore store, string field)
    {
        if (store.Lists.TryGetValue(field, out List<long>? values))
        {
            foreach (long value in values)
            {
                yield return value;
            }
        }
    }

    private void DepthAndTotals()
    {
        TableStore? tables = Find("tables");
        if (tables == null || tables.Rows == 0 || !tables.Usable || !tables.IdsUnique)
        {
            return;
        }

        TableStore? files = Find("files");
        TableStore? rowGroups = Find("rowGroups");
        TableStore? schemas = Find("physicalSchemas");
        TableStore? physical = Find("physicalColumns");
        TableStore? chunks = Find("columnChunks");
        TableStore? columns = Find("columns");
        bool filesOk = Usable(files);
        bool groupsOk = Usable(files, true) && Usable(rowGroups);
        bool chunksOk = Usable(schemas, true) && Usable(physical, true) && Usable(chunks);
        bool columnsOk = Usable(columns, true);

        var fileCount = new Dictionary<long, long>();
        var fileSize = new Dictionary<long, long>();
        var fileSizeUnknown = new HashSet<long>();
        var fileTable = new Dictionary<long, long>();
        if (files != null && filesOk)
        {
            for (int row = 0; row < files.Rows; row++)
            {
                long? table = files.Integer("table", row);
                if (!table.HasValue)
                {
                    continue;
                }

                AddTo(fileCount, table.Value, 1);
                long? size = files.Integer("fileSize", row);
                if (size.HasValue)
                {
                    AddTo(fileSize, table.Value, size.Value);
                }
                else
                {
                    fileSizeUnknown.Add(table.Value);
                }

                long? id = files.Integer("id", row);
                if (id.HasValue)
                {
                    fileTable[id.Value] = table.Value;
                }
            }
        }

        var groupCount = new Dictionary<long, long>();
        if (rowGroups != null && groupsOk)
        {
            for (int row = 0; row < rowGroups.Rows; row++)
            {
                long? file = rowGroups.Integer("file", row);
                if (file.HasValue && fileTable.TryGetValue(file.Value, out long table))
                {
                    AddTo(groupCount, table, 1);
                }
            }
        }

        var schemaTable = new Dictionary<long, long>();
        if (schemas != null)
        {
            for (int row = 0; row < schemas.Rows; row++)
            {
                long? id = schemas.Integer("id", row);
                long? table = schemas.Integer("table", row);
                if (id.HasValue && table.HasValue)
                {
                    schemaTable[id.Value] = table.Value;
                }
            }
        }

        var physicalTable = new Dictionary<long, long>();
        var physicalColumn = new Dictionary<long, long>();
        if (physical != null)
        {
            for (int row = 0; row < physical.Rows; row++)
            {
                long? id = physical.Integer("id", row);
                if (!id.HasValue)
                {
                    continue;
                }

                long? schema = physical.Integer("physicalSchema", row);
                if (schema.HasValue && schemaTable.TryGetValue(schema.Value, out long table))
                {
                    physicalTable[id.Value] = table;
                }

                long? column = physical.Integer("column", row);
                if (column.HasValue)
                {
                    physicalColumn[id.Value] = column.Value;
                }
            }
        }

        var chunkCount = new Dictionary<long, long>();
        var chunkSize = new Dictionary<long, long>();
        var chunkSizeUnknown = new HashSet<long>();
        var columnSize = new Dictionary<long, long>();
        var columnSizeUnknown = new HashSet<long>();
        if (chunks != null && chunksOk)
        {
            for (int row = 0; row < chunks.Rows; row++)
            {
                long? physicalId = chunks.Integer("physicalColumn", row);
                if (!physicalId.HasValue)
                {
                    continue;
                }

                long? size = chunks.Integer("compressedSize", row);
                if (physicalTable.TryGetValue(physicalId.Value, out long table))
                {
                    AddTo(chunkCount, table, 1);
                    if (size.HasValue)
                    {
                        AddTo(chunkSize, table, size.Value);
                    }
                    else
                    {
                        chunkSizeUnknown.Add(table);
                    }
                }

                if (physicalColumn.TryGetValue(physicalId.Value, out long column))
                {
                    if (size.HasValue)
                    {
                        AddTo(columnSize, column, size.Value);
                    }
                    else
                    {
                        columnSizeUnknown.Add(column);
                    }
                }
            }
        }

        // The tables at the Column chunks depth or deeper that have at least one column chunk.
        var columnTotalsTables = new HashSet<long>();
        for (int row = 0; row < tables.Rows; row++)
        {
            long? id = tables.Integer("id", row);
            string? depth = tables.String("detailDepth", row);
            if (!id.HasValue || depth == null)
            {
                continue;
            }

            long table = id.Value;
            int level = Array.IndexOf(Depths, depth);
            if (level < 0)
            {
                // A depth that this version does not know is deeper than Column chunks.
                level = Depths.Length;
            }

            if (filesOk)
            {
                long count = Get(fileCount, table);
                if (level >= 1)
                {
                    long? total = tables.Integer("fileCount", row);
                    if (total.HasValue)
                    {
                        log.Check(total.Value == count, "totals.fileCount", $"The table with id {Text(table)} has fileCount {Text(total.Value)}, and the file has {Text(count)} of its files.", null, "tables", "fileCount");
                    }

                    long? size = tables.Integer("fileSize", row);
                    if (size.HasValue && !fileSizeUnknown.Contains(table))
                    {
                        long sum = Get(fileSize, table);
                        log.Check(size.Value == sum, "totals.fileSize", $"The table with id {Text(table)} has fileSize {Text(size.Value)}, and its files kept add up to {Text(sum)}.", null, "tables", "fileSize");
                    }
                }
                else
                {
                    log.Check(count == 0, "depth.files", $"The table with id {Text(table)} is at the Summary depth, and the file has {Text(count)} of its files.", null, "files", "table");
                }
            }

            if (groupsOk)
            {
                long count = Get(groupCount, table);
                if (level >= 2)
                {
                    long? total = tables.Integer("rowGroupCount", row);
                    if (total.HasValue)
                    {
                        log.Check(total.Value == count, "totals.rowGroupCount", $"The table with id {Text(table)} has rowGroupCount {Text(total.Value)}, and the file has {Text(count)} of its row groups.", null, "tables", "rowGroupCount");
                    }
                }
                else
                {
                    log.Check(count == 0, "depth.rowGroups", $"The table with id {Text(table)} is at the {depth} depth, and the file has {Text(count)} of its row groups.", null, "rowGroups", "file");
                }
            }

            if (chunksOk)
            {
                long count = Get(chunkCount, table);
                if (level >= 3)
                {
                    if (count > 0)
                    {
                        columnTotalsTables.Add(table);
                    }

                    long? total = tables.Integer("compressedSize", row);
                    if (total.HasValue && !chunkSizeUnknown.Contains(table))
                    {
                        long sum = Get(chunkSize, table);
                        log.Check(total.Value == sum, "totals.compressedSize", $"The table with id {Text(table)} has compressedSize {Text(total.Value)}, and its column chunks kept add up to {Text(sum)}.", null, "tables", "compressedSize");
                    }
                }
                else
                {
                    log.Check(count == 0, "depth.columnChunks", $"The table with id {Text(table)} is at the {depth} depth, and the file has {Text(count)} of its column chunks.", null, "columnChunks", "physicalColumn");
                }
            }
        }

        if (columns == null || !columnsOk || !chunksOk)
        {
            return;
        }

        for (int row = 0; row < columns.Rows; row++)
        {
            long? id = columns.Integer("id", row);
            long? table = columns.Integer("table", row);
            long? size = columns.Integer("compressedSize", row);
            if (!id.HasValue || !table.HasValue || !size.HasValue || !columnTotalsTables.Contains(table.Value) || columnSizeUnknown.Contains(id.Value))
            {
                continue;
            }

            long sum = Get(columnSize, id.Value);
            log.Check(size.Value == sum, "totals.columnCompressedSize", $"The column with id {Text(id.Value)} has compressedSize {Text(size.Value)}, and its column chunks kept add up to {Text(sum)}.", null, "columns", "compressedSize");
        }
    }
}
