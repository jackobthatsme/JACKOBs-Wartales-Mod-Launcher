using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

namespace JACKOBsWartalesModLauncher;

internal static class LauncherCore
{
    public const string LauncherVersion = "0.2.3";
    private const string StateFormat = "JACKOB_WARTALES_LAUNCHER_STATE_V1";
    private const string ModFormat = "JACKOB_WARTALES_MOD_V1";
    private const string CdbPatchFormat = "JACKOB_CDB_PATCH_V1";
    private const string XmlPatchFormat = "JACKOB_XML_PATCH_V1";

    private static readonly JsonSerializerOptions StateJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string SettingsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JACKOBTHATSME", "WartalesLauncher");

    private static string SettingsPath => Path.Combine(SettingsRoot, "settings.json");



    // -------------------- GUI facade --------------------

    public static string? FindKnownGameDirectory()
    {
        var candidates = new List<string>();

        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsPath), StateJson);
                if (!string.IsNullOrWhiteSpace(s?.GameDirectory))
                    candidates.Add(s.GameDirectory!);
            }
        }
        catch { }

        candidates.Add(Environment.CurrentDirectory);
        candidates.Add(AppContext.BaseDirectory);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Wartales"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common", "Wartales"));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsGameDirectory(candidate)) continue;
            var full = Path.GetFullPath(candidate);
            SaveSettings(full);
            return full;
        }

        return null;
    }

    public static bool IsValidGameDirectory(string? path) => IsGameDirectory(path);

    public static void SetGameDirectory(string gameDir)
    {
        var full = Path.GetFullPath(gameDir);
        if (!IsGameDirectory(full))
            throw new InvalidOperationException($"res.pak was not found in: {full}");
        SaveSettings(full);
    }

    public static IReadOnlyList<InstalledModState> GetInstalledMods(string gameDir)
    {
        EnsureGameDirectory(gameDir);
        return LoadState(gameDir).InstalledMods
            .Select(m => new InstalledModState
            {
                Id = m.Id,
                Name = m.Name,
                Version = m.Version,
                PackageFile = m.PackageFile,
                PackageSha256 = m.PackageSha256
            })
            .ToArray();
    }

    public static ModManifest PreviewPackage(string packagePath)
    {
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Mod ZIP was not found.", packagePath);
        using var package = ModPackage.Load(packagePath);
        ValidateManifest(package.Manifest);
        return new ModManifest
        {
            Format = package.Manifest.Format,
            Id = package.Manifest.Id,
            Name = package.Manifest.Name,
            Version = package.Manifest.Version,
            Author = package.Manifest.Author,
            Game = package.Manifest.Game,
            Description = package.Manifest.Description,
            Operations = package.Manifest.Operations.Select(o => new ManifestOperation
            {
                Type = o.Type,
                Entry = o.Entry,
                Source = o.Source
            }).ToList()
        };
    }

    public static void InstallPackage(string gameDir, string packagePath)
    {
        EnsureGameDirectory(gameDir);
        InstallOrUpdate(gameDir, Path.GetFullPath(packagePath));
    }

    public static void UninstallMod(string gameDir, string modId)
    {
        EnsureGameDirectory(gameDir);
        var state = LoadState(gameDir);
        if (state.InstalledMods.Count == 0)
            throw new InvalidOperationException("No managed mods are installed.");

        VerifyManagedState(gameDir, state, allowRebaselineWhenEmpty: false);
        var target = state.InstalledMods.FirstOrDefault(x => x.Id.Equals(modId, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException("The selected mod is no longer installed.");

        state.InstalledMods.Remove(target);
        RebuildManagedEntries(gameDir, state);
        SaveState(gameDir, state);

        var stateDir = GetStateDirectory(gameDir);
        TryDelete(Path.Combine(stateDir, target.PackageFile.Replace('/', Path.DirectorySeparatorChar)));
    }

    public static void RestoreBaseline(string gameDir)
    {
        EnsureGameDirectory(gameDir);
        var state = LoadState(gameDir);
        if (state.ManagedEntries.Count == 0)
            return;

        VerifyManagedState(gameDir, state, allowRebaselineWhenEmpty: false);
        var stateDir = GetStateDirectory(gameDir);
        var oldPackages = state.InstalledMods.Select(x => x.PackageFile).ToArray();
        state.InstalledMods.Clear();
        RebuildManagedEntries(gameDir, state);
        SaveState(gameDir, state);
        foreach (var p in oldPackages)
            TryDelete(Path.Combine(stateDir, p.Replace('/', Path.DirectorySeparatorChar)));
    }

    public static void Verify(string gameDir)
    {
        EnsureGameDirectory(gameDir);
        VerifyManagedState(gameDir, LoadState(gameDir), allowRebaselineWhenEmpty: false);
    }

    private static void EnsureGameDirectory(string gameDir)
    {
        if (!IsGameDirectory(gameDir))
            throw new InvalidOperationException("Select a valid Wartales folder containing res.pak first.");
    }

    public static int LegacyConsoleMain(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Console.Title = $"JACKOB's Wartales Mod Launcher v{LauncherVersion}";

        try
        {
            Directory.CreateDirectory(SettingsRoot);
            PrintLogo();

            var gameDir = ResolveGameDirectory();
            if (gameDir is null)
                return 1;

            while (true)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(new string('=', 72));
                Console.ResetColor();
                Console.WriteLine($"Wartales: {gameDir}");

                var state = LoadState(gameDir);
                PrintInstalled(state);

                Console.WriteLine();
                Console.WriteLine("1 - Install / update a mod ZIP");
                Console.WriteLine("2 - Uninstall one managed mod");
                Console.WriteLine("3 - Restore baseline (remove all managed mods)");
                Console.WriteLine("4 - Change Wartales folder");
                Console.WriteLine("5 - Verify managed files");
                Console.WriteLine("0 - Exit");
                Console.Write("Choose / Wybierz: ");
                var choice = Console.ReadLine()?.Trim();

                try
                {
                    switch (choice)
                    {
                        case "1":
                            InstallInteractive(gameDir, args);
                            args = Array.Empty<string>();
                            break;
                        case "2":
                            UninstallInteractive(gameDir);
                            break;
                        case "3":
                            RestoreAllInteractive(gameDir);
                            break;
                        case "4":
                            gameDir = PromptForGameDirectory() ?? gameDir;
                            SaveSettings(gameDir);
                            break;
                        case "5":
                            VerifyManagedState(gameDir, LoadState(gameDir), allowRebaselineWhenEmpty: false);
                            Success("Managed files match launcher state.");
                            break;
                        case "0":
                        case null:
                            return 0;
                        default:
                            Warn("Unknown option.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Error(ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Error(ex.ToString());
            Console.WriteLine("Press ENTER to exit.");
            Console.ReadLine();
            return 1;
        }
    }

    private static void PrintLogo()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("      ██╗ █████╗  ██████╗██╗  ██╗ ██████╗ ██████╗");
        Console.WriteLine("      ██║██╔══██╗██╔════╝██║ ██╔╝██╔═══██╗██╔══██╗");
        Console.WriteLine("      ██║███████║██║     █████╔╝ ██║   ██║██████╔╝");
        Console.WriteLine(" ██   ██║██╔══██║██║     ██╔═██╗ ██║   ██║██╔══██╗");
        Console.WriteLine(" ╚█████╔╝██║  ██║╚██████╗██║  ██╗╚██████╔╝██████╔╝");
        Console.WriteLine("  ╚════╝ ╚═╝  ╚═╝ ╚═════╝╚═╝  ╚═╝ ╚═════╝ ╚═════╝");
        Console.WriteLine();
        Console.WriteLine("████████╗██╗  ██╗ █████╗ ████████╗███████╗███╗   ███╗███████╗");
        Console.WriteLine("╚══██╔══╝██║  ██║██╔══██╗╚══██╔══╝██╔════╝████╗ ████║██╔════╝");
        Console.WriteLine("   ██║   ███████║███████║   ██║   ███████╗██╔████╔██║█████╗");
        Console.WriteLine("   ██║   ██╔══██║██╔══██║   ██║   ╚════██║██║╚██╔╝██║██╔══╝");
        Console.WriteLine("   ██║   ██║  ██║██║  ██║   ██║   ███████║██║ ╚═╝ ██║███████╗");
        Console.WriteLine("   ╚═╝   ╚═╝  ╚═╝╚═╝  ╚═╝   ╚═╝   ╚══════╝╚═╝     ╚═╝╚══════╝");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"JACKOB's WARTALES MOD LAUNCHER v{LauncherVersion}");
        Console.WriteLine("Offline • no PowerShell • no downloads • no registry changes");
    }

    // -------------------- UI / settings --------------------

    private static string? ResolveGameDirectory()
    {
        var candidates = new List<string>();

        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsPath), StateJson);
                if (!string.IsNullOrWhiteSpace(s?.GameDirectory))
                    candidates.Add(s.GameDirectory!);
            }
        }
        catch { }

        candidates.Add(Environment.CurrentDirectory);
        candidates.Add(AppContext.BaseDirectory);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Wartales"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common", "Wartales"));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsGameDirectory(candidate))
            {
                SaveSettings(candidate);
                return Path.GetFullPath(candidate);
            }
        }

        return PromptForGameDirectory();
    }

    private static string? PromptForGameDirectory()
    {
        Console.WriteLine();
        Console.WriteLine("Wartales res.pak was not found automatically.");
        Console.WriteLine("Paste the Wartales folder path (you can drag the folder into this window).");
        Console.Write("Path: ");
        var raw = Unquote(Console.ReadLine()?.Trim());
        if (string.IsNullOrWhiteSpace(raw)) return null;

        raw = Path.GetFullPath(raw);
        if (!IsGameDirectory(raw))
            throw new InvalidOperationException($"res.pak was not found in: {raw}");

        SaveSettings(raw);
        return raw;
    }

    private static bool IsGameDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "res.pak"));

    private static string? Unquote(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s[1..^1];
        return s;
    }

    private static void SaveSettings(string gameDir)
    {
        Directory.CreateDirectory(SettingsRoot);
        File.WriteAllText(SettingsPath,
            JsonSerializer.Serialize(new LauncherSettings { GameDirectory = Path.GetFullPath(gameDir) }, StateJson),
            new UTF8Encoding(false));
    }

    private static void PrintInstalled(LauncherState state)
    {
        Console.WriteLine();
        Console.WriteLine("Managed mods:");
        if (state.InstalledMods.Count == 0)
        {
            Console.WriteLine("  (none)");
            return;
        }

        for (var i = 0; i < state.InstalledMods.Count; i++)
        {
            var m = state.InstalledMods[i];
            Console.WriteLine($"  {i + 1}. {m.Name} v{m.Version}");
        }
    }

    // -------------------- install / uninstall --------------------

    private static void InstallInteractive(string gameDir, string[] args)
    {
        var package = ResolvePackagePath(args);
        if (package is null) return;

        ModManifest previewManifest;
        using (var preview = ModPackage.Load(package))
            previewManifest = preview.Manifest;
        Console.WriteLine();
        Console.WriteLine($"Mod: {previewManifest.Name}");
        Console.WriteLine($"Version: {previewManifest.Version}");
        Console.WriteLine($"Author: {previewManifest.Author}");
        if (!string.IsNullOrWhiteSpace(previewManifest.Description))
            Console.WriteLine(previewManifest.Description);
        Console.Write("Install / update? [Y/N]: ");
        if (!IsYes(Console.ReadLine())) return;

        InstallOrUpdate(gameDir, package);
        Success($"Installed: {previewManifest.Name} v{previewManifest.Version}");
    }

    private static string? ResolvePackagePath(string[] args)
    {
        if (args.Length > 0 && File.Exists(args[0]))
            return Path.GetFullPath(args[0]);

        var dirs = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        var found = new List<(string Path, ModManifest Manifest)>();
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.zip", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var p = ModPackage.Load(file);
                    found.Add((file, p.Manifest));
                }
                catch { }
            }
        }

        if (found.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Launcher-compatible ZIP packages found:");
            for (var i = 0; i < found.Count; i++)
                Console.WriteLine($"  {i + 1}. {found[i].Manifest.Name} v{found[i].Manifest.Version}");
            Console.WriteLine("  0. Enter another path");
            Console.Write("Choose: ");
            if (int.TryParse(Console.ReadLine(), out var n) && n >= 1 && n <= found.Count)
                return Path.GetFullPath(found[n - 1].Path);
        }

        Console.WriteLine("Paste a launcher-compatible mod ZIP path (or drag the ZIP here).");
        Console.Write("ZIP: ");
        var raw = Unquote(Console.ReadLine()?.Trim());
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!File.Exists(raw)) throw new FileNotFoundException("Mod ZIP was not found.", raw);
        return Path.GetFullPath(raw);
    }

    private static void InstallOrUpdate(string gameDir, string packagePath)
    {
        var pakPath = Path.Combine(gameDir, "res.pak");
        var state = LoadState(gameDir);
        VerifyManagedState(gameDir, state, allowRebaselineWhenEmpty: true);

        using var package = ModPackage.Load(packagePath);
        ValidateManifest(package.Manifest);

        var stateDir = GetStateDirectory(gameDir);
        Directory.CreateDirectory(Path.Combine(stateDir, "packages"));
        Directory.CreateDirectory(Path.Combine(stateDir, "originals"));

        CaptureOriginalEntries(pakPath, gameDir, state, package.Manifest);

        var old = state.InstalledMods.FirstOrDefault(x => x.Id.Equals(package.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        var storedName = MakeSafeFileName(package.Manifest.Id) + "-" + MakeSafeFileName(package.Manifest.Version) + ".zip";
        var storedFullPath = Path.Combine(stateDir, "packages", storedName);
        File.Copy(packagePath, storedFullPath, overwrite: true);

        if (old is not null)
            state.InstalledMods.Remove(old);

        state.InstalledMods.Add(new InstalledModState
        {
            Id = package.Manifest.Id,
            Name = package.Manifest.Name,
            Version = package.Manifest.Version,
            PackageFile = Path.GetRelativePath(stateDir, storedFullPath).Replace('\\', '/'),
            PackageSha256 = Sha256File(storedFullPath)
        });

        try
        {
            RebuildManagedEntries(gameDir, state);
            SaveState(gameDir, state);
            if (old is not null && !old.PackageFile.Equals(state.InstalledMods[^1].PackageFile, StringComparison.OrdinalIgnoreCase))
            {
                var oldPath = Path.Combine(stateDir, old.PackageFile.Replace('/', Path.DirectorySeparatorChar));
                TryDelete(oldPath);
            }
        }
        catch
        {
            TryDelete(storedFullPath);
            throw;
        }
    }

    private static void UninstallInteractive(string gameDir)
    {
        var state = LoadState(gameDir);
        if (state.InstalledMods.Count == 0)
        {
            Warn("No managed mods are installed.");
            return;
        }

        VerifyManagedState(gameDir, state, allowRebaselineWhenEmpty: false);
        Console.WriteLine();
        for (var i = 0; i < state.InstalledMods.Count; i++)
            Console.WriteLine($"{i + 1} - {state.InstalledMods[i].Name} v{state.InstalledMods[i].Version}");
        Console.Write("Remove which mod? ");
        if (!int.TryParse(Console.ReadLine(), out var n) || n < 1 || n > state.InstalledMods.Count)
            return;

        var target = state.InstalledMods[n - 1];
        Console.Write($"Uninstall {target.Name}? [Y/N]: ");
        if (!IsYes(Console.ReadLine())) return;

        state.InstalledMods.RemoveAt(n - 1);
        RebuildManagedEntries(gameDir, state);
        SaveState(gameDir, state);

        var stateDir = GetStateDirectory(gameDir);
        TryDelete(Path.Combine(stateDir, target.PackageFile.Replace('/', Path.DirectorySeparatorChar)));
        Success($"Uninstalled: {target.Name}");
    }

    private static void RestoreAllInteractive(string gameDir)
    {
        var state = LoadState(gameDir);
        if (state.ManagedEntries.Count == 0)
        {
            Warn("Nothing has been managed yet.");
            return;
        }

        VerifyManagedState(gameDir, state, allowRebaselineWhenEmpty: false);
        Console.Write("Remove all launcher-managed mods and restore the captured baseline? [Y/N]: ");
        if (!IsYes(Console.ReadLine())) return;

        var stateDir = GetStateDirectory(gameDir);
        var oldPackages = state.InstalledMods.Select(x => x.PackageFile).ToArray();
        state.InstalledMods.Clear();
        RebuildManagedEntries(gameDir, state);
        SaveState(gameDir, state);
        foreach (var p in oldPackages)
            TryDelete(Path.Combine(stateDir, p.Replace('/', Path.DirectorySeparatorChar)));
        Success("All launcher-managed mods were removed. Baseline restored.");
    }

    // -------------------- state --------------------

    private static string GetStateDirectory(string gameDir)
    {
        var normalized = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..16];
        return Path.Combine(SettingsRoot, "games", id);
    }

    private static string GetStatePath(string gameDir) => Path.Combine(GetStateDirectory(gameDir), "state.json");

    private static LauncherState LoadState(string gameDir)
    {
        var path = GetStatePath(gameDir);
        if (!File.Exists(path))
            return NewState(gameDir);

        var state = JsonSerializer.Deserialize<LauncherState>(File.ReadAllText(path), StateJson)
                    ?? throw new InvalidDataException("Launcher state is invalid.");
        if (state.Format != StateFormat)
            throw new InvalidDataException("Unsupported launcher state format.");
        return state;
    }

    private static LauncherState NewState(string gameDir) => new()
    {
        Format = StateFormat,
        GameDirectory = Path.GetFullPath(gameDir)
    };

    private static void SaveState(string gameDir, LauncherState state)
    {
        var dir = GetStateDirectory(gameDir);
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "state.json.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(state, StateJson), new UTF8Encoding(false));
        File.Move(temp, GetStatePath(gameDir), overwrite: true);
    }

    private static void VerifyManagedState(string gameDir, LauncherState state, bool allowRebaselineWhenEmpty)
    {
        if (state.ManagedEntries.Count == 0) return;
        var pak = PakArchive.Open(Path.Combine(gameDir, "res.pak"));
        var mismatches = new List<string>();
        foreach (var managed in state.ManagedEntries.Values)
        {
            var bytes = pak.ReadEntry(managed.Path);
            var actual = Sha256(bytes);
            if (!actual.Equals(managed.LastAppliedHash, StringComparison.OrdinalIgnoreCase))
                mismatches.Add(managed.Path);
        }

        if (mismatches.Count == 0) return;

        if (allowRebaselineWhenEmpty && state.InstalledMods.Count == 0)
        {
            var stateDir = GetStateDirectory(gameDir);
            foreach (var m in state.ManagedEntries.Values)
                TryDelete(Path.Combine(stateDir, m.OriginalFile.Replace('/', Path.DirectorySeparatorChar)));
            state.ManagedEntries.Clear();
            SaveState(gameDir, state);
            Warn("Game files changed while no managed mods were active. Launcher baseline was reset to the current game files.");
            return;
        }

        throw new InvalidOperationException(
            "res.pak changed outside the launcher for: " + string.Join(", ", mismatches) +
            ". This may be a game update or another mod. The launcher stopped without writing anything.");
    }

    private static void CaptureOriginalEntries(string pakPath, string gameDir, LauncherState state, ModManifest manifest)
    {
        var pak = PakArchive.Open(pakPath);
        var stateDir = GetStateDirectory(gameDir);
        foreach (var entryPath in manifest.Operations.Select(x => NormalizePakPath(x.Entry)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = entryPath.ToLowerInvariant();
            if (state.ManagedEntries.ContainsKey(key)) continue;

            var bytes = pak.ReadEntry(entryPath);
            var hash = Sha256(bytes);
            var originalName = Path.Combine("originals", Sha256(Encoding.UTF8.GetBytes(entryPath))[..24] + ".bin").Replace('\\', '/');
            var originalFull = Path.Combine(stateDir, originalName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(originalFull)!);
            File.WriteAllBytes(originalFull, bytes);

            state.ManagedEntries[key] = new ManagedEntryState
            {
                Path = entryPath,
                OriginalFile = originalName,
                OriginalHash = hash,
                LastAppliedHash = hash
            };
        }
    }

    // -------------------- rebuild / patch engine --------------------

    private static void RebuildManagedEntries(string gameDir, LauncherState state)
    {
        var stateDir = GetStateDirectory(gameDir);
        var working = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in state.ManagedEntries.Values)
        {
            var original = Path.Combine(stateDir, m.OriginalFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(original))
                throw new FileNotFoundException("Original baseline entry is missing.", original);
            var bytes = File.ReadAllBytes(original);
            if (!Sha256(bytes).Equals(m.OriginalHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Baseline checksum failed for {m.Path}.");
            working[m.Path] = bytes;
        }

        foreach (var installed in state.InstalledMods)
        {
            var packagePath = Path.Combine(stateDir, installed.PackageFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(packagePath))
                throw new FileNotFoundException($"Stored package for {installed.Name} is missing.", packagePath);
            if (!Sha256File(packagePath).Equals(installed.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Stored package checksum failed for {installed.Name}.");

            using var package = ModPackage.Load(packagePath);
            ApplyPackage(package, working);
        }

        var pakPath = Path.Combine(gameDir, "res.pak");
        PakArchive.ReplaceEntries(pakPath, working);

        var verifyPak = PakArchive.Open(pakPath);
        foreach (var m in state.ManagedEntries.Values)
        {
            var actual = Sha256(verifyPak.ReadEntry(m.Path));
            var expected = Sha256(working[m.Path]);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Post-install verification failed for {m.Path}.");
            m.LastAppliedHash = actual;
        }
    }

    private static void ApplyPackage(ModPackage package, Dictionary<string, byte[]> working)
    {
        ValidateManifest(package.Manifest);
        foreach (var op in package.Manifest.Operations)
        {
            var entry = NormalizePakPath(op.Entry);
            if (!working.ContainsKey(entry))
                throw new InvalidOperationException($"Launcher baseline does not contain required PAK entry: {entry}");

            switch (op.Type)
            {
                case "cdbPatch":
                    working[entry] = ApplyCdbPatch(working[entry], package.ReadJson(op.Source));
                    break;
                case "xmlMerge":
                    working[entry] = ApplyXmlPatch(working[entry], package.ReadJson(op.Source));
                    break;
                case "replaceEntry":
                    working[entry] = package.ReadBytes(op.Source);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported package operation: {op.Type}");
            }
        }
    }

    private static byte[] ApplyCdbPatch(byte[] input, JsonNode patchNode)
    {
        var patch = patchNode.AsObject();
        if (patch["format"]?.GetValue<string>() != CdbPatchFormat)
            throw new InvalidDataException("Unsupported CDB patch format.");

        var root = JsonNode.Parse(input) ?? throw new InvalidDataException("data.cdb is not valid JSON.");
        var operations = patch["operations"]?.AsArray() ?? throw new InvalidDataException("CDB patch has no operations array.");

        foreach (var n in operations)
        {
            var op = n?.AsObject() ?? throw new InvalidDataException("Invalid CDB patch operation.");
            var kind = op["op"]?.GetValue<string>() ?? "";
            var sheetName = op["sheet"]?.GetValue<string>() ?? throw new InvalidDataException("CDB operation has no sheet.");
            var sheet = FindSheet(root, sheetName);
            var lines = sheet["lines"]?.AsArray() ?? throw new InvalidDataException($"Sheet {sheetName} has no lines array.");

            if (kind == "addLine")
            {
                var line = op["line"]?.AsObject() ?? throw new InvalidDataException("addLine has no line.");
                var id = line["id"]?.GetValue<string>() ?? throw new InvalidDataException("addLine line has no id.");
                var existing = FindLineOrNull(lines, id);
                if (existing is not null)
                {
                    if (!JsonNode.DeepEquals(existing, line))
                        throw new InvalidOperationException($"CDB conflict: {sheetName}/{id} already exists with different data.");
                    continue;
                }
                lines.Add(line.DeepClone());
                continue;
            }

            if (kind == "removeLine")
            {
                var id = op["id"]?.GetValue<string>() ?? throw new InvalidDataException("removeLine has no id.");
                var idx = FindLineIndex(lines, id);
                if (idx >= 0) lines.RemoveAt(idx);
                continue;
            }

            if (kind != "set")
                throw new InvalidDataException($"Unsupported CDB patch op: {kind}");

            var lineId = op["id"]?.GetValue<string>() ?? throw new InvalidDataException("set has no id.");
            var lineObj = FindLine(lines, lineId);
            var path = op["path"]?.AsArray() ?? throw new InvalidDataException("set has no path.");
            var current = GetAtPath(lineObj, path);
            var expected = op["expected"];
            if (!JsonNode.DeepEquals(current, expected))
            {
                throw new InvalidOperationException(
                    $"CDB compatibility check failed at {sheetName}/{lineId}/{FormatPath(path)}. " +
                    $"Expected {NodeText(expected)}, found {NodeText(current)}. No PAK data was written.");
            }
            SetAtPath(lineObj, path, op["value"]?.DeepClone());
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString(CompactJson));
    }

    private static JsonObject FindSheet(JsonNode root, string name)
    {
        var sheets = root["sheets"]?.AsArray() ?? throw new InvalidDataException("data.cdb has no sheets array.");
        foreach (var n in sheets)
        {
            if (n is JsonObject o && o["name"]?.GetValue<string>() == name)
                return o;
        }
        throw new InvalidDataException($"data.cdb sheet not found: {name}");
    }

    private static JsonObject FindLine(JsonArray lines, string id) =>
        FindLineOrNull(lines, id) ?? throw new InvalidDataException($"data.cdb line not found: {id}");

    private static JsonObject? FindLineOrNull(JsonArray lines, string id)
    {
        foreach (var n in lines)
        {
            if (n is JsonObject o && o["id"]?.GetValue<string>() == id)
                return o;
        }
        return null;
    }

    private static int FindLineIndex(JsonArray lines, string id)
    {
        for (var i = 0; i < lines.Count; i++)
            if (lines[i] is JsonObject o && o["id"]?.GetValue<string>() == id)
                return i;
        return -1;
    }

    private static JsonNode? GetAtPath(JsonNode start, JsonArray path)
    {
        JsonNode? current = start;
        foreach (var token in path)
        {
            if (current is null) return null;
            if (token is JsonValue v && v.TryGetValue<int>(out var idx))
                current = current.AsArray()[idx];
            else
                current = current.AsObject()[token?.GetValue<string>() ?? throw new InvalidDataException("Invalid path token.")];
        }
        return current;
    }

    private static void SetAtPath(JsonNode start, JsonArray path, JsonNode? value)
    {
        if (path.Count == 0) throw new InvalidDataException("Empty set path is not supported.");
        JsonNode current = start;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var token = path[i]!;
            if (token is JsonValue v && v.TryGetValue<int>(out var idx))
                current = current.AsArray()[idx] ?? throw new InvalidDataException("Null path node.");
            else
                current = current.AsObject()[token.GetValue<string>()] ?? throw new InvalidDataException("Missing path node.");
        }

        var last = path[^1]!;
        if (last is JsonValue lv && lv.TryGetValue<int>(out var li))
            current.AsArray()[li] = value;
        else
            current.AsObject()[last.GetValue<string>()] = value;
    }

    private static byte[] ApplyXmlPatch(byte[] input, JsonNode patchNode)
    {
        var patch = patchNode.AsObject();
        if (patch["format"]?.GetValue<string>() != XmlPatchFormat)
            throw new InvalidDataException("Unsupported XML patch format.");

        var text = Encoding.UTF8.GetString(RemoveUtf8Bom(input));
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(text);

        var nodes = patch["nodes"]?.AsArray() ?? throw new InvalidDataException("XML patch has no nodes array.");
        foreach (var n in nodes)
        {
            var item = n?.AsObject() ?? throw new InvalidDataException("Invalid XML patch node.");
            var sheetName = item["sheet"]?.GetValue<string>() ?? throw new InvalidDataException("XML node has no sheet.");
            var id = item["id"]?.GetValue<string>() ?? throw new InvalidDataException("XML node has no id.");
            var xml = item["xml"]?.GetValue<string>() ?? throw new InvalidDataException("XML node has no xml.");

            var sheet = FindXmlSheet(doc, sheetName);
            XmlElement? existing = null;
            foreach (XmlNode child in sheet.ChildNodes)
            {
                if (child is XmlElement e && e.Name == id) { existing = e; break; }
            }

            var fragmentDoc = new XmlDocument { PreserveWhitespace = true };
            fragmentDoc.LoadXml(xml);
            var imported = doc.ImportNode(fragmentDoc.DocumentElement!, true);

            if (existing is not null)
                sheet.ReplaceChild(imported, existing);
            else
            {
                sheet.AppendChild(doc.CreateWhitespace("\n        "));
                sheet.AppendChild(imported);
            }
        }

        return Encoding.UTF8.GetBytes(doc.OuterXml);
    }

    private static XmlElement FindXmlSheet(XmlDocument doc, string name)
    {
        var root = doc.DocumentElement ?? throw new InvalidDataException("XML has no root element.");
        foreach (XmlNode n in root.ChildNodes)
            if (n is XmlElement e && e.Name == "sheet" && e.GetAttribute("name") == name)
                return e;
        throw new InvalidDataException($"Localization sheet not found: {name}");
    }

    private static ReadOnlySpan<byte> RemoveUtf8Bom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return bytes.AsSpan(3);
        return bytes;
    }

    // -------------------- package --------------------

    private static void ValidateManifest(ModManifest manifest)
    {
        if (manifest.Format != ModFormat) throw new InvalidDataException("Unsupported mod package format.");
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("Mod manifest is missing id, name or version.");
        if (manifest.Operations.Count == 0) throw new InvalidDataException("Mod package has no operations.");
        foreach (var op in manifest.Operations)
        {
            if (string.IsNullOrWhiteSpace(op.Type) || string.IsNullOrWhiteSpace(op.Entry) || string.IsNullOrWhiteSpace(op.Source))
                throw new InvalidDataException("Mod operation is incomplete.");
            if (op.Source.Contains("..") || Path.IsPathRooted(op.Source))
                throw new InvalidDataException("Unsafe package source path.");
        }
    }

    private static string NormalizePakPath(string path) => path.Replace('\\', '/').TrimStart('/');

    // -------------------- helpers --------------------

    private static bool IsYes(string? value) =>
        string.Equals(value?.Trim(), "y", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value?.Trim(), "yes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value?.Trim(), "t", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value?.Trim(), "tak", StringComparison.OrdinalIgnoreCase);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        return sb.ToString();
    }

    private static string FormatPath(JsonArray path) => string.Join("/", path.Select(x => x?.ToJsonString().Trim('"') ?? "null"));
    private static string NodeText(JsonNode? node) => node?.ToJsonString() ?? "null";
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static void Success(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(s); Console.ResetColor(); }
    private static void Warn(string s) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(s); Console.ResetColor(); }
    private static void Error(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("ERROR: " + s); Console.ResetColor(); }
}

// ==================== package model ====================

internal sealed class ModPackage : IDisposable
{
    private readonly FileStream _stream;
    private readonly ZipArchive _zip;
    public ModManifest Manifest { get; }

    private ModPackage(string path)
    {
        _stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _zip = new ZipArchive(_stream, ZipArchiveMode.Read, leaveOpen: false);
        Manifest = ReadManifest();
    }

    public static ModPackage Load(string path) => new(path);

    private ModManifest ReadManifest()
    {
        var node = ReadJson("manifest.json");
        return node.Deserialize<ModManifest>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException("manifest.json is invalid.");
    }

    public byte[] ReadBytes(string name)
    {
        var normalized = name.Replace('\\', '/');
        var entry = _zip.Entries.FirstOrDefault(e => e.FullName.Equals(normalized, StringComparison.Ordinal))
                    ?? throw new InvalidDataException($"Package file not found: {name}");
        using var input = entry.Open();
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }

    public JsonNode ReadJson(string name) =>
        JsonNode.Parse(ReadBytes(name)) ?? throw new InvalidDataException($"Invalid JSON: {name}");

    public void Dispose()
    {
        _zip.Dispose();
        _stream.Dispose();
    }
}

internal sealed class ModManifest
{
    public string Format { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Game { get; set; } = "";
    public string Description { get; set; } = "";
    public List<ManifestOperation> Operations { get; set; } = new();
}

internal sealed class ManifestOperation
{
    public string Type { get; set; } = "";
    public string Entry { get; set; } = "";
    public string Source { get; set; } = "";
}

// ==================== state model ====================

internal sealed class LauncherSettings
{
    public string? GameDirectory { get; set; }
}

internal sealed class LauncherState
{
    public string Format { get; set; } = "";
    public string GameDirectory { get; set; } = "";
    public List<InstalledModState> InstalledMods { get; set; } = new();
    public Dictionary<string, ManagedEntryState> ManagedEntries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class InstalledModState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string PackageFile { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
}

internal sealed class ManagedEntryState
{
    public string Path { get; set; } = "";
    public string OriginalFile { get; set; } = "";
    public string OriginalHash { get; set; } = "";
    public string LastAppliedHash { get; set; } = "";
}

// ==================== Shiro PAK ====================

internal sealed class PakArchive
{
    public string Path { get; }
    public long DataOffset { get; }
    public uint HeaderFileSize { get; }
    public long FileLength { get; }
    public IReadOnlyDictionary<string, PakEntry> Entries { get; }

    private PakArchive(string path, long dataOffset, uint headerFileSize, long fileLength, Dictionary<string, PakEntry> entries)
    {
        Path = path;
        DataOffset = dataOffset;
        HeaderFileSize = headerFileSize;
        FileLength = fileLength;
        Entries = entries;
    }

    public static PakArchive Open(string path)
    {
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
        var magic = br.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != 0x50 || magic[1] != 0x41 || magic[2] != 0x4B || magic[3] != 0x00)
            throw new InvalidDataException("Unsupported res.pak: PAK\\0 header not found.");

        var dataOffset = br.ReadUInt32();
        var headerFileSize = br.ReadUInt32();
        _ = br.ReadUInt16();
        var rootCount = br.ReadUInt32();
        var entries = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);

        void ReadNode(string folder)
        {
            var nameLen = br.ReadByte();
            var nameBytes = br.ReadBytes(nameLen);
            if (nameBytes.Length != nameLen) throw new EndOfStreamException("Unexpected end of PAK index.");
            var name = Encoding.UTF8.GetString(nameBytes);
            var type = br.ReadByte();

            if (type is 0 or 2)
            {
                var offsetFieldPos = fs.Position;
                long relativeOffset = type == 0
                    ? br.ReadUInt32()
                    : Convert.ToInt64(Math.Round(br.ReadDouble()));
                var sizeFieldPos = fs.Position;
                var size = br.ReadUInt32();
                _ = br.ReadUInt32();
                var full = string.IsNullOrEmpty(folder) ? name : folder + "/" + name;
                full = full.TrimStart('/');
                if (!entries.TryAdd(full, new PakEntry(full, type, relativeOffset, dataOffset + relativeOffset, offsetFieldPos, sizeFieldPos, size)))
                    throw new InvalidDataException("Duplicate PAK entry: " + full);
                return;
            }

            if (type == 1)
            {
                var next = string.IsNullOrEmpty(folder) ? name : folder + "/" + name;
                var childCount = br.ReadUInt32();
                for (uint i = 0; i < childCount; i++) ReadNode(next);
                return;
            }

            throw new InvalidDataException("Unsupported PAK entry type: " + type);
        }

        for (uint i = 0; i < rootCount; i++) ReadNode("");
        return new PakArchive(path, dataOffset, headerFileSize, fs.Length, entries);
    }

    public byte[] ReadEntry(string entryPath)
    {
        entryPath = entryPath.Replace('\\', '/').TrimStart('/');
        if (!Entries.TryGetValue(entryPath, out var entry))
            throw new InvalidDataException("PAK entry not found: " + entryPath);
        if (entry.Size > int.MaxValue)
            throw new InvalidDataException("PAK entry is too large for this launcher: " + entryPath);

        using var fs = File.Open(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Position = entry.AbsoluteOffset;
        var data = new byte[(int)entry.Size];
        var total = 0;
        while (total < data.Length)
        {
            var n = fs.Read(data, total, data.Length - total);
            if (n <= 0) throw new EndOfStreamException("Unexpected end of res.pak while reading " + entryPath);
            total += n;
        }
        return data;
    }

    public static void ReplaceEntries(string pakPath, IReadOnlyDictionary<string, byte[]> replacements)
    {
        if (replacements.Count == 0) return;
        var index = Open(pakPath);
        var targets = new List<(PakEntry Entry, byte[] Data)>();
        foreach (var kv in replacements)
        {
            var key = kv.Key.Replace('\\', '/').TrimStart('/');
            if (!index.Entries.TryGetValue(key, out var e))
                throw new InvalidDataException("Required PAK entry not found: " + key);
            targets.Add((e, kv.Value));
        }

        using var fs = File.Open(pakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
        var oldHeader = index.HeaderFileSize;
        var changes = new List<(PakEntry Entry, long NewRelative, uint NewSize)>();

        try
        {
            // Phase 1: append all payloads. Index still points to old data if this phase fails.
            foreach (var t in targets)
            {
                fs.Position = fs.Length;
                var absolute = fs.Position;
                var relative = absolute - index.DataOffset;
                if (t.Entry.Type == 0 && (relative < 0 || relative > uint.MaxValue))
                    throw new InvalidDataException("New PAK offset does not fit type-0 entry: " + t.Entry.Path);
                if (t.Data.LongLength > uint.MaxValue)
                    throw new InvalidDataException("Replacement entry is too large: " + t.Entry.Path);
                bw.Write(t.Data);
                changes.Add((t.Entry, relative, (uint)t.Data.Length));
            }
            bw.Flush();
            fs.Flush(true);

            // Phase 2: switch index pointers only after every payload was appended successfully.
            foreach (var c in changes)
            {
                fs.Position = c.Entry.OffsetFieldPos;
                WriteOffset(bw, c.Entry.Type, c.NewRelative);
                fs.Position = c.Entry.SizeFieldPos;
                bw.Write(c.NewSize);
            }

            var newLength = fs.Length;
            if (index.FileLength <= uint.MaxValue && (uint)index.FileLength == oldHeader && newLength <= uint.MaxValue)
            {
                fs.Position = 8;
                bw.Write((uint)newLength);
            }
            bw.Flush();
            fs.Flush(true);

            // Verification. Any failure restores the old index fields; appended bytes remain unreachable.
            var verify = Open(pakPath);
            foreach (var t in targets)
            {
                var actual = verify.ReadEntry(t.Entry.Path);
                if (!SHA256.HashData(actual).SequenceEqual(SHA256.HashData(t.Data)))
                    throw new InvalidDataException("PAK verification failed for " + t.Entry.Path);
            }
        }
        catch
        {
            try
            {
                foreach (var c in changes)
                {
                    fs.Position = c.Entry.OffsetFieldPos;
                    WriteOffset(bw, c.Entry.Type, c.Entry.RelativeOffset);
                    fs.Position = c.Entry.SizeFieldPos;
                    bw.Write(c.Entry.Size);
                }
                fs.Position = 8;
                bw.Write(oldHeader);
                bw.Flush();
                fs.Flush(true);
            }
            catch { }
            throw;
        }
    }

    private static void WriteOffset(BinaryWriter bw, int type, long relative)
    {
        if (type == 0)
        {
            if (relative < 0 || relative > uint.MaxValue) throw new InvalidDataException("PAK type-0 offset overflow.");
            bw.Write((uint)relative);
        }
        else if (type == 2)
            bw.Write((double)relative);
        else
            throw new InvalidDataException("Unsupported PAK entry type while writing: " + type);
    }
}

internal sealed record PakEntry(
    string Path,
    int Type,
    long RelativeOffset,
    long AbsoluteOffset,
    long OffsetFieldPos,
    long SizeFieldPos,
    uint Size);
