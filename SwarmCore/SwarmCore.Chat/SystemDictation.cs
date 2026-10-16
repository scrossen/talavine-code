using System.Runtime.InteropServices;

namespace SwarmCore.Chat;

/// <summary>
/// Starts the operating system's own dictation in the focused text field: voice typing on Windows (Win+H) and
/// Dictation on macOS. The OS owns the microphone, the speech engine and its privacy settings, and types the words
/// into the field itself, so a chat box needs no audio capture, transcription service or API key of its own.
/// Hosts that run in a desktop window register it; a browser-hosted page uses the browser's speech API instead.
/// </summary>
public interface IDictationLauncher
{
    /// <summary>True when this host can start system dictation.</summary>
    bool IsAvailable { get; }

    /// <summary>Plain words for how to start dictation by hand on this computer.</summary>
    string ManualHint { get; }

    /// <summary>Starts dictation into the currently focused field. False when the OS refused.</summary>
    Task<bool> StartAsync();
}

/// <summary>The built-in launcher for Windows and macOS (including Mac Catalyst) desktop hosts.</summary>
public sealed class SystemDictationLauncher : IDictationLauncher
{
    public bool IsAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS();

    public string ManualHint => OperatingSystem.IsWindows()
        ? "Press Windows key + H to dictate."
        : OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS()
            ? "Press the Fn (Globe) key twice to dictate, or choose Edit > Start Dictation."
            : "Use your device's dictation to talk instead of type.";

    public Task<bool> StartAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Task.FromResult(Windows.PressWinH());
            if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS()) return Task.FromResult(Mac.StartDictation());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { }
        return Task.FromResult(false);
    }

    private static class Windows
    {
        private const ushort VkLeftWindows = 0x5B, VkH = 0x48;
        private const uint KeyUp = 0x0002;

        // Windows voice typing listens to the focused field after Win+H; the keystroke is sent to this
        // foreground window only, the same as the person pressing it.
        public static bool PressWinH()
        {
            Input[] inputs =
            [
                Key(VkLeftWindows, 0), Key(VkH, 0), Key(VkH, KeyUp), Key(VkLeftWindows, KeyUp),
            ];
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
        }

        private static Input Key(ushort vk, uint flags) => new() { Type = 1, Keyboard = new KeyboardInput { VirtualKey = vk, Flags = flags } };

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public KeyboardInput Keyboard;
            private readonly ulong _padding; // MOUSEINPUT is larger than KEYBDINPUT; keep the union size.
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public nint ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);
    }

    private static class Mac
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        // [NSApp sendAction:@selector(startDictation:) to:nil from:nil]: the menu's Edit > Start Dictation.
        public static bool StartDictation()
        {
            var nsApplication = objc_getClass("NSApplication");
            if (nsApplication == 0) return false;
            var app = Send(nsApplication, sel_registerName("sharedApplication"));
            if (app == 0) return false;
            return SendAction(app, sel_registerName("sendAction:to:from:"), sel_registerName("startDictation:"), 0, 0);
        }

        [DllImport(ObjC)] private static extern nint objc_getClass(string name);
        [DllImport(ObjC)] private static extern nint sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendAction(nint receiver, nint selector, nint action, nint to, nint from);
    }
}
