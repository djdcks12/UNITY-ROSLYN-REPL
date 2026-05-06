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
            sb.Append("    public static object _ => RoslynRepl.Editor.Core.ReplEngine.LastResult;\n"); line++;
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
