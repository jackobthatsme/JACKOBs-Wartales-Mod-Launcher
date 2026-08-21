# JACKOB's Wartales Mod Launcher v0.2.2 GUI

Author: **JACKOBTHATSME**

Graphical Windows front-end for the already working JACKOB Wartales patch engine.

## What changed from v0.1.0

- normal Windows GUI instead of the numbered console menu
- original JACKOB THATSME block-character logo retained in the launcher
- Install mod button
- Uninstall selected button
- Verify files button
- Restore vanilla button
- Wartales folder selector
- installed-mod list with version/status
- drag-and-drop of compatible mod ZIP packages
- status bar and error dialogs
- same LocalAppData state format as v0.1.0, so existing launcher state is reused
- target changed to .NET 10 for the current Visual Studio 2026 setup

## Important: patch engine is unchanged in principle

The launcher still:

- works offline
- uses no BAT installer
- uses no PowerShell
- downloads nothing
- changes no registry values
- does not execute files from mod ZIPs
- does not require QuickBMS for the end user
- patches only the PAK entries requested by a mod package
- captures original PAK entry data before the first managed modification
- can rebuild managed entries after uninstalling one mod
- verifies hashes before/after managed operations

State and captured originals remain under:

`%LOCALAPPDATA%\JACKOBTHATSME\WartalesLauncher\`

## Opening in Visual Studio 2026

1. Extract this folder.
2. Open `JACKOBsWartalesModLauncher.csproj`.
3. If Visual Studio asks whether you trust the downloaded project, choose **Trust and continue**.
4. Use **Build > Rebuild Solution**.
5. Run with **Ctrl+F5**.

The project targets:

`net10.0-windows`

and uses Windows Forms. No NuGet packages are required.

## Existing v0.1.0 state

v0.2.2 intentionally keeps the same:

- settings location
- launcher state format
- mod package format

So if v0.1.0 already installed Mod 1 or Mod 2, the GUI should show the same managed mods after it opens the same Wartales folder.

## Mod package format

Current package format:

`JACKOB_WARTALES_MOD_V1`

Supported operations:

- `cdbPatch`
- `xmlMerge`
- `replaceEntry`

This is deliberately generic so future JACKOBTHATSME Wartales mods can use the same launcher without rebuilding the executable, as long as they can be represented by those operation types.

See `PACKAGE_FORMAT.md`.

## Validation status

Before this GUI conversion, the v0.1.0 engine was tested on the user's Windows Wartales installation with:

- Ain't Nobody Got Time for That
- Jack of Two Trades
- install working in-game
- managed-file verification working
- uninstall / restore returning to baseline

v0.2.2 reuses that engine but introduces a new graphical front-end. The GUI build itself should therefore be compiled and smoke-tested on Windows before a Nexus release.

Recommended smoke test:

1. Open GUI.
2. Confirm correct Wartales path.
3. Confirm any v0.1 managed mods are listed.
4. Verify files.
5. Install Mod 1.
6. Launch Wartales and verify Mod 1.
7. Install Mod 2.
8. Verify both mods.
9. Uninstall one mod and confirm the other remains.
10. Restore vanilla and confirm game launches normally.

## Nexus release note

For the eventual public release, prefer a normal framework-dependent publish folder in a standard ZIP first. Avoid obfuscation, embedded scripting, PowerShell, self-extracting archives, downloaders and unnecessary packers.


## v0.2.2 UI polish

- Smaller JACKOB THATSME header
- Dedicated Wartales Mod Launcher title
- Clear offline/security indicators
- Wartales and res.pak detection status
- Friendly empty-state panel when no mods are installed
- Polished button labels and spacing
- Cleaner bottom status bar
  # JACKOB's Wartales Mod Launcher

Offline launcher for installing, updating and managing compatible **JACKOBTHATSME Wartales mods**.

## Download

Get the latest version from **Releases**:

**JACKOB's Wartales Mod Launcher v0.2.3**

## Features

- Install compatible mod packages
- Uninstall managed mods
- Verify files
- Restore vanilla files
- Fully offline
- No PowerShell
- No downloads
- No registry changes

## Installation

1. Download the latest `.exe` from **Releases**.
2. Run the launcher.
3. Make sure your Wartales folder is detected.
4. Click **Install Mod** and select a compatible mod package.

You can also drag and drop a compatible mod ZIP directly into the launcher.

## Supported Mods

- **Ain't Nobody Got Time for That**
- **Jack of Two Trades**

More mods will use the same launcher in the future.

## Compatibility

Only use mod packages specifically made for this launcher.

Other mods modifying the same Wartales data may conflict.

Game updates may require updated mod packages.

## Source Code

The full C#/.NET source code is available in this repository.

## Feedback

Feedback and bug reports are always welcome. They help me improve the launcher and future mods.

— **JACKOBTHATSME**

The patching/backend logic is unchanged from the tested v0.2.0 build.


## v0.2.2 UI refresh
- JACKOBTHATSME ASCII branding is now one horizontal line instead of two stacked words.
- Keeps the polished empty-state, safety indicators, Wartales/res.pak detection and larger action buttons from v0.2.1.
