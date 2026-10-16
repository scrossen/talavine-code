using System.Net.Http.Headers;
using System.Text;

namespace SwarmCore.Coding.Forge;

/// <summary>An HTTP status and body from the GitHub REST API (already scrubbed of the credential).</summary>
public sealed record GitHubApiResponse(int StatusCode, string Body)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>Raised when a GitHub call fails; the message is safe for the model (no token, no user info).</summary>
public sealed class GitHubForgeException(string message, int statusCode = 0) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Sends one GitHub REST request for a repository. Two implementations: <see cref="GhCliApiTransport"/> (the signed-in
/// gh CLI, <c>gh api</c>; SwarmCode never sees its token) and <see cref="HttpTokenApiTransport"/> (a vault token over
/// HttpClient). <paramref name="path"/> is relative to the REST root (<c>repos/o/r/pulls?state=open</c>).
/// </summary>
public interface IGitHubApiTransport
{
    Task<GitHubApiResponse> SendAsync(ForgeRepository repository, HttpMethod method, string path, string? jsonBody, CancellationToken ct);
}

/// <summary>Creates the transport for a resolved credential. Tests replace it with recorded responses.</summary>
public interface IGitHubApiTransportFactory
{
    IGitHubApiTransport Create(GitHubCredential credential, string workingDirectory);
}

public sealed class GitHubApiTransportFactory(IForgeProcessRunner runner, Microsoft.Extensions.Options.IOptions<GitHubForgeOptions> options, IHttpClientFactory? httpClientFactory = null)
    : IGitHubApiTransportFactory
{
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient());

    public IGitHubApiTransport Create(GitHubCredential credential, string workingDirectory)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.ApiTimeoutSeconds, 5, 600));
        return credential.Kind == GitHubCredentialKind.GhCli
            ? new GhCliApiTransport(runner, credential.GhPath!, workingDirectory, timeout)
            : new HttpTokenApiTransport(httpClientFactory?.CreateClient("SwarmCore.GitHubForge") ?? SharedClient.Value, credential, timeout);
    }
}

/// <summary>
/// REST through <c>gh api --hostname &lt;host&gt; -i</c>: the request body goes on stdin (<c>--input -</c>), the status
/// line and headers come back before the body. gh supplies its own stored credential.
/// </summary>
public sealed class GhCliApiTransport(IForgeProcessRunner runner, string ghPath, string workingDirectory, TimeSpan timeout) : IGitHubApiTransport
{
    /// <summary>The gh arguments for one request (exposed for tests; no credential is ever among them).</summary>
    public static IReadOnlyList<string> Arguments(ForgeRepository repository, HttpMethod method, string path, bool hasBody)
    {
        var args = new List<string>
        {
            "api", "--hostname", repository.Host, "-X", method.Method.ToUpperInvariant(), "-i",
            "-H", "Accept: application/vnd.github+json", "-H", "X-GitHub-Api-Version: 2022-11-28",
        };
        if (hasBody) { args.Add("--input"); args.Add("-"); }
        args.Add(path.TrimStart('/'));
        return args;
    }

    public async Task<GitHubApiResponse> SendAsync(ForgeRepository repository, HttpMethod method, string path, string? jsonBody, CancellationToken ct)
    {
        var result = await runner.RunAsync(new ForgeProcessSpec(ghPath, Arguments(repository, method, path, jsonBody is not null),
            workingDirectory, GhCli.Environment, timeout, jsonBody), ct).ConfigureAwait(false);
        if (TryParseIncluded(result.Stdout, out var status, out var body)) return new GitHubApiResponse(status, body);
        var error = (string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr).Trim();
        throw new GitHubForgeException("gh api failed: " + GitRemoteUrl.RedactUserInfo(error.Length == 0 ? $"exit code {result.ExitCode}" : error));
    }

    /// <summary>Splits <c>gh api -i</c> output into the HTTP status code and the body.</summary>
    public static bool TryParseIncluded(string output, out int statusCode, out string body)
    {
        statusCode = 0;
        body = "";
        if (string.IsNullOrEmpty(output) || !output.StartsWith("HTTP/", StringComparison.Ordinal)) return false;
        var firstLineEnd = output.IndexOf('\n');
        var statusLine = (firstLineEnd < 0 ? output : output[..firstLineEnd]).Trim();
        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out statusCode)) return false;
        var crlf = output.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var lf = output.IndexOf("\n\n", StringComparison.Ordinal);
        int start;
        if (crlf >= 0 && (lf < 0 || crlf < lf)) start = crlf + 4;
        else if (lf >= 0) start = lf + 2;
        else start = output.Length;
        body = output[start..];
        return true;
    }
}

/// <summary>REST over HttpClient with a vault token (Bearer). The token is scrubbed from every body and error.</summary>
public sealed class HttpTokenApiTransport(HttpClient http, GitHubCredential credential, TimeSpan timeout) : IGitHubApiTransport
{
    public async Task<GitHubApiResponse> SendAsync(ForgeRepository repository, HttpMethod method, string path, string? jsonBody, CancellationToken ct)
    {
        if (!repository.Host.Equals(credential.Host, StringComparison.OrdinalIgnoreCase))
            throw new GitHubForgeException($"The credential is for {credential.Host}, not {repository.Host}; nothing was sent.");
        using var request = new HttpRequestMessage(method, new Uri(repository.ApiBaseUrl, path.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SwarmCore-Coding", "1.0"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return new GitHubApiResponse((int)response.StatusCode, credential.Scrub(body));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitHubForgeException($"GitHub did not answer within {timeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException e)
        {
            throw new GitHubForgeException("GitHub could not be reached: " + credential.Scrub(e.Message));
        }
    }
}
