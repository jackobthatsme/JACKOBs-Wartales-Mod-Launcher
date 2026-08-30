# JACKOB Wartales Mod Package Format v1

A public mod is a normal ZIP. The v1 package format is intentionally extensible and remains backwards compatible with launcher v0.2.x packages.

At the ZIP root:

```text
manifest.json
patches/...
assets/...
README.txt
```

## Manifest

```json
{
  "format": "JACKOB_WARTALES_MOD_V1",
  "id": "author.example-mod",
  "name": "Example Mod",
  "version": "1.0.0",
  "author": "Author",
  "game": "Wartales",
  "description": "Example.",
  "minimumLauncherVersion": "0.3.0",
  "operations": [
    {
      "type": "cdbPatch",
      "entry": "data.cdb",
      "source": "patches/data.cdb.json"
    },
    {
      "type": "externalBinaryDelta",
      "target": "some-file.dat",
      "source": "patches/some-file.delta.json"
    }
  ]
}
```

`minimumLauncherVersion` is optional. Packages that use the external-file operations introduced in launcher v0.3.0 should set it to `0.3.0` or newer.

A mod may contain any number of operations and may modify both entries inside `res.pak` and ordinary files under the Wartales installation directory.

## Operations inside res.pak

### cdbPatch

Semantically changes a JSON CDB entry, normally `data.cdb`, by sheet + row id + property path.

Supported patch operations:

- `set`
- `addLine`
- `removeLine`

Example:

```json
{
  "op": "set",
  "sheet": "constant",
  "id": "ExampleConstant",
  "path": ["value"],
  "expected": 1,
  "value": 2
}
```

`expected` is a compatibility check. If the current rebuild input does not contain the expected value, the launcher aborts before committing the rebuilt state.

### xmlMerge

Semantically adds or replaces selected nodes in an XML entry inside `res.pak`. The package ships only the nodes it owns, not a complete vanilla XML file.

Patch file format:

```json
{
  "format": "JACKOB_XML_PATCH_V1",
  "nodes": [
    {
      "sheet": "SomeSheet",
      "id": "SomeNodeId",
      "xml": "<SomeNodeId>Replacement text</SomeNodeId>"
    }
  ]
}
```

### replaceEntry

Replaces one existing `res.pak` entry with package-owned bytes.

```json
{
  "type": "replaceEntry",
  "entry": "path/in/pak/file.png",
  "source": "assets/file.png"
}
```

## External-file operations (launcher v0.3.0+)

External targets are always relative to the selected Wartales installation directory. Absolute paths, `..` traversal, empty path components, and attempts to target `res.pak` through an external operation are rejected.

The launcher captures a clean baseline for every managed external target. Rebuild order is:

```text
clean baseline
-> Mod A
-> Mod B
-> Mod C
-> final files
```

Disabling Mod B rebuilds the result as:

```text
clean baseline
-> Mod A
-> Mod C
-> final files
```

This is the same model used for managed `res.pak` entries. `Restore Vanilla` restores all launcher-managed baselines, including external files.

### externalBinaryDelta

Applies a small, fixed-length binary delta to any file under the game directory.

Manifest operation:

```json
{
  "type": "externalBinaryDelta",
  "target": "some-file.dat",
  "source": "patches/some-file.delta.json"
}
```

Delta format:

```json
{
  "format": "JACKOB_BINARY_DELTA_V1",
  "expectedSha256": "64-hex-character SHA-256 of the clean baseline",
  "resultingSha256": "64-hex-character SHA-256 after this delta is applied to the clean baseline",
  "patches": [
    {
      "offset": 1234,
      "expected": "AABBCCDD",
      "replacement": "11223344"
    }
  ]
}
```

Rules:

- `expectedSha256` is mandatory, either in the delta JSON or as `expectedSha256` on the manifest operation.
- `resultingSha256` is mandatory, either in the delta JSON or as `resultingSha256` on the manifest operation.
- `expected` and `replacement` are hexadecimal byte strings and must have equal length in binary-delta v1.
- The baseline SHA-256 must match before the delta is accepted.
- Every hunk verifies its `expected` bytes before writing replacement bytes.
- The declared `resultingSha256` is verified by applying the delta to the captured clean baseline.
- When several mods patch the same target, hunks are also checked against the current rebuild result. Conflicting changes abort safely.

If the target no longer matches the supported game version, the launcher stops with an error such as:

```text
Unsupported game version — target file has changed. This mod needs an update.
```

The launcher never blindly writes a binary patch.

### externalXmlMerge

Semantically merges selected nodes into any XML file outside `res.pak`.

```json
{
  "type": "externalXmlMerge",
  "target": "localization/custom.xml",
  "source": "patches/custom.xml.json",
  "expectedSha256": "optional baseline SHA-256"
}
```

It uses `JACKOB_XML_PATCH_V1`. The original localization-style `sheet` + `id` node form remains supported. For arbitrary XML files, a node may instead use XPath:

```json
{
  "format": "JACKOB_XML_PATCH_V1",
  "nodes": [
    {
      "xpath": "/root/items/item[@id='example']",
      "parentXPath": "/root/items",
      "xml": "<item id=\"example\">Merged value</item>"
    }
  ]
}
```

If `xpath` finds a node, that node is replaced semantically. If it does not exist, `parentXPath` identifies where the new node is appended. Only specified nodes are changed; the entire vanilla XML does not need to be distributed.

### externalReplaceFile

Safely replaces an ordinary file outside `res.pak`.

```json
{
  "type": "externalReplaceFile",
  "target": "path/to/file.json",
  "source": "assets/file.json",
  "expectedSha256": "optional SHA-256 of the clean baseline",
  "resultingSha256": "optional SHA-256 of the replacement"
}
```

The target may be any ordinary game file type (`.json`, `.prefab`, `.png`, `.dat`, `.hx`, etc.). The launcher does not hardcode file names or extensions.

If `expectedSha256` is provided, the captured baseline must match it. If `resultingSha256` is provided, the package-owned replacement bytes must match it.

A missing baseline file is supported for `externalReplaceFile`: the launcher records that the file originally did not exist and deletes the managed file again when restoring the baseline.

## Transaction and safety model

Before a rebuild, all package transformations are prepared in memory from captured baselines. External targets are snapshotted before commit. External files are written first, then the `res.pak` index is switched only after its replacement payloads are appended successfully.

If a later step fails, the launcher attempts to restore the external snapshots and restore the previous managed `res.pak` entry bytes. State is saved only after post-write verification succeeds.

The launcher also verifies that managed files still match its last applied hashes before starting another install/update/uninstall. Unexpected game updates or edits therefore stop the operation instead of being overwritten.

## Backwards compatibility

Packages containing only `cdbPatch`, `xmlMerge`, and `replaceEntry` remain valid. They do not need `minimumLauncherVersion`.
