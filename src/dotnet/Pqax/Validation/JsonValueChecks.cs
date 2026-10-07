using System.Collections.Generic;
using System.Text.Json;

namespace Pqax;

/// <summary>
/// Checks the values of one field against its type, and counts what breaks the rules <c>json.type</c>,
/// <c>json.integer</c>, <c>json.integerRange</c>, and <c>json.timestamp</c>. A null value is always allowed; a null
/// element of a list is allowed only where the format says so, which is <c>partitions.values</c>.
/// </summary>
internal sealed class JsonValueChecks
{
    private static readonly char[] FractionOrExponent = { '.', 'e', 'E' };

    /// <summary>Values whose JSON type is not the type of their field.</summary>
    public int WrongType { get; private set; }

    /// <summary>Numbers of an integer of the format with a fraction or an exponent.</summary>
    public int NotInteger { get; private set; }

    /// <summary>Integers beyond 2^53 - 1 in absolute value.</summary>
    public int OutOfRange { get; private set; }

    /// <summary>Timestamps that are not strings in the form of the format.</summary>
    public int BadTimestamps { get; private set; }

    /// <summary>
    /// Checks one value of a field. Returns the integer of an id, an integer, or a reference, and null otherwise.
    /// The integers of a list of references go to <paramref name="references"/> when it is given.
    /// </summary>
    public long? Value(FieldInfo field, JsonElement value, bool nullElements = false, List<long>? references = null)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        switch (field.Kind)
        {
            case FieldKind.Id:
            case FieldKind.Integer:
            case FieldKind.Reference:
                return Integer(value);
            case FieldKind.String:
                Expect(value.ValueKind == JsonValueKind.String);
                return null;
            case FieldKind.Boolean:
                Expect(value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False);
                return null;
            case FieldKind.Timestamp:
                Timestamp(value);
                return null;
            case FieldKind.StringList:
                if (Expect(value.ValueKind == JsonValueKind.Array))
                {
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        Expect(item.ValueKind == JsonValueKind.String || (nullElements && item.ValueKind == JsonValueKind.Null));
                    }
                }

                return null;
            case FieldKind.ReferenceList:
                if (Expect(value.ValueKind == JsonValueKind.Array))
                {
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        long? number = Expect(item.ValueKind != JsonValueKind.Null) ? Integer(item) : null;
                        if (number.HasValue)
                        {
                            references?.Add(number.Value);
                        }
                    }
                }

                return null;
            case FieldKind.Map:
                if (Expect(value.ValueKind == JsonValueKind.Object))
                {
                    foreach (JsonProperty member in value.EnumerateObject())
                    {
                        Expect(member.Value.ValueKind == JsonValueKind.String);
                    }
                }

                return null;
            case FieldKind.Object:
                if (Expect(value.ValueKind == JsonValueKind.Object))
                {
                    foreach (KeyValuePair<string, JsonElement> member in ManifestReader.Members(value))
                    {
                        FieldInfo? memberField = field.FindMember(member.Key);
                        if (memberField != null)
                        {
                            Value(memberField, member.Value);
                        }
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>The integer of a value that must be an integer, or null when it is null or is not one, which is counted.</summary>
    public long? Integer(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out long number))
                {
                    if (number > JsonRead.MaxSafeInteger || number < -JsonRead.MaxSafeInteger)
                    {
                        OutOfRange++;
                    }

                    return number;
                }

                if (value.GetRawText().IndexOfAny(FractionOrExponent) >= 0)
                {
                    NotInteger++;
                }
                else
                {
                    OutOfRange++;
                }

                return null;
            default:
                WrongType++;
                return null;
        }
    }

    /// <summary>A timestamp: a string in the form of the format. A value that is not a string has the wrong type, which is <c>json.type</c> only.</summary>
    public void Timestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            WrongType++;
        }
        else if (!Timestamps.HasForm(value.GetString()!))
        {
            BadTimestamps++;
        }
    }

    /// <summary>Counts a value of the wrong type when the condition is false, and returns the condition.</summary>
    public bool Expect(bool condition)
    {
        if (!condition)
        {
            WrongType++;
        }

        return condition;
    }
}
