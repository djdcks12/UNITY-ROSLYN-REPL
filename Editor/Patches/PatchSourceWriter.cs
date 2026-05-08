using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// Phase E — write the user's edited patch body back into the
    /// declaring type's source file. Pulls together the same method-
    /// resolution machinery <see cref="PatchSourcePuller"/> uses, then
    /// surgically replaces the text between the target method's
    /// outer braces with the spec's PatchBody.
    ///
    /// Why text-level substitution rather than Roslyn syntax
    /// rewriting:
    ///   - Preserves the user's existing indentation + line endings
    ///     in the surrounding method declaration. Roslyn's
    ///     SyntaxNode.WithBody + ToFullString tends to reformat in
    ///     ways the user didn't ask for.
    ///   - The patch body the user edited *is* C# source the same
    ///     way the original was — Phase D's auto-rewrite happens at
    ///     compile time inside the wrapper, never against
    ///     spec.PatchBody. So spec.PatchBody is always clean source
    ///     (`hp -= 10`, not `__set("hp", __get&lt;int&gt;("hp") - 10)`)
    ///     and pasting it between the original braces produces a
    ///     compilable file.
    ///
    /// Safety: the writer creates a `.bak` sibling of the target
    /// file before writing. Failures during write leave the original
    /// in place via the backup; callers can re-run after fixing
    /// whatever blocked the write.
    /// </summary>
    public static class PatchSourceWriter
    {
        public class WriteResult
        {
            public bool Success;
            public string SourcePath;
            public string Error;
            public string BackupPath;
        }

        /// <summary>
        /// Locate the declaring type's source file + the method's
        /// body extents, replace the body text with
        /// <paramref name="newBody"/>, and save. Returns a structured
        /// result so the UI can surface either the rewritten path or
        /// a specific failure message.
        /// </summary>
        public static WriteResult ApplyToFile(MethodInfo target, string newBody)
        {
            if (target == null) return Fail("Target method is null.");
            if (newBody == null) newBody = string.Empty;

            // Reuse PatchSourcePuller's per-method file resolution so
            // partial classes route to the file that actually
            // declares this exact overload (parameter types matter).
            var paths = ResolveScriptPathsViaReflection(target.DeclaringType);
            if (paths == null || paths.Count == 0)
                return Fail($"No MonoScript found for {target.DeclaringType.FullName}. Source must live in Assets/ or Packages/.");

            var paramTypes = target.GetParameters().Select(p => p.ParameterType).ToArray();

            string chosenPath = null;
            string source = null;
            MethodDeclarationSyntax method = null;

            foreach (var path in paths)
            {
                string src;
                try { src = File.ReadAllText(path); }
                catch (Exception ex) { return Fail($"Could not read '{path}': {ex.Message}"); }

                SyntaxNode root;
                try { root = CSharpSyntaxTree.ParseText(src).GetRoot(); }
                catch (Exception ex) { return Fail($"Roslyn parse failed for '{path}': {ex.Message}"); }

                // Walk MethodDeclarationSyntax nodes inside the
                // matching type declaration. Mirrors
                // PatchSourcePuller's body-pull semantics so the
                // file we write to is the same one Pull Original
                // would have read from.
                var typeDecls = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                    .Where(td => MatchesDeclaringType(td, target.DeclaringType));
                foreach (var td in typeDecls)
                {
                    foreach (var member in td.Members)
                    {
                        if (!(member is MethodDeclarationSyntax m)) continue;
                        if (m.Identifier.ValueText != target.Name) continue;
                        if (m.ParameterList.Parameters.Count != paramTypes.Length) continue;
                        if (!ParamsRoughlyMatch(m.ParameterList.Parameters, paramTypes)) continue;
                        method = m;
                        chosenPath = path;
                        source = src;
                        break;
                    }
                    if (method != null) break;
                }
                if (method != null) break;
            }

            if (method == null)
                return Fail($"Could not find a method matching {target.DeclaringType.Name}.{target.Name}({string.Join(", ", paramTypes.Select(t => t?.Name))}) in any candidate source file.");

            if (method.Body == null)
                return Fail($"{target.DeclaringType.Name}.{target.Name} is expression-bodied; Phase E only writes block-bodied methods. Convert the source to a `{{ … }}` body first.");

            // Splice between the outer braces. Roslyn attaches
            // trailing newlines to the *preceding* token rather than
            // the following one, so CloseBrace.LeadingTrivia is
            // typically just the indent whitespace — no newline. If
            // we substring at FullSpan.Start we miss the line break,
            // and at Span.Start we miss the indent. So we anchor at
            // Span.Start and re-derive the indent text from the
            // source (whitespace from the previous newline up to the
            // `}` position). The normalized body provides leading
            // *and* trailing newline, then we sandwich the indent
            // before the close brace lands.
            int openEnd = method.Body.OpenBraceToken.Span.End;
            int closeStart = method.Body.CloseBraceToken.Span.Start;
            if (closeStart < openEnd) return Fail("Unexpected method body span ordering.");

            string closeIndent = string.Empty;
            int prevNewline = source.LastIndexOf('\n', closeStart - 1);
            if (prevNewline >= 0 && prevNewline + 1 < closeStart)
            {
                var slice = source.Substring(prevNewline + 1, closeStart - prevNewline - 1);
                // Only treat as indent if it's pure whitespace —
                // otherwise something else (a comment, code) lives
                // on the same line as `}`, and we don't want to
                // duplicate it.
                if (string.IsNullOrWhiteSpace(slice)) closeIndent = slice;
            }

            var normalized = NormalizeBlockBody(newBody);
            var newSource = source.Substring(0, openEnd) + normalized + closeIndent + source.Substring(closeStart);

            string backupPath;
            try
            {
                backupPath = chosenPath + ".bak";
                File.Copy(chosenPath, backupPath, overwrite: true);
            }
            catch (Exception ex) { return Fail($"Could not write backup: {ex.Message}"); }

            try
            {
                File.WriteAllText(chosenPath, newSource);
            }
            catch (Exception ex)
            {
                return Fail($"Could not write '{chosenPath}': {ex.Message}");
            }

            // Tell Unity to re-import the asset so the editor picks
            // up the change without a manual focus + alt-tab. Best-
            // effort — failures are non-fatal.
            try { AssetDatabase.ImportAsset(chosenPath, ImportAssetOptions.ForceUpdate); }
            catch { /* best-effort */ }

            return new WriteResult
            {
                Success = true,
                SourcePath = chosenPath,
                BackupPath = backupPath,
            };
        }

        // ─── internals ─────────────────────────────────────────────

        private static WriteResult Fail(string msg) => new WriteResult { Success = false, Error = msg };

        // Ensure the substituted body starts AND ends with a newline
        // — caller adds the close-brace indent after, then the close
        // brace itself. Result reads as
        //   `{`
        //   `<body>`
        //   `<indent>}`
        private static string NormalizeBlockBody(string body)
        {
            if (string.IsNullOrEmpty(body)) return Environment.NewLine;
            var s = body.Replace("\r\n", "\n").TrimEnd('\n', '\r');
            if (!s.StartsWith("\n")) s = "\n" + s;
            return s.Replace("\n", Environment.NewLine) + Environment.NewLine;
        }

        // PatchSourcePuller's ResolveScriptPaths is private, so we
        // mirror its surface here via reflection. Same logic — walk
        // every MonoScript and keep paths whose declared class
        // matches the target type.
        private static System.Collections.Generic.List<string> ResolveScriptPathsViaReflection(Type type)
        {
            var puller = typeof(PatchSourcePuller);
            var m = puller.GetMethod("ResolveScriptPaths", BindingFlags.NonPublic | BindingFlags.Static);
            if (m == null)
            {
                // Fallback — direct AssetDatabase walk.
                var result = new System.Collections.Generic.List<string>();
                var guids = AssetDatabase.FindAssets("t:MonoScript");
                foreach (var guid in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(p)) continue;
                    UnityEditor.MonoScript ms;
                    try { ms = AssetDatabase.LoadAssetAtPath<UnityEditor.MonoScript>(p); }
                    catch { continue; }
                    if (ms == null) continue;
                    Type declared;
                    try { declared = ms.GetClass(); }
                    catch { continue; }
                    if (declared == type) result.Add(p);
                }
                return result;
            }
            return (System.Collections.Generic.List<string>)m.Invoke(null, new object[] { type });
        }

        // Type declaration ancestor-chain match — same logic the
        // puller uses for namespace / nested-type disambiguation.
        // We keep this lightweight and accept simple-name matching
        // because the puller already filters MonoScript paths to
        // the right type; we just need to find the right
        // TypeDeclarationSyntax inside that file.
        private static bool MatchesDeclaringType(TypeDeclarationSyntax td, Type declaringType)
        {
            if (declaringType == null) return false;
            if (td.Identifier.ValueText != StripGenericArity(declaringType.Name)) return false;
            return true; // good enough — MonoScript filtering already narrowed to this file
        }

        private static string StripGenericArity(string name)
        {
            int tick = name.IndexOf('`');
            return tick < 0 ? name : name.Substring(0, tick);
        }

        // Best-effort parameter type matching. Same depth as
        // FindMethodNode's syntactic disambiguation (full name,
        // simple name, C# alias). Returns true for arity-only
        // matches when type names can't be cleanly compared.
        private static bool ParamsRoughlyMatch(SeparatedSyntaxList<ParameterSyntax> pars, Type[] paramTypes)
        {
            for (int i = 0; i < paramTypes.Length; i++)
            {
                var syntaxType = pars[i].Type?.ToString();
                if (string.IsNullOrEmpty(syntaxType)) return false;
                var t = paramTypes[i];
                if (t == null) return false;
                if (syntaxType == t.FullName || syntaxType == t.Name) continue;
                if (syntaxType == ShortAliasFor(t)) continue;
                // Permissive — if we can't disambiguate by name,
                // accept the candidate. The puller's MonoScript
                // filter already restricted to the right type.
            }
            return true;
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
    }
}
