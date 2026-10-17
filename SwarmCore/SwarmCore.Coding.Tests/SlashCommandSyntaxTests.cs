using SwarmCore.Coding.Extensions;

namespace SwarmCore.Coding.Tests;

public class SlashCommandSyntaxTests
{
    [Theory]
    [InlineData("/unknown", true)]
    [InlineData(" /unknown ", true)]
    [InlineData("/bug the dictate button loses focus", false)]
    [InlineData("/note\nA normal message", false)]
    [InlineData("/usr/bin/env", false)]
    [InlineData("/", false)]
    [InlineData("ordinary message", false)]
    public void Unknown_slash_sentences_remain_messages(string text, bool bare) => Assert.Equal(bare, SlashCommandSyntax.IsBareCommand(text));
}
