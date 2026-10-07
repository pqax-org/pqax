using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

namespace Pqax.Collector;

/// <summary>
/// The names of the enumerations of <c>parquet.thrift</c>. A value that this version does not know is written as its
/// number, so that a footprint shows it as it is.
/// </summary>
internal static class ParquetNames
{
    private static readonly string[] Types = { "BOOLEAN", "INT32", "INT64", "INT96", "FLOAT", "DOUBLE", "BYTE_ARRAY", "FIXED_LEN_BYTE_ARRAY" };

    private static readonly string[] Repetitions = { "REQUIRED", "OPTIONAL", "REPEATED" };

    private static readonly string[] ConvertedTypes =
    {
        "UTF8", "MAP", "MAP_KEY_VALUE", "LIST", "ENUM", "DECIMAL", "DATE", "TIME_MILLIS", "TIME_MICROS", "TIMESTAMP_MILLIS",
        "TIMESTAMP_MICROS", "UINT_8", "UINT_16", "UINT_32", "UINT_64", "INT_8", "INT_16", "INT_32", "INT_64", "JSON", "BSON", "INTERVAL",
    };

    private static readonly string?[] EncodingNames =
    {
        "PLAIN", "GROUP_VAR_INT", "PLAIN_DICTIONARY", "RLE", "BIT_PACKED", "DELTA_BINARY_PACKED", "DELTA_LENGTH_BYTE_ARRAY",
        "DELTA_BYTE_ARRAY", "RLE_DICTIONARY", "BYTE_STREAM_SPLIT", "ALP",
    };

    private static readonly string[] Codecs = { "UNCOMPRESSED", "SNAPPY", "GZIP", "LZO", "BROTLI", "LZ4", "ZSTD", "LZ4_RAW" };

    private static readonly string[] PageTypes = { "DATA_PAGE", "INDEX_PAGE", "DICTIONARY_PAGE", "DATA_PAGE_V2" };

    private static readonly string[] TimeUnits = { string.Empty, "MILLIS", "MICROS", "NANOS" };

    private static readonly string[] EdgeAlgorithms = { "SPHERICAL", "VINCENTY", "THOMAS", "ANDOYER", "KARNEY" };

    // Equal lists of encodings share one array: a million column chunks use a handful of distinct lists.
    private static readonly ConcurrentDictionary<long, IReadOnlyList<string>> EncodingLists = new ConcurrentDictionary<long, IReadOnlyList<string>>();

    public static string Type(int value) => Name(Types, value);

    public static string Repetition(int value) => Name(Repetitions, value);

    public static string ConvertedType(int value) => Name(ConvertedTypes, value);

    public static string Encoding(int value) => value >= 0 && value < EncodingNames.Length ? EncodingNames[value]! : Number(value);

    public static string Codec(int value) => Name(Codecs, value);

    public static string PageType(int value) => Name(PageTypes, value);

    public static string TimeUnit(int memberId) => memberId > 0 && memberId < TimeUnits.Length ? TimeUnits[memberId] : Number(memberId);

    public static string EdgeAlgorithm(int value) => Name(EdgeAlgorithms, value);

    /// <summary>The names of a list of encodings, in order, shared with every equal list.</summary>
    public static IReadOnlyList<string> Encodings(int[] values, int count)
    {
        // Up to 15 encodings below 16 pack into one key: 4 bits each, and the count in the top bits.
        bool packable = count <= 15;
        long key = count;
        for (int i = 0; i < count && packable; i++)
        {
            if (values[i] < 0 || values[i] > 15)
            {
                packable = false;
            }
            else
            {
                key = (key << 4) | (long)values[i];
            }
        }

        if (packable && EncodingLists.TryGetValue(key, out IReadOnlyList<string>? shared))
        {
            return shared;
        }

        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = Encoding(values[i]);
        }

        if (packable)
        {
            return EncodingLists.GetOrAdd(key, names);
        }

        return names;
    }

    private static string Name(string[] names, int value) => value >= 0 && value < names.Length ? names[value] : Number(value);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
