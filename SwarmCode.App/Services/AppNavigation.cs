namespace SwarmCode.App.Services;

/// <summary>Small presentation helpers shared by layout and pages.</summary>
public static class AppNavigation
{
    public static string SessionUrl(string sessionId) => $"/session/{Uri.EscapeDataString(sessionId)}";

    /// <summary>Extracts the session id from a relative URL like "session/abc", or null.</summary>
    public static string? SessionIdFrom(string relativeUrl)
    {
        var path = relativeUrl.Split('?', '#')[0].Trim('/');
        const string prefix = "session/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.Length > prefix.Length
            ? Uri.UnescapeDataString(path[prefix.Length..])
            : null;
    }

    public static string FolderName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    public static string RelativeTime(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 45) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)Math.Max(1, span.TotalMinutes)}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return utc.ToLocalTime().ToString("MMM d");
    }

    /// <summary>Wall-clock time in the person's zone, for a message whose day is already shown ("3:42 PM").</summary>
    public static string ClockTime(DateTime utc) => utc.ToLocalTime().ToString("h:mm tt");

    /// <summary>Full local date and time, for tooltips and lists ("Oct 14, 2026 3:42 PM").</summary>
    public static string FullTime(DateTime utc) => utc.ToLocalTime().ToString("MMM d, yyyy h:mm tt");

    /// <summary>Compact local date and time for list rows ("Oct 14 · 3:42 PM").</summary>
    public static string DateAndTime(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Year == DateTime.Now.Year ? local.ToString("MMM d · h:mm tt") : local.ToString("MMM d, yyyy · h:mm tt");
    }

    /// <summary>A day heading in the person's calendar: Today, Yesterday, or the date.</summary>
    public static string DayLabel(DateTime localDate)
    {
        var today = DateTime.Now.Date;
        var date = localDate.Date;
        if (date == today) return "Today";
        if (date == today.AddDays(-1)) return "Yesterday";
        return date.Year == today.Year ? date.ToString("dddd, MMM d") : date.ToString("dddd, MMM d, yyyy");
    }

    /// <summary>The history view, optionally opened on a search or a summary period.</summary>
    public static string HistoryUrl(string? query = null, string? summary = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query)) parts.Add("q=" + Uri.EscapeDataString(query.Trim()));
        if (!string.IsNullOrWhiteSpace(summary)) parts.Add("summary=" + Uri.EscapeDataString(summary.Trim()));
        return parts.Count == 0 ? "/history" : "/history?" + string.Join("&", parts);
    }
}
