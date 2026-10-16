#if WINDOWS
using Microsoft.Maui.ApplicationModel;
using SwarmCode.App.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SwarmCode.App.Platforms.Windows.Services;

public sealed class WindowsFolderPickerService : IFolderPickerService
{
    public bool IsSupported => true;

    public Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default)
        => MainThread.InvokeOnMainThreadAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();

            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add("*");

            var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            var nativeWindow = window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (nativeWindow is not null)
            {
                var hwnd = WindowNative.GetWindowHandle(nativeWindow);
                InitializeWithWindow.Initialize(picker, hwnd);
            }

            var folder = await picker.PickSingleFolderAsync();
            ct.ThrowIfCancellationRequested();
            return folder?.Path;
        });
}
#endif
