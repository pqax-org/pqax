using Azure.Core;
using Pqax.Collector;

namespace Pqax.Cli.Tests;

/// <summary>
/// <see cref="TokenCache"/> with a fake credential and a fake clock: one request while the token is valid, a new one
/// within five minutes of its expiry, one request for concurrent callers, and the scope of OneLake.
/// </summary>
public class TokenCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = Start;
    }

    [Fact]
    public async Task A_valid_token_is_requested_once()
    {
        var clock = new Clock();
        var credential = new FakeCredential(call => new AccessToken("token-" + call, Start.AddHours(1)));
        var cache = new TokenCache(credential, () => clock.Now);
        Assert.Equal("token-1", await cache.GetAsync(CancellationToken.None));
        Assert.Equal("token-1", await cache.GetAsync(CancellationToken.None));
        clock.Now = Start.AddMinutes(30);
        Assert.Equal("token-1", await cache.GetAsync(CancellationToken.None));
        clock.Now = Start.AddMinutes(54);
        Assert.Equal("token-1", await cache.GetAsync(CancellationToken.None));
        Assert.Equal(1, credential.Calls);
    }

    [Theory]
    [InlineData(56)]
    [InlineData(59)]
    [InlineData(61)]
    [InlineData(600)]
    public async Task A_token_within_five_minutes_of_its_expiry_is_requested_again(int minutes)
    {
        var clock = new Clock();
        var credential = new FakeCredential(call => new AccessToken("token-" + call, clock.Now.AddHours(1)));
        var cache = new TokenCache(credential, () => clock.Now);
        Assert.Equal("token-1", await cache.GetAsync(CancellationToken.None));
        clock.Now = Start.AddMinutes(minutes);
        Assert.Equal("token-2", await cache.GetAsync(CancellationToken.None));
        Assert.Equal("token-2", await cache.GetAsync(CancellationToken.None));
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task A_token_that_expires_in_less_than_five_minutes_is_not_kept()
    {
        var clock = new Clock();
        var credential = new FakeCredential(call => new AccessToken("token-" + call, clock.Now.AddMinutes(2)));
        var cache = new TokenCache(credential, () => clock.Now);
        await cache.GetAsync(CancellationToken.None);
        await cache.GetAsync(CancellationToken.None);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task A_token_without_expiry_is_kept()
    {
        var clock = new Clock();
        var credential = new FakeCredential(call => new AccessToken("token-" + call, DateTimeOffset.MaxValue));
        var cache = new TokenCache(credential, () => clock.Now);
        await cache.GetAsync(CancellationToken.None);
        clock.Now = Start.AddDays(400);
        Assert.Equal("token-1", await cache.GetAsync(CancellationToken.None));
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_request()
    {
        var clock = new Clock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var credential = new FakeCredential(call => new AccessToken("token-" + call, Start.AddHours(1))) { Gate = gate.Task };
        var cache = new TokenCache(credential, () => clock.Now);
        Task<string>[] callers = Enumerable.Range(0, 16).Select(_ => Task.Run(() => cache.GetAsync(CancellationToken.None))).ToArray();
        await Task.Delay(200);
        gate.SetResult();
        string[] tokens = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(tokens, token => Assert.Equal("token-1", token));
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public async Task The_scope_is_the_one_of_onelake()
    {
        var credential = new FakeCredential("token");
        var cache = new TokenCache(credential);
        Assert.Equal("token", await cache.GetAsync(CancellationToken.None));
        string[] scopes = Assert.Single(credential.Scopes);
        Assert.Equal([OneLakeStorage.Scope], scopes);
        Assert.Equal("https://storage.azure.com/.default", OneLakeStorage.Scope);
    }

    [Fact]
    public async Task A_failure_of_the_credential_reaches_the_caller_and_a_later_call_tries_again()
    {
        var clock = new Clock();
        var credential = new FakeCredential(call => call == 1 ? throw new InvalidOperationException("synthetic failure") : new AccessToken("token-" + call, Start.AddHours(1)));
        var cache = new TokenCache(credential, () => clock.Now);
        await Assert.ThrowsAnyAsync<Exception>(() => cache.GetAsync(CancellationToken.None));
        Assert.Equal("token-2", await cache.GetAsync(CancellationToken.None));
    }
}
