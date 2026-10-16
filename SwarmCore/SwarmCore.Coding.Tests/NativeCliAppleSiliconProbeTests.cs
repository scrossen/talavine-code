using SwarmCore.Coding.Native;
using SwarmCore.Testing;

namespace SwarmCore.Coding.Tests;

public sealed class NativeCliAppleSiliconProbeTests
{
    [AppleSiliconFact]
    public void Intel_only_cli_does_not_advertise_support_or_abort_selection()
    {
        using var fixture = new AppleSiliconProbeFixture();
        Assert.False(NativeCliCapabilities.Supports(fixture.IntelOnly, "--restricted"));
        Assert.Null(NativeCliCapabilities.ResolveExecutable([fixture.IntelOnly]));
        Assert.Equal(fixture.WorkingScript,
            NativeCliCapabilities.ResolveExecutable([fixture.IntelOnly, fixture.WorkingScript]));
        Assert.True(NativeCliCapabilities.Supports(fixture.WorkingScript, "--restricted"));
    }
}
