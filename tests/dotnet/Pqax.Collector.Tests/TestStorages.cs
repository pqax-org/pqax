using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Pqax.Collector.Tests;

/// <summary>One request that reached a storage.</summary>
public sealed record StorageRequest(string Kind, string Path, long Offset, int Length);

/// <summary>
/// A storage over another one that records every request, and can replace the content of a file, make a request fail,
/// or cancel a token after a number of requests. It does not implement IStorageStatistics, so the collector counts.
/// It is not one of the storages that the discovery knows, so it gives tables and sources with no location.
/// </summary>
public sealed class TestStorage(IStorage inner) : IStorage
{
    private readonly object gate = new();
    private readonly List<StorageRequest> requests = [];

    /// <summary>The content to serve for a file instead of the real one, or null to read the real file.</summary>
    public Func<string, byte[]?>? Content { get; init; }

    /// <summary>
    /// The bytes to serve for a range, given the path, the offset, and the length, instead of the real ones, or null to
    /// go on. For a file too large to build, as <see cref="VirtualFileStorage.Slice"/> serves.
    /// </summary>
    public Func<string, long, int, byte[]?>? Range { get; init; }

    /// <summary>The exception that a request throws: given "list" or "read" and the path. Null to let it through.</summary>
    public Func<string, string, Exception?>? Fail { get; init; }

    /// <summary>Cancelled when the request with this number, from 1, arrives.</summary>
    public CancellationTokenSource? CancelSource { get; init; }

    public int CancelAt { get; init; }

    public IReadOnlyList<StorageRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return requests.ToList();
            }
        }
    }

    private void Arrive(string kind, string path, long offset, int length, CancellationToken cancellation)
    {
        int count;
        lock (gate)
        {
            requests.Add(new StorageRequest(kind, path, offset, length));
            count = requests.Count;
        }

        if (CancelSource != null && count == CancelAt)
        {
            CancelSource.Cancel();
        }

        cancellation.ThrowIfCancellationRequested();
        if (Fail?.Invoke(kind, path) is Exception failure)
        {
            throw failure;
        }
    }

    public async Task<IReadOnlyList<StorageEntry>> ListAsync(string path, CancellationToken cancellation)
    {
        Arrive("list", path, 0, 0, cancellation);
        return await inner.ListAsync(path, cancellation);
    }

    public async Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellation)
    {
        Arrive("read", path, offset, length, cancellation);
        if (Range?.Invoke(path, offset, length) is byte[] range)
        {
            return range;
        }

        if (Content?.Invoke(path) is byte[] content)
        {
            if (offset >= content.Length)
            {
                return [];
            }

            return content.AsSpan((int)offset, (int)Math.Min(length, content.Length - offset)).ToArray();
        }

        return await inner.ReadRangeAsync(path, offset, length, cancellation);
    }
}

/// <summary>
/// A storage with one file of any size, all zeros except its last bytes, to test the footer reader on files that
/// would be too large to build.
/// </summary>
public sealed class VirtualFileStorage(long size, byte[] tail) : IStorage
{
    public List<(long Offset, int Length)> Reads { get; } = [];

    public Task<IReadOnlyList<StorageEntry>> ListAsync(string path, CancellationToken cancellation) =>
        Task.FromResult<IReadOnlyList<StorageEntry>>([]);

    public Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellation)
    {
        Reads.Add((offset, length));
        return Task.FromResult(Slice(size, tail, offset, length));
    }

    /// <summary>A range of a file of any size that is all zeros except its last bytes, cut at the end of the file.</summary>
    public static byte[] Slice(long size, byte[] tail, long offset, int length)
    {
        if (offset >= size)
        {
            return [];
        }

        var buffer = new byte[(int)Math.Min(length, size - offset)];
        long tailStart = size - tail.Length;
        for (int i = 0; i < buffer.Length; i++)
        {
            long position = offset + i;
            buffer[i] = position >= tailStart ? tail[position - tailStart] : (byte)0;
        }

        return buffer;
    }
}

/// <summary>One request that reached the fake OneLake endpoint.</summary>
public sealed record HttpCall(string Method, string Host, string PathAndQuery, string? Authorization, string? Version, string? Range);

/// <summary>
/// A fake OneLake DFS endpoint that serves a folder: the listing of the filesystem API, in pages, and the read of a
/// range of a file. It runs in the process, behind the HttpClient that the storage is given, so nothing goes on the
/// network.
/// </summary>
public sealed class FakeOneLake(string folder, string workspace) : HttpMessageHandler
{
    public ConcurrentQueue<HttpCall> Calls { get; } = new();

    /// <summary>The entries of one page of a listing.</summary>
    public int PageSize { get; init; } = 2;

    /// <summary>An answer that replaces the normal one for a request, or null.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; init; }

    /// <summary>Whether a read ignores the range and returns the whole file with 200, as some servers do.</summary>
    public bool IgnoreRange { get; init; }

    /// <summary>
    /// An answer that replaces the normal one and can take its time or fail as the network does: it gets the token that
    /// the client cancels at its timeout, and the exception of its task is the one that the client sees. It comes before
    /// <see cref="Override"/>. Null, or a null answer, to go on.
    /// </summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Answer { get; init; }

    /// <summary>An answer that never comes: it waits until the client gives up, at its timeout or when its caller cancels.</summary>
    public static async Task<HttpResponseMessage?> Hang(HttpRequestMessage request, CancellationToken cancellation)
    {
        await Task.Delay(Timeout.Infinite, cancellation);
        return null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        Calls.Enqueue(new HttpCall(
            request.Method.Method,
            uri.Host,
            uri.PathAndQuery,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("x-ms-version", out IEnumerable<string>? versions) ? versions.FirstOrDefault() : null,
            request.Headers.Range?.ToString()));
        if (Answer != null && await Answer(request, cancellationToken) is HttpResponseMessage answered)
        {
            return answered;
        }

        return Serve(request);
    }

    private HttpResponseMessage Serve(HttpRequestMessage request)
    {
        Uri uri = request.RequestUri!;
        if (Override?.Invoke(request) is HttpResponseMessage replaced)
        {
            return replaced;
        }

        if (request.Method != HttpMethod.Get)
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length == 0 || segments[0] != workspace)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        Dictionary<string, string> query = Query(uri);
        return segments.Length == 1 && query.GetValueOrDefault("resource") == "filesystem"
            ? List(query)
            : Read(string.Join("/", segments.Skip(1)), request.Headers.Range);
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "");

    private HttpResponseMessage List(Dictionary<string, string> query)
    {
        // As the service does: the root of the filesystem is listed without the parameter, and an empty one is refused.
        if (query.TryGetValue("directory", out string? given) && given.Length == 0)
        {
            return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = new JsonObject { ["code"] = "InvalidQueryParameterValue" } });
        }

        string directory = query.GetValueOrDefault("directory", "").Trim('/');
        string full = directory.Length == 0 ? folder : Path.Combine(folder, directory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(full))
        {
            return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = new JsonObject { ["code"] = "PathNotFound" } });
        }

        var entries = new DirectoryInfo(full).EnumerateFileSystemInfos().OrderBy(info => info.Name, StringComparer.Ordinal).ToList();
        int start = int.Parse(query.GetValueOrDefault("continuation", "0"), CultureInfo.InvariantCulture);
        var page = entries.Skip(start).Take(PageSize).ToList();
        var paths = new JsonArray();
        foreach (FileSystemInfo info in page)
        {
            string name = (directory.Length == 0 ? "" : directory + "/") + info.Name;
            var entry = new JsonObject { ["name"] = name, ["lastModified"] = info.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture) };
            if (info is FileInfo file)
            {
                entry["contentLength"] = file.Length.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                entry["isDirectory"] = "true";
                entry["contentLength"] = "0";
            }

            paths.Add(entry);
        }

        HttpResponseMessage response = Json(HttpStatusCode.OK, new JsonObject { ["paths"] = paths });
        if (start + page.Count < entries.Count)
        {
            response.Headers.Add("x-ms-continuation", (start + page.Count).ToString(CultureInfo.InvariantCulture));
        }

        return response;
    }

    private HttpResponseMessage Read(string path, RangeHeaderValue? range)
    {
        string full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = new JsonObject { ["code"] = "PathNotFound" } });
        }

        byte[] data = File.ReadAllBytes(full);
        if (range == null || IgnoreRange)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        }

        RangeItemHeaderValue item = range.Ranges.Single();
        long from = item.From ?? 0;
        if (from >= data.Length)
        {
            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
        }

        long to = Math.Min(item.To ?? data.Length - 1, data.Length - 1);
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data, (int)from, (int)(to - from + 1)) };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}

/// <summary>The body of an answer that breaks while it comes in, as when the connection drops after the headers.</summary>
public sealed class BrokenContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        Task.FromException(new IOException("The connection was closed while the answer came in."));

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

/// <summary>The body of an answer that stops coming after the headers, until the reader cancels its read.</summary>
public sealed class StalledContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.Delay(Timeout.Infinite);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

/// <summary>A progress that keeps every report, in the order received, on the thread that reports.</summary>
public sealed class ListProgress : IProgress<CollectionProgress>
{
    private readonly object gate = new();

    public List<CollectionProgress> Reports { get; } = [];

    public void Report(CollectionProgress value)
    {
        lock (gate)
        {
            Reports.Add(value);
        }
    }
}
