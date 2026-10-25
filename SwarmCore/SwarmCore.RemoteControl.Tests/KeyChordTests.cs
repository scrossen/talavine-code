namespace SwarmCore.RemoteControl.Tests;

public sealed class KeyChordTests
{
    [Theory]
    [InlineData("enter", "enter")]
    [InlineData("Return", "enter")]
    [InlineData("ctrl+s", "ctrl+s")]
    [InlineData("Control+S", "ctrl+s")]
    [InlineData("shift+ctrl+t", "ctrl+shift+t")]
    [InlineData("ctrl shift t", "ctrl+shift+t")]
    [InlineData("win+shift+alt+ctrl+x", "ctrl+alt+shift+win+x")]
    [InlineData("cmd+r", "win+r")]
    [InlineData("command-r", "win+r")]
    [InlineData("windows+e", "win+e")]
    [InlineData("esc", "escape")]
    [InlineData("del", "delete")]
    [InlineData("pgdn", "pagedown")]
    [InlineData("page down", "pagedown")]
    [InlineData("alt+F4", "alt+f4")]
    [InlineData("f24", "f24")]
    [InlineData("up arrow", "up")]
    [InlineData("numpad5", "numpad5")]
    [InlineData("ctrl++", "ctrl+plus")]
    [InlineData("ctrl+.", "ctrl+period")]
    [InlineData("ctrl+[", "ctrl+leftbracket")]
    [InlineData("win", "win")]
    [InlineData("ctrl+ctrl+a", "ctrl+a")]
    [InlineData("print screen", "printscreen")]
    [InlineData("backquote", "backquote")]
    public void Normalizes(string chord, string expected) => Assert.Equal(expected, KeyChord.Normalize(chord));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ctrl+banana")]
    [InlineData("c12")]
    [InlineData("a b")]
    public void RejectsInvalid(string chord) => Assert.Throws<ArgumentException>(() => KeyChord.Normalize(chord));
}
