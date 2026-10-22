using System.Text;
using System.Text.Json;
using Azure.Core;

namespace Pqax.Cli;

/// <summary>
/// An access token acquired before, such as the one of a notebook or of a CI job. It cannot be refreshed: it is
/// returned until it expires. When the token is a JWT, its <c>exp</c> claim gives the expiry and its <c>aud</c> claim
/// the audience; the claims are read and never checked against a signature.
/// </summary>
public sealed class AccessTokenCredential : TokenCredential
{
    private readonly string token;

    /// <summary>Creates a credential that returns the token.</summary>
    /// <param name="token">The access token.</param>
    public AccessTokenCredential(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            throw new ArgumentException("The token is empty.", nameof(token));
        }

        this.token = token;
        ExpiresOn = DateTimeOffset.MaxValue;
        if (ReadClaims(token) is { } claims)
        {
            if (claims.TryGetProperty("exp", out JsonElement exp) && exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out long seconds))
            {
                ExpiresOn = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            if (claims.TryGetProperty("aud", out JsonElement aud))
            {
                Audience = aud.ValueKind switch
                {
                    JsonValueKind.String => aud.GetString(),
                    JsonValueKind.Array when aud.GetArrayLength() > 0 && aud[0].ValueKind == JsonValueKind.String => aud[0].GetString(),
                    _ => null,
                };
            }
        }
    }

    /// <summary>When the token expires: the <c>exp</c> claim, or <see cref="DateTimeOffset.MaxValue"/> when the token is not a JWT.</summary>
    public DateTimeOffset ExpiresOn { get; }

    /// <summary>The <c>aud</c> claim of the token, null when the token is not a JWT or has none.</summary>
    public string? Audience { get; }

    /// <inheritdoc/>
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(token, ExpiresOn);

    /// <inheritdoc/>
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(new AccessToken(token, ExpiresOn));

    /// <summary>The claims of a JWT, its second segment, or null when the token is not one.</summary>
    private static JsonElement? ReadClaims(string token)
    {
        string[] segments = token.Split('.');
        if (segments.Length != 3)
        {
            return null;
        }

        try
        {
            string payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (Exception e) when (e is FormatException || e is JsonException || e is ArgumentException)
        {
            return null;
        }
    }
}
