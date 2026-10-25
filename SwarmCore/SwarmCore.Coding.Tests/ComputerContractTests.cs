using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.Computer;
using SwarmCore.Coding.Computer;
using SwarmCore.Coding.SelfImprovement;
using Talavine.TestSupport;

namespace SwarmCore.Coding.Tests;

public sealed class ComputerContractTests
{
    [Fact]
    public void RegularChromeIsTheDefaultAndNeverUsesTheManagedProfileToDiscoverSessions()
    {
        Assert.True(new CodingComputerOptions().UsePersonalChrome);
        if (OperatingSystem.IsWindows())
            Assert.EndsWith("Google" + Path.DirectorySeparatorChar + "Chrome" + Path.DirectorySeparatorChar + "User Data",
                PersonalChromeConnection.DefaultUserDataDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("9222\n/devtools/browser/abc-123\n", "ws://127.0.0.1:9222/devtools/browser/abc-123")]
    [InlineData(" 54321\r\n/devtools/browser/ABC-123\r\n", "ws://127.0.0.1:54321/devtools/browser/ABC-123")]
    public void DiscoversChromeConsentEndpoint(string content, string expected)
        => Assert.Equal(expected, PersonalChromeConnection.ParseActivePort(content));

    [Theory]
    [InlineData("0\n/devtools/browser/id")]
    [InlineData("65536\n/devtools/browser/id")]
    [InlineData("9222\n//evil.example/path")]
    [InlineData("9222\n/devtools/browser/id?x=1")]
    [InlineData("9222\n/devtools/browser/")]
    [InlineData("9222\n/devtools/browser/id\nextra")]
    public void RejectsMalformedOrRedirectedDiscovery(string content)
        => Assert.Throws<ArgumentException>(() => PersonalChromeConnection.ParseActivePort(content));

    [Fact]
    public void MissingChromeConnectionExplainsRequiredUserActionWithoutCreatingAProfile()
    {
        using var root = TestTempDirectory.Reserve("missing-chrome-connection");
        var error = Assert.Throws<IOException>(() => PersonalChromeConnection.GetEndpoint(root.Path));
        Assert.Contains(PersonalChromeConnection.SetupUrl, error.Message);
        Assert.IsAssignableFrom<IOException>(error.InnerException);
        Assert.False(root.Exists);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9222\n/devtools/browser/")]
    public async Task UnavailableChromeConnectionIsEnvironmentalAndDoesNotQueueASourceRepair(string? content)
    {
        using var root = TestTempDirectory.Reserve("chrome-connection");
        if (content is not null)
            await File.WriteAllTextAsync(Path.Combine(root.EnsureCreated(), "DevToolsActivePort"), content);

        var error = Record.Exception(() => { PersonalChromeConnection.GetEndpoint(root.Path); });
        Assert.NotNull(error);
        Assert.Contains(PersonalChromeConnection.SetupUrl, error.Message);
        Assert.Contains("allow Chrome's connection prompt", error.Message);
        Assert.Equal(content is not null, root.Exists);

        var backlog = new InMemorySelfImprovementBacklog();
        var reporter = new SelfImprovementReporter(backlog, () => new SelfImprovementOptions());
        Assert.Null(await reporter.ReportDefectAsync(error, "turn-error"));
        // Native MCP failures arrive at capture inside a JSON text block, as in the original sighting.
        var toolError = System.Text.Json.JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = $"{error.GetType().Name}: {error.Message}" } },
        });
        Assert.Null(await reporter.ReportErrorTextAsync(toolError, null, "tool-error"));
        Assert.Empty(await backlog.ListAsync());

        // Keep reporting actual code failures, including failures from browser code.
        Assert.NotNull(await reporter.ReportErrorTextAsync(
            "InvalidOperationException: Sequence contains no elements", null, "tool-error"));
        Assert.Single(await backlog.ListAsync());
    }
    [Theory]
    [InlineData("https://example.com:9222")]
    [InlineData("file:///C:/profile")]
    [InlineData("not a URL")]
    public void CdpRejectsNonLoopbackEndpoints(string endpoint)
        => Assert.Throws<ArgumentException>(() => ChromeComputerSession.ValidateEndpoint(endpoint));

    [Theory]
    [InlineData("http://127.0.0.1:9222")]
    [InlineData("ws://localhost:9222/devtools/browser/id")]
    public void CdpAllowsExplicitLoopbackConnections(string endpoint) => ChromeComputerSession.ValidateEndpoint(endpoint);

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(100, 50)]
    [InlineData(50, 100)]
    public void DesktopRejectsCoordinatesOutsideObservedWindow(double x, double y)
        => Assert.Throws<ArgumentException>(() => WindowsComputerSession.ValidateAction(new("click") { X = x, Y = y }, 100, 100));

    [Fact]
    public void DesktopRejectsInvalidActionsBeforeInput()
    {
        Assert.Throws<ArgumentException>(() => WindowsComputerSession.ValidateAction(new("keypress") { Keys = ["Unsupported"] }, 100, 100));
        Assert.Throws<ArgumentException>(() => WindowsComputerSession.ValidateAction(new("type") { Text = new string('x', 16001) }, 100, 100));
        Assert.Throws<ArgumentException>(() => WindowsComputerSession.ValidateAction(new("drag") { Path = [new(1, 1)] }, 100, 100));
        WindowsComputerSession.ValidateAction(new("keypress") { Keys = ["CTRL", "A"] }, 100, 100);
    }

    [Fact]
    public async Task DisabledComputerCannotListOrAct()
    {
        var options = Options.Create(new CodingComputerOptions { Enabled = false });
        await using var browser = new ChromeComputerSession(options);
        var desktop = new WindowsComputerSession(options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => browser.ListAsync());
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => desktop.ListAsync());
    }
    [SwarmCore.Testing.WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Windows_matching_observation_is_consumed_by_invalid_batch_before_any_native_call(bool invalidKeys)
    {
        var desktop = new WindowsComputerSession(Options.Create(new CodingComputerOptions { Enabled = true }));
        // Seed only the host's captured observation. Both tested branches return before Win32 calls;
        // this is a structural guard test, not proof of Windows capture or physical input.
        var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var sessionId = (string)typeof(WindowsComputerSession).GetField("sessionId", fields)!.GetValue(desktop)!;
        var current = typeof(WindowsComputerSession).GetField("current", fields)!;
        var observation = new ComputerObservation(sessionId, "0", "synthetic-observation", "", DateTimeOffset.UtcNow, 100, 100, 1);
        current.SetValue(desktop, observation);
        ComputerAction[] actions = invalidKeys ? [new("keypress") { Keys = ["unsupported"] }] : [];
        var invalid = await desktop.ExecuteBatchAsync(new(sessionId, "0", observation.ObservationId, actions));
        Assert.False(invalid.Succeeded); Assert.True(invalid.NoPhysicalAction); Assert.Equal(0, invalid.CompletedActions);
        Assert.Null(current.GetValue(desktop));
        var repeated = await desktop.ExecuteBatchAsync(new(sessionId, "0", observation.ObservationId, [new("screenshot")]));
        Assert.Equal("stale-or-missing-observation", repeated.Error); Assert.True(repeated.NoPhysicalAction);
    }

}
