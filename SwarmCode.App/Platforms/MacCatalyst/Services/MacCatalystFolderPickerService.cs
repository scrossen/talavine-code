#if MACCATALYST
using System.Runtime.InteropServices;
using Foundation;
using Microsoft.Maui.ApplicationModel;
using ObjCRuntime;
using SwarmCode.App.Services;

namespace SwarmCode.App.Platforms.MacCatalyst.Services;

public sealed class MacCatalystFolderPickerService : IFolderPickerService
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const nint ModalResponseOk = 1;

    private static readonly NativeHandle NSOpenPanelClass = Class.GetHandle("NSOpenPanel");
    private static readonly NativeHandle OpenPanelSelector = Selector.GetHandle("openPanel");
    private static readonly NativeHandle SetCanChooseFilesSelector = Selector.GetHandle("setCanChooseFiles:");
    private static readonly NativeHandle SetCanChooseDirectoriesSelector = Selector.GetHandle("setCanChooseDirectories:");
    private static readonly NativeHandle SetAllowsMultipleSelectionSelector = Selector.GetHandle("setAllowsMultipleSelection:");
    private static readonly NativeHandle SetCanCreateDirectoriesSelector = Selector.GetHandle("setCanCreateDirectories:");
    private static readonly NativeHandle SetPromptSelector = Selector.GetHandle("setPrompt:");
    private static readonly NativeHandle SetMessageSelector = Selector.GetHandle("setMessage:");
    private static readonly NativeHandle SetDirectoryUrlSelector = Selector.GetHandle("setDirectoryURL:");
    private static readonly NativeHandle RunModalSelector = Selector.GetHandle("runModal");
    private static readonly NativeHandle UrlSelector = Selector.GetHandle("URL");

    public bool IsSupported => true;

    public Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default)
        => MainThread.InvokeOnMainThreadAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            return PickFolderOnMainThread(request);
        });

    private static string? PickFolderOnMainThread(FolderPickerRequest request)
    {
        if (NSOpenPanelClass == NativeHandle.Zero)
        {
            return null;
        }

        var panel = objc_msgSend_NativeHandle(NSOpenPanelClass, OpenPanelSelector);
        if (panel == NativeHandle.Zero)
        {
            return null;
        }

        objc_msgSend_void_bool(panel, SetCanChooseFilesSelector, false);
        objc_msgSend_void_bool(panel, SetCanChooseDirectoriesSelector, true);
        objc_msgSend_void_bool(panel, SetAllowsMultipleSelectionSelector, false);
        objc_msgSend_void_bool(panel, SetCanCreateDirectoriesSelector, true);

        using var prompt = new NSString("Choose");
        objc_msgSend_void_NativeHandle(panel, SetPromptSelector, prompt.Handle);

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            using var message = new NSString(request.Title);
            objc_msgSend_void_NativeHandle(panel, SetMessageSelector, message.Handle);
        }

        var initialDirectory = NormalizeInitialDirectory(request.InitialDirectory);
        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            using var directoryUrl = NSUrl.FromFilename(initialDirectory);
            objc_msgSend_void_NativeHandle(panel, SetDirectoryUrlSelector, directoryUrl.Handle);
        }

        var response = objc_msgSend_nint(panel, RunModalSelector);
        if (response != ModalResponseOk)
        {
            return null;
        }

        var urlHandle = objc_msgSend_NativeHandle(panel, UrlSelector);
        var url = Runtime.GetNSObject<NSUrl>(urlHandle);
        return url?.Path;
    }

    private static string? NormalizeInitialDirectory(string? suggested)
    {
        if (string.IsNullOrWhiteSpace(suggested))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(suggested);
            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }

            var parent = Path.GetDirectoryName(fullPath);
            return !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent)
                ? parent
                : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern NativeHandle objc_msgSend_NativeHandle(NativeHandle receiver, NativeHandle selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_bool(
        NativeHandle receiver,
        NativeHandle selector,
        [MarshalAs(UnmanagedType.I1)] bool arg1);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_NativeHandle(
        NativeHandle receiver,
        NativeHandle selector,
        NativeHandle arg1);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_nint(NativeHandle receiver, NativeHandle selector);
}
#endif
