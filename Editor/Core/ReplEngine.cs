using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEditor;
using UnityEngine;

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
    /// to bail out cleanly.
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
        /// Replaces the carry-over <c>_</c> value from editor UI flows that
        /// surface an object without compiling user code, such as choosing a
        /// row in the Object Browser. Those flows render the value exactly as
        /// a snippet return would, so follow-up snippets and watches should
        /// see the same object through <c>_</c>.
        /// </summary>
        public static void SetLastResult(object value) => LastResult = value;

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

                MethodInfo method = null;
                if (options.UseCompileCache)
                {
                    EnsureAssemblyLoadHook();
                    lock (_compileCacheLock)
                    {
                        if (_compileCache.TryGetValue(wrapped.Source, out var cached))
                        {
                            method = cached.Method;
                        }
                    }
                }

                if (method == null)
                {
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

                    method = type.GetMethod(ReplCodeWrapper.MethodName,
                        BindingFlags.Public | BindingFlags.Static);
                    if (method == null)
                    {
                        return ReplResult.RuntimeError(
                            new InvalidOperationException(
                                $"Internal error: method '{ReplCodeWrapper.MethodName}' not found."),
                            ClassifyLogs(capture.End()), sw.Elapsed);
                    }

                    if (options.UseCompileCache)
                    {
                        // Hold a strong reference to the Assembly even
                        // though MethodInfo already pins it — explicit
                        // ownership makes the lifetime obvious to anyone
                        // reading the cache type, and matches what we'd
                        // want if the cache ever grows to evict by age.
                        lock (_compileCacheLock)
                        {
                            _compileCache[wrapped.Source] = new CachedScript
                            {
                                Assembly = asm,
                                Method = method,
                            };
                        }
                    }
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
                //
                // Background callers (the Watch panel) opt out via
                // ReplOptions.UpdateLastResult so passive observation
                // doesn't quietly mutate the user-visible carry-over —
                // running ten watches between two user runs would
                // otherwise leave `_` pointing at the last watch result
                // instead of the user's actual previous value.
                if (value != null && options.UpdateLastResult)
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

        // ─── compile cache (opt-in via ReplOptions.UseCompileCache) ──
        //
        // Issue #13: amortize the per-Run compile cost across N watch
        // expressions. Watches re-evaluate the same expression text
        // every Run, so the wrapped source ReplCodeWrapper.Wrap returns
        // is byte-identical between runs. Keying the cache on
        // wrapped.Source (which folds in usings + ClassName +
        // MethodName + the user expression) makes a hit safe by
        // construction — different snippet *or* different usings ⇒
        // different key ⇒ fresh compile.
        //
        // Invalidation: AppDomain.AssemblyLoad. Anything that adds a
        // new assembly to the domain — package install, recompile,
        // first-time Type.GetType resolution that pulls in a satellite
        // assembly — could legitimately change which symbols the next
        // compile would resolve, so the safe move is to drop the
        // entire cache. Our own dynamic ReplDynamic_* assemblies are
        // skipped so populating the cache doesn't immediately wipe it.
        //
        // Memory: no explicit eviction. Watches are user-typed and
        // typically <50 distinct snippets per session; the AssemblyLoad
        // sweep already prunes naturally on common project events
        // (script recompile, package add). If a user pathologically
        // typed thousands of unique watches, the cache would grow —
        // but that's the same pressure the underlying Assembly.Load
        // path already exerts, with or without this cache.

        private class CachedScript
        {
            public Assembly Assembly;
            public MethodInfo Method;
        }

        private static readonly Dictionary<string, CachedScript> _compileCache = new();
        private static readonly object _compileCacheLock = new();
        private static bool _assemblyLoadHooked;

        /// <summary>Snapshot of the cached entry count — diagnostics + tests.</summary>
        public static int CompileCacheCount
        {
            get { lock (_compileCacheLock) return _compileCache.Count; }
        }

        /// <summary>
        /// Drop every cached MethodInfo. Called automatically on
        /// AppDomain.AssemblyLoad for assemblies the engine didn't
        /// generate; exposed publicly so tests and the UI's "force
        /// rebuild" path can invalidate manually.
        /// </summary>
        public static void InvalidateCompileCache()
        {
            lock (_compileCacheLock)
            {
                _compileCache.Clear();
            }
        }

        private static void EnsureAssemblyLoadHook()
        {
            if (_assemblyLoadHooked) return;
            _assemblyLoadHooked = true;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;
        }

        private static void OnAssemblyLoaded(object sender, AssemblyLoadEventArgs args)
        {
            var asm = args?.LoadedAssembly;
            if (asm == null) return;

            // Skip every engine-emitted assembly — both the REPL
            // snippet path (ReplDynamic_*) and the runtime patch
            // path (ReplPatch_*) load through Assembly.Load and fire
            // this same event. Wiping the cache on either would
            // defeat the cache, and routing through the shared
            // ReplDiagnostics predicate keeps the prefix list
            // declared in one place.
            string name;
            try { name = asm.GetName().Name ?? string.Empty; }
            catch { name = string.Empty; }
            if (RoslynRepl.Editor.Diagnostics.ReplDiagnostics.IsReplGeneratedAssembly(name)) return;
            if (asm.IsDynamic) return;

            InvalidateCompileCache();
        }

        // ─── Player-frame marshal (issue: same-call-different-result) ──
        //
        // A snippet executed straight from Run lives inside the editor
        // input phase — it isn't fired from EventSystem.Update like a
        // real Button.onClick would be. Most snippets don't care, but
        // any code that spawns a popup / canvas / SuperScrollView whose
        // first init reads viewport metrics ends up reading them
        // *outside the player layout cycle*, and the popup ships with
        // stale layout + stale coroutine timing. The runtime symptom
        // is "the same one-line call gives a different result depending
        // on whether I clicked the button or pressed Run".
        //
        // ExecuteOnPlayerFrame marshals the actual Execute call onto a
        // ReplCoroutineHost coroutine that yields one frame, so the
        // invocation lands inside the next Player Update — the same
        // phase a real onClick fires from. Result + log lifecycle is
        // unchanged (ReplResult comes back the same way), it just
        // arrives one frame late, via a callback so the call site
        // doesn't have to spin-wait on the main thread.
        //
        // Edit Mode has no Player Update cycle, so the helper falls
        // back to a synchronous Execute and reports the result on the
        // same frame. Callers that want the marshal but happen to
        // be invoked outside Play Mode get the safest available
        // semantics rather than a deadlock.
        public static void ExecuteOnPlayerFrame(string userCode, ReplOptions options, Action<ReplResult> onComplete)
        {
            if (onComplete == null) throw new ArgumentNullException(nameof(onComplete));

            // Edit Mode (or about-to-leave Play Mode): no player
            // frame to wait for. Run synchronously so the caller
            // still gets a result.
            if (!EditorApplication.isPlaying)
            {
                onComplete(Execute(userCode, options));
                return;
            }

            var host = ReplCoroutineHost.EnsureInstance();
            if (host == null)
            {
                // Edge case: isPlaying flipped between the check
                // above and the host creation. Fall back to sync
                // rather than dropping the result silently.
                onComplete(Execute(userCode, options));
                return;
            }

            host.Run(Co());

            IEnumerator Co()
            {
                // One Player Update tick. The next frame's Update
                // → coroutine resume happens inside the player
                // loop, so the Execute below fires from the same
                // phase a Button.onClick would.
                yield return null;
                ReplResult result;
                try { result = Execute(userCode, options); }
                catch (Exception ex)
                {
                    // Defensive: Execute already wraps internal
                    // failures in ReplResult, but if something
                    // pathological happens the caller still needs
                    // a result object to render.
                    result = ReplResult.RuntimeError(ex, new List<LogEntry>(), TimeSpan.Zero);
                }
                try { onComplete(result); }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[Roslyn REPL] ExecuteOnPlayerFrame onComplete threw: " + ex);
                }
            }
        }
    }

    /// <summary>
    /// Hidden runtime MonoBehaviour the engine uses to step a
    /// coroutine through the Player Update phase. Defined here
    /// alongside ReplEngine instead of in its own file because
    /// keeping both in one compilation unit dodges a Unity quirk
    /// where a freshly-added Editor-folder MonoBehaviour can fail
    /// to compile on the same domain reload that introduces it,
    /// leaving the rest of the engine with an unresolved reference.
    /// Same-file sibling makes the dependency atomic.
    ///
    /// Lifetime: created lazily by <see cref="EnsureInstance"/>
    /// while the editor is in Play Mode, marked HideAndDontSave +
    /// DontDestroyOnLoad. Unity destroys the runtime GameObject
    /// when Play Mode exits; the static reference clears in
    /// <see cref="OnDestroy"/> so the next Play Mode run gets a
    /// fresh host.
    /// </summary>
    public sealed class ReplCoroutineHost : MonoBehaviour
    {
        private static ReplCoroutineHost _instance;

        public static ReplCoroutineHost EnsureInstance()
        {
            if (_instance != null) return _instance;
            // Only valid in Play Mode — coroutines outside Play
            // Mode don't get a Player Update tick to advance them.
            if (!EditorApplication.isPlayingOrWillChangePlaymode && !Application.isPlaying)
                return null;

            var go = new GameObject("__ReplCoroutineHost");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ReplCoroutineHost>();
            return _instance;
        }

        public Coroutine Run(IEnumerator routine) => StartCoroutine(routine);

        private void OnDestroy()
        {
            // Clear the static reference if Unity tears the host
            // down (Play Mode exit, scene unload edge cases). The
            // next EnsureInstance call will recreate.
            if (_instance == this) _instance = null;
        }
    }
}
