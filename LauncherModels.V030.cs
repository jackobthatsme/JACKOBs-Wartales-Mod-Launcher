using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JACKOBsWartalesModLauncher;

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

    public JsonNode ReadJson(string name) => JsonNode.Parse(ReadBytes(name)) ?? throw new InvalidDataException($"Invalid JSON: {name}");

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
    public string? MinimumLauncherVersion { get; set; }
    public List<ManifestOperation> Operations { get; set; } = new();
}

internal sealed class ManifestOperation
{
    public string Type { get; set; } = "";
    public string Entry { get; set; } = "";
    public string Target { get; set; } = "";
    public string Source { get; set; } = "";
    public string? ExpectedSha256 { get; set; }
    public string? ResultingSha256 { get; set; }
}

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
    public Dictionary<string, ManagedFileState> ManagedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

internal sealed class ManagedFileState
{
    public string Path { get; set; } = "";
    public string OriginalFile { get; set; } = "";
    public bool OriginalExists { get; set; }
    public string OriginalHash { get; set; } = "";
    public bool LastAppliedExists { get; set; }
    public string LastAppliedHash { get; set; } = "";
}

internal sealed record BinaryBaselineEdit(long Offset, byte[] Expected, byte[] Replacement)
{
    public long End => Offset + Expected.LongLength;
}

internal sealed record ExternalWorkingFile(
    bool Exists,
    byte[] Bytes,
    List<BinaryBaselineEdit>? BinaryEdits = null);

internal sealed record ExternalSnapshot(bool Existed, string BackupPath);
