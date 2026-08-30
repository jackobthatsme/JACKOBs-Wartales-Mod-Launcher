<img width="1058" height="752" alt="JACKOB's Wartales Mod Launcher" src="https://github.com/user-attachments/assets/d922939a-0ce8-4039-b49d-6cc0227a4721" />

# JACKOB's Wartales Mod Launcher

Universal offline mod manager for compatible Wartales mod packages.

## Download

### [⬇ Download Latest Release](https://github.com/jackobthatsme/JACKOBs-Wartales-Mod-Launcher/releases/latest)

Download the Windows `.exe` from the release assets.

## v0.3.0 goals

Launcher v0.3.0 extends the existing package model beyond `res.pak`. A single mod can contain any number of operations and can modify both PAK entries and ordinary files in the Wartales directory.

Supported operations:

- `cdbPatch`
- `xmlMerge`
- `replaceEntry`
- `externalBinaryDelta`
- `externalXmlMerge`
- `externalReplaceFile`

External targets are package-defined relative paths. The launcher does not hardcode mod names or specific external filenames.

## Safety model

- Captures clean baselines for launcher-managed PAK entries and external files
- Rebuilds results as baseline → active mods in install order
- Uninstalling one mod rebuilds from baseline plus the remaining mods
- `Restore Vanilla` restores all launcher-managed baselines, not only `res.pak`
- Validates all external target paths and prevents writes outside the selected Wartales directory
- Binary deltas require a supported baseline SHA-256, expected bytes for every hunk, and a declared resulting SHA-256
- Unexpected game-file changes abort instead of being overwritten
- Multi-file commits use snapshots and rollback attempts to avoid leaving a partially applied mod set

See [PACKAGE_FORMAT.md](PACKAGE_FORMAT.md) for the package schema and operation details.

## Features

- Install compatible mod packages
- Update installed mods
- Uninstall individual mods through deterministic rebuilds
- Verify launcher-managed files
- Restore launcher-managed vanilla baselines
- Support multiple compatible mods
- Fully offline
- No PowerShell
- No external downloads
- No registry changes

## Installation

1. Download the latest launcher from Releases.
2. Run `JACKOBsWartalesModLauncher.exe`.
3. Make sure the correct Wartales folder is detected.
4. Click **Install Mod** and select a compatible mod ZIP.

You can also drag and drop a compatible mod ZIP into the launcher window.

## Requirements

- Windows 64-bit
- Wartales

Release builds are published as self-contained Windows executables; a separate .NET installation is not required for the published launcher.

## Package compatibility

The original `JACKOB_WARTALES_MOD_V1` format remains supported. Existing packages that use only `cdbPatch`, `xmlMerge`, and `replaceEntry` remain compatible.

Packages using v0.3.0 external-file operations should declare:

```json
"minimumLauncherVersion": "0.3.0"
```

## Important

Only install mod packages made for this launcher. Mods can still conflict when they attempt incompatible changes to the same data or bytes; compatibility checks are designed to stop those cases safely.

After a Wartales update, a mod that relies on exact baseline hashes or bytes may need an update before it can be applied again.

## Build

GitHub Actions builds the launcher on Windows and publishes a self-contained `win-x64` artifact for pull requests and pushes to `main`.

## Source Code

The full C#/.NET source code is available in this repository.

## Feedback

Bug reports and feedback are welcome.

**JACKOBTHATSME**
