using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// Bootstraps the runtime patch list on every Editor session /
    /// domain reload. Phase B2 made the registry persistent; this
    /// class is the consumer side that pulls the persisted specs
    /// back in and re-installs the Harmony detours that were active
    /// the last time the user touched them.
    ///
    /// Lifecycle:
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
        static PatchAutoReapply()
        {
            // delayCall fires after the current editor frame, by which
            // point [InitializeOnLoad] static ctors of every other
            // package (Roslyn cache warm-up, Harmony binder type info,
            // …) have run and EditorUtility/Debug calls are safe.
            EditorApplication.delayCall += Bootstrap;
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
            var toReapply = PatchRegistry.Specs
                .Where(s => s != null && s.Status == PatchStatus.Active)
                .ToList();

            if (toReapply.Count == 0) return;

            int ok = 0, failed = 0;
            foreach (var spec in toReapply)
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
            Debug.Log($"[Roslyn REPL] Runtime patches: {ok} re-applied, {failed} failed.");
        }
    }
}
