# Roslyn REPL for Unity

Run C# inside the Unity Editor, inspect live objects, save useful probes, and patch methods temporarily while you debug.

Roslyn REPL for Unity is an editor-only toolkit for developers who want a faster feedback loop than creating throwaway editor scripts or sprinkling temporary logs through gameplay code. Open one window, run a snippet, inspect the result, keep the useful parts, and move on.

## Installation

### Unity Version

Unity 2022.3 or newer is recommended.

The package is editor-only. It is designed to stay out of player builds.

### Install From Git URL

Add the package to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.roslyn-repl": "https://github.com/djdcks12/UNITY-ROSLYN-REPL.git"
  }
}
```

To pin a version:

```json
"com.roslyn-repl": "https://github.com/djdcks12/UNITY-ROSLYN-REPL.git#v0.7.0"
```

### Install From Disk

1. Open Unity Package Manager.
2. Click `+`.
3. Choose `Add package from disk...`.
4. Select this package's `package.json`.

You can also place the package directly at:

```text
Packages/com.roslyn-repl/
```

### OpenUPM

The package includes OpenUPM metadata. Once the package is published there, install it through a scoped registry:

```text
Name:   OpenUPM
URL:    https://package.openupm.com
Scope:  com.roslyn-repl
```

Until then, use the Git URL or disk install flow.

## First Setup

After the package is installed, install the compiler dependencies once:

```text
Tools / Roslyn REPL / Install Roslyn DLLs
```

This installs Roslyn and Harmony into the package's editor plugin folder.

Then run:

```text
Tools / Roslyn REPL / Verify Setup
```

`Verify Setup` checks that the compiler assemblies are available and reports common duplicate-assembly situations.

## Why Use It?

- Run C# snippets directly in the Unity Editor.
- Inspect scene objects, ScriptableObjects, dictionaries, lists, and custom classes as expandable trees.
- Keep reusable snippets and run history per project.
- Watch expressions after each run, similar to a lightweight debugger watch panel.
- Browse live scene objects, loaded assets, and common singleton patterns.
- Add custom `using` directives once and reuse them across snippets.
- Use `_` as the previous result for quick follow-up queries.
- Temporarily replace a method body at runtime with a patch powered by Harmony.
- Keep everything editor-only, with no player-build dependency.

## Quick Look

```csharp
return UnityEngine.Application.unityVersion;
```

```csharp
var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

return scene.GetRootGameObjects()
    .Select(go => new
    {
        go.name,
        childCount = go.transform.childCount
    })
    .ToArray();
```

```csharp
Debug.Log("Probe started");
return UnityEngine.Object.FindObjectsByType<UnityEngine.Camera>(
    UnityEngine.FindObjectsSortMode.None);
```

Run snippets with the **Run** button, `F5`, or `Ctrl+Enter`.

## Features

### Interactive C# REPL

The REPL window gives you a multiline C# editor with line numbers, caret position, keyboard shortcuts, compile diagnostics, runtime exceptions, captured logs, and execution timing.

Snippets run on the Unity Editor main thread, so normal editor and Unity APIs are available:

```csharp
var selected = UnityEditor.Selection.activeObject;
return selected != null ? selected.name : "Nothing selected";
```

Use `return` when you want the Output panel to render a value. Log-only snippets are supported too:

```csharp
Debug.Log("No return value needed");
```

### Rich Output Tree

Returned values are rendered as an expandable tree instead of a flat `ToString()` dump.

The renderer handles:

- primitive values,
- strings,
- enums,
- Unity objects,
- plain C# objects,
- fields and safe properties,
- arrays and lists,
- dictionaries,
- nested object graphs,
- destroyed Unity objects.

Each row shows name, type, and preview value, so large results stay readable.

### Object Browser

The Object Browser helps you find objects without writing search code first.

Browse:

- scene `MonoBehaviour` instances,
- loaded `ScriptableObject` instances,
- singleton-like objects,
- mixed `All` results.

Double-click an entry in Output mode to generate an inspection snippet. Switch the lower pane to Patches mode and double-click an entry to pick a method from that object's runtime type.

The list shows up to 200 results by default and the search field waits a moment after you stop typing before re-scanning, so the panel stays responsive on big projects. When the cap is reached, a **Load more** button appears next to the result count for the times you really want the full list.

### Snippet Library

Save useful probes and reload them later. Snippets are stored per project, so one Unity project does not inherit another project's debugging helpers.

Typical snippet ideas:

- inspect the active scene,
- list loaded managers,
- dump selected object data,
- scan ScriptableObject assets,
- check current Play Mode state,
- run project-specific diagnostics.

Import the built-in starter snippets from:

```text
Tools / Roslyn REPL / Import Default Snippets
```

Default snippet import is non-destructive. Your edited snippets are not overwritten.

### Run History

Every executed snippet can be reopened from History. This is useful when you are iterating quickly and want to recover the exact probe that produced a result.

History is project-scoped and local to your machine.

### Custom Usings

Add project namespaces once and write shorter snippets afterward.

Open **Usings** and add entries without the `using` keyword:

```text
MyGame.Runtime
MyGame.EditorTools
System.Text.RegularExpressions
```

The REPL combines built-in usings with your project-specific custom list when compiling snippets.

### Previous Result With `_`

The previous successful non-null returned value is available as `_`.

```csharp
return 41;
```

Then:

```csharp
return _ + 1;
```

`_` is exposed as `dynamic`, so common follow-up expressions work without casts. Failed runs, cancelled runs, log-only snippets, `null` returns, and watch refreshes do not overwrite it.

### Watch Panel

The Watch panel evaluates expressions after each run.

Use it for values you repeatedly care about:

```csharp
Time.frameCount
Selection.activeObject
GameManager.Instance.CurrentState
```

Watch rows can show scalar values or expandable trees. The panel can resolve expressions from normal compilation, the previous result, or the global object search fallback. The row tells you when a fallback result was used so you know where the value came from.

Watch compiles are cached. Each row's wrapped source is compiled once on first sight and the resulting `MethodInfo` is reused on every subsequent refresh, so N watches × M user runs cost N compiles + N×M invokes rather than N×M compiles. The cache is per-row (different expression text or different effective usings ⇒ separate entry) and invalidates automatically on any package install or user-script recompile, so live edits aren't masked by stale assemblies. The interactive editor doesn't share this cache — every Run there stays a fresh compile against the latest editor state.

### Runtime Method Patching

Runtime Method Patch lets you temporarily replace a void instance method while the Editor is running.

Use it to:

- add logging without editing the source file,
- test a small behavior change in Play Mode,
- bypass a branch while investigating,
- prototype a fix before copying it into the real `.cs` file.

Open it from:

```text
Tools / Roslyn REPL / Patch Method
```

Pick a target type and method, write the replacement body, then click **Apply Patch**.

Example patch body:

```csharp
Debug.Log($"Damage called: amount={amount}");

hp -= amount;

if (hp <= 0)
{
    Die();
}
```

The patch body can look like normal source code. Private fields, private methods, private static members, explicit `this` access, `nameof(member)`, compound assignments, and explicit generic method calls are routed through generated reflection helpers when direct access is not available.

You can also click **Pull Original** to load the current source body of the target method, edit it in place, and run the edited version temporarily. Picking a method through **Browse** or by double-clicking an Object Browser row in Patches mode auto-pulls the source so the editor lands on a body you can immediately edit.

Once a body is pulled and edited, the Patches view shows a live diff between the original source and the current edit (`+` / `-` lines, `+N -M` summary). Two actions sit under the diff:

- **Copy diff** writes a unified-diff text blob to the system clipboard, ready to paste into a code review or IDE diff tool.
- **Apply to file** writes the current patch body back into the target method's `.cs` file. A `.bak` sibling is saved before the write, and the body written is the user-edited form — the auto-rewrite that routes inaccessible names through reflection helpers is wrapper-only and never touches `spec.PatchBody`, so the on-disk source stays clean (`hp -= 10`, never `__set("hp", __get<int>("hp") - 10)`).

Patches can be reverted individually or all at once. Active patches are remembered per project and re-applied after domain reload when possible.

When patches are active, the toolbar shows a small `🔧 N active` indicator so it's clear that the running behavior differs from the source files. Click it to open the Patches view.

Automatic reapply on reload can be turned off from:

```text
Tools / Roslyn REPL / Auto-reapply Patches on Reload
```

When it is off, patches are still remembered, but they don't reinstall on reload. Open the Patches view and click Apply on the rows you want to re-enable, or turn the setting back on to install every dormant patch right away.

## Basic Usage

1. Open `Tools / Roslyn REPL / Open`.
2. Type a snippet.
3. Press **Run**, `F5`, or `Ctrl+Enter`.
4. Inspect logs and return values in Output.
5. Save useful probes through **Snippets**.
6. Add repeated checks through **Watch**.

## Window Layout

The main window has three working areas:

- **Object Browser** on the left.
- **Code editor** at the top.
- **Output / Patches / Watch** tools at the bottom.

The toolbar includes Run, Clear, Snippets, History, Usings, setup verification, execution duration, Play Mode state, and package version.

## Snippet Examples

### Current Selection

```csharp
var obj = UnityEditor.Selection.activeObject;

return obj == null
    ? "Nothing selected"
    : new
    {
        obj.name,
        type = obj.GetType().FullName
    };
```

### Scene Root Summary

```csharp
var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

return scene.GetRootGameObjects()
    .Select(go => new
    {
        go.name,
        active = go.activeInHierarchy,
        children = go.transform.childCount
    })
    .ToArray();
```

### Find Loaded ScriptableObjects

```csharp
return UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.ScriptableObject>()
    .Where(x => !string.IsNullOrEmpty(x.name))
    .Select(x => new
    {
        x.name,
        type = x.GetType().FullName
    })
    .OrderBy(x => x.type)
    .ToArray();
```

### Cooperative Long-Running Probe

```csharp
var count = 0;

while (!ct.IsCancellationRequested)
{
    count++;

    if (count >= 100000)
        break;
}

return count;
```

## Runtime Patch Workflow

1. Open `Tools / Roslyn REPL / Patch Method`.
2. Enter a target type, method name, and parameter type list, or use **Browse**.
3. Click **Pull Original** if you want to start from the existing source body.
4. Edit the body.
5. Click **Apply Patch**.
6. Test the behavior in Edit Mode or Play Mode.
7. Click **Revert** when finished.

Supported target shape:

- instance method,
- `void` return type,
- normal value parameters,
- non-constructor,
- non-property-accessor.

Patch bodies can use the original method parameter names. The generated wrapper also provides helper functions for explicit reflection access when you want full control:

| Helper | Purpose |
|---|---|
| `__instance` | Target instance, typed as the declaring type. |
| `__get<T>("name")` | Read a field or property from the target instance. |
| `__set("name", value)` | Write a field or property on the target instance. |
| `__call<T>("name", args...)` | Invoke a method on the target instance. |
| `__getOn<T>(target, "name")` | Read from another object. |
| `__setOn(target, "name", value)` | Write to another object. |
| `__callOn<T>(target, "name", args...)` | Invoke a method on another object. |
| `__getStatic<T>(typeof(X), "name")` | Read a static field or property. |
| `__setStatic(typeof(X), "name", value)` | Write a static field or property. |
| `__callStatic<T>(typeof(X), "name", args...)` | Invoke a static method. |

Most patch bodies do not need these helpers because natural source-style access is rewritten automatically when needed.

## Persistence

The following data is stored locally per Unity project:

- snippets,
- run history,
- custom usings,
- watch expressions,
- runtime method patch specs.

Storage uses Unity `EditorPrefs` with a project discriminator based on the project path. Data is local to the current machine and is not committed to source control.

To clear REPL data for the current project:

```text
Tools / Roslyn REPL / Reset Project Data
```

Reset clears snippets, history, watches, custom usings, previous result `_`, visible Output, stored runtime patch specs, and the in-memory compiled-watch cache. Active Harmony patches are reverted as part of the reset.

### Memory and Domain Reload

Each Run, Watch refresh, and Apply Patch loads a small dynamic assembly that can't be unloaded until the script domain reloads. The toolbar shows a `💾 N asm` indicator when any are loaded. The pill turns yellow as the count climbs and red when it's high enough that you'd notice the memory in a profiler. Click it to open a confirm dialog, or use:

```text
Tools / Roslyn REPL / Force Domain Reload
```

The reload also runs every time you recompile a script or toggle Play Mode, so most of the time the count takes care of itself.

## Safety Notes

This tool executes editor code in your Unity project. Treat snippets and patches like editor scripts:

- they can read and mutate scene state,
- they can call project APIs,
- they can create assets,
- they can trigger side effects through methods and property getters,
- long blocking loops can freeze the Editor if they never return.

### Cooperative Cancel Only

Snippets run synchronously on the Unity Editor's main thread. The 5-second timeout and any external Cancel button only take effect when your snippet observes the cancellation token `ct` — for example:

```csharp
while (some_condition)
{
    ct.ThrowIfCancellationRequested();
    DoWork();
}
```

Code that does not check `ct` — including `while (true) {}`, infinite loops without a bound, or any blocking call that never returns — will freeze the Unity Editor and may require force-quitting the process. There is no hard-kill mechanism for non-cooperative code; both `Thread.Abort` and isolated worker domains are unavailable on Unity's runtime.

The first time you Run a snippet on a workstation the REPL surfaces this caveat as a confirm dialog. After you acknowledge, a persistent yellow banner under the Code header keeps the reminder visible.

Prefer small probes, use `ct` in long loops, and revert runtime patches when you are done testing.

## Dependency Notes

The package installs Roslyn compiler assemblies and Harmony under `Editor/Plugins/`.

If your project already ships Roslyn through another package, Unity may report duplicate assemblies. Run:

```text
Tools / Roslyn REPL / Verify Setup
```

Keep one Editor-compatible Roslyn set active. Avoid disabling random DLLs without checking their importer settings and origin first.

## Troubleshooting

### Compilation Fails Immediately

Run `Tools / Roslyn REPL / Verify Setup`.

If Roslyn is missing, run `Tools / Roslyn REPL / Install Roslyn DLLs`.

### A Namespace Is Missing

Open **Usings** and add the namespace without the `using` keyword:

```text
MyGame.Runtime
```

Also confirm the assembly that defines the namespace is compiled for the Editor.

### The Editor Becomes Unresponsive

The snippet or patch may be doing blocking work on the main thread. Stop the Editor if needed, then add a cancellation check or a clear loop bound before running again.

```csharp
while (!ct.IsCancellationRequested)
{
    // work
}
```

### Watch Shows An Unexpected Source

Watch rows can fall back to previous result `_` or global object search when normal compilation fails. Check the row's source label and qualify the expression with an owner name when needed.

### A Runtime Patch Does Not Apply

Run `Verify Setup` and check that Harmony is present. Then confirm the target method is an instance `void` method and the parameter type list matches the method signature.

### Duplicate Microsoft.CodeAnalysis Assemblies

Another package probably includes Roslyn. Use `Verify Setup` to identify every Roslyn assembly origin, then keep only one compatible set enabled for the Editor.

## Menus

| Menu | Action |
|---|---|
| `Tools / Roslyn REPL / Open` | Open the main REPL window. |
| `Tools / Roslyn REPL / Patch Method` | Open the REPL window in patching mode. |
| `Tools / Roslyn REPL / Import Default Snippets` | Add built-in starter snippets. |
| `Tools / Roslyn REPL / Install Roslyn DLLs` | Install Roslyn and Harmony dependencies. |
| `Tools / Roslyn REPL / Verify Setup` | Check compiler and patch dependencies. |
| `Tools / Roslyn REPL / Reset Project Data` | Clear this project's REPL data and revert active patches. |

## License

MIT for this package.

Installed Roslyn DLLs are MIT-licensed by Microsoft. See `Editor/Plugins/Roslyn/THIRD_PARTY_NOTICES.md` after installation.
