using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Xml;

namespace JACKOBsWartalesModLauncher;

internal static partial class LauncherCore
{
    // -------------------- CDB / XML patch engines --------------------

    private static byte[] ApplyCdbPatch(byte[] input, JsonNode patchNode)
    {
        var patch = patchNode.AsObject();
        if (patch["format"]?.GetValue<string>() != CdbPatchFormat) throw new InvalidDataException("Unsupported CDB patch format.");
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
                    if (!JsonNode.DeepEquals(existing, line)) throw new InvalidOperationException($"CDB conflict: {sheetName}/{id} already exists with different data.");
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
            if (kind != "set") throw new InvalidDataException($"Unsupported CDB patch op: {kind}");

            var lineId = op["id"]?.GetValue<string>() ?? throw new InvalidDataException("set has no id.");
            var lineObj = FindLine(lines, lineId);
            var path = op["path"]?.AsArray() ?? throw new InvalidDataException("set has no path.");
            var current = GetAtPath(lineObj, path);
            var expected = op["expected"];
            if (!JsonNode.DeepEquals(current, expected))
                throw new InvalidOperationException($"CDB compatibility check failed at {sheetName}/{lineId}/{FormatPath(path)}. Expected {NodeText(expected)}, found {NodeText(current)}. No data was written.");
            SetAtPath(lineObj, path, op["value"]?.DeepClone());
        }
        return Encoding.UTF8.GetBytes(root.ToJsonString(CompactJson));
    }

    private static byte[] ApplyXmlPatch(byte[] input, JsonNode patchNode)
    {
        var patch = patchNode.AsObject();
        if (patch["format"]?.GetValue<string>() != XmlPatchFormat) throw new InvalidDataException("Unsupported XML patch format.");
        var text = Encoding.UTF8.GetString(RemoveUtf8Bom(input));
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        doc.LoadXml(text);
        var nodes = patch["nodes"]?.AsArray() ?? throw new InvalidDataException("XML patch has no nodes array.");
        foreach (var n in nodes)
        {
            var item = n?.AsObject() ?? throw new InvalidDataException("Invalid XML patch node.");
            var xml = item["xml"]?.GetValue<string>() ?? throw new InvalidDataException("XML node has no xml.");
            var fragmentDoc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            fragmentDoc.LoadXml(xml);
            var imported = doc.ImportNode(fragmentDoc.DocumentElement!, true);

            var xpath = item["xpath"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(xpath))
            {
                var existing = doc.SelectSingleNode(xpath);
                if (existing?.ParentNode is not null)
                {
                    existing.ParentNode.ReplaceChild(imported, existing);
                    continue;
                }

                var parentXPath = item["parentXPath"]?.GetValue<string>()
                                  ?? throw new InvalidDataException($"XML xpath not found and parentXPath was not supplied: {xpath}");
                var parent = doc.SelectSingleNode(parentXPath)
                             ?? throw new InvalidDataException($"XML parentXPath not found: {parentXPath}");
                parent.AppendChild(imported);
                continue;
            }

            var sheetName = item["sheet"]?.GetValue<string>() ?? throw new InvalidDataException("XML node has neither xpath nor sheet.");
            var id = item["id"]?.GetValue<string>() ?? throw new InvalidDataException("XML sheet node has no id.");
            var sheet = FindXmlSheet(doc, sheetName);
            XmlElement? existingSheetNode = null;
            foreach (XmlNode child in sheet.ChildNodes)
                if (child is XmlElement e && e.Name == id) { existingSheetNode = e; break; }
            if (existingSheetNode is not null) sheet.ReplaceChild(imported, existingSheetNode);
            else
            {
                sheet.AppendChild(doc.CreateWhitespace("\n        "));
                sheet.AppendChild(imported);
            }
        }
        return Encoding.UTF8.GetBytes(doc.OuterXml);
    }

    private static JsonObject FindSheet(JsonNode root, string name)
    {
        var sheets = root["sheets"]?.AsArray() ?? throw new InvalidDataException("data.cdb has no sheets array.");
        foreach (var n in sheets) if (n is JsonObject o && o["name"]?.GetValue<string>() == name) return o;
        throw new InvalidDataException($"data.cdb sheet not found: {name}");
    }

    private static JsonObject FindLine(JsonArray lines, string id) =>
        FindLineOrNull(lines, id) ?? throw new InvalidDataException($"data.cdb line not found: {id}");

    private static JsonObject? FindLineOrNull(JsonArray lines, string id)
    {
        foreach (var n in lines) if (n is JsonObject o && o["id"]?.GetValue<string>() == id) return o;
        return null;
    }

    private static int FindLineIndex(JsonArray lines, string id)
    {
        for (var i = 0; i < lines.Count; i++) if (lines[i] is JsonObject o && o["id"]?.GetValue<string>() == id) return i;
        return -1;
    }

    private static JsonNode? GetAtPath(JsonNode start, JsonArray path)
    {
        JsonNode? current = start;
        foreach (var token in path)
        {
            if (current is null) return null;
            if (token is JsonValue v && v.TryGetValue<int>(out var idx)) current = current.AsArray()[idx];
            else current = current.AsObject()[token?.GetValue<string>() ?? throw new InvalidDataException("Invalid path token.")];
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
            if (token is JsonValue v && v.TryGetValue<int>(out var idx)) current = current.AsArray()[idx] ?? throw new InvalidDataException("Null path node.");
            else current = current.AsObject()[token.GetValue<string>()] ?? throw new InvalidDataException("Missing path node.");
        }
        var last = path[^1]!;
        if (last is JsonValue lv && lv.TryGetValue<int>(out var li)) current.AsArray()[li] = value;
        else current.AsObject()[last.GetValue<string>()] = value;
    }

    private static XmlElement FindXmlSheet(XmlDocument doc, string name)
    {
        var root = doc.DocumentElement ?? throw new InvalidDataException("XML has no root element.");
        foreach (XmlNode n in root.ChildNodes)
            if (n is XmlElement e && e.Name == "sheet" && e.GetAttribute("name") == name) return e;
        throw new InvalidDataException($"Localization sheet not found: {name}");
    }

    private static ReadOnlySpan<byte> RemoveUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsSpan(3) : bytes;

    // -------------------- manifest / path validation --------------------

    private static void ValidateManifest(ModManifest manifest)
    {
        if (manifest.Format != ModFormat) throw new InvalidDataException("Unsupported mod package format.");
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("Mod manifest is missing id, name or version.");
        if (!string.IsNullOrWhiteSpace(manifest.MinimumLauncherVersion) && CompareVersions(LauncherVersion, manifest.MinimumLauncherVersion) < 0)
            throw new InvalidOperationException($"This mod requires JACKOB's Wartales Mod Launcher v{manifest.MinimumLauncherVersion} or newer. Current launcher: v{LauncherVersion}.");
        if (manifest.Operations.Count == 0) throw new InvalidDataException("Mod package has no operations.");

        foreach (var op in manifest.Operations)
        {
            if (string.IsNullOrWhiteSpace(op.Type) || string.IsNullOrWhiteSpace(op.Source)) throw new InvalidDataException("Mod operation is incomplete.");
            _ = NormalizePackagePath(op.Source);
            if (IsPakOperation(op))
            {
                if (string.IsNullOrWhiteSpace(op.Entry)) throw new InvalidDataException($"{op.Type} requires entry.");
                _ = NormalizePakPath(op.Entry);
            }
            else if (IsExternalOperation(op))
            {
                if (string.IsNullOrWhiteSpace(op.Target)) throw new InvalidDataException($"{op.Type} requires target.");
                var target = NormalizeGameRelativePath(op.Target);
                if (target.Equals("res.pak", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("External operations cannot target res.pak.");
            }
            else throw new InvalidDataException($"Unsupported package operation: {op.Type}");
        }
    }

    private static bool IsPakOperation(ManifestOperation op) => op.Type is "cdbPatch" or "xmlMerge" or "replaceEntry";
    private static bool IsExternalOperation(ManifestOperation op) => op.Type is "externalBinaryDelta" or "externalXmlMerge" or "externalReplaceFile";

    private static string NormalizePakPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Split('/').Any(x => x is "" or "." or ".."))
            throw new InvalidDataException("Unsafe PAK entry path.");
        return normalized;
    }

    private static string NormalizePackagePath(string path)
    {
        if (Path.IsPathRooted(path)) throw new InvalidDataException("Unsafe package source path.");
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Split('/').Any(x => x is "" or "." or ".."))
            throw new InvalidDataException("Unsafe package source path.");
        return normalized;
    }

    private static string NormalizeGameRelativePath(string path)
    {
        if (Path.IsPathRooted(path)) throw new InvalidDataException("External target must be relative to the game folder.");
        var normalized = path.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Split('/').Any(x => x is "" or "." or ".."))
            throw new InvalidDataException("Unsafe external target path.");
        return normalized;
    }

    private static string ResolveGameTarget(string gameDir, string relative)
    {
        var normalized = NormalizeGameRelativePath(relative);
        var root = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("External target escapes the game folder.");
        return full;
    }

    private static int CompareVersions(string current, string required)
    {
        static Version Parse(string value)
        {
            var core = value.Split('-', '+')[0];
            if (!Version.TryParse(core, out var v)) throw new InvalidDataException($"Invalid launcher version: {value}");
            return v;
        }
        return Parse(current).CompareTo(Parse(required));
    }

    private static InvalidOperationException UnsupportedGameVersion(string target) =>
        new($"Unsupported game version — target file has changed. This mod needs an update. Target: {target}");

    private static void ValidateSha256(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException($"{field} must be a 64-character SHA-256 hex string.");
    }

    private static byte[] ParseHex(string value)
    {
        var compact = new string(value.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (compact.Length % 2 != 0 || !compact.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid hex byte sequence in binary delta.");
        return Convert.FromHexString(compact);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value) sb.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        return sb.ToString();
    }

    private static string FormatPath(JsonArray path) => string.Join("/", path.Select(x => x?.ToJsonString().Trim('"') ?? "null"));
    private static string NodeText(JsonNode? node) => node?.ToJsonString() ?? "null";
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    private static InstalledModState CloneInstalledMod(InstalledModState m) => new()
    {
        Id = m.Id, Name = m.Name, Version = m.Version, PackageFile = m.PackageFile, PackageSha256 = m.PackageSha256
    };

    private static ModManifest CloneManifest(ModManifest m) => new()
    {
        Format = m.Format,
        Id = m.Id,
        Name = m.Name,
        Version = m.Version,
        Author = m.Author,
        Game = m.Game,
        Description = m.Description,
        MinimumLauncherVersion = m.MinimumLauncherVersion,
        Operations = m.Operations.Select(o => new ManifestOperation
        {
            Type = o.Type, Entry = o.Entry, Target = o.Target, Source = o.Source,
            ExpectedSha256 = o.ExpectedSha256, ResultingSha256 = o.ResultingSha256
        }).ToList()
    };
}
