# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added (Phase 0 — package skeleton)
- UPM package layout (`package.json`, asmdef, README, CHANGELOG, LICENSE)
- Empty UI Toolkit `EditorWindow` (`Tools / Roslyn REPL / Open`)
- `Tools / Roslyn REPL / Verify Setup` diagnostic with origin classification (BundledByUs / UnityShipped / NuGetForUnity / OtherPackage / External / Unknown)
- `AssemblyResolutionGuard` for detecting duplicate Roslyn copies (Unity 6.5 readiness)
- `Tools / Roslyn REPL / Install Roslyn DLLs` (and `Tools~/install-roslyn.ps1`) to fetch Roslyn 4.8.0 binaries from NuGet
- Bundled Roslyn DLLs with stable GUIDs and Editor-only Plugin Importer settings (`validateReferences: 1`)

### Verified
- Compiles cleanly on Unity 6 (6000.0.65f1) with zero console errors
- UI Toolkit window mounts UXML/USS, shows version, mode badge, status, and Verify button
- ValidateReferences correctly defers to a host project's existing Roslyn (e.g. NuGetForUnity-installed copy) when versions match — bundled DLLs serve as fallback for projects that have none
