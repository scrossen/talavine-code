using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.SelfImprovement;

/// <summary>
/// Build and test commands for a cycle when none are configured. The SwarmCode repository gets its console host build
/// plus the harness and hosting test suites; any other repository with exactly one solution at its root gets
/// <c>dotnet build</c> and <c>dotnet test</c> of that solution. Nothing is guessed beyond that: a repository the
/// discovery does not understand yields no checks, and a cycle without checks cannot verify anything.
/// </summary>
public static class SelfImprovementChecks
{
    public const int BuildTimeoutSeconds = 1800;
    public const int TestTimeoutSeconds = 2700;

    private static readonly string[] SwarmCodeTestProjects =
    [
        "SwarmCore/SwarmCore.Coding.Tests/SwarmCore.Coding.Tests.csproj",
        "SwarmCode.Hosting.Tests/SwarmCode.Hosting.Tests.csproj",
        "SwarmCode.Tests/SwarmCode.Tests.csproj",
    ];

    public static IReadOnlyList<CodingAcceptanceCommand> Discover(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot)) return [];
        var root = Path.GetFullPath(repositoryRoot);
        var checks = new List<CodingAcceptanceCommand>();
        var console = Path.Combine(root, "SwarmCode", "SwarmCode.csproj");
        if (File.Exists(console))
        {
            checks.Add(Dotnet("build Talavine Code", ["build", Relative(root, console), "--nologo", "-v", "q"], BuildTimeoutSeconds));
            foreach (var project in SwarmCodeTestProjects)
            {
                var path = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                    checks.Add(Dotnet("test " + Path.GetFileNameWithoutExtension(path), ["test", Relative(root, path), "--nologo", "-v", "q"], TestTimeoutSeconds));
            }
            return checks;
        }
        var solutions = Directory.EnumerateFiles(root, "*.sln").Concat(Directory.EnumerateFiles(root, "*.slnx")).ToList();
        if (solutions.Count == 1)
        {
            var solution = Relative(root, solutions[0]);
            checks.Add(Dotnet("build", ["build", solution, "--nologo", "-v", "q"], BuildTimeoutSeconds));
            checks.Add(Dotnet("test", ["test", solution, "--nologo", "-v", "q"], TestTimeoutSeconds));
        }
        return checks;
    }

    private static CodingAcceptanceCommand Dotnet(string name, IEnumerable<string> arguments, int timeoutSeconds)
        => new() { Name = name, FileName = "dotnet", Arguments = arguments.ToList(), TimeoutSeconds = timeoutSeconds };

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
