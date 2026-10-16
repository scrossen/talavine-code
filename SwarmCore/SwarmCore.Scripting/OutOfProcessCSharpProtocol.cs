namespace SwarmCore.Scripting;

/// <summary>
/// Versioned stdin/stdout contract for the bundled Roslyn execution helper.
/// Keeping the contract in SwarmCore lets every desktop product use the same
/// SDK-free compiler host without sharing an installed application.
/// </summary>
public static class OutOfProcessCSharpProtocol
{
    public const int CurrentVersion = 1;

    public const int MaxRequestCharacters = 2_000_000;
    public const int MaxResponseCharacters = 2_000_000;
    public const int MaxGeneratedOutputCharacters = 512_000;

    public const string CompletedPhase = "completed";
    public const string CompilationPhase = "compilation";
    public const string RuntimePhase = "runtime";
    public const string ProtocolPhase = "protocol";
}

public sealed record OutOfProcessCSharpRequest
{
    public int ProtocolVersion { get; init; } = OutOfProcessCSharpProtocol.CurrentVersion;
    public string AssemblyName { get; init; } = "SwarmGeneratedCode";
    public IReadOnlyList<string> Sources { get; init; } = [];
}

public sealed record OutOfProcessCSharpResponse
{
    public int ProtocolVersion { get; init; } = OutOfProcessCSharpProtocol.CurrentVersion;
    public bool Success { get; init; }
    public string Phase { get; init; } = OutOfProcessCSharpProtocol.ProtocolPhase;
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}
