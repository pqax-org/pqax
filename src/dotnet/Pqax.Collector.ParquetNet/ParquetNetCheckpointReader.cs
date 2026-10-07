using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Parquet;
using Parquet.Schema;

namespace Pqax.Collector.ParquetNet;

/// <summary>
/// Reads Delta checkpoints with Parquet.Net: a classic checkpoint, one part of a multi-part checkpoint, a v2 checkpoint
/// in Parquet, or a sidecar file. It returns the <c>add</c>, <c>remove</c>, <c>metaData</c>, <c>protocol</c>,
/// <c>domainMetadata</c>, <c>sidecar</c>, and <c>checkpointMetadata</c> actions. It reads the file whole, through the
/// storage of the collection, in one request for a file under 64 MB.
/// </summary>
/// <remarks>
/// Fabric writes some checkpoint fields as required inside optional structs, which Parquet.Net returns without the rows
/// where the struct is missing; the reader lines the values up with the rows from the definition levels. It reads the
/// statistics as JSON text when the checkpoint has them so, and as numbers from <c>stats_parsed</c> otherwise, keeping
/// the number of records and the null counts only.
/// </remarks>
public sealed class ParquetNetCheckpointReader : ICheckpointReader
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeltaAction>> ReadAsync(IStorage storage, string path, CancellationToken cancellation)
    {
        if (storage == null)
        {
            throw new ArgumentNullException(nameof(storage));
        }

        byte[] content = await storage.ReadAllAsync(path, cancellation).ConfigureAwait(false);
        using var stream = new MemoryStream(content, false);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, cancellation).ConfigureAwait(false);
        var fields = new Dictionary<string, DataField>(StringComparer.OrdinalIgnoreCase);
        foreach (DataField field in reader.Schema.GetDataFields())
        {
            fields[string.Join("/", field.Path.ToList())] = field;
        }

        var actions = new List<DeltaAction>();
        for (int g = 0; g < reader.RowGroupCount; g++)
        {
            using ParquetRowGroupReader group = reader.OpenRowGroupReader(g);
            await ReadRowGroupAsync(new CheckpointColumns(group, fields, cancellation), actions).ConfigureAwait(false);
        }

        return actions;
    }

    private static async Task ReadRowGroupAsync(CheckpointColumns columns, List<DeltaAction> actions)
    {
        int rows = columns.Rows;

        object?[]? addPath = await columns.ScalarAsync("add/path").ConfigureAwait(false);
        if (addPath != null)
        {
            await ReadAddsAsync(columns, addPath, actions).ConfigureAwait(false);
        }

        object?[]? removePath = await columns.ScalarAsync("remove/path").ConfigureAwait(false);
        if (removePath != null)
        {
            object?[]? size = await columns.ScalarAsync("remove/size").ConfigureAwait(false);
            object?[]? deletion = await columns.ScalarAsync("remove/deletionTimestamp").ConfigureAwait(false);
            Func<int, DeletionVectorDescriptor?> vectors = await VectorsAsync(columns, "remove").ConfigureAwait(false);
            for (int i = 0; i < rows; i++)
            {
                if (removePath[i] is string path)
                {
                    actions.Add(new RemoveAction { Path = path, Size = Long(size, i), DeletionTimestamp = Long(deletion, i), DeletionVector = vectors(i) });
                }
            }
        }

        object?[]? metaId = await columns.ScalarAsync("metaData/id").ConfigureAwait(false);
        if (metaId != null)
        {
            object?[]? name = await columns.ScalarAsync("metaData/name").ConfigureAwait(false);
            object?[]? description = await columns.ScalarAsync("metaData/description").ConfigureAwait(false);
            object?[]? provider = await columns.ScalarAsync("metaData/format/provider").ConfigureAwait(false);
            object?[]? schema = await columns.ScalarAsync("metaData/schemaString").ConfigureAwait(false);
            object?[]? created = await columns.ScalarAsync("metaData/createdTime").ConfigureAwait(false);
            List<string>?[]? partitionColumns = await columns.ListAsync("metaData/partitionColumns").ConfigureAwait(false);
            Dictionary<string, string?>?[]? configuration = await columns.MapAsync("metaData/configuration").ConfigureAwait(false);
            for (int i = 0; i < rows; i++)
            {
                if (metaId[i] is string id)
                {
                    actions.Add(new MetadataAction
                    {
                        Id = id,
                        Name = name?[i] as string,
                        Description = description?[i] as string,
                        Provider = provider?[i] as string,
                        SchemaString = schema?[i] as string,
                        PartitionColumns = (IReadOnlyList<string>?)partitionColumns?[i] ?? new string[0],
                        Configuration = Strings(configuration?[i]),
                        CreatedTime = Long(created, i),
                    });
                }
            }
        }

        object?[]? minReader = await columns.ScalarAsync("protocol/minReaderVersion").ConfigureAwait(false);
        if (minReader != null)
        {
            object?[]? minWriter = await columns.ScalarAsync("protocol/minWriterVersion").ConfigureAwait(false);
            List<string>?[]? readerFeatures = await columns.ListAsync("protocol/readerFeatures").ConfigureAwait(false);
            List<string>?[]? writerFeatures = await columns.ListAsync("protocol/writerFeatures").ConfigureAwait(false);
            for (int i = 0; i < rows; i++)
            {
                if (Long(minReader, i) is long readerVersion)
                {
                    actions.Add(new ProtocolAction
                    {
                        MinReaderVersion = readerVersion,
                        MinWriterVersion = Long(minWriter, i),
                        ReaderFeatures = readerFeatures?[i],
                        WriterFeatures = writerFeatures?[i],
                    });
                }
            }
        }

        object?[]? domain = await columns.ScalarAsync("domainMetadata/domain").ConfigureAwait(false);
        if (domain != null)
        {
            object?[]? configuration = await columns.ScalarAsync("domainMetadata/configuration").ConfigureAwait(false);
            object?[]? removed = await columns.ScalarAsync("domainMetadata/removed").ConfigureAwait(false);
            for (int i = 0; i < rows; i++)
            {
                if (domain[i] is string name)
                {
                    actions.Add(new DomainMetadataAction { Domain = name, Configuration = configuration?[i] as string, Removed = removed?[i] is true });
                }
            }
        }

        object?[]? sidecar = await columns.ScalarAsync("sidecar/path").ConfigureAwait(false);
        if (sidecar != null)
        {
            object?[]? size = await columns.ScalarAsync("sidecar/sizeInBytes").ConfigureAwait(false);
            for (int i = 0; i < rows; i++)
            {
                if (sidecar[i] is string path)
                {
                    actions.Add(new SidecarAction { Path = path, SizeInBytes = Long(size, i) });
                }
            }
        }

        object?[]? checkpointVersion = await columns.ScalarAsync("checkpointMetadata/version").ConfigureAwait(false);
        if (checkpointVersion != null)
        {
            for (int i = 0; i < rows; i++)
            {
                if (Long(checkpointVersion, i) is long version)
                {
                    actions.Add(new CheckpointMetadataAction { Version = version });
                }
            }
        }
    }

    private static async Task ReadAddsAsync(CheckpointColumns columns, object?[] addPath, List<DeltaAction> actions)
    {
        int rows = columns.Rows;
        object?[]? size = await columns.ScalarAsync("add/size").ConfigureAwait(false);
        object?[]? time = await columns.ScalarAsync("add/modificationTime").ConfigureAwait(false);
        object?[]? stats = await columns.ScalarAsync("add/stats").ConfigureAwait(false);
        object?[]? records = await columns.ScalarAsync("add/stats_parsed/numRecords").ConfigureAwait(false);
        object?[]? clustering = await columns.ScalarAsync("add/clusteringProvider").ConfigureAwait(false);
        Dictionary<string, string?>?[]? partitionValues = await columns.MapAsync("add/partitionValues").ConfigureAwait(false);
        Dictionary<string, string?>?[]? tags = await columns.MapAsync("add/tags").ConfigureAwait(false);
        Func<int, DeletionVectorDescriptor?> vectors = await VectorsAsync(columns, "add").ConfigureAwait(false);

        // The null counts of the top-level columns, when the checkpoint has the statistics as a struct.
        var nullCounts = new List<KeyValuePair<string, object?[]>>();
        foreach (string path in columns.Children("add/stats_parsed/nullCount", 4))
        {
            object?[]? values = await columns.ScalarAsync(path).ConfigureAwait(false);
            if (values != null)
            {
                nullCounts.Add(new KeyValuePair<string, object?[]>(path.Substring(path.LastIndexOf('/') + 1), values));
            }
        }

        for (int i = 0; i < rows; i++)
        {
            if (!(addPath[i] is string path))
            {
                continue;
            }

            Dictionary<string, long>? counts = null;
            foreach (KeyValuePair<string, object?[]> column in nullCounts)
            {
                if (Long(column.Value, i) is long count)
                {
                    (counts ??= new Dictionary<string, long>(StringComparer.Ordinal))[column.Key] = count;
                }
            }

            actions.Add(new AddAction
            {
                Path = path,
                Size = Long(size, i),
                ModificationTime = Long(time, i),
                Stats = stats?[i] as string,
                NumRecords = Long(records, i),
                NullCounts = counts,
                PartitionValues = (IReadOnlyDictionary<string, string?>?)partitionValues?[i] ?? (partitionValues != null ? new Dictionary<string, string?>() : null),
                Tags = tags?[i],
                DeletionVector = vectors(i),
                ClusteringProvider = clustering?[i] as string,
            });
        }
    }

    /// <summary>The deletion vector of each row of an <c>add</c> or a <c>remove</c>, null where the row has none.</summary>
    private static async Task<Func<int, DeletionVectorDescriptor?>> VectorsAsync(CheckpointColumns columns, string action)
    {
        object?[]? storage = await columns.ScalarAsync(action + "/deletionVector/storageType").ConfigureAwait(false);
        if (storage == null)
        {
            return _ => null;
        }

        object?[]? location = await columns.ScalarAsync(action + "/deletionVector/pathOrInlineDv").ConfigureAwait(false);
        object?[]? offset = await columns.ScalarAsync(action + "/deletionVector/offset").ConfigureAwait(false);
        object?[]? size = await columns.ScalarAsync(action + "/deletionVector/sizeInBytes").ConfigureAwait(false);
        object?[]? cardinality = await columns.ScalarAsync(action + "/deletionVector/cardinality").ConfigureAwait(false);
        return i => storage[i] is string type
            ? new DeletionVectorDescriptor
            {
                StorageType = type,
                PathOrInlineDv = location?[i] as string,
                Offset = Long(offset, i),
                SizeInBytes = Long(size, i),
                Cardinality = Long(cardinality, i),
            }
            : null;
    }

    private static long? Long(object?[]? values, int row)
    {
        object? value = values?[row];
        if (value == null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> Strings(Dictionary<string, string?>? map)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (map != null)
        {
            foreach (KeyValuePair<string, string?> entry in map)
            {
                if (entry.Value != null)
                {
                    result[entry.Key] = entry.Value;
                }
            }
        }

        return result;
    }
}
