using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Core;
using RoslynRepl.Editor.Diagnostics;
using RoslynRepl.Editor.Patches;
using RoslynRepl.Editor.UI.Find;

namespace RoslynRepl.Editor.UI
{
    public class RoslynReplWindow : EditorWindow
    {
        private static string UxmlPath => ReplPackagePaths.AssetPath("Editor/UI/Layouts/RoslynReplWindow.uxml");
        private static string UssPath  => ReplPackagePaths.AssetPath("Editor/UI/Layouts/RoslynReplWindow.uss");

        private const string SessionKey_CodeText = "RoslynRepl.CodeText";

        // Marshal Run snippet onto a Player Update coroutine before
        // invoking. Default true: most snippets expect to behave like
        // a normal Button.onClick (popup spawns / canvas updates /
        // SuperScrollView Init all rely on the player layout cycle).
        // Edit Mode ignores this and runs synchronously since there's
        // no Player Update to yield to. Stored machine-wide because
        // it's a "how do I want REPL to feel" preference, not a
        // project setting.
        private const string PrefsKey_RunOnPlayerFrame = "RoslynRepl.RunOnPlayerFrame";
        private const string RunOnPlayerFrameMenuPath = "Tools/Roslyn REPL/Run on Player Frame";

        public static bool RunOnPlayerFrameEnabled
        {
            get => EditorPrefs.GetBool(PrefsKey_RunOnPlayerFrame, true);
            set
            {
                EditorPrefs.SetBool(PrefsKey_RunOnPlayerFrame, value);
                Menu.SetChecked(RunOnPlayerFrameMenuPath, value);
            }
        }

        [MenuItem(RunOnPlayerFrameMenuPath, priority = 215)]
        private static void ToggleRunOnPlayerFrame() => RunOnPlayerFrameEnabled = !RunOnPlayerFrameEnabled;

        [MenuItem(RunOnPlayerFrameMenuPath, validate = true)]
        private static bool ToggleRunOnPlayerFrameValidate()
        {
            // Refresh the checkmark on every menu open so a flip
            // from elsewhere (programmatic setter, another window)
            // shows correctly. Same pattern PatchAutoReapply uses
            // for its menu toggle.
            Menu.SetChecked(RunOnPlayerFrameMenuPath, RunOnPlayerFrameEnabled);
            return true;
        }

        // Issue #28: opt-in switch for property getter traversal
        // in the Output result tree. Defaults to off (fields-only)
        // because property getters routinely run lazy init, IO,
        // logging, or state mutation — and Output is supposed to be
        // a passive "inspect this value" pass that doesn't change
        // project state behind the user's back. Watch already opted
        // out for the same reason; this brings the Output side of
        // the panel to the same baseline.
        private const string IncludeOutputPropertiesMenuPath =
            "Tools/Roslyn REPL/Output: Include Property Getters";

        [MenuItem(IncludeOutputPropertiesMenuPath, priority = 218)]
        private static void ToggleIncludeOutputProperties()
            => OutputSettings.IncludeProperties = !OutputSettings.IncludeProperties;

        [MenuItem(IncludeOutputPropertiesMenuPath, validate = true)]
        private static bool ToggleIncludeOutputPropertiesValidate()
        {
            Menu.SetChecked(IncludeOutputPropertiesMenuPath, OutputSettings.IncludeProperties);
            return true;
        }

        // Build the Options instance handed to SimpleObjectSerializer.ToTree
        // at the Output call sites. Centralised so any future Output-tree
        // setting (depth cap override, collection-head bump) lands here
        // automatically instead of having to remember every call site.
        private static SimpleObjectSerializer.Options BuildOutputTreeOptions()
            => new SimpleObjectSerializer.Options
            {
                IncludeProperties = OutputSettings.IncludeProperties,
            };

        // Issue #20: ack flag for the cooperative-cancel safety dialog.
        // Stored in machine-wide EditorPrefs (not project-scoped) — the
        // user only needs to learn the threading model once per
        // workstation, and the project-scoped Reset deliberately leaves
        // it alone so handing the project off doesn't surprise the new
        // user with a missing warning. A new machine still gets the
        // dialog on first Run.
        private const string PrefsKey_CoopCancelAcknowledged = "RoslynRepl.CoopCancelAcknowledged";

        private const string CoopWarningTooltip =
            "Snippets run on the Unity Editor main thread.\n" +
            "Only `ct.ThrowIfCancellationRequested()` (cooperative cancel) " +
            "can interrupt a running snippet — `while(true){}` without a `ct` check freezes the Editor.";

        private const string DefaultCode =
@"// Roslyn REPL — write C# below, F5 or Ctrl+Enter to run.
// Use 'return X;' to surface a value. Debug.Log() output is captured.
return UnityEngine.Application.unityVersion;";

        private CodeEditorView _codeEditor;
        private VisualElement _outputContent;
        private ScrollView _outputScroll;
        private Label _durationLabel;
        private Label _modeLabel;
        private Label _patchBadge;
        private Label _asmBadge;
        // Issue #59: toolbar surface for the current `_` carry-over
        // target. _underscoreHost is the row wrapper we toggle
        // display on; _underscoreBadge is the click-to-reinspect
        // label inside it; _underscoreClearBtn is the ✕ that drops
        // the value via ReplEngine.ResetLastResult().
        private VisualElement _underscoreHost;
        private Label _underscoreBadge;
        private Button _underscoreClearBtn;
        private Label _outputSummary;
        private ObjectBrowserView _browser;
        private WatchPanelView _watch;
        private MethodPatchView _patchView;
        // Ctrl+F overlay: search bar that scans Output / Watch /
        // Patches by name + preview. Owned by this window; the
        // controller mediates between the overlay UI and the three
        // pane-side IReplFindable implementations.
        private ReplFindController _findController;
        private ReplFindOverlay _findOverlay;
        private OutputFindable _outputFindable;
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

        [MenuItem("Tools/Roslyn REPL/Force Domain Reload", priority = 220)]
        public static void ForceDomainReload()
        {
            // Issue #24: long sessions accumulate ReplDynamic_*
            // assemblies that Mono can't unload. The reload tears
            // them all out at once, but it also re-runs every
            // [InitializeOnLoad] static ctor and resets in-memory
            // state — so guard with an explicit confirm so the user
            // doesn't lose work mid-edit by clicking the toolbar
            // badge by accident.
            int count = RoslynRepl.Editor.Diagnostics.ReplDiagnostics.DynamicAssemblyCount;
            bool proceed = EditorUtility.DisplayDialog(
                "Roslyn REPL — Force Domain Reload",
                $"Reload the script domain now to free {count} dynamic assembl{(count == 1 ? "y" : "ies")}?\n\n" +
                "This will:\n" +
                "  • drop every loaded ReplDynamic_* assembly,\n" +
                "  • reset the in-memory `_` carry-over,\n" +
                "  • re-run every [InitializeOnLoad] static constructor.\n\n" +
                "Save your work first. The reload takes a few seconds.",
                "Reload now",
                "Cancel");
            if (!proceed) return;
            EditorUtility.RequestScriptReload();
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

            // PR-review followup on #27: Load() collapses both
            // file-missing and file-corrupt / unreadable into an
            // empty list, so a stuck snippets.json / runHistory.json /
            // watches.json that we *can't* decode would otherwise
            // make the early-return below claim "Nothing to clear" —
            // and the user would think Reset succeeded while the
            // sensitive payload sat untouched on disk. Pull a
            // separate file-existence check (parallel to the patch
            // store's HasAny path) into the scope decision so a
            // corrupt file still routes through Clear.
            bool hasStaleSnippetFile = snippetCount == 0 && SnippetStore.HasAny();
            bool hasStaleHistoryFile = historyCount == 0 && RunHistoryStore.HasAny();
            bool hasStaleWatchFile   = watchCount   == 0 && WatchStore.HasAny();

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
                && !hasStaleSnippetFile && !hasStaleHistoryFile && !hasStaleWatchFile
                && compileCacheCount == 0)
            {
                EditorUtility.DisplayDialog(
                    "Roslyn REPL — Reset Project Data",
                    "Nothing to clear — all four stores are empty, no `_` carry-over is set, no Output panel has run results to wipe, no runtime method patches are active, and the compiled-watch cache is empty.",
                    "OK");
                return;
            }

            // Helper for the stale-file rows below — keeps the
            // "decoded N items" wording when there's content the
            // user expects to see, and switches to the explicit
            // "stale / unreadable file" framing when the file
            // exists but Load returned nothing. Without this the
            // confirm dialog would show "0 saved snippets" next to
            // a row that's about to delete a real file from disk,
            // and the user would have no idea why Reset is
            // touching it at all.
            string Describe(int decoded, bool stale, string singular, string plural)
            {
                if (stale && decoded == 0)
                    return $"the on-disk {singular} file (currently unreadable / cannot be decoded — wiped to recover)";
                return $"{decoded} {(decoded == 1 ? singular : plural)}";
            }

            var detail = new System.Text.StringBuilder();
            detail.Append("This will permanently delete the REPL data for the *current project*:\n");
            detail.Append($"  • {Describe(snippetCount, hasStaleSnippetFile, "saved snippet",      "saved snippets")}\n");
            detail.Append($"  • {Describe(historyCount, hasStaleHistoryFile, "run history entry",   "run history entries")}\n");
            detail.Append($"  • {Describe(watchCount,   hasStaleWatchFile,   "watch expression",    "watch expressions")}\n");
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
            //
            // The four file-backed Clear() calls return bool now so
            // we can tell when a stuck file (locked by an external
            // editor, missing permission) survived the delete. Issue
            // #27 PR-review followup: previously we fired Changed,
            // wiped the visible UI, and showed "Cleared N items"
            // even when snippets.json / patches.json was still on
            // disk — the next Load would resurrect the supposedly
            // cleared sensitive data and the user had no idea.
            // Aggregate the four results here and route into a
            // partial-failure dialog when any of them survived.
            var failedFiles = new List<string>(4);
            if (!SnippetStore.Clear())            failedFiles.Add("snippets.json");
            if (!RunHistoryStore.Clear())         failedFiles.Add("runHistory.json");
            if (!WatchStore.Clear())              failedFiles.Add("watches.json");
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
            if (!RoslynRepl.Editor.Patches.PatchRegistry.Clear()) failedFiles.Add("patches.json");

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
            if (failedFiles.Count > 0)
            {
                // Some files survived. Be explicit about which ones
                // and what the consequence is — those payloads are
                // back in memory the moment the user reopens the
                // panel because the next Load() reads them straight
                // off disk. Steer the user toward the actionable
                // remediation (close the holder, retry Reset, or
                // delete by hand) rather than burying the partial
                // failure in a console warning the dialog already
                // claims everything succeeded.
                var pathHint = RoslynRepl.Editor.Core.UserSettingsStorage
                    .ResolvePath(failedFiles[0]);
                pathHint = System.IO.Path.GetDirectoryName(pathHint);
                EditorUtility.DisplayDialog(
                    "Roslyn REPL — Reset Project Data (partial failure)",
                    $"In-memory state was cleared, but {failedFiles.Count} file{(failedFiles.Count == 1 ? "" : "s")} could not be deleted:\n\n" +
                    "  • " + string.Join("\n  • ", failedFiles) + "\n\n" +
                    $"Folder: {pathHint}\n\n" +
                    "These payloads will reload the next time the matching panel reads them. Close any external editor holding the file open (often the cause), then re-run Reset Project Data — or delete the listed files manually.\n\n" +
                    "Other reset targets (`_` carry-over, Output panels, Harmony detours, the compiled-watch cache) succeeded.",
                    "OK");
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "Roslyn REPL — Reset Project Data",
                    $"Cleared {reportedTotal} item{(reportedTotal == 1 ? "" : "s")} across snippet library, run history, watches, custom usings, the `_` carry-over, visible Output panels, runtime method patches, and the compiled-watch cache.",
                    "OK");
            }
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
            _patchBadge    = root.Q<Label>("patch-badge");
            _asmBadge      = root.Q<Label>("asm-badge");
            _outputSummary = root.Q<Label>("output-summary-label");

            // Issue #22: keep the toolbar badge live against
            // PatchRegistry mutations (Apply, Revert, Reset). Pure
            // Plain-C# event with no MonoBehaviour, so subscribing
            // here and unsubscribing in OnDisable is the matching
            // pair — CreateGUI re-runs on domain reload, so we
            // unhook first to avoid stacking handlers across
            // rebuilds. Same lesson as WatchPanelView's leak fix.
            RoslynRepl.Editor.Patches.PatchRegistry.Changed -= UpdatePatchBadge;
            RoslynRepl.Editor.Patches.PatchRegistry.Changed += UpdatePatchBadge;
            RoslynRepl.Editor.Patches.PatchAutoReapply.SettingsChanged -= UpdatePatchBadge;
            RoslynRepl.Editor.Patches.PatchAutoReapply.SettingsChanged += UpdatePatchBadge;
            if (_patchBadge != null)
            {
                _patchBadge.RegisterCallback<MouseDownEvent>(_ => SetPatchesModeActive(true));
                _patchBadge.tooltip =
                    "Click to open the Patches view.\n" +
                    "Shows the count of currently active runtime patches that\n" +
                    "are diverting Editor / Play Mode behavior. Source files are\n" +
                    "unchanged — Apply ↔ Revert from the Patches view.";
            }
            UpdatePatchBadge();

            // Issue #24 surface: count of dynamic assemblies the
            // engine has loaded since the last domain reload. The
            // AssemblyCountChanged event fires on every load, so a
            // single subscribe is enough — no polling.
            RoslynRepl.Editor.Diagnostics.ReplDiagnostics.AssemblyCountChanged -= UpdateAsmBadge;
            RoslynRepl.Editor.Diagnostics.ReplDiagnostics.AssemblyCountChanged += UpdateAsmBadge;
            if (_asmBadge != null)
            {
                _asmBadge.RegisterCallback<MouseDownEvent>(_ => ForceDomainReload());
            }
            UpdateAsmBadge();

            // Issue #59: keep the toolbar `_` badge in sync with the
            // engine's carry-over value. Subscribe / re-subscribe in
            // BindControls so a CreateGUI rebuild after a domain
            // reload picks up the live state, and unhook first to
            // avoid stacking handlers across rebuilds (same pattern
            // as the patch-registry and assembly-count badges above).
            _underscoreHost     = root.Q<VisualElement>("underscore-host");
            _underscoreBadge    = root.Q<Label>("underscore-badge");
            _underscoreClearBtn = root.Q<Button>("underscore-clear-btn");
            ReplEngine.LastResultChanged -= OnLastResultChanged;
            ReplEngine.LastResultChanged += OnLastResultChanged;
            if (_underscoreBadge != null)
            {
                // Click the label to re-render the current `_` value
                // back into Output as a fresh tree — same shape Browse
                // emits — so the user can re-inspect without having to
                // type `return _;` themselves.
                _underscoreBadge.RegisterCallback<MouseDownEvent>(_ => ReinspectUnderscore());
                _underscoreBadge.tooltip =
                    "Current `_` target.\n" +
                    "Click to re-inspect the value in Output.\n" +
                    "✕ clears the carry-over so the next snippet sees `_` as null.\n\n" +
                    "Use `_` directly in Code or Watch:\n" +
                    "    return _;\n" +
                    "    _.someField";
            }
            if (_underscoreClearBtn != null)
            {
                _underscoreClearBtn.clicked += ClearUnderscore;
                _underscoreClearBtn.tooltip = "Clear the current `_` value.";
            }
            UpdateUnderscoreBadge();

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
                // The patches pane lives behind an Output/Patches
                // mode tab; a Find hit that targets a Patches row
                // would otherwise scroll a display:none element and
                // do nothing visible. The callback flips the mode
                // to Patches before ScrollIntoView fires.
                _patchView.OnFocusRequested = () => SetPatchesModeActive(true);
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
                // Issue #60: right-click context menu actions on
                // browser rows (Inspect / Set as `_` / Patch Method /
                // copy helpers). Double-click stays on the existing
                // mode-aware OnInstanceChosen path so muscle memory
                // doesn't change.
                _browser.OnRowAction += OnBrowserRowAction;
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

            // Ctrl+F find overlay. The output findable lives for the
            // entire window lifetime — every AppendOutput /
            // AppendResult / ClearOutput mirrors into it. The watch
            // + patch views implement IReplFindable themselves so
            // they can scroll-and-highlight rows from their own
            // internal handles. The overlay sits above the toolbar,
            // hidden until Ctrl+F.
            _findController?.UnregisterAllSources();
            _findOverlay?.Dispose();
            _findController = new ReplFindController();
            _outputFindable = new OutputFindable(_outputScroll);
            _findController.RegisterSource(_outputFindable);
            if (_watch != null)     _findController.RegisterSource(_watch);
            if (_patchView != null) _findController.RegisterSource(_patchView);
            _findOverlay = new ReplFindOverlay(_findController);
            // Insert the bar at index 0 of the .rr-root so it shows
            // above the existing toolbar. The bar is display:none by
            // default; Ctrl+F flips it to flex.
            var rootContainer = root.Q<VisualElement>(className: "rr-root") ?? root;
            rootContainer.Insert(0, _findOverlay.Root);

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
            if (runBtn != null)
            {
                // Match the persistent banner under the Code header so
                // hover-discoverable surfaces all carry the same
                // cooperative-cancel reminder. The first-Run dialog is
                // the loud version once per workstation; tooltip + banner
                // cover every subsequent Run.
                runBtn.tooltip = CoopWarningTooltip;
                runBtn.clicked += Run;
            }

            // Same reminder under the Code header — match tooltip text
            // so a user mousing over the warning row gets the
            // ct-throwifcancellation-style call-out without leaving the
            // pane.
            var runWarning = root.Q<Label>("run-warning-label");
            if (runWarning != null) runWarning.tooltip = CoopWarningTooltip;

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
            RoslynRepl.Editor.Patches.PatchRegistry.Changed -= UpdatePatchBadge;
            RoslynRepl.Editor.Patches.PatchAutoReapply.SettingsChanged -= UpdatePatchBadge;
            RoslynRepl.Editor.Diagnostics.ReplDiagnostics.AssemblyCountChanged -= UpdateAsmBadge;
            ReplEngine.LastResultChanged -= OnLastResultChanged;
            // Drop the watch panel's WatchStore subscription so this
            // window's instance doesn't keep refreshing in the
            // background after it's closed (or before it's rebuilt by
            // the next CreateGUI).
            _watch?.Dispose();
            _watch = null;
            _patchView?.Dispose();
            _patchView = null;
            _findOverlay?.Dispose();
            _findOverlay = null;
            _findController?.UnregisterAllSources();
            _findController = null;
            _outputFindable = null;
        }

        private void UpdatePatchBadge()
        {
            if (_patchBadge == null) return;

            // PatchRegistry.GetDisplayState owns the rule for what
            // each spec is rendered as. We just count the two states
            // the toolbar cares about. Active specs that aren't
            // really installed (auto-off, manual registry mutation)
            // come back as DormantAutoOff and contribute to the
            // auto-off count, never the active count — so the badge
            // can never claim "N active" for rows that don't have a
            // live detour.
            int activeCount = 0;
            int dormantCount = 0;
            foreach (var spec in RoslynRepl.Editor.Patches.PatchRegistry.Specs)
            {
                switch (RoslynRepl.Editor.Patches.PatchRegistry.GetDisplayState(spec))
                {
                    case RoslynRepl.Editor.Patches.PatchDisplayState.Active:         activeCount++; break;
                    case RoslynRepl.Editor.Patches.PatchDisplayState.DormantAutoOff: dormantCount++; break;
                }
            }

            if (activeCount > 0)
            {
                _patchBadge.text = $"🔧 {activeCount} active";
                _patchBadge.RemoveFromClassList("rr-patch-badge--auto-off");
                _patchBadge.style.display = DisplayStyle.Flex;
            }
            else if (dormantCount > 0)
            {
                _patchBadge.text = $"🔧 {dormantCount} (auto-off)";
                _patchBadge.AddToClassList("rr-patch-badge--auto-off");
                _patchBadge.style.display = DisplayStyle.Flex;
            }
            else
            {
                _patchBadge.style.display = DisplayStyle.None;
            }
        }

        private void UpdateAsmBadge()
        {
            if (_asmBadge == null) return;
            int count = RoslynRepl.Editor.Diagnostics.ReplDiagnostics.DynamicAssemblyCount;
            if (count == 0)
            {
                _asmBadge.style.display = DisplayStyle.None;
                return;
            }

            _asmBadge.text = $"💾 {count} asm";
            _asmBadge.RemoveFromClassList("rr-asm-badge--warn");
            _asmBadge.RemoveFromClassList("rr-asm-badge--high");

            var severity = RoslynRepl.Editor.Diagnostics.ReplDiagnostics.SeverityOf(count);
            switch (severity)
            {
                case RoslynRepl.Editor.Diagnostics.AssemblyLoadSeverity.High:
                    _asmBadge.AddToClassList("rr-asm-badge--high");
                    _asmBadge.tooltip =
                        $"{count} ReplDynamic_* assemblies loaded since the last domain reload " +
                        $"(at or above the {RoslynRepl.Editor.Diagnostics.ReplDiagnostics.HighThreshold} threshold).\n" +
                        "Memory will keep growing every Run / Watch refresh / Apply Patch until reload.\n" +
                        "Click to open the Force Domain Reload confirm dialog.";
                    break;
                case RoslynRepl.Editor.Diagnostics.AssemblyLoadSeverity.Warn:
                    _asmBadge.AddToClassList("rr-asm-badge--warn");
                    _asmBadge.tooltip =
                        $"{count} ReplDynamic_* assemblies loaded since the last domain reload " +
                        $"(at or above the {RoslynRepl.Editor.Diagnostics.ReplDiagnostics.WarnThreshold} hint level).\n" +
                        "Mono can't unload these — they release on script recompile, Play Mode toggle, or Force Domain Reload.\n" +
                        "Click to open the reload confirm dialog.";
                    break;
                default:
                    _asmBadge.tooltip =
                        $"{count} ReplDynamic_* assemblies loaded since the last domain reload.\n" +
                        "These release on script recompile, Play Mode toggle, or Force Domain Reload.\n" +
                        "Click to open the reload confirm dialog.";
                    break;
            }

            _asmBadge.style.display = DisplayStyle.Flex;
        }

        // Issue #59: keep the toolbar `_` pill aligned with
        // ReplEngine.LastResult. Wired in BindControls; fires every
        // time the engine transitions LastResult (Execute, Browse-
        // inspect, ResetLastResult, Reset Project Data). The
        // argument isn't read directly — we re-format off the live
        // LastResult so a races-with-rebuild scenario can't print a
        // stale value.
        private void OnLastResultChanged(object _) => UpdateUnderscoreBadge();

        private void UpdateUnderscoreBadge()
        {
            if (_underscoreHost == null || _underscoreBadge == null) return;
            var value = ReplEngine.LastResult;
            if (value == null)
            {
                _underscoreHost.style.display = DisplayStyle.None;
                return;
            }
            _underscoreBadge.text = FormatUnderscoreBadge(value);
            _underscoreHost.style.display = DisplayStyle.Flex;
        }

        // "_ : Player (GameObject)" / "_ : 42 (Int32)" / "_ : (destroyed)".
        // Cap the value preview at 24 chars so a long ToString() can't
        // push the version label off the toolbar. UnityEngine.Object
        // values use .name when present (matches how the Object
        // Browser labels them); plain CLR objects use ToString().
        // Type name is GetType().Name (unqualified) — full namespace
        // chains would blow the budget on something like
        // System.Collections.Generic.Dictionary`2.
        private static string FormatUnderscoreBadge(object value)
        {
            if (value == null) return "_ : null";

            string label;
            string typeName;
            if (value is UnityEngine.Object uo)
            {
                // Unity overloads `==` so a destroyed instance is
                // == null. Touching .name on a destroyed object
                // throws MissingReferenceException, so probe via
                // the overloaded operator first.
                if (uo == null) return "_ : (destroyed)";
                try { label = uo.name; }
                catch { return "_ : (destroyed)"; }
                if (string.IsNullOrEmpty(label)) label = "(unnamed)";
                typeName = value.GetType().Name;
            }
            else
            {
                try { label = value.ToString() ?? string.Empty; }
                catch { label = "(ToString threw)"; }
                typeName = value.GetType().Name;
            }

            // Single-line + length-cap. Newlines inside ToString()
            // output would visibly break the toolbar row otherwise.
            label = label.Replace('\n', ' ').Replace('\r', ' ');
            if (label.Length > 24) label = label.Substring(0, 23) + "…";

            return $"_ : {label} ({typeName})";
        }

        // Re-render the live `_` value into Output as a fresh tree.
        // Mirrors the Browse-inspect shape (Clear + info breadcrumb +
        // tree) so the user sees the same layout they'd get from
        // double-clicking the source row. Skips when nothing is
        // carried over — the badge is hidden in that case anyway,
        // but a paranoid click via tooltip / keyboard could still
        // land here.
        private void ReinspectUnderscore()
        {
            var value = ReplEngine.LastResult;
            if (value == null) return;
            if (_outputContent == null) return;

            ClearOutput();
            AppendOutput($"▼ Inspect `_`: {value.GetType().Name}", "info");
            AppendResult(SimpleObjectSerializer.ToTree(value, BuildOutputTreeOptions()));
            if (_durationLabel != null) _durationLabel.text = string.Empty;
            if (_outputSummary != null) _outputSummary.text = "Inspect `_`";
            _watch?.Refresh();
            ScrollOutputToBottom();
            _outputFindable?.RaiseRebuilt();
        }

        // Drop the carry-over. Routes through ReplEngine.ResetLast
        // Result so the LastResultChanged event fires and every
        // subscriber (this badge plus anything else that registers in
        // the future) updates in lockstep.
        private void ClearUnderscore() => ReplEngine.ResetLastResult();

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
                return;
            }

            // Ctrl+F: open the Find overlay and put the caret in its
            // input. Bound here on TrickleDown so it fires before
            // the code editor / watch input fields swallow the key.
            // Cmd+F on macOS goes through evt.commandKey; cover both.
            bool isFindShortcut =
                (evt.ctrlKey || evt.commandKey)
                && !evt.altKey
                && evt.keyCode == KeyCode.F;
            if (isFindShortcut)
            {
                _findOverlay?.Show();
                evt.StopPropagation();
                evt.PreventDefault();
                return;
            }

            // While the Find overlay is open, dedicated search
            // shortcuts steer navigation from anywhere in the window:
            //   F3 / Shift+F3 → Next / Prev
            //   Esc            → close
            // Enter is *deliberately* not in this list. The Find
            // input has its own Enter handler (overlay
            // OnInputKeyDown), but at the window level we leave Enter
            // alone so that typing into the Patches body, the code
            // editor, the Watch input, or any other multi-line /
            // commit-on-Enter field still does what the user
            // expects. F3 fills the "advance Find regardless of
            // focus" role without colliding with text entry.
            if (_findController != null && _findController.IsActive)
            {
                bool isF3 = evt.keyCode == KeyCode.F3
                            && !evt.ctrlKey && !evt.commandKey && !evt.altKey;
                if (isF3)
                {
                    if (evt.shiftKey) _findController.Prev();
                    else _findController.Next();
                    evt.StopPropagation();
                    evt.PreventDefault();
                    return;
                }
                if (evt.keyCode == KeyCode.Escape)
                {
                    _findOverlay?.Hide();
                    evt.StopPropagation();
                    evt.PreventDefault();
                    return;
                }
            }
        }

        private void Run()
        {
            if (_codeEditor == null || _outputContent == null) return;

            // Issue #20 acceptance criterion (OR branch): warn the user
            // about the cooperative-only cancel model before the very
            // first Run on this workstation. Once acknowledged, the
            // permanent banner under the Code header + the Run button
            // tooltip carry the reminder forward; we don't re-prompt on
            // every Run. Cancel aborts this Run only — the user can
            // edit the code and try again, and the dialog still fires
            // until they explicitly OK it.
            if (!EditorPrefs.GetBool(PrefsKey_CoopCancelAcknowledged, false))
            {
                bool proceed = EditorUtility.DisplayDialog(
                    "Roslyn REPL — Cooperative cancel only",
                    "Snippets execute synchronously on the Unity Editor's main thread.\n\n" +
                    "The 5-second timeout (and any external Cancel) only fire when your code observes " +
                    "the cancellation token `ct`. Patterns like `while (true) {}` or any blocking call " +
                    "without a `ct.ThrowIfCancellationRequested()` check will freeze the Editor and may " +
                    "require force-quitting Unity.\n\n" +
                    "This dialog only appears the first time on this machine. The yellow banner under " +
                    "the Code header keeps the reminder visible going forward.",
                    "I understand — run it",
                    "Cancel");
                if (!proceed)
                {
                    AppendOutput("(Run cancelled — ack the cooperative-cancel warning to continue.)", "info");
                    return;
                }
                EditorPrefs.SetBool(PrefsKey_CoopCancelAcknowledged, true);
            }

            var code = _codeEditor.value ?? string.Empty;
            ClearOutput();
            AppendOutput($"▶ Running ({code.Length} chars)…", "info");

            // Pull defaults + user-added usings on every run so changes made
            // in the Usings editor (which writes EditorPrefs synchronously)
            // take effect immediately, no window restart required.
            var options = new ReplOptions { Usings = UsingsStore.EffectiveUsings() };

            // History push happens up front (regardless of where
            // Execute fires) so a Run that fails or is marshalled
            // a frame later still shows up in History immediately.
            RunHistoryStore.Push(code);

            // Player-frame marshal. When the toggle is on AND the
            // editor is in Play Mode, the actual Execute is deferred
            // to a coroutine that yields one player frame so the
            // invocation lands inside the next Player Update — same
            // phase a real Button.onClick fires from. Without this,
            // popup spawns / canvas updates / SuperScrollView Init
            // see stale layout state and snippet results don't
            // match what calling the same code from a button does.
            // Edit Mode skips the marshal automatically (no Player
            // Update to wait for) and runs synchronously.
            if (RunOnPlayerFrameEnabled && EditorApplication.isPlaying)
            {
                ReplEngine.ExecuteOnPlayerFrame(code, options, OnRunComplete);
            }
            else
            {
                OnRunComplete(ReplEngine.Execute(code, options));
            }
        }

        private void OnRunComplete(ReplResult result)
        {
            RenderResult(result);
            // Re-evaluate every watched expression after the user's
            // run lands, so values reflect any side effects the
            // snippet produced (e.g. mutating a manager state
            // visible to a watch).
            _watch?.Refresh();
        }

        // Double-click on a browser row routes through different paths
        // depending on the lower pane's mode. Pull UI: in Patches
        // mode the click means "I want to patch a method on this
        // type's class" — open the method picker instead of the usual
        // Double-click handler — preserves the mode-aware behaviour
        // the panel had before #60: Patches mode opens the method
        // picker, Output mode renders the inspect tree. Refactored
        // to delegate to the same helpers the new context-menu
        // actions call, so both entry points stay in lockstep.
        private void OnBrowserInstanceChosen(InstanceEntry entry)
        {
            if (entry == null) return;
            if (_patchesModeActive)
            {
                OpenBrowserPatchMethod(entry);
            }
            else
            {
                RenderBrowserInspect(entry);
            }
        }

        // Issue #60: dispatch for the row context-menu actions.
        // Inspect / SetAsUnderscore / PatchMethod reuse the same
        // helpers double-click drives so the menu can't drift away
        // from the existing flows. Copy actions write to the
        // clipboard and surface a confirmation in Output — enough of
        // a breadcrumb that the user knows the click landed without
        // having to switch focus to a paste target.
        private void OnBrowserRowAction(InstanceEntry entry, ObjectBrowserView.BrowserRowAction action)
        {
            if (entry == null) return;
            switch (action)
            {
                case ObjectBrowserView.BrowserRowAction.Inspect:
                    RenderBrowserInspect(entry);
                    break;
                case ObjectBrowserView.BrowserRowAction.SetAsUnderscore:
                    SetBrowserEntryAsUnderscore(entry);
                    break;
                case ObjectBrowserView.BrowserRowAction.PatchMethod:
                    OpenBrowserPatchMethod(entry);
                    break;
                case ObjectBrowserView.BrowserRowAction.CopyTypeName:
                    CopyBrowserEntryTypeName(entry);
                    break;
                case ObjectBrowserView.BrowserRowAction.CopyInspectSnippet:
                    CopyBrowserInspectSnippet(entry);
                    break;
            }
        }

        // Shared inspect path — clear Output, breadcrumb, bind `_`,
        // render the tree, refresh watches, scroll, raise the find
        // overlay's rebuild signal. Same shape both double-click in
        // Output mode and the Inspect context-menu action emit.
        private void RenderBrowserInspect(InstanceEntry entry)
        {
            if (entry == null) return;
            object value = entry.Value;
            if (value is UnityEngine.Object uo && uo == null) value = null;
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
            // Issue #59 acceptance: surface that the inspected value
            // is now bound to `_`. Without this the user has to
            // notice the toolbar badge separately to realise their
            // next snippet / watch can reference the value through
            // `_`; emitting it inline next to the rendered tree
            // makes the carry-over discoverable from the same
            // glance as the inspection itself.
            //
            // The "Try: return _;" hint addresses the second leg of
            // the discoverability gap — a newer user might still not
            // realise "available as `_`" means they can literally
            // type `_` into the Code editor or a Watch expression.
            // One concrete example removes the guesswork.
            AppendOutput("→ available as `_` in Code and Watch. Try: return _;", "info");
            AppendResult(SimpleObjectSerializer.ToTree(value, BuildOutputTreeOptions()));
            if (_durationLabel != null) _durationLabel.text = string.Empty;
            if (_outputSummary != null) _outputSummary.text = "Browsed";
            _watch?.Refresh();
            ScrollOutputToBottom();
            // Same commit-point signal as RenderResult — let the
            // overlay refresh hits now that the Browse-inspect tree
            // is appended.
            _outputFindable?.RaiseRebuilt();
        }

        // Shared method-picker entry. Falls back to DeclaredType when
        // Value is null (singleton accessors that read a static
        // member but don't expose a live instance until the
        // accessor is actually called).
        private void OpenBrowserPatchMethod(InstanceEntry entry)
        {
            if (entry == null) return;
            object value = entry.Value;
            if (value is UnityEngine.Object uo && uo == null) value = null;

            if (value == null)
            {
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
        }

        // Set as `_` is Inspect-without-the-render: bind the carry
        // over so `_` resolves to this instance in the next snippet
        // / watch, leave Output alone. The toolbar badge picks the
        // change up through LastResultChanged so the user still gets
        // a visible confirmation without the Output panel scrolling.
        //
        // Watch refresh: the action's main use-case is "pin this
        // object so my `_.foo` watches track it from now on", so a
        // Watch panel left holding evaluations against the *previous*
        // `_` is the worst-case stale-data trap. Same _watch.Refresh
        // commit point that RenderBrowserInspect uses pulls the
        // expressions through the new value immediately.
        private void SetBrowserEntryAsUnderscore(InstanceEntry entry)
        {
            if (entry == null) return;
            object value = entry.Value;
            if (value is UnityEngine.Object uo && uo == null) value = null;
            if (value == null)
            {
                AppendOutput("(can't set `_` — instance is null or destroyed)", "warning");
                return;
            }
            ReplEngine.SetLastResult(value);
            _watch?.Refresh();
            if (_outputSummary != null) _outputSummary.text = "Bound `_`";
        }

        private void CopyBrowserEntryTypeName(InstanceEntry entry)
        {
            if (entry == null) return;
            // Prefer the live runtime type (handles cases where the
            // declared type is an abstract base and the row was
            // surfaced for a concrete subclass); fall back to
            // DeclaredType and finally to the display TypeName.
            //
            // Render through CSharpTypeName so nested types come out
            // as `Outer.Inner` instead of the reflection `Outer+Inner`
            // form, and closed generics resolve to `List<int>` rather
            // than the assembly-qualified backtick syntax. Falls back
            // to the display TypeName when the type isn't renderable
            // (open generic parameters etc.) — that's a display string
            // and not guaranteed valid C#, but it's the best we can
            // offer when the type itself can't be expressed as source.
            var type = entry.Value?.GetType() ?? entry.DeclaredType;
            string name = null;
            if (type != null && CSharpTypeName.IsRenderable(type))
            {
                name = CSharpTypeName.Render(type);
            }
            if (string.IsNullOrEmpty(name)) name = entry.TypeName;
            if (string.IsNullOrEmpty(name))
            {
                AppendOutput("(can't copy type — no type information available)", "warning");
                return;
            }
            EditorGUIUtility.systemCopyBuffer = name;
            AppendOutput($"📋 Copied type name: {name}", "info");
        }

        private void CopyBrowserInspectSnippet(InstanceEntry entry)
        {
            if (entry == null) return;
            var snippet = BuildBrowserInspectSnippet(entry);
            if (string.IsNullOrEmpty(snippet))
            {
                AppendOutput("(can't build snippet — no type information available)", "warning");
                return;
            }
            EditorGUIUtility.systemCopyBuffer = snippet;
            AppendOutput("📋 Copied inspect snippet — paste into Code or a Watch row.", "info");
        }

        // Build a small C# snippet that re-locates the instance.
        // Category drives the shape: MonoBehaviour rows use the
        // scene-wide search (FindFirstObjectByType), ScriptableObject
        // rows use the row's actual asset path (the previous
        // type-wide FindAssets("t:T") would land on whichever asset
        // came first in the search — wrong row for projects with
        // multiple SOs of the same type), singletons get a comment-
        // only template since we can't infer the accessor name.
        //
        // Type expressions go through CSharpTypeName so nested /
        // generic types come out as valid C# source — the reflection
        // FullName form (Outer+Inner, generic backticks, assembly-
        // qualified arguments) doesn't compile when pasted. Bail
        // with a null result when no real Type is available or the
        // type isn't renderable as source; the menu surface gates
        // this action behind the same check so a user can't ask for
        // a snippet that would fail to copy.
        private static string BuildBrowserInspectSnippet(InstanceEntry entry)
        {
            var type = entry.Value?.GetType() ?? entry.DeclaredType;
            if (type == null) return null;
            if (!CSharpTypeName.IsRenderable(type)) return null;
            string typeExpr = CSharpTypeName.Render(type);

            switch (entry.Category)
            {
                case InstanceCategory.MonoBehaviour:
                    return $"return UnityEngine.Object.FindFirstObjectByType<{typeExpr}>();";

                case InstanceCategory.ScriptableObject:
                    // Path-based snippet when the row's live value is
                    // an on-disk asset. Sub-asset entries (e.g. a
                    // baked material packed inside an FBX) live at
                    // the same path as their main asset, so
                    // LoadAssetAtPath would return the wrong object;
                    // filter by name through LoadAllAssetsAtPath
                    // instead. The type-wide template is only used
                    // when there's no path at all (a ScriptableObject
                    // created in-memory via CreateInstance and
                    // surfaced through a singleton-like accessor).
                    if (entry.Value is UnityEngine.Object soAsset && soAsset != null)
                    {
                        var assetPath = UnityEditor.AssetDatabase.GetAssetPath(soAsset);
                        if (!string.IsNullOrEmpty(assetPath))
                        {
                            string verbatimPath = EscapeVerbatimStringLiteral(assetPath);
                            if (UnityEditor.AssetDatabase.IsMainAsset(soAsset))
                            {
                                return $"return UnityEditor.AssetDatabase.LoadAssetAtPath<{typeExpr}>(@\"{verbatimPath}\");";
                            }
                            string verbatimName = EscapeVerbatimStringLiteral(soAsset.name ?? string.Empty);
                            return
$@"return UnityEditor.AssetDatabase.LoadAllAssetsAtPath(@""{verbatimPath}"")
    .OfType<{typeExpr}>()
    .FirstOrDefault(a => a.name == @""{verbatimName}"");";
                        }
                    }
                    // No path — fall back to the type-wide template
                    // with a comment flagging the imprecision so the
                    // user knows the snippet may not pick the same
                    // row when multiple in-memory instances exist.
                    return
$@"// {typeExpr} has no asset path — this template returns the first match by type.
return UnityEditor.AssetDatabase.FindAssets(""t:{type.Name}"")
    .Select(g => UnityEditor.AssetDatabase.LoadAssetAtPath<{typeExpr}>(UnityEditor.AssetDatabase.GUIDToAssetPath(g)))
    .FirstOrDefault();";

                case InstanceCategory.Singleton:
                    // Locator scans for static accessors at scan
                    // time; we don't carry the accessor name through
                    // into the entry, so the snippet is a template
                    // with a clear TODO rather than a wrong default
                    // (e.g. assuming `.Instance` when the project
                    // uses `.I` / `.Singleton` / a property name).
                    return $"// {typeExpr} — replace with your project's accessor (e.g. {type.Name}.Instance)\nreturn null;";

                default:
                    return $"return UnityEngine.Object.FindFirstObjectByType<{typeExpr}>();";
            }
        }

        // Escape for a C# verbatim string literal (@"..."): only the
        // double-quote needs to be doubled. Backslashes / forward
        // slashes / dots pass through unchanged, which is exactly
        // what we want for asset paths (Unity always uses '/' even on
        // Windows, but a defensive escape keeps the snippet robust
        // against future format changes).
        private static string EscapeVerbatimStringLiteral(string s)
            => (s ?? string.Empty).Replace("\"", "\"\"");

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
                        AppendResult(SimpleObjectSerializer.ToTree(result.Value, BuildOutputTreeOptions()));
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
            // Signal the Find overlay (if open) that the visible
            // Output content has fully committed for this Run. Each
            // AppendOutput already mirrored into the findable, but
            // raising the event once at the end lets the overlay
            // recompute hits in one pass instead of N times per
            // log line.
            _outputFindable?.RaiseRebuilt();
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
            // Drop the findable's mirror in sync. RaiseRebuilt fires
            // an empty match set so the overlay (if open) updates
            // its 0 / 0 counter instead of holding stale hits whose
            // VisualElements are now detached.
            _outputFindable?.Clear();
            _outputFindable?.RaiseRebuilt();
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
            var label = new Label();
            label.AddToClassList("rr-output-line");
            label.AddToClassList($"rr-output-line--{severity}");
            label.style.whiteSpace = WhiteSpace.Normal;
            _outputContent.Add(label);
            // BindLabelText sets label.text via the Find overlay's
            // Decorate (wraps matching characters in rich-text when
            // a query is active) and subscribes the label to
            // re-decorate on query change. Cleanup on detach is
            // automatic.
            RoslynRepl.Editor.UI.Find.ReplFindHighlight.BindLabelText(label, text);
            // Mirror the line into the Find findable so the Ctrl+F
            // overlay can match against captured log text (the
            // VisualElement scan can't see virtualized tree rows, so
            // we use an explicit mirror everywhere for consistency).
            _outputFindable?.TrackLogLine(label);
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
            var header = new Label();
            header.AddToClassList("rr-output-line");
            header.AddToClassList("rr-output-line--result");
            _outputContent.Add(header);
            RoslynRepl.Editor.UI.Find.ReplFindHighlight.BindLabelText(header, $"=> {root.Preview}");
            _outputFindable?.TrackLogLine(header);

            var tv = BuildResultTree(root);
            tv.AddToClassList("rr-result-tree");
            _outputContent.Add(tv);
            // Hand the tree + its OutputTreeIndex (stashed on
            // tv.userData by BuildResultTree) to the findable so
            // Find can walk virtualized rows by id without
            // re-traversing the data per keystroke.
            _outputFindable?.TrackResultTree(tv, tv.userData as RoslynRepl.Editor.UI.Find.OutputTreeIndex);
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
            // Build the OutputTreeIndex in lockstep with the item
            // data so the Find overlay (Ctrl+F) can navigate to
            // virtualized rows by id without re-walking the tree on
            // every keystroke. Indices live on tv.userData.
            int nextId = 0;
            var findIndex = new RoslynRepl.Editor.UI.Find.OutputTreeIndex();
            var rootItems = new List<TreeViewItemData<ReplValueNode>>
            {
                ToItemData(root, ref nextId, parentRef: null, findIndex)
            };
            tv.SetRootItems(rootItems);
            tv.userData = findIndex;
            tv.Rebuild();
            tv.ExpandRootItems();
            // Find overlay: refresh visible rows whenever the
            // active query changes so the bind-cell call re-runs
            // with the new Decorate wrapping. Unsubscribed
            // automatically when the tv is detached.
            RoslynRepl.Editor.UI.Find.ReplFindHighlight.BindTreeRefresh(tv);
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
                // Rich-text on so the Find overlay's character-level
                // highlight wrapping shows up; without this the
                // <color> / <b> tags would render as literal text.
                lbl.enableRichText = true;
                return lbl;
            };
            col.bindCell = (ve, idx) =>
            {
                var node = tv.GetItemDataForIndex<ReplValueNode>(idx);
                // Decorate routes through the Find overlay's active
                // query when present, wrapping matching substrings
                // in rich-text. When no query is active Decorate is
                // a fast no-op pass-through.
                ((Label)ve).text = RoslynRepl.Editor.UI.Find.ReplFindHighlight.Decorate(getter(node));
            };
            return col;
        }

        private static TreeViewItemData<ReplValueNode> ToItemData(
            ReplValueNode node,
            ref int nextId,
            RoslynRepl.Editor.UI.Find.NodeRef parentRef,
            RoslynRepl.Editor.UI.Find.OutputTreeIndex findIndex)
        {
            int id = nextId++;
            var nref = new RoslynRepl.Editor.UI.Find.NodeRef
            {
                Node = node,
                Id = id,
                Parent = parentRef,
            };
            findIndex.Refs.Add(nref);
            var children = new List<TreeViewItemData<ReplValueNode>>();
            if (node.Children != null)
            {
                foreach (var c in node.Children)
                    children.Add(ToItemData(c, ref nextId, nref, findIndex));
            }
            return new TreeViewItemData<ReplValueNode>(id, node, children);
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
                var json = System.IO.File.ReadAllText(ReplPackagePaths.AssetPath("package.json"));
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
