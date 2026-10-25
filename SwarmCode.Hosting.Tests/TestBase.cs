namespace SwarmCode.Hosting.Tests;

public abstract class TempDirTest : IDisposable
{
    protected string Tmp { get; } = SwarmCore.Missions.SandboxPathResolver.ResolvePhysicalPath(
        Talavine.TestSupport.TestTempRoot.CreateDirectory("hosting"));
    protected SwarmCodeDataPaths Paths { get; }

    protected TempDirTest()
    {
        Directory.CreateDirectory(Tmp);
        Paths = new SwarmCodeDataPaths(Path.Combine(Tmp, "data"));
    }

    public virtual void Dispose()
    {
        if (!Talavine.TestSupport.TestTempRoot.DeleteDirectory(Tmp))
            throw new IOException($"Hosting fixture did not release its scratch files: {Tmp}");
        GC.SuppressFinalize(this);
    }
}
