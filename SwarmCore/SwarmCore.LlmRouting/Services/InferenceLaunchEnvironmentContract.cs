using System.Collections;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Opt-in physical child contract. No credentials are read, hashed, logged or
/// materialized. Auth readiness is supplied by a separate sanitized host receipt.
/// </summary>
public static class InferenceLaunchEnvironmentContract
{
    private static readonly HashSet<string> OsEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemRoot", "WINDIR", "PATH", "PATHEXT", "TEMP", "TMP", "TMPDIR", "LANG", "LC_ALL", "LC_CTYPE", "TZ",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "ProgramFiles", "ProgramFiles(x86)",
    };
    private static readonly string[] HomeEnvironment = ["HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA"];

    public static IReadOnlyDictionary<string, string?> Capture(InferenceAdmissionEvidence evidence)
    {
        var snapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string name && OsEnvironment.Contains(name)) snapshot[name] = entry.Value?.ToString();
        foreach (var name in HomeEnvironment) snapshot[name] = evidence.CliHomePath;
        snapshot[evidence.CliHomeEnvironmentVariable] = evidence.CliHomePath;
        return snapshot.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public static void ValidateSnapshot(InferenceAdmissionEvidence evidence, IReadOnlyDictionary<string, string?>? snapshot)
    {
        if (snapshot is null || snapshot.Any(pair => !OsEnvironment.Contains(pair.Key)
            && !HomeEnvironment.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)
            && pair.Key != evidence.CliHomeEnvironmentVariable)
            || HomeEnvironment.Any(name => !snapshot.TryGetValue(name, out var value) || !SamePath(value ?? "", evidence.CliHomePath))
            || !snapshot.TryGetValue(evidence.CliHomeEnvironmentVariable, out var home) || !SamePath(home ?? "", evidence.CliHomePath))
            throw Denied("The physical child environment has no exact sanitized owned-home snapshot.");
    }

    public static void Validate(InferenceAdmissionEvidence evidence, JsonElement observed, string workingDirectory, TimeSpan maximumAge)
    {
        var runRoot = Path.GetDirectoryName(Path.GetFullPath(workingDirectory))!;
        var cli = ProviderPackCatalog.GetRequired(evidence.Provider).SubscriptionCli
            ?? throw Denied("This provider has no reviewed controlled CLI launch contract.");
        var controls = cli.ControlledLaunch
            ?? throw Denied("This provider has no reviewed owned-home, configuration or file-authentication metadata.");
        if (!Path.IsPathFullyQualified(evidence.CliHomePath) || !Directory.Exists(evidence.CliHomePath)
            || !IsChild(runRoot, evidence.CliHomePath) || IsChild(workingDirectory, evidence.CliHomePath)
            || SamePath(evidence.CliHomePath, workingDirectory) || IsChild(evidence.CliHomePath, workingDirectory)
            || evidence.CliHomeEnvironmentVariable != controls.HomeEnvironmentVariable
            || evidence.AuthenticationStorageMode != "file" || !evidence.AuthenticationFilePresent
            || !evidence.ConfigurationInventoryRoots.SequenceEqual(controls.ConfigurationRoots))
            throw Denied("The native CLI has no separate owned home, reviewed configuration inventory or file-only authentication binding.");
        RejectLinks(evidence.CliHomePath);
        RejectLinks(workingDirectory);
        RejectLinks(evidence.ExecutablePath);
        if (!SamePath(CodexCliLaunchPath.Resolve(evidence.ExecutablePath) ?? "", evidence.ExecutablePath)
            || evidence.ExecutablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || evidence.ExecutablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            || (OperatingSystem.IsWindows() && !evidence.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw Denied("The inspected launch must use the resolved physical executable directly.");
        var auth = Path.Combine(evidence.CliHomePath, "auth.json");
        if (File.Exists(auth)) RejectLinks(auth);
        if (File.Exists(auth) != evidence.AuthenticationFilePresent)
            throw Denied("The reviewed authentication-file presence changed.");
        var inventory = ReadNonSecretInventory(evidence.CliHomePath, evidence.ConfigurationInventoryRoots,
            controls.NonSecretConfigurationTemplates);
        if (inventory.Count != evidence.NonSecretConfigurationHashes.Count
            || evidence.NonSecretConfigurationHashes.Any(pair => !inventory.TryGetValue(pair.Key, out var hash)
                || !string.Equals(hash, pair.Value, StringComparison.OrdinalIgnoreCase)))
            throw Denied("The reviewed non-secret settings, rules or plugin inventory changed.");
        if (!observed.TryGetProperty("cliHomePath", out var observedHome) || !SamePath(observedHome.GetString() ?? "", evidence.CliHomePath)
            || !observed.TryGetProperty("cliHomeEnvironmentVariable", out var variable) || variable.GetString() != evidence.CliHomeEnvironmentVariable
            || !observed.TryGetProperty("authenticationStorageMode", out var mode) || mode.GetString() != "file"
            || !observed.TryGetProperty("authenticationFilePresent", out var present) || present.ValueKind != JsonValueKind.True
            || !observed.TryGetProperty("configurationInventoryRoots", out var roots)
            || !roots.EnumerateArray().Select(item => item.GetString()).SequenceEqual(evidence.ConfigurationInventoryRoots)
            || !observed.TryGetProperty("nonSecretConfigurationHashes", out var hashes)
            || hashes.EnumerateObject().Count() != inventory.Count
            || hashes.EnumerateObject().Any(pair => !inventory.TryGetValue(pair.Name, out var expected)
                || !string.Equals(pair.Value.GetString(), expected, StringComparison.OrdinalIgnoreCase)))
            throw Denied("The actual inspected native process did not bind this owned home, settings inventory and authentication mode.");
        if (!Path.IsPathFullyQualified(evidence.AuthenticationHealthEvidencePath)
            || !File.Exists(evidence.AuthenticationHealthEvidencePath)
            || !string.Equals(InferenceAdmissionValidator.HashFile(evidence.AuthenticationHealthEvidencePath),
                evidence.AuthenticationHealthEvidenceSha256, StringComparison.OrdinalIgnoreCase))
            throw Denied("The sanitized authentication health evidence is missing or changed.");
        using var health = JsonDocument.Parse(File.ReadAllBytes(evidence.AuthenticationHealthEvidencePath));
        var ready = health.RootElement;
        if (!ready.TryGetProperty("status", out var state) || state.GetString() != "PASS_AUTH_READY"
            || !ready.TryGetProperty("cliHomePath", out var healthyHome) || !SamePath(healthyHome.GetString() ?? "", evidence.CliHomePath)
            || !ready.TryGetProperty("authenticationStorageMode", out var healthyMode) || healthyMode.GetString() != "file"
            || !ready.TryGetProperty("authenticationFilePresent", out var authPresent) || authPresent.ValueKind != JsonValueKind.True
            || !ready.TryGetProperty("authenticatedMode", out var authMode) || authMode.GetString() != controls.AuthenticatedMode
            || !ready.TryGetProperty("provider", out var healthProvider) || healthProvider.GetString() != evidence.Provider
            || !ready.TryGetProperty("operation", out var operation) || operation.GetString() != "login-status"
            || !ready.TryGetProperty("workingDirectory", out var healthWorking) || !SamePath(healthWorking.GetString() ?? "", workingDirectory)
            || !ready.TryGetProperty("executableSha256", out var binaryHash) || binaryHash.GetString() != evidence.ExecutableSha256
            || !ready.TryGetProperty("providerPackSha256", out var packHash) || packHash.GetString() != evidence.ProviderPackSha256
            || !ready.TryGetProperty("exactArgv", out var healthArguments)
            || !healthArguments.EnumerateArray().Select(item => item.GetString()).SequenceEqual(controls.FileAuthenticationArguments.Concat(cli.LoginStatusArguments))
            || !ready.TryGetProperty("authenticatedStatusMarker", out var marker)
            || !cli.AuthenticatedStatusMarkers.Contains(marker.GetString() ?? "", StringComparer.Ordinal)
            || !ready.TryGetProperty("observedAtUtc", out var timestamp) || !timestamp.TryGetDateTimeOffset(out var time)
            || time > DateTimeOffset.UtcNow.AddMinutes(5) || time < DateTimeOffset.UtcNow - maximumAge)
            throw Denied("Authentication health has no current owned-home file-mode receipt.");
    }

    public static IReadOnlyDictionary<string, string> ReadNonSecretInventory(string cliHome, IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, string>? reviewedTemplates = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // A fresh controlled home may contain the separately checked auth file
        // and reviewed surfaces only. In particular, root-level named config
        // profiles must not evade a directory-only inventory.
        foreach (var item in Directory.EnumerateFileSystemEntries(cliHome))
        {
            var relative = Path.GetRelativePath(cliHome, item);
            if (relative.Equals("auth.json", StringComparison.Ordinal)) continue;
            if (!roots.Contains(relative, StringComparer.Ordinal))
                throw Denied("The owned CLI home contains an unexpected load surface.");
        }
        foreach (var relative in roots)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains("..", StringComparison.Ordinal)
                || IsAuthenticationName(relative)) throw Denied("Only relative reviewed non-secret configuration surfaces may be inventoried.");
            var target = Path.GetFullPath(Path.Combine(cliHome, relative));
            if (!IsChild(cliHome, target)) throw Denied("Configuration inventory escaped the owned CLI home.");
            if (!File.Exists(target) && !Directory.Exists(target)) continue;
            RejectLinks(target);
            var pending = new Stack<string>(); pending.Push(target);
            while (pending.Count > 0)
            {
                var item = pending.Pop(); RejectLinks(item);
                if (Directory.Exists(item))
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(item)) pending.Push(child);
                    continue;
                }
                if (IsAuthenticationName(Path.GetRelativePath(cliHome, item)))
                    throw Denied("Authentication material must never enter a non-secret inventory hash.");
                var relativeFile = Path.GetRelativePath(cliHome, item).Replace('\\', '/');
                if (reviewedTemplates is null || !reviewedTemplates.TryGetValue(relativeFile, out var nonSecretTemplate))
                    throw Denied("Unexpected configuration/rule/plugin files have no reviewed non-secret static template.");
                var expectedBytes = Encoding.UTF8.GetBytes(nonSecretTemplate);
                // Compare before hashing: arbitrary copied config may contain inline
                // credentials. Only known static non-secret bytes receive a digest.
                if (new FileInfo(item).Length != expectedBytes.Length || !File.ReadAllBytes(item).SequenceEqual(expectedBytes))
                    throw Denied("Owned CLI configuration differs from its reviewed non-secret static template.");
                result[relativeFile] = Convert.ToHexStringLower(SHA256.HashData(expectedBytes));
            }
        }
        return result;
    }

    private static bool IsAuthenticationName(string path) => path.Split('/', '\\').Any(part =>
        part.Equals("auth.json", StringComparison.OrdinalIgnoreCase) || part.Contains("credential", StringComparison.OrdinalIgnoreCase)
        || part.Contains("secret", StringComparison.OrdinalIgnoreCase) || part.Contains("token", StringComparison.OrdinalIgnoreCase)
        || part.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || part.EndsWith(".pem", StringComparison.OrdinalIgnoreCase));
    private static void RejectLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Denied("The physical launch or owned home crosses a filesystem link.");
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
    }
    private static bool IsChild(string root, string path) => Path.IsPathFullyQualified(path)
        && Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool SamePath(string a, string b) => Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static UnauthorizedAccessException Denied(string reason) => new("Inference admission denied: " + reason);
}
