using System;
using System.Threading;

namespace Lingstrap.Services;

/// <summary>
/// Wraps a real BootstrapDialog but keeps it hidden - matching "Show loading screen" being off - for
/// a normal, fast launch. If the launch is still going after a couple of seconds, though, it reveals
/// the dialog anyway: the one case a silent launch looks like Lingstrap has frozen is Roblox needing
/// an actual install/update, which can take minutes with zero on-screen feedback otherwise. All
/// status/progress calls forward straight to the real dialog immediately either way - it's fine to
/// update a Window's content before Show() is ever called.
/// </summary>
public sealed class SilentUntilSlowLaunchProgressDialog : ILaunchProgressDialog
{
    private const int RevealAfterMs = 2500;

    private readonly Views.BootstrapDialog _dialog;
    private readonly Timer _revealTimer;
    private readonly object _gate = new();
    private bool _done;

    public event Action? CancelRequested
    {
        add => _dialog.CancelRequested += value;
        remove => _dialog.CancelRequested -= value;
    }

    public SilentUntilSlowLaunchProgressDialog(Views.BootstrapDialog dialog)
    {
        _dialog = dialog;
        _revealTimer = new Timer(_ => Reveal(), null, RevealAfterMs, Timeout.Infinite);
    }

    private void Reveal()
    {
        lock (_gate) { if (_done) return; }

        // Never call Show() while holding _gate - it would block this (background timer) thread
        // waiting on the UI thread, while the UI thread could simultaneously be blocked trying to
        // acquire _gate itself from one of the methods below, deadlocking both.
        _dialog.Dispatcher.Invoke(() =>
        {
            lock (_gate) { if (_done) return; }
            _dialog.Show();
        });
    }

    public void SetStatus(string status) => _dialog.SetStatus(status);
    public void SetProgress(double percent) => _dialog.SetProgress(percent);
    public void SetIndeterminate() => _dialog.SetIndeterminate();

    public void ShowError(string message)
    {
        // An error is worth surfacing even for an otherwise-silent launch.
        Reveal();
        _dialog.ShowError(message);
    }

    public void CloseDialog()
    {
        lock (_gate)
        {
            _done = true;
            _revealTimer.Dispose();
        }
        _dialog.CloseDialog();
    }
}
