using SwarmCore.Agent.Catalog;
using SwarmCore.RemoteControl.Agent;

namespace SwarmCore.RemoteControl.Platform;

/// <summary>
/// When an agent should use the desktop tools rather than the browser tools (and rather than pixel computer tools).
/// The same rule for every host; the long form is <c>docs/computer-control-tool-choice.md</c>.
/// </summary>
public static class DesktopToolChoice
{
    /// <summary>Repository path of the long-form rule, for prompts and docs that point at it.</summary>
    public const string DocumentPath = "docs/computer-control-tool-choice.md";

    /// <summary>
    /// Two or three sentences a host adds where its system prompt introduces browser and computer tools.
    /// </summary>
    public const string PromptGuidance =
        "Choose the control tool per step: browser tools for a web page in a browser the host controls when DOM-level reads and actions suffice; "
        + "desktop.* tools for native apps, dialogs, file pickers, system UI, any window (including the user's own browser when no browser bridge is connected) "
        + "and whenever you need control names or values; pixel computer tools only for custom-drawn surfaces with no readable controls. "
        + "The families mix freely within one task (open a page with browser tools, then finish its native Save dialog with desktop.act), "
        + "but ids belong to their family: browser tab and observation ids never work in desktop tools, and desktop wN/cN ids never work in browser tools.";

    /// <summary>The one-sentence choice rule appended to each desktop tool's own description, keyed by tool id.</summary>
    public static IReadOnlyDictionary<string, string> ChoiceSentences { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [DesktopControlTools.ListWindowsTool] =
            "Start here for native apps, dialogs, file pickers, system UI or the user's own browser window when no browser bridge is connected; for a web page in a browser the host controls use the browser tools (their tab ids are not desktop wN ids).",
        [DesktopControlTools.InspectTool] =
            "Prefer this whenever you need control names, values or states in any native window or dialog; for a web page in a host-controlled browser the browser tools' DOM reads are better, and pixel computer tools only suit custom-drawn surfaces with no listed controls.",
        [DesktopControlTools.CaptureTool] =
            "Use it to see native windows, dialogs or the whole screen; a host-controlled browser page is better observed with the browser tools, and inspect gives names and values that a screenshot does not.",
        [DesktopControlTools.ActTool] =
            "Prefer this for native apps, dialogs, file pickers (for example a browser's Save or Upload dialog), system UI and any window when no browser bridge is connected; use the browser tools for DOM-level work on a host-controlled page and pixel computer tools only for custom-drawn surfaces.",
        [DesktopControlTools.InputTool] =
            "Use this for keys or text in native windows when no named control fits; inside a host-controlled browser page prefer the browser tools, and prefer desktop.act by control name whenever the target is listed.",
        [DesktopControlTools.WaitForTool] =
            "Use it to wait for native windows, dialogs or controls to appear; wait on host-controlled browser pages with the browser tools.",
        [DesktopControlTools.CommandTool] =
            "A fallback for native desktop work the structured desktop tools do not cover; never use it for a host-controlled browser page, which the browser tools handle.",
    };

    /// <summary>
    /// The session's desktop tools as this host publishes them: owned by <paramref name="ownerAgentId"/> and each
    /// description carrying its tool-choice sentence. Side effects, schemas and invocation are unchanged.
    /// </summary>
    public static IReadOnlyList<ToolDescriptor> ForHost(DesktopControlTools tools, string ownerAgentId)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAgentId);
        return tools.Descriptors.Select(d => d with
        {
            OwnerAgentId = ownerAgentId,
            Description = ChoiceSentences.TryGetValue(d.ToolId, out var sentence) && !d.Description.Contains(sentence, StringComparison.Ordinal)
                ? d.Description.TrimEnd() + " " + sentence
                : d.Description,
        }).ToArray();
    }
}
