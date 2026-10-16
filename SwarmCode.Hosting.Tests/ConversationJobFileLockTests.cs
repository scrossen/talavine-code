namespace SwarmCode.Hosting.Tests;

public sealed class ConversationJobFileLockTests : TempDirTest
{
    [WindowsJobFileFact]
    public void Save_survives_a_brief_reader_that_denies_atomic_replacement()
    {
        var store = new FileConversationJobStore(Paths);
        store.Save("conv", new() { State = "Idle", Request = "old" });
        var path = Path.Combine(Paths.SessionsDirectory, "jobs", "conv.json");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // The read handle is open before Save starts. A dedicated thread releases it;
        // the write must keep its new snapshot until Windows permits replacement.
        var release = new Thread(() => { Thread.Sleep(120); reader.Dispose(); });
        release.Start();
        try { store.Save("conv", new() { State = "Running", Request = "new" }); }
        finally { release.Join(); }
        Assert.Equal("new", store.Load("conv")!.Request);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [WindowsJobFileFact]
    public async Task Persistent_reader_denial_surfaces_original_error_preserves_old_snapshot_and_cleans_temp()
    {
        var store = new FileConversationJobStore(Paths);
        store.Save("conv", new() { State = "Idle", Request = "old" });
        var path = Path.Combine(Paths.SessionsDirectory, "jobs", "conv.json");
        var original = File.ReadAllBytes(path);
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => Task.Run(() => store.Save("conv", new() { Request = "new" })).WaitAsync(TimeSpan.FromSeconds(3)));
            var denied = Assert.IsType<UnauthorizedAccessException>(error);
            Assert.Equal(5, denied.HResult & 0xffff);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        Assert.Equal("old", store.Load("conv")!.Request);
    }
}

internal sealed class WindowsJobFileFactAttribute : FactAttribute
{
    public WindowsJobFileFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows destination-handle sharing semantics are required.";
    }
}
