using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Abstractions.Computer;

/// <summary>Human-facing OS computer name. Network hostnames and interface routes are not device identity.</summary>
public static partial class HostComputerName
{
    public static string Get()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            try
            {
                var name = ReadAppleComputerName();
                if (!string.IsNullOrWhiteSpace(name)) return WithoutConflictSuffix(name);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        return Environment.MachineName;
    }

    /// <summary>
    /// When Bonjour believes a Mac's name is already taken (common with Wi-Fi and a wired or
    /// Thunderbolt connection on the same network, where the Mac sees its own announcement),
    /// macOS renames the Computer Name to "Name (2)", "Name (3)", and so on. That suffix is not
    /// the owner's choice and changes over time, so it is not part of the stable device name.
    /// </summary>
    public static string WithoutConflictSuffix(string name)
    {
        var match = ConflictSuffix().Match(name);
        return match.Success ? match.Groups[1].Value : name;
    }

    [GeneratedRegex(@"^(.*\S)\s\(\d{1,3}\)$")]
    private static partial Regex ConflictSuffix();

    private static string? ReadAppleComputerName()
    {
        // Read the configured ComputerName without a reverse-DNS lookup. With
        // Wi-Fi and a dock active, gethostname can instead follow DHCP/PTR names.
        var value = SCDynamicStoreCopyComputerName(IntPtr.Zero, IntPtr.Zero);
        if (value == IntPtr.Zero) return null;
        try
        {
            const uint utf8 = 0x08000100;
            var capacity = CFStringGetMaximumSizeForEncoding(CFStringGetLength(value), utf8) + 1;
            if (capacity is <= 0 or > 65536) return null;
            var buffer = new byte[(int)capacity];
            if (CFStringGetCString(value, buffer, capacity, utf8) == 0) return null;
            var length = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
        }
        finally { CFRelease(value); }
    }

    [DllImport("/System/Library/Frameworks/SystemConfiguration.framework/SystemConfiguration")]
    private static extern IntPtr SCDynamicStoreCopyComputerName(IntPtr store, IntPtr nameEncoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFStringGetLength(IntPtr value);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern byte CFStringGetCString(IntPtr value, [Out] byte[] buffer, nint capacity, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);
}
