namespace SwarmCore.Agent.Catalog;

/// <summary>
/// Thrown by a tool's <c>Invoke</c> only before it starts any action (an authorization denial, a refused
/// precondition), so nothing outside the host can have happened. The tool-use engine records a failed tool
/// result that was not dispatched (<c>WasDispatched = false</c>) whose text is exactly the exception message
/// (no exception type name); completion and mission evidence never count it.
/// </summary>
public class ToolCallNotDispatchedException(string message) : UnauthorizedAccessException(message);
