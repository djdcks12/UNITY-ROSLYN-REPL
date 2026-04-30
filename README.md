# Roslyn REPL

Editor-only C# REPL for Unity. Compile and execute arbitrary C# code at runtime, inspect any MonoBehaviour / ScriptableObject / singleton in memory, and watch expressions live — all from a UI Toolkit window inside the Editor.

Powered by Roslyn (`Microsoft.CodeAnalysis.CSharp`). Zero project dependencies. Drop the package into any Unity 6 project and it works.

## Install

Local UPM package. Either:

- Place this folder under `Packages/com.roslyn-repl/` of any Unity 6+ project, or
- Add via Package Manager → "Add package from disk" → select `package.json`

After import, run **Tools / Roslyn REPL / Install Roslyn DLLs** once (or run `Tools~/install-roslyn.ps1` manually) to download the bundled Roslyn binaries from NuGet into `Editor/Plugins/Roslyn/`. Then **Tools / Roslyn REPL / Open** to launch the window.

## Usage

- `Tools / Roslyn REPL / Open` — open the REPL window
- `Tools / Roslyn REPL / Verify Setup` — diagnose Roslyn assembly resolution (shows whether bundled, Unity-shipped, or conflicting)

## Compatibility

- Unity 2022.3+ (recommended Unity 6 / 6000.x)
- Editor-only (excluded from builds)
- Mono BleedingEdge runtime (Editor default)

## Conflict resolution

If another package (e.g. `com.ivanmurzak.unity.mcp`, `com.unity.code-analysis`, or a future Unity 6.5 built-in Roslyn) already ships `Microsoft.CodeAnalysis.dll`, Unity's `Validate References` will surface a duplicate-assembly error. Resolve by disabling one of the duplicate Plugin Importers (Inspector → uncheck Editor for the copy you want excluded). Run `Tools / Roslyn REPL / Verify Setup` to see exactly which copies are loaded and from where.

## Roadmap

Planned phases (deferred from the current release):

- **Phase 4 — UX polish**: C# syntax highlighting in the code editor, line numbers + caret position indicator, compile-error inline markers (red squiggles + gutter), UI for editing `ReplOptions.Usings`.
- **Phase 5 — Persistence + variable continuity**: snippet library (save / load named snippets), run history, `_` variable carrying the previous result between runs, persisted Roslyn options.
- **Phase 6 — Watch panel + async**: live-re-evaluating watch expressions with change highlight, `async` / `await` support in snippet bodies, soft timeout / cancellation.
- **Phase 7 — Distribution**: `Samples~` default snippet library, README polish + screenshots, OpenUPM submission.

## License

MIT for this package. Bundled Roslyn DLLs are MIT-licensed by Microsoft (see `Editor/Plugins/Roslyn/THIRD_PARTY_NOTICES.md` after install).
