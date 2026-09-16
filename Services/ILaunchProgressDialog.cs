using System;

namespace Lingstrap.Services;

/// <summary>
/// What the launcher can tell a progress UI during a launch, and the one thing the UI can tell
/// the launcher back (the user asked to cancel). The launcher never depends on a concrete dialog
/// type - only this interface - so the visual style can change without touching launch logic.
/// </summary>
public interface ILaunchProgressDialog
{
    event Action? CancelRequested;

    void SetStatus(string status);
    void SetProgress(double percent);
    void SetIndeterminate();
    void ShowError(string message);
    void CloseDialog();
}
