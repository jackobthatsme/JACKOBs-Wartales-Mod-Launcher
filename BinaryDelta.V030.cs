using System.Text;
using System.Text.Json.Nodes;

namespace JACKOBsWartalesModLauncher;

internal static partial class LauncherCore
{
    private const string JbdMagic = "JACKOBBD1";

    private sealed record ParsedBinaryDelta(
        string ExpectedSha256,
        string ResultingSha256,
        long OriginalSize,
        long ResultingSize,
        List<BinaryBaselineEdit> Edits);

    private static void ApplyExternalBinaryDeltaV030(ModPackage package, LauncherState state, ManifestOperation op,
        Dictionary<string, ExternalWorkingFile> working, string stateDir)
    {
        var target = NormalizeGameRelativePath(op.Target);
        var managed = state.ManagedFiles[target.ToLowerInvariant()];
        if (!managed.OriginalExists) throw UnsupportedGameVersion(target);

        var baselinePath = Path.Combine(stateDir, managed.OriginalFile.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(baselinePath)) throw new FileNotFoundException("Original external baseline is missing.", baselinePath);
        var baseline = File.ReadAllBytes(baselinePath);

        var source = package.ReadBytes(op.Source);
        var parsed = LooksLikeJbd(source)
            ? ParseJbdDelta(source, baseline, target)
            : ParseJsonBinaryDelta(source, baseline, target, op);

        ValidateSha256(parsed.ExpectedSha256, "externalBinaryDelta expectedSha256");
        ValidateSha256(parsed.ResultingSha256, "externalBinaryDelta resultingSha256");

        if (!string.IsNullOrWhiteSpace(op.ExpectedSha256))
        {
            ValidateSha256(op.ExpectedSha256, "externalBinaryDelta expectedSha256");
            if (!op.ExpectedSha256.Equals(parsed.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Manifest expectedSha256 does not match the binary delta header for {target}.");
        }
        if (!string.IsNullOrWhiteSpace(op.ResultingSha256))
        {
            ValidateSha256(op.ResultingSha256, "externalBinaryDelta resultingSha256");
            if (!op.ResultingSha256.Equals(parsed.ResultingSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Manifest resultingSha256 does not match the binary delta header for {target}.");
        }

        if (baseline.LongLength != parsed.OriginalSize ||
            !Sha256(baseline).Equals(parsed.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw UnsupportedGameVersion(target);

        var standalone = ApplyBaselineEdits(baseline, parsed.Edits, target);
        if (standalone.LongLength != parsed.ResultingSize ||
            !Sha256(standalone).Equals(parsed.ResultingSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Binary delta resulting SHA-256 does not match its declaration for {target}.");

        var current = working[target];
        if (!current.Exists) throw UnsupportedGameVersion(target);

        var existingEdits = current.BinaryEdits;
        if (existingEdits is null)
        {
            if (!current.Bytes.AsSpan().SequenceEqual(baseline))
                throw new InvalidOperationException(
                    $"Binary delta conflict for {target}: the target was already transformed by a non-binary operation. " +
                    "Reorder or redesign the mod operations so binary deltas are composed directly from the clean baseline.");
            existingEdits = new List<BinaryBaselineEdit>();
        }

        var merged = MergeBinaryEdits(existingEdits, parsed.Edits, target);
        var rebuilt = ApplyBaselineEdits(baseline, merged, target);
        working[target] = new ExternalWorkingFile(true, rebuilt, merged);
    }

    private static bool LooksLikeJbd(byte[] source)
    {
        var magic = Encoding.ASCII.GetBytes(JbdMagic);
        return source.Length >= magic.Length && source.AsSpan(0, magic.Length).SequenceEqual(magic);
    }

    private static ParsedBinaryDelta ParseJsonBinaryDelta(byte[] source, byte[] baseline, string target, ManifestOperation op)
    {
        var node = JsonNode.Parse(source) ?? throw new InvalidDataException($"Invalid JSON binary delta for {target}.");
        var delta = node.AsObject();
        if (delta["format"]?.GetValue<string>() != BinaryDeltaFormat)
            throw new InvalidDataException("Unsupported binary delta format.");

        var expectedSha = op.ExpectedSha256 ?? delta["expectedSha256"]?.GetValue<string>();
        var resultingSha = op.ResultingSha256 ?? delta["resultingSha256"]?.GetValue<string>();
        ValidateSha256(expectedSha, "externalBinaryDelta expectedSha256");
        ValidateSha256(resultingSha, "externalBinaryDelta resultingSha256");
        if (!Sha256(baseline).Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            throw UnsupportedGameVersion(target);

        var edits = new List<BinaryBaselineEdit>();
        var hunks = delta["patches"]?.AsArray() ?? throw new InvalidDataException("Binary delta has no patches array.");
        foreach (var n in hunks)
        {
            var h = n?.AsObject() ?? throw new InvalidDataException("Invalid binary delta patch.");
            var offset = h["offset"]?.GetValue<long>() ?? throw new InvalidDataException("Binary delta patch has no offset.");
            var expected = ParseHex(h["expected"]?.GetValue<string>() ?? throw new InvalidDataException("Binary delta patch has no expected bytes."));
            var replacement = ParseHex(h["replacement"]?.GetValue<string>() ?? throw new InvalidDataException("Binary delta patch has no replacement bytes."));
            if (expected.Length != replacement.Length)
                throw new InvalidDataException("JACKOB_BINARY_DELTA_V1 JSON hunks require equal-length expected and replacement byte sequences.");
            ValidateBaselineEdit(baseline, new BinaryBaselineEdit(offset, expected, replacement), target);
            edits.Add(new BinaryBaselineEdit(offset, expected, replacement));
        }

        edits = MergeBinaryEdits(Array.Empty<BinaryBaselineEdit>(), edits, target);
        var standalone = ApplyBaselineEdits(baseline, edits, target);
        if (!Sha256(standalone).Equals(resultingSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Binary delta resulting SHA-256 does not match its declaration for {target}.");

        return new ParsedBinaryDelta(expectedSha!, resultingSha!, baseline.LongLength, standalone.LongLength, edits);
    }

    private static ParsedBinaryDelta ParseJbdDelta(byte[] source, byte[] baseline, string target)
    {
        using var stream = new MemoryStream(source, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var magicBytes = ReadExactly(reader, Encoding.ASCII.GetByteCount(JbdMagic), "JACKOBBD1 magic");
        if (!Encoding.ASCII.GetString(magicBytes).Equals(JbdMagic, StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported JACKOB binary delta magic.");

        var expectedSha = Convert.ToHexString(ReadExactly(reader, 32, "baseline SHA-256")).ToLowerInvariant();
        var resultingSha = Convert.ToHexString(ReadExactly(reader, 32, "resulting SHA-256")).ToLowerInvariant();
        var originalSizeU = reader.ReadUInt64();
        var resultingSizeU = reader.ReadUInt64();
        var segmentCount = reader.ReadUInt32();

        if (originalSizeU > int.MaxValue || resultingSizeU > int.MaxValue)
            throw new InvalidDataException("JACKOBBD1 file size exceeds the launcher in-memory safety limit.");
        var originalSize = (long)originalSizeU;
        var resultingSize = (long)resultingSizeU;
        if (originalSize != baseline.LongLength || !Sha256(baseline).Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            throw UnsupportedGameVersion(target);
        if (segmentCount == 0 || segmentCount > 1_000_000)
            throw new InvalidDataException("JACKOBBD1 has an invalid segment count.");

        using var standalone = new MemoryStream((int)resultingSize);
        using var pendingAdd = new MemoryStream();
        var edits = new List<BinaryBaselineEdit>();
        long baselineCursor = 0;

        for (uint i = 0; i < segmentCount; i++)
        {
            if (stream.Position >= stream.Length) throw new EndOfStreamException("Unexpected end of JACKOBBD1 segments.");
            var kind = reader.ReadByte();
            if (kind == 0)
            {
                var offsetU = reader.ReadUInt64();
                var lengthU = reader.ReadUInt32();
                if (offsetU > int.MaxValue || lengthU > int.MaxValue)
                    throw new InvalidDataException("JACKOBBD1 COPY range exceeds the launcher safety limit.");
                var offset = (long)offsetU;
                var length = (int)lengthU;
                if (offset < baselineCursor)
                    throw new InvalidDataException("JACKOBBD1 COPY ranges must be monotonic for safe multi-mod composition.");
                if (offset + length > baseline.LongLength)
                    throw new InvalidDataException($"JACKOBBD1 COPY range is outside {target}.");

                if (offset > baselineCursor || pendingAdd.Length > 0)
                {
                    var expectedLength = checked((int)(offset - baselineCursor));
                    var expected = baseline.AsSpan((int)baselineCursor, expectedLength).ToArray();
                    var replacement = pendingAdd.ToArray();
                    edits.Add(new BinaryBaselineEdit(baselineCursor, expected, replacement));
                    pendingAdd.SetLength(0);
                    pendingAdd.Position = 0;
                }

                standalone.Write(baseline, (int)offset, length);
                baselineCursor = offset + length;
            }
            else if (kind == 1)
            {
                var lengthU = reader.ReadUInt32();
                if (lengthU > int.MaxValue) throw new InvalidDataException("JACKOBBD1 ADD segment exceeds the launcher safety limit.");
                var literal = ReadExactly(reader, (int)lengthU, "JACKOBBD1 ADD payload");
                standalone.Write(literal, 0, literal.Length);
                pendingAdd.Write(literal, 0, literal.Length);
            }
            else
                throw new InvalidDataException($"Unsupported JACKOBBD1 segment kind: {kind}.");
        }

        if (baselineCursor < baseline.LongLength || pendingAdd.Length > 0)
        {
            var expectedLength = checked((int)(baseline.LongLength - baselineCursor));
            var expected = baseline.AsSpan((int)baselineCursor, expectedLength).ToArray();
            edits.Add(new BinaryBaselineEdit(baselineCursor, expected, pendingAdd.ToArray()));
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException("JACKOBBD1 contains trailing bytes after the declared segments.");

        var result = standalone.ToArray();
        if (result.LongLength != resultingSize || !Sha256(result).Equals(resultingSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"JACKOBBD1 resulting SHA-256 does not match its header for {target}.");

        edits = MergeBinaryEdits(Array.Empty<BinaryBaselineEdit>(), edits, target);
        var reconstructed = ApplyBaselineEdits(baseline, edits, target);
        if (!reconstructed.AsSpan().SequenceEqual(result))
            throw new InvalidDataException($"JACKOBBD1 edit reconstruction failed for {target}.");

        return new ParsedBinaryDelta(expectedSha, resultingSha, originalSize, resultingSize, edits);
    }

    private static byte[] ReadExactly(BinaryReader reader, int count, string field)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException($"Unexpected end of binary delta while reading {field}.");
        return bytes;
    }

    private static List<BinaryBaselineEdit> MergeBinaryEdits(
        IEnumerable<BinaryBaselineEdit> existing,
        IEnumerable<BinaryBaselineEdit> incoming,
        string target)
    {
        var merged = existing.Select(CloneBinaryEdit).ToList();
        foreach (var edit in incoming)
        {
            var identical = merged.FirstOrDefault(x => SameBinaryEdit(x, edit));
            if (identical is not null) continue;

            var conflict = merged.FirstOrDefault(x => BinaryEditsConflict(x, edit));
            if (conflict is not null)
            {
                throw new InvalidOperationException(
                    $"Binary mod conflict for {target}: baseline ranges overlap at offset {Math.Max(conflict.Offset, edit.Offset)}. " +
                    "The launcher stopped without writing anything.");
            }
            merged.Add(CloneBinaryEdit(edit));
        }

        merged.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return merged;
    }

    private static BinaryBaselineEdit CloneBinaryEdit(BinaryBaselineEdit edit) =>
        new(edit.Offset, (byte[])edit.Expected.Clone(), (byte[])edit.Replacement.Clone());

    private static bool SameBinaryEdit(BinaryBaselineEdit a, BinaryBaselineEdit b) =>
        a.Offset == b.Offset && a.Expected.AsSpan().SequenceEqual(b.Expected) && a.Replacement.AsSpan().SequenceEqual(b.Replacement);

    private static bool BinaryEditsConflict(BinaryBaselineEdit a, BinaryBaselineEdit b)
    {
        if (a.Expected.Length == 0 && b.Expected.Length == 0) return a.Offset == b.Offset;
        if (a.Expected.Length == 0) return a.Offset >= b.Offset && a.Offset < b.End;
        if (b.Expected.Length == 0) return b.Offset >= a.Offset && b.Offset < a.End;
        return a.Offset < b.End && b.Offset < a.End;
    }

    private static void ValidateBaselineEdit(byte[] baseline, BinaryBaselineEdit edit, string target)
    {
        if (edit.Offset < 0 || edit.Offset > int.MaxValue || edit.End > baseline.LongLength)
            throw new InvalidDataException($"Binary delta range is outside {target}.");
        var start = (int)edit.Offset;
        if (!baseline.AsSpan(start, edit.Expected.Length).SequenceEqual(edit.Expected))
            throw UnsupportedGameVersion(target);
    }

    private static byte[] ApplyBaselineEdits(byte[] baseline, IEnumerable<BinaryBaselineEdit> edits, string target)
    {
        var ordered = edits.OrderBy(x => x.Offset).ToArray();
        long cursor = 0;
        long outputSize = baseline.LongLength;
        foreach (var edit in ordered)
        {
            ValidateBaselineEdit(baseline, edit, target);
            if (edit.Offset < cursor)
                throw new InvalidOperationException($"Overlapping binary edits detected for {target}.");
            outputSize = checked(outputSize - edit.Expected.LongLength + edit.Replacement.LongLength);
            cursor = edit.End;
        }
        if (outputSize < 0 || outputSize > int.MaxValue)
            throw new InvalidDataException($"Binary delta output size is outside the launcher safety limit for {target}.");

        using var output = new MemoryStream((int)outputSize);
        cursor = 0;
        foreach (var edit in ordered)
        {
            var copyLength = checked((int)(edit.Offset - cursor));
            if (copyLength > 0) output.Write(baseline, (int)cursor, copyLength);
            if (edit.Replacement.Length > 0) output.Write(edit.Replacement, 0, edit.Replacement.Length);
            cursor = edit.End;
        }
        if (cursor < baseline.LongLength)
            output.Write(baseline, (int)cursor, checked((int)(baseline.LongLength - cursor)));
        return output.ToArray();
    }
}
