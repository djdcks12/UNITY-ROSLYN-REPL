# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added (Phase 1 — MVP REPL)
- `RoslynRepl.Editor.Core.ReplEngine`: Roslyn-based one-shot compile + execute pipeline (`CSharpCompilation` → `Emit` → `Assembly.Load` → invoke)
- `ReplCodeWrapper`: wraps user statements in a generated class/method with line-offset tracking so compiler diagnostics map back to user lines
- `ReplLogCapture`: thread-safe `Application.logMessageReceivedThreaded` capture for the duration of a single Execute() call
- `AssemblyReferenceCache` (`[InitializeOnLoad]`): lazy `MetadataReference` cache; auto-invalidates on `AssemblyLoad` so newly compiled user scripts are picked up
- `ReplOptions`: pluggable list of injected `using` namespaces (defaults: System, Linq, UnityEngine, UnityEditor)
- `ReplResult` / `ReplResultKind` / `LogEntry` / `DiagnosticInfo`: result model surfaced to the UI layer
- Window: code editor (UI Toolkit `TextField` multiline) + output panel (`ScrollView` of color-coded `Label`s) split vertically via `TwoPaneSplitView`
- Toolbar: ▶ Run, Clear, duration display, output summary
- Keyboard shortcuts: F5 and Ctrl+Enter to run
- Code input persists across domain reloads via `SessionState`

### Fixed (Phase 1 — PR review feedback)
- Background Play Mode logs (server callbacks, ad SDK, gameplay updates) emitted during compile / emit / load no longer leak into REPL output. `ReplLogCapture` now starts only around the `MethodInfo.Invoke` window, and `ReplEngine` tags each captured log with `LogEntry.FromSnippet` based on whether the generated `__ReplScript` class appears in the stack trace; the UI shows only snippet-originated logs.
- F5 / Ctrl+Enter no longer triggers Run multiple times after a CreateGUI rebuild. The window now unregisters the `KeyDownEvent` handler before re-registering, uses a named `OnPlayModeChanged` method (so subscriptions can be removed), and unsubscribes `EditorApplication.playModeStateChanged` in `OnDisable`.
- Snippets that don't return a value no longer display a synthetic `=> null`. The output panel checks `ReplResult.HasReturnValue` and skips the result line when the wrapper's fallback `return null;` was used.

### Phase 1 limitations (deferred to later phases)
- No variable persistence between runs (each Execute() is isolated) — Phase 4
- No async/await support — Phase 6
- No timeout/cancellation (infinite loops will hang the Editor) — Phase 6
- Result formatting is `ToString()` only; tree view comes in Phase 2

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
