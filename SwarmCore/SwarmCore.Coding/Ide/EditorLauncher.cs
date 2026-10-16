using System.Diagnostics;
using System.Text;

namespace SwarmCore.Coding.Ide;

public enum EditorPlatform { Windows, MacOS, Linux }

/// <summary>What the resolver may look at; tests supply a fake file system and PATH.</summary>
public sealed record EditorEnvironment(
    EditorPlatform Platform,
    string? PathVariable,
    string? PathExt,
    string? LocalAppData,
    string? ProgramFiles,
    string? Home,
    Func<string, bool> FileExists)
{
    public static EditorEnvironment Current() => new(
        OperatingSystem.IsWindows() ? EditorPlatform.Windows : OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() ? EditorPlatform.MacOS : EditorPlatform.Linux,
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("PATHEXT"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        File.Exists);

    public char PathSeparator => Platform == EditorPlatform.Windows ? ';' : ':';

    public string Combine(params string[] parts)
        => Platform == EditorPlatform.Windows ? string.Join('\\', parts.Select((p, i) => i == 0 ? p.TrimEnd('\\', '/') : p.Trim('\\', '/')))
            : string.Join('/', parts.Select((p, i) => i == 0 ? p.TrimEnd('/') : p.Trim('/')));
}

/// <summary>One way to open a file, tried in order until one starts.</summary>
public abstract record EditorLaunchStep(string Via);

/// <summary>Start a program with an argument list (never a shell command line).</summary>
public sealed record EditorProcessStep(string FileName, IReadOnlyList<string> Arguments, string Via) : EditorLaunchStep(Via);

/// <summary>Ask the OS to open a URI (the <c>vscode://file/...</c> handler).</summary>
public sealed record EditorUriStep(Uri Uri, string Via) : EditorLaunchStep(Via);

/// <summary>Open the file in the OS default app.</summary>
public sealed record EditorDefaultAppStep(string Path, string Via) : EditorLaunchStep(Via);

/// <summary>
/// Builds the ordered ways to open <c>file[:line[:col]]</c>: the person's configured editor command when set
/// (then the OS default app); otherwise the VS Code CLI found on PATH or in the standard install locations, the
/// <c>vscode://file/</c> URI, and finally the OS default app. Arguments are always an argument list; a Windows
/// batch file (<c>code.cmd</c>) is used only when every argument is free of cmd.exe metacharacters, and the VS Code
/// executable beside it is preferred so no command interpreter is involved at all.
/// </summary>
public static class EditorCommandResolver
{
    private static readonly char[] BatchUnsafe = ['&', '|', '<', '>', '^', '%', '!', '"', '(', ')', '\r', '\n', '`'];

    public static IReadOnlyList<EditorLaunchStep> Plan(string fullPath, int? line, int? column, string? configuredCommand, EditorEnvironment env)
    {
        var steps = new List<EditorLaunchStep>();
        var location = line is { } l ? column is { } c ? $"{fullPath}:{l}:{c}" : $"{fullPath}:{l}" : fullPath;
        if (!string.IsNullOrWhiteSpace(configuredCommand))
        {
            var tokens = Tokenize(configuredCommand);
            if (tokens.Count > 0)
            {
                var hasPlaceholder = tokens.Skip(1).Any(t => t.Contains("{file}", StringComparison.Ordinal) || t.Contains("{line}", StringComparison.Ordinal));
                var arguments = hasPlaceholder
                    ? tokens.Skip(1).Select(t => t.Replace("{file}", fullPath, StringComparison.Ordinal)
                        .Replace("{line}", (line ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                        .Replace("{column}", (column ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)).ToList()
                    : tokens.Skip(1).Concat(["--goto", location]).ToList();
                var program = FindOnPath(tokens[0], env) ?? tokens[0];
                AddProcess(steps, program, arguments, "configured editor", env);
            }
            steps.Add(new EditorDefaultAppStep(fullPath, "default app"));
            return steps;
        }

        foreach (var cli in VsCodeCandidates(env).Distinct(env.Platform == EditorPlatform.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
        {
            AddProcess(steps, cli, ["--goto", location], "VS Code", env);
            break; // the first installed CLI is the person's VS Code; later candidates would be the same editor
        }
        steps.Add(new EditorUriStep(VsCodeUri(fullPath, line, column), "vscode:// link"));
        steps.Add(new EditorDefaultAppStep(fullPath, "default app"));
        return steps;
    }

    /// <summary><c>vscode://file/&lt;abs path&gt;:line:col</c>, with each path segment percent-encoded.</summary>
    public static Uri VsCodeUri(string fullPath, int? line, int? column)
    {
        var path = fullPath.Replace('\\', '/');
        var encoded = string.Join('/', path.Split('/').Select((segment, i) =>
            i == 0 && segment.Length == 2 && segment[1] == ':' ? segment : Uri.EscapeDataString(segment)));
        if (!encoded.StartsWith('/')) encoded = "/" + encoded;
        var suffix = line is { } l ? column is { } c ? $":{l}:{c}" : $":{l}" : "";
        return new Uri("vscode://file" + encoded + suffix);
    }

    private static void AddProcess(List<EditorLaunchStep> steps, string program, IReadOnlyList<string> arguments, string via, EditorEnvironment env)
    {
        if (env.Platform == EditorPlatform.Windows && IsBatch(program))
        {
            // code.cmd runs "..\Code.exe" with VS Code's CLI script; the executable itself accepts --goto, so
            // launch it directly when present and avoid cmd.exe argument re-parsing entirely.
            var directory = DirectoryOf(program);
            var exe = directory is null ? null : SiblingExecutable(directory, Path.GetFileNameWithoutExtension(program), env);
            if (exe is not null) { steps.Add(new EditorProcessStep(exe, arguments, via)); return; }
            if (arguments.Any(a => a.IndexOfAny(BatchUnsafe) >= 0)) return; // cmd.exe could misread the path: use a later step
        }
        steps.Add(new EditorProcessStep(program, arguments, via));
    }

    private static string? SiblingExecutable(string binDirectory, string name, EditorEnvironment env)
    {
        var parent = DirectoryOf(binDirectory);
        if (parent is null) return null;
        foreach (var candidate in new[] { "Code.exe", "Code - Insiders.exe", "Cursor.exe", "VSCodium.exe", name + ".exe" })
        {
            var path = env.Combine(parent, candidate);
            if (env.FileExists(path)) return path;
        }
        return null;
    }

    private static string? DirectoryOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var index = trimmed.LastIndexOfAny(['\\', '/']);
        return index > 0 ? trimmed[..index] : null;
    }

    private static bool IsBatch(string program)
        => program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> VsCodeCandidates(EditorEnvironment env)
    {
        if (FindOnPath("code", env) is { } onPath) yield return onPath;
        switch (env.Platform)
        {
            case EditorPlatform.Windows:
                foreach (var root in new[] { env.LocalAppData is null ? null : env.Combine(env.LocalAppData, "Programs"), env.ProgramFiles })
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    var cmd = env.Combine(root, "Microsoft VS Code", "bin", "code.cmd");
                    if (env.FileExists(cmd)) yield return cmd;
                }
                break;
            case EditorPlatform.MacOS:
                foreach (var app in new[] { "/Applications", env.Home is null ? null : env.Combine(env.Home, "Applications") })
                {
                    if (string.IsNullOrEmpty(app)) continue;
                    var cli = env.Combine(app, "Visual Studio Code.app", "Contents", "Resources", "app", "bin", "code");
                    if (env.FileExists(cli)) yield return cli;
                }
                foreach (var cli in new[] { "/usr/local/bin/code", "/opt/homebrew/bin/code" })
                    if (env.FileExists(cli)) yield return cli;
                break;
            default:
                foreach (var cli in new[] { "/usr/bin/code", "/usr/local/bin/code", "/snap/bin/code", "/usr/share/code/bin/code" })
                    if (env.FileExists(cli)) yield return cli;
                break;
        }
    }

    /// <summary>Resolves a bare program name on PATH (PATHEXT on Windows); a path is returned when it exists.</summary>
    public static string? FindOnPath(string program, EditorEnvironment env)
    {
        if (string.IsNullOrWhiteSpace(program)) return null;
        if (program.IndexOfAny(['\\', '/']) >= 0) return env.FileExists(program) ? program : null;
        var extensions = env.Platform == EditorPlatform.Windows
            ? (Path.HasExtension(program) ? [""] : (env.PathExt ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries))
            : new[] { "" };
        foreach (var directory in (env.PathVariable ?? "").Split(env.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = directory.Trim().Trim('"');
            if (dir.Length == 0) continue;
            foreach (var extension in extensions)
            {
                var candidate = env.Combine(dir, program + extension.ToLowerInvariant());
                if (env.FileExists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>Splits a command preference into tokens, honoring double quotes (no shell expansion of any kind).</summary>
    public static IReadOnlyList<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var ch in command.Trim())
        {
            if (ch == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (any || current.Length > 0) tokens.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }
            current.Append(ch);
        }
        if (any || current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}

/// <summary>The outcome of an open request, worded for the person.</summary>
public sealed record EditorOpenResult(bool Opened, string Message, string? Via = null);

/// <summary>
/// Opens a file location in the person's editor. Everything (path checks, file reads, process start) runs on the
/// thread pool, so a UI host can await it from its dispatcher. The URI and default-app openers are host seams:
/// a MAUI host passes its platform launcher; the defaults use the OS shell.
/// </summary>
public sealed class EditorLauncher
{
    private const long MaxSearchBytes = 4 * 1024 * 1024;
    private readonly Func<EditorEnvironment> _environment;
    private readonly Func<ProcessStartInfo, bool> _startProcess;
    private readonly Func<Uri, Task<bool>> _openUri;
    private readonly Func<string, Task<bool>> _openDefault;

    public EditorLauncher(
        Func<Uri, Task<bool>>? openUri = null,
        Func<string, Task<bool>>? openDefault = null,
        Func<ProcessStartInfo, bool>? startProcess = null,
        Func<EditorEnvironment>? environment = null)
    {
        _environment = environment ?? EditorEnvironment.Current;
        _startProcess = startProcess ?? StartDetached;
        _openUri = openUri ?? (uri => Task.FromResult(ShellOpen(uri.AbsoluteUri)));
        _openDefault = openDefault ?? (path => Task.FromResult(ShellOpen(path)));
    }

    public Task<EditorOpenResult> OpenAsync(EditorTarget target, string? repositoryRoot, string? configuredCommand, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var full = EditorPathPolicy.Resolve(target.Path, repositoryRoot);
            if (full is null) return new EditorOpenResult(false, $"{target.Path} is not a file in this repository.");
            var line = target.Line;
            var column = target.Column;
            if (line is null && !string.IsNullOrWhiteSpace(target.SearchText))
                (line, column) = await FindAsync(full, target.SearchText, ct).ConfigureAwait(false);
            foreach (var step in EditorCommandResolver.Plan(full, line, column, configuredCommand, _environment()))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var opened = step switch
                    {
                        EditorProcessStep process => _startProcess(ToStartInfo(process)),
                        EditorUriStep uri => await _openUri(uri.Uri).ConfigureAwait(false),
                        EditorDefaultAppStep app => await _openDefault(app.Path).ConfigureAwait(false),
                        _ => false,
                    };
                    if (opened) return new EditorOpenResult(true, $"Opened {Path.GetFileName(full)} ({step.Via}).", step.Via);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException
                    or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException or UriFormatException)
                {
                    // Try the next way to open it.
                }
            }
            return new EditorOpenResult(false, $"Could not open {Path.GetFileName(full)}. Set an editor command in Settings.");
        }, ct);

    public static ProcessStartInfo ToStartInfo(EditorProcessStep step)
    {
        var info = new ProcessStartInfo(step.FileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in step.Arguments) info.ArgumentList.Add(argument);
        // VS Code's own CLI sets this for its script; an inherited value would make the editor run as plain Node.
        info.Environment.Remove("ELECTRON_RUN_AS_NODE");
        return info;
    }

    /// <summary>The 1-based line and column of the first line of <paramref name="text"/> in the file, if present.</summary>
    public static async Task<(int? Line, int? Column)> FindAsync(string path, string text, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxSearchBytes) return (null, null);
            var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var needle = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (needle is null) return (null, null);
            var index = content.IndexOf(needle, StringComparison.Ordinal);
            if (index < 0) return (null, null);
            var line = 1;
            var lineStart = 0;
            for (var i = 0; i < index; i++)
                if (content[i] == '\n') { line++; lineStart = i + 1; }
            return (line, index - lineStart + 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, null); }
    }

    private static bool StartDetached(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
        return process is not null;
    }

    private static bool ShellOpen(string target)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows()) info = new ProcessStartInfo(target) { UseShellExecute = true };
        else
        {
            info = new ProcessStartInfo(OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() ? "open" : "xdg-open") { UseShellExecute = false };
            info.ArgumentList.Add(target);
        }
        using var process = Process.Start(info);
        return process is not null || OperatingSystem.IsWindows();
    }
}
