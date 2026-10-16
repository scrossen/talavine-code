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

    public static string PathFor(string app) => Current.PathFor(app);

    /// <summary>
    /// Publishes the endpoint. A second running instance of the same app does not replace the first one's live
    /// record (agents keep reaching the first); it returns false and the caller keeps serving without publishing.
    /// </summary>
    public static bool Publish(AppControlEndpoint endpoint) => Current.Publish(endpoint);

    /// <summary>
    /// The app's token, kept across restarts in the same user-private folder so a connection an agent saved keeps
    /// working after the app restarts. A new token is created when none exists or the file is unreadable.
    /// </summary>
    public static string GetOrCreateToken(string app, Func<string> create) => Current.GetOrCreateToken(app, create);

    /// <summary>Removes the record only when it still describes this process (another instance may have replaced it).</summary>
    public static void Withdraw(string app, int processId) => Current.Withdraw(app, processId);

    /// <summary>The live endpoint for <paramref name="app"/>, or null when the app is not running.</summary>
    public static AppControlEndpoint? Find(string app) => Current.Find(app);

    /// <summary>Every running app that published an endpoint.</summary>
    public static IReadOnlyList<AppControlEndpoint> List() => Current.List();
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

    public AppControlEndpoint? Find(string app)
    {
        var path = PathFor(app);
        var endpoint = Read(path);
        if (endpoint is null) return null;
        if (IsLive(endpoint)) return endpoint;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return null;
    }

    public IReadOnlyList<AppControlEndpoint> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(f => Find(Path.GetFileNameWithoutExtension(f)))
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

    private static AppControlEndpoint? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AppControlEndpoint>(File.ReadAllText(path), Json) : null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

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
