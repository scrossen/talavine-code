using SwarmCore.RemoteControl.Mac.Mapping;

namespace SwarmCore.RemoteControl.Mac.Tests;

public class AxFingerprintTests
{
    [Fact]
    public void SameElementMatches()
    {
        var issued = AxFingerprint.Create("AXRow", "AXTableRow", "Invoice 42", "row-42");
        Assert.True(AxFingerprint.Matches(issued, AxFingerprint.Create("AXRow", "AXTableRow", "Invoice 42", "row-42")));
    }

    [Fact]
    public void RowInsertedAboveShiftsPathAndIsRejected()
    {
        // ax:1:2:0.1.3 was "Invoice 42"; after a row is inserted above, the same path holds "Invoice 41".
        var issued = AxFingerprint.Create("AXRow", null, "Invoice 42", null);
        var nowAtPath = AxFingerprint.Create("AXRow", null, "Invoice 41", null);
        Assert.False(AxFingerprint.Matches(issued, nowAtPath));
    }

    [Theory]
    [InlineData("AXButton", null, "Save", "save-id")]   // role changed
    [InlineData("AXCheckBox", "AXSwitch", "Save", "save-id")] // subrole changed
    [InlineData("AXCheckBox", null, "Save", "other-id")] // identifier changed
    public void AnyIdentityPartChangeIsRejected(string role, string? subrole, string name, string id)
    {
        var issued = AxFingerprint.Create("AXCheckBox", null, "Save", "save-id");
        Assert.False(AxFingerprint.Matches(issued, AxFingerprint.Create(role, subrole, name, id)));
    }

    [Fact]
    public void FieldsDoNotRunTogether() =>
        Assert.NotEqual(AxFingerprint.Create("AXButton", "a", "b", null), AxFingerprint.Create("AXButton", "ab", "", null));

    [Fact]
    public void GoneElementNeverMatchesAndNoExpectationAcceptsLive()
    {
        Assert.False(AxFingerprint.Matches(AxFingerprint.Create("AXButton", null, "OK", null), null));
        Assert.False(AxFingerprint.Matches(null, null));
        Assert.True(AxFingerprint.Matches(null, AxFingerprint.Create("AXButton", null, "OK", null)));
    }
}

public class AxReadOnlyRuleTests
{
    [Theory]
    [InlineData("checkbox")]
    [InlineData("radiobutton")]
    [InlineData("button")]
    [InlineData("slider")]
    [InlineData("text")]
    public void NonTextControlsAreNeverReadOnly(string type) => Assert.False(AxReadOnlyRule.IsReadOnly(type, hasValue: true, valueSettable: false));

    [Theory]
    [InlineData("edit")]
    [InlineData("document")]
    [InlineData("combobox")]
    public void TextAndChoiceControlsWithUnsettableValueAreReadOnly(string type) =>
        Assert.True(AxReadOnlyRule.IsReadOnly(type, hasValue: true, valueSettable: false));

    [Fact]
    public void SettableOrValuelessEditIsNotReadOnly()
    {
        Assert.False(AxReadOnlyRule.IsReadOnly("edit", hasValue: true, valueSettable: true));
        Assert.False(AxReadOnlyRule.IsReadOnly("edit", hasValue: false, valueSettable: false));
    }
}

public class MacModifierNameTests
{
    [Theory]
    [InlineData("primary+a")]
    [InlineData("mod+a")]
    [InlineData("cmdorctrl+a")]
    [InlineData("win+a")]
    public void PrimarySynonymsMeanCommand(string chord)
    {
        var parsed = MacKeyMap.Parse(chord);
        Assert.Equal(MacModifiers.Command, parsed.Modifiers);
        Assert.Equal((ushort)0, parsed.KeyCode);
    }

    [Fact]
    public void ClickModifiersParse()
    {
        Assert.Equal(MacModifiers.None, MacKeyMap.ParseModifiers(null));
        Assert.Equal(MacModifiers.None, MacKeyMap.ParseModifiers(" "));
        Assert.Equal(MacModifiers.Shift, MacKeyMap.ParseModifiers("shift"));
        Assert.Equal(MacModifiers.Control | MacModifiers.Shift, MacKeyMap.ParseModifiers("ctrl+shift"));
        Assert.Equal(MacModifiers.Option, MacKeyMap.ParseModifiers("alt"));
        Assert.Equal(MacModifiers.Command, MacKeyMap.ParseModifiers("primary"));
    }

    [Fact]
    public void ClickModifiersRejectNonModifierKeys() => Assert.Throws<ArgumentException>(() => MacKeyMap.ParseModifiers("shift+a"));
}
