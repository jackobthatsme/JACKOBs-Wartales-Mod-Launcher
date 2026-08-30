# JACKOB Wartales Mod Package Format v1

A public mod is a normal ZIP. Package format v1 remains backwards compatible with launcher v0.2.x packages while launcher v0.3.0 adds generic external-file operations.

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
      "source": "patches/some-file.jbd"
    }
  ]
}
```

`minimumLauncherVersion` is optional. Packages using external-file operations introduced in launcher v0.3.0 should set it to `0.3.0` or newer.

A mod may contain any number of operations and may modify both entries inside `res.pak` and ordinary files under the selected Wartales installation directory.

## Operation paths

Operations inside `res.pak` use `entry`:

- `cdbPatch`
- `xmlMerge`
- `replaceEntry`

Operations outside `res.pak` use `target`:

- `externalBinaryDelta`
- `externalXmlMerge`
- `externalReplaceFile`

External targets are always relative to the selected game directory. Absolute paths, `.`/`..` traversal, empty path components and attempts to target `res.pak` through an external operation are rejected.

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

`expected` is a compatibility check. If the rebuild input does not contain the expected value, the launcher aborts before committing the rebuilt state.

### xmlMerge

Semantically adds or replaces selected nodes in an XML entry inside `res.pak`.

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

## External-file rebuild model

The launcher captures a clean baseline for every managed external target. Rebuild order is deterministic:

```text
clean baseline
-> Mod A
-> Mod B
-> Mod C
-> final file
```

Disabling Mod B rebuilds the target as:

```text
clean baseline
-> Mod A
-> Mod C
-> final file
```

The launcher does not restore an old copy owned by one mod. `Restore Vanilla` restores all launcher-managed baselines, including external files.

## externalBinaryDelta

Applies a guarded binary transformation to any ordinary file under the game directory.

```json
{
  "type": "externalBinaryDelta",
  "target": "some-file.dat",
  "source": "patches/some-file.jbd",
  "expectedSha256": "optional duplicate of the delta baseline SHA-256",
  "resultingSha256": "optional duplicate of the standalone result SHA-256"
}
```

Launcher v0.3.0 supports two binary-delta encodings.

### JACKOBBD1 — variable-length COPY/ADD delta

`JACKOBBD1` is intended for compact patches where the resulting file may be longer or shorter than vanilla. It does not distribute the complete target file.

Binary layout, little-endian integers:

```text
ASCII[9]  "JACKOBBD1"
byte[32]  SHA-256 of the clean baseline
byte[32]  SHA-256 of the standalone patched result
uint64    clean baseline size
uint64    standalone result size
uint32    segment count
segments...
```

Segment types:

```text
0x00 COPY
    uint64 baselineOffset
    uint32 length

0x01 ADD
    uint32 length
    byte[length] literalData
```

A delta result is reconstructed only from verified baseline ranges (`COPY`) and package-owned literal data (`ADD`). COPY ranges must be monotonic so the launcher can derive baseline-relative edit ranges safely.

For every skipped/replaced baseline range the launcher derives the exact expected original bytes from the captured clean baseline. The complete baseline must already match the SHA-256 stored in the delta header, so the launcher never applies this format blindly to an unknown game version.

The standalone output size and SHA-256 from the header are verified before any game file is committed.

### JACKOB_BINARY_DELTA_V1 — fixed-length JSON hunks

The previous fixed-length JSON form remains supported:

```json
{
  "format": "JACKOB_BINARY_DELTA_V1",
  "expectedSha256": "64 hex characters",
  "resultingSha256": "64 hex characters",
  "patches": [
    {
      "offset": 1234,
      "expected": "AABBCCDD",
      "replacement": "11223344"
    }
  ]
}
```

In this encoding `expected` and `replacement` must have equal length. Every hunk is checked against the clean baseline.

### Multiple binary mods on one target

Binary changes are tracked in clean-baseline coordinates rather than current-output offsets. Therefore an earlier variable-length edit does not silently shift the offsets used by a later mod.

Non-overlapping baseline edits can be composed. Overlapping edits are treated as a mod conflict and abort before commit. Identical edits are idempotent.

If a target has already been transformed by an arbitrary non-binary operation such as `externalReplaceFile` or `externalXmlMerge`, a later baseline-relative binary delta is rejected rather than guessed.

If the clean target no longer matches the supported game version, the launcher stops with:

```text
Unsupported game version — target file has changed. This mod needs an update.
```

## externalXmlMerge

Semantically merges selected nodes into any XML file outside `res.pak`.

```json
{
  "type": "externalXmlMerge",
  "target": "localization/custom.xml",
  "source": "patches/custom.xml.json",
  "expectedSha256": "optional baseline SHA-256"
}
```

It uses `JACKOB_XML_PATCH_V1`. The localization-style `sheet` + `id` form remains supported. For arbitrary XML a node may instead use XPath:

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

If `xpath` finds a node, that node is replaced. If it is missing, `parentXPath` identifies where the new node is appended. Only specified nodes are changed; a complete vanilla XML does not need to be distributed.

## externalReplaceFile

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

The target may be any ordinary game file type such as `.json`, `.prefab`, `.png`, `.dat` or `.hx`. Names and extensions are not hardcoded.

A missing baseline file is supported for `externalReplaceFile`: the launcher records that the file originally did not exist and removes the managed file again when restoring vanilla.

## Transaction and safety model

All package transformations are prepared from captured baselines before commit. External targets are snapshotted before writing. External files are written through temporary files. Managed `res.pak` entry payloads are appended before index pointers are switched.

If a later commit step fails, the launcher attempts to restore the external snapshots and the previous managed `res.pak` entry data. Launcher state is saved only after post-write verification succeeds.

Before every install, update, uninstall or restore, managed files are checked against the last applied hashes. Unexpected game updates or edits stop the operation instead of being overwritten.

## Backwards compatibility

Packages containing only `cdbPatch`, `xmlMerge` and `replaceEntry` remain valid and do not need `minimumLauncherVersion`.
