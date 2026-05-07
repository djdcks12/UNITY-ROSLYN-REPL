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
- Setup verification and Roslyn DLL installer menu items.

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
3. Open `Window / Package Manager`, switch the source dropdown to `My Registries`, find **Roslyn REPL**, and click `Install`.

The Package Manager UI will then show update notifications when a new version is published.

> Status: the package metadata and the OpenUPM descriptor (`Documentation~/openupm/com.roslyn-repl.yml`) are ready; the registry submission itself is still pending. Until then, use Option B, C, or D below.

### Option B: Git URL (no OpenUPM required)

Add a single line to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.roslyn-repl": "https://github.com/djdcks12/ROSLYN-REPL.git"
  }
}
```

Pin to a specific tag if you don't want bleeding-edge `main`:

```json
"com.roslyn-repl": "https://github.com/djdcks12/ROSLYN-REPL.git#v0.7.0"
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

## Persistence Model

The following data is stored per Unity project:

- snippets,
- run history,
- custom usings,
- watch expressions.

The storage uses `EditorPrefs` with a project discriminator based on the project path. This keeps one project's `MyGame.Runtime` using or snippets from leaking into another Unity project.

Because this is `EditorPrefs` storage:

- the data is local to the current machine/user,
- it is not committed to source control,
- moving the project to a different path can create a fresh project-scoped bucket.

## Menus

Main menus:

```text
Tools / Roslyn REPL / Open
Tools / Roslyn REPL / Import Default Snippets
Tools / Roslyn REPL / Verify Setup
Tools / Roslyn REPL / Install Roslyn DLLs
```

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
