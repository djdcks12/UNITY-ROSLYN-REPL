# Roslyn REPL for Unity

[English](README.md) | [한국어](README_kr.md)

Run C# inside the Unity Editor, inspect live objects, keep useful probes, and temporarily patch methods while you debug.

Roslyn REPL for Unity is an editor-only toolkit for developers who want a faster feedback loop than creating throwaway editor scripts or sprinkling temporary logs through gameplay code. Open one window, run a snippet, inspect the result, save what is useful, and move on.

## Install

### Requirements

- Unity 2022.3 or newer is recommended.
- The package is editor-only and is designed to stay out of Player builds.

### OpenUPM

Install with the OpenUPM CLI:

```powershell
openupm add com.youngchan.roslyn-repl
```

Or add OpenUPM as a scoped registry in Unity:

```text
Name:   OpenUPM
URL:    https://package.openupm.com
Scope:  com.youngchan
```

Then add the package by name:

```json
{
  "dependencies": {
    "com.youngchan.roslyn-repl": "0.7.1"
  }
}
```

Package page:

```text
https://openupm.com/packages/com.youngchan.roslyn-repl/
```

### Git URL

Use the Git URL if you prefer not to add a scoped registry:

```json
"com.youngchan.roslyn-repl": "https://github.com/djdcks12/UNITY-ROSLYN-REPL.git#v0.7.1"
```

### From Disk

1. Open Unity Package Manager.
2. Click `+`.
3. Choose `Add package from disk...`.
4. Select this package's `package.json`.

You can also place the package directly at:

```text
Packages/com.youngchan.roslyn-repl/
```

## First Setup

Roslyn and Harmony are shipped under `Editor/Plugins/` as editor-only plugins. After importing the package, run:

```text
Tools / Roslyn REPL / Verify Setup
```

`Verify Setup` checks that the compiler and patching assemblies are available and reports duplicate-assembly situations. If the dependencies are missing or were removed, run:

```text
Tools / Roslyn REPL / Install Roslyn DLLs
```

## Why Use It?

- Run C# snippets directly in the Unity Editor.
- Inspect scene objects, ScriptableObjects, dictionaries, lists, and custom classes as expandable trees.
- Browse live scene objects, loaded assets, and common singleton patterns.
- Keep reusable snippets and run history per project.
- Add project-specific `using` directives once and reuse them across snippets.
- Use `_` as the previous result for quick follow-up expressions.
- Watch expressions after each run, similar to a lightweight debugger watch panel.
- Temporarily replace a method body at runtime with a Harmony-powered patch.
- Keep the tool editor-only, with no Player-build dependency.

## Quick Start

1. Open `Tools / Roslyn REPL / Open`.
2. Type a snippet.
3. Press **Run**, `F5`, or `Ctrl+Enter`.
4. Inspect logs and returned values in Output.
5. Save useful probes through **Snippets**.
6. Add repeated checks through **Watch**.

```csharp
return UnityEngine.Application.unityVersion;
```

```csharp
var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

return scene.GetRootGameObjects()
    .Select(go => new
    {
        go.name,
        active = go.activeInHierarchy,
        childCount = go.transform.childCount
    })
    .ToArray();
```

```csharp
Debug.Log("Probe started");
return UnityEngine.Object.FindObjectsByType<UnityEngine.Camera>(
    UnityEngine.FindObjectsSortMode.None);
```

## Features

### Interactive C# REPL

The main window gives you a multiline C# editor with line numbers, caret position, keyboard shortcuts, compile diagnostics, runtime exceptions, captured logs, and execution timing.

In Play Mode, snippets run one frame later through a tiny coroutine so the call lands in the same Player Update phase as a normal `Button.onClick`. This helps UI, popup, canvas, and scroll-view code behave the same from the REPL as it does from an in-game button. You can turn this off from `Tools / Roslyn REPL / Run on Player Frame` when you need immediate evaluation.

Snippets run on the Unity Editor main thread, so normal editor and Unity APIs are available:

```csharp
var selected = UnityEditor.Selection.activeObject;
return selected != null ? selected.name : "Nothing selected";
```

Use `return` when you want Output to render a value. Log-only snippets are supported too:

```csharp
Debug.Log("No return value needed");
```

### Rich Output Tree

Returned values are rendered as an expandable tree instead of a flat `ToString()` dump.

The renderer handles:

- primitive values,
- strings and enums,
- Unity objects,
- plain C# objects,
- fields (instance + private + inherited),
- arrays and lists,
- dictionaries,
- nested object graphs,
- destroyed Unity objects.

Each row shows name, type, and preview value, so large results stay readable.

The tree walks fields only by default. Property getters can run user-defined code (lazy init, IO, logging, state mutation) and inspecting a value shouldn't change project state behind your back. If you want property values in the tree, turn on `Tools / Roslyn REPL / Output: Include Property Getters` and re-Run — Watch follows the same fields-only default and is not affected by the toggle.

### Object Browser

The Object Browser helps you find objects without writing search code first.

Browse:

- scene `MonoBehaviour` instances,
- loaded `ScriptableObject` instances,
- singleton-like objects,
- mixed `All` results.

Double-click an entry in Output mode to generate an inspection snippet. Switch the lower pane to Patches mode and double-click an entry to pick a method from that object's runtime type.

The browser caps the first page at 200 results and debounces search input so large projects remain responsive. When more results exist, **Load more** appears next to the count.

### Snippets, History, and Usings

Save useful probes and reload them later. Snippets and run history are project-scoped, so one Unity project does not inherit another project's debugging helpers.

Import starter snippets from:

```text
Tools / Roslyn REPL / Import Default Snippets
```

Add project namespaces once from **Usings** and write shorter snippets afterward:

```text
MyGame.Runtime
MyGame.EditorTools
System.Text.RegularExpressions
```

The REPL combines built-in usings with your custom list on every run.

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

Watch rows can show scalar values or expandable trees. The evaluator can resolve expressions through normal compilation, the previous result `_`, or a global object-search fallback. Rows show their source so you know where a value came from.

Watch compiles are cached. Each row's wrapped source is compiled once on first sight and the resulting `MethodInfo` is reused on later refreshes. The cache invalidates automatically when assemblies load, so project edits are not masked by stale watch code. The interactive editor still compiles fresh on every Run.

### Runtime Method Patching

Runtime Method Patch lets you temporarily replace a `void` instance method while the Editor is running.

Use it to:

- add logging without editing the source file,
- test a small behavior change in Play Mode,
- bypass a branch while investigating,
- prototype a fix before copying it into the real `.cs` file.

Open it from:

```text
Tools / Roslyn REPL / Patch Method…
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

Once a body is pulled and edited, the Patches view shows a live diff between the original source and the current edit. **Copy diff** copies a unified diff. **Apply to file** writes the current patch body back into the target method's `.cs` file. A timestamped backup is saved under `<project>/Library/RoslynRepl/Backups/` before the write so you can restore by hand if the edit needs to go back; that folder is Unity-ignored (no Project window noise, no accidental commits), and Unity may delete it on a Library reimport — copy a backup out if you need it long-term.

Patches can be reverted individually or all at once. Active patches are remembered per project and re-applied after domain reload when possible. Disable automatic reapply from:

```text
Tools / Roslyn REPL / Auto-reapply Patches on Reload
```

When auto-reapply is off, patches are still remembered but stay dormant on reload. Re-enable the setting to install every dormant patch immediately, or click **Apply** per row.

Supported patch targets:

- instance methods,
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

## Editor-Only Build Footprint

The package is designed not to affect Player build output:

- all scripts live under `Editor/`,
- the assembly definition is limited to the `Editor` platform,
- Roslyn and Harmony plugin importers are enabled for `Editor` and disabled for `Any Platform`,
- there are no runtime assemblies, `Resources`, `StreamingAssets`, build preprocessors, or postprocessors.

Runtime Method Patch changes the current Editor process through Harmony. Those detours do not enter a Player build. The one exception is **Apply to file**: it intentionally edits your actual `.cs` source file, and that source change will be compiled into future builds just like any manual code edit.

## Data and Cleanup

The following data is stored locally per Unity project:

- snippets,
- run history,
- custom usings,
- watch expressions,
- runtime method patch specs.

Storage uses Unity `EditorPrefs` with a project discriminator based on the project path. Data is local to your machine and is not committed to source control.

Clear REPL data for the current project from:

```text
Tools / Roslyn REPL / Reset Project Data
```

Reset clears snippets, history, watches, custom usings, previous result `_`, visible Output, stored runtime patch specs, and the in-memory compiled-watch cache. Active Harmony patches are reverted as part of reset.

### Memory and Domain Reload

Each Run, Watch refresh, and Apply Patch loads a small dynamic assembly that cannot be unloaded until the script domain reloads. The toolbar shows an assembly-count indicator when dynamic assemblies are loaded. Click it to open a confirm dialog, or use:

```text
Tools / Roslyn REPL / Force Domain Reload
```

Script recompiles and Play Mode transitions also reload the domain in typical Unity settings, so the count usually takes care of itself.

## Safety Notes

This tool executes editor code in your Unity project. Treat snippets and patches like editor scripts:

- they can read and mutate scene state,
- they can call project APIs,
- they can create assets,
- they can trigger side effects through methods and property getters,
- long blocking loops can freeze the Editor if they never return.

### Cooperative Cancel Only

Snippets run synchronously on the Unity Editor main thread. The timeout only takes effect when your code observes the cancellation token `ct`.

```csharp
while (some_condition)
{
    ct.ThrowIfCancellationRequested();
    DoWork();
}
```

Code that does not check `ct`, including `while (true) {}` or any blocking call that never returns, can freeze the Unity Editor and may require force-quitting the process. The first Run on a workstation shows this warning as a confirm dialog, and the Code header keeps a persistent reminder visible afterward.

Prefer small probes, use `ct` in long loops, and revert runtime patches when you are done testing.

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

### Watch Shows An Unexpected Source

Watch rows can fall back to previous result `_` or global object search when normal compilation fails. Check the row's source label and qualify the expression with an owner name when needed.

### A Runtime Patch Does Not Apply

Run `Verify Setup` and check that Harmony is present. Then confirm the target method is an instance `void` method and the parameter type list matches the method signature.

### Duplicate Microsoft.CodeAnalysis Assemblies

Another package probably includes Roslyn. Use `Verify Setup` to identify every Roslyn assembly origin, then keep one compatible Editor-enabled set active.

## Menus

| Menu | Action |
|---|---|
| `Tools / Roslyn REPL / Open` | Open the main REPL window. |
| `Tools / Roslyn REPL / Patch Method…` | Open the REPL window in patching mode. |
| `Tools / Roslyn REPL / Import Default Snippets` | Add built-in starter snippets. |
| `Tools / Roslyn REPL / Verify Setup` | Check compiler and patch dependencies. |
| `Tools / Roslyn REPL / Install Roslyn DLLs` | Install or repair Roslyn and Harmony dependencies. |
| `Tools / Roslyn REPL / Reset Project Data` | Clear this project's REPL data and revert active patches. |
| `Tools / Roslyn REPL / Auto-reapply Patches on Reload` | Toggle whether active patch specs reinstall after domain reload. |
| `Tools / Roslyn REPL / Run on Player Frame` | Toggle one-frame Play Mode marshaling for Run. |
| `Tools / Roslyn REPL / Force Domain Reload` | Reload the script domain to unload dynamic REPL assemblies. |

## License

MIT for this package.

Installed Roslyn and Harmony DLLs are MIT-licensed by their authors. See `Editor/Plugins/Roslyn/THIRD_PARTY_NOTICES.md` and `Editor/Plugins/Harmony/THIRD_PARTY_NOTICES.md`.
