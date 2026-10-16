using System.Diagnostics;
using SwarmCore.Abstractions.Processes;
using SwarmCore.LlmRouting.LocalRuntime;

namespace SwarmCode.Hosting;

/// <summary>Launches the installed desktop runtime; the desktop app owns its daemon and settings.</summary>
public static class OllamaDesktopSetup
{
    public static async Task<OllamaInventory> StartAsync(OllamaSetupClient client, string endpoint, CancellationToken ct = default)
    {
        var uri = new Uri(OllamaSetupClient.ValidateEndpoint(endpoint));
        try { return await client.InventoryAsync(endpoint, ct).ConfigureAwait(false); }
        catch (HttpRequestException) { }
        if (!uri.IsLoopback || uri.Port != 11434)
            throw new InvalidOperationException("Start Ollama on the configured server, then check the connection again.");

        ProcessStartInfo start;
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            var app = new[] { "/Applications/Ollama.app", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/Ollama.app") }.FirstOrDefault(Directory.Exists);
            if (app is null) throw new InvalidOperationException("Install Ollama using Get Ollama, move it to Applications, then choose Start Ollama here.");
            start = new("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add(app);
        }
        else if (OperatingSystem.IsWindows())
        {
            var app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama app.exe");
            if (!File.Exists(app)) throw new InvalidOperationException("Install Ollama using Get Ollama and complete its installer, then choose Start Ollama here.");
            start = new(app) { UseShellExecute = false };
        }
        else throw new InvalidOperationException("Install and start Ollama from ollama.com/download, then check the connection again.");
        using var process = ChildProcessLauncher.Start(start);
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            try { return await client.InventoryAsync(endpoint, ct).ConfigureAwait(false); }
            catch (HttpRequestException) { }
        }
        throw new InvalidOperationException("Ollama was opened but its server is not responding yet. Complete any Ollama setup window, then check again.");
    }
}
