using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.Networking;
using SwarmCore.Agent.Procedures;

namespace SwarmCore.RemoteControl.Agent;

/// <summary>
/// Stable procedure identity for desktop work: surface "desktop", the site or app the task runs against, and the
/// task label. A site is a registrable domain (walmart.com) when the window is a browser showing a URL, otherwise
/// the process name (notepad). Sites and tasks are data; nothing here knows any particular site.
/// </summary>
public static class DesktopProcedureKeys
{
    public const string Surface = "desktop";
    public const string BindingVersion = "desktop-procedure-v1";

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "safari", "chromium", "google chrome", "microsoft edge",
    };

    /// <summary>True for process names of common browsers, whose site identity comes from the address bar.</summary>
    public static bool IsBrowserProcess(string? processName) =>
        processName is not null && BrowserProcesses.Contains(Path.GetFileNameWithoutExtension(processName.Trim()));

    /// <summary>Lowercase, trimmed, single-spaced site identity: a registrable domain or a process name.</summary>
    public static string NormalizeSite(string site)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(site);
        var s = site.Trim().ToLowerInvariant();
        if (s.EndsWith(".exe", StringComparison.Ordinal) || s.EndsWith(".app", StringComparison.Ordinal)) s = s[..^4];
        else if (TryRegistrableDomain(s, out var domain)) return domain;
        return Collapse(s, 80);
    }

    /// <summary>Lowercase, single-spaced task label, at most 120 characters.</summary>
    public static string NormalizeTask(string task)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        return Collapse(task.Trim().ToLowerInvariant(), 120);
    }

    /// <summary>The procedure task id: desktop/&lt;site&gt;/&lt;task&gt;.</summary>
    public static string TaskId(string site, string task) => $"{Surface}/{NormalizeSite(site)}/{NormalizeTask(task)}";

    public static AgentProcedureKey Key(string profileId, string agentId, string site, string task) =>
        new(profileId, agentId, TaskId(site, task));

    /// <summary>
    /// Binding fingerprint. It changes when the site, task, the names of the per-run inputs, the secret slots, or the
    /// host's verification contract change, so a procedure learned under one contract is never replayed under another.
    /// </summary>
    public static string BindingFingerprint(string site, string task, JsonObject inputs,
        IEnumerable<string>? secretSlots = null, string verificationContract = "desktop-verify-v1")
    {
        var names = string.Join(",", inputs.Select(p => p.Key).Order(StringComparer.Ordinal));
        var slots = string.Join(",", (secretSlots ?? []).Order(StringComparer.Ordinal));
        var material = string.Join("\n", BindingVersion, NormalizeSite(site), NormalizeTask(task), names, slots, verificationContract);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    /// The site identity of a window: the registrable domain of the URL in a browser's address bar or document,
    /// otherwise the process name. <paramref name="controls"/> are the window's controls (any listing).
    /// </summary>
    public static string SiteOf(WindowInfo window, IEnumerable<ControlInfo> controls)
    {
        if (IsBrowserProcess(window.ProcessName))
            foreach (var c in controls.Where(c => c.ControlType is "edit" or "document" or "combobox"))
                if (TryRegistrableDomain(c.Value, out var domain)) return domain;
        return NormalizeSite(window.ProcessName);
    }

    /// <summary>Reads the site of the session's current window (or the foreground window) without acting on it.</summary>
    public static string? SiteOfCurrentWindow(RemoteControlSession session)
    {
        var windows = session.Execute(new RemoteCommand(CommandKind.ListWindows)).Data as WindowListing;
        var current = windows?.Windows.FirstOrDefault(w => w.Window.Handle == session.CurrentWindowHandle)
            ?? windows?.Windows.FirstOrDefault(w => w.Window.IsForeground);
        if (current is null) return null;
        IEnumerable<ControlInfo> controls = [];
        if (IsBrowserProcess(current.Window.ProcessName)
            && session.Execute(new RemoteCommand(CommandKind.ListControls) { Window = new WindowSelector(Handle: current.Window.Handle) }).Data is ControlListing listing)
            controls = listing.Controls.Select(e => e.Control);
        return SiteOf(current.Window, controls);
    }

    /// <summary>Parses a URL or bare host ("walmart.com/ip/123", "https://www.walmart.com") to its registrable domain.</summary>
    public static bool TryRegistrableDomain(string? text, out string domain)
    {
        domain = "";
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || t.Contains(' ') || !t.Contains('.')) return false;
        if (!t.Contains("://", StringComparison.Ordinal)) t = "https://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return false;
        var registrable = PublicSuffixList.RegistrableDomainOf(uri.Host);
        if (registrable is null) return false;
        domain = registrable.ToLowerInvariant();
        return true;
    }

    private static string Collapse(string s, int max)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var c = char.IsWhiteSpace(ch) || ch == '/' ? ' ' : ch;
            if (c == ' ' && (sb.Length == 0 || sb[^1] == ' ')) continue;
            sb.Append(c);
        }
        var r = sb.ToString().TrimEnd();
        return r.Length <= max ? r : r[..max].TrimEnd();
    }
}
