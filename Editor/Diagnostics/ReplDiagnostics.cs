using System;
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
    /// Thresholds are deliberately conservative — `WarnThreshold` is
    /// the point at which the toolbar badge picks up the yellow
    /// auto-off styling to call attention, `HighThreshold` is when
    /// the user really should reload before continuing. The numbers
    /// match a tens-of-MB vs hundreds-of-MB estimate (a typical
    /// `ReplDynamic_*` assembly is 30–80 KB depending on snippet
    /// size) and can be tuned later without breaking callers.
    /// </summary>
    [InitializeOnLoad]
    public static class ReplDiagnostics
    {
        public const int WarnThreshold = 200;
        public const int HighThreshold = 500;
        public const string DynamicAssemblyPrefix = "ReplDynamic_";

        /// <summary>
        /// Fires on every <see cref="AppDomain.AssemblyLoad"/> event,
        /// so subscribers can repaint without polling. The event is
        /// raised even for unrelated assembly loads (Unity's package
        /// resolution, satellite assemblies) — that's intentional, a
        /// reload of *any* assembly is information the diagnostic UI
        /// wants to surface.
        /// </summary>
        public static event Action AssemblyCountChanged;

        static ReplDiagnostics()
        {
            // Fixed hook (no opt-in flag) — diagnostics need to see
            // every load, regardless of whether the compile cache
            // happens to be on.
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            // Defensive try/catch: AssemblyLoad fires on the loader
            // thread, and an exception here would propagate into the
            // CLR loader. Subscriber faults must not take down the
            // editor.
            try { AssemblyCountChanged?.Invoke(); }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Live count of <c>ReplDynamic_*</c> assemblies loaded into
        /// the current AppDomain. O(N) over loaded assemblies — fine
        /// for the editor toolbar's once-per-load refresh path; not
        /// suitable for inner loops.
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
                    if (name.StartsWith(DynamicAssemblyPrefix, StringComparison.Ordinal))
                        n++;
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
