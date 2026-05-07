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
            // type-name match. Falls back to the first candidate if
            // nothing matches more precisely (the user's spec only
            // disambiguated this far anyway).
            foreach (var c in candidates)
            {
                bool allMatch = true;
                for (int i = 0; i < paramTypes.Length; i++)
                {
                    var syntaxType = c.ParameterList.Parameters[i].Type?.ToString();
                    if (string.IsNullOrEmpty(syntaxType)) { allMatch = false; break; }
                    var t = paramTypes[i];
                    if (t == null) { allMatch = false; break; }
                    if (syntaxType == t.FullName || syntaxType == t.Name) continue;
                    // Common short-name aliases (System.Int32 ↔ int).
                    if (syntaxType == ShortAliasFor(t)) continue;
                    allMatch = false; break;
                }
                if (allMatch) return c;
            }
            return candidates[0];
        }

        // Resolve every TypeDeclarationSyntax in the file whose simple
        // name + ancestor chain matches the target type. For partial
        // classes a single file can declare the same type multiple
        // times (or split across files — we'd be invoked once per
        // path). For nested types we walk the syntactic ancestors so
        // an unrelated outer type that happens to contain a nested
        // class with the same simple name doesn't get conflated.
        private static List<TypeDeclarationSyntax> FindMatchingTypeDeclarations(SyntaxNode root, Type declaringType)
        {
            // Build the simple-name chain Outer → Inner ignoring generics
            // (Foo`1 → "Foo"). Phase A doesn't pull from generic methods,
            // and reflection's `Type.Name` already drops type-arg names.
            var nameChain = new List<string>();
            for (var cur = declaringType; cur != null; cur = cur.DeclaringType)
                nameChain.Add(StripGenericArity(cur.Name));
            nameChain.Reverse(); // outermost first

            var matches = new List<TypeDeclarationSyntax>();
            foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (td.Identifier.ValueText != nameChain[nameChain.Count - 1]) continue;
                if (!AncestorChainMatches(td, nameChain)) continue;
                matches.Add(td);
            }
            return matches;
        }

        private static bool AncestorChainMatches(TypeDeclarationSyntax leaf, List<string> nameChain)
        {
            // Walk syntactic parents collecting enclosing type names.
            // Stop when we reach a non-type ancestor (namespace / file
            // scope). Then compare bottom-up to the expected chain.
            var actual = new List<string>();
            actual.Add(leaf.Identifier.ValueText);
            for (var p = leaf.Parent; p != null; p = p.Parent)
            {
                if (p is TypeDeclarationSyntax pt) actual.Add(pt.Identifier.ValueText);
                else if (p is NamespaceDeclarationSyntax || p is CompilationUnitSyntax
                         // FileScopedNamespaceDeclarationSyntax exists in newer Roslyn — pattern-match by name to keep older builds compiling
                         || p.GetType().Name == "FileScopedNamespaceDeclarationSyntax")
                    break;
            }
            // actual is innermost-first; reverse to outer-first
            actual.Reverse();
            if (actual.Count != nameChain.Count) return false;
            for (int i = 0; i < nameChain.Count; i++)
                if (actual[i] != nameChain[i]) return false;
            return true;
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
