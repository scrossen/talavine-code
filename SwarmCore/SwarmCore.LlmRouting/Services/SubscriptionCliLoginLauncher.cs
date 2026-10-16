using System.Diagnostics;
using System.Text.RegularExpressions;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Selects browser login when the installed CLI proves it supports the reviewed
/// command, retaining a visible terminal for interactive-only versions/providers.
/// </summary>
internal static class SubscriptionCliLoginLauncher
{
    internal static bool RequiresInteractiveTerminal(string providerName, bool supportsBrowserLogin = false)
        => (providerName.Equals(SubscriptionCliProviders.ClaudeCode, StringComparison.OrdinalIgnoreCase)
            && !supportsBrowserLogin)
           || providerName.Equals(SubscriptionCliProviders.CodexCli, StringComparison.OrdinalIgnoreCase)
           || providerName.Equals(SubscriptionCliProviders.GeminiCli, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Claude added <c>auth login</c> in 2.1.41. Older versions need the documented
    /// REPL <c>/login</c> compatibility path. Other providers use
    /// their reviewed direct login arguments inside the visible terminal.
    /// </summary>
    internal static IReadOnlyList<string> GetInteractiveLoginArguments(
        string providerName,
        IReadOnlyList<string> configuredArguments)
    {
        ArgumentNullException.ThrowIfNull(configuredArguments);
        return providerName.Equals(
            SubscriptionCliProviders.ClaudeCode,
            StringComparison.OrdinalIgnoreCase)
            ? []
            : configuredArguments;
    }

    // A successful --help exit alone is insufficient: older Claude versions
    // show top-level help even when the preceding command is unrecognized.
    internal static bool AdvertisesLoginCommand(string help, IReadOnlyList<string> arguments)
        => arguments.Count > 0 && Regex.IsMatch(
            help,
            @"(?m)^\s*Usage:[ \t]+\S+[ \t]+"
            + string.Join(@"[ \t]+", arguments.Select(Regex.Escape))
            + @"(?:[ \t]+[^\r\n]*|[ \t]*)\r?$",
            RegexOptions.CultureInvariant);

    internal static ProcessStartInfo CreateMacOsLoginStartInfo(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        // `do script` creates a visible Terminal window with a TTY. Passing the
        // AppleScript itself as one ProcessStartInfo argument avoids a shell
        // between Talavine and osascript. The command executed by Terminal is
        // independently single-quoted so paths/arguments remain inert data.
        var commandParts = new[] { executablePath }.Concat(arguments);
        var scrubNames = string.Join(
            " ",
            SubscriptionCliEnvironment.ApiCredentialEnvironmentVariables
                .Select(QuoteForPosixShell));
        var terminalCommand = $"unset {scrubNames}; exec "
                              + string.Join(" ", commandParts.Select(QuoteForPosixShell));
        // `launch` does not send the normal run/reopen event that creates an
        // empty default window. Create the login session before activation.
        var script = "if application \"Terminal\" is not running then\n"
                     + "tell application \"Terminal\" to launch\nend if\n"
                     + $"tell application \"Terminal\" to do script {QuoteForAppleScript(terminalCommand)}\n"
                     + "tell application \"Terminal\" to activate";
        var startInfo = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        SubscriptionCliEnvironment.ScrubApiCredentialEnvironment(startInfo.Environment);
        return startInfo;
    }

    internal static ProcessStartInfo CreateMacOsGeminiLoginStartInfo(string executablePath)
        => CreateMacOsLoginStartInfo(executablePath, []);

    private static string QuoteForPosixShell(string value)
        => "'" + value.Replace("'", "'\\\"'\\\"'", StringComparison.Ordinal) + "'";

    private static string QuoteForAppleScript(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
