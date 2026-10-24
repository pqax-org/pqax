using Azure.Core;

namespace Pqax.Cli;

/// <summary>
/// What <c>pqax collect</c> takes from its surroundings: the environment variables, the credential, and the HTTP client
/// of OneLake. The defaults are those of the machine; a test or a host replaces them.
/// </summary>
public sealed class CollectHost
{
    /// <summary>The host of the command line: the environment variables of the process, the credentials of Azure.Identity, OneLake.</summary>
    public static CollectHost Default { get; } = new();

    /// <summary>Reads an environment variable. The default is <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    public Func<string, string?> GetVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Builds the credential of an identity instead of <see cref="SignIn.CreateCredential"/>. Null, the default, uses it.</summary>
    public Func<SignInOptions, TokenCredential>? CreateCredential { get; init; }

    /// <summary>The HTTP client of OneLake, which the command does not dispose. Null, the default, creates one.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>The DFS endpoint of OneLake, for <c>--workspace</c>. Null, the default, is the endpoint of OneLake. <c>--adls</c> takes its endpoint from its URL.</summary>
    public Uri? Endpoint { get; init; }
}
