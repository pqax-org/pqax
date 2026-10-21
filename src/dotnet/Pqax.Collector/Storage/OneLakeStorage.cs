using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pqax.Collector;

/// <summary>
/// A OneLake workspace through its DFS endpoint. The class sends GET requests only, so it cannot change anything.
/// Paths are relative to the workspace: <c>&lt;item&gt;.&lt;Kind&gt;/Tables/...</c>.
/// </summary>
/// <remarks>
/// The token comes from a delegate, so that the library does not depend on Azure.Identity. The delegate is called
/// before every request and is expected to cache the token; it must return an access token for <see cref="Scope"/>.
/// The token is sent in the <c>Authorization</c> header and is never stored or written anywhere.
/// <para>
/// The discovery records <c>abfss://&lt;workspace&gt;@&lt;host&gt;/&lt;item&gt;</c> as the location of a source, with the
/// host of the endpoint and the workspace and item as the caller gave them, and the identifiers <c>workspaceId</c> and
/// <c>itemId</c> for those given as ids.
/// </para>
/// </remarks>
public sealed class OneLakeStorage : IStorage, IStorageStatistics, ILocatedStorage, IRecursiveStorage, IDisposable
{
    /// <summary>The scope of the access token that the token provider must return.</summary>
    public const string Scope = "https://storage.azure.com/.default";

    /// <summary>The DFS endpoint of OneLake.</summary>
    public static readonly Uri DefaultEndpoint = new Uri("https://onelake.dfs.fabric.microsoft.com");

    private const string ApiVersion = "2018-11-09";

    private static readonly IReadOnlyList<StorageEntry> NoEntries = new StorageEntry[0];

    private readonly Func<CancellationToken, Task<string>> tokenProvider;
    private readonly HttpClient http;
    private readonly bool ownsClient;
    private readonly string baseUrl;
    private readonly string authority;
    private readonly int maxRetries;
    private readonly Action<string>? trace;
    private long requests;
    private long bytes;

    /// <summary>Creates a storage for a workspace.</summary>
    /// <param name="workspace">The name or the id of the workspace.</param>
    /// <param name="tokenProvider">Returns an access token for <see cref="Scope"/>. It is called before every request.</param>
    /// <param name="options">The endpoint, the retries, and the HTTP client. Null for the defaults.</param>
    public OneLakeStorage(string workspace, Func<CancellationToken, Task<string>> tokenProvider, OneLakeStorageOptions? options = null)
    {
        if (string.IsNullOrEmpty(workspace))
        {
            throw new ArgumentException("The workspace is missing.", nameof(workspace));
        }

        this.tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        options ??= new OneLakeStorageOptions();
        if (options.MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxRetries, "MaxRetries is negative.");
        }

        Workspace = workspace;
        maxRetries = options.MaxRetries;
        trace = options.Trace;
        if (options.HttpClient != null)
        {
            http = options.HttpClient;
        }
        else
        {
            http = new HttpClient { Timeout = options.Timeout };
            ownsClient = true;
        }

        Uri endpointUri = options.Endpoint ?? DefaultEndpoint;
        baseUrl = endpointUri.ToString().TrimEnd('/') + "/" + Uri.EscapeDataString(workspace);
        authority = endpointUri.Authority;
    }

    /// <summary>The name or the id of the workspace.</summary>
    public string Workspace { get; }

    /// <inheritdoc/>
    public long RequestCount => Interlocked.Read(ref requests);

    /// <inheritdoc/>
    public long BytesRead => Interlocked.Read(ref bytes);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StorageEntry>> ListAsync(string path, CancellationToken cancellation)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        List<StorageEntry>? entries = await ListCoreAsync(path.Trim('/'), false, cancellation).ConfigureAwait(false);
        return entries == null || entries.Count == 0 ? NoEntries : Sorted(entries);
    }

    /// <summary>
    /// Lists the files under a folder at any depth, with one listing of the DFS endpoint with <c>recursive=true</c>, in as
    /// many pages as it takes. The endpoint cannot leave a folder out, so the files of the excluded folders are dropped here.
    /// </summary>
    /// <param name="path">The folder, relative to the workspace.</param>
    /// <param name="excludedFolders">The names of folders directly inside the folder whose files are left out.</param>
    /// <param name="cancellation">Cancels the requests.</param>
    /// <returns>The files, sorted by path. Empty when the folder does not exist.</returns>
    async Task<IReadOnlyList<StorageEntry>> IRecursiveStorage.ListFilesAsync(string path, IReadOnlyCollection<string> excludedFolders, CancellationToken cancellation)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        string directory = path.Trim('/');
        List<StorageEntry>? entries = await ListCoreAsync(directory, true, cancellation).ConfigureAwait(false);
        if (entries == null)
        {
            return NoEntries;
        }

        entries.RemoveAll(e => e.IsDirectory || StorageListing.IsExcluded(directory, e.Path, excludedFolders));
        return StorageListing.Sorted(entries);
    }

    /// <summary>Gets every page of a listing. Null when the folder does not exist.</summary>
    private async Task<List<StorageEntry>?> ListCoreAsync(string directory, bool recursive, CancellationToken cancellation)
    {
        var entries = new List<StorageEntry>();
        string? continuation = null;
        do
        {
            // The root of the workspace is listed without the directory parameter: OneLake answers 400 to an empty one.
            string url = baseUrl + "?resource=filesystem&recursive=" + (recursive ? "true" : "false")
                + (directory.Length == 0 ? string.Empty : "&directory=" + Uri.EscapeDataString(directory));
            if (continuation != null)
            {
                url += "&continuation=" + Uri.EscapeDataString(continuation);
            }

            using HttpResponseMessage response = await SendAsync(url, null, 0, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return entries.Count == 0 ? null : entries;
            }

            EnsureSuccess(response);
            continuation = response.Headers.TryGetValues("x-ms-continuation", out IEnumerable<string>? values)
                ? values.FirstOrDefault(v => !string.IsNullOrEmpty(v))
                : null;
            byte[] body = await ReadBodyAsync(response).ConfigureAwait(false);
            ParseListing(body, entries);
        }
        while (continuation != null);

        return entries;
    }

    /// <inheritdoc/>
    public async Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellation)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset is negative.");
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length is negative.");
        }

        if (length == 0)
        {
            return new byte[0];
        }

        string url = baseUrl + "/" + string.Join("/", path.Trim('/').Split('/').Select(Uri.EscapeDataString));
        using HttpResponseMessage response = await SendAsync(url, offset, length, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return new byte[0];
        }

        EnsureSuccess(response);
        byte[] body = await ReadBodyAsync(response).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            // The whole file came back: keep the range that was asked for.
            if (offset >= body.Length)
            {
                return new byte[0];
            }

            int count = (int)Math.Min(length, body.Length - offset);
            if (offset == 0 && count == body.Length)
            {
                return body;
            }

            var range = new byte[count];
            Buffer.BlockCopy(body, (int)offset, range, 0, count);
            return range;
        }

        if (body.Length > length)
        {
            Array.Resize(ref body, length);
        }

        return body;
    }

    /// <summary>Disposes the HTTP client when the storage created it.</summary>
    public void Dispose()
    {
        if (ownsClient)
        {
            http.Dispose();
        }
    }

    /// <summary>The ABFSS URI of a folder of the workspace, with the workspace as the caller gave it, name or id.</summary>
    string ILocatedStorage.LocationOf(string path)
    {
        string folder = path.Trim('/');
        return "abfss://" + Workspace + "@" + authority + (folder.Length == 0 ? string.Empty : "/" + folder);
    }

    /// <summary>
    /// <c>workspaceId</c> when the workspace was given as an id, and <c>itemId</c> when the item was. A name gives no
    /// identifier: the listing of the DFS endpoint has no id, and the collector does not guess one.
    /// </summary>
    IReadOnlyDictionary<string, string>? ILocatedStorage.IdentifiersOf(string item)
    {
        var identifiers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (IsId(Workspace))
        {
            identifiers["workspaceId"] = Workspace;
        }

        string folder = item.Trim('/');
        if (IsId(folder))
        {
            identifiers["itemId"] = folder;
        }

        return identifiers.Count == 0 ? null : identifiers;
    }

    /// <summary>Whether a workspace or an item is named by its id: a GUID with hyphens, as Fabric writes its ids.</summary>
    private static bool IsId(string value) => Guid.TryParseExact(value, "D", out _);

    private static IReadOnlyList<StorageEntry> Sorted(List<StorageEntry> entries)
    {
        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return entries;
    }

    private static void ParseListing(byte[] body, List<StorageEntry> entries)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("paths", out JsonElement paths)
            || paths.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in paths.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string name = nameElement.GetString() ?? string.Empty;
            bool isDirectory = item.TryGetProperty("isDirectory", out JsonElement directory)
                && (directory.ValueKind == JsonValueKind.True
                    || (directory.ValueKind == JsonValueKind.String && string.Equals(directory.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
            long? size = null;
            if (!isDirectory && item.TryGetProperty("contentLength", out JsonElement length))
            {
                if (length.ValueKind == JsonValueKind.Number && length.TryGetInt64(out long number))
                {
                    size = number;
                }
                else if (length.ValueKind == JsonValueKind.String && long.TryParse(length.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
                {
                    size = parsed;
                }
            }

            DateTimeOffset? modified = null;
            if (item.TryGetProperty("lastModified", out JsonElement lastModified) && lastModified.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(lastModified.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsedTime))
            {
                modified = parsedTime;
            }

            entries.Add(new StorageEntry(name.Trim('/'), isDirectory, size, modified));
        }
    }

    private static bool IsTransient(HttpStatusCode status)
    {
        int code = (int)status;
        return code == 429 || code == 500 || code == 502 || code == 503 || code == 504;
    }

    private static TimeSpan Backoff(int attempt, HttpResponseMessage? response)
    {
        RetryConditionHeaderValue? retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            return delta < TimeSpan.FromMinutes(1) ? delta : TimeSpan.FromMinutes(1);
        }

        if (retryAfter?.Date is DateTimeOffset date)
        {
            TimeSpan wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                return wait < TimeSpan.FromMinutes(1) ? wait : TimeSpan.FromMinutes(1);
            }
        }

        double seconds = Math.Min(30, Math.Pow(2, attempt));
        return TimeSpan.FromSeconds(seconds);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        int code = (int)response.StatusCode;
        throw new StorageException($"OneLake answered {code} {response.ReasonPhrase}{ServiceErrorCode(response)}.", code);
    }

    /// <summary>The error code in the JSON body of a failed answer, as " (Code)", or nothing. Never the message, which can name a path.</summary>
    private static string ServiceErrorCode(HttpResponseMessage response)
    {
        try
        {
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out System.Text.Json.JsonElement error)
                && error.TryGetProperty("code", out System.Text.Json.JsonElement code)
                && code.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return " (" + code.GetString() + ")";
            }
        }
        catch (Exception)
        {
            // No body, or not JSON: the status code is enough.
        }

        return string.Empty;
    }

    private async Task<byte[]> ReadBodyAsync(HttpResponseMessage response)
    {
        byte[] body;
        try
        {
            body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException || e is System.IO.IOException)
        {
            // The connection broke while the answer came in: a failed request, as IStorage says.
            throw new StorageException("The answer of OneLake could not be read: " + e.GetType().Name + ".", null, e);
        }

        Interlocked.Add(ref bytes, body.Length);
        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, long? offset, int length, CancellationToken cancellation)
    {
        for (int attempt = 0; ; attempt++)
        {
            string token = await tokenProvider(cancellation).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("x-ms-version", ApiVersion);
            if (offset != null)
            {
                request.Headers.Range = new RangeHeaderValue(offset.Value, offset.Value + length - 1);
            }

            HttpResponseMessage response;
            try
            {
                // The body is read here too, so the timeout of the client and the cancellation of the caller cover a body that
                // stalls after the headers. Every answer is small enough to buffer: a listing, a log file, a footer tail.
                response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellation).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                Interlocked.Increment(ref requests);
                await Task.Delay(Backoff(attempt, null), cancellation).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException) when (!cancellation.IsCancellationRequested && attempt < maxRetries)
            {
                // A timeout of the client, not a cancellation of the caller.
                Interlocked.Increment(ref requests);
                await Task.Delay(Backoff(attempt, null), cancellation).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException e)
            {
                Interlocked.Increment(ref requests);
                throw new StorageException("A request to OneLake failed: " + e.GetType().Name + ".", null, e);
            }
            catch (TaskCanceledException e) when (!cancellation.IsCancellationRequested)
            {
                // The last attempt timed out: a failed request, as IStorage says, and not a cancellation.
                Interlocked.Increment(ref requests);
                throw new StorageException("A request to OneLake timed out.", null, e);
            }

            Interlocked.Increment(ref requests);
            trace?.Invoke("GET " + url + " -> " + (int)response.StatusCode);
            if (IsTransient(response.StatusCode) && attempt < maxRetries)
            {
                TimeSpan wait = Backoff(attempt, response);
                response.Dispose();
                await Task.Delay(wait, cancellation).ConfigureAwait(false);
                continue;
            }

            return response;
        }
    }
}
