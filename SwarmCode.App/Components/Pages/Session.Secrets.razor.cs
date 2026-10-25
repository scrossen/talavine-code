using SwarmCode.App.Services;
using SwarmCore.Secrets;

namespace SwarmCode.App.Components.Pages;

/// <summary>
/// Vault commands typed into the composer. They are answered here, on the device, and the
/// message is never sent to a lane, stored in the transcript, or logged.
/// </summary>
public partial class Session
{
    private async Task HandleSecretCommandAsync(string text)
    {
        try
        {
            var outcome = await App.SecretIntake.HandleCommandAsync(text);
            if (outcome.PromptForValueName is { } name)
            {
                var siteFlag = outcome.PromptForValueSite is { } site ? $"--site {site} " : "";
                AddNote("info", $"Type /secret {siteFlag}{name} <value> to save it, or add it under Credentials in Settings (the value is stored protected and never shown to the model).");
                return;
            }
            if (outcome.StoredNames.Count > 0)
                AddNote("secret", outcome.Message ?? $"Saved {string.Join(", ", outcome.StoredNames)}.");
            else if (outcome.Message is not null)
                AddNote("info", outcome.Message.Contains('\n') ? "Stored secrets" : outcome.Message, outcome.Message.Contains('\n') ? outcome.Message : null);
        }
        catch (Exception ex)
        {
            AddNote("error", "The vault command failed.", ex.Message);
        }
    }

    private async Task ImportPasswordsAsync(string pathArgument)
    {
        try
        {
            var path = pathArgument.Trim().Trim('"');
            if (path.Length == 0)
            {
                var picked = await DesktopPlatform.PickFileAsync("Choose a password export (CSV)", "csv");
                if (picked is null) { AddNote("info", "Import cancelled."); return; }
                path = picked;
            }
            var summary = await App.PasswordImporter.ImportFileAsync(path);
            if (!summary.Success) { AddNote("warn", summary.Describe()); return; }
            var details = summary.Names.Count == 0 ? null : "Logins:\n" + string.Join("\n", summary.Names.Select(n => $"{n}  ->  {SecretReference.Placeholder(n)}"));
            AddNote("secret", summary.Describe(), details);
        }
        catch (Exception ex)
        {
            AddNote("error", "Password import failed.", ex.Message);
        }
    }
}
