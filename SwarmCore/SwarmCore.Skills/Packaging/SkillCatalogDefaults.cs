using System.Text.Json.Serialization;
using SwarmCore.Abstractions.Packaging;

namespace SwarmCore.Skills.Packaging;

/// <summary>
/// The signature document shipped inside a first-party skill package as <c>SIGNATURE.json</c>.
/// Minted offline by release tooling; the web host never signs anything, so a compromised host
/// can serve stale packages but cannot forge a verified one.
/// </summary>
public sealed class SkillPackageSignatureFile
{
    public const string FileName = "SIGNATURE.json";

    [JsonPropertyName("format")] public string Format { get; set; } = "swarm.skill.signature";
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    /// <summary>Skill name the signature covers. Must match the manifest and the directory.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Base64 signature over the package's canonical digest.</summary>
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";

    /// <summary>Hex SHA-256 of the canonical digest, for display and cheap comparison.</summary>
    [JsonPropertyName("digestSha256")] public string DigestSha256 { get; set; } = "";

    /// <summary>Who signed it, for display in the import review.</summary>
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = "Talavine";
}

/// <summary>Well-known defaults for the swarmmarshal.com first-party skill catalog.</summary>
public static class SkillCatalogDefaults
{
    public const string CatalogUrl = "https://swarmmarshal.com/skills/catalog";

    /// <summary>
    /// The publisher key skill packages are verified against. This is the same offline key the
    /// vibe catalog and installer updates use — one publisher, one key, deliberately pinned in
    /// the app so rotating it requires shipping an update rather than changing a web host.
    /// Referenced rather than copied so a rotation is a single edit.
    /// </summary>
    public const string PublisherPublicKeyPem = SwarmFamilyReleaseTrust.PublisherPublicKeyPem;
}
