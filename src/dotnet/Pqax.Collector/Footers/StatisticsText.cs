using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Pqax.Collector;

/// <summary>
/// Turns the bounds of the statistics of a column chunk into text, from the physical type and the annotation of its
/// leaf. Used only when a collection includes data values.
/// </summary>
internal static class StatisticsText
{
    /// <summary>The longest unscaled value of a decimal bound that is written: 32 bytes, the largest decimal of Arrow.</summary>
    public const int MaxDecimalBytes = 32;

    /// <summary>The largest scale of a decimal bound that is written: 76, the most digits of a decimal of 32 bytes.</summary>
    public const int MaxDecimalScale = 76;

    private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static string? Format(byte[]? value, ParquetLeafColumn leaf)
    {
        if (value == null)
        {
            return null;
        }

        string? annotation = leaf.TypeAnnotation;
        try
        {
            switch (leaf.PhysicalType)
            {
                case "BOOLEAN":
                    return value.Length >= 1 ? (value[0] != 0 ? "true" : "false") : null;
                case "INT32":
                    if (value.Length < 4)
                    {
                        return null;
                    }

                    return FormatInteger(BinaryPrimitives.ReadInt32LittleEndian(value), annotation, leaf);
                case "INT64":
                    if (value.Length < 8)
                    {
                        return null;
                    }

                    return FormatInteger(BinaryPrimitives.ReadInt64LittleEndian(value), annotation, leaf);
                case "FLOAT":
                    return value.Length >= 4
                        ? BitConverter.ToSingle(value, 0).ToString("R", CultureInfo.InvariantCulture)
                        : null;
                case "DOUBLE":
                    return value.Length >= 8
                        ? BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(value)).ToString("R", CultureInfo.InvariantCulture)
                        : null;
                case "BYTE_ARRAY":
                case "FIXED_LEN_BYTE_ARRAY":
                    if (annotation != null && annotation.StartsWith("DECIMAL(", StringComparison.Ordinal))
                    {
                        // A longer value is no decimal that a writer bounds, and its text would take quadratic time.
                        return value.Length <= MaxDecimalBytes ? Decimal(new BigInteger(Reverse(value)), Scale(leaf)) : null;
                    }

                    if (IsText(annotation) || (annotation == null && leaf.PhysicalType == "BYTE_ARRAY" && IsValidUtf8(value)))
                    {
                        return Encoding.UTF8.GetString(value);
                    }

                    return "0x" + Hex(value);
                default:
                    return "0x" + Hex(value);
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? FormatInteger(long value, string? annotation, ParquetLeafColumn leaf)
    {
        if (annotation == null)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        if (annotation.StartsWith("DECIMAL(", StringComparison.Ordinal))
        {
            return Decimal(new BigInteger(value), Scale(leaf));
        }

        if (annotation == "DATE")
        {
            return Epoch.AddDays(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        long ticksPerUnit = 0;
        if (annotation.StartsWith("TIMESTAMP(MILLIS", StringComparison.Ordinal) || annotation == "TIMESTAMP_MILLIS")
        {
            ticksPerUnit = TimeSpan.TicksPerMillisecond;
        }
        else if (annotation.StartsWith("TIMESTAMP(MICROS", StringComparison.Ordinal) || annotation == "TIMESTAMP_MICROS")
        {
            ticksPerUnit = 10;
        }
        else if (annotation.StartsWith("TIMESTAMP(NANOS", StringComparison.Ordinal))
        {
            return Epoch.AddTicks(value / 100).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        }

        if (ticksPerUnit > 0)
        {
            return Epoch.AddTicks(value * ticksPerUnit).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        }

        if (annotation.StartsWith("INTEGER(", StringComparison.Ordinal) && annotation.IndexOf("UNSIGNED", StringComparison.Ordinal) >= 0)
        {
            return leaf.PhysicalType == "INT32"
                ? ((uint)value).ToString(CultureInfo.InvariantCulture)
                : ((ulong)value).ToString(CultureInfo.InvariantCulture);
        }

        if (annotation.StartsWith("UINT_", StringComparison.Ordinal))
        {
            return leaf.PhysicalType == "INT32"
                ? ((uint)value).ToString(CultureInfo.InvariantCulture)
                : ((ulong)value).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static int Scale(ParquetLeafColumn leaf)
    {
        string? annotation = leaf.TypeAnnotation;
        if (annotation != null)
        {
            int comma = annotation.IndexOf(',');
            int close = annotation.IndexOf(')');
            if (comma > 0 && close > comma
                && int.TryParse(annotation.Substring(comma + 1, close - comma - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int scale))
            {
                return scale;
            }
        }

        return leaf.Scale ?? 0;
    }

    private static string? Decimal(BigInteger unscaled, int scale)
    {
        if (scale <= 0)
        {
            return unscaled.ToString(CultureInfo.InvariantCulture);
        }

        if (scale > MaxDecimalScale)
        {
            // The text would hold as many zeros as the scale says, which a damaged footer sets to any integer.
            return null;
        }

        bool negative = unscaled.Sign < 0;
        string digits = BigInteger.Abs(unscaled).ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        string text = digits.Substring(0, digits.Length - scale) + "." + digits.Substring(digits.Length - scale);
        return negative ? "-" + text : text;
    }

    private static bool IsText(string? annotation) =>
        annotation == "STRING" || annotation == "UTF8" || annotation == "ENUM" || annotation == "JSON";

    private static bool IsValidUtf8(byte[] value)
    {
        try
        {
            new UTF8Encoding(false, true).GetString(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // The bytes of a decimal are big-endian two's complement; BigInteger reads little-endian.
    private static byte[] Reverse(byte[] value)
    {
        var copy = new byte[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            copy[i] = value[value.Length - 1 - i];
        }

        return copy;
    }

    private static string Hex(byte[] value)
    {
        var text = new StringBuilder(value.Length * 2);
        foreach (byte b in value)
        {
            text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
