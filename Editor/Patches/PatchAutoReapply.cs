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
    /// don't disappear — but they're shown as Inactive in the live
    /// process *without* persisting that demotion. The persisted
    /// desired status stays Active, so flipping the menu back on
    /// makes the next reload re-install them automatically. The
    /// user can also click Apply per row to re-install during the
    /// current session. PR review for #22 caught the alternative
    /// (persisting the demotion) collapsing the toggle into a
    /// one-way bulk deactivation that lost the reapply intent.
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
                EditorPrefs.SetBool(PrefsKey_AutoReapplyEnabled, value);
                Menu.SetChecked(AutoReapplyMenuPath, value);
                SettingsChanged?.Invoke();
            }
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
            // install Harmony detours for any of these specs — but
            // we deliberately do NOT persist the demotion. Mutating
            // spec.Status in place updates the live registry view
            // (the Patches list and the toolbar badge both refresh
            // through PatchRegistry.Changed) while leaving the
            // persisted desired status as Active on disk. That
            // matters because the menu has to behave like a reload
            // policy, not a one-way bulk deactivation:
            //
            //   • toggle back on, next domain reload     → LoadFromPersistence
            //                                              still surfaces these
            //                                              specs as Active
            //                                              and the auto-on
            //                                              path re-installs.
            //   • toggle stays off, click Apply per row  → MethodPatchView
            //                                              routes through
            //                                              PatchEngine.Apply,
            //                                              which sets
            //                                              spec.Status =
            //                                              Active and persists
            //                                              normally. The
            //                                              user's explicit
            //                                              action overrides
            //                                              the in-memory
            //                                              opt-out for that
            //                                              row.
            //   • toggle stays off, click Revert per row → spec.Status =
            //                                              Inactive with
            //                                              persistence. That
            //                                              IS an explicit
            //                                              user intent and we
            //                                              honor it.
            if (!AutoReapplyEnabled)
            {
                foreach (var spec in activeSpecs)
                {
                    spec.Status = PatchStatus.Inactive;
                    spec.LastError = "Auto-reapply is off — toggle on for next reload, or click Apply to install now.";
                }
                // Notify-only: persistence layer is intentionally
                // bypassed. The disk still says Active for these
                // specs; only the live process treats them as
                // Inactive for this session.
                PatchRegistry.NotifyInMemoryMutation();
                Debug.Log($"[Roslyn REPL] Runtime patches: auto-reapply is off — {activeSpecs.Count} active spec(s) hidden in this session (persisted Active intent preserved). Toggle '{AutoReapplyMenuPath}' on for next reload, or click Apply per row to install now.");
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
