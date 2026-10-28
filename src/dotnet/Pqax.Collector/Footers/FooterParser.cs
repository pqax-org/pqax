using System;
using System.Collections.Generic;
using System.Globalization;

namespace Pqax.Collector;

/// <summary>
/// Decodes a <c>FileMetaData</c> of <c>parquet.thrift</c> from the compact protocol. A field that this version does not
/// know is skipped, whatever its type, and so is a known field with an unexpected wire type. A field that appears twice
/// takes the last value. A required field that is missing makes the footer invalid. An empty list may give any type to
/// its elements, as fastparquet writes the row groups of a file without rows.
/// </summary>
internal static class FooterParser
{
    public static ParquetFooter Parse(byte[] buffer, int offset, int count, bool includeDataValues)
    {
        var reader = new ThriftCompactReader(buffer, offset, count);
        int? version = null;
        long? numRows = null;
        string? createdBy = null;
        List<ParquetSchemaElement>? schema = null;
        List<RawRowGroup>? rowGroups = null;
        List<ParquetKeyValue>? keyValues = null;
        bool encryption = false;
        var counts = new Counts();

        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            switch (id)
            {
                case 1 when type == ThriftCompactReader.I32:
                    version = reader.ReadI32();
                    break;
                case 2 when type == ThriftCompactReader.List:
                    schema = ReadSchema(reader);
                    break;
                case 3 when IsInteger(type):
                    numRows = reader.ReadInteger(type);
                    break;
                case 4 when type == ThriftCompactReader.List:
                    rowGroups = ReadRowGroups(reader, includeDataValues, counts);
                    break;
                case 5 when type == ThriftCompactReader.List:
                    keyValues = ReadKeyValues(reader);
                    break;
                case 6 when type == ThriftCompactReader.Binary:
                    createdBy = reader.ReadString();
                    break;
                case 8 when type == ThriftCompactReader.Struct:
                    encryption = true;
                    reader.SkipStruct();
                    break;
                default:
                    // 7 column_orders, 9 footer_signing_key_metadata, and anything newer.
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct();
        if (version == null || schema == null || numRows == null || rowGroups == null)
        {
            throw ThriftCompactReader.Invalid("The footer lacks a required field: version, schema, num_rows, or row_groups.");
        }

        List<ParquetLeafColumn> leaves = Leaves(schema);
        var groups = new List<ParquetRowGroup>(rowGroups.Count);
        foreach (RawRowGroup raw in rowGroups)
        {
            if (raw.Columns.Count != leaves.Count)
            {
                throw ThriftCompactReader.Invalid(
                    "A row group has " + raw.Columns.Count.ToString(CultureInfo.InvariantCulture) + " column chunks for "
                    + leaves.Count.ToString(CultureInfo.InvariantCulture) + " leaf columns.");
            }

            var chunks = new ParquetColumnChunk[raw.Columns.Count];
            for (int i = 0; i < chunks.Length; i++)
            {
                RawColumnChunk rawChunk = raw.Columns[i];
                if (includeDataValues)
                {
                    rawChunk.Chunk.MinValue = StatisticsText.Format(rawChunk.Min, leaves[i]);
                    rawChunk.Chunk.MaxValue = StatisticsText.Format(rawChunk.Max, leaves[i]);
                }

                chunks[i] = rawChunk.Chunk;
            }

            groups.Add(new ParquetRowGroup(chunks)
            {
                NumRows = raw.NumRows,
                TotalByteSize = raw.TotalByteSize,
                TotalCompressedSize = raw.TotalCompressedSize,
                FileOffset = raw.FileOffset,
                Ordinal = raw.Ordinal,
                SortingColumns = raw.SortingColumns,
            });
        }

        return new ParquetFooter(
            version.Value,
            numRows.Value,
            createdBy,
            (IReadOnlyList<ParquetKeyValue>?)keyValues ?? new ParquetKeyValue[0],
            schema,
            leaves,
            groups,
            encryption);
    }

    private static bool IsInteger(byte type) =>
        type == ThriftCompactReader.I64 || type == ThriftCompactReader.I32 || type == ThriftCompactReader.I16 || type == ThriftCompactReader.Byte;

    private static List<ParquetSchemaElement> ReadSchema(ThriftCompactReader reader)
    {
        int size = reader.EnterList(out byte elementType);
        if (size > 0 && elementType != ThriftCompactReader.Struct)
        {
            throw ThriftCompactReader.Invalid("The schema of the footer is not a list of structures.");
        }

        var elements = new List<ParquetSchemaElement>(size);
        for (int i = 0; i < size; i++)
        {
            elements.Add(ReadSchemaElement(reader));
        }

        reader.ExitList();
        return elements;
    }

    private static ParquetSchemaElement ReadSchemaElement(ThriftCompactReader reader)
    {
        var element = new ParquetSchemaElement();
        bool hasName = false;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            switch (id)
            {
                case 1 when type == ThriftCompactReader.I32:
                    element.Type = ParquetNames.Type(reader.ReadI32());
                    break;
                case 2 when type == ThriftCompactReader.I32:
                    element.TypeLength = reader.ReadI32();
                    break;
                case 3 when type == ThriftCompactReader.I32:
                    element.Repetition = ParquetNames.Repetition(reader.ReadI32());
                    break;
                case 4 when type == ThriftCompactReader.Binary:
                    element.Name = reader.ReadString();
                    hasName = true;
                    break;
                case 5 when type == ThriftCompactReader.I32:
                    element.NumChildren = reader.ReadI32();
                    break;
                case 6 when type == ThriftCompactReader.I32:
                    element.ConvertedType = ParquetNames.ConvertedType(reader.ReadI32());
                    break;
                case 7 when type == ThriftCompactReader.I32:
                    element.Scale = reader.ReadI32();
                    break;
                case 8 when type == ThriftCompactReader.I32:
                    element.Precision = reader.ReadI32();
                    break;
                case 9 when type == ThriftCompactReader.I32:
                    element.FieldId = reader.ReadI32();
                    break;
                case 10 when type == ThriftCompactReader.Struct:
                    element.LogicalType = ReadLogicalType(reader);
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct();
        if (!hasName)
        {
            throw ThriftCompactReader.Invalid("A schema element of the footer has no name.");
        }

        if (element.NumChildren is int children && children < 0)
        {
            throw ThriftCompactReader.Invalid("A schema element of the footer has a negative number of children.");
        }

        return element;
    }

    /// <summary>Reads the union <c>LogicalType</c> as text, such as <c>DECIMAL(18,2)</c> or <c>TIMESTAMP(MICROS, UTC)</c>.</summary>
    private static string? ReadLogicalType(ThriftCompactReader reader)
    {
        string? text = null;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            if (type != ThriftCompactReader.Struct)
            {
                reader.Skip(type);
                continue;
            }

            switch (id)
            {
                case 1:
                    text = Empty(reader, "STRING");
                    break;
                case 2:
                    text = Empty(reader, "MAP");
                    break;
                case 3:
                    text = Empty(reader, "LIST");
                    break;
                case 4:
                    text = Empty(reader, "ENUM");
                    break;
                case 5:
                    text = ReadDecimal(reader);
                    break;
                case 6:
                    text = Empty(reader, "DATE");
                    break;
                case 7:
                    text = ReadTime(reader, "TIME");
                    break;
                case 8:
                    text = ReadTime(reader, "TIMESTAMP");
                    break;
                case 10:
                    text = ReadIntegerType(reader);
                    break;
                case 11:
                    text = Empty(reader, "UNKNOWN");
                    break;
                case 12:
                    text = Empty(reader, "JSON");
                    break;
                case 13:
                    text = Empty(reader, "BSON");
                    break;
                case 14:
                    text = Empty(reader, "UUID");
                    break;
                case 15:
                    text = Empty(reader, "FLOAT16");
                    break;
                case 16:
                    text = ReadVariant(reader);
                    break;
                case 17:
                    text = ReadGeo(reader, "GEOMETRY");
                    break;
                case 18:
                    text = ReadGeo(reader, "GEOGRAPHY");
                    break;
                default:
                    reader.SkipStruct();
                    text = "LOGICAL(" + id.ToString(CultureInfo.InvariantCulture) + ")";
                    break;
            }
        }

        reader.ExitStruct();
        return text;
    }

    private static string Empty(ThriftCompactReader reader, string name)
    {
        reader.SkipStruct();
        return name;
    }

    private static string ReadDecimal(ThriftCompactReader reader)
    {
        int? scale = null;
        int? precision = null;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            if (id == 1 && type == ThriftCompactReader.I32)
            {
                scale = reader.ReadI32();
            }
            else if (id == 2 && type == ThriftCompactReader.I32)
            {
                precision = reader.ReadI32();
            }
            else
            {
                reader.Skip(type);
            }
        }

        reader.ExitStruct();
        return "DECIMAL(" + Text(precision) + "," + Text(scale) + ")";
    }

    private static string ReadTime(ThriftCompactReader reader, string name)
    {
        bool? utc = null;
        string? unit = null;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            if (id == 1 && ThriftCompactReader.IsBoolean(type))
            {
                utc = type == ThriftCompactReader.BooleanTrue;
            }
            else if (id == 2 && type == ThriftCompactReader.Struct)
            {
                unit = ReadUnion(reader, ParquetNames.TimeUnit);
            }
            else
            {
                reader.Skip(type);
            }
        }

        reader.ExitStruct();
        string adjusted = utc == null ? "?" : utc.Value ? "UTC" : "LOCAL";
        return name + "(" + (unit ?? "?") + ", " + adjusted + ")";
    }

    private static string ReadIntegerType(ThriftCompactReader reader)
    {
        long? bitWidth = null;
        bool? signed = null;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            if (id == 1 && IsInteger(type))
            {
                bitWidth = reader.ReadInteger(type);
            }
            else if (id == 2 && ThriftCompactReader.IsBoolean(type))
            {
                signed = type == ThriftCompactReader.BooleanTrue;
            }
            else
            {
                reader.Skip(type);
            }
        }

        reader.ExitStruct();
        string width = bitWidth == null ? "?" : bitWidth.Value.ToString(CultureInfo.InvariantCulture);
        string sign = signed == null ? "?" : signed.Value ? "SIGNED" : "UNSIGNED";
        return "INTEGER(" + width + ", " + sign + ")";
    }

    private static string ReadVariant(ThriftCompactReader reader)
    {
        long? specification = null;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            if (id == 1 && IsInteger(type))
            {
                specification = reader.ReadInteger(type);
            }
            else
            {
                reader.Skip(type);
            }
        }

        reader.ExitStruct();
        return specification == null ? "VARIANT" : "VARIANT(" + specification.Value.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static string ReadGeo(ThriftCompactReader reader, string name)
    {
        string? crs = null;
        string? algorithm = null;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            if (id == 1 && type == ThriftCompactReader.Binary)
            {
                crs = reader.ReadString();
            }
            else if (id == 2 && type == ThriftCompactReader.I32)
            {
                algorithm = ParquetNames.EdgeAlgorithm(reader.ReadI32());
            }
            else
            {
                reader.Skip(type);
            }
        }

        reader.ExitStruct();
        var parameters = new List<string>();
        if (crs != null)
        {
            parameters.Add(crs);
        }

        if (algorithm != null)
        {
            parameters.Add(algorithm);
        }

        return parameters.Count == 0 ? name : name + "(" + string.Join(", ", parameters) + ")";
    }

    /// <summary>Reads a union of empty structures, such as <c>TimeUnit</c>, as the name of the member that is set.</summary>
    private static string? ReadUnion(ThriftCompactReader reader, Func<int, string> name)
    {
        string? member = null;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            if (type == ThriftCompactReader.Struct)
            {
                reader.SkipStruct();
                member = name(id);
            }
            else
            {
                reader.Skip(type);
            }
        }

        reader.ExitStruct();
        return member;
    }

    private static List<RawRowGroup> ReadRowGroups(ThriftCompactReader reader, bool includeDataValues, Counts counts)
    {
        int size = reader.EnterList(out byte elementType);
        if (size > 0 && elementType != ThriftCompactReader.Struct)
        {
            throw ThriftCompactReader.Invalid("The row groups of the footer are not a list of structures.");
        }

        var groups = new List<RawRowGroup>(size);
        for (int i = 0; i < size; i++)
        {
            groups.Add(ReadRowGroup(reader, includeDataValues, counts));
        }

        reader.ExitList();
        return groups;
    }

    private static RawRowGroup ReadRowGroup(ThriftCompactReader reader, bool includeDataValues, Counts counts)
    {
        var group = new RawRowGroup();
        bool hasColumns = false;
        bool hasRows = false;
        bool hasByteSize = false;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            switch (id)
            {
                case 1 when type == ThriftCompactReader.List:
                {
                    int size = reader.EnterList(out byte elementType);
                    if (size > 0 && elementType != ThriftCompactReader.Struct)
                    {
                        throw ThriftCompactReader.Invalid("The column chunks of a row group are not a list of structures.");
                    }

                    // Every chunk read counts, a list that a second one replaces included: the limit bounds the memory.
                    counts.ColumnChunks += size;
                    if (counts.ColumnChunks > FooterReader.MaxColumnChunks)
                    {
                        throw new ParquetFooterException(
                            IssueCodes.FooterTooLarge,
                            "The row groups of the footer hold more column chunks than the limit of " + FooterReader.MaxColumnChunks.ToString(CultureInfo.InvariantCulture) + ".");
                    }

                    // A second list of column chunks replaces the first, as every other field that appears twice.
                    group.Columns.Clear();
                    group.Columns.Capacity = size;
                    for (int i = 0; i < size; i++)
                    {
                        group.Columns.Add(ReadColumnChunk(reader, includeDataValues));
                    }

                    reader.ExitList();
                    hasColumns = true;
                    break;
                }

                case 2 when IsInteger(type):
                    group.TotalByteSize = reader.ReadInteger(type);
                    hasByteSize = true;
                    break;
                case 3 when IsInteger(type):
                    group.NumRows = reader.ReadInteger(type);
                    hasRows = true;
                    break;
                case 4 when type == ThriftCompactReader.List:
                    group.SortingColumns = ReadSortingColumns(reader, counts);
                    break;
                case 5 when IsInteger(type):
                    group.FileOffset = reader.ReadInteger(type);
                    break;
                case 6 when IsInteger(type):
                    group.TotalCompressedSize = reader.ReadInteger(type);
                    break;
                case 7 when IsInteger(type):
                    group.Ordinal = (int)reader.ReadInteger(type);
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct();
        if (!hasColumns || !hasRows || !hasByteSize)
        {
            throw ThriftCompactReader.Invalid("A row group of the footer lacks its column chunks, its number of rows, or its size.");
        }

        return group;
    }

    private static List<ParquetSortingColumn>? ReadSortingColumns(ThriftCompactReader reader, Counts counts)
    {
        int size = reader.EnterList(out byte elementType);
        if (size > 0 && elementType != ThriftCompactReader.Struct)
        {
            SkipElements(reader, elementType, size);
            reader.ExitList();
            return null;
        }

        // An empty sorting column takes one byte, as an empty column chunk does: the same limit, over all the row groups.
        counts.SortingColumns += size;
        if (counts.SortingColumns > FooterReader.MaxColumnChunks)
        {
            throw new ParquetFooterException(
                IssueCodes.FooterTooLarge,
                "The row groups of the footer hold more sorting columns than the limit of " + FooterReader.MaxColumnChunks.ToString(CultureInfo.InvariantCulture) + ".");
        }

        var columns = new List<ParquetSortingColumn>(size);
        for (int i = 0; i < size; i++)
        {
            int index = -1;
            bool descending = false;
            bool nullsFirst = false;
            reader.EnterStruct();
            short lastId = 0;
            while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
            {
                if (id == 1 && type == ThriftCompactReader.I32)
                {
                    index = reader.ReadI32();
                }
                else if (id == 2 && ThriftCompactReader.IsBoolean(type))
                {
                    descending = type == ThriftCompactReader.BooleanTrue;
                }
                else if (id == 3 && ThriftCompactReader.IsBoolean(type))
                {
                    nullsFirst = type == ThriftCompactReader.BooleanTrue;
                }
                else
                {
                    reader.Skip(type);
                }
            }

            reader.ExitStruct();
            columns.Add(new ParquetSortingColumn(index, descending, nullsFirst));
        }

        reader.ExitList();
        return columns;
    }

    private static RawColumnChunk ReadColumnChunk(ThriftCompactReader reader, bool includeDataValues)
    {
        var raw = new RawColumnChunk();
        ParquetColumnChunk chunk = raw.Chunk;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            switch (id)
            {
                case 1 when type == ThriftCompactReader.Binary:
                    chunk.FilePath = reader.ReadString();
                    break;
                case 3 when type == ThriftCompactReader.Struct:
                    ReadColumnMetaData(reader, raw, includeDataValues);
                    chunk.HasMetadata = true;
                    break;
                case 4 when IsInteger(type):
                    chunk.OffsetIndexOffset = reader.ReadInteger(type);
                    break;
                case 5 when IsInteger(type):
                    chunk.OffsetIndexLength = (int)reader.ReadInteger(type);
                    break;
                case 6 when IsInteger(type):
                    chunk.ColumnIndexOffset = reader.ReadInteger(type);
                    break;
                case 7 when IsInteger(type):
                    chunk.ColumnIndexLength = (int)reader.ReadInteger(type);
                    break;
                case 8:
                case 9:
                    // crypto_metadata and encrypted_column_metadata: only their presence is kept.
                    chunk.IsEncrypted = true;
                    reader.Skip(type);
                    break;
                default:
                    // 2 file_offset, which is deprecated, and anything newer.
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct();
        return raw;
    }

    private static void ReadColumnMetaData(ThriftCompactReader reader, RawColumnChunk raw, bool includeDataValues)
    {
        ParquetColumnChunk chunk = raw.Chunk;

        // A meta_data that appears twice replaces the first whole, as any field that appears twice.
        chunk.PhysicalType = null;
        chunk.Encodings = new string[0];
        chunk.Codec = null;
        chunk.NumValues = 0;
        chunk.TotalUncompressedSize = 0;
        chunk.TotalCompressedSize = 0;
        chunk.DataPageOffset = 0;
        chunk.IndexPageOffset = null;
        chunk.DictionaryPageOffset = null;
        ResetStatistics(raw);
        chunk.HasStatistics = false;
        chunk.EncodingStats = null;
        chunk.BloomFilterOffset = null;
        chunk.BloomFilterLength = null;
        chunk.UnencodedByteArrayDataBytes = null;
        int seen = 0;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            switch (id)
            {
                case 1 when type == ThriftCompactReader.I32:
                    chunk.PhysicalType = ParquetNames.Type(reader.ReadI32());
                    break;
                case 2 when type == ThriftCompactReader.List:
                    chunk.Encodings = ReadEncodings(reader);
                    break;
                case 4 when type == ThriftCompactReader.I32:
                    chunk.Codec = ParquetNames.Codec(reader.ReadI32());
                    break;
                case 5 when IsInteger(type):
                    chunk.NumValues = reader.ReadInteger(type);
                    seen |= 1;
                    break;
                case 6 when IsInteger(type):
                    chunk.TotalUncompressedSize = reader.ReadInteger(type);
                    seen |= 2;
                    break;
                case 7 when IsInteger(type):
                    chunk.TotalCompressedSize = reader.ReadInteger(type);
                    seen |= 4;
                    break;
                case 9 when IsInteger(type):
                    chunk.DataPageOffset = reader.ReadInteger(type);
                    seen |= 8;
                    break;
                case 10 when IsInteger(type):
                    chunk.IndexPageOffset = reader.ReadInteger(type);
                    break;
                case 11 when IsInteger(type):
                    chunk.DictionaryPageOffset = reader.ReadInteger(type);
                    break;
                case 12 when type == ThriftCompactReader.Struct:
                    ReadStatistics(reader, raw, includeDataValues);
                    chunk.HasStatistics = true;
                    break;
                case 13 when type == ThriftCompactReader.List:
                    chunk.EncodingStats = ReadEncodingStats(reader);
                    break;
                case 14 when IsInteger(type):
                    chunk.BloomFilterOffset = reader.ReadInteger(type);
                    break;
                case 15 when IsInteger(type):
                    chunk.BloomFilterLength = (int)reader.ReadInteger(type);
                    break;
                case 16 when type == ThriftCompactReader.Struct:
                    chunk.UnencodedByteArrayDataBytes = ReadSizeStatistics(reader);
                    break;
                default:
                    // 3 path_in_schema, which the schema already gives; 8 key_value_metadata; 17 geospatial_statistics,
                    // which are data values; and anything newer.
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct();
        if (seen != 15)
        {
            throw ThriftCompactReader.Invalid("The metadata of a column chunk lacks num_values, a size, or data_page_offset.");
        }
    }

    private static IReadOnlyList<string> ReadEncodings(ThriftCompactReader reader)
    {
        int size = reader.EnterList(out byte elementType);
        if (size > 0 && elementType != ThriftCompactReader.I32)
        {
            SkipElements(reader, elementType, size);
            reader.ExitList();
            return new string[0];
        }

        var values = new int[size];
        for (int i = 0; i < size; i++)
        {
            values[i] = reader.ReadI32();
        }

        reader.ExitList();
        return ParquetNames.Encodings(values, size);
    }

    /// <summary>Forgets the statistics of a chunk, so that a statistics that appears twice replaces the first whole.</summary>
    private static void ResetStatistics(RawColumnChunk raw)
    {
        raw.Chunk.NullCount = null;
        raw.Chunk.DistinctCount = null;
        raw.Chunk.HasMinMax = false;
        raw.Min = null;
        raw.Max = null;
    }

    private static void ReadStatistics(ThriftCompactReader reader, RawColumnChunk raw, bool includeDataValues)
    {
        ParquetColumnChunk chunk = raw.Chunk;
        ResetStatistics(raw);
        byte[]? legacyMax = null;
        byte[]? legacyMin = null;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            bool bound = type == ThriftCompactReader.Binary && (id == 1 || id == 2 || id == 5 || id == 6);
            if (bound)
            {
                chunk.HasMinMax = true;
                if (!includeDataValues)
                {
                    // A bound is a data value: skipped without being read.
                    reader.SkipBinary();
                }
                else if (id == 1)
                {
                    legacyMax = reader.ReadBinary();
                }
                else if (id == 2)
                {
                    legacyMin = reader.ReadBinary();
                }
                else if (id == 5)
                {
                    raw.Max = reader.ReadBinary();
                }
                else
                {
                    raw.Min = reader.ReadBinary();
                }
            }
            else if (id == 3 && IsInteger(type))
            {
                chunk.NullCount = reader.ReadInteger(type);
            }
            else if (id == 4 && IsInteger(type))
            {
                chunk.DistinctCount = reader.ReadInteger(type);
            }
            else
            {
                reader.Skip(type);
            }
        }

        reader.ExitStruct();
        raw.Min ??= legacyMin;
        raw.Max ??= legacyMax;
    }

    private static List<ParquetPageEncodingStats>? ReadEncodingStats(ThriftCompactReader reader)
    {
        int size = reader.EnterList(out byte elementType);
        if (size > 0 && elementType != ThriftCompactReader.Struct)
        {
            SkipElements(reader, elementType, size);
            reader.ExitList();
            return null;
        }

        var stats = new List<ParquetPageEncodingStats>(size);
        for (int i = 0; i < size; i++)
        {
            int? pageType = null;
            int? encoding = null;
            int? count = null;
            reader.EnterStruct();
            short lastId = 0;
            while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
            {
                if (id == 1 && type == ThriftCompactReader.I32)
                {
                    pageType = reader.ReadI32();
                }
                else if (id == 2 && type == ThriftCompactReader.I32)
                {
                    encoding = reader.ReadI32();
                }
                else if (id == 3 && type == ThriftCompactReader.I32)
                {
                    count = reader.ReadI32();
                }
                else
                {
                    reader.Skip(type);
                }
            }

            reader.ExitStruct();
            if (pageType != null && encoding != null && count != null)
            {
                stats.Add(new ParquetPageEncodingStats(ParquetNames.PageType(pageType.Value), ParquetNames.Encoding(encoding.Value), count.Value));
            }
        }

        reader.ExitList();
        return stats;
    }

    private static long? ReadSizeStatistics(ThriftCompactReader reader)
    {
        long? unencoded = null;
        reader.EnterStruct();
        short lastId = 0;
        while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
        {
            if (id == 1 && IsInteger(type))
            {
                unencoded = reader.ReadInteger(type);
            }
            else
            {
                // 2 and 3: the level histograms, which the model leaves out.
                reader.Skip(type);
            }
        }

        reader.ExitStruct();
        return unencoded;
    }

    private static List<ParquetKeyValue> ReadKeyValues(ThriftCompactReader reader)
    {
        int size = reader.EnterList(out byte elementType);
        var result = new List<ParquetKeyValue>();
        if (size > 0 && elementType != ThriftCompactReader.Struct)
        {
            SkipElements(reader, elementType, size);
            reader.ExitList();
            return result;
        }

        for (int i = 0; i < size; i++)
        {
            string? key = null;
            int? valueLength = null;
            string? value = null;
            reader.EnterStruct();
            short lastId = 0;
            while (reader.ReadFieldHeader(ref lastId, out short id, out byte type))
            {
                if (id == 1 && type == ThriftCompactReader.Binary)
                {
                    key = reader.ReadString();
                }
                else if (id == 2 && type == ThriftCompactReader.Binary)
                {
                    // The key comes before the value in every writer, as in parquet.thrift. A value of a key that is not
                    // on the allow-list, or that comes before its key, is skipped without being read.
                    int length = reader.ReadBinaryLength();
                    valueLength = length;
                    if (key != null && ParquetKeyValue.IsKept(key) && length <= ParquetKeyValue.MaxKeptValueLength * 4)
                    {
                        string text = reader.ReadUtf8(length);
                        value = text.Length <= ParquetKeyValue.MaxKeptValueLength ? text : null;
                    }
                    else
                    {
                        reader.SkipBytes(length);
                    }
                }
                else
                {
                    reader.Skip(type);
                }
            }

            reader.ExitStruct();
            if (key != null)
            {
                result.Add(new ParquetKeyValue(key, valueLength, value));
            }
        }

        reader.ExitList();
        return result;
    }

    private static List<ParquetLeafColumn> Leaves(List<ParquetSchemaElement> schema)
    {
        if (schema.Count == 0)
        {
            throw ThriftCompactReader.Invalid("The schema of the footer has no root.");
        }

        var leaves = new List<ParquetLeafColumn>();
        int index = 1;
        var path = new List<string>();
        Visit(schema, ref index, schema[0].NumChildren ?? 0, path, null, leaves, 1);
        if (index != schema.Count)
        {
            throw ThriftCompactReader.Invalid("The schema of the footer has elements that its tree does not reach.");
        }

        return leaves;
    }

    private static void Visit(
        List<ParquetSchemaElement> schema,
        ref int index,
        int children,
        List<string> path,
        int? topLevelFieldId,
        List<ParquetLeafColumn> leaves,
        int depth)
    {
        if (depth > 256)
        {
            throw ThriftCompactReader.Invalid("The schema of the footer is nested too deep.");
        }

        for (int i = 0; i < children; i++)
        {
            if (index >= schema.Count)
            {
                throw ThriftCompactReader.Invalid("The schema of the footer declares more children than it has elements.");
            }

            ParquetSchemaElement element = schema[index++];
            int? fieldId = depth == 1 ? element.FieldId : topLevelFieldId;
            path.Add(element.Name);
            if (element.NumChildren != null)
            {
                Visit(schema, ref index, element.NumChildren.Value, path, fieldId, leaves, depth + 1);
            }
            else
            {
                leaves.Add(new ParquetLeafColumn(leaves.Count, path.ToArray(), element, fieldId));
            }

            path.RemoveAt(path.Count - 1);
        }
    }

    private static void SkipElements(ThriftCompactReader reader, byte elementType, int size)
    {
        for (int i = 0; i < size; i++)
        {
            reader.SkipElement(elementType);
        }
    }

    private static string Text(int? value) => value == null ? "?" : value.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The column chunks and the sorting columns read so far, over all the row groups of a footer.</summary>
    private sealed class Counts
    {
        public int ColumnChunks { get; set; }

        public int SortingColumns { get; set; }
    }

    /// <summary>A row group while it is read.</summary>
    private sealed class RawRowGroup
    {
        public List<RawColumnChunk> Columns { get; } = new List<RawColumnChunk>();

        public long NumRows { get; set; }

        public long TotalByteSize { get; set; }

        public long? TotalCompressedSize { get; set; }

        public long? FileOffset { get; set; }

        public int? Ordinal { get; set; }

        public IReadOnlyList<ParquetSortingColumn>? SortingColumns { get; set; }
    }

    /// <summary>A column chunk while it is read, with the bounds as bytes until the schema gives their type.</summary>
    private sealed class RawColumnChunk
    {
        public ParquetColumnChunk Chunk { get; } = new ParquetColumnChunk();

        public byte[]? Min { get; set; }

        public byte[]? Max { get; set; }
    }
}
