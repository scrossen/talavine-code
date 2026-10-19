using System.Diagnostics;

namespace SwarmCode.App.Services;

/// <summary>Reveals a folder in the platform file manager.</summary>
public static class FolderLauncher
{
    public static async Task<bool> OpenAsync(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
#if WINDOWS
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            await Task.CompletedTask;
            return true;
#else
            return await DesktopPlatform.OpenFileAsync(folder);
#endif
        }
        catch (Exception ex)
        {
            DesktopHost.LogToFile($"Open folder failed: {ex.Message}");
            return false;
        }
    }
}
