namespace SwarmCore.Coding.Extensions;

/// <summary>Unknown bare slash names are commands; paths and slash-prefixed sentences are messages.</summary>
public static class SlashCommandSyntax
{
    public static bool IsBareCommand(string text)
    {
        var value = text.Trim();
        return value.Length > 1 && value[0] == '/' && !value[1..].Contains('/') && !value.Any(char.IsWhiteSpace);
    }
}
