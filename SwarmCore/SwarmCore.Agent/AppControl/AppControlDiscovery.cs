using System.Diagnostics;
using System.Text.Json;

namespace SwarmCore.Agent.AppControl;

/// <summary>What a running app publishes so an agent the person connects on this computer can reach it.</summary>
public sealed record AppControlEndpoint(
    string App,
    string DisplayName,
    string Url,
    string Token,
    int ProcessId,
    string? Profile,
    string? Version,
    DateTimeOffset WrittenUtc)
{
    /// <summary>When the publishing process started; a record whose process id was reused by another process is stale.</summary>
    public DateTimeOffset? ProcessStartUtc { get; init; }
}

/// <summary>
/// Per-user, per-app discovery records under the local application-data folder
/// (<c>SwarmCore/app-control/&lt;app&gt;.json</c>). The folder is private to the signed-in user, and on Unix the
/// file is written owner-read/write only, so the token reaches only processes running as that user: the same
/// boundary as the app's own data files. A record whose process is gone is ignored and removed.
/// Every record in this folder is an MCP app-control server (agents list and connect them); other loopback
/// companions use their own <see cref="LoopbackDiscoveryDirectory"/>.
/// </summary>
public static class AppControlDiscovery
{
    /// <summary>Overridable for tests and isolated hosts (<c>SWARMCORE_APP_CONTROL_DIR</c>).</summary>
    public static string Directory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("SWARMCORE_APP_CONTROL_DIR");
            return !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwarmCore", "app-control");
        }
    }

    private static LoopbackDiscoveryDirectory Current => new(Directory);

    // Mac Catalyst mapped LocalApplicationData to Documents, while native macOS
    // maps it to Application Support. Read both without moving a publisher's
    // record/token or scanning other users' or sandboxed profiles.
    private static IReadOnlyList<string> CandidateDirectories => ResolveDirectories(
        Environment.GetEnvironmentVariable("SWARMCORE_APP_CONTROL_DIR"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static IReadOnlyList<string> ResolveDirectories(string? overridden, string localApplicationData,
        string documents, bool includeLegacyMacDirectory, string? userProfile = null)
    {
        if (!string.IsNullOrWhiteSpace(overridden)) return [overridden];
        var directories = new List<string> { Path.Combine(localApplicationData, "SwarmCore", "app-control") };
        if (includeLegacyMacDirectory && !string.IsNullOrWhiteSpace(userProfile))
            directories.Add(Path.Combine(userProfile, "Library", "Application Support", "SwarmCore", "app-control"));
        if (includeLegacyMacDirectory && !string.IsNullOrWhiteSpace(documents))
            directories.Add(Path.Combine(documents, "SwarmCore", "app-control"));
        return directories.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    public static string PathFor(string app) => Current.PathFor(app);

    /// <summary>
    /// Publishes the endpoint. A second running instance of the same app does not replace the first one's live
    /// record (agents keep reaching the first); it returns false and the caller keeps serving without publishing.
    /// </summary>
    public static bool Publish(AppControlEndpoint endpoint) => Publish(endpoint, Directory, CandidateDirectories);

    internal static bool Publish(AppControlEndpoint endpoint, string directory, IReadOnlyList<string> candidates)
    {
        // A legacy publisher retains ownership even when this runtime writes to a
        // different default directory. Never replace or migrate its bearer.
        if (candidates.Select(path => new LoopbackDiscoveryDirectory(path).Find(endpoint.App, requireReadable: true))
            .Any(existing => existing is not null && existing.ProcessId != endpoint.ProcessId)) return false;
        return new LoopbackDiscoveryDirectory(directory).Publish(endpoint);
    }

    /// <summary>
    /// The app's token, kept across restarts in the same user-private folder so a connection an agent saved keeps
    /// working after the app restarts. A new token is created when none exists or the file is unreadable.
    /// </summary>
    public static string GetOrCreateToken(string app, Func<string> create) => Current.GetOrCreateToken(app, create);

    /// <summary>Removes the record only when it still describes this process (another instance may have replaced it).</summary>
    public static void Withdraw(string app, int processId) => Current.Withdraw(app, processId);

    /// <summary>The live endpoint for <paramref name="app"/>, or null when the app is not running.</summary>
    public static AppControlEndpoint? Find(string app) => Find(app, CandidateDirectories);

    internal static AppControlEndpoint? Find(string app, IReadOnlyList<string> candidates)
    {
        var matches = UniquePublishers(candidates.Select(path => new LoopbackDiscoveryDirectory(path).Find(app, requireReadable: true))
            .OfType<AppControlEndpoint>()).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException($"More than one live instance of '{app}' publishes app control. "
                + "Close the unintended instance or explicitly select its discovery directory before connecting.");
        return matches.FirstOrDefault();
    }

    /// <summary>Every running app that published an endpoint.</summary>
    public static IReadOnlyList<AppControlEndpoint> List() => List(CandidateDirectories);

    internal static IReadOnlyList<AppControlEndpoint> List(IReadOnlyList<string> candidates)
        => UniquePublishers(candidates.SelectMany(path => new LoopbackDiscoveryDirectory(path).List(requireReadable: true))).ToArray();

    private static IEnumerable<AppControlEndpoint> UniquePublishers(IEnumerable<AppControlEndpoint> endpoints)
        => endpoints.DistinctBy(endpoint => (endpoint.App.ToLowerInvariant(), endpoint.ProcessId,
            endpoint.Url, endpoint.Profile, endpoint.Token));
}

/// <summary>
/// The discovery-record conventions behind <see cref="AppControlDiscovery"/>, for any user-private folder: atomic
/// owner-only writes, first live instance wins, and a record is live only while its process id AND process start
/// time still match (process ids are reused, so an id alone could hand the token to an unrelated listener).
/// </summary>
public sealed class LoopbackDiscoveryDirectory(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Directory { get; } = directory;

    public string PathFor(string app) => Path.Combine(Directory, Sanitize(app) + ".json");

    public bool Publish(AppControlEndpoint endpoint)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(endpoint.App);
        if (Read(path) is { } existing && existing.ProcessId != endpoint.ProcessId && IsLive(existing)) return false;
        endpoint = endpoint with { ProcessStartUtc = endpoint.ProcessStartUtc ?? StartTime(endpoint.ProcessId) };
        WritePrivate(path, JsonSerializer.Serialize(endpoint, Json));
        return true;
    }

    public string GetOrCreateToken(string app, Func<string> create)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, Sanitize(app) + ".token");
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: >= 32 } existing) return existing;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var token = create();
        WritePrivate(path, token);
        return token;
    }

    public void Withdraw(string app, int processId)
    {
        try
        {
            var path = PathFor(app);
            if (Read(path) is { } current && current.ProcessId != processId) return;
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public AppControlEndpoint? Find(string app) => Find(app, requireReadable: false);

    internal AppControlEndpoint? Find(string app, bool requireReadable)
    {
        var path = PathFor(app);
        var endpoint = Read(path, requireReadable);
        if (endpoint is null) return null;
        if (IsLive(endpoint)) return endpoint;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return null;
    }

    public IReadOnlyList<AppControlEndpoint> List() => List(requireReadable: false);

    internal IReadOnlyList<AppControlEndpoint> List(bool requireReadable)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(f => Find(Path.GetFileNameWithoutExtension(f), requireReadable))
            .OfType<AppControlEndpoint>()
            .ToList();
    }

    /// <summary>True while the publishing process is still the one running under that id.</summary>
    public static bool IsLive(AppControlEndpoint endpoint)
    {
        var started = StartTime(endpoint.ProcessId);
        if (started is null) return false;
        return endpoint.ProcessStartUtc is not { } recorded || Math.Abs((started.Value - recorded).TotalSeconds) < 2;
    }

    private void WritePrivate(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        // Created owner-only from the start: no window in which another local user can read the token.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(content);
        File.Move(temporary, path, overwrite: true);
    }

    private static AppControlEndpoint? Read(string path, bool requireReadable = false)
    {
        try
        {
            if (!requireReadable && !File.Exists(path)) return null;
            var endpoint = JsonSerializer.Deserialize<AppControlEndpoint>(File.ReadAllText(path), Json);
            if (requireReadable && (endpoint is null || string.IsNullOrWhiteSpace(endpoint.App)
                || !string.Equals(endpoint.App, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(endpoint.Token) || endpoint.ProcessId <= 0
                || !Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var url) || !url.IsLoopback
                || url.Scheme != "http" || !string.IsNullOrEmpty(url.UserInfo)))
                throw UnreadableRecord();
            return endpoint;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) when (!requireReadable) { return null; }
        catch (IOException) when (!requireReadable) { return null; }
        catch (Exception ex) when (requireReadable && ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Choosing another candidate could silently change the connected
            // profile. Do not include record contents or bearer values in errors.
            throw UnreadableRecord();
        }
    }

    private static InvalidOperationException UnreadableRecord()
        => new("An app-control discovery record is unreadable. Repair it or explicitly select the intended discovery directory before connecting.");

    private static DateTimeOffset? StartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static string Sanitize(string app)
    {
        var clean = new string(app.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return clean.Length is 0 or > 40 ? throw new ArgumentException("Invalid app name.", nameof(app)) : clean;
    }
}
