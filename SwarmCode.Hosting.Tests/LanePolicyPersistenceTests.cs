using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class LanePolicyPersistenceTests
{
    [Fact]
    public void Saving_attempt_limit_with_detected_lanes_preserves_policy_and_unrelated_settings()
    {
        var root = Path.Combine(Path.GetTempPath(), "lane-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new SwarmCodeDataPaths(root);
            var writer = new UserSettingsFile(paths);
            writer.WriteValue("Other", "Keep", "retained");
            writer.WriteLanes(new CodingLaneOptions { MaxLanesPerRequest = 4 });
            var reopened = new UserSettingsFile(paths);
            Assert.Equal(4, reopened.ReadLanes().MaxLanesPerRequest);
            Assert.Empty(reopened.ReadLanes().Lanes);
            Assert.Equal("retained", reopened.ReadValue("Other", "Keep"));
            reopened.WriteLanes(new CodingLaneOptions { HandOffOnRefusal = false });
            Assert.False(new UserSettingsFile(paths).ReadLanes().HandOffOnRefusal);
            Assert.Equal("retained", reopened.ReadValue("Other", "Keep"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
