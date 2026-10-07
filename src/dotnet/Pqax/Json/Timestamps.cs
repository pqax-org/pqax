using System;
using System.Globalization;

namespace Pqax;

/// <summary>The one form of a timestamp in the format: <c>2026-10-05T08:15:30.000Z</c>, UTC, with exactly three decimals.</summary>
internal static class Timestamps
{
    private const string Pattern = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'";

    /// <summary>The text of a timestamp. Ticks below the millisecond are dropped.</summary>
    public static string Format(DateTimeOffset value) => value.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);

    /// <summary>Reads a timestamp: the form of the format, or another ISO 8601 form, which a reader tolerates.</summary>
    public static DateTimeOffset? Parse(string text)
    {
        if (TryParseExact(text, out DateTimeOffset value))
        {
            return value;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
        {
            return value.ToUniversalTime();
        }

        return null;
    }

    /// <summary>
    /// Whether the text has the form of the format, <c>^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$</c>.
    /// This is the rule of the validator, which checks the form and not the calendar.
    /// </summary>
    public static bool HasForm(string text)
    {
        if (text.Length != 24)
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char expected = i switch
            {
                4 or 7 => '-',
                10 => 'T',
                13 or 16 => ':',
                19 => '.',
                23 => 'Z',
                _ => '0',
            };
            bool ok = expected == '0' ? c >= '0' && c <= '9' : c == expected;
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the text is a timestamp in the form of the format, with a date and a time that exist.</summary>
    public static bool TryParseExact(string text, out DateTimeOffset value)
    {
        value = default;
        if (text.Length != 24 || text[4] != '-' || text[7] != '-' || text[10] != 'T' || text[13] != ':' || text[16] != ':' || text[19] != '.' || text[23] != 'Z')
        {
            return false;
        }

        if (!Digits(text, 0, 4, out int year) || !Digits(text, 5, 2, out int month) || !Digits(text, 8, 2, out int day)
            || !Digits(text, 11, 2, out int hour) || !Digits(text, 14, 2, out int minute) || !Digits(text, 17, 2, out int second)
            || !Digits(text, 20, 3, out int millisecond))
        {
            return false;
        }

        if (year < 1 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        value = new DateTimeOffset(year, month, day, hour, minute, second, millisecond, TimeSpan.Zero);
        return true;
    }

    private static bool Digits(string text, int start, int length, out int value)
    {
        value = 0;
        for (int i = start; i < start + length; i++)
        {
            char c = text[i];
            if (c < '0' || c > '9')
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        return true;
    }
}
