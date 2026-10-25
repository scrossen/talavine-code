using System.Runtime.CompilerServices;

namespace SwarmCore.Testing;

/// <summary>
/// Linked into every test project by Directory.Build.targets. A test host that loads the assembly
/// moves its temp location into the owned run root, so a fixture that calls
/// <see cref="Path.GetTempPath"/> directly is still reclaimed when the run ends, or by the next
/// run's abandoned-owner recovery after a crash.
/// </summary>
internal static class TestTempIsolation
{
    [ModuleInitializer]
    internal static void Install() => OwnedTestScratch.IsolateProcessTemp();
}
