namespace JACKOBsWartalesModLauncher;

internal static partial class LauncherCore
{
    /// <summary>
    /// Returns true when the launcher still has installed mods recorded, but one or more
    /// launcher-managed game files no longer match the last state written by the launcher.
    /// This is the state commonly produced by a Wartales/Steam update while mods were installed.
    /// The check is read-only and never changes game files or launcher metadata.
    /// </summary>
    public static bool NeedsGameUpdateRecovery(string gameDir)
    {
        EnsureGameDirectory(gameDir);
        var state = LoadState(gameDir);

        if (state.InstalledMods.Count == 0) return false;
        if (state.ManagedEntries.Count == 0 && state.ManagedFiles.Count == 0) return false;

        try
        {
            if (state.ManagedEntries.Count > 0)
            {
                var pak = PakArchive.Open(Path.Combine(gameDir, "res.pak"));
                foreach (var managed in state.ManagedEntries.Values)
                {
                    var actual = Sha256(pak.ReadEntry(managed.Path));
                    if (!actual.Equals(managed.LastAppliedHash, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            foreach (var managed in state.ManagedFiles.Values)
            {
                var target = ResolveGameTarget(gameDir, managed.Path);
                var exists = File.Exists(target);
                if (exists != managed.LastAppliedExists)
                    return true;

                if (exists)
                {
                    var actual = Sha256File(target);
                    if (!actual.Equals(managed.LastAppliedHash, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }
        catch
        {
            // If a previously managed file can no longer be read consistently, treat the
            // state as requiring recovery. The recovery path itself still does not touch
            // the game files and requires explicit user confirmation.
            return true;
        }
    }

    /// <summary>
    /// Forgets launcher state after a game update without writing to the Wartales directory.
    /// The old state directory is moved aside as a timestamped backup so recovery remains
    /// reversible for advanced users. The next mod install captures a fresh baseline.
    /// </summary>
    public static string ResetLauncherStateAfterGameUpdate(string gameDir)
    {
        EnsureGameDirectory(gameDir);

        var stateDir = GetStateDirectory(gameDir);
        if (!Directory.Exists(stateDir)) return "";

        var parent = Directory.GetParent(stateDir)?.FullName
                     ?? throw new InvalidOperationException("Could not resolve launcher state directory.");
        var name = Path.GetFileName(stateDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(parent, $"{name}.pre-update-{stamp}");

        var suffix = 1;
        while (Directory.Exists(backupDir))
            backupDir = Path.Combine(parent, $"{name}.pre-update-{stamp}-{suffix++}");

        Directory.Move(stateDir, backupDir);
        return backupDir;
    }
}
