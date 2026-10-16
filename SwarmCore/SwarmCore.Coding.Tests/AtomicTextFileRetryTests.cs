using System.Text;
using SwarmCore.Agent.Continual;
using SwarmCore.Missions;
using SwarmCore.Testing;

namespace SwarmCore.Coding.Tests;

public sealed class AtomicTextFileRetryTests
{
    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Temporary_windows_lock_retries_but_a_competing_edit_is_preserved(bool competingEdit)
    {
        var root = Path.Combine(Path.GetTempPath(), "atomic-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "file.txt");
        try
        {
            await File.WriteAllTextAsync(path, "before");
            var snapshot = await AtomicTextFile.ReadAsync(path, CancellationToken.None);
            Task write;
            using (var blocker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                write = AtomicTextFile.WriteAsync(path, Encoding.UTF8.GetBytes("after"), snapshot!.Sha256, CancellationToken.None);
                await Task.Delay(30);
                Assert.False(write.IsCompleted, "The transient lock should be retried instead of failing immediately.");
                if (competingEdit)
                {
                    blocker.SetLength(0);
                    await blocker.WriteAsync(Encoding.UTF8.GetBytes("competing edit"));
                    blocker.Flush(flushToDisk: true);
                }
            }
            if (competingEdit)
            {
                await Assert.ThrowsAsync<MissionActionSkippedException>(() => write);
                Assert.Equal("competing edit", await File.ReadAllTextAsync(path));
            }
            else
            {
                await write;
                Assert.Equal("after", await File.ReadAllTextAsync(path));
            }
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
