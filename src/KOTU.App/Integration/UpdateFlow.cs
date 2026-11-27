using KOTU.Core.Threading;
using KOTU.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Velopack;
using Microsoft.Extensions.DependencyInjection;

namespace KOTU.App.Integration;

/// <summary>자동 안내와 Settings가 공유하는 다운로드/설치 흐름. 현재 문서를 교체하지 않는다.</summary>
internal static class UpdateFlow
{
    private static MainWindow? _dialogHost;
    private static ContentDialog? _dialog;
    private static bool _closeHostAfterDialog;
    private static string? _downloadedVersion;
    public static bool IsBusy { get; private set; }
    public static string Status { get; private set; } = string.Empty;
    public static event Action? Changed;

    internal static bool DismissForWindowClose(MainWindow window)
    {
        if (!ReferenceEquals(window, _dialogHost) || _dialog is null) return false;
        _closeHostAfterDialog = true;
        _dialog.Hide();
        return true;
    }

    internal static bool HasDialog(MainWindow window) => ReferenceEquals(window, _dialogHost) && _dialog is not null;

    public static async Task<bool> OfferAsync(MainWindow window, UpdateInfo info, UpdateVersion version, UpdateStateStore store)
    {
        if (IsBusy) return false;
        IsBusy = true;
        Notify();
        using var worker = new ModuleWorker("KOTU update choice", ThreadPriority.BelowNormal);
        IDisposable? operation = null;
        try
        {
            operation = await worker.Run(_ => store.TryAcquireLease("operation"));
            if (operation is null) return false;
            if (await worker.Run(_ => store.IsDeclined(version))) return true;
            if (App.IsWelcomePending || !window.CanHostUpdate || !App.Services.GetRequiredService<WindowManager>().CanPresentUpdate) return false;
            var explanation = new TextBlock
            {
                Text = $"KOTU v{version} is available. Would you like to update?",
                TextWrapping = TextWrapping.Wrap,
            };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(explanation);
            body.Children.Add(error);
            var prompt = new ContentDialog
            {
                Title = "Update available", Content = body, PrimaryButtonText = "Yes", SecondaryButtonText = "No",
                DefaultButton = ContentDialogButton.Primary, XamlRoot = window.Content.XamlRoot,
            };
            var saving = false;
            prompt.Closing += (_, args) => { if (saving) args.Cancel = true; };
            prompt.SecondaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                saving = true;
                try { await worker.Run(_ => store.Decline(version)); }
                catch
                {
                    args.Cancel = true;
                    error.Text = "Your choice could not be saved. Check that your settings folder is writable, then click No to try again.";
                    error.Visibility = Visibility.Visible;
                }
                finally { saving = false; deferral.Complete(); }
            };
            if (await ShowAsync(window, prompt) == ContentDialogResult.Primary && !window.IsClosedForUpdates)
                await DownloadAndInstallAsync(window, info, worker);
            return true;
        }
        catch (Exception ex) { SetStatus("Update notification postponed: " + ex.Message); return false; }
        finally
        {
            try { if (operation is not null) await worker.Run(_ => operation.Dispose()); }
            finally { IsBusy = false; Notify(); }
        }
    }

    public static async Task StartAsync(MainWindow window, UpdateInfo info)
    {
        if (IsBusy || App.IsWelcomePending || !window.CanHostUpdate || !App.Services.GetRequiredService<WindowManager>().CanPresentUpdate ||
            UpdateCoordinator.Store is not { } store) return;
        IsBusy = true; Notify();
        using var worker = new ModuleWorker("KOTU update download", ThreadPriority.BelowNormal);
        IDisposable? operation = null;
        try
        {
            operation = await worker.Run(_ => store.TryAcquireLease("operation"));
            if (operation is null) { SetStatus("Another KOTU window or process is updating. Try again when it finishes."); return; }
            if (App.IsWelcomePending || !window.CanHostUpdate || !App.Services.GetRequiredService<WindowManager>().CanPresentUpdate) return;
            await DownloadAndInstallAsync(window, info, worker);
        }
        catch (Exception ex) { SetStatus("Update failed: " + ex.Message); }
        finally
        {
            try { if (operation is not null) await worker.Run(_ => operation.Dispose()); }
            finally { IsBusy = false; Notify(); }
        }
    }

    private static async Task DownloadAndInstallAsync(MainWindow window, UpdateInfo info, ModuleWorker worker)
    {
        var version = info.TargetFullRelease.Version.ToString();
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Width = 320 };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(text); content.Children.Add(progress);
        var dialog = new ContentDialog
        {
            Title = "Downloading update", Content = content, CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Close, XamlRoot = window.Content.XamlRoot,
        };
        var visible = true;
        var succeeded = false;
        async Task Download()
        {
            try
            {
                SetStatus($"Downloading v{version}..."); text.Text = Status;
                if (_downloadedVersion != version)
                {
                    // 다운로드의 동기 시작/파일 I/O까지 전용 워커에서 실행한다.
                    await worker.Run(_ => UpdateService.DownloadAsync(info, percent =>
                        window.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (window.IsClosedForUpdates) return;
                            SetStatus($"Downloading v{version}... {percent}%");
                            if (visible) { text.Text = Status; progress.Value = percent; }
                        })).GetAwaiter().GetResult());
                }
                _downloadedVersion = version; succeeded = true;
                SetStatus($"v{version} downloaded. Choose Update to v{version} when you are ready to install.");
                if (!visible || window.IsClosedForUpdates) return;
                dialog.Title = "Ready to install";
                text.Text = $"KOTU will close and restart to finish installing v{version}. Install now?";
                progress.Visibility = Visibility.Collapsed;
                dialog.PrimaryButtonText = "Install and restart";
                dialog.DefaultButton = ContentDialogButton.Primary;
            }
            catch (Exception ex)
            {
                SetStatus("Update failed: " + ex.Message);
                if (!visible || window.IsClosedForUpdates) return;
                dialog.Title = "Update failed"; text.Text = Status;
                progress.Visibility = Visibility.Collapsed; dialog.CloseButtonText = "Close";
            }
        }
        var downloading = Download();
        ContentDialogResult choice;
        try { choice = await ShowAsync(window, dialog); }
        finally { visible = false; await downloading; }
        if (choice != ContentDialogResult.Primary || !succeeded || window.IsClosedForUpdates) return;
        SetStatus("Preparing to restart...");
        if (await UpdateService.ApplyAndRestartAsync(info)) return;
        SetStatus(UpdateService.LastRestartError);
        if (!window.CanHostUpdate) return;
        await ShowAsync(window, new ContentDialog
        {
            Title = "Restart postponed", Content = Status, CloseButtonText = "Close", XamlRoot = window.Content.XamlRoot,
        });
    }

    private static async Task<ContentDialogResult> ShowAsync(MainWindow window, ContentDialog dialog)
    {
        _dialogHost = window; _dialog = dialog; _closeHostAfterDialog = false;
        void OnClosed(object sender, WindowEventArgs args) => dialog.Hide();
        window.Closed += OnClosed;
        try { return await dialog.ShowAsync(); }
        finally
        {
            window.Closed -= OnClosed;
            _dialog = null; _dialogHost = null;
            if (_closeHostAfterDialog && !window.IsClosedForUpdates)
            { _closeHostAfterDialog = false; window.DispatcherQueue.TryEnqueue(() => window.Close()); }
        }
    }

    private static void SetStatus(string status) { Status = status; Notify(); }
    private static void Notify() => Changed?.Invoke();
}
