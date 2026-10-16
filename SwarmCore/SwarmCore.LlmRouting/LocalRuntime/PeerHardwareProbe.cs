using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using SwarmCore.Abstractions.Compute;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.LlmRouting.LocalRuntime;

/// <summary>Bounded, shell-free hardware discovery, cached for five minutes. Never exposes device serials.</summary>
public sealed class PeerHardwareProbe
{
    private readonly SemaphoreSlim _gate = new(1);
    private PeerHardware? _cached;

    public async Task<PeerHardware> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } old && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - old.MeasuredUtcMs < 300_000) return old;
            return _cached = await Task.Run(() => DetectAsync(ct), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static async Task<PeerHardware> DetectAsync(CancellationToken ct)
    {
        string? cpu = null;
        long? memory = null;
        List<PeerGpu> gpus = [];
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            cpu = await RunAsync("/usr/sbin/sysctl", ["-n", "machdep.cpu.brand_string"], ct).ConfigureAwait(false);
            memory = Number(await RunAsync("/usr/sbin/sysctl", ["-n", "hw.memsize"], ct).ConfigureAwait(false));
            var displays = await RunAsync("/usr/sbin/system_profiler", ["SPDisplaysDataType", "-json"], ct).ConfigureAwait(false);
            try
            {
                using var json = JsonDocument.Parse(displays ?? "{}");
                if (json.RootElement.TryGetProperty("SPDisplaysDataType", out var list))
                    foreach (var gpu in list.EnumerateArray())
                    {
                        var name = gpu.TryGetProperty("sppci_model", out var model) ? model.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(name)) gpus.Add(new(name, UnifiedMemory: RuntimeInformation.OSArchitecture == Architecture.Arm64));
                    }
            }
            catch (JsonException) { }
        }
        else if (OperatingSystem.IsWindows())
        {
            // These inexpensive OS reads must survive a cold or unavailable CIM service.
            // PowerShell startup alone can exceed the budget on a busy peer.
            (cpu, memory) = ReadWindowsSystem();
            // CIM JSON avoids localized table parsing. AdapterRAM is a 32-bit legacy field and is not reliable VRAM evidence.
            var data = await RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
                "@{cpu=(Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name);memory=(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory;gpus=@(Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name)} | ConvertTo-Json -Compress"], ct, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            try
            {
                using var json = JsonDocument.Parse(data ?? "{}");
                if (string.IsNullOrWhiteSpace(cpu) && json.RootElement.TryGetProperty("cpu", out var c)) cpu = c.GetString();
                if (memory is null && json.RootElement.TryGetProperty("memory", out var m) && m.TryGetInt64(out var bytes)) memory = bytes;
                if (json.RootElement.TryGetProperty("gpus", out var list))
                    foreach (var gpu in list.EnumerateArray())
                        if (gpu.GetString() is { Length: > 0 } name) gpus.Add(new(name));
            }
            catch (JsonException) { }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                var mem = await File.ReadAllLinesAsync("/proc/meminfo", ct).ConfigureAwait(false);
                var kb = mem.FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.Ordinal))?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
                if (Number(kb) is { } count) memory = count * 1024;
                cpu = (await File.ReadAllLinesAsync("/proc/cpuinfo", ct).ConfigureAwait(false))
                    .FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2).Last().Trim();
            }
            catch (IOException) { }
        }
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsMacCatalyst())
        {
            var nvidia = await RunAsync("nvidia-smi", ["--query-gpu=name,memory.total", "--format=csv,noheader,nounits"], ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(nvidia))
            {
                var detected = ParseNvidia(nvidia);
                foreach (var gpu in detected)
                {
                    gpus.RemoveAll(g => g.Name.Equals(gpu.Name, StringComparison.OrdinalIgnoreCase));
                    gpus.Add(gpu);
                }
            }
        }
        return new() { CpuName = cpu, TotalMemoryBytes = memory, LogicalProcessors = Environment.ProcessorCount,
            Gpus = gpus, MeasuredUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
    }

    internal static IReadOnlyList<PeerGpu> ParseNvidia(string csv)
        => csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(','))
            .Where(parts => parts.Length == 2 && Number(parts[1]) is > 0)
            .Select(parts => new PeerGpu(parts[0].Trim(), checked(Number(parts[1])!.Value * 1024 * 1024))).ToArray();

    private static long? Number(string? text) => long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    [SupportedOSPlatform("windows")]
    private static (string? Cpu, long? Memory) ReadWindowsSystem()
    {
        string? cpu = null;
        try { cpu = (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string)?.Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        var status = new MemoryStatus();
        long? memory = GlobalMemoryStatusEx(status) && status.TotalPhysical is > 0 and <= long.MaxValue ? (long)status.TotalPhysical : null;
        return (cpu, memory);
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatus>();
        public uint Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);

    private static async Task<string?> RunAsync(string executable, string[] args, CancellationToken ct, TimeSpan? budget = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget ?? TimeSpan.FromSeconds(3));
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = ChildProcessLauncher.Start(start);
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                return process.ExitCode == 0 ? (await stdout.ConfigureAwait(false)).Trim() : null;
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }
}
