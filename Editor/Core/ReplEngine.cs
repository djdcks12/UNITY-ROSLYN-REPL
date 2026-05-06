using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// One-shot Roslyn-based C# execution engine. Wraps user statements in a
    /// generated class, compiles to an in-memory assembly, loads it, and invokes
    /// the entry point synchronously on the calling thread.
    ///
    /// Phase 5 introduces a single piece of carry-over state: the most recent
    /// non-null returned value is stored in <see cref="LastResult"/> and
    /// surfaced inside snippets as the static property <c>_</c> on the
    /// generated wrapper class. Other locals still don't survive between runs.
    ///
    /// Phase 6 adds soft timeout / cancellation. Each Execute call links a
    /// new <see cref="CancellationTokenSource"/> with the optional external
    /// token from <see cref="ReplOptions.ExternalCancellation"/> and (when
    /// enabled) <c>CancelAfter(TimeoutMs)</c>. The combined token is exposed
    /// to user snippets as the static property <c>ct</c> on the wrapper
    /// class, so cooperative loops can call <c>ct.ThrowIfCancellationRequested()</c>
    /// to bail out cleanly. Hard kill of a synchronous snippet that ignores
    /// the token isn't supported on Editor's main thread (Thread.Abort is
    /// unavailable); see README "Known limitations".
    ///
    /// async/await inside snippets is deferred — see README.
    /// </summary>
    public static class ReplEngine
    {
        /// <summary>
        /// The value returned by the most recent successful, non-null Execute
        /// call. Read inside snippets as <c>_</c>. Reset to <c>null</c> if the
        /// editor reloads its assemblies (the value is in-memory only).
        /// </summary>
        public static object LastResult { get; private set; }

        /// <summary>Clears the carry-over <c>_</c> value.</summary>
        public static void ResetLastResult() => LastResult = null;

        /// <summary>
        /// Cancellation token for the snippet currently executing. Read inside
        /// snippets as <c>ct</c>. <c>CancellationToken.None</c> when no Execute
        /// call is in flight (e.g. between runs or during compilation).
        /// </summary>
        public static CancellationToken CurrentCancellation { get; private set; } = CancellationToken.None;

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
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(options.ExternalCancellation);
                if (options.TimeoutMs > 0) cts.CancelAfter(options.TimeoutMs);
                CurrentCancellation = cts.Token;
                capture.Begin();
                try
                {
                    value = method.Invoke(null, null);
                }
                catch (TargetInvocationException tie) when (IsCancellation(tie.InnerException, cts.Token))
                {
                    // Cooperative cancel from inside the snippet
                    // (ct.ThrowIfCancellationRequested() or similar).
                    // Distinguish the timeout-driven cancel from an
                    // external one via cts.IsCancellationRequested vs the
                    // external token's state — the message helps the user
                    // tell "I hit Cancel" from "snippet ran past 5s".
                    var reason = BuildCancelReason(options, cts.Token);
                    return ReplResult.Cancelled(reason, ClassifyLogs(capture.End()), sw.Elapsed);
                }
                catch (TargetInvocationException tie)
                {
                    return ReplResult.RuntimeError(tie.InnerException ?? tie, ClassifyLogs(capture.End()), sw.Elapsed);
                }
                finally
                {
                    // Whatever happens, the next call shouldn't see this
                    // call's token if it's been disposed.
                    CurrentCancellation = CancellationToken.None;
                }

                // Carry-over: record only meaningful values. The wrapper
                // adds an unconditional `return null;` fallback at the end
                // for snippets that don't return anything, and overwriting
                // LastResult with that synthetic null would defeat the
                // purpose of `_` — we'd erase the previous useful value
                // every time the user ran a Debug.Log-only line.
                if (value != null)
                {
                    LastResult = value;
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

        private static bool IsCancellation(Exception ex, CancellationToken token)
        {
            // Either the framework's OperationCanceledException matched the
            // token we issued, or some user code threw OCE for any reason
            // and the token is also signalled — in the latter case treat it
            // as a cancel too, since the user almost certainly wired their
            // own check off `ct`. Matching tokens is the strict-correct
            // signal; relaxing to "token signalled" catches the common
            // pattern `if (ct.IsCancellationRequested) throw new OperationCanceledException();`.
            if (ex is OperationCanceledException oce)
            {
                if (oce.CancellationToken == token) return true;
                if (token.IsCancellationRequested) return true;
            }
            return false;
        }

        private static string BuildCancelReason(ReplOptions options, CancellationToken token)
        {
            // External token wins in the message — if the user hit a Cancel
            // button somewhere, "timeout" would be misleading.
            if (options.ExternalCancellation.IsCancellationRequested)
                return "Snippet cancelled by external request";
            if (options.TimeoutMs > 0 && token.IsCancellationRequested)
                return $"Snippet cancelled after {options.TimeoutMs} ms timeout";
            return "Snippet cancelled";
        }
    }
}
