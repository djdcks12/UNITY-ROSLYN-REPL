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

            // Already applied? Revert first so the user can iterate
            // without manually unwinding state every edit.
            if (_applied.ContainsKey(spec.Key)) Revert(spec);

            // 1. Resolve the target MethodInfo.
            var target = ResolveTargetMethod(spec);

            // 2. Generate + compile the wrapper class. Compile errors
            //    bubble up as a single Exception with the diagnostics
            //    formatted; the UI surface throws a row-failure marker.
            var className = "__ReplPatch_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var source = PatchCodeGenerator.Generate(spec, target, className);
            var asm = CompileToAssembly(source);
            var patchType = asm.GetType(className)
                ?? throw new InvalidOperationException($"Compiled patch is missing class '{className}'");
            var prefix = patchType.GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"Compiled patch is missing static Prefix method");

            // 3. Hand the prefix to Harmony.
            HarmonyBridge.Patch(target, prefix);

            // 4. Bookkeeping.
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

        private static Assembly CompileToAssembly(string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var compilation = CSharpCompilation.Create(
                assemblyName: "ReplPatch_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                syntaxTrees: new[] { tree },
                references: AssemblyReferenceCache.GetReferences(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

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
        public static string Generate(MethodPatchSpec spec, MethodInfo target, string className)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Reflection;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine();
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
            sb.AppendLine("            var __bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy;");
            sb.AppendLine("            var __m = __t.GetMethod(name, __bf);");
            sb.AppendLine("            if (__m == null) throw new System.InvalidOperationException(\"Member '\" + name + \"' not found on \" + __t.Name);");
            sb.AppendLine("            var __r = __m.Invoke(__instance, args);");
            sb.AppendLine("            return __r is T __cast ? __cast : default;");
            sb.AppendLine("        }");

            sb.AppendLine();
            sb.AppendLine("        // ===== user patch body =====");
            // User body verbatim. MVP forbids `return X;` (method is void)
            // but a bare `return;` to early-exit is fine.
            sb.AppendLine(spec.PatchBody ?? string.Empty);
            sb.AppendLine("        // ===== end =====");
            sb.AppendLine();
            sb.AppendLine("        return false; // Harmony Prefix: skip the original method body");
            sb.AppendLine("    }");
            sb.AppendLine("}");
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
