using System;
using System.Net.Http;

namespace Pqax.Collector;

/// <summary>How a <see cref="OneLakeStorage"/> connects.</summary>
public sealed class OneLakeStorageOptions
{
    /// <summary>
    /// The DFS endpoint. The default is <see cref="OneLakeStorage.DefaultEndpoint"/>; a regional OneLake endpoint, or the DFS
    /// endpoint of a storage account with hierarchical namespace, such as <c>https://&lt;account&gt;.dfs.core.windows.net</c>.
    /// </summary>
    public Uri Endpoint { get; set; } = OneLakeStorage.DefaultEndpoint;

    /// <summary>
    /// How many times a request is repeated after a throttling or transient answer (429, 500, 502, 503, 504) or a
    /// network failure, with a growing delay or the one that <c>Retry-After</c> asks for. The default is 4.
    /// </summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>
    /// The client to send the requests with. When null, the storage creates one and disposes it with itself. A client
    /// given here is not disposed.
    /// </summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>The timeout of one request when the storage creates its own client. The default is 5 minutes.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Receives one line for each answer of the service: the method, the URL, and the status code, never a header or a
    /// body. The URL names the items and the tables, so a trace is for a local diagnosis. Null, the default, traces nothing.
    /// </summary>
    public Action<string>? Trace { get; set; }
}
