using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JACKOBsWartalesModLauncher;

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
                long relativeOffset = type == 0 ? br.ReadUInt32() : Convert.ToInt64(Math.Round(br.ReadDouble()));
                var sizeFieldPos = fs.Position;
                var size = br.ReadUInt32();
                _ = br.ReadUInt32();
                var full = (string.IsNullOrEmpty(folder) ? name : folder + "/" + name).TrimStart('/');
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
        if (!Entries.TryGetValue(entryPath, out var entry)) throw new InvalidDataException("PAK entry not found: " + entryPath);
        return ReadRange(Path, entry.AbsoluteOffset, entry.Size);
    }

    public static void ReplaceEntries(string pakPath, IReadOnlyDictionary<string, byte[]> replacements)
    {
        if (replacements.Count == 0) return;

        var index = Open(pakPath);
        var baseline = PakBaselineStore.Ensure(pakPath, index);

        if (CanRestoreExactBaseline(pakPath, replacements, baseline))
        {
            RestoreExactBaseline(pakPath, replacements, baseline);
            return;
        }

        var targets = new List<(PakEntry Entry, byte[] Data)>();
        foreach (var kv in replacements)
        {
            var key = kv.Key.Replace('\\', '/').TrimStart('/');
            if (!index.Entries.TryGetValue(key, out var e)) throw new InvalidDataException("Required PAK entry not found: " + key);
            targets.Add((e, kv.Value));
        }

        var oldPrefix = ReadPrefix(pakPath, index.DataOffset);
        var oldLength = index.FileLength;

        try
        {
            using (var fs = File.Open(pakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
            {
                var oldHeader = index.HeaderFileSize;
                var changes = new List<(PakEntry Entry, long NewRelative, uint NewSize)>();

                foreach (var t in targets)
                {
                    fs.Position = fs.Length;
                    var relative = fs.Position - index.DataOffset;
                    if (t.Entry.Type == 0 && (relative < 0 || relative > uint.MaxValue))
                        throw new InvalidDataException("New PAK offset does not fit type-0 entry: " + t.Entry.Path);
                    if (t.Data.LongLength > uint.MaxValue)
                        throw new InvalidDataException("Replacement entry is too large: " + t.Entry.Path);

                    bw.Write(t.Data);
                    changes.Add((t.Entry, relative, (uint)t.Data.Length));
                }

                bw.Flush();
                fs.Flush(true);

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
            }

            var verify = Open(pakPath);
            foreach (var t in targets)
            {
                var actual = verify.ReadEntry(t.Entry.Path);
                if (!SHA256.HashData(actual).SequenceEqual(SHA256.HashData(t.Data)))
                    throw new InvalidDataException("PAK verification failed for " + t.Entry.Path);
            }

            PakBaselineStore.UpdateKnown(pakPath, baseline);
        }
        catch
        {
            try { RestorePrefixAndLength(pakPath, oldPrefix, oldLength); } catch { }
            throw;
        }
    }

    private static bool CanRestoreExactBaseline(
        string pakPath,
        IReadOnlyDictionary<string, byte[]> replacements,
        PakBaselineMetadata baseline)
    {
        if (replacements.Count == 0) return false;

        var originalIndex = Open(baseline.OriginalPrefixPath);
        if (originalIndex.DataOffset != baseline.DataOffset)
            throw new InvalidDataException("Stored res.pak baseline index is inconsistent.");

        foreach (var kv in replacements)
        {
            var key = kv.Key.Replace('\\', '/').TrimStart('/');
            if (!originalIndex.Entries.TryGetValue(key, out var originalEntry)) return false;
            if (kv.Value.LongLength != originalEntry.Size) return false;

            var raw = ReadRange(pakPath, baseline.DataOffset + originalEntry.RelativeOffset, originalEntry.Size);
            if (!raw.AsSpan().SequenceEqual(kv.Value)) return false;
        }

        return true;
    }

    private static void RestoreExactBaseline(
        string pakPath,
        IReadOnlyDictionary<string, byte[]> replacements,
        PakBaselineMetadata baseline)
    {
        var current = Open(pakPath);
        if (current.DataOffset != baseline.DataOffset)
            throw new InvalidOperationException("res.pak layout changed unexpectedly. Exact vanilla restore was aborted.");
        if (current.FileLength < baseline.OriginalFileLength)
            throw new InvalidOperationException("res.pak is shorter than the captured vanilla baseline. Exact restore was aborted.");

        var originalPrefix = File.ReadAllBytes(baseline.OriginalPrefixPath);
        if (!Sha256(originalPrefix).Equals(baseline.OriginalPrefixSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Stored res.pak baseline index checksum failed.");

        var currentPrefix = ReadPrefix(pakPath, current.DataOffset);
        var currentLength = current.FileLength;
        var stateDir = System.IO.Path.GetDirectoryName(baseline.MetadataPath)!;
        var tailBackup = System.IO.Path.Combine(stateDir, "transactions", "res-pak-tail-" + Guid.NewGuid().ToString("N") + ".bin");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tailBackup)!);
        var tailSaved = false;

        try
        {
            if (currentLength > baseline.OriginalFileLength)
            {
                CopyRangeToFile(pakPath, baseline.OriginalFileLength, currentLength - baseline.OriginalFileLength, tailBackup);
                tailSaved = true;
            }

            using (var fs = File.Open(pakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                fs.Position = 0;
                fs.Write(originalPrefix);
                fs.Flush(true);
            }

            var restoredIndex = Open(pakPath);
            foreach (var kv in replacements)
            {
                var actual = restoredIndex.ReadEntry(kv.Key);
                if (!actual.AsSpan().SequenceEqual(kv.Value))
                    throw new InvalidDataException("Exact vanilla PAK restore verification failed for " + kv.Key + ".");
            }

            using (var fs = File.Open(pakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                fs.SetLength(baseline.OriginalFileLength);
                fs.Flush(true);
            }

            var verify = Open(pakPath);
            if (verify.FileLength != baseline.OriginalFileLength)
                throw new InvalidDataException("Exact vanilla PAK restore length verification failed.");
            if (!Sha256(ReadPrefix(pakPath, verify.DataOffset)).Equals(baseline.OriginalPrefixSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Exact vanilla PAK restore index verification failed.");

            PakBaselineStore.UpdateKnown(pakPath, baseline);
        }
        catch
        {
            try
            {
                using var fs = File.Open(pakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                if (tailSaved)
                {
                    fs.SetLength(baseline.OriginalFileLength);
                    fs.Position = baseline.OriginalFileLength;
                    using var tail = File.OpenRead(tailBackup);
                    tail.CopyTo(fs);
                }
                fs.SetLength(currentLength);
                fs.Position = 0;
                fs.Write(currentPrefix);
                fs.Flush(true);
            }
            catch { }
            throw;
        }
        finally
        {
            try { if (File.Exists(tailBackup)) File.Delete(tailBackup); } catch { }
        }
    }

    private static byte[] ReadPrefix(string pakPath, long length)
    {
        if (length < 0 || length > int.MaxValue) throw new InvalidDataException("PAK index is too large.");
        return ReadRange(pakPath, 0, (uint)length);
    }

    private static byte[] ReadRange(string path, long offset, uint size)
    {
        if (size > int.MaxValue) throw new InvalidDataException("PAK entry is too large for this launcher.");
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset < 0 || offset + size > fs.Length) throw new EndOfStreamException("Requested PAK range is outside the file.");
        fs.Position = offset;
        var data = new byte[(int)size];
        var total = 0;
        while (total < data.Length)
        {
            var n = fs.Read(data, total, data.Length - total);
            if (n <= 0) throw new EndOfStreamException("Unexpected end of res.pak while reading data.");
            total += n;
        }
        return data;
    }

    private static void CopyRangeToFile(string sourcePath, long offset, long length, string destinationPath)
    {
        using var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset < 0 || length < 0 || offset + length > input.Length)
            throw new EndOfStreamException("Requested PAK backup range is outside the file.");
        input.Position = offset;
        using var output = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = input.Read(buffer, 0, toRead);
            if (read <= 0) throw new EndOfStreamException("Unexpected end of res.pak while backing up appended data.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
        output.Flush(true);
    }

    private static void RestorePrefixAndLength(string pakPath, byte[] prefix, long length)
    {
        using var fs = File.Open(pakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        fs.Position = 0;
        fs.Write(prefix);
        fs.SetLength(length);
        fs.Flush(true);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void WriteOffset(BinaryWriter bw, int type, long relative)
    {
        if (type == 0)
        {
            if (relative < 0 || relative > uint.MaxValue) throw new InvalidDataException("PAK type-0 offset overflow.");
            bw.Write((uint)relative);
        }
        else if (type == 2)
        {
            bw.Write((double)relative);
        }
        else
        {
            throw new InvalidDataException("Unsupported PAK entry type while writing: " + type);
        }
    }

    private static class PakBaselineStore
    {
        private const string Format = "JACKOB_WARTALES_PAK_BASELINE_V1";
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static PakBaselineMetadata Ensure(string pakPath, PakArchive current)
        {
            var stateDir = GetStateDirectory(pakPath);
            Directory.CreateDirectory(System.IO.Path.Combine(stateDir, "originals"));
            var metadataPath = System.IO.Path.Combine(stateDir, "pak-baseline.json");
            var prefixPath = System.IO.Path.Combine(stateDir, "originals", "res-pak-index.bin");

            if (!File.Exists(metadataPath))
            {
                if (HasInstalledMods(stateDir))
                    throw new InvalidOperationException(
                        "Exact res.pak baseline metadata is missing while launcher-managed mods are active. " +
                        "Restore/remove the old launcher-managed mods and run Steam Verify before using this v0.3.0 build.");
                return CaptureFresh(pakPath, current, metadataPath, prefixPath);
            }

            var metadata = JsonSerializer.Deserialize<PakBaselineMetadata>(File.ReadAllText(metadataPath), JsonOptions)
                           ?? throw new InvalidDataException("res.pak baseline metadata is invalid.");
            metadata.MetadataPath = metadataPath;
            metadata.OriginalPrefixPath = prefixPath;

            if (metadata.Format != Format)
                throw new InvalidDataException("Unsupported res.pak baseline metadata format.");
            if (!File.Exists(prefixPath))
                throw new FileNotFoundException("Stored res.pak baseline index is missing.", prefixPath);

            var originalPrefix = File.ReadAllBytes(prefixPath);
            if (!Sha256(originalPrefix).Equals(metadata.OriginalPrefixSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Stored res.pak baseline index checksum failed.");

            var currentPrefixHash = Sha256(ReadPrefix(pakPath, current.DataOffset));
            var known = current.FileLength == metadata.LastKnownFileLength
                        && current.DataOffset == metadata.DataOffset
                        && currentPrefixHash.Equals(metadata.LastKnownPrefixSha256, StringComparison.OrdinalIgnoreCase);
            if (known) return metadata;

            if (HasInstalledMods(stateDir))
                throw new InvalidOperationException(
                    "res.pak changed outside the launcher while managed mods are active. " +
                    "The launcher stopped without writing anything.");

            return CaptureFresh(pakPath, current, metadataPath, prefixPath);
        }

        public static void UpdateKnown(string pakPath, PakBaselineMetadata metadata)
        {
            var current = Open(pakPath);
            metadata.LastKnownFileLength = current.FileLength;
            metadata.LastKnownPrefixSha256 = Sha256(ReadPrefix(pakPath, current.DataOffset));
            SaveMetadata(metadata);
        }

        private static PakBaselineMetadata CaptureFresh(
            string pakPath,
            PakArchive current,
            string metadataPath,
            string prefixPath)
        {
            var prefix = ReadPrefix(pakPath, current.DataOffset);
            var prefixTemp = prefixPath + ".tmp";
            File.WriteAllBytes(prefixTemp, prefix);
            File.Move(prefixTemp, prefixPath, overwrite: true);

            var metadata = new PakBaselineMetadata
            {
                Format = Format,
                DataOffset = current.DataOffset,
                OriginalFileLength = current.FileLength,
                OriginalPrefixSha256 = Sha256(prefix),
                LastKnownFileLength = current.FileLength,
                LastKnownPrefixSha256 = Sha256(prefix),
                MetadataPath = metadataPath,
                OriginalPrefixPath = prefixPath
            };
            SaveMetadata(metadata);
            return metadata;
        }

        private static void SaveMetadata(PakBaselineMetadata metadata)
        {
            var temp = metadata.MetadataPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(metadata, JsonOptions), new UTF8Encoding(false));
            File.Move(temp, metadata.MetadataPath, overwrite: true);
        }

        private static string GetStateDirectory(string pakPath)
        {
            var gameDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(pakPath))
                          ?? throw new InvalidOperationException("Could not resolve Wartales directory for res.pak.");
            var normalized = gameDir.TrimEnd(System.IO.Path.DirectorySeparatorChar).ToLowerInvariant();
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..16];
            var settingsRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JACKOBTHATSME", "WartalesLauncher");
            return System.IO.Path.Combine(settingsRoot, "games", id);
        }

        private static bool HasInstalledMods(string stateDir)
        {
            var statePath = System.IO.Path.Combine(stateDir, "state.json");
            if (!File.Exists(statePath)) return false;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(statePath));
                if (!doc.RootElement.TryGetProperty("InstalledMods", out var mods)) return false;
                return mods.ValueKind == JsonValueKind.Array && mods.GetArrayLength() > 0;
            }
            catch
            {
                return true;
            }
        }
    }
}

internal sealed class PakBaselineMetadata
{
    public string Format { get; set; } = "";
    public long DataOffset { get; set; }
    public long OriginalFileLength { get; set; }
    public string OriginalPrefixSha256 { get; set; } = "";
    public long LastKnownFileLength { get; set; }
    public string LastKnownPrefixSha256 { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string MetadataPath { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string OriginalPrefixPath { get; set; } = "";
}

internal sealed record PakEntry(
    string Path,
    int Type,
    long RelativeOffset,
    long AbsoluteOffset,
    long OffsetFieldPos,
    long SizeFieldPos,
    uint Size);
