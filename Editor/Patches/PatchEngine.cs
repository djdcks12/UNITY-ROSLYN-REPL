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

            // Wrapper-level static helpers + cache for `base.X(...)`
            // non-virtual invocation. The cache lives on the wrapper
            // (one per Apply, recycled per re-apply) and stores
            // delegates built via Reflection.Emit so repeated
            // `base.OnEnable()` calls within one patch session reuse
            // the same DynamicMethod. Phase D's rewriter pre-pass
            // replaces `base.X(args)` with `__callBase<R>("X", args)`,
            // which routes through __BaseInvokeFromDerived → either
            // the cached delegate or a freshly emitted one.
            sb.AppendLine("    private static readonly System.Collections.Generic.Dictionary<System.Reflection.MethodInfo, System.Func<object, object[], object>> __BaseInvokerCache = new System.Collections.Generic.Dictionary<System.Reflection.MethodInfo, System.Func<object, object[], object>>();");
            sb.AppendLine();
            sb.AppendLine("    private static object __BaseInvokeFromDerived(object instance, System.Type derivedDecl, string name, object[] args)");
            sb.AppendLine("    {");
            sb.AppendLine("        args = args ?? new object[0];");
            // Collect ALL matching name+arity candidates across the
            // base chain (instead of stopping at the first) so we can
            // disambiguate same-arity overloads at runtime by their
            // argument types. A first-hit-wins walk would silently
            // pick `Apply(int)` for `base.Apply("hi")` when both
            // overloads live on the same base class.
            sb.AppendLine("        var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("        var ms = new System.Collections.Generic.List<System.Reflection.MethodInfo>();");
            sb.AppendLine("        for (var bt = derivedDecl.BaseType; bt != null; bt = bt.BaseType)");
            sb.AppendLine("        {");
            sb.AppendLine("            foreach (var mm in bt.GetMethods(bf))");
            sb.AppendLine("            {");
            sb.AppendLine("                if (mm.Name == name && mm.GetParameters().Length == args.Length) ms.Add(mm);");
            sb.AppendLine("            }");
            sb.AppendLine("            if (ms.Count > 0) break;"); // first level that has any match wins (matches C# `base` shadowing)
            sb.AppendLine("        }");
            sb.AppendLine("        if (ms.Count == 0) throw new System.InvalidOperationException(\"Base method '\" + name + \"(\" + args.Length + \" args)' not found on the base chain of \" + derivedDecl.Name);");
            sb.AppendLine();
            sb.AppendLine("        System.Reflection.MethodInfo m;");
            sb.AppendLine("        if (ms.Count == 1) { m = ms[0]; }");
            sb.AppendLine("        else");
            sb.AppendLine("        {");
            // Specificity scoring across same-arity base candidates.
            // Same policy as __InvokeReflective so `base.Foo("x")`
            // routing matches the rewriter's compile-time pick.
            sb.AppendLine("            var argTypes = new System.Type[args.Length];");
            sb.AppendLine("            for (int i = 0; i < args.Length; i++) argTypes[i] = args[i] == null ? null : args[i].GetType();");
            sb.AppendLine("            System.Reflection.MethodInfo best = null;");
            sb.AppendLine("            int bestScore = -1;");
            sb.AppendLine("            foreach (var c in ms)");
            sb.AppendLine("            {");
            sb.AppendLine("                var pars = c.GetParameters();");
            sb.AppendLine("                int score = 0;");
            sb.AppendLine("                bool ok = true;");
            sb.AppendLine("                for (int i = 0; i < pars.Length; i++)");
            sb.AppendLine("                {");
            sb.AppendLine("                    if (argTypes[i] == null) continue;");
            sb.AppendLine("                    var pt = pars[i].ParameterType;");
            sb.AppendLine("                    if (pt == argTypes[i]) score += 2;");
            sb.AppendLine("                    else if (pt.IsAssignableFrom(argTypes[i])) score += 1;");
            sb.AppendLine("                    else { ok = false; break; }");
            sb.AppendLine("                }");
            sb.AppendLine("                if (!ok) continue;");
            sb.AppendLine("                if (score > bestScore) { best = c; bestScore = score; }");
            sb.AppendLine("            }");
            sb.AppendLine("            m = best ?? ms[0];");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        System.Func<object, object[], object> del;");
            sb.AppendLine("        lock (__BaseInvokerCache) { __BaseInvokerCache.TryGetValue(m, out del); }");
            sb.AppendLine("        if (del == null)");
            sb.AppendLine("        {");
            sb.AppendLine("            del = __BuildBaseInvoker(m);");
            sb.AppendLine("            lock (__BaseInvokerCache) { __BaseInvokerCache[m] = del; }");
            sb.AppendLine("        }");
            sb.AppendLine("        return del(instance, args);");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Strict-match variant — when the rewriter already knows
            // the exact base overload, skip runtime narrowing entirely
            // and look up MethodInfo by name + paramTypes. Reuses the
            // same delegate cache via __BuildBaseInvoker so repeated
            // calls amortize the IL emit cost. Falls back to the
            // narrowing path when paramTypes is null or no exact
            // overload is found (defensive).
            sb.AppendLine("    private static object __BaseInvokeFromDerivedExact(object instance, System.Type derivedDecl, string name, System.Type[] paramTypes, object[] args)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (paramTypes == null) return __BaseInvokeFromDerived(instance, derivedDecl, name, args);");
            sb.AppendLine("        args = args ?? new object[0];");
            sb.AppendLine("        System.Reflection.MethodInfo m = null;");
            sb.AppendLine("        var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("        for (var bt = derivedDecl.BaseType; bt != null && m == null; bt = bt.BaseType)");
            sb.AppendLine("        {");
            sb.AppendLine("            foreach (var mm in bt.GetMethods(bf))");
            sb.AppendLine("            {");
            sb.AppendLine("                if (mm.Name != name) continue;");
            sb.AppendLine("                var pars = mm.GetParameters();");
            sb.AppendLine("                if (pars.Length != paramTypes.Length) continue;");
            sb.AppendLine("                bool ok = true;");
            sb.AppendLine("                for (int i = 0; i < pars.Length; i++)");
            sb.AppendLine("                {");
            sb.AppendLine("                    if (pars[i].ParameterType != paramTypes[i]) { ok = false; break; }");
            sb.AppendLine("                }");
            sb.AppendLine("                if (ok) { m = mm; break; }");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("        if (m == null) return __BaseInvokeFromDerived(instance, derivedDecl, name, args);");
            sb.AppendLine();
            sb.AppendLine("        System.Func<object, object[], object> del;");
            sb.AppendLine("        lock (__BaseInvokerCache) { __BaseInvokerCache.TryGetValue(m, out del); }");
            sb.AppendLine("        if (del == null)");
            sb.AppendLine("        {");
            sb.AppendLine("            del = __BuildBaseInvoker(m);");
            sb.AppendLine("            lock (__BaseInvokerCache) { __BaseInvokerCache[m] = del; }");
            sb.AppendLine("        }");
            sb.AppendLine("        return del(instance, args);");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Build a Func<object, object[], object> delegate that
            // invokes `m` *non-virtually* (IL `call`, not `callvirt`).
            // MethodInfo.Invoke + Delegate.CreateDelegate both go
            // through virtual dispatch for non-final virtual methods,
            // which would re-enter the patched override and infinite-
            // loop. DynamicMethod with EmitCall(OpCodes.Call, ...) is
            // the only standard path that explicitly calls the base
            // implementation.
            sb.AppendLine("    private static System.Func<object, object[], object> __BuildBaseInvoker(System.Reflection.MethodInfo m)");
            sb.AppendLine("    {");
            sb.AppendLine("        var dyn = new System.Reflection.Emit.DynamicMethod(");
            sb.AppendLine("            \"RrCallBase_\" + m.Name + \"_\" + System.Guid.NewGuid().ToString(\"N\").Substring(0, 8),");
            sb.AppendLine("            typeof(object),");
            sb.AppendLine("            new System.Type[] { typeof(object), typeof(object[]) },");
            sb.AppendLine("            m.DeclaringType,");
            sb.AppendLine("            true);");
            sb.AppendLine("        var il = dyn.GetILGenerator();");
            sb.AppendLine();
            sb.AppendLine("        // load instance, cast to declaring type");
            sb.AppendLine("        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);");
            sb.AppendLine("        if (m.DeclaringType.IsValueType) il.Emit(System.Reflection.Emit.OpCodes.Unbox_Any, m.DeclaringType);");
            sb.AppendLine("        else il.Emit(System.Reflection.Emit.OpCodes.Castclass, m.DeclaringType);");
            sb.AppendLine();
            sb.AppendLine("        // load each arg from the object[] and unbox/cast as appropriate");
            sb.AppendLine("        var pars = m.GetParameters();");
            sb.AppendLine("        for (int i = 0; i < pars.Length; i++)");
            sb.AppendLine("        {");
            sb.AppendLine("            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);");
            sb.AppendLine("            il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, i);");
            sb.AppendLine("            il.Emit(System.Reflection.Emit.OpCodes.Ldelem_Ref);");
            sb.AppendLine("            var pt = pars[i].ParameterType;");
            sb.AppendLine("            if (pt.IsValueType) il.Emit(System.Reflection.Emit.OpCodes.Unbox_Any, pt);");
            sb.AppendLine("            else il.Emit(System.Reflection.Emit.OpCodes.Castclass, pt);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        // non-virtual call");
            sb.AppendLine("        il.EmitCall(System.Reflection.Emit.OpCodes.Call, m, null);");
            sb.AppendLine();
            sb.AppendLine("        // return: void → ldnull, valuetype → box, reference → as-is");
            sb.AppendLine("        if (m.ReturnType == typeof(void)) il.Emit(System.Reflection.Emit.OpCodes.Ldnull);");
            sb.AppendLine("        else if (m.ReturnType.IsValueType) il.Emit(System.Reflection.Emit.OpCodes.Box, m.ReturnType);");
            sb.AppendLine("        il.Emit(System.Reflection.Emit.OpCodes.Ret);");
            sb.AppendLine();
            sb.AppendLine("        return (System.Func<object, object[], object>)dyn.CreateDelegate(typeof(System.Func<object, object[], object>));");
            sb.AppendLine("    }");
            sb.AppendLine();

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
            // Same-instance helpers walk the base chain from the
            // *declaring type* (not the runtime type of __instance).
            // Why: Reflection.BindingFlags.FlattenHierarchy doesn't
            // include private inherited members — so a body that
            // touches `_hp` declared in a base class would silently
            // fail at runtime when the patched method runs on a
            // derived instance. Walking from the declaring type with
            // BindingFlags.DeclaredOnly + .BaseType iteration covers
            // every inheritance level. Starting from declaringType
            // (not the runtime type) also avoids accidental
            // shadowing: a derived class with its own `_hp` won't
            // hijack a body whose source intent was the base's `_hp`.
            sb.AppendLine("        T __get<T>(string name)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var __t = typeof({declType});");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("            while (__t != null)");
            sb.AppendLine("            {");
            sb.AppendLine("                var __f = __t.GetField(name, __bf);");
            sb.AppendLine("                if (__f != null) return (T)__f.GetValue(__instance);");
            sb.AppendLine("                var __p = __t.GetProperty(name, __bf);");
            sb.AppendLine("                if (__p != null) return (T)__p.GetValue(__instance);");
            sb.AppendLine("                __t = __t.BaseType;");
            sb.AppendLine("            }");
            sb.AppendLine($"            throw new System.InvalidOperationException(\"Member '\" + name + \"' not found on \" + typeof({declType}).Name + \" or any base\");");
            sb.AppendLine("        }");

            sb.AppendLine("        void __set(string name, object value)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var __t = typeof({declType});");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("            while (__t != null)");
            sb.AppendLine("            {");
            sb.AppendLine("                var __f = __t.GetField(name, __bf);");
            sb.AppendLine("                if (__f != null) { __f.SetValue(__instance, value); return; }");
            sb.AppendLine("                var __p = __t.GetProperty(name, __bf);");
            sb.AppendLine("                if (__p != null) { __p.SetValue(__instance, value); return; }");
            sb.AppendLine("                __t = __t.BaseType;");
            sb.AppendLine("            }");
            sb.AppendLine($"            throw new System.InvalidOperationException(\"Member '\" + name + \"' not found on \" + typeof({declType}).Name + \" or any base\");");
            sb.AppendLine("        }");

            sb.AppendLine("        T __call<T>(string name, params object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var __r = __InvokeReflective(typeof({declType}), __instance, name, args);");
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
            // External-instance helpers walk the runtime type's chain.
            // Different from the same-instance helpers because the
            // user wrote `someObj.X` — they explicitly mean the
            // runtime resolution path, not the patch's declaring
            // type. We still walk DeclaredOnly + BaseType to catch
            // private inherited members FlattenHierarchy ignores.
            sb.AppendLine("        T __getOn<T>(object target, string name)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (target == null) throw new System.ArgumentNullException(nameof(target));");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("            var __t = target.GetType();");
            sb.AppendLine("            while (__t != null)");
            sb.AppendLine("            {");
            sb.AppendLine("                var __f = __t.GetField(name, __bf);");
            sb.AppendLine("                if (__f != null) return (T)__f.GetValue(target);");
            sb.AppendLine("                var __p = __t.GetProperty(name, __bf);");
            sb.AppendLine("                if (__p != null) return (T)__p.GetValue(target);");
            sb.AppendLine("                __t = __t.BaseType;");
            sb.AppendLine("            }");
            sb.AppendLine("            throw new System.InvalidOperationException(\"Member '\" + name + \"' not found on \" + target.GetType().Name + \" or any base\");");
            sb.AppendLine("        }");

            sb.AppendLine("        void __setOn(object target, string name, object value)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (target == null) throw new System.ArgumentNullException(nameof(target));");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("            var __t = target.GetType();");
            sb.AppendLine("            while (__t != null)");
            sb.AppendLine("            {");
            sb.AppendLine("                var __f = __t.GetField(name, __bf);");
            sb.AppendLine("                if (__f != null) { __f.SetValue(target, value); return; }");
            sb.AppendLine("                var __p = __t.GetProperty(name, __bf);");
            sb.AppendLine("                if (__p != null) { __p.SetValue(target, value); return; }");
            sb.AppendLine("                __t = __t.BaseType;");
            sb.AppendLine("            }");
            sb.AppendLine("            throw new System.InvalidOperationException(\"Member '\" + name + \"' not found on \" + target.GetType().Name + \" or any base\");");
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
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("            var __t = type;");
            sb.AppendLine("            while (__t != null)");
            sb.AppendLine("            {");
            sb.AppendLine("                var __f = __t.GetField(name, __bf);");
            sb.AppendLine("                if (__f != null) return (T)__f.GetValue(null);");
            sb.AppendLine("                var __p = __t.GetProperty(name, __bf);");
            sb.AppendLine("                if (__p != null) return (T)__p.GetValue(null);");
            sb.AppendLine("                __t = __t.BaseType;");
            sb.AppendLine("            }");
            sb.AppendLine("            throw new System.InvalidOperationException(\"Static member '\" + name + \"' not found on \" + type.Name + \" or any base\");");
            sb.AppendLine("        }");

            sb.AppendLine("        void __setStatic(System.Type type, string name, object value)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (type == null) throw new System.ArgumentNullException(nameof(type));");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("            var __t = type;");
            sb.AppendLine("            while (__t != null)");
            sb.AppendLine("            {");
            sb.AppendLine("                var __f = __t.GetField(name, __bf);");
            sb.AppendLine("                if (__f != null) { __f.SetValue(null, value); return; }");
            sb.AppendLine("                var __p = __t.GetProperty(name, __bf);");
            sb.AppendLine("                if (__p != null) { __p.SetValue(null, value); return; }");
            sb.AppendLine("                __t = __t.BaseType;");
            sb.AppendLine("            }");
            sb.AppendLine("            throw new System.InvalidOperationException(\"Static member '\" + name + \"' not found on \" + type.Name + \" or any base\");");
            sb.AppendLine("        }");

            sb.AppendLine("        T __callStatic<T>(System.Type type, string name, params object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (type == null) throw new System.ArgumentNullException(nameof(type));");
            sb.AppendLine("            var __r = __InvokeReflective(type, null, name, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            // `base.X(args)` → __callBase<R>("X", args). Routes through
            // __BaseInvokeFromDerived which walks the declaring type's
            // base chain and dispatches via a non-virtual DynamicMethod
            // delegate. Without this the override would re-enter
            // itself and infinite-loop, since Harmony's Prefix
            // intercepts the patched method's normal vtable slot.
            sb.AppendLine("        T __callBase<T>(string name, params object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var __r = __BaseInvokeFromDerived(__instance, typeof({declType}), name, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            // ─── Phase D paramTypes-aware variants ───────────────
            // C# doesn't allow local-function overloading, so these
            // are distinct names (`...X`) from the legacy
            // `__call` / `__callOn` / `__callStatic` / `__callBase`.
            // The rewriter emits these when it has compile-time
            // knowledge of the target method's exact parameter types
            // (the `argTypes`-aware FindMethodInfo / FindBaseMethod-
            // ByArgs paths). They perform *strict* parameter-type
            // matching against `paramTypes` instead of inferring
            // from runtime arg types. Two reasons this matters:
            //   1. null arg values lose their static type at runtime
            //      (`null.GetType()` is impossible) — without
            //      paramTypes, `Foo(string)` vs `Foo(object)` for a
            //      null call would tie at score 0 and pick first-
            //      found.
            //   2. numeric widening conversions: `base.Foo(1)`
            //      compiles to `Foo(long)`, but the boxed value
            //      flowing through `object[] args` is a boxed int.
            //      The rewriter pre-casts to the target paramType so
            //      the boxed type matches what the DynamicMethod
            //      invoker's Unbox_Any expects.
            sb.AppendLine("        T __callX<T>(string name, System.Type[] paramTypes, object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var __t = typeof({declType});");
            sb.AppendLine("            var __r = __InvokeReflectiveExact(__t, __instance, name, paramTypes, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            sb.AppendLine("        T __callOnX<T>(object target, string name, System.Type[] paramTypes, object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (target == null) throw new System.ArgumentNullException(nameof(target));");
            sb.AppendLine("            var __r = __InvokeReflectiveExact(target.GetType(), target, name, paramTypes, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            sb.AppendLine("        T __callStaticX<T>(System.Type type, string name, System.Type[] paramTypes, object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (type == null) throw new System.ArgumentNullException(nameof(type));");
            sb.AppendLine("            var __r = __InvokeReflectiveExact(type, null, name, paramTypes, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            sb.AppendLine("        T __callBaseX<T>(string name, System.Type[] paramTypes, object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var __r = __BaseInvokeFromDerivedExact(__instance, typeof({declType}), name, paramTypes, args);");
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

            // ─── Generic paramTypes-aware variants ───────────────
            // Same role as __callX/__callOnX/__callStaticX for the
            // non-generic path: rewriter passes the post-substitution
            // parameter types so the helper performs strict matching
            // instead of inferring from runtime args. Critical for
            // null arguments (no runtime type) and implicit widening
            // (boxed int reaching a long parameter).
            sb.AppendLine("        T __callGX<T>(string name, System.Type[] typeArgs, System.Type[] paramTypes, object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var __t = typeof({declType});");
            sb.AppendLine("            var __r = __InvokeReflectiveGenericExact(__t, __instance, name, typeArgs, paramTypes, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            sb.AppendLine("        T __callGOnX<T>(object target, string name, System.Type[] typeArgs, System.Type[] paramTypes, object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (target == null) throw new System.ArgumentNullException(nameof(target));");
            sb.AppendLine("            var __r = __InvokeReflectiveGenericExact(target.GetType(), target, name, typeArgs, paramTypes, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            sb.AppendLine("        T __callGStaticX<T>(System.Type type, string name, System.Type[] typeArgs, System.Type[] paramTypes, object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (type == null) throw new System.ArgumentNullException(nameof(type));");
            sb.AppendLine("            var __r = __InvokeReflectiveGenericExact(type, null, name, typeArgs, paramTypes, args);");
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
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("            args = args ?? new object[0];");
            sb.AppendLine("            var __ms = new System.Collections.Generic.List<System.Reflection.MethodInfo>();");
            // Walk the base chain so private inherited methods are
            // found — FlattenHierarchy hides those, see field/property
            // helpers above for the same trade-off. First-declared-
            // wins shadowing matches C# semantics: a derived type that
            // re-declares a method with `new` shadows the base.
            sb.AppendLine("            var __wt = type;");
            sb.AppendLine("            while (__wt != null)");
            sb.AppendLine("            {");
            sb.AppendLine("                foreach (var __mm in __wt.GetMethods(__bf))");
            sb.AppendLine("                {");
            sb.AppendLine("                    if (__mm.Name != name) continue;");
            sb.AppendLine("                    if (__mm.GetParameters().Length != args.Length) continue;");
            sb.AppendLine("                    __ms.Add(__mm);");
            sb.AppendLine("                }");
            sb.AppendLine("                __wt = __wt.BaseType;");
            sb.AppendLine("            }");
            sb.AppendLine("            if (__ms.Count == 0) throw new System.InvalidOperationException(\"Method '\" + name + \"(\" + args.Length + \" args)' not found on \" + type.Name + \" or any base\");");
            sb.AppendLine("            System.Reflection.MethodInfo __m;");
            sb.AppendLine("            if (__ms.Count == 1) { __m = __ms[0]; }");
            sb.AppendLine("            else");
            sb.AppendLine("            {");
            // Specificity-based tie-break. The previous policy was
            // "first candidate whose params are all assignable from
            // arg runtime types" — but `Foo(object)` and `Foo(string)`
            // are *both* assignable for a string argument, so
            // first-match leaked the more-general overload through
            // whenever GetMethods returned it first. Score each
            // candidate (exact = 2, assignable = 1, mismatch =
            // reject) and keep the highest. Mirrors C#'s "more
            // specific wins" overload resolution and stays in lock-
            // step with the rewriter's compile-time overload pick.
            sb.AppendLine("                var __argTypes = new System.Type[args.Length];");
            sb.AppendLine("                for (int __i = 0; __i < args.Length; __i++) __argTypes[__i] = args[__i] == null ? null : args[__i].GetType();");
            sb.AppendLine("                System.Reflection.MethodInfo __best = null;");
            sb.AppendLine("                int __bestScore = -1;");
            sb.AppendLine("                foreach (var __c in __ms)");
            sb.AppendLine("                {");
            sb.AppendLine("                    var __pars = __c.GetParameters();");
            sb.AppendLine("                    int __score = 0;");
            sb.AppendLine("                    bool __ok = true;");
            sb.AppendLine("                    for (int __i = 0; __i < __pars.Length; __i++)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        if (__argTypes[__i] == null) continue;");
            sb.AppendLine("                        var __pt = __pars[__i].ParameterType;");
            sb.AppendLine("                        if (__pt == __argTypes[__i]) __score += 2;");
            sb.AppendLine("                        else if (__pt.IsAssignableFrom(__argTypes[__i])) __score += 1;");
            sb.AppendLine("                        else { __ok = false; break; }");
            sb.AppendLine("                    }");
            sb.AppendLine("                    if (!__ok) continue;");
            sb.AppendLine("                    if (__score > __bestScore) { __best = __c; __bestScore = __score; }");
            sb.AppendLine("                }");
            sb.AppendLine("                __m = __best ?? __ms[0];");
            sb.AppendLine("            }");
            sb.AppendLine("            return __m.Invoke(instance, args);");
            sb.AppendLine("        }");

            // Strict paramTypes match — bypasses the specificity
            // scoring path entirely. The rewriter uses this when it
            // already knows the exact target overload at compile
            // time, so we don't risk a runtime tie-break landing on
            // a different method. Falls back to specificity scoring
            // when paramTypes is null (or strict match couldn't
            // find anything — defensive, shouldn't hit in practice).
            sb.AppendLine("        object __InvokeReflectiveExact(System.Type type, object instance, string name, System.Type[] paramTypes, object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (paramTypes == null) return __InvokeReflective(type, instance, name, args);");
            sb.AppendLine("            args = args ?? new object[0];");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("            for (var __wt = type; __wt != null; __wt = __wt.BaseType)");
            sb.AppendLine("            {");
            sb.AppendLine("                foreach (var __mm in __wt.GetMethods(__bf))");
            sb.AppendLine("                {");
            sb.AppendLine("                    if (__mm.Name != name) continue;");
            sb.AppendLine("                    if (__mm.IsGenericMethodDefinition) continue;");
            sb.AppendLine("                    var __pars = __mm.GetParameters();");
            sb.AppendLine("                    if (__pars.Length != paramTypes.Length) continue;");
            sb.AppendLine("                    bool __ok = true;");
            sb.AppendLine("                    for (int __i = 0; __i < __pars.Length; __i++)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        if (__pars[__i].ParameterType != paramTypes[__i]) { __ok = false; break; }");
            sb.AppendLine("                    }");
            sb.AppendLine("                    if (__ok) return __mm.Invoke(instance, args);");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            return __InvokeReflective(type, instance, name, args);");
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
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("            args = args ?? new object[0];");
            sb.AppendLine("            typeArgs = typeArgs ?? new System.Type[0];");
            sb.AppendLine("            var __ms = new System.Collections.Generic.List<System.Reflection.MethodInfo>();");
            // Walk the base chain — DeclaredOnly + manual climb so
            // private inherited generics are visible to user bodies
            // touching `BaseClass.PrivateGeneric<T>(...)` style calls.
            sb.AppendLine("            var __wt = type;");
            sb.AppendLine("            while (__wt != null)");
            sb.AppendLine("            {");
            sb.AppendLine("                foreach (var __mm in __wt.GetMethods(__bf))");
            sb.AppendLine("                {");
            sb.AppendLine("                    if (__mm.Name != name) continue;");
            sb.AppendLine("                    if (__mm.GetParameters().Length != args.Length) continue;");
            sb.AppendLine("                    if (typeArgs.Length > 0)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        if (!__mm.IsGenericMethodDefinition) continue;");
            sb.AppendLine("                        if (__mm.GetGenericArguments().Length != typeArgs.Length) continue;");
            sb.AppendLine("                    }");
            sb.AppendLine("                    else if (__mm.IsGenericMethodDefinition) continue;");
            sb.AppendLine("                    __ms.Add(__mm);");
            sb.AppendLine("                }");
            sb.AppendLine("                __wt = __wt.BaseType;");
            sb.AppendLine("            }");
            sb.AppendLine("            if (__ms.Count == 0) throw new System.InvalidOperationException(\"Method '\" + name + \"<\" + typeArgs.Length + \">(\" + args.Length + \" args)' not found on \" + type.Name + \" or any base\");");
            sb.AppendLine("            System.Reflection.MethodInfo __m;");
            sb.AppendLine("            if (__ms.Count == 1) { __m = __ms[0]; }");
            sb.AppendLine("            else");
            sb.AppendLine("            {");
            // Multiple overloads sharing name + arity + generic arity.
            // Specificity scoring (exact = 2, assignable = 1,
            // mismatch = reject) — same policy as the non-generic
            // helper. For generic candidates, instantiate per-
            // candidate with typeArgs *first* so the parameter types
            // are concrete; then score the same way.
            sb.AppendLine("                var __argTypes = new System.Type[args.Length];");
            sb.AppendLine("                for (int __i = 0; __i < args.Length; __i++) __argTypes[__i] = args[__i] == null ? null : args[__i].GetType();");
            sb.AppendLine("                System.Reflection.MethodInfo __best = null;");
            sb.AppendLine("                int __bestScore = -1;");
            sb.AppendLine("                foreach (var __c in __ms)");
            sb.AppendLine("                {");
            sb.AppendLine("                    var __inst = (typeArgs.Length > 0 && __c.IsGenericMethodDefinition) ? __c.MakeGenericMethod(typeArgs) : __c;");
            sb.AppendLine("                    var __pars = __inst.GetParameters();");
            sb.AppendLine("                    int __score = 0;");
            sb.AppendLine("                    bool __ok = true;");
            sb.AppendLine("                    for (int __i = 0; __i < __pars.Length; __i++)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        if (__argTypes[__i] == null) continue;");
            sb.AppendLine("                        var __pt = __pars[__i].ParameterType;");
            sb.AppendLine("                        if (__pt == __argTypes[__i]) __score += 2;");
            sb.AppendLine("                        else if (__pt.IsAssignableFrom(__argTypes[__i])) __score += 1;");
            sb.AppendLine("                        else { __ok = false; break; }");
            sb.AppendLine("                    }");
            sb.AppendLine("                    if (!__ok) continue;");
            sb.AppendLine("                    if (__score > __bestScore) { __best = __c; __bestScore = __score; }");
            sb.AppendLine("                }");
            sb.AppendLine("                __m = __best ?? __ms[0];");
            sb.AppendLine("            }");
            sb.AppendLine("            if (typeArgs.Length > 0 && __m.IsGenericMethodDefinition) __m = __m.MakeGenericMethod(typeArgs);");
            sb.AppendLine("            return __m.Invoke(instance, args);");
            sb.AppendLine("        }");

            // Strict paramTypes match for generic methods. Walks the
            // base chain like __InvokeReflectiveGeneric, but instead
            // of specificity scoring after MakeGenericMethod, looks
            // for the *exact* candidate whose substituted parameter
            // types match `paramTypes` element-wise. Falls back to
            // the specificity-scoring path when paramTypes is null
            // or no exact match exists (defensive — shouldn't hit
            // in practice when the rewriter routes here).
            sb.AppendLine("        object __InvokeReflectiveGenericExact(System.Type type, object instance, string name, System.Type[] typeArgs, System.Type[] paramTypes, object[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (paramTypes == null) return __InvokeReflectiveGeneric(type, instance, name, typeArgs, args);");
            sb.AppendLine("            args = args ?? new object[0];");
            sb.AppendLine("            typeArgs = typeArgs ?? new System.Type[0];");
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("            for (var __wt = type; __wt != null; __wt = __wt.BaseType)");
            sb.AppendLine("            {");
            sb.AppendLine("                foreach (var __mm in __wt.GetMethods(__bf))");
            sb.AppendLine("                {");
            sb.AppendLine("                    if (__mm.Name != name) continue;");
            sb.AppendLine("                    if (__mm.GetParameters().Length != paramTypes.Length) continue;");
            sb.AppendLine("                    if (typeArgs.Length > 0)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        if (!__mm.IsGenericMethodDefinition) continue;");
            sb.AppendLine("                        if (__mm.GetGenericArguments().Length != typeArgs.Length) continue;");
            sb.AppendLine("                    }");
            sb.AppendLine("                    else if (__mm.IsGenericMethodDefinition) continue;");
            sb.AppendLine("                    var __inst = (typeArgs.Length > 0 && __mm.IsGenericMethodDefinition) ? __mm.MakeGenericMethod(typeArgs) : __mm;");
            sb.AppendLine("                    var __pars = __inst.GetParameters();");
            sb.AppendLine("                    bool __ok = true;");
            sb.AppendLine("                    for (int __i = 0; __i < __pars.Length; __i++)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        if (__pars[__i].ParameterType != paramTypes[__i]) { __ok = false; break; }");
            sb.AppendLine("                    }");
            sb.AppendLine("                    if (__ok) return __inst.Invoke(instance, args);");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            return __InvokeReflectiveGeneric(type, instance, name, typeArgs, args);");
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
