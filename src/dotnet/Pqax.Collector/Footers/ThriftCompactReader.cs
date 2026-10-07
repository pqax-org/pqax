using System;
using System.Buffers.Binary;
using System.Text;

namespace Pqax.Collector;

/// <summary>
/// Reads the Thrift compact protocol from a buffer, the encoding of a Parquet footer. It reads field headers with short
/// and long deltas, booleans in the header, zigzag varints, doubles, binaries, lists, sets, maps, and structs, and it
/// skips a value of any type without allocating. Every length is checked against the bytes left, so a damaged footer
/// fails with an exception instead of a large allocation.
/// </summary>
internal sealed class ThriftCompactReader
{
    /// <summary>The wire types of the compact protocol.</summary>
    public const byte Stop = 0;
    public const byte BooleanTrue = 1;
    public const byte BooleanFalse = 2;
    public const byte Byte = 3;
    public const byte I16 = 4;
    public const byte I32 = 5;
    public const byte I64 = 6;
    public const byte Double = 7;
    public const byte Binary = 8;
    public const byte List = 9;
    public const byte Set = 10;
    public const byte Map = 11;
    public const byte Struct = 12;
    public const byte Uuid = 13;

    private const int MaxDepth = 64;

    private readonly byte[] buffer;
    private readonly int end;
    private int position;
    private int depth;

    public ThriftCompactReader(byte[] buffer, int offset, int count)
    {
        this.buffer = buffer;
        position = offset;
        end = offset + count;
    }

    /// <summary>The bytes left.</summary>
    public int Remaining => end - position;

    /// <summary>Whether a wire type is a boolean, which a field header carries with no payload.</summary>
    public static bool IsBoolean(byte type) => type == BooleanTrue || type == BooleanFalse;

    /// <summary>Starts a struct: checks the nesting depth.</summary>
    public void EnterStruct()
    {
        if (++depth > MaxDepth)
        {
            throw Invalid("The footer nests structures more than " + MaxDepth + " levels deep.");
        }
    }

    /// <summary>Ends a struct.</summary>
    public void ExitStruct() => depth--;

    /// <summary>Reads the header of the next field of a struct.</summary>
    /// <param name="lastId">The id of the previous field of the struct, updated.</param>
    /// <param name="id">The id of the field.</param>
    /// <param name="type">The wire type of the field.</param>
    /// <returns>False at the end of the struct.</returns>
    public bool ReadFieldHeader(ref short lastId, out short id, out byte type)
    {
        byte header = ReadRawByte();
        if (header == Stop)
        {
            id = 0;
            type = Stop;
            return false;
        }

        type = (byte)(header & 0x0F);
        int delta = header >> 4;
        id = delta == 0 ? ReadI16() : (short)(lastId + delta);
        lastId = id;
        return true;
    }

    /// <summary>Reads one byte.</summary>
    public byte ReadRawByte()
    {
        if (position >= end)
        {
            throw Truncated();
        }

        return buffer[position++];
    }

    /// <summary>Reads an unsigned varint of up to 32 bits.</summary>
    public uint ReadVarint32()
    {
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = ReadRawByte();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw Invalid("A varint of the footer is longer than 32 bits.");
    }

    /// <summary>Reads an unsigned varint of up to 64 bits.</summary>
    public ulong ReadVarint64()
    {
        ulong result = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            byte b = ReadRawByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw Invalid("A varint of the footer is longer than 64 bits.");
    }

    /// <summary>Reads an i16 as a zigzag varint.</summary>
    public short ReadI16()
    {
        int value = ReadI32();
        if (value < short.MinValue || value > short.MaxValue)
        {
            throw Invalid("An i16 of the footer is out of range.");
        }

        return (short)value;
    }

    /// <summary>Reads an i32 as a zigzag varint.</summary>
    public int ReadI32()
    {
        uint n = ReadVarint32();
        return (int)(n >> 1) ^ -(int)(n & 1);
    }

    /// <summary>Reads an i64 as a zigzag varint.</summary>
    public long ReadI64()
    {
        ulong n = ReadVarint64();
        return (long)(n >> 1) ^ -(long)(n & 1);
    }

    /// <summary>Reads an integer field of any integer wire type: byte, i16, i32, or i64.</summary>
    public long ReadInteger(byte type)
    {
        switch (type)
        {
            case Byte:
                return (sbyte)ReadRawByte();
            case I16:
            case I32:
                return ReadI32();
            case I64:
                return ReadI64();
            default:
                throw Invalid("A field of the footer has wire type " + type + " where an integer is expected.");
        }
    }

    /// <summary>Reads a double: 8 bytes, little endian.</summary>
    public double ReadDouble()
    {
        Need(8);
        long bits = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(buffer, position, 8));
        position += 8;
        return BitConverter.Int64BitsToDouble(bits);
    }

    /// <summary>Reads the length of a binary and checks it against the bytes left.</summary>
    public int ReadBinaryLength()
    {
        uint length = ReadVarint32();
        if (length > (uint)Remaining)
        {
            throw Truncated();
        }

        return (int)length;
    }

    /// <summary>Reads a binary as UTF-8 text.</summary>
    public string ReadString()
    {
        int length = ReadBinaryLength();
        string value = Encoding.UTF8.GetString(buffer, position, length);
        position += length;
        return value;
    }

    /// <summary>Reads a binary as a copy of its bytes.</summary>
    public byte[] ReadBinary()
    {
        int length = ReadBinaryLength();
        var value = new byte[length];
        Buffer.BlockCopy(buffer, position, value, 0, length);
        position += length;
        return value;
    }

    /// <summary>Skips a binary and returns its length, without allocating.</summary>
    public int SkipBinary()
    {
        int length = ReadBinaryLength();
        position += length;
        return length;
    }

    /// <summary>Reads the bytes of a binary whose length was read with <see cref="ReadBinaryLength"/>, as UTF-8 text.</summary>
    public string ReadUtf8(int length)
    {
        Need(length);
        string value = Encoding.UTF8.GetString(buffer, position, length);
        position += length;
        return value;
    }

    /// <summary>Skips bytes whose length was read with <see cref="ReadBinaryLength"/>.</summary>
    public void SkipBytes(int length) => Advance(length);

    /// <summary>Reads the header of a list or a set.</summary>
    /// <param name="elementType">The wire type of the elements.</param>
    /// <returns>The number of elements, checked against the bytes left.</returns>
    public int ReadListHeader(out byte elementType)
    {
        byte header = ReadRawByte();
        int size = header >> 4;
        elementType = (byte)(header & 0x0F);
        if (size == 15)
        {
            uint large = ReadVarint32();
            if (large > int.MaxValue)
            {
                throw Invalid("A list of the footer is too long.");
            }

            size = (int)large;
        }

        // Every element takes at least one byte.
        if (size > Remaining)
        {
            throw Truncated();
        }

        return size;
    }

    /// <summary>Reads a boolean element of a list, which takes one byte.</summary>
    public bool ReadBooleanElement() => ReadRawByte() == BooleanTrue;

    /// <summary>Skips a value of a field, of any wire type, without allocating.</summary>
    public void Skip(byte type)
    {
        switch (type)
        {
            case BooleanTrue:
            case BooleanFalse:
                // In a field header, a boolean has no payload.
                return;
            case Byte:
                Advance(1);
                return;
            case I16:
            case I32:
            case I64:
                ReadVarint64();
                return;
            case Double:
                Advance(8);
                return;
            case Binary:
                SkipBinary();
                return;
            case List:
            case Set:
            {
                int size = ReadListHeader(out byte elementType);
                for (int i = 0; i < size; i++)
                {
                    SkipElement(elementType);
                }

                return;
            }

            case Map:
            {
                uint size = ReadVarint32();
                if (size == 0)
                {
                    return;
                }

                if (size > (uint)Remaining)
                {
                    throw Truncated();
                }

                byte types = ReadRawByte();
                byte keyType = (byte)(types >> 4);
                byte valueType = (byte)(types & 0x0F);
                for (uint i = 0; i < size; i++)
                {
                    SkipElement(keyType);
                    SkipElement(valueType);
                }

                return;
            }

            case Struct:
                SkipStruct();
                return;
            case Uuid:
                Advance(16);
                return;
            default:
                throw Invalid("The footer has a value of unknown wire type " + type + ".");
        }
    }

    /// <summary>Skips an element of a list, a set, or a map: a boolean element takes one byte there.</summary>
    public void SkipElement(byte type)
    {
        if (IsBoolean(type))
        {
            Advance(1);
            return;
        }

        Skip(type);
    }

    /// <summary>Skips a struct, all its fields included.</summary>
    public void SkipStruct()
    {
        EnterStruct();
        short lastId = 0;
        while (ReadFieldHeader(ref lastId, out _, out byte type))
        {
            Skip(type);
        }

        ExitStruct();
    }

    /// <summary>An exception for bytes that do not follow the protocol.</summary>
    public static ParquetFooterException Invalid(string message) => new ParquetFooterException(IssueCodes.FooterInvalid, message);

    private static ParquetFooterException Truncated() =>
        new ParquetFooterException(IssueCodes.FooterInvalid, "The footer ends in the middle of a value.");

    private void Advance(int count)
    {
        Need(count);
        position += count;
    }

    private void Need(int count)
    {
        if (count > Remaining)
        {
            throw Truncated();
        }
    }
}
