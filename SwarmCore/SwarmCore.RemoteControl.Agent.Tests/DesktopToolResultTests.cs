using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.RemoteControl.Agent.Tests;

/// <summary>What the desktop tools hand a model: only downscaled screenshots, and compact listings with a "more" hint.</summary>
public sealed class DesktopToolResultTests : IDisposable
{
    private const long App = 400;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "desktop-tool-result-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private DesktopControlTools Tools(ShopDesktop desktop, RemoteControlSessionOptions? options = null) =>
        new(new RemoteControlSession(desktop, (options ?? DesktopControlTools.EngineSessionOptions(_dir)) with { UserIdleRequired = TimeSpan.Zero }));

    private static DesktopToolResult Invoke(DesktopControlTools tools, string toolId, JsonObject args)
    {
        var descriptor = tools.Descriptors.Single(d => d.ToolId == toolId);
        var input = JsonSerializer.Deserialize(args, descriptor.InputType, DesktopControlTools.Json)!;
        return (DesktopToolResult)descriptor.Invoke(input, CancellationToken.None).GetAwaiter().GetResult()!;
    }

    /// <summary>70 text rows then 30 buttons, one with a very long name.</summary>
    private static ShopDesktop BigPage()
    {
        var d = new ShopDesktop();
        d.AddWindow(App, "browser", "A long page", foreground: true);
        for (var i = 0; i < 70; i++) d.Add(App, "text", $"Paragraph {i}");
        for (var i = 0; i < 29; i++) d.Add(App, "button", $"Button {i}");
        d.Add(App, "button", "Buy " + new string('x', 200));
        return d;
    }

    [Fact]
    public void CaptureExposesOnlyTheScaledImageAndItsCoordinateMapping()
    {
        var desktop = BigPage();
        byte[] scaledBytes = [1, 2, 3, 4];
        var tools = Tools(desktop, DesktopControlTools.EngineSessionOptions(_dir, (png, maxSide) => (scaledBytes, "image/jpeg", 600, 400, 0.5)));

        var result = Invoke(tools, DesktopControlTools.CaptureTool, new() { ["target"] = "window browser" });

        Assert.True(result.Ok, result.Error);
        var image = Assert.Single(((IToolResultImages)result).Images);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(Convert.ToBase64String(scaledBytes), image.Base64Data);
        var json = JsonSerializer.Serialize(result, DesktopControlTools.Json);
        Assert.DoesNotContain("pngBase64", json);
        Assert.DoesNotContain(image.Base64Data, json);
        Assert.Equal(0.5, result.Data!["imageScale"]!.GetValue<double>());
        Assert.Equal(600, result.Data["imageWidth"]!.GetValue<int>());
        Assert.Equal(400, result.Data["imageHeight"]!.GetValue<int>());
        Assert.NotNull(result.Data["screenBounds"]);
        Assert.Contains("scale 0.5", result.Text);
    }

    [Fact]
    public void CaptureWithoutAScalerReturnsNoImageRatherThanTheFullCapture()
    {
        var desktop = BigPage();
        // Even a session that embeds the full PNG must not hand it to the model.
        var tools = Tools(desktop, new RemoteControlSessionOptions { CaptureDirectory = _dir, EmbedScreenshotBase64 = true });

        var result = Invoke(tools, DesktopControlTools.CaptureTool, new() { ["target"] = "window browser" });

        Assert.True(result.Ok, result.Error);
        Assert.Empty(((IToolResultImages)result).Images);
        Assert.DoesNotContain("pngBase64", JsonSerializer.Serialize(result, DesktopControlTools.Json));
        Assert.Contains("No image attached", result.Text);
        Assert.Empty(((IToolResultImages)Invoke(Tools(BigPage()), DesktopControlTools.CaptureTool, new() { ["target"] = "all" })).Images);
    }

    [Fact]
    public void InspectCutsToSixtyRowsInteractiveFirstWithAHint()
    {
        var result = Invoke(Tools(BigPage()), DesktopControlTools.InspectTool, new() { ["window"] = "browser" });

        Assert.True(result.Ok, result.Error);
        var rows = result.Text.Split('\n').Skip(1).Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^c\d+ ")).ToArray();
        Assert.Equal(60, rows.Length);
        Assert.Equal(30, rows.Count(r => r.Contains(" button ")));
        Assert.Contains("40 more controls not shown; narrow with controlType or nameContains", result.Text);
        var buy = rows.Single(r => r.Contains("Buy "));
        Assert.Contains("…", buy);
        Assert.DoesNotContain(new string('x', 100), buy);
        Assert.Equal(60, result.Data!["controls"]!.AsArray().Count);
        Assert.Equal(100, result.Data["total"]!.GetValue<int>());
        Assert.Equal(40, result.Data["more"]!.GetValue<int>());
    }

    [Fact]
    public void InspectHonorsMaxRowsInteractiveOnlyAndRejectsZeroRows()
    {
        var tools = Tools(BigPage());

        var five = Invoke(tools, DesktopControlTools.InspectTool, new() { ["window"] = "browser", ["maxRows"] = 5 });
        Assert.Equal(5, five.Data!["controls"]!.AsArray().Count);
        Assert.Contains("95 more controls not shown", five.Text);

        var interactive = Invoke(tools, DesktopControlTools.InspectTool, new() { ["window"] = "browser", ["interactiveOnly"] = true });
        Assert.Equal(30, interactive.Data!["total"]!.GetValue<int>());
        Assert.All(interactive.Data["controls"]!.AsArray(), c => Assert.Equal("button", c!["control"]!["controlType"]!.GetValue<string>()));
        Assert.DoesNotContain("more controls not shown", interactive.Text);

        var filtered = Invoke(tools, DesktopControlTools.InspectTool, new() { ["window"] = "browser", ["controlType"] = "button", ["maxRows"] = 3 });
        Assert.Equal(3, filtered.Data!["controls"]!.AsArray().Count);
        Assert.Contains("27 more controls not shown", filtered.Text);

        var zero = Invoke(tools, DesktopControlTools.InspectTool, new() { ["window"] = "browser", ["maxRows"] = 0 });
        Assert.False(zero.Ok);
        Assert.Equal(FailureCategories.NotUnderstood, zero.FailureCategory);

        // The new arguments are durable constants a saved procedure may keep.
        var compiler = new DesktopProcedureCompiler([]);
        Assert.Null(compiler.Explain(DesktopControlTools.InspectTool, "/maxRows", JsonValue.Create(5), new JsonObject()));
        Assert.Null(compiler.Explain(DesktopControlTools.InspectTool, "/interactiveOnly", JsonValue.Create(true), new JsonObject()));
    }

    [Fact]
    public void ListWindowsIsCompactAndBounded()
    {
        var desktop = new ShopDesktop();
        for (var i = 0; i < 70; i++) desktop.AddWindow(1000 + i, "app" + i, "Window " + i + " " + new string('t', 150), foreground: i == 0);

        var result = Invoke(Tools(desktop), DesktopControlTools.ListWindowsTool, new());

        Assert.True(result.Ok, result.Error);
        Assert.Contains("10 more windows not shown", result.Text);
        Assert.Equal(60, result.Data!["windows"]!.AsArray().Count);
        Assert.Equal(70, result.Data["total"]!.GetValue<int>());
        Assert.DoesNotContain(new string('t', 100), result.Text);
    }
}
