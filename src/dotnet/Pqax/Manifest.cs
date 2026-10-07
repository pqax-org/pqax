using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>The content of <c>manifest.json</c>: what the file is, how it was collected, and which parts it has.</summary>
public sealed class Manifest
{
    internal Manifest(
        string format,
        string formatVersion,
        CollectionInfo collection,
        string? obfuscatorDictionaryId,
        string? obfuscatorLib,
        string? obfuscatorLibVersion,
        IReadOnlyList<PartInfo> parts,
        IReadOnlyDictionary<string, JsonElement> extensions)
    {
        Format = format;
        FormatVersion = formatVersion;
        Collection = collection;
        ObfuscatorDictionaryId = obfuscatorDictionaryId;
        ObfuscatorLib = obfuscatorLib;
        ObfuscatorLibVersion = obfuscatorLibVersion;
        Parts = parts;
        Extensions = extensions;
    }

    /// <summary>Always <c>pqax</c>.</summary>
    public string Format { get; }

    /// <summary>The version of the format, such as <c>0.1</c>.</summary>
    public string FormatVersion { get; }

    /// <summary>How and when the footprint was collected. Empty when the manifest has no collection.</summary>
    public CollectionInfo Collection { get; }

    /// <summary>Set when names were obfuscated, as in VPAX.</summary>
    public string? ObfuscatorDictionaryId { get; }

    /// <summary>The library that obfuscated the names.</summary>
    public string? ObfuscatorLib { get; }

    /// <summary>The version of the library that obfuscated the names.</summary>
    public string? ObfuscatorLibVersion { get; }

    /// <summary>The parts of the file, in the order of the manifest. The first one is <c>summary.json</c>.</summary>
    public IReadOnlyList<PartInfo> Parts { get; }

    /// <summary>The members of the manifest that this version of the format does not know, in the order of the file.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; }
}
