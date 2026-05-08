using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Core;
using RoslynRepl.Editor.Diagnostics;

namespace RoslynRepl.Editor.UI
{
    public class RoslynReplWindow : EditorWindow
    {
        private const string PackageRoot = "Packages/com.roslyn-repl";
        private const string UxmlPath = PackageRoot + "/Editor/UI/Layouts/RoslynReplWindow.uxml";
        private const string UssPath  = PackageRoot + "/Editor/UI/Layouts/RoslynReplWindow.uss";

        private const string SessionKey_CodeText = "RoslynRepl.CodeText";

        private const string DefaultCode =
@"// Roslyn REPL — write C# below, F5 or Ctrl+Enter to run.
// Use 'return X;' to surface a value. Debug.Log() output is captured.
return UnityEngine.Application.unityVersion;";

        private CodeEditorView _codeEditor;
        private VisualElement _outputContent;
        private ScrollView _outputScroll;
        private Label _durationLabel;
        private Label _modeLabel;
        private Label _outputSummary;
        private ObjectBrowserView _browser;
        private WatchPanelView _watch;
        private MethodPatchView _patchView;
        private VisualElement _outputScrollHost;       // wraps the ScrollView
        private VisualElement _patchPaneHost;
        private Label _outputTab;
        private Label _patchesTab;
        private bool _patchesModeActive;

        [MenuItem("Tools/Roslyn REPL/Open", priority = 10)]
        public static void Open()
        {
            var window = GetWindow<RoslynReplWindow>();
            window.titleContent = new GUIContent("Roslyn REPL");
            window.minSize = new Vector2(720, 480);
            window.Show();
        }

        [MenuItem("Tools/Roslyn REPL/Patch Method…", priority = 40)]
        public static void OpenPatchMode()
        {
            // Open (or focus) the main window, then flip the Output / Patches
            // tab so the user lands directly on the patch UI. One menu, one
            // window — see issue #14 review.
            Open();
            var win = GetWindow<RoslynReplWindow>();
            win.SetPatchesModeActive(true);
        }

        [MenuItem("Tools/Roslyn REPL/Import Default Snippets", priority = 30)]
        public static void ImportDefaultSnippets()
        {
            var (added, skipped) = DefaultSnippets.ImportAll();
            string message = added > 0
                ? $"Added {added} default snippets to your library."
                : "No new snippets were added.";
            if (skipped > 0)
                message += $"\n{skipped} skipped (a snippet with the same name already exists).";
            EditorUtility.DisplayDialog("Default snippets", message, "OK");
            // If the library popup is open, refresh its list so the new
            // entries appear without forcing the user to reopen it.
            SnippetLibraryWindow.NotifyChanged();
        }

        [MenuItem("Tools/Roslyn REPL/Reset Project Data", priority = 200)]
        public static void ResetProjectData()
        {
            // Snapshot counts before clearing so the confirmation and the
            // result dialog can both surface concrete numbers — vague
            // "are you sure?" prompts get clicked through without thought.
            int snippetCount = SnippetStore.Load().Count;
            int historyCount = RunHistoryStore.Load().Count;
            int watchCount   = WatchStore.Load().Count;
            int usingsCount  = UsingsStore.LoadCustom().Count;
            int storeTotal = snippetCount + historyCount + watchCount + usingsCount;

            // Phase 11 PR fix: the persistent stores aren't the only
            // surface that can hold sensitive data. A user who only ran
            // a single ad-hoc snippet and never saved it will have
            // storeTotal=0 but a non-null `_` and a result rendered
            // into Output — exactly the case Reset is supposed to
            // cover. Pull both into the scope check so the early-return
            // doesn't lie to the user about there being nothing to wipe.
            bool hasCarryOver = ReplEngine.LastResult != null;
            var openWindows = Resources.FindObjectsOfTypeAll<RoslynReplWindow>();
            int dirtyOutputs = 0;
            foreach (var w in openWindows)
                if (w != null && w.HasInspectableOutput()) dirtyOutputs++;

            // the engine PR fix: runtime method patches are also "live state"
            // a security/cleanup reset has to wipe. The in-memory
            // PatchRegistry holds patch bodies (potentially containing
            // sensitive snippet text); Harmony has already redirected
            // the targeted methods at the IL level so even after the
            // dialog the user could still observe altered Play Mode
            // behavior. Pull both into the reset scope.
            int patchCount = RoslynRepl.Editor.Patches.PatchRegistry.Count;

            // the persistence layer PR fix: an upgraded user can land here with the
            // in-memory registry empty *and* the persisted EditorPrefs
            // key still present (the previous package version wrote
            // SetString(key, "") on Reset, which deserializes back to
            // zero specs but the key sticks). Counting only patchCount
            // would let the early-return below skip the cleanup that
            // README promises. PatchPersistence.HasAny() catches the
            // stale-key case so reset still routes through Clear().
            bool hasStalePatchKey = patchCount == 0
                && RoslynRepl.Editor.Patches.PatchPersistence.HasAny();

            // Watch compile cache PR fix: WatchStore.Clear() drops the
            // expression *list* from EditorPrefs, but ReplEngine's
            // compile cache still holds a strong reference to each
            // wrapped source string keyed under that exact text. A
            // user resetting after running sensitive watches would see
            // the dialog claim "watches cleared" while the same
            // expression text stays alive in process memory until the
            // next AppDomain.AssemblyLoad. Pull the cache size into
            // both the early-return scope check and the dialog so the
            // counts don't lie about what actually got wiped.
            int compileCacheCount = ReplEngine.CompileCacheCount;

            if (storeTotal == 0 && !hasCarryOver && dirtyOutputs == 0
                && patchCount == 0 && !hasStalePatchKey
                && compileCacheCount == 0)
            {
                EditorUtility.DisplayDialog(
                    "Roslyn REPL — Reset Project Data",
                    "Nothing to clear — all four stores are empty, no `_` carry-over is set, no Output panel has run results to wipe, no runtime method patches are active, and the compiled-watch cache is empty.",
                    "OK");
                return;
            }

            var detail = new System.Text.StringBuilder();
            detail.Append("This will permanently delete the REPL data for the *current project*:\n");
            detail.Append($"  • {snippetCount} saved snippet{(snippetCount == 1 ? "" : "s")}\n");
            detail.Append($"  • {historyCount} run history entr{(historyCount == 1 ? "y" : "ies")}\n");
            detail.Append($"  • {watchCount} watch expression{(watchCount == 1 ? "" : "s")}\n");
            detail.Append($"  • {usingsCount} custom using{(usingsCount == 1 ? "" : "s")}\n");
            // Always list the in-memory targets — they're always reset,
            // even when their visible state is empty, so the dialog
            // doesn't surprise the user later.
            detail.Append(hasCarryOver
                ? "  • the in-memory previous-result carry-over (`_`)\n"
                : "  • the in-memory previous-result carry-over (`_`, currently empty)\n");
            detail.Append(dirtyOutputs > 0
                ? $"  • the Output panel of {dirtyOutputs} open REPL window{(dirtyOutputs == 1 ? "" : "s")}\n"
                : "  • the Output panel of any open REPL window (currently idle)\n");
            detail.Append(patchCount > 0
                ? $"  • {patchCount} runtime method patch{(patchCount == 1 ? "" : "es")} (Harmony detours will be reverted)\n"
                : "  • runtime method patches (none active)\n");
            detail.Append(compileCacheCount > 0
                ? $"  • {compileCacheCount} cached compiled watch{(compileCacheCount == 1 ? "" : "es")} (in-memory wrapped source + MethodInfo)\n"
                : "  • the compiled-watch cache (currently empty)\n");
            detail.Append("\nOther projects on this machine are not affected. There is no undo.");

            if (!EditorUtility.DisplayDialog(
                "Roslyn REPL — Reset Project Data",
                detail.ToString(),
                "Delete everything",
                "Cancel"))
            {
                return;
            }

            // Always run every clear, regardless of which buckets had
            // content. Skipping ResetLastResult / ClearOutputAfterReset
            // when storeTotal==0 is exactly the bug the Phase 11 PR
            // caught; same lesson here for runtime patches.
            SnippetStore.Clear();
            RunHistoryStore.Clear();
            WatchStore.Clear();
            UsingsStore.Clear();
            ReplEngine.ResetLastResult();
            // Order: clear the cache *after* WatchStore.Clear so a
            // racing AssemblyLoad mid-reset can't repopulate from a
            // surviving WatchEvaluator timer. WatchStore is the only
            // surface that asks the evaluator to compile, so once it's
            // empty the cache stays empty regardless of when the
            // invalidation lands.
            ReplEngine.InvalidateCompileCache();

            // Drop every Harmony detour first, then wipe the registry
            // — order matters: PatchRegistry.Clear fires Changed, which
            // would render an "active patches" UI inconsistent for a
            // beat if we hadn't already torn down Harmony state.
            RoslynRepl.Editor.Patches.PatchEngine.RevertAll();
            RoslynRepl.Editor.Patches.PatchRegistry.Clear();

            // Phase 11b: snippet/history/usings/watch popups already
            // refresh themselves through their store Changed events.
            // The host Output panel doesn't subscribe to anything —
            // ClearOutputAfterReset is the explicit handshake so the
            // visible UI matches the wiped data.
            foreach (var w in openWindows)
            {
                if (w != null) w.ClearOutputAfterReset();
            }

            int reportedTotal = storeTotal + (hasCarryOver ? 1 : 0) + dirtyOutputs + patchCount + compileCacheCount;
            EditorUtility.DisplayDialog(
                "Roslyn REPL — Reset Project Data",
                $"Cleared {reportedTotal} item{(reportedTotal == 1 ? "" : "s")} across snippet library, run history, watches, custom usings, the `_` carry-over, visible Output panels, runtime method patches, and the compiled-watch cache.",
                "OK");
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                ShowFallback(root, $"UXML asset not found at:\n{UxmlPath}\n\nVerify the package was imported correctly.");
                return;
            }
            uxml.CloneTree(root);

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null) root.styleSheets.Add(uss);

            BindControls(root);
        }

        private void BindControls(VisualElement root)
        {
            _outputContent = root.Q<VisualElement>("output-content");
            _outputScroll  = root.Q<ScrollView>("output-scroll");
            _durationLabel = root.Q<Label>("duration-label");
            _modeLabel     = root.Q<Label>("mode-label");
            _outputSummary = root.Q<Label>("output-summary-label");

            // Note: Output / Patches mode tabs in the lower pane
            // header. Clicking either label flips the visible host
            // between the existing output ScrollView and the patch UI.
            _outputScrollHost = _outputScroll;
            _patchPaneHost = root.Q<VisualElement>("patch-pane-host");
            _outputTab = root.Q<Label>("output-tab-output");
            _patchesTab = root.Q<Label>("output-tab-patches");
            if (_outputTab != null)
            {
                _outputTab.RegisterCallback<MouseDownEvent>(_ => SetPatchesModeActive(false));
            }
            if (_patchesTab != null)
            {
                _patchesTab.RegisterCallback<MouseDownEvent>(_ => SetPatchesModeActive(true));
            }
            // Mount the patch view (subscribes to PatchRegistry.Changed
            // in its ctor; the previous instance is disposed first to
            // avoid the same WatchPanelView-style handler leak Phase 10
            // patched).
            _patchView?.Dispose();
            _patchView = null;
            if (_patchPaneHost != null)
            {
                _patchView = new MethodPatchView(_patchPaneHost);
            }
            // Apply the current mode (Output by default; OpenPatchMode
            // can flip to Patches before this returns by calling
            // SetPatchesModeActive again).
            SetPatchesModeActive(_patchesModeActive);

            // Mount the object browser into its host
            var browserHost = root.Q<VisualElement>("browser-host");
            if (browserHost != null)
            {
                browserHost.Clear();
                _browser = new ObjectBrowserView(browserHost);
                _browser.OnInstanceChosen += OnBrowserInstanceChosen;
            }

            // Mount the watch panel below output. CreateGUI can fire
            // again on domain reload or panel rebuild; the previous view
            // — if any — has already subscribed to the static
            // WatchStore.Changed event, so we must dispose it first or
            // every rebuild leaves a stale handler subscribed forever.
            // After enough rebuilds an Add/Remove fires the panel
            // refresh N times, causing each watch to evaluate N times
            // and `_` to update N times against the same row.
            _watch?.Dispose();
            _watch = null;
            var watchHost = root.Q<VisualElement>("watch-pane-host");
            if (watchHost != null)
            {
                _watch = new WatchPanelView(watchHost);
            }

            // Code editor: restore from session and persist on change.
            // Phase 4 lifts the bare TextField into a composite view that
            // owns the gutter + caret indicator.
            var editorHost = root.Q<VisualElement>("code-editor-host");
            if (editorHost != null)
            {
                editorHost.Clear();
                _codeEditor = new CodeEditorView();
                editorHost.Add(_codeEditor);

                var saved = SessionState.GetString(SessionKey_CodeText, null);
                _codeEditor.value = string.IsNullOrEmpty(saved) ? DefaultCode : saved;
                _codeEditor.TextChanged += newText =>
                    SessionState.SetString(SessionKey_CodeText, newText);
            }

            var runBtn = root.Q<Button>("run-btn");
            if (runBtn != null) runBtn.clicked += Run;

            var clearBtn = root.Q<Button>("clear-btn");
            if (clearBtn != null) clearBtn.clicked += ClearOutput;

            var verifyBtn = root.Q<Button>("verify-btn");
            if (verifyBtn != null) verifyBtn.clicked += () => SetupVerifier.Verify();

            var usingsBtn = root.Q<Button>("usings-btn");
            if (usingsBtn != null) usingsBtn.clicked += UsingsEditorWindow.Open;

            var historyBtn = root.Q<Button>("history-btn");
            if (historyBtn != null) historyBtn.clicked += RunHistoryWindow.Open;

            var snippetsBtn = root.Q<Button>("snippets-btn");
            if (snippetsBtn != null) snippetsBtn.clicked += SnippetLibraryWindow.Open;

            // Subscribe once: re-binding on every CreateGUI would stack
            // handlers across domain reloads. Plain `-= +=` covers both the
            // first mount and rebuilds.
            RunHistoryWindow.OnSnippetChosen -= LoadSnippetIntoEditor;
            RunHistoryWindow.OnSnippetChosen += LoadSnippetIntoEditor;
            SnippetLibraryWindow.OnSnippetChosen -= LoadSnippetIntoEditor;
            SnippetLibraryWindow.OnSnippetChosen += LoadSnippetIntoEditor;
            SnippetLibraryWindow.OnSaveRequested -= SaveCurrentEditorAsSnippet;
            SnippetLibraryWindow.OnSaveRequested += SaveCurrentEditorAsSnippet;

            UpdateModeLabel();
            // Dedupe: CreateGUI may be called multiple times (domain reload,
            // explicit rebuild). root.Clear() removes children but keeps
            // callbacks on root itself, and EditorApplication.playModeStateChanged
            // is a static event — both will accumulate without explicit unregister.
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            var versionLabel = root.Q<Label>("version-label");
            if (versionLabel != null) versionLabel.text = $"v{ReadPackageVersion()}";

            // F5 / Ctrl+Enter to run; intercept on TrickleDown so TextField
            // doesn't swallow the key first. Unregister first to avoid stacking
            // when CreateGUI rebuilds — otherwise one keypress triggers Run N times.
            root.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            ShowReadyMessage();
        }

        private void OnPlayModeChanged(PlayModeStateChange _) => UpdateModeLabel();

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            RunHistoryWindow.OnSnippetChosen -= LoadSnippetIntoEditor;
            SnippetLibraryWindow.OnSnippetChosen -= LoadSnippetIntoEditor;
            SnippetLibraryWindow.OnSaveRequested -= SaveCurrentEditorAsSnippet;
            // Drop the watch panel's WatchStore subscription so this
            // window's instance doesn't keep refreshing in the
            // background after it's closed (or before it's rebuilt by
            // the next CreateGUI).
            _watch?.Dispose();
            _watch = null;
            _patchView?.Dispose();
            _patchView = null;
        }

        public void SetPatchesModeActive(bool active)
        {
            _patchesModeActive = active;
            // The output/patch swap toggles DisplayStyle directly rather
            // than relying on a CSS class — UIElements doesn't pick up
            // class-based `display:none` cleanly across all 2022.3
            // versions.
            if (_outputScrollHost != null)
                _outputScrollHost.style.display = active ? DisplayStyle.None : DisplayStyle.Flex;
            if (_patchPaneHost != null)
                _patchPaneHost.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;

            if (_outputTab != null)
                _outputTab.EnableInClassList("rr-pane-tab--active", !active);
            if (_patchesTab != null)
                _patchesTab.EnableInClassList("rr-pane-tab--active", active);
        }

        private void LoadSnippetIntoEditor(string code)
        {
            if (_codeEditor == null || code == null) return;
            _codeEditor.value = code;
        }

        private void SaveCurrentEditorAsSnippet(string name)
        {
            if (_codeEditor == null || string.IsNullOrWhiteSpace(name)) return;
            // Pull live code at commit time — the popup stays passive about
            // the buffer state, so it can't go stale if the user kept
            // typing after opening it.
            SnippetStore.Save(name, _codeEditor.value ?? string.Empty);
            SnippetLibraryWindow.NotifyChanged();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            bool isF5         = evt.keyCode == KeyCode.F5;
            bool isCtrlReturn = evt.ctrlKey && (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter);
            if (isF5 || isCtrlReturn)
            {
                Run();
                evt.StopPropagation();
            }
        }

        private void Run()
        {
            if (_codeEditor == null || _outputContent == null) return;

            var code = _codeEditor.value ?? string.Empty;
            ClearOutput();
            AppendOutput($"▶ Running ({code.Length} chars)…", "info");

            // Pull defaults + user-added usings on every run so changes made
            // in the Usings editor (which writes EditorPrefs synchronously)
            // take effect immediately, no window restart required.
            var options = new ReplOptions { Usings = UsingsStore.EffectiveUsings() };
            var result = ReplEngine.Execute(code, options);
            // Record the snippet whenever Execute returns — including
            // failures, since users often want to scroll back to a broken
            // run, fix the error, and try again. RunHistoryStore de-dupes
            // identical consecutive entries so re-running the same code
            // doesn't churn the ring.
            RunHistoryStore.Push(code);
            RenderResult(result);
            // Re-evaluate every watched expression after the user's
            // run lands, so values reflect any side effects the snippet
            // produced (e.g. mutating a manager state visible to a
            // watch).
            _watch?.Refresh();
        }

        // Double-click on a browser row routes through different paths
        // depending on the lower pane's mode. Pull UI: in Patches
        // mode the click means "I want to patch a method on this
        // type's class" — open the method picker instead of the usual
        // Output inspect. Output mode keeps the legacy
        // render-as-`return X;` behavior.
        private void OnBrowserInstanceChosen(InstanceEntry entry)
        {
            if (entry == null) return;

            object value = entry.Value;
            if (value is UnityEngine.Object uo && uo == null) value = null;

            if (_patchesModeActive)
            {
                if (value == null)
                {
                    // Without a live instance we can still pick a
                    // method on the declared type — but DeclaredType
                    // is only set for some entries (singletons read
                    // from a static accessor). Fall back to
                    // value's GetType() when present, otherwise show
                    // a hint.
                    if (entry.DeclaredType != null)
                    {
                        OpenMethodPickerForType(entry.DeclaredType);
                    }
                    else
                    {
                        AppendOutput("(can't open method picker — instance is null and no DeclaredType)", "warning");
                    }
                    return;
                }
                OpenMethodPickerForType(value.GetType());
                return;
            }

            // Output mode — original behavior.
            if (_outputContent == null) return;

            ClearOutput();
            AppendOutput($"▼ Browse: {entry.TypeName} \"{entry.DisplayName}\" ({entry.SubLabel})", "info");

            if (value == null)
            {
                AppendOutput("(instance is null or destroyed)", "warning");
                if (_outputSummary != null) _outputSummary.text = "null";
                return;
            }

            ReplEngine.SetLastResult(value);
            AppendResult(SimpleObjectSerializer.ToTree(value));
            if (_durationLabel != null) _durationLabel.text = string.Empty;
            if (_outputSummary != null) _outputSummary.text = "Browsed";
            _watch?.Refresh();
            ScrollOutputToBottom();
        }

        private void OpenMethodPickerForType(Type type)
        {
            if (type == null) return;
            MethodPickerPopup.Open(type, picked =>
            {
                if (picked == null) return;
                _patchView?.FillFormFromMethod(picked);
            });
        }

        private void RenderResult(ReplResult result)
        {
            ClearOutput();
            // Drop any markers from a previous compile before laying down new
            // ones — a successful run should leave the gutter clean.
            _codeEditor?.ClearErrorMarkers();

            // Captured logs first — but filter out background noise from other
            // Play Mode systems (server, ad SDK, etc.) that fired during the
            // Invoke window. ClassifyLogs in ReplEngine flags only logs whose
            // stack trace contains the generated wrapper class.
            foreach (var log in result.Logs)
            {
                if (!log.FromSnippet) continue;
                var sev = log.Type switch
                {
                    LogType.Error or LogType.Exception or LogType.Assert => "error",
                    LogType.Warning => "warning",
                    _ => "log"
                };
                AppendOutput($"[{log.Type}] {log.Message}", sev);
            }

            // Then the result, error, or diagnostics
            switch (result.Kind)
            {
                case ReplResultKind.Success:
                    // Suppress the synthetic "=> null" caused by the wrapper's
                    // fallback `return null;` for snippets that don't return a
                    // value. HasReturnValue is true only when Value != null, so
                    // an explicit `return null;` from user code is also hidden —
                    // a small acceptable false-negative; users wanting to surface
                    // a null indicator can `return "null"` (a non-null string).
                    if (result.HasReturnValue)
                        AppendResult(SimpleObjectSerializer.ToTree(result.Value));
                    break;

                case ReplResultKind.CompileError:
                    AppendOutput("Compilation failed:", "error");
                    foreach (var d in result.Diagnostics)
                    {
                        var prefix = d.IsInUserCode ? $"line {d.Line}, col {d.Column}" : "(internal)";
                        AppendOutput($"  {prefix}: {d.Code} — {d.Message}", "diagnostic");
                    }
                    // Surface the same diagnostics inline against the code:
                    // every user-code diagnostic gets a red gutter dot whose
                    // tooltip shows the message. Internal (wrapper-region)
                    // diagnostics are omitted — they have no meaningful line
                    // in what the user actually typed.
                    var markers = new List<(int line, string message)>(result.Diagnostics.Count);
                    foreach (var d in result.Diagnostics)
                    {
                        if (!d.IsInUserCode) continue;
                        markers.Add((d.Line, $"{d.Code}: {d.Message}"));
                    }
                    _codeEditor?.SetErrorMarkers(markers);
                    break;

                case ReplResultKind.RuntimeError:
                    AppendOutput($"Runtime error: {result.ErrorMessage}", "error");
                    if (!string.IsNullOrEmpty(result.StackTrace))
                        AppendOutput(result.StackTrace, "diagnostic");
                    break;

                case ReplResultKind.Cancelled:
                    // Cancellation isn't a bug — render as a warning so
                    // it stands apart from compile / runtime errors.
                    AppendOutput($"⏹ {result.ErrorMessage}", "warning");
                    break;
            }

            UpdateStatusLabels(result);
            ScrollOutputToBottom();
        }

        private void UpdateStatusLabels(ReplResult result)
        {
            if (_durationLabel != null)
                _durationLabel.text = $"{result.Duration.TotalMilliseconds:0} ms";

            if (_outputSummary != null)
            {
                _outputSummary.text = result.Kind switch
                {
                    ReplResultKind.Success      => "OK",
                    ReplResultKind.CompileError => $"Compile error ({result.Diagnostics.Count})",
                    ReplResultKind.RuntimeError => "Runtime error",
                    ReplResultKind.Cancelled    => "Cancelled",
                    _ => string.Empty
                };
            }
        }

        private void ClearOutput()
        {
            if (_outputContent != null) _outputContent.Clear();
            if (_outputSummary != null) _outputSummary.text = string.Empty;
        }

        // Called by ResetProjectData so the visible Output panel matches
        // the wiped data state. Without this the cleared run gets a
        // dialog confirmation, but the *previous* run's logs / result
        // tree stayed on screen unchanged — visually contradicting the
        // "everything was reset" message. Also nudges the duration
        // label and gutter error markers back to their idle state.
        public void ClearOutputAfterReset()
        {
            ClearOutput();
            if (_durationLabel != null) _durationLabel.text = string.Empty;
            _codeEditor?.ClearErrorMarkers();
            ShowReadyMessage();
        }

        // Idle-state predicate used by ResetProjectData to decide
        // whether this window has anything worth wiping. The Output
        // panel always carries the Ready prompt as a single child after
        // ClearOutput → ShowReadyMessage; anything past that means a
        // Run produced rows the user hasn't cleared. Treat duration
        // text as a secondary signal so a finished run with zero
        // log/result rows (rare but possible) still counts as dirty.
        public bool HasInspectableOutput()
        {
            if (_outputContent != null && _outputContent.childCount > 1) return true;
            if (_durationLabel != null && !string.IsNullOrEmpty(_durationLabel.text)) return true;
            if (_outputSummary != null && !string.IsNullOrEmpty(_outputSummary.text)) return true;
            return false;
        }

        private void AppendOutput(string text, string severity)
        {
            if (_outputContent == null) return;
            var label = new Label(text);
            label.AddToClassList("rr-output-line");
            label.AddToClassList($"rr-output-line--{severity}");
            label.style.whiteSpace = WhiteSpace.Normal;
            _outputContent.Add(label);
        }

        private void AppendResult(ReplValueNode root)
        {
            if (_outputContent == null || root == null) return;

            // Leaf result (primitive, string, Vector3, etc.) — render inline,
            // identical look to Phase 1.
            if (!root.IsExpandable || root.Children.Count == 0)
            {
                AppendOutput($"=> {root.Preview}", "result");
                return;
            }

            // Header above the tree
            var header = new Label($"=> {root.Preview}");
            header.AddToClassList("rr-output-line");
            header.AddToClassList("rr-output-line--result");
            _outputContent.Add(header);

            var tv = BuildResultTree(root);
            tv.AddToClassList("rr-result-tree");
            _outputContent.Add(tv);
        }

        private static MultiColumnTreeView BuildResultTree(ReplValueNode root)
        {
            var tv = new MultiColumnTreeView();
            tv.fixedItemHeight = 22;
            var treeHeight = CalculateResultTreeHeight(root);
            tv.style.height = treeHeight;
            tv.style.minHeight = treeHeight;
            tv.style.maxHeight = treeHeight;
            tv.style.flexGrow = 0;
            tv.style.flexShrink = 0;

            // --- columns ---
            tv.columns.Add(MakeColumn("name",  "Name",  220, n => n?.Name     ?? string.Empty, "rr-treecell--name", tv));
            tv.columns.Add(MakeColumn("type",  "Type",  160, n => n?.TypeName ?? string.Empty, "rr-treecell--type", tv));
            tv.columns.Add(MakeColumn("value", "Value", 320, n => n?.Preview  ?? string.Empty, "rr-treecell--value", tv));

            // --- data ---
            int nextId = 0;
            var rootItems = new List<TreeViewItemData<ReplValueNode>>
            {
                ToItemData(root, ref nextId)
            };
            tv.SetRootItems(rootItems);
            tv.Rebuild();
            tv.ExpandRootItems();
            return tv;
        }

        private static float CalculateResultTreeHeight(ReplValueNode root)
        {
            const float HeaderHeight = 24f;
            const float RowHeight = 22f;
            const float MinHeight = 140f;
            const float MaxHeight = 420f;

            int rowCount = CountTreeRows(root);
            return Mathf.Clamp(HeaderHeight + rowCount * RowHeight, MinHeight, MaxHeight);
        }

        private static int CountTreeRows(ReplValueNode node)
        {
            if (node == null) return 0;

            int count = 1;
            if (node.Children == null) return count;
            foreach (var child in node.Children)
                count += CountTreeRows(child);
            return count;
        }

        private static Column MakeColumn(
            string name, string title, float width,
            System.Func<ReplValueNode, string> getter,
            string extraClass,
            MultiColumnTreeView tv)
        {
            var col = new Column
            {
                name = name,
                title = title,
                width = width,
                minWidth = 80,
                stretchable = true
            };
            col.makeCell = () =>
            {
                var lbl = new Label();
                lbl.AddToClassList("rr-treecell");
                if (!string.IsNullOrEmpty(extraClass)) lbl.AddToClassList(extraClass);
                return lbl;
            };
            col.bindCell = (ve, idx) =>
            {
                var node = tv.GetItemDataForIndex<ReplValueNode>(idx);
                ((Label)ve).text = getter(node);
            };
            return col;
        }

        private static TreeViewItemData<ReplValueNode> ToItemData(ReplValueNode node, ref int nextId)
        {
            var children = new List<TreeViewItemData<ReplValueNode>>();
            if (node.Children != null)
            {
                foreach (var c in node.Children)
                    children.Add(ToItemData(c, ref nextId));
            }
            return new TreeViewItemData<ReplValueNode>(nextId++, node, children);
        }

        private void ScrollOutputToBottom()
        {
            if (_outputScroll == null) return;
            EditorApplication.delayCall += () =>
            {
                if (_outputScroll == null) return;
                _outputScroll.verticalScroller.value = _outputScroll.verticalScroller.highValue;
            };
        }

        private void ShowReadyMessage()
        {
            ClearOutput();
            AppendOutput("Roslyn REPL ready. Press F5 or Ctrl+Enter to run.", "info");
        }

        private void UpdateModeLabel()
        {
            if (_modeLabel == null) return;
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            _modeLabel.text = playing ? "PLAY" : "EDIT";
            _modeLabel.RemoveFromClassList("rr-badge--play");
            _modeLabel.RemoveFromClassList("rr-badge--edit");
            _modeLabel.AddToClassList(playing ? "rr-badge--play" : "rr-badge--edit");
        }

        private static string ReadPackageVersion()
        {
            try
            {
                var json = System.IO.File.ReadAllText(PackageRoot + "/package.json");
                const string key = "\"version\"";
                var i = json.IndexOf(key, StringComparison.Ordinal);
                if (i < 0) return "?";
                i = json.IndexOf('"', i + key.Length + 1);
                if (i < 0) return "?";
                var j = json.IndexOf('"', i + 1);
                return j > i ? json.Substring(i + 1, j - i - 1) : "?";
            }
            catch
            {
                return "?";
            }
        }

        private static void ShowFallback(VisualElement root, string message)
        {
            var label = new Label(message);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.paddingLeft = 12;
            label.style.paddingTop = 12;
            root.Add(label);
        }
    }
}
