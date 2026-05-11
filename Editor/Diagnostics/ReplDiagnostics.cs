using System;
using System.Threading;
using UnityEditor;

namespace RoslynRepl.Editor.Diagnostics
{
    /// <summary>
    /// Live-process diagnostics for the dynamic assemblies the REPL
    /// engine and runtime patcher emit through
    /// <c>Assembly.Load(byte[])</c>. Mono / .NET Standard 2.1 has no
    /// CollectibleAssemblyLoadContext, so every Run, every Watch
    /// refresh that misses the compile cache, and every Apply Patch
    /// adds an assembly that can only be unloaded by reloading the
    /// editor's AppDomain. Long sessions with frequent Runs accumulate
    /// MB-range memory that's invisible until somebody profiles the
    /// process. Issue #24's fix scope is to make that drift visible
    /// (a count + an event) and to give the user a one-click way to
    /// shed the assemblies (Force Domain Reload menu, surfaced from
    /// the toolbar badge as well).
    ///
    /// Two engine-emitted prefixes count toward the total — the REPL
    /// snippet path uses <c>ReplDynamic_*</c>, the runtime patch path
    /// uses <c>ReplPatch_*</c>. <see cref="IsReplGeneratedAssembly"/>
    /// is the canonical predicate; every counter and every cache
    /// invalidation gate goes through it so a future prefix rename
    /// only edits one place.
    ///
    /// Thresholds are deliberately conservative — `WarnThreshold` is
    /// the point at which the toolbar badge picks up the yellow
    /// auto-off styling to call attention, `HighThreshold` is when
    /// the user really should reload before continuing. The numbers
    /// match a tens-of-MB vs hundreds-of-MB estimate (a typical
    /// engine-emitted assembly is 30–80 KB depending on snippet
    /// size) and can be tuned later without breaking callers.
    /// </summary>
    [InitializeOnLoad]
    public static class ReplDiagnostics
    {
        public const int WarnThreshold = 200;
        public const int HighThreshold = 500;
        public const string DynamicAssemblyPrefix = "ReplDynamic_";
        public const string PatchAssemblyPrefix   = "ReplPatch_";

        /// <summary>
        /// Fires on each editor frame after one or more
        /// <see cref="AppDomain.AssemblyLoad"/> events have come in
        /// since the last drain. Coalesced and raised on the main
        /// thread (see the marshalling note in the static ctor) so
        /// UI Toolkit subscribers can mutate VisualElements without
        /// extra dispatch glue.
        /// </summary>
        public static event Action AssemblyCountChanged;

        // Reviewer-driven (PR review for #24): AppDomain.AssemblyLoad
        // doesn't promise the loader thread is the Unity main
        // thread. Calling subscribers (and the UI Toolkit code they
        // touch) directly from that callback is unsafe — a Mono
        // background loader would race the editor frame and Unity
        // tools that aren't thread-safe would either throw under
        // the existing try/catch (silently dropping the repaint) or
        // corrupt state. Pattern: the loader-thread callback only
        // bumps an Interlocked counter; an EditorApplication.update
        // tick on the main thread drains the counter and invokes
        // subscribers. Coalescing means N rapid loads (Apply Patch
        // → its rewrite passes → its dependent assemblies) collapse
        // into one repaint, which is also the cheapest thing we
        // could do.
        private static int _pendingLoads;

        static ReplDiagnostics()
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            EditorApplication.update += DrainPending;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            // Loader thread. Cheapest possible work — a single
            // interlocked increment. Anything beyond this gets
            // marshalled to the main thread by DrainPending.
            Interlocked.Increment(ref _pendingLoads);
        }

        private static void DrainPending()
        {
            // Editor frame, main thread. Atomic check-and-clear so a
            // load racing this drain still gets noticed on the next
            // frame instead of being lost between the read and the
            // reset.
            if (Interlocked.Exchange(ref _pendingLoads, 0) == 0) return;
            try { AssemblyCountChanged?.Invoke(); }
            catch { /* best-effort: subscriber faults must not stall the editor loop */ }
        }

        /// <summary>
        /// True for any assembly the REPL engine or runtime patcher
        /// emitted itself — the assemblies that can't unload until a
        /// domain reload, and the ones the badge / Verify Setup /
        /// Force Domain Reload dialog all need to count.
        /// </summary>
        public static bool IsReplGeneratedAssembly(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName)) return false;
            return assemblyName.StartsWith(DynamicAssemblyPrefix, StringComparison.Ordinal)
                || assemblyName.StartsWith(PatchAssemblyPrefix,   StringComparison.Ordinal);
        }

        /// <summary>
        /// Live count of engine-emitted assemblies in the current
        /// AppDomain. Includes both <c>ReplDynamic_*</c> (REPL Run /
        /// Watch refresh) and <c>ReplPatch_*</c> (Apply Patch). O(N)
        /// over loaded assemblies — fine for the editor toolbar's
        /// once-per-load refresh path; not suitable for inner loops.
        /// </summary>
        public static int DynamicAssemblyCount
        {
            get
            {
                int n = 0;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name;
                    try { name = a.GetName().Name ?? string.Empty; }
                    catch { continue; }
                    if (IsReplGeneratedAssembly(name)) n++;
                }
                return n;
            }
        }

        /// <summary>
        /// Pick a one-word severity for the given count. UI surfaces
        /// switch on this directly so the threshold rule lives in one
        /// place.
        /// </summary>
        public static AssemblyLoadSeverity SeverityOf(int count)
        {
            if (count >= HighThreshold) return AssemblyLoadSeverity.High;
            if (count >= WarnThreshold) return AssemblyLoadSeverity.Warn;
            return AssemblyLoadSeverity.Normal;
        }
    }

    public enum AssemblyLoadSeverity
    {
        /// <summary>Below <see cref="ReplDiagnostics.WarnThreshold"/>.</summary>
        Normal,
        /// <summary>At or above <see cref="ReplDiagnostics.WarnThreshold"/>.</summary>
        Warn,
        /// <summary>At or above <see cref="ReplDiagnostics.HighThreshold"/>.</summary>
        High,
    }
}
