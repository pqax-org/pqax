using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Pqax.Collector;

/// <summary>
/// Reads the statistics of a data file, which the log holds as JSON text. It keeps <c>numRecords</c> and the null count
/// of each top-level column, and skips everything else without materializing it: the bounds are data values.
/// </summary>
internal static class DeltaStatsParser
{
    private static readonly byte[] NumRecords = Encoding.UTF8.GetBytes("numRecords");
    private static readonly byte[] NullCount = Encoding.UTF8.GetBytes("nullCount");

    /// <summary>Parses the statistics.</summary>
    /// <returns>False when the text is not valid JSON or not an object.</returns>
    public static bool TryParse(string stats, out long? numRecords, out KeyValuePair<string, long>[]? nullCounts)
    {
        numRecords = null;
        nullCounts = null;
        byte[] utf8 = Encoding.UTF8.GetBytes(stats);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals(NumRecords))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long rows))
                    {
                        numRecords = rows;
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                else if (reader.ValueTextEquals(NullCount))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        nullCounts = ReadNullCounts(ref reader);
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            return reader.TokenType == JsonTokenType.EndObject;
        }
        catch (JsonException)
        {
            numRecords = null;
            nullCounts = null;
            return false;
        }
        catch (InvalidOperationException)
        {
            numRecords = null;
            nullCounts = null;
            return false;
        }
    }

    private static KeyValuePair<string, long>[] ReadNullCounts(ref Utf8JsonReader reader)
    {
        var counts = new List<KeyValuePair<string, long>>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string name = reader.GetString() ?? string.Empty;
            reader.Read();
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long count))
            {
                counts.Add(new KeyValuePair<string, long>(name, count));
            }
            else
            {
                // A nested column has an object of counts; the model keeps top-level columns only.
                reader.Skip();
            }
        }

        return counts.ToArray();
    }
}
