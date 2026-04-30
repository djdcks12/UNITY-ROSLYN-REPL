using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// One-shot Roslyn-based C# execution engine. Wraps user statements in a
    /// generated class, compiles to an in-memory assembly, loads it, and invokes
    /// the entry point synchronously on the calling thread.
    ///
    /// Phase 1 limitations:
    ///  - No variable persistence between calls (each Execute is isolated)
    ///  - No async/await support (the entry method is synchronous)
    ///  - No timeout / cancellation (infinite loops will hang the Editor)
    /// </summary>
    public static class ReplEngine
    {
        public static ReplResult Execute(string userCode, ReplOptions options = null)
        {
            options ??= new ReplOptions();

            var capture = new ReplLogCapture();
            var sw = Stopwatch.StartNew();

            try
            {
                var wrapped = ReplCodeWrapper.Wrap(userCode, options.Usings);

                var tree = CSharpSyntaxTree.ParseText(wrapped.Source);
                var compilation = CSharpCompilation.Create(
                    assemblyName: "ReplDynamic_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    syntaxTrees: new[] { tree },
                    references: AssemblyReferenceCache.GetReferences(),
                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                );

                using var ms = new MemoryStream();
                var emit = compilation.Emit(ms);
                if (!emit.Success)
                {
                    var diagnostics = emit.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => ToDiagnosticInfo(d, wrapped.UserCodeLineOffset))
                        .ToList();
                    return ReplResult.CompileError(diagnostics, ClassifyLogs(capture.End()), sw.Elapsed);
                }

                ms.Seek(0, SeekOrigin.Begin);
                var asm = Assembly.Load(ms.ToArray());
                var type = asm.GetType(ReplCodeWrapper.ClassName);
                if (type == null)
                {
                    return ReplResult.RuntimeError(
                        new InvalidOperationException(
                            $"Internal error: class '{ReplCodeWrapper.ClassName}' not found in compiled assembly."),
                        ClassifyLogs(capture.End()), sw.Elapsed);
                }

                var method = type.GetMethod(ReplCodeWrapper.MethodName,
                    BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    return ReplResult.RuntimeError(
                        new InvalidOperationException(
                            $"Internal error: method '{ReplCodeWrapper.MethodName}' not found."),
                        ClassifyLogs(capture.End()), sw.Elapsed);
                }

                // Begin capture as late as possible — only the Invoke window — so
                // background logs emitted during Wrap / Parse / Compile / Emit /
                // Load do not contaminate the snippet's output.
                object value;
                capture.Begin();
                try
                {
                    value = method.Invoke(null, null);
                }
                catch (TargetInvocationException tie)
                {
                    return ReplResult.RuntimeError(tie.InnerException ?? tie, ClassifyLogs(capture.End()), sw.Elapsed);
                }

                return ReplResult.Success(value, FormatValue(value), ClassifyLogs(capture.End()), sw.Elapsed);
            }
            catch (Exception ex)
            {
                return ReplResult.RuntimeError(ex, ClassifyLogs(capture.End()), sw.Elapsed);
            }
        }

        // Tags each captured log with whether it likely originated from inside the
        // user's snippet, by checking whether the generated wrapper class name
        // appears anywhere in the stack trace. Logs without any stack trace are
        // treated as snippet-originated to be safe (Unity may suppress traces for
        // some log levels via the Stack Trace Logging editor preference).
        private static List<LogEntry> ClassifyLogs(List<LogEntry> logs)
        {
            if (logs == null || logs.Count == 0) return logs ?? new List<LogEntry>();
            foreach (var log in logs)
            {
                if (string.IsNullOrEmpty(log.StackTrace))
                {
                    log.FromSnippet = true;
                }
                else
                {
                    log.FromSnippet = log.StackTrace.Contains(ReplCodeWrapper.ClassName);
                }
            }
            return logs;
        }

        private static DiagnosticInfo ToDiagnosticInfo(Diagnostic d, int userCodeLineOffset)
        {
            var pos = d.Location.GetLineSpan().StartLinePosition;
            int compileLine = pos.Line + 1;
            int userLine = compileLine - userCodeLineOffset + 1;
            return new DiagnosticInfo
            {
                Severity = d.Severity.ToString(),
                Code = d.Id,
                Message = d.GetMessage(),
                Line = userLine > 0 ? userLine : compileLine,
                Column = pos.Character + 1,
                IsInUserCode = userLine > 0
            };
        }

        private static string FormatValue(object value)
        {
            if (value == null) return "null";
            if (value is string s) return $"\"{s}\"";
            if (value is char c)   return $"'{c}'";
            try { return value.ToString(); }
            catch (Exception ex) { return $"<Error in ToString(): {ex.Message}>"; }
        }
    }
}
