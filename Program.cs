namespace JACKOBsWartalesModLauncher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

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
