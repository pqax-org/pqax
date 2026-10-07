using System;
using System.Text.Json;

namespace Pqax;

/// <summary>Walks the members of the top-level object of a part with a <see cref="Utf8JsonReader"/>, without building a document.</summary>
internal static class PartScanner
{
    /// <summary>
    /// Calls <paramref name="handler"/> for each member of the top-level object. A top-level value that is not an object
    /// has no tables. The whole part is parsed, so a part that is not valid JSON throws <see cref="PqaxFormatException"/>
    /// with the code <c>json.invalid</c>.
    /// </summary>
    public static void Scan(byte[] bytes, string partName, MemberHandler handler)
    {
        try
        {
            var reader = new Utf8JsonReader(JsonRead.WithoutBom(bytes).Span, new JsonReaderOptions { MaxDepth = 256 });
            if (!reader.Read())
            {
                throw new PqaxFormatException("json.invalid", $"The part {partName} is empty.");
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string name = reader.GetString()!;
                    reader.Read();
                    handler(name, ref reader);
                }
            }
            else
            {
                JsonRead.SkipValue(ref reader);
            }

            // Reading past the value finds any text after it, which is not valid JSON.
            while (reader.Read())
            {
            }
        }
        catch (JsonException e)
        {
            throw new PqaxFormatException("json.invalid", $"The part {partName} is not valid JSON: {e.Message}", e);
        }
        catch (InvalidOperationException e)
        {
            throw new PqaxFormatException("json.invalid", $"The part {partName} has a value that cannot be read: {e.Message}", e);
        }
    }
}
