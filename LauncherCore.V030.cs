using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JACKOBsWartalesModLauncher;

internal static partial class LauncherCore
{
    public const string LauncherVersion = "0.3.0";
    private const string StateFormatV1 = "JACKOB_WARTALES_LAUNCHER_STATE_V1";
    private const string StateFormatV2 = "JACKOB_WARTALES_LAUNCHER_STATE_V2";
    private const string ModFormat = "JACKOB_WARTALES_MOD_V1";
    private const string CdbPatchFormat = "JACKOB_CDB_PATCH_V1";
    private const string XmlPatchFormat = "JACKOB_XML_PATCH_V1";
    private const string BinaryDeltaFormat = "JACKOB_BINARY_DELTA_V1";

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

    public static string? FindKnownGameDirectory()
    {
        var candidates = new List<string>();
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsPath), StateJson);
                if (!string.IsNullOrWhiteSpace(s?.GameDirectory)) candidates.Add(s.GameDirectory!);
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
        if (!IsGameDirectory(full)) throw new InvalidOperationException($"res.pak was not found in: {full}");
        SaveSettings(full);
    }

    public static IReadOnlyList<InstalledModState> GetInstalledMods(string gameDir)
    {
        EnsureGameDirectory(gameDir);
        return LoadState(gameDir).InstalledMods.Select(CloneInstalledMod).ToArray();
    }

    public static ModManifest PreviewPackage(string packagePath)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Mod ZIP was not found.", packagePath);
        using var package = ModPackage.Load(packagePath);
        ValidateManifest(package.Manifest);
        return CloneManifest(package.Manifest);
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
        if (state.InstalledMods.Count == 0) throw new InvalidOperationException("No managed mods are installed.");
        VerifyManagedState(gameDir, state, allowRebaselineWhenEmpty: false);

        var target = state.InstalledMods.FirstOrDefault(x => x.Id.Equals(modId, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException("The selected mod is no longer installed.");
        state.InstalledMods.Remove(target);
        RebuildAndCommit(gameDir, state);

        var stateDir = GetStateDirectory(gameDir);
        TryDelete(Path.Combine(stateDir, target.PackageFile.Replace('/', Path.DirectorySeparatorChar)));
    }

    public static void RestoreBaseline(string gameDir)
    {
        EnsureGameDirectory(gameDir);
        var state = LoadState(gameDir);
        if (state.ManagedEntries.Count == 0 && state.ManagedFiles.Count == 0) return;
        VerifyManagedState(gameDir, state, allowRebaselineWhenEmpty: false);

        var oldPackages = state.InstalledMods.Select(x => x.PackageFile).ToArray();
        state.InstalledMods.Clear();
        RebuildAndCommit(gameDir, state);

        var stateDir = GetStateDirectory(gameDir);
        foreach (var p in oldPackages) TryDelete(Path.Combine(stateDir, p.Replace('/', Path.DirectorySeparatorChar)));
    }

    public static void Verify(string gameDir)
    {
        EnsureGameDirectory(gameDir);
        VerifyManagedState(gameDir, LoadState(gameDir), allowRebaselineWhenEmpty: false);
    }

    private static void EnsureGameDirectory(string gameDir)
    {
        if (!IsGameDirectory(gameDir)) throw new InvalidOperationException("Select a valid Wartales folder containing res.pak first.");
    }

    private static bool IsGameDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "res.pak"));

    private static void SaveSettings(string gameDir)
    {
        Directory.CreateDirectory(SettingsRoot);
        File.WriteAllText(SettingsPath,
            JsonSerializer.Serialize(new LauncherSettings { GameDirectory = Path.GetFullPath(gameDir) }, StateJson),
            new UTF8Encoding(false));
    }

    private static void InstallOrUpdate(string gameDir, string packagePath)
    {
        var state = LoadState(gameDir);
        VerifyManagedState(gameDir, state, allowRebaselineWhenEmpty: true);

        using var package = ModPackage.Load(packagePath);
        ValidateManifest(package.Manifest);

        var stateDir = GetStateDirectory(gameDir);
        Directory.CreateDirectory(Path.Combine(stateDir, "packages"));
        Directory.CreateDirectory(Path.Combine(stateDir, "originals", "pak"));
        Directory.CreateDirectory(Path.Combine(stateDir, "originals", "files"));

        CaptureOriginals(gameDir, state, package.Manifest);

        var old = state.InstalledMods.FirstOrDefault(x => x.Id.Equals(package.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        var storedName = MakeSafeFileName(package.Manifest.Id) + "-" + MakeSafeFileName(package.Manifest.Version) + ".zip";
        var storedFullPath = Path.Combine(stateDir, "packages", storedName);
        File.Copy(packagePath, storedFullPath, overwrite: true);

        if (old is not null) state.InstalledMods.Remove(old);
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
            RebuildAndCommit(gameDir, state);
            if (old is not null && !old.PackageFile.Equals(state.InstalledMods[^1].PackageFile, StringComparison.OrdinalIgnoreCase))
                TryDelete(Path.Combine(stateDir, old.PackageFile.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            TryDelete(storedFullPath);
            throw;
        }
    }

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
        if (!File.Exists(path)) return NewState(gameDir);
        var state = JsonSerializer.Deserialize<LauncherState>(File.ReadAllText(path), StateJson)
                    ?? throw new InvalidDataException("Launcher state is invalid.");
        if (state.Format != StateFormatV1 && state.Format != StateFormatV2)
            throw new InvalidDataException("Unsupported launcher state format.");
        state.Format = StateFormatV2;
        state.ManagedEntries ??= new();
        state.ManagedFiles ??= new();
        state.ManagedEntries = new Dictionary<string, ManagedEntryState>(state.ManagedEntries, StringComparer.OrdinalIgnoreCase);
        state.ManagedFiles = new Dictionary<string, ManagedFileState>(state.ManagedFiles, StringComparer.OrdinalIgnoreCase);
        state.InstalledMods ??= new();
        return state;
    }

    private static LauncherState NewState(string gameDir) => new()
    {
        Format = StateFormatV2,
        GameDirectory = Path.GetFullPath(gameDir)
    };

    private static void SaveState(string gameDir, LauncherState state)
    {
        state.Format = StateFormatV2;
        state.GameDirectory = Path.GetFullPath(gameDir);
        var dir = GetStateDirectory(gameDir);
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "state.json.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(state, StateJson), new UTF8Encoding(false));
        File.Move(temp, GetStatePath(gameDir), overwrite: true);
    }

    private static void VerifyManagedState(string gameDir, LauncherState state, bool allowRebaselineWhenEmpty)
    {
        var mismatches = new List<string>();
        if (state.ManagedEntries.Count > 0)
        {
            var pak = PakArchive.Open(Path.Combine(gameDir, "res.pak"));
            foreach (var managed in state.ManagedEntries.Values)
            {
                var actual = Sha256(pak.ReadEntry(managed.Path));
                if (!actual.Equals(managed.LastAppliedHash, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add("res.pak::" + managed.Path);
            }
        }

        foreach (var managed in state.ManagedFiles.Values)
        {
            var target = ResolveGameTarget(gameDir, managed.Path);
            var exists = File.Exists(target);
            if (exists != managed.LastAppliedExists)
            {
                mismatches.Add(managed.Path);
                continue;
            }
            if (exists && !Sha256File(target).Equals(managed.LastAppliedHash, StringComparison.OrdinalIgnoreCase))
                mismatches.Add(managed.Path);
        }

        if (mismatches.Count == 0) return;
        if (allowRebaselineWhenEmpty && state.InstalledMods.Count == 0)
        {
            ResetBaselinesToCurrent(gameDir, state);
            SaveState(gameDir, state);
            return;
        }

        throw new InvalidOperationException(
            "Managed game files changed outside the launcher: " + string.Join(", ", mismatches) +
            ". This may be a game update or another mod. The launcher stopped without writing anything.");
    }

    private static void ResetBaselinesToCurrent(string gameDir, LauncherState state)
    {
        var stateDir = GetStateDirectory(gameDir);
        if (state.ManagedEntries.Count > 0)
        {
            var pak = PakArchive.Open(Path.Combine(gameDir, "res.pak"));
            foreach (var m in state.ManagedEntries.Values)
            {
                var bytes = pak.ReadEntry(m.Path);
                var original = Path.Combine(stateDir, m.OriginalFile.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.WriteAllBytes(original, bytes);
                m.OriginalHash = m.LastAppliedHash = Sha256(bytes);
            }
        }

        foreach (var m in state.ManagedFiles.Values)
        {
            var target = ResolveGameTarget(gameDir, m.Path);
            var original = Path.Combine(stateDir, m.OriginalFile.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target))
            {
                var bytes = File.ReadAllBytes(target);
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.WriteAllBytes(original, bytes);
                m.OriginalExists = m.LastAppliedExists = true;
                m.OriginalHash = m.LastAppliedHash = Sha256(bytes);
            }
            else
            {
                TryDelete(original);
                m.OriginalExists = m.LastAppliedExists = false;
                m.OriginalHash = m.LastAppliedHash = "";
            }
        }
    }

    private static void CaptureOriginals(string gameDir, LauncherState state, ModManifest manifest)
    {
        var stateDir = GetStateDirectory(gameDir);
        var pakOps = manifest.Operations.Where(IsPakOperation).ToArray();
        if (pakOps.Length > 0)
        {
            var pak = PakArchive.Open(Path.Combine(gameDir, "res.pak"));
            foreach (var entryPath in pakOps.Select(x => NormalizePakPath(x.Entry)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = entryPath.ToLowerInvariant();
                if (state.ManagedEntries.ContainsKey(key)) continue;
                var bytes = pak.ReadEntry(entryPath);
                var hash = Sha256(bytes);
                var originalName = Path.Combine("originals", "pak", Sha256(entryPath)[..24] + ".bin").Replace('\\', '/');
                var originalFull = Path.Combine(stateDir, originalName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(originalFull)!);
                File.WriteAllBytes(originalFull, bytes);
                state.ManagedEntries[key] = new ManagedEntryState
                {
                    Path = entryPath, OriginalFile = originalName, OriginalHash = hash, LastAppliedHash = hash
                };
            }
        }

        foreach (var targetPath in manifest.Operations.Where(IsExternalOperation)
                     .Select(x => NormalizeGameRelativePath(x.Target)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = targetPath.ToLowerInvariant();
            if (state.ManagedFiles.ContainsKey(key)) continue;
            var full = ResolveGameTarget(gameDir, targetPath);
            var exists = File.Exists(full);
            var originalName = Path.Combine("originals", "files", Sha256(targetPath)[..24] + ".bin").Replace('\\', '/');
            var originalFull = Path.Combine(stateDir, originalName.Replace('/', Path.DirectorySeparatorChar));
            var hash = "";
            if (exists)
            {
                var bytes = File.ReadAllBytes(full);
                hash = Sha256(bytes);
                Directory.CreateDirectory(Path.GetDirectoryName(originalFull)!);
                File.WriteAllBytes(originalFull, bytes);
            }
            state.ManagedFiles[key] = new ManagedFileState
            {
                Path = targetPath,
                OriginalFile = originalName,
                OriginalExists = exists,
                OriginalHash = hash,
                LastAppliedExists = exists,
                LastAppliedHash = hash
            };
        }
    }

    private static void RebuildAndCommit(string gameDir, LauncherState state)
    {
        var stateDir = GetStateDirectory(gameDir);
        var pakWorking = LoadPakBaselines(stateDir, state);
        var fileWorking = LoadFileBaselines(stateDir, state);

        foreach (var installed in state.InstalledMods)
        {
            var packagePath = Path.Combine(stateDir, installed.PackageFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(packagePath)) throw new FileNotFoundException($"Stored package for {installed.Name} is missing.", packagePath);
            if (!Sha256File(packagePath).Equals(installed.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Stored package checksum failed for {installed.Name}.");
            using var package = ModPackage.Load(packagePath);
            ApplyPackage(package, state, pakWorking, fileWorking);
        }

        var pakPath = Path.Combine(gameDir, "res.pak");
        var oldPakBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (pakWorking.Count > 0)
        {
            var currentPak = PakArchive.Open(pakPath);
            foreach (var path in pakWorking.Keys) oldPakBytes[path] = currentPak.ReadEntry(path);
        }

        var txDir = Path.Combine(stateDir, "transactions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(txDir);
        var fileSnapshots = SnapshotExternalTargets(gameDir, state, txDir);
        var pakWritten = false;
        try
        {
            WriteExternalFiles(gameDir, state, fileWorking);

            if (pakWorking.Count > 0)
            {
                PakArchive.ReplaceEntries(pakPath, pakWorking);
                pakWritten = true;
            }

            VerifyApplied(gameDir, state, pakWorking, fileWorking);
            SaveState(gameDir, state);
        }
        catch
        {
            if (pakWritten)
            {
                try { PakArchive.ReplaceEntries(pakPath, oldPakBytes); } catch { }
            }
            try { RestoreExternalSnapshots(gameDir, fileSnapshots); } catch { }
            throw;
        }
        finally
        {
            TryDeleteDirectory(txDir);
        }
    }

    private static Dictionary<string, byte[]> LoadPakBaselines(string stateDir, LauncherState state)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in state.ManagedEntries.Values)
        {
            var original = Path.Combine(stateDir, m.OriginalFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(original)) throw new FileNotFoundException("Original baseline entry is missing.", original);
            var bytes = File.ReadAllBytes(original);
            if (!Sha256(bytes).Equals(m.OriginalHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Baseline checksum failed for res.pak::{m.Path}.");
            result[m.Path] = bytes;
        }
        return result;
    }

    private static Dictionary<string, ExternalWorkingFile> LoadFileBaselines(string stateDir, LauncherState state)
    {
        var result = new Dictionary<string, ExternalWorkingFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in state.ManagedFiles.Values)
        {
            if (!m.OriginalExists)
            {
                result[m.Path] = new ExternalWorkingFile(false, Array.Empty<byte>());
                continue;
            }
            var original = Path.Combine(stateDir, m.OriginalFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(original)) throw new FileNotFoundException("Original external baseline is missing.", original);
            var bytes = File.ReadAllBytes(original);
            if (!Sha256(bytes).Equals(m.OriginalHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Baseline checksum failed for {m.Path}.");
            result[m.Path] = new ExternalWorkingFile(true, bytes);
        }
        return result;
    }

    private static Dictionary<string, ExternalSnapshot> SnapshotExternalTargets(string gameDir, LauncherState state, string txDir)
    {
        var result = new Dictionary<string, ExternalSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in state.ManagedFiles.Values)
        {
            var target = ResolveGameTarget(gameDir, m.Path);
            if (!File.Exists(target))
            {
                result[m.Path] = new ExternalSnapshot(false, "");
                continue;
            }
            var backup = Path.Combine(txDir, Sha256(m.Path)[..24] + ".bak");
            File.Copy(target, backup, overwrite: true);
            result[m.Path] = new ExternalSnapshot(true, backup);
        }
        return result;
    }

    private static void WriteExternalFiles(string gameDir, LauncherState state, Dictionary<string, ExternalWorkingFile> working)
    {
        foreach (var m in state.ManagedFiles.Values)
        {
            var target = ResolveGameTarget(gameDir, m.Path);
            var item = working[m.Path];
            if (!item.Exists)
            {
                if (File.Exists(target)) File.Delete(target);
                continue;
            }
            var directory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, ".jackob-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temp, item.Bytes);
                File.Move(temp, target, overwrite: true);
            }
            finally { TryDelete(temp); }
        }
    }

    private static void RestoreExternalSnapshots(string gameDir, Dictionary<string, ExternalSnapshot> snapshots)
    {
        foreach (var (relative, snap) in snapshots)
        {
            var target = ResolveGameTarget(gameDir, relative);
            if (!snap.Existed)
            {
                TryDelete(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(snap.BackupPath, target, overwrite: true);
        }
    }

    private static void VerifyApplied(string gameDir, LauncherState state,
        Dictionary<string, byte[]> pakWorking, Dictionary<string, ExternalWorkingFile> fileWorking)
    {
        if (pakWorking.Count > 0)
        {
            var verifyPak = PakArchive.Open(Path.Combine(gameDir, "res.pak"));
            foreach (var m in state.ManagedEntries.Values)
            {
                var actual = Sha256(verifyPak.ReadEntry(m.Path));
                var expected = Sha256(pakWorking[m.Path]);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Post-install verification failed for res.pak::{m.Path}.");
                m.LastAppliedHash = actual;
            }
        }

        foreach (var m in state.ManagedFiles.Values)
        {
            var target = ResolveGameTarget(gameDir, m.Path);
            var expected = fileWorking[m.Path];
            var exists = File.Exists(target);
            if (exists != expected.Exists) throw new InvalidDataException($"Post-install verification failed for {m.Path}.");
            var hash = exists ? Sha256File(target) : "";
            if (exists && !hash.Equals(Sha256(expected.Bytes), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Post-install verification failed for {m.Path}.");
            m.LastAppliedExists = exists;
            m.LastAppliedHash = hash;
        }
    }

    private static void ApplyPackage(ModPackage package, LauncherState state,
        Dictionary<string, byte[]> pakWorking, Dictionary<string, ExternalWorkingFile> fileWorking)
    {
        ValidateManifest(package.Manifest);
        foreach (var op in package.Manifest.Operations)
        {
            switch (op.Type)
            {
                case "cdbPatch":
                case "xmlMerge":
                case "replaceEntry":
                {
                    var entry = NormalizePakPath(op.Entry);
                    if (!pakWorking.ContainsKey(entry)) throw new InvalidOperationException($"Launcher baseline does not contain required PAK entry: {entry}");
                    pakWorking[entry] = op.Type switch
                    {
                        "cdbPatch" => ApplyCdbPatch(pakWorking[entry], package.ReadJson(op.Source)),
                        "xmlMerge" => ApplyXmlPatch(pakWorking[entry], package.ReadJson(op.Source)),
                        _ => package.ReadBytes(op.Source)
                    };
                    break;
                }
                case "externalBinaryDelta":
                    ApplyExternalBinaryDelta(package, state, op, fileWorking, stateDir: GetStateDirectory(state.GameDirectory));
                    break;
                case "externalXmlMerge":
                    ApplyExternalXmlMerge(package, state, op, fileWorking);
                    break;
                case "externalReplaceFile":
                    ApplyExternalReplaceFile(package, state, op, fileWorking);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported package operation: {op.Type}");
            }
        }
    }

    private static void ApplyExternalBinaryDelta(ModPackage package, LauncherState state, ManifestOperation op,
        Dictionary<string, ExternalWorkingFile> working, string stateDir)
    {
        var target = NormalizeGameRelativePath(op.Target);
        var managed = state.ManagedFiles[target.ToLowerInvariant()];
        if (!managed.OriginalExists) throw UnsupportedGameVersion(target);
        var baselinePath = Path.Combine(stateDir, managed.OriginalFile.Replace('/', Path.DirectorySeparatorChar));
        var baseline = File.ReadAllBytes(baselinePath);

        var delta = package.ReadJson(op.Source).AsObject();
        if (delta["format"]?.GetValue<string>() != BinaryDeltaFormat) throw new InvalidDataException("Unsupported binary delta format.");
        var expectedSha = op.ExpectedSha256 ?? delta["expectedSha256"]?.GetValue<string>();
        var resultingSha = op.ResultingSha256 ?? delta["resultingSha256"]?.GetValue<string>();
        ValidateSha256(expectedSha, "externalBinaryDelta expectedSha256");
        ValidateSha256(resultingSha, "externalBinaryDelta resultingSha256");
        if (!Sha256(baseline).Equals(expectedSha, StringComparison.OrdinalIgnoreCase)) throw UnsupportedGameVersion(target);

        var standalone = ApplyBinaryHunks((byte[])baseline.Clone(), delta, target);
        if (!Sha256(standalone).Equals(resultingSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Binary delta resulting SHA-256 does not match its declaration for {target}.");

        var current = working[target];
        if (!current.Exists) throw UnsupportedGameVersion(target);
        working[target] = new ExternalWorkingFile(true, ApplyBinaryHunks((byte[])current.Bytes.Clone(), delta, target));
    }

    private static byte[] ApplyBinaryHunks(byte[] bytes, JsonObject delta, string target)
    {
        var hunks = delta["patches"]?.AsArray() ?? throw new InvalidDataException("Binary delta has no patches array.");
        foreach (var n in hunks)
        {
            var h = n?.AsObject() ?? throw new InvalidDataException("Invalid binary delta patch.");
            var offset = h["offset"]?.GetValue<long>() ?? throw new InvalidDataException("Binary delta patch has no offset.");
            var expected = ParseHex(h["expected"]?.GetValue<string>() ?? throw new InvalidDataException("Binary delta patch has no expected bytes."));
            var replacement = ParseHex(h["replacement"]?.GetValue<string>() ?? throw new InvalidDataException("Binary delta patch has no replacement bytes."));
            if (offset < 0 || offset > int.MaxValue || offset + expected.Length > bytes.LongLength)
                throw new InvalidDataException($"Binary delta offset is outside {target}.");
            if (expected.Length != replacement.Length)
                throw new InvalidDataException("Binary delta v1 requires equal-length expected and replacement byte sequences.");
            var start = (int)offset;
            if (!bytes.AsSpan(start, expected.Length).SequenceEqual(expected)) throw UnsupportedGameVersion(target);
            replacement.CopyTo(bytes, start);
        }
        return bytes;
    }

    private static void ApplyExternalXmlMerge(ModPackage package, LauncherState state, ManifestOperation op,
        Dictionary<string, ExternalWorkingFile> working)
    {
        var target = NormalizeGameRelativePath(op.Target);
        var managed = state.ManagedFiles[target.ToLowerInvariant()];
        if (!managed.OriginalExists) throw UnsupportedGameVersion(target);
        if (!string.IsNullOrWhiteSpace(op.ExpectedSha256))
        {
            ValidateSha256(op.ExpectedSha256, "externalXmlMerge expectedSha256");
            if (!managed.OriginalHash.Equals(op.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) throw UnsupportedGameVersion(target);
        }
        var current = working[target];
        if (!current.Exists) throw UnsupportedGameVersion(target);
        working[target] = new ExternalWorkingFile(true, ApplyXmlPatch(current.Bytes, package.ReadJson(op.Source)));
    }

    private static void ApplyExternalReplaceFile(ModPackage package, LauncherState state, ManifestOperation op,
        Dictionary<string, ExternalWorkingFile> working)
    {
        var target = NormalizeGameRelativePath(op.Target);
        var managed = state.ManagedFiles[target.ToLowerInvariant()];
        if (!string.IsNullOrWhiteSpace(op.ExpectedSha256))
        {
            ValidateSha256(op.ExpectedSha256, "externalReplaceFile expectedSha256");
            if (!managed.OriginalExists || !managed.OriginalHash.Equals(op.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw UnsupportedGameVersion(target);
        }
        var replacement = package.ReadBytes(op.Source);
        if (!string.IsNullOrWhiteSpace(op.ResultingSha256))
        {
            ValidateSha256(op.ResultingSha256, "externalReplaceFile resultingSha256");
            if (!Sha256(replacement).Equals(op.ResultingSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Replacement SHA-256 does not match its declaration for {target}.");
        }
        working[target] = new ExternalWorkingFile(true, replacement);
    }
}
