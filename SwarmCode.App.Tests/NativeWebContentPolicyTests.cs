using SwarmCode.App.Services;

namespace SwarmCode.App.Tests;

public sealed class NativeWebContentPolicyTests
{
    [Theory]
    [InlineData("app://swarmcode/")]
    [InlineData("app://swarmcode/settings")]
    [InlineData("app://swarmcode/session/synthetic-id?view=changes#artifact")]
    public void BundledRoutesCanUseTheRenderer(string url)
        => Assert.True(NativeWebContentPolicy.IsAppUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("/settings")]
    [InlineData("https://swarmcode/settings")]
    [InlineData("app://swarmcode.attacker.invalid/")]
    [InlineData("app://swarmcode@attacker.invalid/")]
    [InlineData("app://attacker@swarmcode/")]
    [InlineData("app://swarmcode:1234/")]
    [InlineData("file:///settings")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,untrusted")]
    public void UntrustedDocumentsCannotReachTheManagedRenderer(string? url)
        => Assert.False(NativeWebContentPolicy.IsAppUrl(url));
    [Theory]
    [InlineData("about:blank")]
    [InlineData("about:srcdoc")]
    public void SandboxedArtifactFramesCanRenderWithoutAdmittingThemToTheRenderer(string url)
    {
        Assert.True(NativeWebContentPolicy.CanNavigateInView(url, isMainFrame: false));
        Assert.False(NativeWebContentPolicy.CanNavigateInView(url, isMainFrame: true));
        Assert.False(NativeWebContentPolicy.IsAppUrl(url));
    }

    [Fact]
    public void ExternalDocumentsCannotNavigateInsideAnyFrame()
    {
        Assert.False(NativeWebContentPolicy.CanNavigateInView("https://example.com", isMainFrame: false));
        Assert.False(NativeWebContentPolicy.CanNavigateInView("https://example.com", isMainFrame: true));
    }
}
