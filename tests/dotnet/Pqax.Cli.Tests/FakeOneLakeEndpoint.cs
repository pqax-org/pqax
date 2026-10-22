using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Pqax.Cli.Tests;

/// <summary>
/// A fake OneLake DFS endpoint in the process: it serves a folder as a workspace, the listing of the filesystem API and
/// the read of a range of a file, or answers every request with one status. It records the Authorization header of each
/// request. Nothing goes on the network. It follows FakeOneLake of the collector tests.
/// </summary>
public sealed class FakeOneLakeEndpoint(string folder, string workspace) : HttpMessageHandler
{
    /// <summary>The endpoint that the command is given; the handler answers for it.</summary>
    public static readonly Uri Endpoint = new("https://onelake.test/");

    public ConcurrentQueue<string?> Authorizations { get; } = new();

    public ConcurrentQueue<string> Urls { get; } = new();

    /// <summary>A status that answers every request instead of the folder, or null.</summary>
    public HttpStatusCode? Status { get; init; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Authorizations.Enqueue(request.Headers.Authorization?.ToString());
        Urls.Enqueue(request.RequestUri!.ToString());
        if (Status is HttpStatusCode status)
        {
            return Task.FromResult(Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = status == HttpStatusCode.Forbidden ? "Forbidden" : "Unauthorized" } }));
        }

        return Task.FromResult(Serve(request));
    }

    private HttpResponseMessage Serve(HttpRequestMessage request)
    {
        Uri uri = request.RequestUri!;
        if (request.Method != HttpMethod.Get)
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length == 0 || segments[0] != workspace)
        {
            return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = new JsonObject { ["code"] = "FilesystemNotFound" } });
        }

        Dictionary<string, string> query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "");
        return segments.Length == 1 && query.GetValueOrDefault("resource") == "filesystem"
            ? List(query)
            : Read(string.Join("/", segments.Skip(1)), request.Headers.Range);
    }

    private HttpResponseMessage List(Dictionary<string, string> query)
    {
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

        bool recursive = query.GetValueOrDefault("recursive") == "true";
        var root = new DirectoryInfo(full);
        var paths = new JsonArray();
        foreach (FileSystemInfo info in (recursive ? root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories) : root.EnumerateFileSystemInfos())
            .OrderBy(info => info.FullName, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(full, info.FullName).Replace('\\', '/');
            var entry = new JsonObject
            {
                ["name"] = (directory.Length == 0 ? "" : directory + "/") + relative,
                ["lastModified"] = info.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture),
            };
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

        return Json(HttpStatusCode.OK, new JsonObject { ["paths"] = paths });
    }

    private HttpResponseMessage Read(string path, RangeHeaderValue? range)
    {
        string full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = new JsonObject { ["code"] = "PathNotFound" } });
        }

        byte[] data = File.ReadAllBytes(full);
        if (range == null)
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
