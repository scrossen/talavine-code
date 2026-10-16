using SwarmCore.Secrets;

namespace SwarmCore.Coding;

/// <summary>Validates HTTP launch coordinates without returning credentials in diagnostics.</summary>
public static class McpHttpLaunch
{
    public static string? ValidationProblem(McpServerLaunch launch)
    {
        if (!Uri.TryCreate(launch.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            return "Use an absolute HTTP(S) endpoint without embedded credentials or a fragment.";
        var decoded = Uri.UnescapeDataString(launch.Url!);
        if (SwarmCore.Abstractions.Security.CredentialPatterns.Scrub(decoded, "[redacted]") != decoded)
            return "Keep credentials out of the endpoint URL; use a bearer credential reference.";
        if (!string.IsNullOrWhiteSpace(launch.Command)) return "Choose an HTTP endpoint or a stdio command.";
        if (launch.BearerTokenReference is { } reference
            && (SecretReference.Pattern.Match(reference) is not { Success: true } match || match.Value != reference))
            return "Bearer authentication must be a single {{secret:name}} reference.";
        return null;
    }

    public static async Task<string?> ResolveBearerAsync(McpServerLaunch launch, ISecretVault? vault, CancellationToken ct)
    {
        if (ValidationProblem(launch) is { } problem) throw new InvalidOperationException(problem);
        if (launch.BearerTokenReference is not { } reference) return null;
        var name = SecretReference.Names(reference).Single();
        var value = vault is null ? null : await vault.GetValueAsync(name, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("The HTTP server's credential is unavailable in this host's vault.");
        return value;
    }
}
