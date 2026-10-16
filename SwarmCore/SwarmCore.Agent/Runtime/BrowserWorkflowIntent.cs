using System.Text.RegularExpressions;

namespace SwarmCore.Agent.Runtime;

/// <summary>Positive routing hints, never permission or proof of available browser access.</summary>
public static partial class BrowserWorkflowIntent
{
    public static bool RequestsConnectedChrome(string text)
        => ConnectedChromeInstruction().IsMatch(text);

    public static bool RequestsTalavineBrowser(string text)
        => TalavineBrowserInstruction().IsMatch(text);

    [GeneratedRegex(@"(?:^|[.!?]\s+)(?:please\s+)?(?:use|inspect|read|check)\s+(?:the\s+|my\s+)?(?:talavine-browser|talavine['’]s\s+(?:separate\s+|persistent\s+)?browser)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TalavineBrowserInstruction();

    // Require an instruction, not a stray browser/product name in stored mail
    // or a negative clause such as "do not use Chrome". The host supplies the
    // authentic objective, not page content or continuation receipts.
    [GeneratedRegex(@"(?:^|[.!?]\s+)(?:please\s+)?(?:use|inspect|read|check)\s+(?:the\s+|my\s+)?(?:normal[- ]chrome|connected[- ](?:chrome|site)|chrome\s+(?:browser|profile|tab))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectedChromeInstruction();
}
