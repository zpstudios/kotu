using System.Diagnostics;
using KOTU.Core.Jobs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace KOTU.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<Guid, JobRow> _jobRows = [];
    private int _jobRefreshQueued;
    private IDisposable? _jobCloseLease;
    private bool _jobsClosed;

    private void InitializeJobs()
    {
        JobsLayer.Tag = KOTU.Input.HotkeySupport.PassThroughTag;
        _manager.Jobs.Changed += OnJobsChanged;
        Closed += (_, _) =>
        {
            _jobsClosed = true;
            _manager.Jobs.Changed -= OnJobsChanged;
            foreach (var row in _jobRows.Values) row.ClearPassword();
            _jobRows.Clear();
        };
        RootLayout.SizeChanged += (_, _) => ResizeJobsPanel();
        RefreshJobs();
    }

    private BackgroundJobSnapshot? CurrentContentJob()
        => ContentJobPresentation.FindActive(ModuleHost.Content, _manager.Jobs.GetSnapshots());

    private bool RedirectFromWorkingArchive(Action<MainWindow> open)
    {
        if (CurrentContentJob() is null) return false;
        _manager.OpenDestinationWindow(open);
        return true;
    }
    private void OnJobsChanged()
    {
        // 워커 통지를 합쳐 최신 스냅샷만 반영한다. 닫힌 창의 큐도 더 이상 UI를 만지지 않는다.
        if (Interlocked.Exchange(ref _jobRefreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _jobRefreshQueued, 0);
            if (!_jobsClosed) RefreshJobs();
        })) Interlocked.Exchange(ref _jobRefreshQueued, 0);
    }

    private void RefreshJobs()
    {
        var jobs = _manager.Jobs.GetSnapshots();
        if (_tray is not null) ApplyTitle();
        var active = jobs.Count(j => j.IsActive);
        var waiting = jobs.Count(j => j.State == BackgroundJobState.WaitingForPassword);
        JobsButton.Content = waiting > 0 ? $"Jobs ({active}) - password needed"
            : active > 0 ? $"Jobs ({active})" : $"Jobs ({jobs.Count})";
        JobsButton.Visibility = jobs.Count > 0 || JobsPanel.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        var ids = jobs.Select(j => j.Id).ToHashSet();
        foreach (var id in _jobRows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            var row = _jobRows[id];
            row.ClearPassword();
            if (IsFocusWithin(row.Root)) JobsButton.Focus(FocusState.Programmatic);
            JobsRows.Children.Remove(row.Root);
            _jobRows.Remove(id);
        }
        foreach (var job in jobs)
        {
            if (!_jobRows.TryGetValue(job.Id, out var row))
            {
                row = new JobRow(this, job);
                _jobRows.Add(job.Id, row);
                JobsRows.Children.Add(row.Root);
            }
            row.Update(job);
        }
        if (active == 0 && JobsNotice.Visibility == Visibility.Visible)
            JobsNotice.Text = "No jobs are running. You can close KOTU now.";
    }

    internal void ShowJobs(string? notice = null)
    {
        if (_jobsClosed) return;
        BringToFront();
        JobsPanel.Visibility = Visibility.Visible;
        JobsNotice.Text = notice ?? string.Empty;
        JobsNotice.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshJobs();
        ResizeJobsPanel();
        if (JobsButton.Visibility == Visibility.Visible) JobsButton.Focus(FocusState.Programmatic);
        else ShellFocusAnchor.Focus(FocusState.Programmatic);
    }

    private void ResizeJobsPanel()
    {
        JobsPanel.Width = Math.Max(160, Math.Min(360, RootLayout.ActualWidth - 16));
        JobsScroll.MaxHeight = Math.Max(80, Math.Min(400, RootLayout.ActualHeight - 180));
    }

    private void HideJobs()
    {
        JobsPanel.Visibility = Visibility.Collapsed;
        foreach (var row in _jobRows.Values) row.ClearPassword();
        RefreshJobs();
        if (JobsButton.Visibility == Visibility.Visible) JobsButton.Focus(FocusState.Programmatic);
        else ShellFocusAnchor.Focus(FocusState.Programmatic);
    }

    private void OnJobsButtonClick(object sender, RoutedEventArgs e)
    {
        if (JobsPanel.Visibility == Visibility.Visible) HideJobs(); else ShowJobs();
    }
    private void OnJobsCloseClick(object sender, RoutedEventArgs e) => HideJobs();

    private sealed class JobRow
    {
        private readonly MainWindow _owner;
        private BackgroundJobSnapshot _snapshot;
        private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
        private readonly PasswordBox _password = new() { PlaceholderText = "Archive password" };
        private readonly Button _submit = new() { Content = "Continue" };
        private readonly Button _cancel = new() { Content = "Cancel" };
        private readonly Button _open = new() { Content = "Open folder" };
        private readonly Button _dismiss = new() { Content = "Dismiss" };
        public StackPanel Root { get; } = new() { Spacing = 6 };

        public JobRow(MainWindow owner, BackgroundJobSnapshot snapshot)
        {
            _owner = owner;
            _snapshot = snapshot;
            Root.Children.Add(_title);
            Root.Children.Add(_state);
            Root.Children.Add(_progress);
            Root.Children.Add(_error);
            Root.Children.Add(_password);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var button in new[] { _submit, _cancel, _open, _dismiss }) buttons.Children.Add(button);
            Root.Children.Add(buttons);
            _submit.Click += (_, _) => SubmitPassword();
            _password.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter) return;
                SubmitPassword();
                e.Handled = true;
            };
            _cancel.Click += (_, _) => _owner._manager.Jobs.Cancel(_snapshot.Id);
            _dismiss.Click += (_, _) => _owner._manager.Jobs.Dismiss(_snapshot.Id);
            _open.Click += async (_, _) =>
            {
                var path = _snapshot.ResultPath;
                if (_snapshot.State != BackgroundJobState.Succeeded || string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    // 결과 위치를 여는 셸 호출도 UI 밖에서 한다.
                    await Task.Run(() =>
                    {
                        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                            throw new DirectoryNotFoundException("The result folder is no longer available.");
                        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                    });
                }
                catch (Exception ex)
                {
                    if (!_owner._jobsClosed) { _error.Text = ex.Message; _error.Visibility = Visibility.Visible; }
                }
            };
        }

        public void Update(BackgroundJobSnapshot snapshot)
        {
            if (_snapshot.PasswordRequestId != snapshot.PasswordRequestId || snapshot.State != BackgroundJobState.WaitingForPassword)
                ClearPassword();
            _snapshot = snapshot;
            _title.Text = snapshot.Title;
            var progress = double.IsFinite(snapshot.Progress) ? Math.Clamp(snapshot.Progress, 0, 1) : 0;
            _progress.Value = progress * 100;
            _progress.Visibility = snapshot.IsActive ? Visibility.Visible : Visibility.Collapsed;
            _state.Text = snapshot.State switch
            {
                BackgroundJobState.Running => $"Running - {progress:P0}",
                BackgroundJobState.WaitingForPassword => "Password needed",
                BackgroundJobState.Canceling => "Canceling - waiting for the current operation",
                BackgroundJobState.Succeeded => "Completed",
                BackgroundJobState.Failed => "Failed - partial files may remain",
                _ => "Canceled - partial files may remain",
            };
            _error.Text = snapshot.Error ?? string.Empty;
            _error.Visibility = string.IsNullOrWhiteSpace(snapshot.Error) ? Visibility.Collapsed : Visibility.Visible;
            var waiting = snapshot.State == BackgroundJobState.WaitingForPassword;
            if (!waiting && _owner.IsFocusWithin(_password)) _owner.JobsButton.Focus(FocusState.Programmatic);
            _password.Visibility = _submit.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
            _cancel.Visibility = snapshot.IsActive ? Visibility.Visible : Visibility.Collapsed;
            _cancel.IsEnabled = snapshot.State != BackgroundJobState.Canceling;
            _dismiss.Visibility = snapshot.IsActive ? Visibility.Collapsed : Visibility.Visible;
            _open.Visibility = snapshot.State == BackgroundJobState.Succeeded && !string.IsNullOrWhiteSpace(snapshot.ResultPath)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        public void ClearPassword() => _password.Password = string.Empty;
        private void SubmitPassword()
        {
            var password = _password.Password;
            ClearPassword();
            if (_snapshot.PasswordRequestId is { } request)
                _owner._manager.Jobs.ProvidePassword(_snapshot.Id, request, password);
        }
    }
}
