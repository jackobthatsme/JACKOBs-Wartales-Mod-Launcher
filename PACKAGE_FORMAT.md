# JACKOB Wartales Mod Package Format v1

A public mod is a **normal ZIP**, not a nested archive and not an executable.

At the ZIP root:

```text
manifest.json
patches/...
assets/...
README.txt
```

`manifest.json` example:

```json
{
  "format": "JACKOB_WARTALES_MOD_V1",
  "id": "jackobthatsme.example-mod",
  "name": "Example Mod",
  "version": "1.0.0",
  "author": "JACKOBTHATSME",
  "game": "Wartales",
  "description": "Example.",
  "operations": [
    {
      "type": "cdbPatch",
      "entry": "data.cdb",
      "source": "patches/data.cdb.json"
    }
  ]
}
```

## cdbPatch

Changes `data.cdb` semantically by sheet + row id + property path.

Supported operations:

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

`expected` is a safety check. If the user's current baseline does not contain the expected value, the launcher aborts without changing the PAK index.

## xmlMerge

Adds or replaces specific nodes inside a localization sheet. The package only needs to ship the custom nodes rather than the game's complete localization XML.

## replaceEntry

Replaces one existing PAK entry with a mod-owned data file such as a custom PNG atlas.

## Future mods

As long as a future mod can be represented using these operations, it works with the same launcher without changing the launcher executable.

If a future mod requires a new transformation type, add a new operation type to the launcher while keeping existing package formats backwards compatible.
