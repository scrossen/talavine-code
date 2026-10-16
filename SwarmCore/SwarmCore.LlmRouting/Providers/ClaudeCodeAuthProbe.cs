using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// Local subscription-session proof, using Claude's authoritative macOS
/// Keychain entry before its fallback credentials file. Only validated
/// auth metadata leaves the probe; account identifiers and tokens never do.
/// </summary>
public static class ClaudeCodeAuthProbe
{
    internal const int MaximumCredentialCharacters = 64 * 1024;

    /// <summary>
    /// Detail returned when the access token has expired but a refresh token
    /// is cached. The local store cannot prove the session; the CLI's own
    /// login-status (or inference) boundary can, because only the CLI renews
    /// the token. Callers that can run that boundary use
    /// <see cref="IsRenewalPending"/> to try it instead of failing closed.
    /// </summary>
    public const string RenewalPendingDetail =
        "Claude Code cached OAuth credentials are expired or invalid until the CLI renews them. Run `claude` to refresh the subscription sign-in.";

    /// <summary>True when a failed probe only needs the CLI to renew an expired access token.</summary>
    public static bool IsRenewalPending(string? detail)
        => string.Equals(detail, RenewalPendingDetail, StringComparison.Ordinal);
    private static readonly TimeSpan KeychainReadTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static bool HasRequiredScope(JsonElement oauth)
        => !oauth.TryGetProperty("scopes", out var scopes)
           || scopes.ValueKind != JsonValueKind.Array
           || scopes.EnumerateArray().Any(item =>
               item.ValueKind == JsonValueKind.String
               && item.GetString()!.Equals("user:sessions:claude_code", StringComparison.OrdinalIgnoreCase));

    public static (bool IsAuthenticated, string Detail) Probe(
        string? configDirectory = null,
        Func<string, string?>? environmentVariableReader = null,
        Func<DateTimeOffset>? utcNow = null)
        => ProbeWithStores(
            configDirectory,
            environmentVariableReader ?? Environment.GetEnvironmentVariable,
            utcNow ?? (static () => DateTimeOffset.UtcNow),
            OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst(),
            ReadKeychainCredential);

    // Inject stores, not credentials into logs or host-facing result objects.
    internal static (bool IsAuthenticated, string Detail) ProbeWithStores(
        string? configDirectory,
        Func<string, string?> environmentVariableReader,
        Func<DateTimeOffset> utcNow,
        bool useMacOsKeychain,
        Func<string, string, string?> keychainCredentialReader)
    {
        // `claude setup-token` requires a Claude subscription and emits this
        // dedicated OAuth variable. It is distinct from Anthropic API auth.
        if (!string.IsNullOrWhiteSpace(environmentVariableReader("CLAUDE_CODE_OAUTH_TOKEN")))
        {
            return (true, "Claude Code has a subscription OAuth setup token.");
        }

        // This newer independent override is not part of the documented
        // config-directory contract. Never substitute the default identity.
        if (environmentVariableReader("CLAUDE_SECURESTORAGE_CONFIG_DIR") is not null)
            return (false, "Claude Code uses an alternate credential store that cannot be verified locally.");

        var configuredDirectory = configDirectory ?? environmentVariableReader("CLAUDE_CONFIG_DIR");
        var directory = string.IsNullOrEmpty(configuredDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configuredDirectory.Normalize(NormalizationForm.FormC);
        var credentialsPath = Path.Combine(directory, ".credentials.json");

        try
        {
            string? credentials = null;
            if (useMacOsKeychain)
            {
                var serviceName = GetKeychainServiceName(configuredDirectory);
                var accountName = environmentVariableReader("USER");
                if (string.IsNullOrEmpty(accountName)) accountName = Environment.UserName;
                credentials = keychainCredentialReader(serviceName, accountName);
            }

            // Null means unavailable. An available but empty/malformed/expired
            // primary store must never be replaced by an older file identity.
            if (credentials is null)
            {
                if (!File.Exists(credentialsPath))
                    return (false, "Claude Code is installed but has no cached subscription OAuth credentials. Run `claude` to sign in.");
                using var reader = File.OpenText(credentialsPath);
                credentials = ReadBoundedAsync(reader, CancellationToken.None).GetAwaiter().GetResult();
            }

            if (credentials.Length > MaximumCredentialCharacters)
                return UnreadableCredentials();
            using var doc = JsonDocument.Parse(credentials, LenientJson);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object)
            {
                return (false,
                    "Claude Code credentials do not contain a subscription OAuth session. Run `claude` to sign in.");
            }

            var subscriptionType = ReadString(oauth, "subscriptionType");
            if (string.IsNullOrWhiteSpace(subscriptionType)
                || subscriptionType.Equals("none", StringComparison.OrdinalIgnoreCase)
                || subscriptionType.Equals("api", StringComparison.OrdinalIgnoreCase)
                || subscriptionType.Equals("console", StringComparison.OrdinalIgnoreCase))
            {
                return (false,
                    "Claude Code does not have a verified subscription session. Run `claude` and sign in with a Claude subscription.");
            }

            var hasRefreshToken = !string.IsNullOrWhiteSpace(ReadString(oauth, "refreshToken"));
            var hasCredential = !string.IsNullOrWhiteSpace(ReadString(oauth, "accessToken"))
                                || hasRefreshToken;
            if (!hasCredential)
            {
                return (false,
                    "Claude Code subscription metadata exists but its cached OAuth credential is missing. Run `claude` to sign in again.");
            }

            // A credential shape is not readiness proof after its access token
            // has expired. Claude Code may be able to renew it with the refresh
            // token, but only an actual CLI status/inference boundary can prove
            // that renewal. Treating a months-old refresh token as Ready is the
            // exact stale-positive failure that kept an automatic Claude route
            // alive while every physical request exited 1.
            if (oauth.TryGetProperty("expiresAt", out var expiresAt))
            {
                if (!TryReadExpiration(expiresAt, out var expiration)
                    || expiration <= utcNow())
                {
                    // Never Ready from local shape alone, but a refresh token
                    // means the CLI may renew it: let a CLI boundary decide.
                    if (hasRefreshToken && HasRequiredScope(oauth))
                        return (false, RenewalPendingDetail);
                    return (false,
                        "Claude Code cached OAuth credentials are expired or invalid. Run `claude` to refresh the subscription sign-in.");
                }
            }

            if (!HasRequiredScope(oauth))
            {
                return (false,
                    "Claude Code OAuth credentials do not grant Claude Code subscription sessions. Run `claude` to sign in again.");
            }

            return (true, "Claude Code has cached subscription OAuth credentials.");
        }
        catch (Exception)
        {
            return UnreadableCredentials();
        }
    }

    internal static string GetKeychainServiceName(string? configuredDirectory)
        => "Claude Code-credentials" + (string.IsNullOrEmpty(configuredDirectory)
            ? string.Empty
            : "-" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(configuredDirectory.Normalize(NormalizationForm.FormC))))[..8]);

    private static (bool IsAuthenticated, string Detail) UnreadableCredentials()
        => (false, "Claude Code credentials could not be read. Run `claude` to repair the subscription sign-in.");

    private static string? ReadKeychainCredential(string serviceName, string accountName)
        => ReadKeychainCredentialAsync(serviceName, accountName).GetAwaiter().GetResult();

    internal static async Task<string?> ReadKeychainCredentialAsync(
        string serviceName,
        string accountName,
        Func<ProcessStartInfo, IChildProcess>? launcher = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo("/usr/bin/security")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var argument in new[] { "find-generic-password", "-a", accountName, "-w", "-s", serviceName })
            startInfo.ArgumentList.Add(argument);

        IChildProcess? process = null;
        var readLimitExceeded = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? KeychainReadTimeout);
        try
        {
            process = (launcher ?? ChildProcessLauncher.Start)(startInfo);
            process.StandardInput.Close();
            void RejectOversizedOutput()
            {
                Interlocked.Exchange(ref readLimitExceeded, 1);
                deadline.Cancel();
            }
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token, RejectOversizedOutput);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token, RejectOversizedOutput);
            try
            {
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token))
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                // Observe failures even when the deadline wins over pipe reads.
                _ = stdout.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                _ = stderr.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            return process.ExitCode == 0 ? stdout.Result : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return readLimitExceeded != 0 ? string.Empty : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return string.Empty; // Present but oversized: do not admit a fallback.
        }
        catch (Exception)
        {
            return null; // Never expose raw command output or exception details.
        }
        finally
        {
            if (process is not null)
            {
                await ChildProcessReaper.KillProcessTreeAndWaitAsync(process, TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);
                if (process.HasExited) process.Dispose();
                else ChildProcessReaper.Track(process);
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader, CancellationToken ct, Action? boundExceeded = null)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) return result.ToString();
            if (result.Length + count > MaximumCredentialCharacters)
            {
                boundExceeded?.Invoke();
                throw new InvalidDataException("Credential metadata exceeded its bounded read limit.");
            }
            result.Append(buffer, 0, count);
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static bool TryReadExpiration(
        JsonElement element,
        out DateTimeOffset expiration)
    {
        expiration = default;
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out var raw))
        {
            return false;
        }

        try
        {
            // Claude currently persists milliseconds. Accept seconds as a
            // defensive compatibility shape, but reject anything unparseable
            // instead of turning malformed metadata into route authority.
            expiration = raw >= 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
                : DateTimeOffset.FromUnixTimeSeconds(raw);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

}
