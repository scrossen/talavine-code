using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SwarmCode.App.Tests.Components;
using SwarmCore.Coding.Routing;

namespace SwarmCode.App.Tests;

public sealed class ProviderPopupTests
{
    [Fact]
    public void Closing_the_host_during_focus_does_not_fault_the_provider_renderer()
    {
        using var context = new BunitContext();
        context.JSInterop.SetupVoid("Blazor._internal.domWrapper.focus", _ => true)
            .SetException(new JSDisconnectedException("Host closed"));
        var picker = context.Render<ProviderPicker>();
        picker.Find(".provider-picker__toggle").Click();
        picker.Find(".provider-picker").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(picker.FindAll(".provider-picker__menu"));
        Assert.Equal("false", picker.Find(".provider-picker__toggle").GetAttribute("aria-expanded"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Accessibility_activation_claims_focus_and_escape_selection_and_reopening_keep_the_control_alive(
        bool automatic, bool above)
    {
        using var context = new BunitContext();
        context.JSInterop.SetupVoid("Blazor._internal.domWrapper.focus", _ => true).SetVoidResult();
        var selections = new List<string>();
        var picker = context.Render<ProviderPicker>(p => p
            .Add(c => c.Value, "first")
            .Add(c => c.IncludeAutomatic, automatic)
            .Add(c => c.OpenAbove, above)
            .Add(c => c.Lanes, new CodingLane[] { new("first", "a", "one"), new("second", "b", "two") })
            .Add(c => c.ValueChanged, value => selections.Add(value)));

        for (var cycle = 0; cycle < 3; cycle++)
        {
            picker.Find(".provider-picker__toggle").Click();
            Assert.Equal(cycle * 4 + 1, context.JSInterop.Invocations.Count);
            Assert.Equal("true", picker.Find(".provider-picker__toggle").GetAttribute("aria-expanded"));
            Assert.Single(picker.FindAll("[role=group]"));

            picker.Find(".provider-picker").KeyDown(new KeyboardEventArgs { Key = "Escape" });
            Assert.Empty(picker.FindAll(".provider-picker__menu"));
            Assert.Equal(cycle * 4 + 2, context.JSInterop.Invocations.Count);
            Assert.Equal(cycle, selections.Count);

            picker.Find(".provider-picker__toggle").Click();
            picker.FindAll(".provider-picker__menu button")[^1].Click();
            Assert.Equal("second", selections[^1]);
            Assert.Empty(picker.FindAll(".provider-picker__menu"));
            Assert.Equal("false", picker.Find(".provider-picker__toggle").GetAttribute("aria-expanded"));
            Assert.Equal(cycle * 4 + 4, context.JSInterop.Invocations.Count);
            Assert.Empty(picker.FindAll("select"));
        }
    }
}
