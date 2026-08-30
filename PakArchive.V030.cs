using System.Security.Cryptography;
using System.Text;

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
        Path = path; DataOffset = dataOffset; HeaderFileSize = headerFileSize; FileLength = fileLength; Entries = entries;
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
        if (entry.Size > int.MaxValue) throw new InvalidDataException("PAK entry is too large for this launcher: " + entryPath);
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
            if (!index.Entries.TryGetValue(key, out var e)) throw new InvalidDataException("Required PAK entry not found: " + key);
            targets.Add((e, kv.Value));
        }

        using var fs = File.Open(pakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
        var oldHeader = index.HeaderFileSize;
        var changes = new List<(PakEntry Entry, long NewRelative, uint NewSize)>();
        try
        {
            foreach (var t in targets)
            {
                fs.Position = fs.Length;
                var relative = fs.Position - index.DataOffset;
                if (t.Entry.Type == 0 && (relative < 0 || relative > uint.MaxValue)) throw new InvalidDataException("New PAK offset does not fit type-0 entry: " + t.Entry.Path);
                if (t.Data.LongLength > uint.MaxValue) throw new InvalidDataException("Replacement entry is too large: " + t.Entry.Path);
                bw.Write(t.Data);
                changes.Add((t.Entry, relative, (uint)t.Data.Length));
            }
            bw.Flush(); fs.Flush(true);
            foreach (var c in changes)
            {
                fs.Position = c.Entry.OffsetFieldPos; WriteOffset(bw, c.Entry.Type, c.NewRelative);
                fs.Position = c.Entry.SizeFieldPos; bw.Write(c.NewSize);
            }
            var newLength = fs.Length;
            if (index.FileLength <= uint.MaxValue && (uint)index.FileLength == oldHeader && newLength <= uint.MaxValue)
            {
                fs.Position = 8; bw.Write((uint)newLength);
            }
            bw.Flush(); fs.Flush(true);

            var verify = Open(pakPath);
            foreach (var t in targets)
            {
                var actual = verify.ReadEntry(t.Entry.Path);
                if (!SHA256.HashData(actual).SequenceEqual(SHA256.HashData(t.Data))) throw new InvalidDataException("PAK verification failed for " + t.Entry.Path);
            }
        }
        catch
        {
            try
            {
                foreach (var c in changes)
                {
                    fs.Position = c.Entry.OffsetFieldPos; WriteOffset(bw, c.Entry.Type, c.Entry.RelativeOffset);
                    fs.Position = c.Entry.SizeFieldPos; bw.Write(c.Entry.Size);
                }
                fs.Position = 8; bw.Write(oldHeader); bw.Flush(); fs.Flush(true);
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
        else if (type == 2) bw.Write((double)relative);
        else throw new InvalidDataException("Unsupported PAK entry type while writing: " + type);
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
