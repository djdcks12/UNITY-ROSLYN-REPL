using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// Compiles a user-edited method body, then redirects calls to the
    /// target method onto the compiled replacement via Harmony.
    ///
    /// Phase A MVP scope:
    ///  • instance methods returning void
    ///  • public, internal, private, and protected — anything Harmony can
    ///    reach via the BindingFlags below
    ///  • parameter types limited to fully-qualifiable names (the spec
    ///    stores them as a comma-joined list of full type names)
    ///
    /// Out of scope for the MVP (see issue #14 for the larger plan):
    ///  • non-void return types
    ///  • static methods
    ///  • ref / out / in parameters
    ///  • generic methods
    ///  • constructors
    ///
    /// Harmony lives in the optional 0Harmony.dll. The engine talks to
    /// Harmony exclusively through reflection so the package doesn't
    /// fail to compile when the DLL hasn't been installed yet — every
    /// call here that hits HarmonyLib types looks the type up at first
    /// use and surfaces a clear "install Harmony first" error if it's
    /// missing.
    /// </summary>
    public static class PatchEngine
    {
        public const string HarmonyId = "com.roslyn-repl.runtime-patch";

        // Per-applied-patch handle so Revert knows what to undo. Keyed by
        // the same MethodPatchSpec.Key so the registry and the engine
        // agree on identity.
        private class AppliedPatch
        {
            public MethodInfo Target;
            public MethodInfo PrefixReplacement;
            public Assembly DynamicAssembly;
        }
        private static readonly Dictionary<string, AppliedPatch> _applied = new();

        public static bool IsApplied(MethodPatchSpec spec) => spec != null && _applied.ContainsKey(spec.Key);

        public static int AppliedCount => _applied.Count;

        public static IReadOnlyCollection<string> AppliedKeys => _applied.Keys;

        /// <summary>
        /// Compile <paramref name="spec"/>'s patch body, install a Harmony
        /// Prefix that returns the patched body and skips the original,
        /// and update the registry's status. Throws on resolution /
        /// compile / Harmony failures with a message intended for the UI.
        /// </summary>
        public static void Apply(MethodPatchSpec spec)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));

            // Phase 1 — resolve + compile. No mutations to existing
            // state happen here, so a typo / missing type / missing
            // method / compile error bubbles up before we touch the
            // currently-applied patch (if any). Live Play Mode flow
            // demands this: the user iterates by editing the body and
            // re-Applying; an old "naively revert first, then compile"
            // path would silently drop the working patch on every typo.
            var target = ResolveTargetMethod(spec);
            // Phase D — pull the source file's namespace + using
            // directives so the generated wrapper can compile pulled
            // bodies that reference same-namespace types or rely on
            // file-level usings (aliases, project namespaces). Falls
            // back to "no namespace, standard usings only" when the
            // source can't be located.
            var fileContext = PatchSourcePuller.GetDeclaringFileContext(target);
            var className = "__ReplPatch_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var source = PatchCodeGenerator.Generate(spec, target, className, fileContext);
            var asm = CompileToAssembly(source, target.DeclaringType, spec.Key);
            var fullClassName = string.IsNullOrEmpty(fileContext?.Namespace)
                ? className
                : fileContext.Namespace + "." + className;
            var patchType = asm.GetType(fullClassName)
                ?? throw new InvalidOperationException($"Compiled patch is missing class '{fullClassName}'");
            var prefix = patchType.GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"Compiled patch is missing static Prefix method");

            // Phase 2 — atomic swap. Unpatch old (if any) → patch new.
            // If patch-new fails, restore the old prefix so the user's
            // working state is preserved. Both Unpatch/Patch run on
            // the Editor main thread synchronously, so the window
            // where neither prefix is active is bounded by a single
            // Harmony call — not observable in practice.
            _applied.TryGetValue(spec.Key, out var oldApplied);
            if (oldApplied != null)
            {
                try { HarmonyBridge.Unpatch(oldApplied.Target, oldApplied.PrefixReplacement); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning($"[Roslyn REPL] Failed to remove previous patch: {ex.Message}"); }
            }

            try
            {
                HarmonyBridge.Patch(target, prefix);
            }
            catch
            {
                // Roll back: re-install the prior prefix. Best-effort —
                // a Harmony-level failure on restore is rare but
                // logged so the user can spot a "lost patch" case
                // instead of silently running unpatched code.
                if (oldApplied != null)
                {
                    try { HarmonyBridge.Patch(oldApplied.Target, oldApplied.PrefixReplacement); }
                    catch (Exception ex2) { UnityEngine.Debug.LogWarning($"[Roslyn REPL] Failed to restore previous patch after re-apply error: {ex2.Message}"); }
                }
                throw;
            }

            // Phase 3 — bookkeeping (only on success).
            _applied[spec.Key] = new AppliedPatch
            {
                Target = target,
                PrefixReplacement = prefix,
                DynamicAssembly = asm,
            };
            spec.Status = PatchStatus.Active;
            spec.LastError = null;
            PatchRegistry.AddOrUpdate(spec); // re-fires Changed for the UI
        }

        /// <summary>
        /// Remove the Harmony patch and mark the spec Inactive. Safe to
        /// call when nothing is currently applied — no-op in that case.
        /// </summary>
        public static void Revert(MethodPatchSpec spec)
        {
            if (spec == null) return;
            if (!_applied.TryGetValue(spec.Key, out var applied))
            {
                spec.Status = PatchStatus.Inactive;
                spec.LastError = null;
                PatchRegistry.AddOrUpdate(spec);
                return;
            }

            try { HarmonyBridge.Unpatch(applied.Target, applied.PrefixReplacement); }
            catch
            {
                // Best-effort. If Harmony itself blew up, drop the
                // bookkeeping anyway — leaving _applied populated would
                // permanently shadow the key.
            }

            _applied.Remove(spec.Key);
            spec.Status = PatchStatus.Inactive;
            spec.LastError = null;
            PatchRegistry.AddOrUpdate(spec);
        }

        /// <summary>
        /// Revert every active patch. Convenience for Reset Project Data
        /// and for the popup's "Disable all" affordance.
        /// </summary>
        public static int RevertAll()
        {
            int n = _applied.Count;
            // Snapshot keys before mutating the dictionary in Revert().
            foreach (var key in _applied.Keys.ToList())
            {
                var spec = PatchRegistry.Specs.FirstOrDefault(s => s.Key == key);
                if (spec != null) Revert(spec);
                else
                {
                    // Spec gone from registry but engine still holds the
                    // detour — flush directly.
                    var applied = _applied[key];
                    try { HarmonyBridge.Unpatch(applied.Target, applied.PrefixReplacement); }
                    catch { /* best-effort */ }
                    _applied.Remove(key);
                }
            }
            return n;
        }

        /// <summary>
        /// Public-facing wrapper around <see cref="ResolveTargetMethod"/>.
        /// Phase C's "Pull Original" UI needs the same MethodInfo
        /// resolution the engine uses, but without throwing — failure
        /// just means "can't pull, show the user a hint".
        /// </summary>
        public static MethodInfo TryResolveTargetMethod(MethodPatchSpec spec, out string error)
        {
            error = null;
            try { return ResolveTargetMethod(spec); }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        /// <summary>
        /// Look up <paramref name="spec"/>'s target method. Errors are
        /// thrown with messages the UI can show as the row's LastError.
        /// </summary>
        private static MethodInfo ResolveTargetMethod(MethodPatchSpec spec)
        {
            var type = ResolveType(spec.TargetTypeName)
                ?? throw new InvalidOperationException($"Type not found: {spec.TargetTypeName}");

            var paramTypes = ResolveParamTypes(spec.ParameterTypes);
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var method = type.GetMethod(spec.MethodName, bf, null, paramTypes, null);
            if (method == null)
            {
                throw new InvalidOperationException(
                    $"Method not found: {spec.TargetTypeName}.{spec.MethodName}({spec.ParameterTypes ?? ""})");
            }
            if (method.ReturnType != typeof(void))
            {
                throw new InvalidOperationException(
                    $"Phase A MVP only patches void instance methods; {method.Name} returns {method.ReturnType.Name}.");
            }
            if (method.IsStatic)
            {
                throw new InvalidOperationException(
                    $"Phase A MVP only patches *instance* methods; {method.Name} is static.");
            }
            return method;
        }

        private static Type[] ResolveParamTypes(string commaJoined)
        {
            if (string.IsNullOrEmpty(commaJoined)) return Type.EmptyTypes;
            var parts = commaJoined.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            var types = new Type[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                types[i] = ResolveType(parts[i])
                    ?? throw new InvalidOperationException($"Parameter type not found: {parts[i]}");
            }
            return types;
        }

        private static Type ResolveType(string fullName)
        {
            // Type.GetType(string, throwOnError: false) walks the calling
            // assembly + mscorlib. For user types in Assembly-CSharp etc.
            // we have to search the AppDomain manually.
            var direct = Type.GetType(fullName, throwOnError: false);
            if (direct != null) return direct;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            return null;
        }

        private static Assembly CompileToAssembly(string source, Type declaringType, string specKey)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var asmName = "ReplPatch_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var refs = AssemblyReferenceCache.GetReferences();
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            var compilation = CSharpCompilation.Create(
                assemblyName: asmName,
                syntaxTrees: new[] { tree },
                references: refs,
                options: options);

            // Phase D — natural-code rewrite. The user can write
            // `hp -= 10`, `Singleton.Instance.privateField`,
            // `MyClass.PrivateStatic = …` and have the wrapper still
            // compile. The mechanism: ask Roslyn what it complains
            // about, route every "name doesn't exist" / "inaccessible
            // due to protection level" / "no definition" location
            // through the reflection helpers PatchCodeGenerator
            // already pre-emits into the wrapper. Code that compiles
            // untouched (public access, locals, etc.) is left exactly
            // as the user wrote it, so direct calls keep their fast
            // codegen.
            //
            // The loop is mandatory, not paranoid: a compound rewrite
            // like `hidden = hp + armor` swallows the inner `hp` and
            // `armor` reads when their parent assignment is replaced
            // (SyntaxNode.ReplaceNodes is outer-first; once the
            // assignment is rewritten, the read nodes inside live in
            // the new helper expression and get re-flagged by the
            // next compile). Iterating until DidRewrite is false
            // converges on the fix point. Cap at 8 passes — anything
            // beyond that is an infinite loop, not a deeply nested
            // patch.
            //
            // The first-pass error count guards the common case: if
            // a patch already uses helpers explicitly (Phase A's
            // documented form), the first compile is clean and we
            // skip the rewriter walk entirely.
            int rewriteRounds = 0;
            int totalRewrites = 0;
            var allNotes = new List<string>();
            if (declaringType != null)
            {
                while (rewriteRounds < 8)
                {
                    var passErrors = compilation.GetDiagnostics()
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .ToArray();
                    if (passErrors.Length == 0) break;

                    var rewrite = PatchSyntaxRewriter.Rewrite(tree, compilation, declaringType);
                    if (!rewrite.DidRewrite) break;

                    tree = rewrite.Tree;
                    compilation = CSharpCompilation.Create(
                        assemblyName: asmName,
                        syntaxTrees: new[] { tree },
                        references: refs,
                        options: options);

                    rewriteRounds++;
                    totalRewrites += rewrite.Notes.Count;
                    allNotes.AddRange(rewrite.Notes);
                }

                if (totalRewrites > 0)
                {
                    // Surface a single summary line. Quiet enough not
                    // to spam, verbose enough to be a debug breadcrumb
                    // when a rewrite goes subtly wrong.
                    UnityEngine.Debug.Log(
                        $"[Roslyn REPL] Patch '{specKey}' rewrote {totalRewrites} access(es) in {rewriteRounds} pass(es): "
                        + string.Join("; ", allNotes));
                }
            }

            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms);
            if (!emit.Success)
            {
                var diag = string.Join("\n  ", emit.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => $"{d.Id}: {d.GetMessage()}"));
                throw new InvalidOperationException("Patch compile failed:\n  " + diag);
            }
            ms.Seek(0, SeekOrigin.Begin);
            return Assembly.Load(ms.ToArray());
        }
    }

    /// <summary>
    /// Builds the C# source for the wrapper class that hosts the user's
    /// patch body. Kept separate from the engine so we can unit-check
    /// the generated source without touching Harmony.
    /// </summary>
    public static class PatchCodeGenerator
    {
        // Phase D originally had no FileContext parameter; preserve
        // the old call shape so any caller that doesn't have one
        // (probes, future callers without source-puller hookup) still
        // works.
        public static string Generate(MethodPatchSpec spec, MethodInfo target, string className)
            => Generate(spec, target, className, null);

        public static string Generate(MethodPatchSpec spec, MethodInfo target, string className, PatchSourcePuller.FileContext context)
        {
            var sb = new StringBuilder();

            // Compilation-unit usings: the standard wrapper imports
            // plus the source file's top-level usings. Dedupe by
            // exact text so `using System;` from both sources doesn't
            // double-emit and trigger CS0105.
            var cuEmitted = new HashSet<string>(StringComparer.Ordinal);
            void EmitCuUsing(string line)
            {
                if (cuEmitted.Add(line)) sb.AppendLine(line);
            }
            EmitCuUsing("using System;");
            EmitCuUsing("using System.Collections.Generic;");
            EmitCuUsing("using System.Linq;");
            EmitCuUsing("using System.Reflection;");
            EmitCuUsing("using UnityEngine;");
            if (context?.CompilationUnitUsings != null)
            {
                foreach (var u in context.CompilationUnitUsings) EmitCuUsing(u);
            }
            sb.AppendLine();

            // Wrap the wrapper class in the target type's namespace
            // so pulled bodies that reference same-namespace types
            // resolve without explicit qualification. Top-level
            // (declaringType.Namespace == null) falls back to the
            // global namespace, same as the pre-Phase-D wrapper.
            string ns = context?.Namespace;
            bool hasNs = !string.IsNullOrEmpty(ns);
            if (hasNs)
            {
                sb.AppendLine($"namespace {ns}");
                sb.AppendLine("{");
            }

            // Namespace-scoped usings — these were originally declared
            // inside a `namespace { … }` block in the source file.
            // Emit them inside the wrapper's namespace block so their
            // scoping (and any relative resolution against the
            // enclosing namespace) is preserved instead of leaked to
            // the compilation unit. Skip ones already emitted at CU
            // level to avoid CS0105.
            if (context?.NamespaceScopedUsings != null)
            {
                foreach (var u in context.NamespaceScopedUsings)
                {
                    if (cuEmitted.Contains(u)) continue;
                    sb.AppendLine(hasNs ? "    " + u : u);
                }
                if (context.NamespaceScopedUsings.Count > 0) sb.AppendLine();
            }

            sb.AppendLine($"public static class {className}");
            sb.AppendLine("{");

            // Build the parameter list. First param is `__instance` — the
            // Harmony convention for instance method targets — typed as
            // the declaring type so the user's body can write
            // `__instance.PublicField` without casting.
            var declType = target.DeclaringType.FullName.Replace('+', '.');
            sb.Append($"    public static bool Prefix({declType} __instance");
            foreach (var p in target.GetParameters())
            {
                var pType = p.ParameterType.FullName?.Replace('+', '.') ?? p.ParameterType.Name;
                sb.Append($", {pType} {p.Name}");
            }
            sb.AppendLine(")");
            sb.AppendLine("    {");

            // Local-function helpers. C# 7+ local functions allow generic
            // signatures, so `__get<int>("hp")` compiles cleanly. They
            // capture `__instance` through the enclosing scope so the
            // user never has to thread it through.
            sb.AppendLine("        T __get<T>(string name)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var __t = __instance != null ? __instance.GetType() : typeof({declType});");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy;");
            sb.AppendLine("            var __f = __t.GetField(name, __bf);");
            sb.AppendLine("            if (__f != null) return (T)__f.GetValue(__instance);");
            sb.AppendLine("            var __p = __t.GetProperty(name, __bf);");
            sb.AppendLine("            if (__p != null) return (T)__p.GetValue(__instance);");
            sb.AppendLine("            throw new System.InvalidOperationException(\"Member '\" + name + \"' not found on \" + __t.Name);");
            sb.AppendLine("        }");

            sb.AppendLine("        void __set(string name, object value)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var __t = __instance != null ? __instance.GetType() : typeof({declType});");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy;");
            sb.AppendLine("            var __f = __t.GetField(name, __bf);");
            sb.AppendLine("            if (__f != null) { __f.SetValue(__instance, value); return; }");
            sb.AppendLine("            var __p = __t.GetProperty(name, __bf);");
            sb.AppendLine("            if (__p != null) { __p.SetValue(__instance, value); return; }");
            sb.AppendLine("            throw new System.InvalidOperationException(\"Member '\" + name + \"' not found on \" + __t.Name);");
            sb.AppendLine("        }");

            sb.AppendLine("        T __call<T>(string name, params object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var __t = __instance != null ? __instance.GetType() : typeof({declType});");
            sb.AppendLine("            var __r = __InvokeReflective(__t, __instance, name, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            // ─── Phase D helpers: same shape as the originals but
            // parameterized over the target type / instance, so the
            // syntax rewriter can redirect inaccessible member access
            // through them no matter where the symbol lives:
            //   • __getOn / __setOn / __callOn — instance reflection on
            //     an arbitrary `target` object (covers `singleton.X`,
            //     `someField.PrivateMember`, etc.).
            //   • __getStatic / __setStatic / __callStatic — static
            //     reflection on an arbitrary `Type` (covers
            //     `SomeClass.PrivateStatic`, `Type.PrivateStaticMethod()`).
            //
            // Phase A's original `__get<T>` / `__set` / `__call<T>` keep
            // their signatures so existing patches written against the
            // documented helpers still compile unchanged.
            sb.AppendLine();
            sb.AppendLine("        T __getOn<T>(object target, string name)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (target == null) throw new System.ArgumentNullException(nameof(target));");
            sb.AppendLine("            var __t = target.GetType();");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy;");
            sb.AppendLine("            var __f = __t.GetField(name, __bf);");
            sb.AppendLine("            if (__f != null) return (T)__f.GetValue(target);");
            sb.AppendLine("            var __p = __t.GetProperty(name, __bf);");
            sb.AppendLine("            if (__p != null) return (T)__p.GetValue(target);");
            sb.AppendLine("            throw new System.InvalidOperationException(\"Member '\" + name + \"' not found on \" + __t.Name);");
            sb.AppendLine("        }");

            sb.AppendLine("        void __setOn(object target, string name, object value)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (target == null) throw new System.ArgumentNullException(nameof(target));");
            sb.AppendLine("            var __t = target.GetType();");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy;");
            sb.AppendLine("            var __f = __t.GetField(name, __bf);");
            sb.AppendLine("            if (__f != null) { __f.SetValue(target, value); return; }");
            sb.AppendLine("            var __p = __t.GetProperty(name, __bf);");
            sb.AppendLine("            if (__p != null) { __p.SetValue(target, value); return; }");
            sb.AppendLine("            throw new System.InvalidOperationException(\"Member '\" + name + \"' not found on \" + __t.Name);");
            sb.AppendLine("        }");

            sb.AppendLine("        T __callOn<T>(object target, string name, params object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (target == null) throw new System.ArgumentNullException(nameof(target));");
            sb.AppendLine("            var __r = __InvokeReflective(target.GetType(), target, name, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            sb.AppendLine("        T __getStatic<T>(System.Type type, string name)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (type == null) throw new System.ArgumentNullException(nameof(type));");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy;");
            sb.AppendLine("            var __f = type.GetField(name, __bf);");
            sb.AppendLine("            if (__f != null) return (T)__f.GetValue(null);");
            sb.AppendLine("            var __p = type.GetProperty(name, __bf);");
            sb.AppendLine("            if (__p != null) return (T)__p.GetValue(null);");
            sb.AppendLine("            throw new System.InvalidOperationException(\"Static member '\" + name + \"' not found on \" + type.Name);");
            sb.AppendLine("        }");

            sb.AppendLine("        void __setStatic(System.Type type, string name, object value)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (type == null) throw new System.ArgumentNullException(nameof(type));");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy;");
            sb.AppendLine("            var __f = type.GetField(name, __bf);");
            sb.AppendLine("            if (__f != null) { __f.SetValue(null, value); return; }");
            sb.AppendLine("            var __p = type.GetProperty(name, __bf);");
            sb.AppendLine("            if (__p != null) { __p.SetValue(null, value); return; }");
            sb.AppendLine("            throw new System.InvalidOperationException(\"Static member '\" + name + \"' not found on \" + type.Name);");
            sb.AppendLine("        }");

            sb.AppendLine("        T __callStatic<T>(System.Type type, string name, params object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (type == null) throw new System.ArgumentNullException(nameof(type));");
            sb.AppendLine("            var __r = __InvokeReflective(type, null, name, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            // ─── Phase D explicit-generic method call helpers ──────
            // Same shape as __call / __callOn / __callStatic but pass
            // a typeArgs array along so the helper can call
            // MakeGenericMethod before invoke. Used when the user
            // wrote `Cache.GetPrivate<Foo>(...)` — the rewriter sees
            // a GenericNameSyntax and emits the typeArgs as
            // `new System.Type[] { typeof(Foo), ... }`.
            sb.AppendLine("        T __callG<T>(string name, System.Type[] typeArgs, params object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var __t = __instance != null ? __instance.GetType() : typeof({declType});");
            sb.AppendLine("            var __r = __InvokeReflectiveGeneric(__t, __instance, name, typeArgs, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            sb.AppendLine("        T __callGOn<T>(object target, string name, System.Type[] typeArgs, params object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (target == null) throw new System.ArgumentNullException(nameof(target));");
            sb.AppendLine("            var __r = __InvokeReflectiveGeneric(target.GetType(), target, name, typeArgs, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            sb.AppendLine("        T __callGStatic<T>(System.Type type, string name, System.Type[] typeArgs, params object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (type == null) throw new System.ArgumentNullException(nameof(type));");
            sb.AppendLine("            var __r = __InvokeReflectiveGeneric(type, null, name, typeArgs, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            // Shared invoke pipeline for __call / __callOn / __callStatic.
            // Picks an overload by argument count first (so user code with
            // params doesn't accidentally bind to a different-arity
            // method), then narrows by argument types when more than one
            // candidate remains. Returns the boxed result; the caller
            // unboxes via (T) cast — `null` round-trips as `default(T)`
            // for value types, which matches the pattern Phase A used.
            sb.AppendLine("        object __InvokeReflective(System.Type type, object instance, string name, object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy;");
            sb.AppendLine("            args = args ?? new object[0];");
            sb.AppendLine("            var __ms = new System.Collections.Generic.List<System.Reflection.MethodInfo>();");
            sb.AppendLine("            foreach (var __mm in type.GetMethods(__bf))");
            sb.AppendLine("            {");
            sb.AppendLine("                if (__mm.Name != name) continue;");
            sb.AppendLine("                if (__mm.GetParameters().Length != args.Length) continue;");
            sb.AppendLine("                __ms.Add(__mm);");
            sb.AppendLine("            }");
            sb.AppendLine("            if (__ms.Count == 0) throw new System.InvalidOperationException(\"Method '\" + name + \"(\" + args.Length + \" args)' not found on \" + type.Name);");
            sb.AppendLine("            System.Reflection.MethodInfo __m;");
            sb.AppendLine("            if (__ms.Count == 1) { __m = __ms[0]; }");
            sb.AppendLine("            else");
            sb.AppendLine("            {");
            sb.AppendLine("                var __argTypes = new System.Type[args.Length];");
            sb.AppendLine("                for (int __i = 0; __i < args.Length; __i++) __argTypes[__i] = args[__i] == null ? typeof(object) : args[__i].GetType();");
            sb.AppendLine("                __m = type.GetMethod(name, __bf, null, __argTypes, null) ?? __ms[0];");
            sb.AppendLine("            }");
            sb.AppendLine("            return __m.Invoke(instance, args);");
            sb.AppendLine("        }");

            // Generic method invocation. Picks an arity- *and*
            // generic-arity-matching open generic definition, calls
            // MakeGenericMethod with the user-supplied typeArgs, and
            // invokes. Falls back to picking the first matching
            // candidate when a runtime arg-type tie-break would need
            // post-substitution comparisons we don't try to
            // approximate here — overload resolution after
            // MakeGenericMethod is good enough for the common case.
            sb.AppendLine("        object __InvokeReflectiveGeneric(System.Type type, object instance, string name, System.Type[] typeArgs, object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy;");
            sb.AppendLine("            args = args ?? new object[0];");
            sb.AppendLine("            typeArgs = typeArgs ?? new System.Type[0];");
            sb.AppendLine("            var __ms = new System.Collections.Generic.List<System.Reflection.MethodInfo>();");
            sb.AppendLine("            foreach (var __mm in type.GetMethods(__bf))");
            sb.AppendLine("            {");
            sb.AppendLine("                if (__mm.Name != name) continue;");
            sb.AppendLine("                if (__mm.GetParameters().Length != args.Length) continue;");
            sb.AppendLine("                if (typeArgs.Length > 0)");
            sb.AppendLine("                {");
            sb.AppendLine("                    if (!__mm.IsGenericMethodDefinition) continue;");
            sb.AppendLine("                    if (__mm.GetGenericArguments().Length != typeArgs.Length) continue;");
            sb.AppendLine("                }");
            sb.AppendLine("                else if (__mm.IsGenericMethodDefinition) continue;");
            sb.AppendLine("                __ms.Add(__mm);");
            sb.AppendLine("            }");
            sb.AppendLine("            if (__ms.Count == 0) throw new System.InvalidOperationException(\"Method '\" + name + \"<\" + typeArgs.Length + \">(\" + args.Length + \" args)' not found on \" + type.Name);");
            sb.AppendLine("            System.Reflection.MethodInfo __m;");
            sb.AppendLine("            if (__ms.Count == 1) { __m = __ms[0]; }");
            sb.AppendLine("            else");
            sb.AppendLine("            {");
            // Multiple overloads sharing name + arity + generic arity.
            // Disambiguate by argument types — but for generic
            // candidates the parameters become concrete only after
            // MakeGenericMethod, so instantiate per-candidate first
            // and then compare. `null` arguments match anything since
            // we have no static type info; non-null arguments must be
            // assignable to the candidate's parameter type.
            sb.AppendLine("                var __argTypes = new System.Type[args.Length];");
            sb.AppendLine("                for (int __i = 0; __i < args.Length; __i++) __argTypes[__i] = args[__i] == null ? typeof(object) : args[__i].GetType();");
            sb.AppendLine("                System.Reflection.MethodInfo __best = null;");
            sb.AppendLine("                foreach (var __c in __ms)");
            sb.AppendLine("                {");
            sb.AppendLine("                    var __inst = (typeArgs.Length > 0 && __c.IsGenericMethodDefinition) ? __c.MakeGenericMethod(typeArgs) : __c;");
            sb.AppendLine("                    var __pars = __inst.GetParameters();");
            sb.AppendLine("                    bool __ok = true;");
            sb.AppendLine("                    for (int __i = 0; __i < __pars.Length; __i++)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        if (args[__i] == null) continue;");
            sb.AppendLine("                        if (!__pars[__i].ParameterType.IsAssignableFrom(__argTypes[__i])) { __ok = false; break; }");
            sb.AppendLine("                    }");
            sb.AppendLine("                    if (__ok) { __best = __c; break; }");
            sb.AppendLine("                }");
            sb.AppendLine("                __m = __best ?? __ms[0];");
            sb.AppendLine("            }");
            sb.AppendLine("            if (typeArgs.Length > 0 && __m.IsGenericMethodDefinition) __m = __m.MakeGenericMethod(typeArgs);");
            sb.AppendLine("            return __m.Invoke(instance, args);");
            sb.AppendLine("        }");

            // ─── Phase D mutate helpers: single-evaluation read-modify-
            // write for compound assignments and ++/--. The naive
            // rewrite for `obj.X += amount` was
            //   __setOn(obj, "X", __getOn<T>(obj, "X") + (amount))
            // which evaluates `obj` twice — broken for receivers with
            // side effects (singleton properties that build state, lazy
            // initializers, anything that can return a different
            // instance on the second call). These helpers take the
            // mutator as a delegate so the receiver / type / name is
            // captured exactly once and the rewriter can pass the live
            // value through __cur to the binary op.
            sb.AppendLine();
            sb.AppendLine("        T __mutate<T>(string name, System.Func<T, T> fn)");
            sb.AppendLine("        {");
            sb.AppendLine("            var __cur = __get<T>(name);");
            sb.AppendLine("            var __next = fn(__cur);");
            sb.AppendLine("            __set(name, __next);");
            sb.AppendLine("            return __next;");
            sb.AppendLine("        }");

            sb.AppendLine("        T __mutateOn<T>(object target, string name, System.Func<T, T> fn)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (target == null) throw new System.ArgumentNullException(nameof(target));");
            sb.AppendLine("            var __cur = __getOn<T>(target, name);");
            sb.AppendLine("            var __next = fn(__cur);");
            sb.AppendLine("            __setOn(target, name, __next);");
            sb.AppendLine("            return __next;");
            sb.AppendLine("        }");

            sb.AppendLine("        T __mutateStatic<T>(System.Type type, string name, System.Func<T, T> fn)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (type == null) throw new System.ArgumentNullException(nameof(type));");
            sb.AppendLine("            var __cur = __getStatic<T>(type, name);");
            sb.AppendLine("            var __next = fn(__cur);");
            sb.AppendLine("            __setStatic(type, name, __next);");
            sb.AppendLine("            return __next;");
            sb.AppendLine("        }");

            sb.AppendLine();
            // Wrap the user body in a void local function. The Prefix
            // method itself returns bool (Harmony's "skip original"
            // signal), but the user's body looks and feels like an
            // ordinary void method body — guard clauses (`if (!ready)
            // return;`), early-exit returns, copy-paste of an existing
            // void method, all valid C#. Inserting that text directly
            // into a bool Prefix would have CS0126'd on every bare
            // `return;`. The local function captures __instance, the
            // method parameters, and the __get / __set / __call
            // helpers from the enclosing scope, so the user-visible
            // shape is unchanged.
            sb.AppendLine("        // ===== user patch body =====");
            sb.AppendLine("        void __exec()");
            sb.AppendLine("        {");
            sb.AppendLine(spec.PatchBody ?? string.Empty);
            sb.AppendLine("        }");
            sb.AppendLine("        // ===== end =====");
            sb.AppendLine();
            sb.AppendLine("        __exec();");
            sb.AppendLine("        return false; // Harmony Prefix: skip the original method body");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            if (hasNs) sb.AppendLine("}"); // close namespace block
            return sb.ToString();
        }
    }

    /// <summary>
    /// Reflection-only adapter to Harmony. Lets PatchEngine compile when
    /// 0Harmony.dll isn't installed yet (the user gets a clear error at
    /// the first Apply instead of a missing-asmdef-reference build
    /// failure on import).
    /// </summary>
    internal static class HarmonyBridge
    {
        private static Type _harmonyType;
        private static Type _harmonyMethodType;
        private static object _instance;
        private static MethodInfo _patchMethod;
        private static MethodInfo _unpatchSpecificMethod;

        private static void EnsureLoaded()
        {
            if (_instance != null) return;

            Assembly harmonyAsm = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(a.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase))
                {
                    harmonyAsm = a;
                    break;
                }
            }
            if (harmonyAsm == null)
            {
                throw new InvalidOperationException(
                    "Harmony is not loaded. Run Tools / Roslyn REPL / Install Harmony to enable runtime method patching.");
            }

            _harmonyType = harmonyAsm.GetType("HarmonyLib.Harmony")
                ?? throw new InvalidOperationException("HarmonyLib.Harmony type not found in 0Harmony assembly.");
            _harmonyMethodType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                ?? throw new InvalidOperationException("HarmonyLib.HarmonyMethod type not found.");

            _instance = Activator.CreateInstance(_harmonyType, PatchEngine.HarmonyId);

            // Harmony.Patch(MethodBase original, HarmonyMethod prefix = null,
            //               HarmonyMethod postfix = null, HarmonyMethod transpiler = null,
            //               HarmonyMethod finalizer = null, HarmonyMethod ilmanipulator = null)
            //
            // We bind to the simplest overload available across recent
            // Harmony versions: pass prefix, leave the rest null.
            _patchMethod = _harmonyType.GetMethods()
                .FirstOrDefault(m => m.Name == "Patch"
                                  && m.GetParameters().Length >= 2
                                  && m.GetParameters()[0].ParameterType == typeof(MethodBase));
            if (_patchMethod == null)
                throw new InvalidOperationException("HarmonyLib.Harmony.Patch overload not found.");

            // Harmony.Unpatch(MethodBase original, MethodInfo patch) — the
            // surgical overload that removes exactly our prefix.
            _unpatchSpecificMethod = _harmonyType.GetMethod("Unpatch",
                new[] { typeof(MethodBase), typeof(MethodInfo) });
            if (_unpatchSpecificMethod == null)
                throw new InvalidOperationException("HarmonyLib.Harmony.Unpatch(MethodBase, MethodInfo) overload not found.");
        }

        public static void Patch(MethodInfo target, MethodInfo prefix)
        {
            EnsureLoaded();
            var harmonyMethod = Activator.CreateInstance(_harmonyMethodType, prefix);

            // Build the args array for whichever Patch overload we bound to.
            var pars = _patchMethod.GetParameters();
            var args = new object[pars.Length];
            args[0] = target;
            args[1] = harmonyMethod;
            // Remaining parameters (postfix / transpiler / finalizer / ...)
            // accept null — Harmony treats null as "no patch of this kind".
            _patchMethod.Invoke(_instance, args);
        }

        public static void Unpatch(MethodInfo target, MethodInfo prefix)
        {
            EnsureLoaded();
            _unpatchSpecificMethod.Invoke(_instance, new object[] { target, prefix });
        }
    }
}
