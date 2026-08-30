namespace JACKOBsWartalesModLauncher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        try
        {
            var gameDir = LauncherCore.FindKnownGameDirectory();
            if (!string.IsNullOrWhiteSpace(gameDir))
                LauncherCore.ReconcilePersistedState(gameDir);
        }
        catch
        {
            // Startup recovery is conservative and best-effort. If it cannot prove the
            // current files match the captured baseline, normal launcher safety checks
            // remain responsible for reporting the mismatch to the user.
        }

        var form = new MainForm();
        try
        {
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is not null) form.Icon = icon;
        }
        catch
        {
            // The executable icon is cosmetic; launcher startup must not depend on it.
        }

        Application.Run(form);
    }
}
