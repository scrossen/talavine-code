namespace SwarmCore.Testing;

/// <summary>
/// A structured command every test machine can run without anything on PATH: the platform shell printing a fixed
/// line (<c>cmd.exe /d /c echo</c> from the Windows system folder, <c>/bin/sh -c echo</c> elsewhere). Use it where a
/// test needs "some real executable with known stdout"; PATH-dependent tools such as <c>dotnet</c> are absent on a
/// lane that runs a pinned SDK with a minimal PATH.
/// </summary>
internal static class TestShell
{
    /// <summary>The executable and arguments that print <paramref name="text"/> (no spaces or shell metacharacters).</summary>
    public static (string FileName, List<string> Arguments) Echo(string text)
    {
        if (text.Any(c => char.IsWhiteSpace(c) || "&|<>^\"'`$;()%!".Contains(c)))
            throw new ArgumentException("Echo text must be a plain token.", nameof(text));
        return OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", "echo " + text])
            : ("/bin/sh", ["-c", "echo " + text]);
    }
}
