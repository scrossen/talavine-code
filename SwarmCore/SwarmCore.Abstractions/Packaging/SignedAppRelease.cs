using System.Text;
using System.Runtime.InteropServices;

namespace SwarmCore.Abstractions.Packaging;

/// <summary>
/// Shared, publisher-signed release manifest contract for Swarm family desktop apps.
/// The verifying key is compiled into each app; a website response is never a trust anchor.
/// </summary>
public sealed record SignedAppReleaseManifest
{
    public string AppName { get; init; } = string.Empty;
    public string Channel { get; init; } = "preview";
    public string Version { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public DateTime PublishedAtUtc { get; init; }
    public string WindowsInstallScriptUrl { get; init; } = string.Empty;
    public string WindowsInstallScriptSha256 { get; init; } = string.Empty;
    public string MacInstallScriptUrl { get; init; } = string.Empty;
    public string MacInstallScriptSha256 { get; init; } = string.Empty;
    public SignedAppReleaseAsset Windows { get; init; } = new();
    public SignedAppReleaseAsset WindowsArm64 { get; init; } = new();
    public SignedAppReleaseAsset Mac { get; init; } = new();
    public string Signature { get; init; } = string.Empty;
    public string ArchitectureSignature { get; init; } = string.Empty;
}

public sealed record SignedAppReleaseAsset
{
    public string Version { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string PackageType { get; init; } = string.Empty;
}

public enum SignedAppReleasePlatform
{
    Windows,
    WindowsArm64,
    Mac
}

public sealed record SignedAppReleaseInstallPlan(
    Uri ScriptUrl,
    string ScriptSha256,
    SignedAppReleaseAsset Asset);

/// <summary>
/// The swarmmarshal.com release-signing public key. The corresponding private key is available
/// only to release tooling through TALAVINE_RELEASE_PRIVATE_KEY_PEM.
/// </summary>
public static class SwarmFamilyReleaseTrust
{
    public const string PublisherPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIDAk61AZ3hMo2Ro7SQcocSIwoTNx
2s1JuZnSzlN8ZXAB0Hqb7iA+qxEhb/XT0VMsuNL5ivlPFfaHrK5wJehybg==
-----END PUBLIC KEY-----
""";
}

/// <summary>
/// Canonical, domain-separated signing payload and fail-closed validation for installer updates.
/// </summary>
public static class SignedAppReleaseSigning
{
    public const string PayloadFormatId = "talavine.app-release.v1";
    public const string ArchitecturePayloadFormatId = "talavine.app-release.v2";

    public static SignedAppReleasePlatform ResolveWindowsPlatform(Architecture processArchitecture)
        => processArchitecture switch
        {
            Architecture.X64 => SignedAppReleasePlatform.Windows,
            Architecture.Arm64 => SignedAppReleasePlatform.WindowsArm64,
            _ => throw new PlatformNotSupportedException(
                $"Windows updates are published only for x64 and ARM64 processes, not {processArchitecture}.")
        };

    public static string BuildSignedPayload(SignedAppReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var builder = new StringBuilder(PayloadFormatId);
        Append(builder, "appName", manifest.AppName);
        Append(builder, "channel", manifest.Channel);
        Append(builder, "version", manifest.Version);
        Append(builder, "windowsInstallScriptUrl", manifest.WindowsInstallScriptUrl);
        Append(builder, "windowsInstallScriptSha256", manifest.WindowsInstallScriptSha256);
        Append(builder, "macInstallScriptUrl", manifest.MacInstallScriptUrl);
        Append(builder, "macInstallScriptSha256", manifest.MacInstallScriptSha256);
        AppendAsset(builder, "windows", manifest.Windows);
        AppendAsset(builder, "mac", manifest.Mac);
        return builder.ToString();
    }

    public static string Sign(SignedAppReleaseManifest manifest, string privateKeyPem)
        => PublisherSigning.Sign(BuildSignedPayload(manifest), privateKeyPem);

    public static string BuildArchitectureSignedPayload(SignedAppReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var builder = new StringBuilder(ArchitecturePayloadFormatId);
        Append(builder, "appName", manifest.AppName);
        Append(builder, "channel", manifest.Channel);
        Append(builder, "version", manifest.Version);
        Append(builder, "windowsInstallScriptUrl", manifest.WindowsInstallScriptUrl);
        Append(builder, "windowsInstallScriptSha256", manifest.WindowsInstallScriptSha256);
        Append(builder, "macInstallScriptUrl", manifest.MacInstallScriptUrl);
        Append(builder, "macInstallScriptSha256", manifest.MacInstallScriptSha256);
        AppendAsset(builder, "windows", manifest.Windows);
        AppendAsset(builder, "windowsArm64", manifest.WindowsArm64);
        AppendAsset(builder, "mac", manifest.Mac);
        return builder.ToString();
    }

    public static string SignArchitecture(SignedAppReleaseManifest manifest, string privateKeyPem)
        => PublisherSigning.Sign(BuildArchitectureSignedPayload(manifest), privateKeyPem);

    public static bool Verify(SignedAppReleaseManifest manifest, string publicKeyPem)
        => manifest is not null
           && !string.IsNullOrWhiteSpace(manifest.Signature)
           && PublisherSigning.Verify(BuildSignedPayload(manifest), manifest.Signature.Trim(), publicKeyPem);

    public static bool VerifyArchitecture(SignedAppReleaseManifest manifest, string publicKeyPem)
        => manifest is not null
           && !string.IsNullOrWhiteSpace(manifest.ArchitectureSignature)
           && PublisherSigning.Verify(
               BuildArchitectureSignedPayload(manifest),
               manifest.ArchitectureSignature.Trim(),
               publicKeyPem);

    public static SignedAppReleaseInstallPlan? TryBuildInstallPlan(
        SignedAppReleaseManifest manifest,
        SignedAppReleasePlatform platform,
        string publicKeyPem,
        out string error)
    {
        error = string.Empty;
        var requiresArchitectureSignature = platform == SignedAppReleasePlatform.WindowsArm64;
        var verified = requiresArchitectureSignature
            ? VerifyArchitecture(manifest, publicKeyPem)
            : Verify(manifest, publicKeyPem);
        if (!verified)
        {
            var signature = requiresArchitectureSignature ? manifest?.ArchitectureSignature : manifest?.Signature;
            error = string.IsNullOrWhiteSpace(signature)
                ? "This release manifest is not signed by the Swarm family release key, so the update was not started."
                : "This release manifest's publisher signature didn't verify. It may have been tampered with — not installing.";
            return null;
        }

        var (scriptUrl, scriptSha, asset) = platform switch
        {
            SignedAppReleasePlatform.Windows => (manifest.WindowsInstallScriptUrl, manifest.WindowsInstallScriptSha256, manifest.Windows),
            SignedAppReleasePlatform.WindowsArm64 => (manifest.WindowsInstallScriptUrl, manifest.WindowsInstallScriptSha256, manifest.WindowsArm64),
            SignedAppReleasePlatform.Mac => (manifest.MacInstallScriptUrl, manifest.MacInstallScriptSha256, manifest.Mac),
            _ => (string.Empty, string.Empty, new SignedAppReleaseAsset())
        };
        if (!TryResolveHttpsOrLoopback(scriptUrl, out var resolved))
        {
            error = "The release manifest's installer script address isn't a valid https URL.";
            return null;
        }
        if (!LooksLikeSha256(scriptSha))
        {
            error = "The release manifest does not include a checksum for this platform's installer script.";
            return null;
        }
        if (!TryResolveHttpsOrLoopback(asset.DownloadUrl, out _) || !LooksLikeSha256(asset.Sha256))
        {
            error = "The release manifest does not include a valid, checksummed download for this platform.";
            return null;
        }

        return new SignedAppReleaseInstallPlan(resolved, scriptSha.Trim().ToLowerInvariant(), asset);
    }

    public static bool LooksLikeSha256(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length == 64 && value.Trim().All(Uri.IsHexDigit);

    private static bool TryResolveHttpsOrLoopback(string? url, out Uri resolved)
    {
        resolved = null!;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var candidate)) return false;
        if (candidate.Scheme == Uri.UriSchemeHttps || (candidate.Scheme == Uri.UriSchemeHttp && candidate.IsLoopback))
        {
            resolved = candidate;
            return true;
        }
        return false;
    }

    private static void Append(StringBuilder builder, string name, string? value)
        => builder.Append('\n').Append(name).Append('=').Append(value?.Trim() ?? string.Empty);

    private static void AppendAsset(StringBuilder builder, string prefix, SignedAppReleaseAsset asset)
    {
        asset ??= new SignedAppReleaseAsset();
        Append(builder, $"{prefix}.version", asset.Version);
        Append(builder, $"{prefix}.downloadUrl", asset.DownloadUrl);
        Append(builder, $"{prefix}.fileName", asset.FileName);
        Append(builder, $"{prefix}.sha256", asset.Sha256);
        Append(builder, $"{prefix}.packageType", asset.PackageType);
    }
}
