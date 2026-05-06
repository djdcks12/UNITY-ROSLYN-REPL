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
