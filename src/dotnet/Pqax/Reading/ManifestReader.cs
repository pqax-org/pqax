using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Pqax;

/// <summary>Reads <c>manifest.json</c>. It refuses a file only when the format is not <c>pqax</c> or the first number of the version is unknown.</summary>
internal static class ManifestReader
{
    /// <summary>The first numbers of <c>formatVersion</c> that this library reads.</summary>
    public static readonly IReadOnlyList<int> KnownMajorVersions = new[] { 0 };

    private static readonly HashSet<string> KnownMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "format", "formatVersion", "collection", "obfuscatorDictionaryId", "obfuscatorLib", "obfuscatorLibVersion", "parts",
    };

    public static Manifest Read(byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(JsonRead.WithoutBom(bytes), new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException e)
        {
            throw new PqaxFormatException("json.invalid", $"The part manifest.json is not valid JSON: {e.Message}", e);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new PqaxFormatException("manifest.format", "The manifest is not a JSON object, so it does not say that the format is pqax.");
            }

            Dictionary<string, JsonElement> members = Members(root);
            string? format = StringOf(members, "format");
            if (format != "pqax")
            {
                throw new PqaxFormatException("manifest.format", format == null ? "The manifest does not say that the format is pqax." : $"The format of the file is {format}, not pqax.");
            }

            string? version = StringOf(members, "formatVersion");
            int? major = MajorVersion(version);
            if (version == null || major == null || !Contains(KnownMajorVersions, major.Value))
            {
                throw new PqaxFormatException("manifest.versionUnknown", version == null
                    ? "The manifest does not state the format version."
                    : $"The format version {version} is not one that this library reads. It reads the versions that start with {string.Join(", ", KnownMajorVersions)}.");
            }

            CollectionInfo collection = new CollectionInfo();
            if (members.TryGetValue("collection", out JsonElement collectionElement) && collectionElement.ValueKind == JsonValueKind.Object)
            {
                collection = ReadCollection(collectionElement);
            }

            var parts = new List<PartInfo>();
            if (members.TryGetValue("parts", out JsonElement partsElement) && partsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement part in partsElement.EnumerateArray())
                {
                    PartInfo? info = ReadPart(part);
                    if (info != null)
                    {
                        parts.Add(info);
                    }
                }
            }

            var extensions = new OrderedMap<JsonElement>();
            foreach (KeyValuePair<string, JsonElement> member in members)
            {
                if (!KnownMembers.Contains(member.Key))
                {
                    extensions.Set(member.Key, member.Value.Clone());
                }
            }

            return new Manifest(
                format,
                version,
                collection,
                StringOf(members, "obfuscatorDictionaryId"),
                StringOf(members, "obfuscatorLib"),
                StringOf(members, "obfuscatorLibVersion"),
                parts,
                extensions);
        }
    }

    /// <summary>
    /// The first number of a version: the text before the first dot, when it is made of ASCII digits. <c>0.1</c>,
    /// <c>0</c>, and <c>0.1.2</c> give 0. Null when the text before the dot is not a number.
    /// </summary>
    public static int? MajorVersion(string? version)
    {
        if (version == null)
        {
            return null;
        }

        int dot = version.IndexOf('.');
        string head = dot < 0 ? version : version.Substring(0, dot);
        if (head.Length == 0 || head.Length > 9)
        {
            return null;
        }

        foreach (char c in head)
        {
            if (c < '0' || c > '9')
            {
                return null;
            }
        }

        return int.Parse(head, CultureInfo.InvariantCulture);
    }

    /// <summary>The members of an object, in order. When a name repeats, the last value wins.</summary>
    public static Dictionary<string, JsonElement> Members(JsonElement element)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            members[property.Name] = property.Value;
        }

        return members;
    }

    private static CollectionInfo ReadCollection(JsonElement element)
    {
        // The collection is read with the same schema that writes it, so unknown members land in its extensions.
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(element.GetRawText());
        var reader = new Utf8JsonReader(bytes);
        reader.Read();
        return Schemas.CollectionSchema.ReadObject(ref reader, new ReadContext()) ?? new CollectionInfo();
    }

    private static PartInfo? ReadPart(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Dictionary<string, JsonElement> members = Members(part);
        string? name = StringOf(members, "name");
        if (name == null)
        {
            return null;
        }

        var tables = new OrderedMap<int>();
        if (members.TryGetValue("tables", out JsonElement tablesElement) && tablesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty table in tablesElement.EnumerateObject())
            {
                if (table.Value.ValueKind == JsonValueKind.Number && table.Value.TryGetInt32(out int rows) && rows >= 0)
                {
                    tables.Set(table.Name, rows);
                }
            }
        }

        var extensions = new OrderedMap<JsonElement>();
        foreach (KeyValuePair<string, JsonElement> member in members)
        {
            if (member.Key != "name" && member.Key != "tables")
            {
                extensions.Set(member.Key, member.Value.Clone());
            }
        }

        return new PartInfo(name, tables, extensions);
    }

    private static string? StringOf(Dictionary<string, JsonElement> members, string name)
        => members.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Contains(IReadOnlyList<int> list, int value)
    {
        foreach (int item in list)
        {
            if (item == value)
            {
                return true;
            }
        }

        return false;
    }
}
