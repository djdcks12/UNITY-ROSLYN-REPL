using System.Collections.Generic;
using System.Text;

namespace RoslynRepl.Editor.Core
{
    public class WrappedCode
    {
        public string Source { get; }
        // 1-based line in Source where the user's first line starts.
        public int UserCodeLineOffset { get; }

        public WrappedCode(string source, int userCodeLineOffset)
        {
            Source = source;
            UserCodeLineOffset = userCodeLineOffset;
        }
    }

    /// <summary>
    /// Wraps user-supplied C# (statements) inside a static class + static method
    /// suitable for one-shot compilation by Roslyn. Tracks the line offset so
    /// compiler diagnostics can be remapped back to the user's editor lines.
    /// </summary>
    public static class ReplCodeWrapper
    {
        public const string ClassName = "__ReplScript";
        public const string MethodName = "__Run";

        public static WrappedCode Wrap(string userCode, IReadOnlyList<string> usings)
        {
            userCode ??= string.Empty;

            var sb = new StringBuilder();
            int line = 1;

            if (usings != null)
            {
                foreach (var u in usings)
                {
                    if (string.IsNullOrWhiteSpace(u)) continue;
                    sb.Append("using ").Append(u).Append(";\n");
                    line++;
                }
            }

            sb.Append('\n');                                  line++;
            sb.Append("public static class ").Append(ClassName).Append("\n");  line++;
            sb.Append("{\n");                                  line++;
            // Carry-over `_` exposed as a static property of the wrapper
            // class so snippets can reference it unqualified
            // (`return _ + 1;`). A property — rather than a local inside
            // __Run — avoids the CS0219 "declared but never used"
            // diagnostic when the user doesn't reference it, and lets a
            // user-introduced local `_` shadow it cleanly inside its own
            // scope (e.g. `int _ = 5;`). Reads pull through to
            // ReplEngine.LastResult, so the same instance reflects the
            // most recent non-null result on every invocation.
            //
            // Property type is `dynamic` rather than `object` so operators
            // and member access bind at runtime against the actual stored
            // value: `return _ + 1;` works after `return 41;`, no
            // user-side `(int)` cast needed. The C# compiler synthesizes
            // a CallSite under the hood; AssemblyReferenceCache force-
            // loads Microsoft.CSharp so the runtime binder is reachable.
            sb.Append("    public static dynamic _ => RoslynRepl.Editor.Core.ReplEngine.LastResult;\n"); line++;
            // Cooperative cancellation token. Snippets call
            // `ct.ThrowIfCancellationRequested()` inside long loops so the
            // engine's CancelAfter timer (default 5s) or an external
            // Cancel button can interrupt them. Snippets that don't check
            // `ct` still hang the Editor — there's no hard kill on the
            // main thread (Thread.Abort is unavailable on Mono / .NET 6+).
            sb.Append("    public static System.Threading.CancellationToken ct => RoslynRepl.Editor.Core.ReplEngine.CurrentCancellation;\n"); line++;
            sb.Append("    public static object ").Append(MethodName).Append("()\n");  line++;
            sb.Append("    {\n");                              line++;

            int userStart = line;
            sb.Append(userCode);
            if (!userCode.EndsWith("\n")) sb.Append('\n');

            sb.Append("        return null;\n");
            sb.Append("    }\n");
            sb.Append("}\n");

            return new WrappedCode(sb.ToString(), userStart);
        }

        public static int ToUserLine(int wrappedLine, int userCodeLineOffset)
        {
            return wrappedLine - userCodeLineOffset + 1;
        }
    }
}
