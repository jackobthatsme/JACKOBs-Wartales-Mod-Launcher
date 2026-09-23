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
            {
                LauncherCore.ReconcilePersistedState(gameDir);

                if (LauncherCore.NeedsGameUpdateRecovery(gameDir))
                {
                    var answer = MessageBox.Show(
                        "The launcher detected that one or more files it manages changed outside the launcher.\n\n" +
                        "This commonly happens after a Wartales update, but it can also be caused by another modding tool.\n\n" +
                        "BEFORE RESETTING THE LAUNCHER STATE:\n" +
                        "1. Close Wartales.\n" +
                        "2. In Steam: Wartales > Properties > Installed Files > Verify integrity of game files.\n" +
                        "3. Wait for Steam to finish, then return here.\n\n" +
                        "If you have ALREADY verified the game files, click Yes. The launcher will forget its old mod/baseline state and archive that metadata as a backup. It will NOT modify any Wartales game files.\n\n" +
                        "Click No to exit now and verify the game files first.",
                        "Wartales update detected - launcher recovery",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);

                    if (answer != DialogResult.Yes)
                        return;

                    var backupDir = LauncherCore.ResetLauncherStateAfterGameUpdate(gameDir);
                    var backupNote = string.IsNullOrWhiteSpace(backupDir)
                        ? "No previous launcher state directory was present."
                        : $"The old launcher state was archived at:\n{backupDir}";

                    MessageBox.Show(
                        "Launcher state reset completed. No Wartales game files were changed.\n\n" +
                        backupNote +
                        "\n\nInstall only mod versions that are compatible with the current Wartales patch.",
                        "Launcher recovery completed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "The launcher could not complete its startup recovery check. No recovery changes were made.\n\n" + ex.Message,
                "JACKOB's Wartales Mod Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
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
