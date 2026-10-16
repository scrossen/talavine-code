using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace SwarmCore.Chat.Components;

/// <summary>Every icon position the transcript components render.</summary>
public enum ChatIcon
{
    ToolRunning,
    ToolCompleted,
    ToolFailed,
    ToolUnknown,
    /// <summary>A collapsed section that can be opened.</summary>
    Expand,
    /// <summary>An open section that can be collapsed.</summary>
    Collapse,
    /// <summary>Stop one running step.</summary>
    StopStep,
    Reasoning,
    ReasoningActive,
    Permission,
    PermissionApproved,
    PermissionDenied,
    RecoveryFailed,
    RecoveryPaused,
}

/// <summary>
/// Host icon markup for the transcript components (for example an icon font). Supply it
/// as a component parameter or as a cascading value around the transcript. The renderer
/// receives the icon position and the component's element class for that position
/// (for example <c>tool-call__chevron</c>), which the host may put on its own element;
/// returning null keeps the shared glyph for that position.
/// </summary>
public sealed class ChatIconSet
{
    private readonly Func<ChatIcon, string?, RenderFragment?> _render;

    public ChatIconSet(Func<ChatIcon, string?, RenderFragment?> render)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
    }

    public RenderFragment? Render(ChatIcon icon, string? cssClass) => _render(icon, cssClass);
}

/// <summary>The shared default glyphs, used wherever the host supplies no icon.</summary>
public static class ChatGlyphs
{
    /// <summary>The host icon when one is supplied, else the shared glyph (which may be nothing).</summary>
    public static RenderFragment? Resolve(ChatIcon icon, string? cssClass, ChatIconSet? host)
        => host?.Render(icon, cssClass) ?? Default(icon, cssClass);

    /// <summary>The shared glyph for a position, or null when the position has none by default.</summary>
    public static RenderFragment? Default(ChatIcon icon, string? cssClass) => icon switch
    {
        ChatIcon.ToolRunning or ChatIcon.ReasoningActive => Spinner(cssClass),
        ChatIcon.ToolCompleted or ChatIcon.PermissionApproved => Glyph("✓", cssClass),
        ChatIcon.ToolFailed or ChatIcon.PermissionDenied => Glyph("✗", cssClass),
        ChatIcon.ToolUnknown => Glyph("?", cssClass),
        ChatIcon.Expand => Chevron(open: false, cssClass),
        ChatIcon.Collapse => Chevron(open: true, cssClass),
        ChatIcon.Permission or ChatIcon.RecoveryFailed => Glyph("⚠", cssClass),
        ChatIcon.RecoveryPaused => Glyph("⏸", cssClass),
        _ => null,
    };

    private static RenderFragment Glyph(string text, string? cssClass) => builder =>
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", Join("chat-glyph", cssClass));
        builder.AddAttribute(2, "aria-hidden", "true");
        builder.AddContent(3, text);
        builder.CloseElement();
    };

    private static RenderFragment Spinner(string? cssClass) => builder =>
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", Join("chat-spinner", cssClass));
        builder.AddAttribute(2, "aria-hidden", "true");
        builder.CloseElement();
    };

    private static RenderFragment Chevron(bool open, string? cssClass) => builder =>
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", Join(open ? "chat-chevron chat-chevron--open" : "chat-chevron", cssClass));
        builder.AddAttribute(2, "aria-hidden", "true");
        builder.AddContent(3, "▸");
        builder.CloseElement();
    };

    private static string Join(string baseClass, string? extra)
        => string.IsNullOrWhiteSpace(extra) ? baseClass : baseClass + " " + extra;
}
