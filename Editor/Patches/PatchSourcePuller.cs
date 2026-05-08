using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynRepl.Editor.Core;
using UnityEditor;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// Locates the original `.cs` source for a target method and extracts
    /// the method body text. Used by Phase C of the Runtime Method Patch
    /// feature to pre-populate the patch editor with the existing
    /// implementation so the user can edit it in place rather than write
    /// from a blank slate.
    ///
    /// Two-step pipeline:
    ///   1. Resolve every <see cref="MonoScript"/> whose declared class
    ///      matches the target type. Returns one or more `.cs` paths
    ///      (multiple are possible for partial classes).
    ///   2. Parse each candidate with Roslyn, look for a method whose
    ///      identifier + parameter count match the target, and extract
    ///      the text between its outer braces.
    ///
    /// Returns a structured <see cref="PullResult"/> so the UI can show
    /// either the body or a specific diagnostic when extraction fails.
    /// </summary>
    public static class PatchSourcePuller
    {
        public class PullResult
        {
            public bool Success;
            /// <summary>Body text between the outer braces, no leading
            /// or trailing newline trim — preserves user indentation.</summary>
            public string Body;
            public string SourcePath;
            public string Error;
        }

        public static PullResult TryPullMethodBody(MethodInfo method)
        {
            if (method == null)
                return Fail("Method is null.");

            var declaringType = method.DeclaringType;
            if (declaringType == null)
                return Fail("Method has no declaring type (anonymous?).");

            var paths = ResolveScriptPaths(declaringType);
            if (paths.Count == 0)
                return Fail($"No MonoScript found for {declaringType.FullName}. " +
                            "Ensure the type lives in a `.cs` file inside Assets/ or Packages/.");

            var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
            string lastParseError = null;

            foreach (var path in paths)
            {
                string source;
                try { source = File.ReadAllText(path); }
                catch (Exception ex) { lastParseError = $"Could not read '{path}': {ex.Message}"; continue; }

                MethodDeclarationSyntax match;
                try { match = FindMethodNode(source, declaringType, method.Name, paramTypes); }
                catch (Exception ex) { lastParseError = $"Roslyn parse failed for '{path}': {ex.Message}"; continue; }

                if (match == null) continue;
                if (match.Body == null)
                {
                    // Expression-bodied (`=> expr`) — Phase C MVP doesn't
                    // unwrap those. Surface a specific message rather
                    // than silently returning empty.
                    return Fail($"{declaringType.Name}.{method.Name} is expression-bodied; Phase C MVP only extracts block-bodied methods. Edit the file to a `{{ … }}` body or write the patch from scratch.");
                }

                var body = ExtractBodyInside(source, match.Body);
                return new PullResult
                {
                    Success = true,
                    Body = body,
                    SourcePath = path,
                };
            }

            return Fail(
                $"Could not find a method matching {declaringType.Name}.{method.Name}({string.Join(", ", paramTypes.Select(t => t?.Name))}) " +
                $"in any of the {paths.Count} candidate file(s). " +
                (lastParseError != null ? "Last error: " + lastParseError : "Method may be auto-generated or in an assembly without source available."));
        }

        // ─── Phase D file context ──────────────────────────────────
        // Snapshot of the namespace + using directives the declaring
        // type's `.cs` file was authored in. Phase D's compiler
        // wrapper uses this so a pulled body — which routinely refers
        // to same-namespace types or types covered by a file-level
        // using — can compile without forcing the user to fully
        // qualify every name post-pull. Best-effort: when the source
        // can't be located (no MonoScript, IO failure, malformed
        // syntax) the context returns just the runtime namespace.
        //
        // The two using buckets matter because C# scoping does:
        //   - CompilationUnitUsings: declared at file top, before any
        //     namespace block. Apply file-wide; safe to emit at the
        //     wrapper's compilation unit too.
        //   - NamespaceScopedUsings: declared inside a `namespace { …
        //     }` block. Originally apply *only* inside that block.
        //     Hoisting them to the wrapper's compilation unit would
        //     change scoping (a relative `using SubNs;` resolves
        //     differently outside its namespace) and can collide with
        //     usings from sibling namespaces in the same file. We
        //     emit these inside the wrapper's namespace block so the
        //     original scoping is preserved.
        //
        // Only the namespace blocks that *enclose the target type*
        // contribute. A `using` declared inside an unrelated namespace
        // in the same file is intentionally dropped — that's exactly
        // the duplicate-alias / wrong-resolution risk the PR review
        // surfaced.
        public class FileContext
        {
            public string Namespace;                            // declaring type's runtime namespace
            public List<string> CompilationUnitUsings = new();  // file top-level usings
            public List<string> NamespaceScopedUsings = new();  // usings from namespaces enclosing the target
        }

        public static FileContext GetDeclaringFileContext(MethodInfo targetMethod)
        {
            var declaringType = targetMethod?.DeclaringType;
            var ctx = new FileContext { Namespace = declaringType?.Namespace };
            if (declaringType == null) return ctx;

            var paths = ResolveScriptPaths(declaringType);
            if (paths.Count == 0) return ctx;

            // Pick the *single* file that actually declares the method
            // body. C# file-level usings + aliases are per-file, not
            // per partial-class — `Player.Part1.cs` with
            // `using Model = A.Model;` and `Player.Part2.cs` with
            // `using Model = B.Model;` both compile independently,
            // but merging both into one wrapper compilation fails
            // with CS1537 (duplicate alias) before the user's body
            // even gets a chance. So: locate the partial that
            // contains the method, use that file's enclosing chain
            // only.
            //
            // Falls back to the first candidate path's first matching
            // type declaration when no syntactic method match is
            // found — auto-generated partials, source missing, etc.
            // Picking a single file (even a "wrong" one) is still
            // strictly safer than merging every file's aliases.
            string targetPath = null;
            SyntaxNode targetRoot = null;
            TypeDeclarationSyntax targetTypeDecl = null;

            if (targetMethod != null)
            {
                var paramTypes = targetMethod.GetParameters().Select(p => p.ParameterType).ToArray();
                foreach (var path in paths)
                {
                    if (TryFindMethodBearingDecl(path, declaringType, targetMethod.Name, paramTypes,
                        out var foundRoot, out var foundDecl))
                    {
                        targetPath = path;
                        targetRoot = foundRoot;
                        targetTypeDecl = foundDecl;
                        break;
                    }
                }
            }

            if (targetTypeDecl == null)
            {
                // Fallback: first path, first matching type
                // declaration in it.
                foreach (var path in paths)
                {
                    if (TryParseFile(path, out var root))
                    {
                        var decls = FindMatchingTypeDeclarations(root, declaringType);
                        if (decls.Count > 0)
                        {
                            targetPath = path;
                            targetRoot = root;
                            targetTypeDecl = decls[0];
                            break;
                        }
                    }
                }
                if (targetTypeDecl == null) return ctx;
            }

            var seenCu = new HashSet<string>(StringComparer.Ordinal);
            var seenNs = new HashSet<string>(StringComparer.Ordinal);
            CollectUsingsForTypeContext(targetTypeDecl, ctx, seenCu, seenNs);
            return ctx;
        }

        // Probe a candidate path for the *exact* method declaration
        // using SemanticModel-based parameter matching. Returns the
        // parsed root + the specific TypeDeclarationSyntax that
        // contains the method, so the caller can walk that exact
        // ancestor chain for usings.
        //
        // Cross-file file-context selection is strict: simple-name
        // matches like `Apply(Model)` against `Apply(Model)` are
        // *intentionally* not enough, because partial files can
        // legitimately have same-arity-and-simple-name parameters
        // resolving to different CLR types via per-file aliases:
        //
        //     Part1.cs: using Model = A.Model; void Apply(Model);
        //     Part2.cs: using Model = B.Model; void Apply(Model);
        //
        // The body-pull pipeline used to land on whichever file
        // appeared first in MonoScript order; the new semantic
        // matcher consults each file's own using/alias context and
        // returns true only when the resolved CLR type matches the
        // target's reflection Type[]. When semantics can't resolve
        // (rare — broken source, generic type parameters), we
        // return false rather than fall back to simple-name
        // matching, because the caller iterates to the next file
        // and the GetDeclaringFileContext fallback path handles the
        // "no precise match anywhere" tail case.
        private static bool TryFindMethodBearingDecl(
            string path,
            Type declaringType,
            string methodName,
            Type[] paramTypes,
            out SyntaxNode root,
            out TypeDeclarationSyntax typeDecl)
        {
            root = null;
            typeDecl = null;
            if (!TryParseFile(path, out root)) return false;

            var typeDecls = FindMatchingTypeDeclarations(root, declaringType);
            if (typeDecls.Count == 0) return false;

            int arity = paramTypes.Length;
            var model = BuildSingleFileSemanticModel(root);

            foreach (var td in typeDecls)
            {
                foreach (var member in td.Members)
                {
                    if (!(member is MethodDeclarationSyntax m)) continue;
                    if (m.Identifier.ValueText != methodName) continue;
                    if (m.ParameterList.Parameters.Count != arity) continue;

                    if (MatchesParamTypesSemantic(m, paramTypes, model))
                    {
                        typeDecl = td;
                        return true;
                    }
                }
            }

            return false;
        }

        // ─── Parameter-type matching ─────────────────────────────
        // Two matchers, used at different precision levels:
        //
        //   - Semantic: each candidate file gets its own single-file
        //     Compilation + SemanticModel so a `ParameterSyntax` like
        //     `Model m` resolves through that file's actual `using`
        //     directives + namespace scoping. Returns the resolved
        //     System.Type, which we compare for identity to the
        //     reflection Type[]. This is the only way to disambiguate
        //     across partial files where the same simple-name param
        //     means different CLR types because of per-file aliases:
        //         Part1.cs: using Model = A.Model; void Apply(Model);
        //         Part2.cs: using Model = B.Model; void Apply(Model);
        //
        //   - Syntactic: name-only comparison (full name → simple name
        //     → C# alias). Cheap, no Compilation cost, works when the
        //     file has only one arity-matching candidate (no
        //     ambiguity to break) or when SemanticModel build fails
        //     (compile-broken source, missing reference).
        //
        // Cross-file selection uses semantic-only — a syntactic match
        // there can leak the wrong file's using context into the
        // wrapper. Per-file body extraction tries semantic first and
        // falls back to syntactic only when SemanticModel can't
        // resolve at all, so a single-overload file in a project
        // without compile errors still pulls reliably.

        private static bool MatchesParamTypesSyntactic(MethodDeclarationSyntax m, Type[] paramTypes)
        {
            for (int i = 0; i < paramTypes.Length; i++)
            {
                var syntaxType = m.ParameterList.Parameters[i].Type?.ToString();
                if (string.IsNullOrEmpty(syntaxType)) return false;
                var t = paramTypes[i];
                if (t == null) return false;
                if (syntaxType == t.FullName || syntaxType == t.Name) continue;
                if (syntaxType == ShortAliasFor(t)) continue;
                return false;
            }
            return true;
        }

        private static bool MatchesParamTypesSemantic(
            MethodDeclarationSyntax m,
            Type[] paramTypes,
            SemanticModel model)
        {
            if (model == null) return false;
            for (int i = 0; i < paramTypes.Length; i++)
            {
                var typeSyntax = m.ParameterList.Parameters[i].Type;
                if (typeSyntax == null) return false;
                var t = paramTypes[i];
                if (t == null) return false;

                // Either path can land us on the same ITypeSymbol;
                // GetSymbolInfo returns the named type symbol when
                // the name binds, GetTypeInfo additionally covers
                // built-ins like `int` whose Symbol may be null.
                var sym = model.GetSymbolInfo(typeSyntax).Symbol as ITypeSymbol
                    ?? model.GetTypeInfo(typeSyntax).Type;
                if (sym == null) return false;

                var resolved = ResolveTypeFromITypeSymbol(sym);
                if (resolved == null) return false;
                if (resolved != t) return false;
            }
            return true;
        }

        // Build a single-file Compilation just to ask the SemanticModel
        // a question. Cost is one CSharpCompilation.Create + one
        // GetSemanticModel — handful of ms on a warm reference cache,
        // amortized across all the matching probes inside one file.
        // Returns null when the build itself fails (very rare; Roslyn
        // tolerates malformed source as long as the syntax tree
        // parsed at all).
        private static SemanticModel BuildSingleFileSemanticModel(SyntaxNode root)
        {
            try
            {
                var tree = root.SyntaxTree;
                var compilation = CSharpCompilation.Create(
                    "RrPullerSemantic_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    new[] { tree },
                    AssemblyReferenceCache.GetReferences());
                return compilation.GetSemanticModel(tree);
            }
            catch { return null; }
        }

        // ITypeSymbol → System.Type via fully-qualified display name.
        // Roslyn renders nested types with `.`; reflection wants `+`.
        // We try both forms after walking the AppDomain.
        private static Type ResolveTypeFromITypeSymbol(ITypeSymbol sym)
        {
            if (sym == null) return null;
            string fqn;
            try { fqn = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", ""); }
            catch { return null; }

            var direct = Type.GetType(fqn, throwOnError: false);
            if (direct != null) return direct;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var found = asm.GetType(fqn, throwOnError: false)
                    ?? asm.GetType(fqn.Replace('.', '+'), throwOnError: false);
                if (found != null) return found;
            }
            return null;
        }

        private static bool TryParseFile(string path, out SyntaxNode root)
        {
            root = null;
            string source;
            try { source = File.ReadAllText(path); }
            catch { return false; }

            try
            {
                var tree = CSharpSyntaxTree.ParseText(source);
                root = tree.GetRoot();
                return true;
            }
            catch { return false; }
        }

        // Walks ancestors of the target type's declaration, collecting
        // usings from every enclosing namespace block + the
        // compilation unit. Stops at CU; nothing useful lives above
        // it. NamespaceDeclarationSyntax exposes `.Usings` directly;
        // the file-scoped variant (newer Roslyn) is reflected so the
        // package still compiles against older Microsoft.CodeAnalysis
        // builds that lack the type. Sibling-namespace usings — i.e.,
        // a using inside a namespace block that doesn't enclose the
        // target — are intentionally not visited.
        private static void CollectUsingsForTypeContext(
            TypeDeclarationSyntax target,
            FileContext ctx,
            HashSet<string> seenCu,
            HashSet<string> seenNs)
        {
            for (var p = (SyntaxNode)target.Parent; p != null; p = p.Parent)
            {
                if (p is CompilationUnitSyntax cu)
                {
                    foreach (var u in cu.Usings)
                    {
                        var text = u.ToFullString().Trim();
                        if (string.IsNullOrEmpty(text)) continue;
                        if (seenCu.Add(text)) ctx.CompilationUnitUsings.Add(text);
                    }
                    break;
                }
                else if (p is NamespaceDeclarationSyntax nsDecl)
                {
                    foreach (var u in nsDecl.Usings)
                    {
                        var text = u.ToFullString().Trim();
                        if (string.IsNullOrEmpty(text)) continue;
                        if (seenNs.Add(text)) ctx.NamespaceScopedUsings.Add(text);
                    }
                }
                else if (p.GetType().Name == "FileScopedNamespaceDeclarationSyntax")
                {
                    // Reflection access — see CollectUsings in
                    // AncestorChainMatches for why we don't reference
                    // the type by name.
                    var usingsProp = p.GetType().GetProperty("Usings");
                    if (usingsProp?.GetValue(p) is System.Collections.IEnumerable list)
                    {
                        foreach (var item in list)
                        {
                            if (item == null) continue;
                            var text = item.ToString().Trim();
                            if (string.IsNullOrEmpty(text)) continue;
                            if (seenNs.Add(text)) ctx.NamespaceScopedUsings.Add(text);
                        }
                    }
                }
                // Otherwise (TypeDeclarationSyntax for an enclosing
                // outer type, etc.) — keep walking up.
            }
        }

        // Step 1 — find every .cs whose first class declaration is the
        // requested type. AssetDatabase.FindAssets is editor-only so
        // this whole class lives in Editor/. Partial classes return
        // multiple paths; we'll try them in order until one yields a
        // matching method node.
        private static List<string> ResolveScriptPaths(Type type)
        {
            var result = new List<string>();
            var guids = AssetDatabase.FindAssets("t:MonoScript");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                MonoScript script;
                try { script = AssetDatabase.LoadAssetAtPath<MonoScript>(path); }
                catch { continue; }
                if (script == null) continue;
                Type declared;
                try { declared = script.GetClass(); }
                catch { continue; }
                if (declared == type) result.Add(path);
            }
            return result;
        }

        // Step 2 — Roslyn-parse the source and look for a method
        // declaration whose name + parameter count match. Parameter
        // type names are checked best-effort (full name first, then
        // short name); count alone disambiguates the common cases
        // and avoids the headache of comparing Roslyn's syntax-level
        // type strings to System.Type's reflection metadata.
        //
        // Scoped to the declaring type's syntax declarations so a file
        // hosting two top-level classes — or an outer class + nested
        // helper class — that happen to share a method name + arity
        // can't leak the wrong body. We walk the ancestor chain
        // (Outer → Inner) when the target is a nested type, and gather
        // methods *directly* under each matching declaration, never
        // descending into further nested types of those.
        private static MethodDeclarationSyntax FindMethodNode(string source, Type declaringType, string methodName, Type[] paramTypes)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();

            var typeDecls = FindMatchingTypeDeclarations(root, declaringType);
            if (typeDecls.Count == 0) return null;

            var candidates = new List<MethodDeclarationSyntax>();
            foreach (var td in typeDecls)
            {
                foreach (var member in td.Members)
                {
                    if (member is MethodDeclarationSyntax m
                        && m.Identifier.ValueText == methodName
                        && m.ParameterList.Parameters.Count == paramTypes.Length)
                    {
                        candidates.Add(m);
                    }
                }
            }

            if (candidates.Count == 0) return null;

            // Try semantic disambiguation first — even when there's
            // only one arity-matching candidate in this file, we
            // verify its parameter types resolve to what the caller
            // requested. Two partial files can each have a single
            // `void Apply(Model)` whose `Model` aliases to a
            // different CLR type; without semantic verification this
            // method would happily return the first file's body
            // when the user's spec actually targeted the other.
            // SemanticModel.GetSymbolInfo gives us the resolved type
            // through the file's own using/alias context.
            var model = BuildSingleFileSemanticModel(root);
            if (model != null)
            {
                foreach (var c in candidates)
                {
                    if (MatchesParamTypesSemantic(c, paramTypes, model)) return c;
                }

                // SemanticModel built but resolved no candidate —
                // either the file's overloads target different
                // CLR types than the caller asked for (correct: try
                // the next path), or semantics genuinely failed
                // (e.g., file references a type from an unloaded
                // assembly). Fall through to the syntactic path
                // only when we have a *single* arity match — no
                // ambiguity for syntactic to make worse — so we
                // don't silently miss the body for the broken-
                // semantic edge case.
                if (candidates.Count == 1) return candidates[0];
                return null;
            }

            // Semantic build failed entirely — fall back to syntactic
            // matching. Single arity match is unambiguous; multiple
            // arity matches go through the simple-name comparison and
            // accept the first hit.
            if (candidates.Count == 1) return candidates[0];
            foreach (var c in candidates)
            {
                if (MatchesParamTypesSyntactic(c, paramTypes)) return c;
            }
            return candidates[0];
        }

        // Resolve every TypeDeclarationSyntax in the file whose simple
        // name + ancestor type chain + enclosing namespace matches the
        // target type. For partial classes a single file can declare
        // the same type multiple times (or split across files — we'd
        // be invoked once per path). For nested types we walk the
        // syntactic ancestors so an unrelated outer type sharing a
        // nested class name doesn't get conflated. The namespace check
        // covers the case where one file holds `namespace A { class
        // Player { void Hit() {…} } }` and `namespace B { class Player
        // { void Hit() {…} } }` — without it, both syntactic Player
        // declarations pass on simple name + (empty) type chain and
        // the candidate at source-order index 0 silently wins.
        private static List<TypeDeclarationSyntax> FindMatchingTypeDeclarations(SyntaxNode root, Type declaringType)
        {
            // Build the simple-name chain Outer → Inner ignoring generics
            // (Foo`1 → "Foo"). Phase A doesn't pull from generic methods,
            // and reflection's `Type.Name` already drops type-arg names.
            var nameChain = new List<string>();
            for (var cur = declaringType; cur != null; cur = cur.DeclaringType)
                nameChain.Add(StripGenericArity(cur.Name));
            nameChain.Reverse(); // outermost first

            // Type.Namespace returns the namespace of the outermost
            // declaring type even for nested types (good — that's the
            // only namespace level expressible in C# syntax). null for
            // global namespace; normalize to "" so the comparison is a
            // straight string equality.
            string expectedNamespace = declaringType.Namespace ?? string.Empty;

            var matches = new List<TypeDeclarationSyntax>();
            foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (td.Identifier.ValueText != nameChain[nameChain.Count - 1]) continue;
                if (!AncestorChainMatches(td, nameChain, expectedNamespace)) continue;
                matches.Add(td);
            }
            return matches;
        }

        private static bool AncestorChainMatches(TypeDeclarationSyntax leaf, List<string> nameChain, string expectedNamespace)
        {
            // Walk syntactic parents collecting both the enclosing type
            // chain and the enclosing namespace chain. We deliberately
            // walk all the way to CompilationUnitSyntax (rather than
            // breaking at the first namespace) so a chain like
            // `namespace A { namespace B { class X } }` produces parts
            // ["B", "A"] which reverse to "A.B".
            var actualTypes = new List<string>();
            actualTypes.Add(leaf.Identifier.ValueText);
            var nsParts = new List<string>(); // innermost-first

            for (var p = leaf.Parent; p != null; p = p.Parent)
            {
                if (p is TypeDeclarationSyntax pt)
                {
                    actualTypes.Add(pt.Identifier.ValueText);
                }
                else if (p is NamespaceDeclarationSyntax pn)
                {
                    nsParts.Add(pn.Name.ToString());
                }
                else if (p.GetType().Name == "FileScopedNamespaceDeclarationSyntax")
                {
                    // File-scoped namespace lives in newer Roslyn; access
                    // its `.Name` via reflection so the package still
                    // compiles against older Microsoft.CodeAnalysis builds
                    // that lack the type altogether.
                    var nameNode = p.GetType().GetProperty("Name")?.GetValue(p);
                    if (nameNode != null) nsParts.Add(nameNode.ToString());
                }
                else if (p is CompilationUnitSyntax)
                {
                    break;
                }
            }

            actualTypes.Reverse(); // outer-first
            if (actualTypes.Count != nameChain.Count) return false;
            for (int i = 0; i < nameChain.Count; i++)
                if (actualTypes[i] != nameChain[i]) return false;

            nsParts.Reverse(); // outer-first
            string actualNamespace = nsParts.Count == 0 ? string.Empty : string.Join(".", nsParts);
            return actualNamespace == expectedNamespace;
        }

        private static string StripGenericArity(string name)
        {
            int tick = name.IndexOf('`');
            return tick < 0 ? name : name.Substring(0, tick);
        }

        private static string ShortAliasFor(Type t)
        {
            if (t == typeof(int))    return "int";
            if (t == typeof(long))   return "long";
            if (t == typeof(short))  return "short";
            if (t == typeof(byte))   return "byte";
            if (t == typeof(float))  return "float";
            if (t == typeof(double)) return "double";
            if (t == typeof(bool))   return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(char))   return "char";
            if (t == typeof(object)) return "object";
            return null;
        }

        private static string ExtractBodyInside(string source, BlockSyntax body)
        {
            // The substring between the open / close braces, exclusive.
            // We deliberately don't trim leading/trailing newlines —
            // preserving them keeps the user's existing indentation
            // intact when the text lands in the editor.
            int start = body.OpenBraceToken.Span.End;
            int end = body.CloseBraceToken.Span.Start;
            if (end <= start) return string.Empty;
            return source.Substring(start, end - start);
        }

        private static PullResult Fail(string message) => new PullResult { Success = false, Error = message };
    }
}
