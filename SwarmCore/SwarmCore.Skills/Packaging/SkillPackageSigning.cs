using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmCore.Abstractions.Packaging;

namespace SwarmCore.Skills.Packaging;

/// <summary>
/// A file inside a skill package, with the hash of its exact bytes.
/// </summary>
/// <param name="RelativePath">Forward-slashed path relative to the package root.</param>
/// <param name="Sha256Hex">Hex SHA-256 of the file's bytes.</param>
public sealed record SkillPackageFileDigest(
    [property: JsonPropertyName("path")] string RelativePath,
    [property: JsonPropertyName("sha256")] string Sha256Hex);

/// <summary>
/// The canonical description of a skill package's contents. This — not the zip — is what gets
/// signed, so re-zipping with a different compression level or timestamp does not invalidate
/// a signature, while changing any file's bytes does.
/// </summary>
public sealed class SkillPackageDigest
{
    [JsonPropertyName("format")] public string Format { get; set; } = "swarm.skill.package";
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    /// <summary>Skill name from the manifest, which is also its directory name.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Every file in the package, ordered by path.</summary>
    [JsonPropertyName("files")] public List<SkillPackageFileDigest> Files { get; set; } = [];
}

/// <summary>
/// Signs and verifies first-party skill packages, mirroring how <c>.vibe</c> packages are
/// handled: signatures are minted offline by release tooling against a key that never reaches
/// the web host, and apps verify against an embedded public key.
///
/// Without this, a skill package downloaded from the catalog is trusted purely because of
/// where it came from — a compromised or impersonated host could serve a modified SKILL.md and
/// the importer would have no way to tell. Signing bounds the damage to serving stale content.
/// </summary>
public static class SkillPackageSigning
{
    /// <summary>Files that describe the download rather than the skill, excluded from the digest.</summary>
    private static readonly string[] ExcludedFileNames = ["SIGNATURE.json", ".DS_Store", "Thumbs.db"];

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Generate a publisher key pair. Keep the private key out of source control.</summary>
    public static (string PrivateKeyPem, string PublicKeyPem) GenerateKeyPair()
        => PublisherSigning.GenerateKeyPair();

    /// <summary>
    /// Build the canonical digest for a package directory. Ordering is by ordinal path so the
    /// same directory always produces byte-identical JSON regardless of filesystem enumeration
    /// order — otherwise a signature minted on one machine would fail to verify on another.
    /// </summary>
    public static SkillPackageDigest ComputeDigest(string packageDirectory, string skillName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);

        var root = Path.GetFullPath(packageDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Skill package directory not found: {root}");

        var files = new List<SkillPackageFileDigest>();

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (ExcludedFileNames.Contains(info.Name, StringComparer.OrdinalIgnoreCase)) continue;

            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            files.Add(new SkillPackageFileDigest(relative, HashFile(path)));
        }

        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        return new SkillPackageDigest { Name = skillName, Files = files };
    }

    /// <summary>Serialize a digest to the exact bytes that get signed and verified.</summary>
    public static string Canonicalize(SkillPackageDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        return JsonSerializer.Serialize(digest, CanonicalJson);
    }

    /// <summary>Sign a package directory. Returns a base64 signature over its canonical digest.</summary>
    public static string Sign(string packageDirectory, string skillName, string privateKeyPem)
        => PublisherSigning.Sign(Canonicalize(ComputeDigest(packageDirectory, skillName)), privateKeyPem);

    /// <summary>
    /// Verify that a package on disk matches a signature. Fails closed: a malformed key,
    /// a malformed signature, an added file, a removed file, or one changed byte all return false.
    /// </summary>
    public static bool Verify(
        string packageDirectory,
        string skillName,
        string signatureBase64,
        string publisherPublicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64) || string.IsNullOrWhiteSpace(publisherPublicKeyPem))
            return false;

        try
        {
            var canonical = Canonicalize(ComputeDigest(packageDirectory, skillName));
            return PublisherSigning.Verify(canonical, signatureBase64, publisherPublicKeyPem);
        }
        catch
        {
            // Unreadable package, missing directory, permission error — all mean "not verified".
            return false;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Hex SHA-256 over the exact UTF-8 bytes of a document.</summary>
    public static string Sha256Hex(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
