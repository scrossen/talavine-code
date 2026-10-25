using SwarmCore.RemoteControl;
using SwarmCore.RemoteControl.Mac.Mapping;

namespace SwarmCore.RemoteControl.Mac.Tests;

public class AxControlFilterTests
{
    private static string Type(string role, string? subrole = null) => AxRoleMap.Map(role, subrole);

    [Fact]
    public void NamelessEmptyTextAreaIsKept() =>
        Assert.True(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXTextArea"), "", "", [ControlAction.SetValue, ControlAction.Focus]));

    [Fact]
    public void NamelessTextAreaIsKeptEvenWithoutSettableValue() =>
        Assert.True(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXTextArea"), "", null, []));

    [Fact]
    public void NamelessGroupIsDropped() =>
        Assert.False(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXGroup"), "", null, []));

    [Fact]
    public void NamelessScrollAreaAndToolbarAreDropped()
    {
        Assert.False(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXScrollArea"), "", null, [ControlAction.Focus]));
        Assert.False(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXToolbar"), null, null, []));
        Assert.False(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXUnknown"), null, null, []));
    }

    [Fact]
    public void NamelessImageIsDropped() =>
        Assert.False(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXImage"), "", null, []));

    [Fact]
    public void EmptyStaticTextIsDropped() =>
        Assert.False(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXStaticText"), "", null, []));

    [Fact]
    public void NamelessButtonIsKept() =>
        Assert.True(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXButton"), "", null, [ControlAction.Invoke]));

    [Fact]
    public void NamelessPressableOrSettableCustomElementIsKept()
    {
        Assert.True(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXGroup"), "", null, [ControlAction.Invoke]));
        Assert.True(AxControlFilter.KeepWhenUnnamedExcluded("custom", "", null, [ControlAction.SetValue]));
    }

    [Fact]
    public void NamedOrValuedStructuralElementsAreKept()
    {
        Assert.True(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXGroup"), "Sidebar", null, []));
        Assert.True(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXStaticText"), "", "Hello", []));
        Assert.True(AxControlFilter.KeepWhenUnnamedExcluded(Type("AXImage"), "Logo", null, []));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("document")]
    [InlineData("combobox")]
    [InlineData("button")]
    [InlineData("checkbox")]
    [InlineData("radiobutton")]
    [InlineData("menuitem")]
    [InlineData("tabitem")]
    [InlineData("listitem")]
    [InlineData("dataitem")]
    [InlineData("treeitem")]
    [InlineData("slider")]
    [InlineData("spinner")]
    [InlineData("hyperlink")]
    public void InteractiveTypesAreAlwaysKept(string type) =>
        Assert.True(AxControlFilter.KeepWhenUnnamedExcluded(type, null, null, []));
}
