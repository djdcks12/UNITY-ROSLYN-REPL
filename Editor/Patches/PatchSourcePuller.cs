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
    /// the method body text. Used by the Runtime Method Patch
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

        /// <summary>
        /// Located method declaration shared across the Pull body
        /// extractor and the source writer. Carries enough context
        /// (path + raw source + parsed root + the specific
        /// MethodDeclarationSyntax + its containing TypeDeclaration)
        /// for callers to inspect or splice without re-doing the
        /// disambiguation walk.
        /// </summary>
        public class FoundMethod
        {
            public string SourcePath;
            public string Source;
            public SyntaxNode Root;
            public TypeDeclarationSyntax TypeDecl;
            public MethodDeclarationSyntax Method;
        }

        /// <summary>
        /// Semantic-aware method-declaration locator shared by Pull
        /// Original (<see cref="TryPullMethodBody"/>) and Apply To
        /// File (<see cref="PatchSourceWriter"/>). Walks every
        /// MonoScript path the declaring type maps to (covers
        /// partial classes split across files), parses each, builds
        /// a one-shot SemanticModel for it, and matches the target
        /// by name + arity + each parameter's resolved CLR type.
        ///
        /// Two-pass resolution (issue #30 follow-up review): a
        /// strict semantic match in a *later* path always beats a
        /// fallback in an *earlier* path. The earlier shape walked
        /// path-by-path and returned the per-path fallback as soon
        /// as it ran out of strict matches in that one file —
        /// which, in a partial class with per-file `using Model
        /// = …` aliases, picked the wrong file's body when each
        /// part declared exactly one arity-matching overload but
        /// only one of them resolved to the target's CLR type.
        ///
        /// New shape:
        ///   Pre-scan: parse every path once, collect every
        ///   arity-matching (TypeDecl, MethodDecl) pair, and cache
        ///   the SemanticModel (or null when Roslyn semantics
        ///   failed for the file).
        ///
        ///   Pass 1 (strict): walk every cached path/candidate and
        ///   return the first one whose semantic parameter types
        ///   exactly match the target's CLR types. Files whose
        ///   semantic build failed are skipped here — they can't
        ///   produce a strict match by definition.
        ///
        ///   Pass 2 (fallback): if Pass 1 found nothing, collect a
        ///   *global* fallback pool from every path:
        ///     • Semantic-built file with no strict match and
        ///       exactly one arity candidate → that candidate is a
        ///       fallback. Multi-candidate semantic-built files
        ///       contribute nothing (we already proved no strict
        ///       match exists, and we won't guess between
        ///       overloads).
        ///     • Semantic-build-failed file with exactly one arity
        ///       candidate → that candidate is a fallback.
        ///     • Semantic-build-failed file with multiple arity
        ///       candidates → only the syntactic-name matches join
        ///       the pool. No "first candidate as last-resort" fall
        ///       back: silently picking the wrong overload is the
        ///       exact failure mode this rewrite exists to fix.
        ///
        ///   The fallback is taken only when the global pool has
        ///   exactly one member. Two or more members → return null
        ///   so the caller surfaces "Could not find a method
        ///   matching ..." rather than commit to a guess.
        /// </summary>
        public static FoundMethod FindMethodForTarget(MethodInfo target)
        {
            if (target == null || target.DeclaringType == null) return null;
            var paths = ResolveScriptPaths(target.DeclaringType);
            if (paths.Count == 0) return null;

            var paramTypes = target.GetParameters().Select(p => p.ParameterType).ToArray();

            // Pre-scan: parse + collect candidates + cache semantic
            // model per path. We need the full picture before any
            // return decision — partial classes with per-file
            // aliases mean a later path's strict semantic match
            // must always win over an earlier path's fallback.
            //
            // Single read of the file — `source` is the exact text
            // we parse. The original code did
            // `File.ReadAllText(path)` then `TryParseFile(path)`
            // which read the file *again*; an IDE / autosave / VCS
            // write between those reads would pair MethodInfo
            // spans from version B with `source` from version A,
            // and the writer's later splice would mangle the file.
            // Locking the parse to the same string we hold closes
            // that race.
            var contexts = new List<PerPathContext>();
            foreach (var path in paths)
            {
                string source;
                try { source = File.ReadAllText(path); }
                catch { continue; }

                SyntaxNode root;
                try { root = CSharpSyntaxTree.ParseText(source).GetRoot(); }
                catch { continue; }

                var typeDecls = FindMatchingTypeDeclarations(root, target.DeclaringType);
                if (typeDecls.Count == 0) continue;

                var candidates = new List<(TypeDeclarationSyntax td, MethodDeclarationSyntax m)>();
                foreach (var td in typeDecls)
                {
                    foreach (var member in td.Members)
                    {
                        if (!(member is MethodDeclarationSyntax m)) continue;
                        if (m.Identifier.ValueText != target.Name) continue;
                        if (m.ParameterList.Parameters.Count != paramTypes.Length) continue;
                        candidates.Add((td, m));
                    }
                }
                if (candidates.Count == 0) continue;

                contexts.Add(new PerPathContext
                {
                    Path = path,
                    Source = source,
                    Root = root,
                    Candidates = candidates,
                    Model = BuildSingleFileSemanticModel(root),
                });
            }
            if (contexts.Count == 0) return null;

            // Pass 1: strict semantic match across ALL paths. A
            // later partial that resolves exactly always beats an
            // earlier partial whose alias points at a different
            // CLR type.
            foreach (var ctx in contexts)
            {
                if (ctx.Model == null) continue;
                foreach (var c in ctx.Candidates)
                {
                    if (MatchesParamTypesSemantic(c.m, paramTypes, ctx.Model))
                        return MakeFound(ctx.Path, ctx.Source, ctx.Root, c.td, c.m);
                }
            }

            // Pass 2: fallback. Collect globally. Auto-select only
            // when the pool has exactly one member — that's the
            // boundary between "Pull used to handle this case" and
            // "we'd be guessing between two equally-likely
            // overloads".
            //
            // Tri-state matcher (PR review): a semantic-built file
            // whose single candidate Roslyn explicitly resolved to
            // the *wrong* CLR type does NOT join the fallback pool.
            // Letting it through would let an alias-mismatched
            // partial win the global selection just because some
            // other partial happened to be missing or unparseable
            // — exactly the failure the rewrite is supposed to
            // avoid.
            var fallbacks = new List<(PerPathContext ctx, TypeDeclarationSyntax td, MethodDeclarationSyntax m)>();
            foreach (var ctx in contexts)
            {
                if (ctx.Model != null)
                {
                    // Strict pass already ruled out semantic
                    // matches in this file. Only single-candidate
                    // files can fall back — multiple unresolved
                    // overloads stay ambiguous.
                    if (ctx.Candidates.Count == 1)
                    {
                        var only = ctx.Candidates[0];
                        var classification = ClassifyParamTypesSemantic(only.m, paramTypes, ctx.Model);
                        // Exact would have been picked in Pass 1.
                        // Reaching here means the candidate is
                        // either Unresolved (semantics couldn't
                        // resolve a param type — broken assembly
                        // ref, generic, etc.) or Mismatch
                        // (semantics resolved and disagreed).
                        // Only Unresolved is a legitimate
                        // fallback; Mismatch is a known-wrong
                        // verdict and gets dropped.
                        if (classification == ParamMatch.Unresolved)
                            fallbacks.Add((ctx, only.td, only.m));
                    }
                }
                else
                {
                    // Semantic build failed entirely. Single arity
                    // candidate is unambiguous; multi-candidate
                    // files use syntactic name match — but the
                    // earlier "first candidate as last-resort"
                    // is dropped on purpose.
                    if (ctx.Candidates.Count == 1)
                    {
                        fallbacks.Add((ctx, ctx.Candidates[0].td, ctx.Candidates[0].m));
                    }
                    else
                    {
                        foreach (var c in ctx.Candidates)
                        {
                            if (MatchesParamTypesSyntactic(c.m, paramTypes))
                                fallbacks.Add((ctx, c.td, c.m));
                        }
                    }
                }
            }

            if (fallbacks.Count == 1)
            {
                var sel = fallbacks[0];
                return MakeFound(sel.ctx.Path, sel.ctx.Source, sel.ctx.Root, sel.td, sel.m);
            }
            return null;
        }

        private sealed class PerPathContext
        {
            public string Path;
            public string Source;
            public SyntaxNode Root;
            public List<(TypeDeclarationSyntax td, MethodDeclarationSyntax m)> Candidates;
            public SemanticModel Model; // null if Roslyn semantic build failed for this file
        }

        private static FoundMethod MakeFound(
            string path,
            string source,
            SyntaxNode root,
            TypeDeclarationSyntax td,
            MethodDeclarationSyntax m)
        {
            return new FoundMethod
            {
                SourcePath = path,
                Source = source,
                Root = root,
                TypeDecl = td,
                Method = m,
            };
        }

        /// <summary>
        /// Same `body between outer braces` extraction logic the
        /// Pull pipeline applies — exposed for callers (e.g. the
        /// source writer's conflict check) that already have a
        /// MethodDeclarationSyntax in hand and need its current
        /// body text without re-parsing.
        /// </summary>
        public static string ExtractMethodBody(string source, MethodDeclarationSyntax method)
        {
            if (method?.Body == null || string.IsNullOrEmpty(source)) return string.Empty;
            return ExtractBodyInside(source, method.Body);
        }

        public static PullResult TryPullMethodBody(MethodInfo method)
        {
            if (method == null)
                return Fail("Method is null.");

            var declaringType = method.DeclaringType;
            if (declaringType == null)
                return Fail("Method has no declaring type (anonymous?).");

            // Issue #30 (resolver parity): route Pull through the same
            // FindMethodForTarget call Apply To File uses, so the two
            // surfaces always agree on which (path, MethodDeclSyntax)
            // tuple represents "the" target. The previous shape used a
            // private FindMethodNode whose fallback chain (single arity
            // candidate, syntactic match, last-resort first candidate)
            // was richer than the writer's finder — Pull would succeed
            // on a body Apply could no longer locate. Sharing the
            // finder closes that gap by construction.
            var found = FindMethodForTarget(method);
            if (found == null)
            {
                var paths = ResolveScriptPaths(declaringType);
                if (paths.Count == 0)
                    return Fail($"No MonoScript found for {declaringType.FullName}. " +
                                "Ensure the type lives in a `.cs` file inside Assets/ or Packages/.");

                var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
                return Fail(
                    $"Could not find a method matching {declaringType.Name}.{method.Name}({string.Join(", ", paramTypes.Select(t => t?.Name))}) " +
                    $"in any of the {paths.Count} candidate file(s). " +
                    "Method may be auto-generated or in an assembly without source available.");
            }

            if (found.Method.Body == null)
            {
                // Expression-bodied (`=> expr`) — we don't unwrap
                // those. Surface a specific message rather than
                // silently returning empty.
                return Fail($"{declaringType.Name}.{method.Name} is expression-bodied; only block-bodied methods can be pulled. Convert the source to a `{{ … }}` body or write the patch from scratch.");
            }

            return new PullResult
            {
                Success = true,
                Body = ExtractBodyInside(found.Source, found.Method.Body),
                SourcePath = found.SourcePath,
            };
        }

        // ─── Source file context ──────────────────────────────────
        // Snapshot of the namespace + using directives the declaring
        // type's `.cs` file was authored in. the rewriter's compiler
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

        // Tri-state result for the semantic param-type matcher.
        // The previous bool collapsed two cases (PR review for #30):
        //   - Roslyn semantics resolved the param type and it disagrees
        //     with the target's CLR type → this candidate is *known
        //     wrong*, must not feed the fallback pool.
        //   - Roslyn couldn't resolve at all (broken assembly reference,
        //     missing using, generic parameter) → uncertain, candidate
        //     can still join the fallback pool when nothing better is
        //     available globally.
        // Splitting them prevents the "single semantic-built candidate
        // explicitly mismatches but still wins the global fallback"
        // failure mode.
        private enum ParamMatch
        {
            Exact,        // every param resolved and matches target
            Unresolved,   // at least one param's type couldn't be resolved
            Mismatch,     // a param resolved but to a different CLR type
        }

        private static ParamMatch ClassifyParamTypesSemantic(
            MethodDeclarationSyntax m,
            Type[] paramTypes,
            SemanticModel model)
        {
            if (model == null) return ParamMatch.Unresolved;

            bool sawUnresolved = false;
            for (int i = 0; i < paramTypes.Length; i++)
            {
                var typeSyntax = m.ParameterList.Parameters[i].Type;
                var t = paramTypes[i];
                if (typeSyntax == null || t == null)
                {
                    sawUnresolved = true;
                    continue;
                }

                // Either path can land us on the same ITypeSymbol;
                // GetSymbolInfo returns the named type symbol when
                // the name binds, GetTypeInfo additionally covers
                // built-ins like `int` whose Symbol may be null.
                var sym = model.GetSymbolInfo(typeSyntax).Symbol as ITypeSymbol
                    ?? model.GetTypeInfo(typeSyntax).Type;
                if (sym == null) { sawUnresolved = true; continue; }

                var resolved = ResolveTypeFromITypeSymbol(sym);
                if (resolved == null) { sawUnresolved = true; continue; }

                // Definitive disagreement — semantics gave us a
                // concrete CLR type that isn't the one the caller
                // asked for. Short-circuit; no point looking at
                // other params, and we don't want any later
                // "Unresolved" param to mask this verdict.
                if (resolved != t) return ParamMatch.Mismatch;
            }
            return sawUnresolved ? ParamMatch.Unresolved : ParamMatch.Exact;
        }

        private static bool MatchesParamTypesSemantic(
            MethodDeclarationSyntax m,
            Type[] paramTypes,
            SemanticModel model)
            => ClassifyParamTypesSemantic(m, paramTypes, model) == ParamMatch.Exact;

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
        // We try both forms after walking the AppDomain. CLR built-
        // ins go through the SpecialType switch first because
        // FullyQualifiedFormat uses C# keyword aliases (`string`,
        // `int`, `object`) that `Type.GetType` won't load.
        private static Type ResolveTypeFromITypeSymbol(ITypeSymbol sym)
        {
            if (sym == null) return null;

            switch (sym.SpecialType)
            {
                case SpecialType.System_Object:   return typeof(object);
                case SpecialType.System_String:   return typeof(string);
                case SpecialType.System_Boolean:  return typeof(bool);
                case SpecialType.System_Char:     return typeof(char);
                case SpecialType.System_SByte:    return typeof(sbyte);
                case SpecialType.System_Byte:     return typeof(byte);
                case SpecialType.System_Int16:    return typeof(short);
                case SpecialType.System_UInt16:   return typeof(ushort);
                case SpecialType.System_Int32:    return typeof(int);
                case SpecialType.System_UInt32:   return typeof(uint);
                case SpecialType.System_Int64:    return typeof(long);
                case SpecialType.System_UInt64:   return typeof(ulong);
                case SpecialType.System_Single:   return typeof(float);
                case SpecialType.System_Double:   return typeof(double);
                case SpecialType.System_Decimal:  return typeof(decimal);
                case SpecialType.System_Void:     return typeof(void);
                case SpecialType.System_IntPtr:   return typeof(IntPtr);
                case SpecialType.System_UIntPtr:  return typeof(UIntPtr);
                case SpecialType.System_DateTime: return typeof(DateTime);
            }

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

        // FindMethodNode (the Pull-only variant) was removed in the
        // resolver-parity change for issue #30. Both Pull and Apply
        // now share FindMethodForTarget, which carries the same
        // candidate-collection + semantic / syntactic / single-
        // candidate / first-candidate fallback chain that used to
        // live here. The shared finder also returns the parsed
        // TypeDeclarationSyntax and the source string so callers can
        // skip the "re-parse to extract the body" step the previous
        // shape made Pull do.

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
            // (Foo`1 → "Foo"). the engine doesn't pull from generic methods,
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
