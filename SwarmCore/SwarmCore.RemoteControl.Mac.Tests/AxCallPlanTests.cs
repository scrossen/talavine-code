using SwarmCore.RemoteControl.Mac.Mapping;

namespace SwarmCore.RemoteControl.Mac.Tests;

public class AxCallPlanTests
{
    private static AxCallPlan PlanFor(string role, string? subrole = null, string? parentRole = null) =>
        AxCallPlan.For(role, AxRoleMap.Map(role, subrole, parentRole));

    [Theory]
    [InlineData("AXStaticText")]
    [InlineData("AXHeading")]
    [InlineData("AXImage")]
    [InlineData("AXGroup")]
    [InlineData("AXWebArea")]
    [InlineData("AXScrollArea")]
    [InlineData("AXSplitGroup")]
    [InlineData("AXLayoutArea")]
    [InlineData("AXToolbar")]
    [InlineData("AXUnknown")]
    [InlineData("AXWindow")]
    [InlineData("AXSomethingNew")]
    public void StructuralAndDisplayElementsCostNoFollowUpCalls(string role)
    {
        var plan = PlanFor(role);
        Assert.Equal(0, plan.ExtraCalls);
        Assert.False(plan.ActionNames);
        Assert.False(plan.TitleElement);
    }

    [Fact]
    public void ButtonsAndLinksReadActionsAndFocusOnly()
    {
        foreach (var role in new[] { "AXButton", "AXLink" })
        {
            var plan = PlanFor(role);
            Assert.True(plan.ActionNames);
            Assert.True(plan.FocusedSettable);
            Assert.False(plan.ValueSettable);
            Assert.False(plan.SelectedSettable);
            Assert.False(plan.ExpandedSettable);
            Assert.Equal(2, plan.ExtraCalls);
        }
    }

    [Fact]
    public void TextFieldsCheckValueFocusAndActions()
    {
        var plan = PlanFor("AXTextField");
        Assert.True(plan.ValueSettable);
        Assert.True(plan.FocusedSettable);
        Assert.True(plan.ActionNames); // AXConfirm
        Assert.True(plan.TitleElement);
        Assert.Equal(3, plan.ExtraCalls);
    }

    [Fact]
    public void TextAreaDocumentChecksValueButNotActions()
    {
        var plan = PlanFor("AXTextArea", "AXDocument");
        Assert.True(plan.ValueSettable);
        Assert.True(plan.FocusedSettable);
        Assert.False(plan.ActionNames);
    }

    [Fact]
    public void RowsAndTabsCheckSelection()
    {
        Assert.True(PlanFor("AXRow").SelectedSettable);
        Assert.True(PlanFor("AXCell").SelectedSettable);
        Assert.True(PlanFor("AXRadioButton", "AXTabButton").SelectedSettable);
        Assert.False(PlanFor("AXButton").SelectedSettable);
    }

    [Fact]
    public void ExpandableRolesCheckExpanded()
    {
        Assert.True(PlanFor("AXDisclosureTriangle").ExpandedSettable);
        Assert.True(PlanFor("AXPopUpButton").ExpandedSettable);
        Assert.True(PlanFor("AXComboBox").ExpandedSettable);
        Assert.True(PlanFor("AXRow", null, "AXOutline").ExpandedSettable);
        Assert.False(PlanFor("AXRow", null, "AXTable").ExpandedSettable);
        Assert.False(PlanFor("AXCheckBox").ExpandedSettable);
    }

    [Fact]
    public void CheckboxesAndSlidersCheckValue()
    {
        Assert.True(PlanFor("AXCheckBox").ValueSettable);
        Assert.True(PlanFor("AXRadioButton").ValueSettable);
        Assert.True(PlanFor("AXSlider").ValueSettable);
        Assert.False(PlanFor("AXButton").ValueSettable);
        Assert.False(PlanFor("AXLink").ValueSettable);
    }

    [Fact]
    public void FullPlanAsksEverything() => Assert.Equal(5, AxCallPlan.Full.ExtraCalls);

    [Theory]
    [InlineData("AXTable", true)]
    [InlineData("AXOutline", true)]
    [InlineData("AXList", true)]
    [InlineData("AXGroup", false)]
    [InlineData("AXStaticText", false)]
    [InlineData("AXButton", false)]
    public void VisibleChildrenFallbackOnlyForCollections(string role, bool expected) =>
        Assert.Equal(expected, AxCallPlan.UsesVisibleChildrenFallback(role));

    [Fact]
    public void WalkStatsSummarizeCallsPerElement()
    {
        var stats = new MacWalkStats(1000, 12, 1040, TimeSpan.FromMilliseconds(850));
        Assert.Equal(1.04, stats.CallsPerElement, 6);
        Assert.Contains("1000 elements read", stats.ToString());
        Assert.Contains("1040 AX calls", stats.ToString());
        Assert.Equal(0, new MacWalkStats(0, 0, 0, TimeSpan.Zero).CallsPerElement);
    }
}

public class AxTypeFilterTests
{
    [Fact]
    public void NoFilterAcceptsEverything()
    {
        var f = AxTypeFilter.Create(null, null);
        Assert.False(f.IsActive);
        Assert.True(f.Accepts("group"));
    }

    [Fact]
    public void SetFilterAcceptsOnlyListedTypesCaseInsensitive()
    {
        var f = AxTypeFilter.Create(null, ["edit", "Combobox", "button", "splitbutton", "hyperlink"]);
        Assert.True(f.IsActive);
        Assert.True(f.Accepts("edit"));
        Assert.True(f.Accepts("combobox"));
        Assert.True(f.Accepts("HYPERLINK"));
        Assert.False(f.Accepts("group"));
        Assert.False(f.Accepts("text"));
    }

    [Fact]
    public void SingleAndSetMustBothMatch()
    {
        var f = AxTypeFilter.Create("button", ["button", "edit"]);
        Assert.True(f.Accepts("button"));
        Assert.False(f.Accepts("edit"));
        Assert.True(AxTypeFilter.Create("edit", null).Accepts("EDIT"));
        Assert.False(AxTypeFilter.Create(" ", null).IsActive);
    }

    [Fact]
    public void EmptySetAcceptsNothing() => Assert.False(AxTypeFilter.Create(null, []).Accepts("button"));
}
