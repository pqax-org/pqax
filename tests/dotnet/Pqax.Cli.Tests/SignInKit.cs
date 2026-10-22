using System.Diagnostics;
using System.Text;
using Azure.Core;

namespace Pqax.Cli.Tests;

/// <summary>
/// What the tests of the sign-in share: the command line in a process of its own with extra environment variables, the
/// parsing of a command line, synthetic identifiers, and a fake credential.
/// </summary>
public static class SignInKit
{
    /// <summary>A synthetic tenant id.</summary>
    public const string Tenant = "00000000-1111-2222-3333-444444444444";

    /// <summary>A synthetic client id.</summary>
    public const string ClientId = "55555555-6666-7777-8888-999999999999";

    /// <summary>The options of a workspace collection, before the identity options.</summary>
    public static string[] Workspace(params string[] identity) =>
        ["collect", "--workspace", "Test Workspace", "--item", "Item.Lakehouse", "--output", Path.Combine(Path.GetTempPath(), "pqax-signin-unused.pqax"), .. identity];

    /// <summary>Parses a command line that must be valid, and returns its sign-in options.</summary>
    public static SignInOptions Valid(params string[] identity)
    {
        CollectOptions? options = CollectOptions.Parse(Workspace(identity), out string? problem);
        Assert.True(options != null, $"{string.Join(' ', identity)} was refused: {problem}");
        Assert.Null(problem);
        return options!.SignIn;
    }

    /// <summary>Parses a command line that must be refused, and returns the problem.</summary>
    public static string Refused(params string[] arguments)
    {
        CollectOptions? options = CollectOptions.Parse(arguments, out string? problem);
        Assert.True(options == null, $"{string.Join(' ', arguments)} was accepted");
        Assert.False(string.IsNullOrWhiteSpace(problem), $"{string.Join(' ', arguments)} was refused without a problem");
        return problem!;
    }

    /// <summary>Runs <c>dotnet Pqax.Cli.dll</c> as <see cref="CliKit.Run"/> does, with environment variables set or, for null, removed.</summary>
    public static (int ExitCode, string Output, string Error) Run(IReadOnlyDictionary<string, string?> environment, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            WorkingDirectory = CliKit.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(CliKit.CliAssembly);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach ((string name, string? value) in environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"pqax {string.Join(' ', arguments)} did not end");
        }

        return (process.ExitCode, output.Result, error.Result);
    }

    /// <summary>A JWT with these claims, unsigned: what a test needs to read its expiry and audience.</summary>
    public static string Jwt(object claims)
    {
        static string Encode(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + Encode(System.Text.Json.JsonSerializer.Serialize(claims)) + ".";
    }
}

/// <summary>A credential that returns tokens from a function, counts its calls, and records the scopes asked for.</summary>
public sealed class FakeCredential : TokenCredential
{
    private readonly Func<int, AccessToken> token;
    private int calls;

    public FakeCredential(Func<int, AccessToken> token)
    {
        this.token = token;
    }

    public FakeCredential(string token)
        : this(_ => new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)))
    {
    }

    /// <summary>When set, a call waits for this task before it answers.</summary>
    public Task? Gate { get; set; }

    public int Calls => Volatile.Read(ref calls);

    public List<string[]> Scopes { get; } = [];

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        int call = Interlocked.Increment(ref calls);
        lock (Scopes)
        {
            Scopes.Add(requestContext.Scopes);
        }

        if (Gate != null)
        {
            await Gate.WaitAsync(cancellationToken);
        }

        return token(call);
    }
}
