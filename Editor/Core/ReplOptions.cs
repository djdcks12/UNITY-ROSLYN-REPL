using System.Collections.Generic;
using System.Threading;

namespace RoslynRepl.Editor.Core
{
    public class ReplOptions
    {
        public IReadOnlyList<string> Usings { get; set; } = DefaultUsings;

        /// <summary>
        /// Soft cancellation budget for the snippet, in milliseconds.
        /// 0 or negative disables the timer; otherwise the engine fires
        /// <c>CancellationTokenSource.CancelAfter</c> so user code that
        /// observes <c>ct</c> (the wrapper-class accessor) can bail out.
        /// Code that doesn't check <c>ct</c> still hangs the Editor —
        /// see README "Known limitations" for why a hard kill isn't
        /// available.
        /// </summary>
        public int TimeoutMs { get; set; } = 5000;

        /// <summary>
        /// Optional external cancellation source. The engine links it
        /// with the timeout token via <c>CreateLinkedTokenSource</c>, so
        /// either trigger cancels the snippet.
        /// </summary>
        public CancellationToken ExternalCancellation { get; set; } = CancellationToken.None;

        /// <summary>
        /// When <c>true</c> (the default), a successful non-null return
        /// value is recorded into <see cref="ReplEngine.LastResult"/> so
        /// the next snippet can read it as <c>_</c>. Background callers
        /// that observe values without "running" the user's REPL — most
        /// notably the Watch panel — set this to <c>false</c> so a
        /// passive evaluation can't quietly mutate the user's
        /// previous-result state.
        /// </summary>
        public bool UpdateLastResult { get; set; } = true;

        /// <summary>
        /// When <c>true</c>, the engine reuses the dynamic assembly +
        /// entry-point <see cref="System.Reflection.MethodInfo"/> from a
        /// previous Execute call whose wrapped source matches byte-for-
        /// byte. The cache is keyed on the full wrapped source — usings
        /// included — so a change in either user code or
        /// <see cref="Usings"/> produces a fresh compile.
        ///
        /// Off by default. The Watch panel turns it on so a refresh of
        /// N rows over many user Runs amortizes to one compile per row
        /// rather than N×Run compiles. Interactive snippets leave it
        /// off so each Run is a fresh compile against the latest editor
        /// state — the cache invalidates on AppDomain.AssemblyLoad
        /// anyway, but explicit "compile every Run" matches the user
        /// mental model for the main editor.
        ///
        /// Side effect: cache hits skip Wrap → Parse → Compile → Emit →
        /// Load entirely; only Invoke runs. Compile errors therefore
        /// can't surface from cache hits — they only happen on first
        /// compile (which goes through the normal path).
        /// </summary>
        public bool UseCompileCache { get; set; } = false;

        public static IReadOnlyList<string> DefaultUsings { get; } = new[]
        {
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "UnityEngine",
            "UnityEditor",
        };
    }
}
