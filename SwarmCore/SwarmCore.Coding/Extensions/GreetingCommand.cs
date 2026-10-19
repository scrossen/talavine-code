namespace SwarmCore.Coding.Extensions;

/// <summary>A local welcome shared by coding hosts; no model or account is needed.</summary>
public static class GreetingCommand
{
    public const string Name = "greeting";
    public const string Description = "Show a welcome message";
    public const string Message = "Hello! Tell me what you'd like to work on, or type /help to see the available commands.";
}
