# Roslyn REPL for Unity

An editor-only C# REPL window for Unity, powered by Roslyn.

Use this package as a practical Unity Editor console for C# investigation. Run snippets, inspect returned objects, keep reusable probes, track watch expressions, manage custom `using` directives, and browse common runtime/editor objects without adding runtime dependencies to the game.

## Feature Highlights

- Roslyn-backed C# snippet execution from an EditorWindow.
- Multiline code editor with line numbers, caret position, keyboard shortcuts, and compile-error gutter markers.
- Captured `Debug.Log` output from the executed snippet.
- Structured result rendering for objects, collections, dictionaries, Unity objects, and primitive values.
- Object Browser for scene `MonoBehaviour` instances, loaded `ScriptableObject` instances, and singleton discovery.
- Project-scoped snippet library with built-in default snippets.
- Project-scoped run history.
- Project-scoped custom `using` directives.
- Watch panel for repeatedly evaluating expressions after each run.
- Previous-result carry-over through `_`.
- Cooperative cancellation through `ct`.
- **Runtime Method Patch (Phase A MVP)** — redirect a void instance method to a runtime-compiled body, no source `.cs` edit, revertable. Powered by Harmony.
- Setup verification and one-click Roslyn + Harmony installer.

The package is editor-only and is designed for debugging, investigation, tool building, and quick one-off probes while working inside Unity.

## Requirements

- Unity 2022.3 or newer.
- Windows, macOS, or Linux Unity Editor.
- Internet access the first time you install the bundled Roslyn DLLs, unless you provide the DLLs manually.

The package does not require a runtime dependency in your game assemblies.

## Installation

### Option A: OpenUPM (recommended once published)

Once the package is registered on OpenUPM, install it through the Unity Package Manager:

1. `Edit / Project Settings / Package Manager`.
2. Add a Scoped Registry:
    - **Name:** `OpenUPM`
    - **URL:** `https://package.openupm.com`
    - **Scopes:** `com.roslyn-repl`
3. Open `Window / Package Manager`, switch the source dropdown to `My Registries`, find **Unity Roslyn REPL**, and click `Install`.

The Package Manager UI will then show update notifications when a new version is published.

> Status: the package metadata and the OpenUPM descriptor (`Documentation~/openupm/com.roslyn-repl.yml`) are ready; the registry submission itself is still pending. Until then, use Option B, C, or D below.

### Option B: Git URL (no OpenUPM required)

Add a single line to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.roslyn-repl": "https://github.com/djdcks12/UNITY-ROSLYN-REPL.git"
  }
}
```

Pin to a specific tag if you don't want bleeding-edge `main`:

```json
"com.roslyn-repl": "https://github.com/djdcks12/UNITY-ROSLYN-REPL.git#v0.7.0"
```

### Option C: Put The Package In The Project

Copy this package to:

```text
Packages/com.roslyn-repl/
```

Unity will detect the package through `package.json`.

### Option D: Add From Disk

1. Open Unity Package Manager.
2. Click `+`.
3. Choose `Add package from disk...`.
4. Select this package's `package.json`.

### Install Roslyn DLLs

After the package is present in Unity, install the Roslyn compiler DLLs once:

1. Open `Tools / Roslyn REPL / Install Roslyn DLLs`.
2. Wait for the installer to download Roslyn 4.8.0 assemblies into `Editor/Plugins/Roslyn/`.
3. Run `Tools / Roslyn REPL / Verify Setup`.

You can also run the script manually:

```powershell
Tools~/install-roslyn.ps1
```

The installer keeps compiler assemblies inside the package so the REPL can compile snippets without adding NuGetForUnity or project-wide package dependencies.

## Quick Start

1. Open `Tools / Roslyn REPL / Open`.
2. Type:

```csharp
return UnityEngine.Application.unityVersion;
```

3. Press `Run`, `F5`, or `Ctrl+Enter`.
4. Read the result in the Output panel.
5. Open `Tools / Roslyn REPL / Import Default Snippets` to seed the snippet library.
6. Click `Snippets` in the toolbar and load one of the built-in examples.

For a log-only probe:

```csharp
Debug.Log("Hello from the REPL");
```

For a scene probe:

```csharp
return UnityEngine.Object.FindObjectsByType<UnityEngine.Camera>(
    UnityEngine.FindObjectsSortMode.None);
```

For a multi-line investigation:

```csharp
var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
var roots = scene.GetRootGameObjects();

return roots.Select(go => new
{
    go.name,
    childCount = go.transform.childCount
}).ToArray();
```

## Window Guide

Open the window from:

```text
Tools / Roslyn REPL / Open
```

The window is split into three main areas:

- Left: Object Browser.
- Center top: Code editor.
- Center bottom: Output tree and Watch panel.

### Toolbar

- `Run`: executes the current editor contents.
- `Clear`: clears the editor and output view.
- `Snippets`: opens the snippet library popup.
- `History`: opens the run history popup.
- `Usings`: opens the custom usings popup.
- `Verify Setup`: checks Roslyn assembly availability and common conflict conditions.
- Duration label: shows the last execution time.
- Mode badge: shows whether the Editor is in Edit Mode or Play Mode.
- Version label: shows the package version.

### Code Editor

The editor supports multiline snippets. It shows:

- line numbers,
- current caret line and column,
- compile-error markers,
- compile-error tooltips,
- persisted text while the Unity editor session stays alive.

Shortcuts:

- `F5`: run the current snippet.
- `Ctrl+Enter`: run the current snippet.

### Output

The Output panel shows:

- captured snippet logs,
- compile diagnostics,
- runtime exceptions,
- cancellation warnings,
- scalar return values,
- expandable result trees for complex objects.

Object results are rendered with three columns:

- `Name`
- `Type`
- `Value`

If a snippet only logs and does not return a value, the UI does not show a fake `=> null` result.

## Writing Snippets

Snippets are wrapped into a generated C# class and method before compilation. You can write normal C# statements:

```csharp
var selected = UnityEditor.Selection.activeObject;
return selected != null ? selected.name : "Nothing selected";
```

Use `return` when you want a value rendered in the Output panel:

```csharp
return UnityEngine.Time.frameCount;
```

If no `return` statement is used, the snippet still runs, but the synthetic fallback result is hidden.

### Available Helpers

Two helper values are available inside snippets:

- `_`: previous successful non-null result.
- `ct`: cancellation token for cooperative cancellation.

Example using `_`:

```csharp
return 41;
```

Then run:

```csharp
return _ + 1;
```

`_` is exposed as `dynamic`, so common REPL-style follow-up expressions work without a cast. It is updated only by successful returned values. Compile failures, runtime failures, cancellations, log-only snippets, null returns, and watch evaluations do not overwrite it.

Example using `ct`:

```csharp
var count = 0;
while (!ct.IsCancellationRequested)
{
    count++;
    if (count > 100000)
        break;
}

return count;
```

Snippet execution uses cooperative timeout handling. Code that never returns and never checks `ct` can still block the Editor.

### Execution Context

Snippets run on the Unity Editor main thread, so Unity API calls work the same way they do in normal editor scripts. For long loops or repeated work, check `ct` so cancellation can keep the Editor responsive.

## Object Browser

The Object Browser helps you locate objects without writing discovery code first.

Categories:

- `All`: scene `MonoBehaviour` instances and loaded `ScriptableObject` instances.
- `MonoBehaviour`: scene `MonoBehaviour` instances only.
- `ScriptableObject`: loaded `ScriptableObject` instances only.
- `Singleton`: opt-in reflection scan for static singleton-like members.

Use the filter box to search by display name or type name. Press the refresh button to rescan.

Using the browser:

- Press refresh when you want a fresh scan.
- Select `Singleton` when you want to run the singleton scanner.
- Result lists are capped to keep the window responsive.
- Double-clicking a row renders that object in the Output tree.

Singleton discovery looks for static fields/properties that can expose an instance related to the scanned type, including common base/interface-typed singleton patterns. Property getters are handled conservatively so browsing does not accidentally invoke expensive user code.

## Snippet Library

Open with:

```text
Toolbar / Snippets
```

The snippet popup lets you:

- save the current editor contents as a named snippet,
- load a saved snippet,
- rename a snippet,
- delete a snippet,
- double-click a row to load it into the editor.

Snippets are stored per Unity project through project-scoped `EditorPrefs`.

### Default Snippets

Import built-in examples from:

```text
Tools / Roslyn REPL / Import Default Snippets
```

The import is non-destructive:

- snippets with new names are added,
- snippets with existing names are skipped,
- user-edited snippets are not overwritten.

Included default snippets cover:

- Unity version,
- editor time and frame information,
- active scene summary,
- root GameObjects,
- a singleton/object lookup starter,
- memory snapshot,
- current Unity selection,
- previous-result carry-over with `_`.

## Run History

Open with:

```text
Toolbar / History
```

The history popup records recent runs so you can bring back a previous probe quickly.

Behavior:

- Stores up to 50 entries.
- Stores successful runs, compile failures, runtime failures, and cancellations.
- Skips duplicate consecutive snippets.
- Refreshes while the history popup is open.
- Double-clicking a row loads that snippet back into the editor.
- `Clear` removes all saved history after confirmation.

History is project-scoped through `EditorPrefs`.

## Custom Usings

Open with:

```text
Toolbar / Usings
```

Default usings:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
```

The Usings popup lets you add project-specific namespaces. You may type either:

```csharp
MyGame.Runtime
```

or:

```csharp
using MyGame.Runtime;
```

The editor normalizes the entry, removes the leading `using`, removes the trailing semicolon, and skips duplicates.

Custom usings are read fresh for every Run and Watch evaluation, so newly added namespaces are available immediately.

Custom usings are stored per Unity project through project-scoped `EditorPrefs`, not globally across every Unity project on the same machine.

## Watch Panel

The Watch panel is the lower-right area of the main window.

Use it to track expressions while repeatedly running snippets:

```csharp
UnityEditor.Selection.activeObject
```

or:

```csharp
return UnityEngine.Time.frameCount;
```

Behavior:

- Press `Enter` or `+` to add a watch.
- Expressions without `return` are automatically wrapped as `return <expr>;`.
- Expressions starting with `return ` are used as written.
- Watches are reevaluated after every Run.
- Watches are reevaluated after add/remove.
- Changed previews briefly flash green.
- Failed watches show compile/runtime/cancel information.
- Watch evaluations do not overwrite `_`.

Watch expressions use a shorter timeout than normal runs so the panel remains responsive during normal use. Timeout is still cooperative.

### Resolution order

Each Watch is resolved in three steps. The first one that yields a value wins; if all three fail, the row renders as a `<compile error>` or `<runtime error>`.

1. **Normal C# evaluation.** The expression is wrapped as `return <expr>;` and compiled through the same Roslyn pipeline as the main editor. This handles full snippets like `Manager.Instance.Counter`, lambdas, anonymous types, and references that resolve through the Usings list.
2. **`_` carry-over path.** If step 1 fails, the expression is treated as a member path off the previous successful result. So after a snippet returning `new { value = 42 }`, a Watch like `value` resolves through the carry-over without any owner reference.
3. **Global instance pool.** If steps 1–2 both fail, the resolver walks live MonoBehaviour / ScriptableObject / Singleton instances (up to a 1000-entry cap) and returns the first owner whose path matches. The Watch row then shows a `↳ Resolved from: …` line so the picked owner is visible.

Step 3 is opt-out via the `Fallback` toggle in the Watch panel header — flipping it off makes a failing Watch stay failed instead of doing a best-effort search.

### Property getter policy

The REPL is conservative about *when* it invokes a user-defined property getter, since getters in the wild commonly lazy-init, log, mutate counters, or do IO. The rules:

| Surface | Calls user property getters? | Why |
|---|---|---|
| Snippet expression itself (compile success) | Yes | The user wrote `Manager.Counter`; calling the getter is the expressed intent. |
| Output panel result tree | Yes | One-shot, user-initiated inspection. |
| Watch result tree (expand a Watch row) | **No** | Tree refreshes after every Run; walking properties would amplify any side effect to N rows × every Run. |
| Watch fallback — `_` carry-over (`_.foo`) | Yes | Single, user-named owner; no ambiguity. |
| Watch fallback — owner-qualified (`GameManager.Config`) | Yes | The user explicitly named the type; calling that owner's property is consistent with intent. |
| Watch fallback — unqualified (`Count`, `IsReady`) | **No (fields-only)** | First-match wins; allowing properties would silently fire arbitrary code on whichever owner happens to come up first. Qualify the owner if a property is needed. |

## Result Rendering

Returned values are converted into a safe inspection tree where possible.

Supported result shapes include:

- primitive values,
- strings,
- enums,
- arrays,
- lists and other `IEnumerable` values,
- dictionaries,
- anonymous objects,
- normal managed objects,
- Unity objects,
- destroyed Unity object references.

The serializer applies depth and node caps so accidental large graphs do not overwhelm the Editor. Collection previews show the head of large sequences instead of expanding everything.

Unity objects need special care because some Unity-owned properties can trigger native assertions or throw after destruction. The serializer handles destroyed Unity fake-null objects and avoids unsafe Unity-engine property walks while still surfacing user-defined fields and safe user-defined properties.

## Runtime Method Patch (Phase A MVP)

The Patches tab in the main REPL window's lower pane (next to Output) lets you redirect a live method's calls to a runtime-compiled body without touching the source `.cs` file. Use it to add a `Debug.Log` mid-method, change a comparison, swap a return value, and iterate without losing the running scene state — then bake the change back into source when you're happy.

### Open

Either:

- `Tools / Roslyn REPL / Patch Method…` — opens the main window and flips the lower pane to **Patches**.
- Or click **Patches** on the lower pane's mode tab (next to **Output**) inside an already-open REPL window.

### Form

| Field | Example | Notes |
|---|---|---|
| **Target type** | `MyGame.Player` | Full type name including namespace. |
| **Method name** | `Damage` | Must be a `void` instance method. |
| **Parameter types** | `System.Int32,System.String` | Comma-joined full type names. Empty when the method has no parameters. |
| **Patch body** | (see below) | C# statements that replace the original method body. |

### Pull Original — start from the existing implementation

Click **Pull Original** in the Patch body header to copy the target method's current source into the editor. Use it as the starting point for an in-place edit (add a `Debug.Log`, change a comparison, swap a return path, …) instead of writing from scratch.

What it does:

1. Resolves the target method via the same path Apply uses (Target type / Method name / Parameter types), so any Phase A scope error (non-void, static, …) shows up here too.
2. Walks the project's `MonoScript`s for one whose declared class matches the target type. Partial classes are tried in order until the matching method node is found.
3. Roslyn-parses the candidate file, locates the matching method by identifier + parameter count (with type-name disambiguation for overloads), and extracts the text between the outer braces.
4. Replaces the editor body with that text, preserving original indentation. If the editor already has non-default content, an overwrite confirm dialog runs first.

Limitations (mostly mirror Phase A's scope):

| Try to pull | Result |
|---|---|
| `void` instance method, block body, source in `Assets/` or `Packages/` | ✅ pulled |
| `=> expr` (expression-bodied) | ❌ "Phase C MVP only extracts block-bodied methods" |
| Method in a precompiled DLL (no MonoScript) | ❌ "No MonoScript found for X" |
| Auto-generated partial (Unity codegen) | ⚠️ may pull but the body is rarely user-editable |
| Same name + same arity overloads | best-effort match by parameter type names; falls back to the first candidate |

After Phase D lands, you'll be able to leave the pulled body essentially as-is — the rewriter will translate `hp -= amount;` into `__set("hp", __get<int>("hp") - amount);` automatically. Until then, private member access still uses the helper functions described below.

### Helpers available inside a patch body

| Symbol | What it does |
|---|---|
| `__instance` | The target instance, typed as the declaring type. Use for `public` member access. |
| `__get<T>("name")` | Read a `private` (or public) field/property. |
| `__set("name", value)` | Write a `private` (or public) field/property. |
| `__call<T>("name", args…)` | Invoke a `private` (or public) method. Use `__call<object>` for `void` methods. |
| Method parameters | Same names as the original (e.g. `amount`). |

### Example — log inside `Player.Damage`

```csharp
// Target type:     MyGame.Player
// Method name:     Damage
// Parameter types: System.Int32
// Patch body:
var hp = __get<int>("hp");
UnityEngine.Debug.Log($"[patched] before damage: hp={hp}, amount={amount}");
__set("hp", hp - amount);
UnityEngine.Debug.Log($"[patched] after damage:  hp={__get<int>(\"hp\")}");
```

### Lifecycle

- **Apply Patch** — compiles the body, installs a Harmony Prefix, and skips the original method body on every subsequent call.
- **Revert** — removes the patch matching the current form. The Active patches list also has a per-row Revert.
- **Revert All** — drops every active patch.
- The Active patches list shows the status dot (green = active, red = failed, grey = inactive). **Load** repopulates the form from a saved spec; if you re-Apply, the engine reverts the previous patch and installs the new one in one step.

### Phase A scope (intentional)

| Try to patch | Result |
|---|---|
| `void` instance method | ✅ supported |
| Non-`void` method (`int Calculate()`, `string GetName()`) | ❌ rejected with `"Phase A MVP only patches void instance methods"` |
| `static` method | ❌ rejected with `"Phase A MVP only patches *instance* methods"` |
| `ref` / `out` / `in` parameters | ❌ not supported |
| Generic method | ❌ not supported |
| Constructor / static constructor | ❌ not supported |
| Property getter / setter | ❌ not supported |

Source-style editing (no `__get`/`__set` boilerplate) and `.cs` export will land in later phases of the Runtime Method Patch work (originally tracked as [issue #14](https://github.com/djdcks12/UNITY-ROSLYN-REPL/issues/14)).

### Persistence and auto-reapply

Patches are persisted to `EditorPrefs` per project, the same way snippets / history / watches / custom usings are. That means:

- **Active patches survive domain reloads.** Editor restart, script recompile, or Play Mode toggle: every patch you'd Apply'd is automatically re-installed one editor frame after the reload finishes. A summary line lands in the Console (`[Roslyn REPL] Runtime patches: N re-applied, M failed.`).
- **Failed re-applies don't retry on every boot.** If the target type / method renamed, the body no longer compiles, or anything else goes wrong during the re-install, the spec is flipped to `Failed` with `LastError` set to `"Auto-reapply failed: …"` and persisted in that state. The next boot loads it but doesn't try again — you have to re-Apply explicitly from the UI.
- **Inactive and Failed drafts persist too.** The Patches panel remembers what you authored even if you Revert'd or last-applied'd into a failure. Use `Load` from the active list to bring an old spec back into the form.
- **The Patches list is project-scoped.** Two projects on the same machine never share patch sets — same `ProjectScopedPrefs` hash all the other persistence stores use.

To wipe everything: `Tools / Roslyn REPL / Reset Project Data` reverts every active Harmony detour and clears the persisted spec list (alongside snippets / history / watches / usings / `_` / Output panels).

### Dependencies

The Patch feature needs `0Harmony.dll`. The bundled `Tools / Roslyn REPL / Install Roslyn DLLs` menu installs it alongside Roslyn — one click covers both. The first time you import the package, a setup prompt offers to install both dependencies; pick **Install Now** and you're done.

`Tools / Roslyn REPL / Verify Setup` reports the live Harmony state (`[optional] Harmony (Runtime Method Patch): present` once installed).

## Persistence Model

The following data is stored per Unity project:

- snippets,
- run history,
- custom usings,
- watch expressions,
- runtime method patches (Phase B onward — auto-reapplied on domain reload).

The storage uses `EditorPrefs` with a project discriminator based on the project path. This keeps one project's `MyGame.Runtime` using or snippets from leaking into another Unity project.

Because this is `EditorPrefs` storage:

- the data is local to the current machine/user,
- it is not committed to source control,
- moving the project to a different path can create a fresh project-scoped bucket.

To wipe everything for the current project (snippets, run history, watches, custom usings, runtime method patches, and the in-memory `_` carry-over) in one click, use `Tools / Roslyn REPL / Reset Project Data`. The menu reverts every active Harmony detour, deletes the persisted patch list along with the other four stores, reports counts before and after, and never touches data for other Unity projects on the same machine.

What `Reset Project Data` does and doesn't do:

| Action | What it touches |
|---|---|
| **Clears** | Saved snippets for *this* project |
| **Clears** | Run history for this project |
| **Clears** | Watch expressions for this project |
| **Clears** | Custom Usings for this project |
| **Clears** | The in-memory `_` carry-over |
| **Clears** | The Output panel of any open REPL window (logs, summary, duration label, gutter error markers) |
| **Clears** | Active runtime method patches (reverts every Harmony detour) and the persisted patch list |
| **Does NOT touch** | Unity scenes, prefabs, or assets |
| **Does NOT touch** | Package files (`Packages/com.roslyn-repl/...` or installed Roslyn DLLs) |
| **Does NOT touch** | REPL data for *other* Unity projects on the same machine |
| **Does NOT touch** | Already-loaded dynamic REPL assemblies (those clear on the next domain reload — see Verify Setup for the live count) |

## Security and Data Handling

The REPL is a power tool. Read this section before pasting things into snippets, watches, or method patches that you wouldn't comfortably paste into a plain text file on disk.

### Plain-text storage

The following data is written to `EditorPrefs` as plain text:

- snippets,
- run history,
- watch expressions,
- custom usings,
- runtime method patch bodies (Phase B onward — `__get<T>("hp")` style helper calls are part of the body and end up here verbatim).

Storage location:

- on Windows, that's `HKEY_CURRENT_USER\Software\Unity Technologies\Unity Editor 5.x` in the registry;
- on macOS / Linux, the corresponding `~/Library/Preferences/...` plist or `~/.config/unity3d/...` files.

Anyone with read access to the current OS user can read the values. Tokens, server URLs, account ids, and any string the user types into a snippet, a watch, or a patch body end up there until cleared. Treat the storage like a `.bash_history`, not like a secrets vault.

If something sensitive ends up in the data, run `Tools / Roslyn REPL / Reset Project Data` — it removes every `EditorPrefs` key the package owns (snippets, run history, watches, custom usings, runtime method patch list), reverts every active Harmony detour, and resets the in-memory `_` carry-over.

### Arbitrary editor code execution

Snippets compile and run as Editor C#. They can:

- read and mutate any in-memory game state during Play Mode,
- modify scenes, prefabs, and assets through `AssetDatabase`,
- call out to the file system, network, and reflection,
- import / delete Unity packages.

This is intentional — the REPL exists to do these things — but it means **only run snippets you trust**. Pasting a snippet from chat or the web carries the same risk as running an unaudited Editor script.

### Watch side-effects

Watch expressions re-evaluate after every Run. A watch like `MyManager.SpawnNextEnemy()` runs the call **once per Run, every Run**, until removed. Watches that mutate state, allocate, log, or talk to the network compound those costs across the whole session. From Phase 10 the Watch tree no longer walks property getters of the returned object (so a passive `Manager.Counter` watch is safe), but the *expression itself* still runs whatever the user wrote.

### Runtime method patches outlive the session

Method patches are persisted across domain reloads and Editor restarts (Phase B). That has two implications:

- The patch body sits in `EditorPrefs` until you Revert (which keeps the draft) or use Reset Project Data (which deletes it). Same plain-text caveats as snippets — don't paste secrets in there.
- Active patches re-install themselves on every Editor launch and Play Mode toggle. A `Player.Damage` redirect you applied last week will still be redirecting this week unless you reverted or reset it. Check the Patches list (or `Tools / Roslyn REPL / Verify Setup`) if you can't reproduce a bug from a clean source repo — a stale patch from earlier debugging may still be live.

### Editor hang from non-cooperative loops

Soft cancellation only stops snippets that observe `ct`. A snippet like `while (true) { }` or `for (long i = 0; i < long.MaxValue; i++) { }` with no `ct.ThrowIfCancellationRequested()` call will hang the Editor main thread until the OS kills the process. There is no hard kill — `Thread.Abort` is unavailable on Mono / .NET 6+. When in doubt, write loops as:

```csharp
for (long i = 0; i < N; i++)
{
    if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
    // ...
}
```

## Menus

Main menus:

```text
Tools / Roslyn REPL / Open
Tools / Roslyn REPL / Import Default Snippets
Tools / Roslyn REPL / Patch Method…
Tools / Roslyn REPL / Reset Project Data
Tools / Roslyn REPL / Verify Setup
Tools / Roslyn REPL / Install Roslyn DLLs
```

`Install Roslyn DLLs` installs Harmony alongside Roslyn — both dependencies in one click.

`Patch Method…` opens the main REPL window and flips its lower pane to the **Patches** tab. See "Runtime Method Patch" above.

Use `Reset Project Data` when:

- a project's snippet/history/watch list contains stale or sensitive entries,
- you're decommissioning a project and want to leave nothing behind in `EditorPrefs`,
- a teammate is taking over the machine.

The menu shows counts before deletion and asks for confirmation. Other projects on the same machine are unaffected.

Use `Verify Setup` when:

- the REPL window cannot compile,
- Unity reports duplicate `Microsoft.CodeAnalysis` assemblies,
- another package also ships Roslyn DLLs,
- the package has just been installed on a new machine.

## Roslyn DLLs And Conflict Resolution

The REPL expects Roslyn assemblies under:

```text
Editor/Plugins/Roslyn/
```

If Unity reports duplicate compiler assemblies, another package may also be shipping `Microsoft.CodeAnalysis` DLLs. Common sources include NuGetForUnity, analyzer packages, or another editor tooling package.

Recommended flow:

1. Run `Tools / Roslyn REPL / Verify Setup`.
2. Read the reported Roslyn assembly origins.
3. Keep one Editor-compatible Roslyn set active.
4. Disable duplicate Plugin Importer entries for Editor, or remove the duplicate package if it is not needed.

Do not disable random DLLs blindly. Unity's assembly loading rules are global within the Editor, so duplicate compiler assemblies can cause confusing compile failures.

## Project Behavior

- Unity 2022.3+ is the supported target.
- The tool is editor-only.
- Snippets run on the Editor main thread.
- Play Mode is supported. Treat snippets as live editor commands because they can read or mutate active game state.
- Logs from the generated snippet are captured; unrelated background Play Mode logs are filtered out when possible.
- The package is designed to avoid runtime player builds.

## Troubleshooting

### The REPL Window Opens But Compilation Fails

Run:

```text
Tools / Roslyn REPL / Verify Setup
```

If Roslyn DLLs are missing, run:

```text
Tools / Roslyn REPL / Install Roslyn DLLs
```

### Unity Reports Duplicate Microsoft.CodeAnalysis Assemblies

Another package probably includes Roslyn. Use `Verify Setup` to identify origins, then keep one Editor-compatible Roslyn copy active.

### My Custom Namespace Is Not Found

Open `Usings`, add the namespace without the `using` keyword, and run again.

Example:

```text
MyGame.Runtime
```

If the namespace still fails, confirm the assembly containing that namespace is compiled for the Editor and referenced by the current project.

### The Editor Freezes During A Snippet

The snippet may be doing long-running work on the main thread. Add cancellation checks:

```csharp
while (!ct.IsCancellationRequested)
{
    // work
}
```

Avoid blocking waits, unbounded loops, and heavy reflection scans in snippets.

### A Watch Expression Keeps Failing

Open the Watch panel tooltip or output message and check whether it is a compile error, runtime exception, or timeout. Watches are normal snippets under the hood, so the same namespace and main-thread rules apply.

### Object Browser Does Not Show A Singleton

Switch the category to `Singleton` and press refresh. The `All` category avoids singleton scanning by design.

If the singleton still does not appear, check that the instance is loaded and exposed through a static field/property pattern that the browser can inspect safely.

### Default Snippets Did Not Overwrite My Edits

That is expected. Default snippet import is non-destructive. Delete or rename the existing snippet first if you want to re-import the built-in version.

## License

MIT for this package. Installed Roslyn DLLs are MIT-licensed by Microsoft; see `Editor/Plugins/Roslyn/THIRD_PARTY_NOTICES.md` after installation.
