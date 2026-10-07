using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pqax.Collector;

/// <summary>
/// Parses the actions of a Delta commit file, or of a checkpoint in JSON: one JSON object per line. It never reads the
/// user names, notebook identifiers, or cluster identifiers of a <c>commitInfo</c>, and it reads the parameters of an
/// operation only when asked.
/// </summary>
internal static class DeltaJsonParser
{
    /// <summary>Parses every line of a file.</summary>
    /// <exception cref="JsonException">A line is not valid JSON.</exception>
    public static List<DeltaAction> Parse(byte[] content, bool includeParameters)
    {
        var actions = new List<DeltaAction>();
        int start = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
        while (start < content.Length)
        {
            int end = Array.IndexOf(content, (byte)'\n', start);
            if (end < 0)
            {
                end = content.Length;
            }

            int first = start;
            int last = end - 1;
            while (first <= last && IsSpace(content[first]))
            {
                first++;
            }

            while (last >= first && IsSpace(content[last]))
            {
                last--;
            }

            if (last >= first)
            {
                ParseLine(new ReadOnlyMemory<byte>(content, first, last - first + 1), includeParameters, actions);
            }

            start = end + 1;
        }

        return actions;
    }

    private static bool IsSpace(byte b) => b == (byte)' ' || b == (byte)'\r' || b == (byte)'\t' || b == (byte)'\n';

    private static void ParseLine(ReadOnlyMemory<byte> line, bool includeParameters, List<DeltaAction> actions)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A line of the log is not a JSON object.");
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            JsonElement value = property.Value;
            if (value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            switch (property.Name)
            {
                case "add":
                    actions.Add(Add(value));
                    break;
                case "remove":
                    actions.Add(Remove(value));
                    break;
                case "metaData":
                    actions.Add(Metadata(value));
                    break;
                case "protocol":
                    actions.Add(Protocol(value));
                    break;
                case "domainMetadata":
                    actions.Add(new DomainMetadataAction
                    {
                        Domain = Text(value, "domain"),
                        Configuration = Text(value, "configuration"),
                        Removed = Bool(value, "removed") == true,
                    });
                    break;
                case "commitInfo":
                    actions.Add(CommitInfo(value, includeParameters));
                    break;
                case "sidecar":
                    actions.Add(new SidecarAction { Path = Text(value, "path"), SizeInBytes = Long(value, "sizeInBytes") });
                    break;
                case "checkpointMetadata":
                    actions.Add(new CheckpointMetadataAction { Version = Long(value, "version") });
                    break;
            }
        }
    }

    private static AddAction Add(JsonElement value) => new AddAction
    {
        Path = Text(value, "path"),
        PartitionValues = NullableMap(value, "partitionValues"),
        Size = Long(value, "size"),
        ModificationTime = Long(value, "modificationTime"),
        Stats = Text(value, "stats"),
        Tags = NullableMap(value, "tags"),
        DeletionVector = DeletionVector(value),
        ClusteringProvider = Text(value, "clusteringProvider"),
    };

    private static RemoveAction Remove(JsonElement value) => new RemoveAction
    {
        Path = Text(value, "path"),
        Size = Long(value, "size"),
        DeletionTimestamp = Long(value, "deletionTimestamp"),
        DeletionVector = DeletionVector(value),
    };

    private static MetadataAction Metadata(JsonElement value) => new MetadataAction
    {
        Id = Text(value, "id"),
        Name = Text(value, "name"),
        Description = Text(value, "description"),
        Provider = value.TryGetProperty("format", out JsonElement format) && format.ValueKind == JsonValueKind.Object ? Text(format, "provider") : null,
        SchemaString = Text(value, "schemaString"),
        PartitionColumns = List(value, "partitionColumns"),
        Configuration = Map(value, "configuration"),
        CreatedTime = Long(value, "createdTime"),
    };

    private static ProtocolAction Protocol(JsonElement value) => new ProtocolAction
    {
        MinReaderVersion = Long(value, "minReaderVersion"),
        MinWriterVersion = Long(value, "minWriterVersion"),
        ReaderFeatures = List(value, "readerFeatures"),
        WriterFeatures = List(value, "writerFeatures"),
    };

    private static CommitInfoAction CommitInfo(JsonElement value, bool includeParameters) => new CommitInfoAction
    {
        Timestamp = Long(value, "timestamp"),
        InCommitTimestamp = Long(value, "inCommitTimestamp"),
        Operation = Text(value, "operation"),
        EngineInfo = Text(value, "engineInfo"),
        IsBlindAppend = Bool(value, "isBlindAppend"),
        OperationMetrics = Map(value, "operationMetrics"),
        OperationParameters = includeParameters ? Map(value, "operationParameters") : null,
    };

    private static DeletionVectorDescriptor? DeletionVector(JsonElement value)
    {
        if (!value.TryGetProperty("deletionVector", out JsonElement vector) || vector.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new DeletionVectorDescriptor
        {
            StorageType = Text(vector, "storageType"),
            PathOrInlineDv = Text(vector, "pathOrInlineDv"),
            Offset = Long(vector, "offset"),
            SizeInBytes = Long(vector, "sizeInBytes"),
            Cardinality = Long(vector, "cardinality"),
        };
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool? Bool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false : (bool?)null;
    }

    private static IReadOnlyList<string>? List(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString() ?? string.Empty);
            }
        }

        return list;
    }

    private static IReadOnlyDictionary<string, string>? Map(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    map[property.Name] = property.Value.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    break;
                default:
                    map[property.Name] = property.Value.GetRawText();
                    break;
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string?>? NullableMap(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                _ => property.Value.GetRawText(),
            };
        }

        return map;
    }
}
