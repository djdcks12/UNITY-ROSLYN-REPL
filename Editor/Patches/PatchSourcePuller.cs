using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

        // Probe a candidate path for the *exact* method declaration —
        // matching name + parameter count + parameter types — inside
        // the type's matching syntactic declaration. Returns the
        // parsed root + the specific TypeDeclarationSyntax that
        // contains the method, so the caller can walk that exact
        // ancestor chain for usings.
        //
        // Same matching depth as `FindMethodNode`'s overload-
        // resolution path (shared via MatchesParamTypes). When
        // multiple arity-matching candidates exist in the same file,
        // we pick the one whose parameter types match exactly; only
        // when no candidate matches by type do we fall back to the
        // first arity match. That mirrors the body-pull semantics so
        // a `void Apply(int)` and `void Apply(string)` overload pair
        // — split across two partial files — gets routed to whichever
        // file actually holds the requested overload, not just the
        // first file with *any* `Apply(...)` signature.
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
            TypeDeclarationSyntax fallback = null;
            int arity = paramTypes.Length;

            foreach (var td in typeDecls)
            {
                foreach (var member in td.Members)
                {
                    if (!(member is MethodDeclarationSyntax m)) continue;
                    if (m.Identifier.ValueText != methodName) continue;
                    if (m.ParameterList.Parameters.Count != arity) continue;

                    if (MatchesParamTypes(m, paramTypes))
                    {
                        typeDecl = td;
                        return true;
                    }
                    if (fallback == null) fallback = td;
                }
            }

            if (fallback != null)
            {
                // No exact param-type match in this file; remember
                // the arity-only match but only use it when *no*
                // file gives an exact one. Caller iterates files in
                // order, so returning false here lets the next file
                // try; the GetDeclaringFileContext fallback path
                // handles the "all files arity-only" case.
                return false;
            }
            return false;
        }

        // Compare a method's syntactic parameter type list to a
        // System.Type[] from reflection. Returns true when every
        // parameter's type name matches by full name, simple name,
        // or short C# alias (System.Int32 ↔ int). Shared between
        // body-pull and file-context paths so both round-trips agree
        // on which overload is "the" target.
        private static bool MatchesParamTypes(MethodDeclarationSyntax m, Type[] paramTypes)
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
            if (candidates.Count == 1) return candidates[0];

            // Multiple overloads with the same arity — try a deeper
            // type-name match (shared with the file-context path).
            // Falls back to the first candidate if nothing matches
            // more precisely (the user's spec only disambiguated this
            // far anyway).
            foreach (var c in candidates)
            {
                if (MatchesParamTypes(c, paramTypes)) return c;
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
