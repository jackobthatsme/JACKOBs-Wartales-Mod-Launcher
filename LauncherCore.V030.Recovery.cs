namespace JACKOBsWartalesModLauncher;

internal static partial class LauncherCore
{
    /// <summary>
    /// Repairs stale persisted metadata when the game files already match the launcher's
    /// captured clean baseline. This is intentionally read/metadata-only with respect to
    /// the game directory: it never rewrites game files.
    /// </summary>
    public static void ReconcilePersistedState(string gameDir)
    {
        EnsureGameDirectory(gameDir);
        var state = LoadState(gameDir);

        if (state.InstalledMods.Count == 0) return;
        if (state.ManagedEntries.Count == 0 && state.ManagedFiles.Count == 0) return;
        if (!CurrentGameMatchesCapturedBaseline(gameDir, state)) return;

        var stateDir = GetStateDirectory(gameDir);
        var stalePackages = state.InstalledMods
            .Select(x => x.PackageFile)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        state.InstalledMods.Clear();

        foreach (var managed in state.ManagedEntries.Values)
            managed.LastAppliedHash = managed.OriginalHash;

        foreach (var managed in state.ManagedFiles.Values)
        {
            managed.LastAppliedExists = managed.OriginalExists;
            managed.LastAppliedHash = managed.OriginalHash;
        }

        SaveState(gameDir, state);

        foreach (var package in stalePackages)
            TryDelete(Path.Combine(stateDir, package.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool CurrentGameMatchesCapturedBaseline(string gameDir, LauncherState state)
    {
        try
        {
            if (state.ManagedEntries.Count > 0)
            {
                var pak = PakArchive.Open(Path.Combine(gameDir, "res.pak"));
                foreach (var managed in state.ManagedEntries.Values)
                {
                    var actual = Sha256(pak.ReadEntry(managed.Path));
                    if (!actual.Equals(managed.OriginalHash, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            foreach (var managed in state.ManagedFiles.Values)
            {
                var target = ResolveGameTarget(gameDir, managed.Path);
                var exists = File.Exists(target);
                if (exists != managed.OriginalExists) return false;

                if (exists)
                {
                    var actual = Sha256File(target);
                    if (!actual.Equals(managed.OriginalHash, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            return true;
        }
        catch
        {
            // Recovery must never guess. Any unreadable/missing/inconsistent baseline
            // leaves the state untouched so the normal safety checks can report it.
            return false;
        }
    }
}
