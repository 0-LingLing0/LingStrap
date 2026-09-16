namespace Lingstrap.Services;

/// <summary>The only place that knows about the concrete dialog type.</summary>
public static class LaunchProgressDialogFactory
{
    public static ILaunchProgressDialog Create()
    {
        var dialog = new Views.BootstrapDialog();

        if (!SettingsService.Current.ShowLoadingScreen)
            return new SilentUntilSlowLaunchProgressDialog(dialog);

        dialog.Show();
        return dialog;
    }
}
