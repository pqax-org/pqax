using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Pqax.Collector.Tests;

/// <summary>
/// A temporary folder with the layout of a OneLake workspace, for what a test writes itself and the fixture does not
/// have: Delta logs of any length, items named by id, files of any kind. Deleted when disposed.
/// </summary>
public sealed class TempLake : IDisposable
{
    public TempLake()
    {
        Root = Path.Combine(Path.GetTempPath(), "pqax-lake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>The folder, which plays the workspace.</summary>
    public string Root { get; }

    /// <summary>A copy of the fixture, for a test that does to a file on disk what the other tests must not see.</summary>
    public static TempLake CopyOfFixture()
    {
        var lake = new TempLake();
        foreach (string file in Directory.EnumerateFiles(Fixture.Folder, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(lake.Root, Path.GetRelativePath(Fixture.Folder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return lake;
    }

    /// <summary>The full path on disk of a path of the storage, which has forward slashes.</summary>
    public string FullPath(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Writes a file and the folders above it.</summary>
    public void Write(string path, byte[] content)
    {
        string full = FullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    public void Write(string path, string text) => Write(path, Encoding.UTF8.GetBytes(text));

    /// <summary>Writes the commit file of a version in the log of a table, one action for each line.</summary>
    public void Commit(string table, long version, params JsonNode[] actions) =>
        Write(table + "/_delta_log/" + LogLines.CommitName(version), string.Concat(actions.Select(action => action.ToJsonString() + "\n")));

    /// <summary>
    /// Writes a classic checkpoint of a version, whose bytes only a <see cref="StateCheckpointReader"/> reads, and the
    /// <c>_last_checkpoint</c> that names it.
    /// </summary>
    public void Checkpoint(string table, long version)
    {
        Write(table + "/_delta_log/" + version.ToString("D20", CultureInfo.InvariantCulture) + ".checkpoint.parquet", "A checkpoint for a test reader.");
        Write(table + "/_delta_log/_last_checkpoint", new JsonObject { ["version"] = version, ["size"] = 2 }.ToJsonString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A file still open: the folder stays in the temporary folder of the machine.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The actions of the Delta logs that the tests write, as JSON lines: a table with one column, <c>id</c>, whose commit
/// of each version records a time one minute after the previous one.
/// </summary>
public static class LogLines
{
    /// <summary>The time of version 0, in milliseconds since 1970.</summary>
    public const long Epoch = 1_700_000_000_000;

    public const string TableId = "00000000-0000-4000-8000-000000000001";

    public static string SchemaString { get; } = new JsonObject
    {
        ["type"] = "struct",
        ["fields"] = new JsonArray(new JsonObject { ["name"] = "id", ["type"] = "long", ["nullable"] = true, ["metadata"] = new JsonObject() }),
    }.ToJsonString();

    public static string CommitName(long version) => version.ToString("D20", CultureInfo.InvariantCulture) + ".json";

    /// <summary>The time that the commit of a version records.</summary>
    public static DateTimeOffset TimeOf(long version) => DateTimeOffset.FromUnixTimeMilliseconds(Epoch + (version * 60_000));

    public static JsonObject Protocol() => new() { ["protocol"] = new JsonObject { ["minReaderVersion"] = 1, ["minWriterVersion"] = 2 } };

    public static JsonObject Metadata() => new()
    {
        ["metaData"] = new JsonObject
        {
            ["id"] = TableId,
            ["format"] = new JsonObject { ["provider"] = "parquet", ["options"] = new JsonObject() },
            ["schemaString"] = SchemaString,
            ["partitionColumns"] = new JsonArray(),
            ["configuration"] = new JsonObject(),
            ["createdTime"] = Epoch,
        },
    };

    public static JsonObject CommitInfo(long version) => new()
    {
        ["commitInfo"] = new JsonObject
        {
            ["timestamp"] = TimeOf(version).ToUnixTimeMilliseconds(),
            ["operation"] = "WRITE",
            ["engineInfo"] = "Test engine",
            ["isBlindAppend"] = true,
            ["operationMetrics"] = new JsonObject { ["numFiles"] = "0" },
        },
    };

    /// <summary>The add action of a file, with its rows in the statistics. A null size leaves the size out of the log.</summary>
    public static JsonObject Add(string path, long? size, long rows)
    {
        var add = new JsonObject
        {
            ["path"] = path,
            ["partitionValues"] = new JsonObject(),
            ["modificationTime"] = Epoch,
            ["dataChange"] = true,
            ["stats"] = new JsonObject { ["numRecords"] = rows }.ToJsonString(),
        };
        if (size != null)
        {
            add["size"] = size.Value;
        }

        return new JsonObject { ["add"] = add };
    }

    /// <summary>
    /// The add action of a file, with its rows in the statistics, a modification time, or none when null, and the
    /// values of the partition column <c>p</c> of <see cref="PartitionedMetadata"/>, or no partition values when null.
    /// </summary>
    public static JsonObject Add(string path, long? size, long rows, long? modificationTime, string? partition = null)
    {
        JsonObject action = Add(path, size, rows);
        JsonObject add = action["add"]!.AsObject();
        add.Remove("modificationTime");
        if (modificationTime != null)
        {
            add["modificationTime"] = modificationTime.Value;
        }

        if (partition != null)
        {
            add["partitionValues"] = new JsonObject { ["p"] = partition };
        }

        return action;
    }

    /// <summary>The remove action of a file, with its size and its deletion time, each left out of the log when null.</summary>
    public static JsonObject Remove(string path, long? size, long? deletionTimestamp)
    {
        var remove = new JsonObject { ["path"] = path, ["dataChange"] = true };
        if (deletionTimestamp != null)
        {
            remove["deletionTimestamp"] = deletionTimestamp.Value;
        }

        if (size != null)
        {
            remove["size"] = size.Value;
        }

        return new JsonObject { ["remove"] = remove };
    }

    /// <summary>The metadata of a table with two columns, <c>id</c> and the partition column <c>p</c>, a string.</summary>
    public static JsonObject PartitionedMetadata()
    {
        JsonObject metadata = Metadata();
        JsonObject inner = metadata["metaData"]!.AsObject();
        inner["schemaString"] = new JsonObject
        {
            ["type"] = "struct",
            ["fields"] = new JsonArray(
                new JsonObject { ["name"] = "id", ["type"] = "long", ["nullable"] = true, ["metadata"] = new JsonObject() },
                new JsonObject { ["name"] = "p", ["type"] = "string", ["nullable"] = true, ["metadata"] = new JsonObject() }),
        }.ToJsonString();
        inner["partitionColumns"] = new JsonArray("p");
        return metadata;
    }

    /// <summary>The state that a checkpoint of these tables holds: the protocol and the metadata, and no file.</summary>
    public static IReadOnlyList<DeltaAction> CheckpointState() =>
    [
        new ProtocolAction { MinReaderVersion = 1, MinWriterVersion = 2 },
        new MetadataAction
        {
            Id = TableId,
            Provider = "parquet",
            SchemaString = SchemaString,
            PartitionColumns = [],
            Configuration = new Dictionary<string, string>(),
            CreatedTime = Epoch,
        },
    ];
}

/// <summary>
/// A checkpoint reader for the tables that the tests write: whatever the file holds, it gives
/// <see cref="LogLines.CheckpointState"/>. It records the paths it is asked for.
/// </summary>
public sealed class StateCheckpointReader : ICheckpointReader
{
    private readonly List<string> paths = [];

    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (paths)
            {
                return paths.ToList();
            }
        }
    }

    public Task<IReadOnlyList<DeltaAction>> ReadAsync(IStorage storage, string path, CancellationToken cancellation)
    {
        lock (paths)
        {
            paths.Add(path);
        }

        return Task.FromResult(LogLines.CheckpointState());
    }
}

/// <summary>Writes the Thrift compact protocol of parquet.thrift, to build footers that the fixture does not have.</summary>
public sealed class ThriftWriter
{
    private const byte I32 = 5;
    private const byte I64 = 6;
    private const byte Binary = 8;
    private const byte List = 9;
    private const byte Struct = 12;

    private readonly List<byte> bytes = [];
    private readonly Stack<short> outer = new();
    private short lastId;

    public ThriftWriter Int32(short id, int value)
    {
        Header(id, I32);
        Varint(Zigzag(value));
        return this;
    }

    public ThriftWriter Int64(short id, long value)
    {
        Header(id, I64);
        Varint(Zigzag(value));
        return this;
    }

    public ThriftWriter Text(short id, string value)
    {
        Header(id, Binary);
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        Varint((ulong)utf8.Length);
        bytes.AddRange(utf8);
        return this;
    }

    /// <summary>A field that is a list of structures. Each one follows, from <see cref="Element"/> to <see cref="End"/>.</summary>
    public ThriftWriter Structs(short id, int count)
    {
        Header(id, List);
        if (count < 15)
        {
            bytes.Add((byte)((count << 4) | Struct));
        }
        else
        {
            bytes.Add(0xF0 | Struct);
            Varint((ulong)count);
        }

        return this;
    }

    /// <summary>Starts a structure that is an element of a list: its field ids start again.</summary>
    public ThriftWriter Element()
    {
        outer.Push(lastId);
        lastId = 0;
        return this;
    }

    /// <summary>Ends a structure, or the message at the top level, with the stop byte.</summary>
    public ThriftWriter End()
    {
        bytes.Add(0);
        lastId = outer.Count > 0 ? outer.Pop() : (short)0;
        return this;
    }

    public byte[] ToArray() => [.. bytes];

    private void Header(short id, byte type)
    {
        int delta = id - lastId;
        if (delta is > 0 and <= 15)
        {
            bytes.Add((byte)((delta << 4) | type));
        }
        else
        {
            bytes.Add(type);
            Varint(Zigzag(id));
        }

        lastId = id;
    }

    private void Varint(ulong value)
    {
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
    }

    private static ulong Zigzag(long value) => (ulong)((value << 1) ^ (value >> 63));
}

/// <summary>Parquet files, or their ends, that the fixture does not have.</summary>
public static class ParquetFiles
{
    /// <summary>
    /// A footer with one INT32 column and one row group whose column chunk has no metadata, as when the column is
    /// encrypted. Valid in every other way.
    /// </summary>
    public static byte[] FooterWithoutChunkMetadata()
    {
        var footer = new ThriftWriter();
        footer.Int32(1, 1);                                              // version
        footer.Structs(2, 2);                                            // schema: the root and one leaf
        footer.Element().Text(4, "schema").Int32(5, 1).End();            //   name, num_children
        footer.Element().Int32(1, 1).Int32(3, 0).Text(4, "id").End();    //   type INT32, repetition REQUIRED, name
        footer.Int64(3, 100);                                            // num_rows
        footer.Structs(4, 1);                                            // row_groups
        footer.Element().Structs(1, 1);                                  //   columns
        footer.Element().Int64(2, 4).End();                              //     file_offset, and no meta_data
        footer.Int64(2, 400).Int64(3, 100).End();                        //   total_byte_size, num_rows
        return footer.End().ToArray();
    }

    /// <summary>A file of a size that starts with the magic and ends with a footer, its length, and the magic.</summary>
    public static byte[] EndingWith(byte[] footer, int size)
    {
        var file = new byte[size];
        "PAR1"u8.CopyTo(file);
        footer.CopyTo(file, size - 8 - footer.Length);
        Tail(footer.Length).CopyTo(file, size - 8);
        return file;
    }

    /// <summary>The last 8 bytes of a Parquet file whose footer has a length: the length and the magic.</summary>
    public static byte[] Tail(int footerLength)
    {
        var tail = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(tail, footerLength);
        "PAR1"u8.CopyTo(tail.AsSpan(4));
        return tail;
    }
}
