using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// Bootstraps the runtime patch list on every Editor session /
    /// domain reload. Persistence made the registry persistent; this
    /// class is the consumer side that pulls the persisted specs
    /// back in and re-installs the Harmony detours that were active
    /// the last time the user touched them.
    ///
    /// Auto-reapply is configurable (issue #22). Active specs are
    /// only re-installed as Harmony detours when
    /// <see cref="AutoReapplyEnabled"/> is true (the default, to
    /// preserve historical behavior). When the toggle is off, the
    /// specs still hydrate into the registry — the user's drafts
    /// don't disappear — but they're shadowed as dormant in the
    /// live process *without* persisting that state. The persisted
    /// <see cref="MethodPatchSpec.Status"/> stays Active, so
    /// flipping the toggle back on installs every dormant spec
    /// immediately (the setter calls
    /// <see cref="PatchEngine.Apply"/> on each Active+dormant spec
    /// and reports per-spec failures the same way the Bootstrap
    /// auto-on path does). The user can also click Apply per row
    /// to re-install one at a time without touching the toggle.
    ///
    /// Dormancy is tracked via
    /// <see cref="PatchRegistry.MarkSessionDormant"/> on a separate
    /// in-memory set keyed on <see cref="MethodPatchSpec.Key"/>.
    /// The earlier shape mutated <c>spec.Status</c> in place — and
    /// because the same instance lives in <c>_byKey</c>, the next
    /// unrelated registry write (Apply on a *different* spec,
    /// adding a draft, anything that runs Persist over
    /// <c>_byKey.Values</c>) would have serialized the in-memory
    /// Inactive into EditorPrefs and quietly lost the reapply
    /// intent. The session-set design keeps spec.Status pinned to
    /// the user's persisted desire and uses a separate live
    /// dormancy view for the auto-off opt-out, so unrelated
    /// session writes can't leak through.
    ///
    /// Lifecycle (auto-reapply on):
    ///  1. <see cref="EditorApplication.delayCall"/> defers the
    ///     bootstrap by one Editor frame so the UI / dialog APIs
    ///     are safely callable and the assembly reference cache
    ///     has had a chance to warm up.
    ///  2. <see cref="PatchRegistry.LoadFromPersistence"/> hydrates
    ///     the in-memory dictionary.
    ///  3. Every spec stored as <see cref="PatchStatus.Active"/> is
    ///     fed through <see cref="PatchEngine.Apply"/>. Successful
    ///     re-applies stay Active. Failures flip the spec to
    ///     <see cref="PatchStatus.Failed"/>, fill <c>LastError</c>
    ///     with the diagnostic, and persist the new status — the
    ///     next boot won't retry until the user explicitly Apply's
    ///     it again from the UI.
    ///
    /// Inactive / Failed specs are loaded into the registry too —
    /// the user authored them and probably wants the UI to remember
    /// their drafts — but they're left at their stored status, not
    /// re-applied.
    /// </summary>
    [InitializeOnLoad]
    public static class PatchAutoReapply
    {
        // EditorPrefs is machine-wide (not project-scoped) on
        // purpose: a developer who's decided they don't want patches
        // re-installed on every domain reload makes that call per
        // workstation, not per project. Two projects on the same
        // machine sharing the toggle is the right default; a team
        // member checking out the same project on a different box
        // gets the historical default (on) until they opt out.
        private const string PrefsKey_AutoReapplyEnabled = "RoslynRepl.PatchAutoReapplyEnabled";
        private const string AutoReapplyMenuPath = "Tools/Roslyn REPL/Auto-reapply Patches on Reload";

        /// <summary>
        /// Notifies subscribers when <see cref="AutoReapplyEnabled"/> flips.
        /// The window's Patches-count badge listens so its tooltip
        /// stays in sync with the menu state.
        /// </summary>
        public static event Action SettingsChanged;

        public static bool AutoReapplyEnabled
        {
            get => EditorPrefs.GetBool(PrefsKey_AutoReapplyEnabled, true);
            set
            {
                bool was = EditorPrefs.GetBool(PrefsKey_AutoReapplyEnabled, true);
                EditorPrefs.SetBool(PrefsKey_AutoReapplyEnabled, value);
                Menu.SetChecked(AutoReapplyMenuPath, value);

                // OFF → ON transition: install the dormant specs
                // immediately so the toggle tooltip + Patches-tab
                // copy ("toggle this back on … to install now") is
                // honest. The earlier shape only flipped the pref
                // and waited for the next domain reload's Bootstrap,
                // which left the row rendering as "(auto-off)" with
                // the toggle on — exactly the contradiction the PR
                // review caught. Apply failures are recorded as
                // Failed status the same way Bootstrap's auto-on
                // path handles them. SettingsChanged fires last so
                // UI subscribers see the updated dormancy state.
                if (!was && value)
                {
                    ReapplyDormantSpecs("Toggle on");
                }

                SettingsChanged?.Invoke();
            }
        }

        private static void ReapplyDormantSpecs(string trigger)
        {
            var dormantSpecs = PatchRegistry.Specs
                .Where(s => s != null
                         && s.Status == PatchStatus.Active
                         && PatchRegistry.IsSessionDormant(s.Key))
                .ToList();
            if (dormantSpecs.Count == 0) return;

            int ok = 0, failed = 0;
            foreach (var spec in dormantSpecs)
            {
                try
                {
                    PatchEngine.Apply(spec);
                    // Apply ends with PatchRegistry.AddOrUpdate,
                    // which clears the dormancy mark for this key
                    // — the spec is now genuinely live.
                    ok++;
                }
                catch (Exception ex)
                {
                    spec.Status = PatchStatus.Failed;
                    spec.LastError = $"{trigger} reapply failed: " + ex.Message;
                    PatchRegistry.AddOrUpdate(spec);
                    failed++;
                    Debug.LogWarning($"[Roslyn REPL] {trigger} reapply failed for {spec.TargetTypeName}.{spec.MethodName}: {ex.Message}");
                }
            }
            Debug.Log($"[Roslyn REPL] Runtime patches: {trigger.ToLowerInvariant()} re-installed {ok} dormant patch(es), {failed} failed.");
        }

        static PatchAutoReapply()
        {
            // delayCall fires after the current editor frame, by which
            // point [InitializeOnLoad] static ctors of every other
            // package (Roslyn cache warm-up, Harmony binder type info,
            // …) have run and EditorUtility/Debug calls are safe.
            EditorApplication.delayCall += Bootstrap;
            // Sync the menu checkmark with the persisted setting on
            // every domain reload — Menu.SetChecked doesn't survive
            // reload on its own.
            EditorApplication.delayCall += () => Menu.SetChecked(AutoReapplyMenuPath, AutoReapplyEnabled);
        }

        [MenuItem(AutoReapplyMenuPath, priority = 210)]
        private static void ToggleAutoReapply() => AutoReapplyEnabled = !AutoReapplyEnabled;

        [MenuItem(AutoReapplyMenuPath, validate = true)]
        private static bool ToggleAutoReapplyValidate()
        {
            // Validate fires before the menu opens; refresh the
            // checkmark so a flip from elsewhere (e.g. a programmatic
            // setter) shows the right state.
            Menu.SetChecked(AutoReapplyMenuPath, AutoReapplyEnabled);
            return true;
        }

        private static void Bootstrap()
        {
            try { PatchRegistry.LoadFromPersistence(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Roslyn REPL] Failed to load runtime patches from persistence: {ex.Message}");
                return;
            }

            // Snapshot — the loop below mutates registry state
            // (spec.Status / LastError) which would re-fire Changed
            // and re-enumerate the live collection.
            var activeSpecs = PatchRegistry.Specs
                .Where(s => s != null && s.Status == PatchStatus.Active)
                .ToList();

            if (activeSpecs.Count == 0) return;

            // Auto-reapply gate. The user has opted out, so we don't
            // install Harmony detours for any of these specs — and
            // crucially we never touch spec.Status, because the same
            // instance lives in PatchRegistry's _byKey and any later
            // session write (Apply on another spec, adding a draft,
            // Remove, Clear) calls Persist over _byKey.Values. If
            // we'd mutated Status, that next Persist would silently
            // serialize the in-memory Inactive into EditorPrefs and
            // we'd lose the reapply intent — the exact bug PR
            // review for #22 caught the previous round.
            //
            // Instead the dormancy is recorded on a separate set
            // keyed on spec.Key, and PatchRegistry.AddOrUpdate /
            // Remove / Clear drop the mark automatically when the
            // user takes an explicit action on that spec. The disk
            // continues to read Active throughout, so:
            //
            //   • toggle back on, same session          → AutoReapplyEnabled's
            //                                              setter detects the
            //                                              OFF→ON edge and runs
            //                                              ReapplyDormantSpecs,
            //                                              which Apply's every
            //                                              dormant spec right
            //                                              away. No reload
            //                                              required.
            //   • toggle back on, next domain reload     → LoadFromPersistence
            //                                              still reads Active
            //                                              and the auto-on
            //                                              path re-installs.
            //   • toggle stays off, click Apply per row  → AddOrUpdate
            //                                              clears the dormancy
            //                                              mark; Apply path
            //                                              persists Active
            //                                              normally.
            //   • toggle stays off, click Revert per row → AddOrUpdate
            //                                              clears the dormancy
            //                                              mark; Revert path
            //                                              persists Inactive.
            //                                              That's an explicit
            //                                              user intent and we
            //                                              honor it.
            //   • some other spec gets Apply / Revert /
            //     a draft save / a Remove                → unrelated registry
            //                                              writes; the
            //                                              dormant specs keep
            //                                              their Status =
            //                                              Active in memory
            //                                              (we never touched
            //                                              it) so Persist
            //                                              writes Active for
            //                                              them too.
            if (!AutoReapplyEnabled)
            {
                foreach (var spec in activeSpecs)
                {
                    PatchRegistry.MarkSessionDormant(spec.Key);
                }
                PatchRegistry.NotifyInMemoryMutation();
                Debug.Log($"[Roslyn REPL] Runtime patches: auto-reapply is off — {activeSpecs.Count} active spec(s) shown as dormant in this session (persisted Active intent preserved). Toggle '{AutoReapplyMenuPath}' (or the inline switch in the Patches view) back on to install every dormant row immediately, or click Apply per row.");
                return;
            }

            int ok = 0, failed = 0;
            foreach (var spec in activeSpecs)
            {
                try
                {
                    PatchEngine.Apply(spec);
                    ok++;
                }
                catch (Exception ex)
                {
                    spec.Status = PatchStatus.Failed;
                    spec.LastError = "Auto-reapply failed: " + ex.Message;
                    // AddOrUpdate also persists, so the next boot
                    // sees the Failed status and skips this spec.
                    PatchRegistry.AddOrUpdate(spec);
                    failed++;
                    Debug.LogWarning($"[Roslyn REPL] Auto-reapply failed for {spec.TargetTypeName}.{spec.MethodName}: {ex.Message}");
                }
            }

            // One summary log so users notice the redirects came
            // back without scrolling through per-row warnings (which
            // only appear on the failure path anyway).
            Debug.Log($"[Roslyn REPL] Runtime patches: {ok} re-applied, {failed} failed. (Toggle '{AutoReapplyMenuPath}' to disable.)");
        }
    }
}
