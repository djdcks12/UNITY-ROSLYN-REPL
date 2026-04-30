# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added (Phase 3 — instance browser side panel)
- New `RoslynRepl.Editor.Core.InstanceLocator` enumerates user-visible runtime instances by category (`MonoBehaviour`, `ScriptableObject`, `Singleton`, `All`) with substring filtering on type/display name. Hides Unity-shipped types, Editor framework objects, and the package's own assembly so the list stays focused on the user's project.
- `SingletonScanner` (`[InitializeOnLoad]`) reflects over user assemblies for static `Instance` properties / fields. Member discovery is cached for the domain lifetime (invalidated on `AssemblyLoad`); values are read fresh each call so destroyed singletons drop out.
- `RoslynRepl.Editor.UI.ObjectBrowserView` is a UI Toolkit side panel: title row + refresh button, `EnumField` category drop-down, `ToolbarSearchField`, and a virtualizing `ListView` of results. Each row shows display name (bold), short type name (italic teal), and a sub-label (scene / "ScriptableObject" / "Singleton"); inactive rows render dimmer.
- The window now lays out as `[toolbar] / [browser | code+output]` via an outer horizontal `TwoPaneSplitView` (browser fixed at 280px) wrapping the existing vertical split.
- Double-clicking (or Enter on) a browser row renders that instance into the output panel through `SimpleObjectSerializer.ToTree`, equivalent to typing `return X;` — but no code required.

### Fixed (Phase 3 — Play Mode safety, surfaced while browsing real managers)
- `SingletonScanner` now detects **any** self-returning public static member regardless of name (`Instance`, `it`, `I`, `Self`, `Current`, `Singleton`, etc.), so plain C# managers like `LobbyFriendManager.it` are surfaced. Member-type matching accepts both directions: the canonical exact / derived case (`public static Foo Instance` declared on `Foo`) is taken regardless of name, and the base / interface case (`public static IService Current => _instance;` on a class that implements `IService`, or `public static BaseThing Instance = new DerivedThing();` declared on the derived class) is taken when the name is in the standard singleton accessor set — guarding against false positives like `public static IDisposable s_dispose;` on unrelated classes. Compiler-generated closure / lambda-cache types (names starting with `<`) are filtered out so they don't flood the list.
- For plain C# *property* singletons whose getter we still won't invoke (lazy-init side effects), a sibling private static **backing field** of the same type is read directly when present — covering the canonical `private static T _instance; public static T it => _instance ??= new T();` pattern.
- `InstanceEntry.Object` (UnityEngine.Object) → `InstanceEntry.Value` (object). Plain C# instances now flow through the browser and into `ToTree` on double-click.
- `InstanceCategory.All` no longer includes the Singleton sweep — that path scans every loaded user assembly and is too heavy to run automatically. Users opt in by selecting the Singleton category.
- `ObjectBrowserView` no longer auto-`Refresh()`s on construction; the first scan happens only when the user changes category, types in search, or presses ↻. Avoids freezing the Editor on window open or domain reload.
- `SimpleObjectSerializer` hardened against runaway graphs:
  * `System.Delegate` (Action / Func / event) is treated as a leaf — walking the invocation list reaches every subscribed view and explodes the graph. Preview shows handler count and first target.
  * `BuildNode` now threads a `BuildState` (replacing the loose `Options` + `visited` pair) so a per-call **total node cap** (default 2000) can abort cleanly with a `(node cap reached)` leaf.
  * Default `MaxDepth` lowered 6 → 4.
- `ValueFormatter` renders delegate previews like `Action → ClassName.Handler` or `Action (3 targets, first: …)`.

### Added (Phase 2 — object tree view in output)
- `RoslynRepl.Editor.Core.ReplValueNode`: a row in the result tree (`Name`, `TypeName`, `Preview`, `IsExpandable`, `Children`)
- `SimpleObjectSerializer.ToTree(value)`: reflection-based converter that walks fields (incl. private + inherited) and readable instance properties, with cycle detection (reference-equality `HashSet`), depth cap (default 6), collection-head cap (default 50), and special handling for `IDictionary` / `IEnumerable`
- `ValueFormatter`: 1-line previews for primitives, strings (truncated + escaped), Unity types (`Vector2/3/4`, `Color`, `Quaternion`, `Rect`, `Bounds`, `GameObject`, `Component`, `UnityEngine.Object`), collections, and a `ToString` fallback
- `TypeFormatter`: short type names with C# keyword aliases, generic argument formatting, array rank, and `Nullable<T>` → `T?`
- Window output now renders complex results with a `MultiColumnTreeView` (Name / Type / Value columns), auto-expanding the root level. Leaf results (`int`, `string`, `Vector3`, etc.) keep the inline `=> X` rendering from Phase 1 to avoid one-row trees
- Compiler-generated `<...>k__BackingField` entries are filtered out so auto-property values appear only once via the property itself

### Fixed (Phase 2 — surfaced while inspecting real Unity scene objects)
- `ValueFormatter.Format` no longer throws `NullReferenceException` when the value is a Unity "fake-null" reference (a wrapper whose native side is destroyed or was never assigned). Each `UnityEngine.Object` / `Component` / `GameObject` branch now compares against null using Unity's overload first and returns a `(destroyed)` marker.
- `SimpleObjectSerializer.BuildNode` short-circuits on fake-null `UnityEngine.Object` values, emitting a `(missing/destroyed)` leaf instead of attempting to walk a destroyed object's fields.
- `UnityEngine.Transform` (and `RectTransform`) is now treated as a leaf type. Its computed accessors (`position`, `lossyScale`, `eulerAngles`, …) read from internal matrices and fire a native `Assertion failed: 'ValidTRS()'` when the matrix is degenerate; those asserts bypass managed `try/catch` and spam the Console.
- Properties whose **declaring type** lives in a Unity-shipped assembly (`UnityEngine.*`, `UnityEditor.*`, `Unity.*`) are skipped. Many of those accessors (`Image.mainTexture`, `Material.color`, `Renderer.bounds`, `Canvas.worldCamera`, …) read native state and trigger the same class of native asserts. Properties declared on **user types** (MonoBehaviour / ScriptableObject subclasses, etc.) are walked normally, so user-defined `Computed => …` accessors stay visible. Users wanting a specific Unity-shipped computed value can call it directly (e.g. `return rect.localPosition;`).
- `BuildDictChildren` now mirrors `BuildEnumerableChildren`'s try/catch: a custom `IDictionary` whose `GetEnumerator()` / `MoveNext` / `Current` throws no longer aborts the whole `ToTree`; partially-collected entries plus a `<enumeration>` error leaf are returned.
- `ValueFormatter.Format` is now guaranteed not to throw. The dict / collection preview branches read `Count` inside their own `try/catch` and fall back to `(IDictionary, count unavailable)` / `(ICollection, count unavailable)`; an outer `try/catch` around the whole formatter returns `<TypeName> <preview error: …>` for any other escape. This closes a hole where `BuildNode` called `Format(value)` *before* `BuildDictChildren`'s guard could run, so a dict with a throwing `Count` getter still aborted the whole `ToTree`.

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
