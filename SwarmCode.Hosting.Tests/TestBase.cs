namespace SwarmCode.Hosting.Tests;

public abstract class TempDirTest : IDisposable
{
    protected string Tmp { get; } = Path.Combine(Path.GetTempPath(), "swarmcode-hosting-tests", Guid.NewGuid().ToString("N"));
    protected SwarmCodeDataPaths Paths { get; }

    protected TempDirTest()
    {
        Directory.CreateDirectory(Tmp);
        Paths = new SwarmCodeDataPaths(Path.Combine(Tmp, "data"));
    }

    public virtual void Dispose()
    {
        try { Directory.Delete(Tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
