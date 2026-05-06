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

Shipped:

- **Phase 4 — UX polish**: line-number gutter + caret position indicator, compile-error gutter markers with hover tooltips, `Usings…` editor popup that persists user-added namespaces via `EditorPrefs`. *Inline syntax highlighting is intentionally not shipped — see "Known limitations" below.*
- **Phase 5 — Persistence + variable continuity**: `_` carries the previous successful non-null result into the next snippet (`return _ + 1;`); auto-saved run history (`History…`) — last 50 entries, double-click to reload; named snippet library (`Snippets…`) with save / load / rename / delete and overwrite confirmation. All persistence is project-scoped via a shared `ProjectScopedPrefs` helper so different Unity projects don't share state.
- **Phase 6 — Watch panel + soft cancellation (in this release)**: in-window Watch panel below Output that auto-re-evaluates user expressions after every Run, with a green flash on rows whose preview changed; `ReplOptions.TimeoutMs` (default 5000ms) wires `CancellationTokenSource.CancelAfter`, and snippets observe the token via the new wrapper-class `ct` accessor (`for(...) { ct.ThrowIfCancellationRequested(); }`). Cancellation surfaces as a distinct `ReplResultKind.Cancelled` so the UI can render it as a warning instead of a runtime error. *`async / await` inside snippets is intentionally not shipped — see "Known limitations" below.*

Planned (deferred):

- **Phase 7 — Distribution**: `Samples~` default snippet library, README polish + screenshots, OpenUPM submission.

## Known limitations

- **No inline C# syntax highlighting in the code editor.** UI Toolkit's `TextField` renders its content with a single foreground color and offers no per-token color API; the only options are (a) lose editability by replacing the input with a `RichText`-rendered `Label` overlay, or (b) maintain a parallel non-editable highlight surface and keep it in sync with the live `TextField` on every keystroke. Both add substantial complexity for what amounts to a quality-of-life nicety in a snippet REPL — Roslyn diagnostics are already surfaced in the gutter (Phase 4) and in the output panel — so true highlighting stays out for now. If a future Unity release exposes a richer text-input element, this can be revisited.
- **No `async` / `await` inside snippets.** The naive implementation (wrapper signature → `Task<object>`, `GetAwaiter().GetResult()` on the Editor main thread) deadlocks on every realistic await: most Unity continuations post back to the same main thread the engine is blocking, so the continuation never runs. Doing it correctly would require pumping `Execute` on `EditorApplication.update` with the entire UI rebuilt around per-frame callbacks (different return contract, different result rendering, different Watch integration). The cost is far above the value for a snippet REPL whose 95% case is synchronous inspection — `return Manager.Instance.Count;`, `return list.Where(...).Sum();`, etc. If a snippet really needs to observe an async result, run the work on `Task.Run` and surface a sentinel via `Debug.Log` or the `_` carry-over.
- **No hard kill of a synchronous snippet.** Phase 6 ships soft cancellation: snippets can call `ct.ThrowIfCancellationRequested()` inside long loops, and `ReplOptions.TimeoutMs` (default 5000ms) automatically cancels the token. But code that *doesn't* check `ct` — most notably `while (true) { … }` with no `ct` access — still hangs the Editor main thread until process kill. `Thread.Abort` is unavailable on Mono / .NET 6+ and the snippet runs on the same thread the engine is invoked from, so there's no safe way to forcibly stop a non-cooperative loop. Treat the timeout as a courtesy, not a guarantee, and write Run-style snippets with `ct` awareness when you don't trust the body.

## License

MIT for this package. Bundled Roslyn DLLs are MIT-licensed by Microsoft (see `Editor/Plugins/Roslyn/THIRD_PARTY_NOTICES.md` after install).
