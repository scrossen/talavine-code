namespace SwarmCore.Mcp.ExternalClients;

/// <summary>
/// The launch coordinates an external MCP client (Claude Desktop, Cursor, Codex CLI, …)
/// stores for one stdio server: the executable, its arguments, and extra environment
/// variables. These are device-local launch coordinates, never a credential store:
/// hosts must not put secrets in <see cref="EnvironmentVariables"/>.
/// </summary>
public sealed class McpServerLaunchEntry : IEquatable<McpServerLaunchEntry>
{
    public McpServerLaunchEntry(
        string command,
        IEnumerable<string>? arguments = null,
        IEnumerable<KeyValuePair<string, string>>? environmentVariables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        Command = command;

        var args = new List<string>();
        foreach (var argument in arguments ?? [])
        {
            ArgumentNullException.ThrowIfNull(argument, nameof(arguments));
            args.Add(argument);
        }

        Arguments = args.AsReadOnly();

        var env = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in environmentVariables ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(environmentVariables));
            env[key] = value ?? string.Empty;
        }

        EnvironmentVariables = env;
    }

    public string Command { get; }

    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Extra variables in ordinal key order, so rendering is deterministic.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    /// <summary>
    /// Exact, ordinal comparison of command, argument sequence, and environment. A client
    /// entry that differs in any of these is not the entry the host would write.
    /// </summary>
    public bool Equals(McpServerLaunchEntry? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;
        if (!string.Equals(Command, other.Command, StringComparison.Ordinal))
            return false;
        if (!Arguments.SequenceEqual(other.Arguments, StringComparer.Ordinal))
            return false;
        if (EnvironmentVariables.Count != other.EnvironmentVariables.Count)
            return false;

        foreach (var (key, value) in EnvironmentVariables)
        {
            if (!other.EnvironmentVariables.TryGetValue(key, out var otherValue)
                || !string.Equals(value, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as McpServerLaunchEntry);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Command, StringComparer.Ordinal);
        foreach (var argument in Arguments)
            hash.Add(argument, StringComparer.Ordinal);
        foreach (var (key, value) in EnvironmentVariables)
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
