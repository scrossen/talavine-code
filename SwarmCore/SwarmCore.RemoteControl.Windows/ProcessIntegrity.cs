using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static SwarmCore.RemoteControl.Windows.NativeMethods;

namespace SwarmCore.RemoteControl.Windows;

/// <summary>
/// Compares process integrity levels. User Interface Privilege Isolation (UIPI) blocks input and window messages
/// from a process to windows of a higher-integrity process, and SendInput does not report it, so callers check first.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProcessIntegrity
{
    private const int TokenElevationSize = sizeof(int);
    private const int IntegrityBufferSize = 256;
    /// <summary>SECURITY_MANDATORY_SYSTEM_RID: nothing an ordinary or elevated process can drive sits above it.</summary>
    private const uint SystemIntegrityRid = 0x4000;

    private static readonly Lazy<bool> CurrentElevated = new(() => ReadToken(GetCurrentProcess(), out var elevated, out _) && elevated);
    private static readonly Lazy<uint?> CurrentIntegrity = new(() => ReadToken(GetCurrentProcess(), out _, out var rid) ? rid : null);

    public static bool IsCurrentProcessElevated => CurrentElevated.Value;

    /// <summary>
    /// True when the process runs at a higher integrity level than this one, so UIPI blocks our input to it. This is
    /// a pure integrity comparison: it also holds for an elevated (High) caller facing System-integrity windows.
    /// </summary>
    public static bool IsHigherThanCurrent(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId) return false;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == 0) return Marshal.GetLastPInvokeError() == ErrorAccessDenied && DeniedMeansHigher();
        try
        {
            if (!ReadToken(process, out var elevated, out var rid))
                return Marshal.GetLastPInvokeError() == ErrorAccessDenied && DeniedMeansHigher();
            if (CurrentIntegrity.Value is { } mine && rid is { } theirs) return theirs > mine;
            return elevated && !IsCurrentProcessElevated;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>
    /// A process whose token we may not even query is protected or system-owned; it outranks us unless we already run
    /// at System integrity ourselves.
    /// </summary>
    private static bool DeniedMeansHigher() => CurrentIntegrity.Value is not { } mine || mine < SystemIntegrityRid;

    public static bool IsWindowHigherThanCurrent(nint hwnd)
    {
        if (hwnd == 0) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return IsHigherThanCurrent((int)pid);
    }

    private static bool ReadToken(nint process, out bool elevated, out uint? integrityRid)
    {
        elevated = false;
        integrityRid = null;
        if (!OpenProcessToken(process, TokenQuery, out var token)) return false;
        var buffer = Marshal.AllocHGlobal(IntegrityBufferSize);
        try
        {
            if (GetTokenInformation(token, TokenInformationElevation, buffer, TokenElevationSize, out _))
                elevated = Marshal.ReadInt32(buffer) != 0;
            if (GetTokenInformation(token, TokenInformationIntegrityLevel, buffer, IntegrityBufferSize, out _))
            {
                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label { PSID Sid; DWORD Attributes; } }
                var sid = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                if (count > 0) integrityRid = unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1))));
            }
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(token);
        }
    }
}
