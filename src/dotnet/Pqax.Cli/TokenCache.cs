using Azure.Core;
using Pqax.Collector;

namespace Pqax.Cli;

/// <summary>
/// Caches the access token of <see cref="OneLakeStorage.Scope"/> until five minutes before it expires. One request for
/// a token at a time: the callers that arrive meanwhile wait for it and share it. <see cref="GetAsync"/> is the token
/// delegate of <see cref="OneLakeStorage"/>.
/// </summary>
public sealed class TokenCache
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly TokenCredential credential;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private AccessToken? token;

    /// <summary>Creates a cache over a credential.</summary>
    /// <param name="credential">Where the tokens come from.</param>
    /// <param name="clock">The current time; null for the clock of the machine. A test passes its own.</param>
    public TokenCache(TokenCredential credential, Func<DateTimeOffset>? clock = null)
    {
        this.credential = credential ?? throw new ArgumentNullException(nameof(credential));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The token: the cached one, or a new one when there is none or it expires within five minutes.</summary>
    /// <param name="cancellation">Cancels the wait and the request.</param>
    /// <returns>The access token, for the <c>Authorization</c> header.</returns>
    public async Task<string> GetAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (token is not { } current || current.ExpiresOn < clock() + Margin)
            {
                token = current = await credential.GetTokenAsync(new TokenRequestContext([OneLakeStorage.Scope]), cancellation).ConfigureAwait(false);
            }

            return current.Token;
        }
        finally
        {
            gate.Release();
        }
    }
}
