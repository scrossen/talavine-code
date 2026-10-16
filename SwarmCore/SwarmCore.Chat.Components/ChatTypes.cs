using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SwarmCore.Chat.Components;

public enum ToolCallCardState { Running, Completed, Failed, Unknown }

/// <summary>How <c>ToolCallCard</c> shows a result.</summary>
public enum ToolResultDisplay
{
    /// <summary>Error and diff sections are lifted out of a JSON result; the rest is pretty-printed.</summary>
    Parsed,
    /// <summary>The result text verbatim (clipped), shown once the call is no longer running.</summary>
    Raw,
}

/// <summary>A permission choice made on <c>PermissionCard</c>.</summary>
public enum PermissionCardDecision { ApproveOnce, AlwaysAllow, Deny }

/// <summary>Visual weight of a <see cref="ChatNoticeAction"/>.</summary>
public enum ChatNoticeActionStyle { Default, Primary, Ghost }

/// <summary>An action button on a recovery <c>ChatNotice</c>.</summary>
public sealed record ChatNoticeAction(string Label, EventCallback OnClick)
{
    /// <summary>Optional icon markup rendered before the label.</summary>
    public RenderFragment? Icon { get; init; }
    public string? Title { get; init; }
    public bool Disabled { get; init; }
    public ChatNoticeActionStyle Style { get; init; }
}

/// <summary>A slash command offered by <c>ChatComposer</c>. Name may be given with or without the leading slash.</summary>
public sealed record SlashCommand(string Name, string Description);

/// <summary>Raised by <c>ChatHistoryDrawer</c> when the user renames a conversation.</summary>
public sealed record ConversationRenameRequest(string ConversationId, string Title);

/// <summary>Derives the one-line summary and pretty-printed JSON shown by <c>ToolCallCard</c>.</summary>
public static partial class ToolCallSummary
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Up to three top-level string/number properties as "key: value", truncated; null when none.</summary>
    public static string? FromInput(string? inputJson, int maxValueLength = 60, int maxTotalLength = 140)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var parts = new List<string>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                string? value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null,
                };
                if (value is null)
                {
                    continue;
                }

                parts.Add($"{property.Name}: {Truncate(Collapse(value), maxValueLength)}");
                if (parts.Count == 3)
                {
                    break;
                }
            }

            return parts.Count == 0 ? null : Truncate(string.Join(", ", parts), maxTotalLength);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Indented JSON when the text parses as JSON; otherwise the text unchanged.</summary>
    public static string PrettyPrint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, Indented);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>Compact human duration: "0.4s", "12s", "2m 05s".</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 10) return $"{elapsed.TotalSeconds:0.0}s";
        if (elapsed.TotalSeconds < 60) return $"{elapsed.TotalSeconds:0}s";
        return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s";
    }

    /// <summary>Running clock for a step in progress: "0s", "42s", "2m 05s".</summary>
    public static string FormatRunningElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
            : $"{elapsed.TotalSeconds:F0}s";
    }

    /// <summary>The text cut to <paramref name="max"/> characters with a note saying how much was left out.</summary>
    public static string Clip(string text, int max)
    {
        if (max <= 0 || text.Length <= max) return text;
        return text[..max] + $"\n\n… [truncated {text.Length - max} chars]";
    }

    internal static string Collapse(string value) => Whitespace().Replace(value.Trim(), " ");

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>
/// Loads <c>chat-components.js</c> on demand so hosts do not have to add a script tag
/// (they still may). Failures are swallowed: the components degrade to plain behavior.
/// </summary>
internal static class ChatJs
{
    private const string ModuleUrl = "./_content/SwarmCore.Chat.Components/chat-components.js";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IJSRuntime, Task> Loaded = new();

    public static Task EnsureAsync(IJSRuntime js) =>
        Loaded.GetValue(js, static runtime => LoadAsync(runtime));

    private static async Task LoadAsync(IJSRuntime js)
    {
        try { await js.InvokeVoidAsync("import", ModuleUrl); }
        catch (Exception ex) when (IsJsFailure(ex)) { /* script tag may already provide it */ }
    }

    public static async Task InvokeVoidAsync(IJSRuntime js, string function, params object?[] args)
    {
        try
        {
            await EnsureAsync(js);
            await js.InvokeVoidAsync("chatComponents." + function, args);
        }
        catch (Exception ex) when (IsJsFailure(ex)) { }
    }

    public static async Task<T?> InvokeAsync<T>(IJSRuntime js, string function, params object?[] args)
    {
        try
        {
            await EnsureAsync(js);
            return await js.InvokeAsync<T>("chatComponents." + function, args);
        }
        catch (Exception ex) when (IsJsFailure(ex)) { return default; }
    }

    private static bool IsJsFailure(Exception ex) =>
        ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException or OperationCanceledException;
}
