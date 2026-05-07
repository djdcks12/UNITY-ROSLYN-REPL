# Roslyn REPL

Editor-only C# REPL for Unity. Compile and execute arbitrary C# code at runtime, inspect any MonoBehaviour / ScriptableObject / singleton in memory, and watch expressions live — all from a UI Toolkit window inside the Editor.

Powered by Roslyn (`Microsoft.CodeAnalysis.CSharp`). Zero project dependencies. Drop the package into any Unity 6 project and it works.

## Install

Local UPM package. Either:

- Place this folder under `Packages/com.roslyn-repl/` of any Unity 6+ project, or
- Add via Package Manager → "Add package from disk" → select `package.json`

After import, run **Tools / Roslyn REPL / Install Roslyn DLLs** once (or run `Tools~/install-roslyn.ps1` manually) to download the bundled Roslyn binaries from NuGet into `Editor/Plugins/Roslyn/`. Then **Tools / Roslyn REPL / Open** to launch the window.

## Quick start

1. **Tools / Roslyn REPL / Open** — opens the window. The first time you open it, the editor is pre-filled with a one-line snippet that returns the Unity version. Press **F5** to run it.
2. **Tools / Roslyn REPL / Import Default Snippets** — copies an 8-entry starter library (Unity version, scene info, time, memory snapshot, current Selection, …) into your project. They land in the **Snippets…** popup and are normal snippets from there on — edit, rename, delete freely.
3. **Snippets… → Save current editor contents as…** — give your active code a name. From now on, the **Snippets…** popup holds it for one-click reload.

## Window guide

```
┌─────────────────────────────────────────────────────────────────┐
│ ▶ Run   Clear     Roslyn REPL    [duration]  EDIT  vX.Y  Verify Setup │
│                                                  Snippets… History… Usings… │
├──────────────┬──────────────────────────────────────────────────┤
│              │ Code                                F5 / Ctrl+Enter │
│              │ ┌─┬─────────────────────────────────────────────┐ │
│   Browser    │ │1│ // gutter shows line numbers + error markers│ │
│              │ │2│ return Manager.Instance.SomeValue;          │ │
│  (Phase 3)   │ ├─┴─────────────────────────────────────────────┤ │
│              │ │ Ln 1, Col 1                                   │ │
│              ├──────────────────────────────────────────────────┤
│              │ Output                                       OK │
│              │ => 42                                            │
│              │ [tree-view of complex results]                   │
│              ├──────────────────────────────────────────────────┤
│              │ Watch                       [+] expression…  + │
│              │ Manager.Count    42                int       ✕ │
│              │ Time.frameCount  120391            int       ✕ │
└──────────────┴──────────────────────────────────────────────────┘
```

### Toolbar buttons

| Button | What it does |
|---|---|
| **▶ Run** | Compile + execute the editor contents. F5 / Ctrl+Enter do the same. |
| **Clear** | Wipe the Output panel. Doesn't touch history or carry-over. |
| **Snippets…** | Save / load / rename / delete named snippets. Per-project. |
| **History…** | Last 50 runs (success or failure). Double-click to reload. |
| **Usings…** | Edit the `using` namespaces injected at the top of every snippet. |
| **Verify Setup** | Diagnose Roslyn assembly resolution (bundled vs Unity-shipped vs conflicting). |

### Side panels

- **Browser** (left) — discover live `MonoBehaviour`, `ScriptableObject`, and singleton instances by category + search. Double-click renders the instance into Output, equivalent to typing `return X;`.
- **Watch** (lower right) — type an expression like `Manager.Instance.Count`, hit Enter. The panel re-evaluates every entry after each Run; rows whose value changed flash green for 1.5s.

## Inside snippets

The wrapper exposes a couple of accessors snippets can use unqualified:

- **`_`** — previous successful non-null result. Operators bind at runtime against the actual value (`return _ + 1;` after `return 41;` → 42). User locals like `int _ = 5;` shadow it cleanly inside their own scope.
- **`ct`** — `CancellationToken` for the current snippet. Long loops should call `ct.ThrowIfCancellationRequested()` so the soft 5-second timeout (configurable via `ReplOptions.TimeoutMs`) can interrupt them.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| **F5** | Run |
| **Ctrl + Enter** | Run |
| **Enter** (in Snippets save / Watch add field) | Submit |
| **Esc** (in Rename modal) | Cancel |

## Menus

- `Tools / Roslyn REPL / Open` — open the REPL window
- `Tools / Roslyn REPL / Import Default Snippets` — add the 8 starter snippets to your project
- `Tools / Roslyn REPL / Verify Setup` — diagnose Roslyn assembly resolution (shows whether bundled, Unity-shipped, or conflicting)
- `Tools / Roslyn REPL / Install Roslyn DLLs` — re-fetch the bundled Roslyn 4.8.0 binaries from NuGet

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
- **Phase 6 — Watch panel + soft cancellation**: in-window Watch panel below Output that auto-re-evaluates user expressions after every Run, with a green flash on rows whose preview changed; `ReplOptions.TimeoutMs` (default 5000ms) wires `CancellationTokenSource.CancelAfter`, and snippets observe the token via the new wrapper-class `ct` accessor (`for(...) { ct.ThrowIfCancellationRequested(); }`). Cancellation surfaces as a distinct `ReplResultKind.Cancelled` so the UI can render it as a warning instead of a runtime error. *`async / await` inside snippets is intentionally not shipped — see "Known limitations" below.*
- **Phase 7 — Distribution (in this release)**: bundled `DefaultSnippets` library — 8 starter snippets one menu click away; expanded README with a quick-start, window guide, keyboard shortcuts, and troubleshooting.

Open:

- **OpenUPM registry submission**. The package metadata is in shape; submission is a separate yaml PR against the OpenUPM repo plus a tagged GitHub release. Held until distribution is the priority.

## Known limitations

- **No inline C# syntax highlighting in the code editor.** UI Toolkit's `TextField` renders its content with a single foreground color and offers no per-token color API; the only options are (a) lose editability by replacing the input with a `RichText`-rendered `Label` overlay, or (b) maintain a parallel non-editable highlight surface and keep it in sync with the live `TextField` on every keystroke. Both add substantial complexity for what amounts to a quality-of-life nicety in a snippet REPL — Roslyn diagnostics are already surfaced in the gutter (Phase 4) and in the output panel — so true highlighting stays out for now. If a future Unity release exposes a richer text-input element, this can be revisited.
- **No `async` / `await` inside snippets.** The naive implementation (wrapper signature → `Task<object>`, `GetAwaiter().GetResult()` on the Editor main thread) deadlocks on every realistic await: most Unity continuations post back to the same main thread the engine is blocking, so the continuation never runs. Doing it correctly would require pumping `Execute` on `EditorApplication.update` with the entire UI rebuilt around per-frame callbacks (different return contract, different result rendering, different Watch integration). The cost is far above the value for a snippet REPL whose 95% case is synchronous inspection — `return Manager.Instance.Count;`, `return list.Where(...).Sum();`, etc. If a snippet really needs to observe an async result, run the work on `Task.Run` and surface a sentinel via `Debug.Log` or the `_` carry-over.
- **No hard kill of a synchronous snippet.** Phase 6 ships soft cancellation: snippets can call `ct.ThrowIfCancellationRequested()` inside long loops, and `ReplOptions.TimeoutMs` (default 5000ms) automatically cancels the token. But code that *doesn't* check `ct` — most notably `while (true) { … }` with no `ct` access — still hangs the Editor main thread until process kill. `Thread.Abort` is unavailable on Mono / .NET 6+ and the snippet runs on the same thread the engine is invoked from, so there's no safe way to forcibly stop a non-cooperative loop. Treat the timeout as a courtesy, not a guarantee, and write Run-style snippets with `ct` awareness when you don't trust the body.

## Troubleshooting

- **"The window is empty / shows a fallback message about UXML."** Run `Tools / Roslyn REPL / Verify Setup` and check that the package was imported correctly. The window loads its UXML / USS from `Packages/com.roslyn-repl/Editor/UI/Layouts/`; if those paths don't resolve, the package didn't import as expected.
- **"My snippet says CS0103 but it works elsewhere."** The snippet runs in a fresh class with a fixed default `using` set (`System`, `System.Collections.Generic`, `System.Linq`, `UnityEngine`, `UnityEditor`). Add anything else through **Usings…**, or qualify the type fully (`MyGame.Manager.Whatever`).
- **"`return _ + 1;` worked yesterday and now CS-something."** `_` carries the previous *successful, non-null* result. After a Compile error, Cancelled run, or `Debug.Log`-only snippet, `_` keeps its earlier value — but if the editor reloaded its assemblies (entered Play Mode and back, or recompiled scripts), `_` resets to `null`. Run a value-returning snippet to repopulate.
- **"Editor froze when I ran a `while(true)` snippet."** The soft cancellation in Phase 6 needs the snippet to cooperate — it observes `ct.ThrowIfCancellationRequested()` inside the loop. Code that doesn't check `ct` runs on the Editor main thread with no available hard kill (see "Known limitations" below); kill the Editor process if it gets there. Future versions of the loop should use `for (long i = 0; i < N; i++) { if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested(); … }`.
- **"Watch panel shows `<compile error>` for one row."** Each watch compiles independently and surfaces its own diagnostic in the row tooltip. Hover the value cell for the message; fix the expression in the add field and re-add.
- **"After installing another package the REPL stops compiling."** Most likely a duplicate Roslyn assembly conflict — e.g. NuGetForUnity also shipped `Microsoft.CodeAnalysis.dll`. Run **Verify Setup** to see which copies are loaded; uncheck Editor in the Plugin Importer for the duplicate you don't want.

## License

MIT for this package. Bundled Roslyn DLLs are MIT-licensed by Microsoft (see `Editor/Plugins/Roslyn/THIRD_PARTY_NOTICES.md` after install).
