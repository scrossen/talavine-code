using System.Diagnostics;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Starts a Windows batch file (an npm <c>.cmd</c>/<c>.bat</c> shim) through cmd.exe without letting its path or
/// arguments become command syntax. Batch files cannot be started directly with UseShellExecute=false, and cmd.exe
/// neither understands .NET's <c>\"</c> escaping nor keeps quotes predictably under its <c>/c</c> rules (a quoted
/// shim path under a profile with a space plus any other quoted argument runs the wrong program). Values travel in
/// uniquely named environment variables, so cmd.exe receives a fixed command shape; the fixed command clears those
/// variables before the batch file runs, so the values are not inherited by the CLI either. Every launcher of a
/// batch-file CLI uses this one bridge.
/// </summary>
public static class WindowsBatchLaunch
{
    /// <summary>True on Windows for a <c>.cmd</c> or <c>.bat</c> file, which only cmd.exe can run.</summary>
    public static bool IsBatchFile(string fileName)
        => OperatingSystem.IsWindows()
           && (fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Points <paramref name="startInfo"/> at cmd.exe running <paramref name="batchFile"/> with <paramref name="arguments"/>.
    /// Throws <see cref="ArgumentException"/> for a value holding a double quote, a percent sign or a line break: a
    /// batch file re-reads its arguments as command text, so such a value cannot be passed through it safely.
    /// </summary>
    public static void Configure(ProcessStartInfo startInfo, string batchFile, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchFile);
        ArgumentNullException.ThrowIfNull(arguments);
        ValidateBatchValue(batchFile, nameof(batchFile));
        foreach (var argument in arguments)
        {
            ValidateBatchValue(argument, nameof(arguments));
        }

        startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec")
                             ?? Path.Combine(
                                 Environment.GetFolderPath(Environment.SpecialFolder.System),
                                 "cmd.exe");
        startInfo.ArgumentList.Clear();
        var prefix = $"SWARMCORE_CLI_BATCH_{Guid.NewGuid():N}";
        var executableVariable = prefix + "_EXE";
        startInfo.Environment[executableVariable] = batchFile;
        var variables = new List<string> { executableVariable };
        for (var index = 0; index < arguments.Count; index++)
        {
            var variable = $"{prefix}_ARG_{index}";
            startInfo.Environment[variable] = arguments[index];
            variables.Add(variable);
        }

        // cmd expands all %VAR% references in this compound line before it
        // executes the leading `set` commands. That lets us clear the bridge
        // variables before the batch/provider child is created while retaining
        // their already-expanded values in the final invocation. Do not use
        // `call`: call reparses and expands the command a second time, turning
        // otherwise inert percent sequences into command syntax.
        var command = new System.Text.StringBuilder();
        foreach (var variable in variables)
        {
            command.Append($"set \"{variable}=\" & ");
        }

        command.Append($"\"%{executableVariable}%\"");
        for (var index = 0; index < arguments.Count; index++)
        {
            command.Append($" \"%{prefix}_ARG_{index}%\"");
        }

        // cmd.exe parses the text after /c from its raw command line rather
        // than with the normal CommandLineToArgvW rules. Supplying this final
        // command through ArgumentList causes .NET's normal quote escaping to
        // surface literal backslashes to cmd. The raw string is safe here: it
        // contains only the internally generated hexadecimal variable prefix;
        // all external values remain in the environment bridge above.
        startInfo.Arguments = $"/d /v:off /s /c \"{command}\"";
    }

    private static void ValidateBatchValue(string value, string parameterName)
    {
        if (value.IndexOfAny(['\0', '\r', '\n', '"', '%']) >= 0)
        {
            throw new ArgumentException(
                "Windows batch-backed CLI paths and arguments cannot contain quotes, percent signs, or line breaks.",
                parameterName);
        }
    }
}
