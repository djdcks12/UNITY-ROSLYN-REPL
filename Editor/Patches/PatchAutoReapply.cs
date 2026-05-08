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
    /// don't disappear — but their status flips to Inactive and
    /// no detour is registered. The user must explicitly Apply
    /// from the UI to re-activate, which is the "explicit
    /// confirmation" half of the issue's acceptance criterion.
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

            // Auto-reapply gate. When the user has opted out, we
            // demote each Active spec to Inactive so the registry's
            // Status field reflects what's actually installed in the
            // process — no Harmony detour exists for any of these
            // specs after a fresh reload, and leaving them tagged
            // Active would be a worse lie than the original
            // silent-reapply problem this issue is about.
            //
            // The user can re-activate in two ways: flip the menu
            // toggle back on (next reload re-installs them) or open
            // the Patches view and click Apply on each row
            // explicitly. The Patches list keeps the spec text so no
            // authoring work is lost.
            if (!AutoReapplyEnabled)
            {
                foreach (var spec in activeSpecs)
                {
                    spec.Status = PatchStatus.Inactive;
                    spec.LastError = "Auto-reapply is disabled — Apply manually to re-install.";
                    PatchRegistry.AddOrUpdate(spec);
                }
                Debug.Log($"[Roslyn REPL] Runtime patches: auto-reapply disabled — {activeSpecs.Count} active spec(s) demoted to inactive on reload. Re-Apply from the Patches view to re-install, or toggle '{AutoReapplyMenuPath}' back on.");
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
