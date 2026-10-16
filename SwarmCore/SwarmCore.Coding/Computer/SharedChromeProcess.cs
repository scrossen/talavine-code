using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Coding.Computer;

/// <summary>A host-profile Chrome process survives individual MCP clients, allowing API/native lanes to share tabs.</summary>
internal static class SharedChromeProcess
{
    private sealed record Connection(int Pid, long StartedUtcTicks, string Executable, string Endpoint);

    public static string GetEndpoint(CodingComputerOptions options, CancellationToken ct)
    {
        var profile = Path.GetFullPath(options.BrowserProfileDirectory);
        Directory.CreateDirectory(profile);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ToUpperInvariant())))[..24];
        using var mutex = new Mutex(false, "SwarmCodeChrome-" + hash);
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new TimeoutException("Another agent is connecting Chrome; retry shortly.");
            ct.ThrowIfCancellationRequested();
            var marker = Path.Combine(profile, "swarmcode-connection.json");
            if (File.Exists(marker))
            {
                try
                {
                    var connection = JsonSerializer.Deserialize<Connection>(File.ReadAllText(marker));
                    if (connection is not null)
                    {
                        using var process = Process.GetProcessById(connection.Pid);
                        if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == connection.StartedUtcTicks
                            && string.Equals(process.MainModule?.FileName, connection.Executable, StringComparison.OrdinalIgnoreCase))
                        {
                            ChromeComputerSession.ValidateEndpoint(connection.Endpoint);
                            return connection.Endpoint;
                        }
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException) { }
            }
            var executable = FindChrome();
            var activePortFile = Path.Combine(profile, "DevToolsActivePort");
            var oldPortWrite = File.Exists(activePortFile) ? File.GetLastWriteTimeUtc(activePortFile) : DateTime.MinValue;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var arg in new[] { "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1", "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "--new-window" }) start.ArgumentList.Add(arg);
            if (options.Headless) start.ArgumentList.Add("--headless=new");
            start.ArgumentList.Add("about:blank");
            using var launched = Process.Start(start) ?? throw new InvalidOperationException("Chrome could not start.");
            try
            {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                ct.ThrowIfCancellationRequested();
                if (launched.HasExited) throw new InvalidOperationException("Chrome profile is already open without a Talavine Code connection. Close that profile or configure its loopback CDP endpoint in Settings.");
                if (File.Exists(activePortFile) && File.GetLastWriteTimeUtc(activePortFile) != oldPortWrite)
                {
                    try
                    {
                        var first = File.ReadLines(activePortFile).FirstOrDefault();
                        if (int.TryParse(first, out var port) && port is > 0 and <= 65535)
                        {
                            var endpoint = "http://127.0.0.1:" + port;
                            var connection = new Connection(launched.Id, launched.StartTime.ToUniversalTime().Ticks, executable, endpoint);
                            var temp = marker + ".tmp";
                            File.WriteAllText(temp, JsonSerializer.Serialize(connection));
                            File.Move(temp, marker, overwrite: true);
                            return endpoint;
                        }
                    }
                    catch (IOException) { }
                }
                Thread.Sleep(100);
            }
            throw new TimeoutException("Chrome did not expose its debug connection in time.");
            }
            catch
            {
                // Only this unregistered launch belongs to this attempt. Successful shared browsers outlive clients.
                try { if (!launched.HasExited) launched.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                throw;
            }
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    internal static string FindChrome()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                var path = Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
                if (File.Exists(path)) return path;
            }
        }
        if ((OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()) && File.Exists("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome")) return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        throw new InvalidOperationException("Google Chrome is not installed. Install Chrome or configure an existing loopback Chrome connection in Settings.");
    }
}
