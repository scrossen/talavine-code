using Bunit;
using SwarmCode.App.Tests.Components;
using SwarmCore.Coding.Routing;

namespace SwarmCode.App.Tests;

[Trait("LlmMode", "Mock")]
public sealed class ThinkingPickerTests
{
    [Fact]
    public void Newly_discovered_models_use_their_catalog_levels_and_keep_the_exact_model_on_change()
    {
        using var context = new BunitContext();
        string? changed = null;
        var picker = context.Render<ReasoningPicker>(p => p
            .Add(c => c.Model, "codex-cli:future-model@high")
            .Add(c => c.CatalogProvider, "CodexCli")
            .Add(c => c.Models, new SwarmCore.Abstractions.LlmRouting.LlmCatalogModel[]
            {
                new("codex-cli:future-model", null, true, null, null, null) { ReasoningEfforts = ["low", "high", "xhigh"] }
            })
            .Add(c => c.ModelChanged, value => changed = value));
        Assert.Equal("High", picker.Find("input").GetAttribute("aria-valuetext"));
        picker.Find("input").Change("3");
        Assert.Equal("codex-cli:future-model@xhigh", changed);
        picker.Find("button").Click();
        Assert.Equal("codex-cli:future-model", changed);
    }

    [Fact]
    public void Provider_menu_changes_route_and_can_return_to_automatic_without_native_selects()
    {
        using var context = new BunitContext();
        context.JSInterop.SetupVoid("Blazor._internal.domWrapper.focus", _ => true).SetVoidResult();
        string? selected = null;
        var picker = context.Render<ProviderPicker>(p => p
            .Add(c => c.IncludeAutomatic, true)
            .Add(c => c.Lanes, new CodingLane[] { new("first", "provider-a", "one"), new("second", "provider-b", "two") })
            .Add(c => c.ValueChanged, value => selected = value));
        picker.Find(".provider-picker__toggle").Click();
        picker.FindAll(".provider-picker__menu button")[2].Click();
        Assert.Equal("second", selected);
        Assert.Empty(picker.FindAll(".provider-picker__menu"));
        picker.Render(p => p.Add(c => c.Value, "second"));
        Assert.Contains("second", picker.Find(".provider-picker__toggle").TextContent);
        picker.Find(".provider-picker__toggle").Click();
        picker.FindAll(".provider-picker__menu button")[0].Click();
        Assert.Equal("", selected);
        Assert.Empty(picker.FindAll("select"));
    }

    [Fact]
    public void Slider_changes_thinking_without_changing_the_model_and_reset_removes_the_override()
    {
        using var context = new BunitContext();
        string? changed = null;
        var picker = context.Render<ReasoningPicker>(p => p
            .Add(c => c.Model, "o3@high")
            .Add(c => c.Lane, new CodingLane("api", "OpenAI", "o3"))
            .Add(c => c.ModelChanged, value => changed = value));
        Assert.Equal("High", picker.Find("input").GetAttribute("aria-valuetext"));
        picker.Find("input").Change("1");
        Assert.Equal("o3@low", changed);
        picker.Find("button").Click();
        Assert.Equal("o3", changed);
    }

    [Fact]
    public void Unsupported_routes_remain_visible_and_do_not_offer_an_ineffective_control()
    {
        using var context = new BunitContext();
        var picker = context.Render<ReasoningPicker>(p => p
            .Add(c => c.Model, "community/model@revision")
            .Add(c => c.Lane, new CodingLane("custom", "custom-provider", "community/model@revision")));
        Assert.Contains("Thinking level", picker.Markup);
        Assert.Contains("Provider controlled", picker.Markup);
        Assert.Empty(picker.FindAll("input"));
        Assert.Contains("Choose another model or provider", picker.Markup);
    }

    [Fact]
    public void Provider_or_model_switch_replaces_the_supported_levels_and_disabled_changes_are_ignored()
    {
        using var context = new BunitContext();
        string? changed = null;
        var picker = context.Render<ReasoningPicker>(p => p
            .Add(c => c.Model, "o3")
            .Add(c => c.CatalogProvider, "OpenAI")
            .Add(c => c.Disabled, true)
            .Add(c => c.ModelChanged, value => changed = value));
        picker.Find("input").Change("3");
        Assert.Null(changed);
        picker.Render(p => p.Add(c => c.Model, "custom").Add(c => c.CatalogProvider, "custom"));
        Assert.Empty(picker.FindAll("input"));
    }
}
