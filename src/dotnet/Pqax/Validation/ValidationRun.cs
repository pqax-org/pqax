using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Pqax;

/// <summary>
/// One validation of a PQAX file: the container, the JSON of the manifest and of each part, the manifest, the parts,
/// and the values of each table. The rules that join tables run afterwards in <see cref="CrossTableRules"/>. Every
/// issue is reported, and a rule that cannot be evaluated because an earlier rule failed is skipped.
/// </summary>
internal sealed class ValidationRun
{
    private const string ManifestName = "manifest.json";
    private const string SummaryName = "summary.json";

    private static readonly string[] RequiredMembers = { "format", "formatVersion", "collection", "parts" };
    private static readonly string[] ObfuscationMembers = { "obfuscatorDictionaryId", "obfuscatorLib", "obfuscatorLibVersion" };
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly ByteRange range;
    private readonly IssueLog log = new IssueLog();
    private readonly Dictionary<string, TableStore> stores = new Dictionary<string, TableStore>(StringComparer.Ordinal);
    private ZipDirectory? directory;

    public ValidationRun(ByteRange range)
    {
        this.range = range;
    }

    public ValidationResult Run()
    {
        if (Container() && Manifest(out List<ManifestPart>? parts) && parts != null)
        {
            Parts(parts);
            new CrossTableRules(log, stores).Run();
        }

        return log.Result();
    }

    /// <summary>The members of an object. When a name repeats, the last value wins, as in most JSON parsers.</summary>
    private static Dictionary<string, JsonElement> Members(JsonElement element) => ManifestReader.Members(element);

    /// <summary>Whether a name is one that a part may have: not empty, and only ASCII letters, digits, '.', '-', and '_'.</summary>
    internal static bool IsPartName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        foreach (char c in name)
        {
            bool allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the text matches <c>^[0-9]+\.[0-9]+$</c>.</summary>
    private static bool IsTwoNumbers(string text)
    {
        int dot = text.IndexOf('.');
        if (dot <= 0 || dot == text.Length - 1)
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (i != dot && (text[i] < '0' || text[i] > '9'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The name with ASCII letters in lower case: two entries collide when these are equal.</summary>
    private static string AsciiLower(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] >= 'A' && chars[i] <= 'Z')
            {
                chars[i] = (char)(chars[i] + 32);
            }
        }

        return new string(chars);
    }

    private static int CountDuplicateMembers(JsonElement element)
    {
        int count = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string>? names = null;
            bool repeats = false;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                names ??= new HashSet<string>(StringComparer.Ordinal);
                if (!names.Add(property.Name))
                {
                    repeats = true;
                }

                JsonValueKind kind = property.Value.ValueKind;
                if (kind == JsonValueKind.Object || kind == JsonValueKind.Array)
                {
                    count += CountDuplicateMembers(property.Value);
                }
            }

            if (repeats)
            {
                count++;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object || item.ValueKind == JsonValueKind.Array)
                {
                    count += CountDuplicateMembers(item);
                }
            }
        }

        return count;
    }

    private static string Plural(long count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    /// <summary>The rules of the container: the zip, its entries, and the manifest entry.</summary>
    private bool Container()
    {
        try
        {
            directory = ZipDirectory.Read(range);
        }
        catch (InvalidDataException e)
        {
            log.Fail("container.manifestMissing", $"The file is not a zip archive that can be read, so it has no manifest.json: {e.Message}");
            return false;
        }

        if (directory.NeedsZip64)
        {
            log.Fail("container.zip64", "The archive ends with ZIP64 records. A PQAX file does not use ZIP64.");
            return false;
        }

        log.Check(!directory.HasZip64Locator, "container.zip64", "The archive has a ZIP64 end of central directory locator. A PQAX file does not use ZIP64.");

        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (ZipEntry entry in directory.Entries)
        {
            log.Check(!entry.NeedsZip64 && !entry.HasZip64Extra, "container.zip64", $"The entry {entry.Name} has a ZIP64 extra field. A PQAX file does not use ZIP64.", entry.Name);
            log.Check(entry.IsSupportedMethod, "container.method", $"The entry {entry.Name} uses compression method {entry.Method}, which is neither stored (0) nor deflate (8).", entry.Name);
            log.Check(!entry.IsEncrypted, "container.encrypted", $"The entry {entry.Name} is encrypted.", entry.Name);
            string key = AsciiLower(entry.Name);
            if (!byName.TryGetValue(key, out List<string>? same))
            {
                same = new List<string>();
                byName.Add(key, same);
            }

            same.Add(entry.Name);
        }

        bool collision = false;
        foreach (List<string> same in byName.Values)
        {
            if (same.Count > 1)
            {
                collision = true;
                log.Fail("container.caseCollision", $"The archive has {same.Count} entries named {string.Join(", ", same)}, which differ only in case or not at all.", same[0]);
            }
        }

        if (!collision)
        {
            log.Pass();
        }

        return log.Check(directory.Find(ManifestName) != null, "container.manifestMissing", "The archive has no manifest.json entry.");
    }

    /// <summary>
    /// Reads a document and checks its JSON: the byte order mark, the encoding and the syntax, the top level, and
    /// repeated member names. Returns null when the rest of the document cannot be evaluated.
    /// </summary>
    private JsonDocument? LoadJson(string name)
    {
        ZipEntry? entry = directory!.Find(name);
        if (entry == null || entry.NeedsZip64 || entry.IsEncrypted || !entry.IsSupportedMethod)
        {
            // The container rules reported it.
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = directory.ReadBytes(entry, Archive.MaxPartSize);
        }
        catch (Exception e) when (e is InvalidDataException || e is NotSupportedException)
        {
            log.Fail("json.invalid", $"The document {name} cannot be read: {e.Message}", name);
            return null;
        }

        bool bom = JsonRead.HasBom(bytes);
        log.Check(!bom, "json.bom", $"The document {name} starts with a byte order mark.", name);
        ReadOnlyMemory<byte> json = JsonRead.WithoutBom(bytes);
        try
        {
            StrictUtf8.GetCharCount(bytes, bom ? 3 : 0, json.Length);
        }
        catch (DecoderFallbackException)
        {
            log.Fail("json.invalid", $"The document {name} is not valid UTF-8.", name);
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException e)
        {
            log.Fail("json.invalid", $"The document {name} is not valid JSON: {e.Message}", name);
            return null;
        }

        log.Pass();
        if (!log.Check(document.RootElement.ValueKind == JsonValueKind.Object, "json.topLevel", $"The top level of the document {name} is not an object.", name))
        {
            document.Dispose();
            return null;
        }

        int repeated = CountDuplicateMembers(document.RootElement);
        log.Check(repeated == 0, "json.duplicateMember", $"The document {name} has {Plural(repeated, "object that repeats", "objects that repeat")} a member name.", name);
        return document;
    }

    /// <summary>
    /// The rules of the manifest. Returns false when no part can be evaluated: the manifest cannot be read, the format
    /// is not pqax, or the version is one that this validator does not know. The parts are null when the manifest has none.
    /// </summary>
    private bool Manifest(out List<ManifestPart>? parts)
    {
        parts = null;
        using JsonDocument? document = LoadJson(ManifestName);
        if (document == null)
        {
            return false;
        }

        Dictionary<string, JsonElement> members = Members(document.RootElement);
        bool proceed = true;
        foreach (string name in RequiredMembers)
        {
            log.Check(members.TryGetValue(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null, "manifest.required", $"The manifest has no {name}.", ManifestName, null, name);
        }

        if (members.TryGetValue("format", out JsonElement format) && format.ValueKind != JsonValueKind.Null)
        {
            log.Check(format.ValueKind == JsonValueKind.String, "json.type", "The format of the manifest is not a string.", ManifestName, null, "format");
            proceed &= log.Check(format.ValueKind == JsonValueKind.String && format.GetString() == "pqax", "manifest.format", "The format that the manifest states is not pqax.", ManifestName, null, "format");
        }

        if (members.TryGetValue("formatVersion", out JsonElement version) && version.ValueKind != JsonValueKind.Null)
        {
            string? text = version.ValueKind == JsonValueKind.String ? version.GetString() : null;
            log.Check(text != null, "json.type", "The formatVersion of the manifest is not a string.", ManifestName, null, "formatVersion");
            if (log.Check(text != null && IsTwoNumbers(text), "manifest.formatVersion", "The formatVersion of the manifest is not two numbers separated by a dot, as in 0.1.", ManifestName, null, "formatVersion"))
            {
                int major = ManifestReader.MajorVersion(text) ?? -1;
                bool known = false;
                foreach (int item in ManifestReader.KnownMajorVersions)
                {
                    known |= item == major;
                }

                proceed &= log.Check(known, "manifest.versionUnknown", $"The format version {text} starts with a number that this validator does not know. It knows {string.Join(", ", ManifestReader.KnownMajorVersions)}.", ManifestName, null, "formatVersion");
            }
        }

        foreach (string name in ObfuscationMembers)
        {
            if (members.TryGetValue(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null)
            {
                log.Check(value.ValueKind == JsonValueKind.String, "json.type", $"The {name} of the manifest is not a string.", ManifestName, null, name);
            }
        }

        if (members.TryGetValue("collection", out JsonElement collection) && collection.ValueKind != JsonValueKind.Null)
        {
            if (log.Check(collection.ValueKind == JsonValueKind.Object, "json.type", "The collection of the manifest is not an object.", ManifestName, null, "collection"))
            {
                CheckCollection(collection);
            }
            else
            {
                log.Fail("manifest.required", "The collection of the manifest is not an object, so it has no level.", ManifestName, null, "collection.level");
            }
        }

        if (members.TryGetValue("parts", out JsonElement partsElement) && partsElement.ValueKind != JsonValueKind.Null)
        {
            if (log.Check(partsElement.ValueKind == JsonValueKind.Array, "json.type", "The parts of the manifest are not an array.", ManifestName, null, "parts"))
            {
                parts = new List<ManifestPart>();
                CheckParts(partsElement, parts);
            }
        }

        return proceed;
    }

    private void CheckCollection(JsonElement collection)
    {
        Dictionary<string, JsonElement> members = Members(collection);
        var values = new JsonValueChecks();
        foreach (FieldInfo field in Schemas.CollectionSchema.Info.Fields)
        {
            bool present = members.TryGetValue(field.Name, out JsonElement value) && value.ValueKind != JsonValueKind.Null;
            if (field.Required)
            {
                log.Check(present, "manifest.required", $"The collection of the manifest has no {field.Name}, which is required.", ManifestName, null, "collection." + field.Name);
            }

            if (present)
            {
                values.Value(field, value);
            }
        }

        log.Check(values.WrongType == 0, "json.type", $"The collection of the manifest has {Plural(values.WrongType, "value", "values")} of the wrong JSON type.", ManifestName, null, "collection");
        log.Check(values.NotInteger == 0, "json.integer", $"The collection of the manifest has {Plural(values.NotInteger, "number that is not an integer", "numbers that are not integers")}.", ManifestName, null, "collection");
        log.Check(values.OutOfRange == 0, "json.integerRange", $"The collection of the manifest has {Plural(values.OutOfRange, "integer", "integers")} beyond 2^53 - 1.", ManifestName, null, "collection");
        log.Check(values.BadTimestamps == 0, "json.timestamp", $"The collection of the manifest has {Plural(values.BadTimestamps, "timestamp", "timestamps")} not in the form 2026-10-05T08:15:30.000Z.", ManifestName, null, "collection");
    }

    private void CheckParts(JsonElement partsElement, List<ManifestPart> parts)
    {
        int index = 0;
        string? firstName = null;
        foreach (JsonElement element in partsElement.EnumerateArray())
        {
            ManifestPart? part = ParsePart(element, index);
            if (index == 0)
            {
                firstName = part?.Name;
            }

            if (part != null)
            {
                parts.Add(part);
            }

            index++;
        }

        log.Check(
            index > 0 && firstName == SummaryName,
            "manifest.firstPart",
            index == 0 ? "The manifest lists no parts, so summary.json is not the first." : "The first part of the manifest is not summary.json.",
            ManifestName,
            null,
            "parts");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var repeated = new HashSet<string>(StringComparer.Ordinal);
        foreach (ManifestPart part in parts)
        {
            if (!seen.Add(part.Name) && repeated.Add(part.Name))
            {
                log.Fail("manifest.duplicatePart", $"The manifest lists the part {part.Name} more than once.", part.Name);
            }
        }

        if (repeated.Count == 0)
        {
            log.Pass();
        }

        foreach (string name in seen)
        {
            log.Check(IsPartName(name), "container.partName", "The name of a part is empty or has a character other than ASCII letters, digits, '.', '-', and '_'.", name);
            log.Check(directory!.Find(name) != null, "manifest.partMissing", $"The manifest lists the part {name}, and the archive has no entry with that name.", name);
        }
    }

    private ManifestPart? ParsePart(JsonElement element, int index)
    {
        string where = $"parts[{index}]";
        if (!log.Check(element.ValueKind == JsonValueKind.Object, "json.type", $"Part {index} of the manifest is not an object.", ManifestName, null, where))
        {
            log.Fail("manifest.required", $"Part {index} of the manifest is not an object, so it has no name and no tables.", ManifestName, null, where);
            return null;
        }

        Dictionary<string, JsonElement> members = Members(element);
        bool hasName = members.TryGetValue("name", out JsonElement nameElement) && nameElement.ValueKind != JsonValueKind.Null;
        string? name = hasName && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
        if (hasName)
        {
            log.Check(name != null, "json.type", $"The name of part {index} of the manifest is not a string.", ManifestName, null, where + ".name");
        }

        log.Check(name != null, "manifest.required", $"Part {index} of the manifest has no name.", ManifestName, null, where + ".name");
        bool hasTablesMember = members.TryGetValue("tables", out JsonElement tablesElement) && tablesElement.ValueKind != JsonValueKind.Null;
        bool hasTables = hasTablesMember && tablesElement.ValueKind == JsonValueKind.Object;
        if (hasTablesMember)
        {
            log.Check(hasTables, "json.type", $"The tables of part {index} of the manifest are not an object.", name ?? ManifestName, null, where + ".tables");
        }

        log.Check(hasTables, "manifest.required", $"Part {index} of the manifest has no tables.", name ?? ManifestName, null, where + ".tables");
        if (name == null)
        {
            return null;
        }

        var tables = new OrderedMap<long?>();
        if (hasTables)
        {
            var values = new JsonValueChecks();
            foreach (JsonProperty table in tablesElement.EnumerateObject())
            {
                long? count = values.Integer(table.Value);
                if (table.Value.ValueKind == JsonValueKind.Null)
                {
                    values.Expect(false);
                }

                tables.Set(table.Name, count);
            }

            log.Check(values.WrongType == 0, "json.type", $"The manifest gives {Plural(values.WrongType, "row count", "row counts")} of the part {name} that are not numbers.", name, null, where + ".tables");
            log.Check(values.NotInteger == 0, "json.integer", $"The manifest gives {Plural(values.NotInteger, "row count", "row counts")} of the part {name} that are not integers.", name, null, where + ".tables");
            log.Check(values.OutOfRange == 0, "json.integerRange", $"The manifest gives {Plural(values.OutOfRange, "row count", "row counts")} of the part {name} beyond 2^53 - 1.", name, null, where + ".tables");
        }

        return new ManifestPart(name, tables, hasTables);
    }

    /// <summary>The rules of each part listed in the manifest, once for each name, in the order of the manifest.</summary>
    private void Parts(List<ManifestPart> parts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ManifestPart part in parts)
        {
            if (!seen.Add(part.Name))
            {
                continue;
            }

            using JsonDocument? document = directory!.Find(part.Name) == null ? null : LoadJson(part.Name);
            if (document == null)
            {
                // The part cannot be read, so the rules that need its tables are skipped.
                foreach (string table in part.Tables.Keys)
                {
                    Store(table).Incomplete = true;
                }

                continue;
            }

            CheckPart(part, document.RootElement);
        }
    }

    private TableStore Store(string tableName)
    {
        if (!stores.TryGetValue(tableName, out TableStore? store))
        {
            store = new TableStore(tableName, Schemas.FindTable(tableName));
            stores.Add(tableName, store);
        }

        return store;
    }

    private void CheckPart(ManifestPart part, JsonElement root)
    {
        string partName = part.Name;
        bool isSummary = partName == SummaryName;
        Dictionary<string, JsonElement> members = Members(root);
        if (part.HasTables)
        {
            var missing = new List<string>();
            var unlisted = new List<string>();
            foreach (string name in part.Tables.Keys)
            {
                if (!members.ContainsKey(name))
                {
                    missing.Add(name);
                }
            }

            foreach (string name in members.Keys)
            {
                if (!part.Tables.ContainsKey(name))
                {
                    unlisted.Add(name);
                }
            }

            string message = "The tables of the part differ from the tables that the manifest states for it."
                + (missing.Count > 0 ? $" The part does not have {string.Join(", ", missing)}." : string.Empty)
                + (unlisted.Count > 0 ? $" The manifest does not state {string.Join(", ", unlisted)}." : string.Empty);
            log.Check(missing.Count == 0 && unlisted.Count == 0, "part.tables", message, partName);
        }

        foreach (string name in members.Keys)
        {
            TableInfo? info = Schemas.FindTable(name);
            if (info != null)
            {
                log.Check(
                    info.IsSummary == isSummary,
                    "part.kind",
                    info.IsSummary ? $"The summary table {name} is in the part {partName}, and only summary.json holds summary tables." : $"The detail table {name} is in summary.json.",
                    partName,
                    name);
            }
        }

        if (!isSummary)
        {
            log.Check(members.Count <= 1, "part.singleTable", $"The detail part {partName} holds {members.Count} tables, and a detail part holds one.", partName);
        }

        long actualRows = 0;
        foreach (KeyValuePair<string, JsonElement> member in members)
        {
            if (!log.Check(member.Value.ValueKind == JsonValueKind.Object, "json.type", $"The member {member.Key} of the part {partName} is not a table: its value is not an object.", partName, member.Key))
            {
                continue;
            }

            long? stated = part.Tables.TryGetValue(member.Key, out long? count) ? count : null;
            actualRows += CheckTable(partName, member.Key, member.Value, stated);
        }

        if (!isSummary)
        {
            long statedRows = 0;
            foreach (long? count in part.Tables.Values)
            {
                statedRows += count ?? 0;
            }

            long rows = Math.Max(statedRows, actualRows);
            log.Check(rows <= FootprintBuilderOptions.MaxRowsPerPart, "part.tooLarge", $"The detail part {partName} holds {rows} rows, and a detail part holds at most {FootprintBuilderOptions.MaxRowsPerPart}.", partName);
        }
    }

    /// <summary>The rules of one table in one part. Returns the rows of the table in the part.</summary>
    private int CheckTable(string partName, string tableName, JsonElement table, long? stated)
    {
        TableInfo? info = Schemas.FindTable(tableName);
        var arrays = new List<KeyValuePair<string, JsonElement>>();
        foreach (KeyValuePair<string, JsonElement> member in Members(table))
        {
            if (member.Value.ValueKind == JsonValueKind.Array)
            {
                arrays.Add(member);
            }
        }

        int maxLength = 0;
        int firstLength = -1;
        bool aligned = true;
        foreach (KeyValuePair<string, JsonElement> array in arrays)
        {
            int length = array.Value.GetArrayLength();
            maxLength = Math.Max(maxLength, length);
            if (stated.HasValue)
            {
                aligned &= log.Check(length == stated.Value, "part.rowCount", $"{tableName}.{array.Key} has {length} values, and the manifest states {stated.Value} rows of {tableName} in this part.", partName, tableName, array.Key);
            }
            else if (firstLength >= 0 && length != firstLength)
            {
                aligned = false;
            }

            firstLength = firstLength < 0 ? length : firstLength;
        }

        // The rows of the table in this part are those of its longest array, so that a manifest cannot make the
        // validator allocate rows. The arrays are compared with the manifest above.
        int partRows = maxLength;
        TableStore store = Store(tableName);
        int start = store.Rows;
        store.Rows += partRows;
        if (!store.Parts.Contains(partName))
        {
            store.Parts.Add(partName);
        }

        store.Broken |= !aligned;
        if (info == null)
        {
            return partRows;
        }

        foreach (FieldInfo field in info.Fields)
        {
            if (!field.Required)
            {
                continue;
            }

            JsonElement? values = null;
            foreach (KeyValuePair<string, JsonElement> array in arrays)
            {
                if (array.Key == field.Name)
                {
                    values = array.Value;
                }
            }

            if (values == null)
            {
                log.Fail("table.required", $"{tableName}.{field.Name} is required, and the part does not have it.", partName, tableName, field.Name);
                continue;
            }

            int nulls = 0;
            foreach (JsonElement value in values.Value.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.Null)
                {
                    nulls++;
                }
            }

            log.Check(nulls == 0, "table.required", $"{tableName}.{field.Name} is required, and {Plural(nulls, "row has", "rows have")} no value.", partName, tableName, field.Name);
        }

        foreach (KeyValuePair<string, JsonElement> array in arrays)
        {
            FieldInfo? field = info.Find(array.Key);
            if (field != null)
            {
                CheckValues(partName, tableName, field, array.Value, store, start);
            }
        }

        store.PadTo(start + partRows);
        return partRows;
    }

    /// <summary>The rules on the values of one field: their JSON type, integers and their range, and timestamps. Keeps what the rules across tables need.</summary>
    private void CheckValues(string partName, string tableName, FieldInfo field, JsonElement array, TableStore store, int start)
    {
        var values = new JsonValueChecks();
        bool nullElements = tableName == "partitions" && field.Name == "values";
        List<long>? references = store.Lists.TryGetValue(field.Name, out List<long>? list) ? list : null;
        bool keepInteger = field.IsInteger && store.Keeps(field.Name);
        bool keepString = field.Kind == FieldKind.String && store.Keeps(field.Name);
        int row = start;
        foreach (JsonElement value in array.EnumerateArray())
        {
            long? number = values.Value(field, value, nullElements, references);
            if (keepInteger)
            {
                store.SetInteger(field.Name, row, number);
            }
            else if (keepString)
            {
                store.SetString(field.Name, row, value.ValueKind == JsonValueKind.String ? value.GetString() : null);
            }

            row++;
        }

        log.Check(values.WrongType == 0, "json.type", $"{tableName}.{field.Name} has {Plural(values.WrongType, "value", "values")} of the wrong JSON type.", partName, tableName, field.Name);
        if (field.IsInteger || field.Kind == FieldKind.ReferenceList || field.Kind == FieldKind.Object)
        {
            log.Check(values.NotInteger == 0, "json.integer", $"{tableName}.{field.Name} has {Plural(values.NotInteger, "number that is not an integer", "numbers that are not integers")}.", partName, tableName, field.Name);
            log.Check(values.OutOfRange == 0, "json.integerRange", $"{tableName}.{field.Name} has {Plural(values.OutOfRange, "integer", "integers")} beyond 2^53 - 1.", partName, tableName, field.Name);
        }

        if (field.Kind == FieldKind.Timestamp)
        {
            log.Check(values.BadTimestamps == 0, "json.timestamp", $"{tableName}.{field.Name} has {Plural(values.BadTimestamps, "timestamp", "timestamps")} not in the form 2026-10-05T08:15:30.000Z.", partName, tableName, field.Name);
        }
    }
}
