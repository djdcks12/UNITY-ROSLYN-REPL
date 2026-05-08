using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.IO;
using System.Reflection;
using UnityEditor;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// Write the user's edited patch body back into the declaring
    /// type's source file. Delegates method resolution to
    /// <see cref="PatchSourcePuller.FindMethodForTarget"/> so Pull
    /// Original and Apply To File can never disagree on which
    /// overload is "the" target — a critical guarantee in
    /// per-file-alias / partial-class scenarios where the same
    /// simple-name parameter resolves to a different CLR type per
    /// file.
    ///
    /// Why text-level substitution rather than Roslyn syntax
    /// rewriting:
    ///   - Preserves the user's existing indentation + line endings
    ///     in the surrounding method declaration. Roslyn's
    ///     SyntaxNode.WithBody + ToFullString tends to reformat in
    ///     ways the user didn't ask for.
    ///   - The patch body the user edited *is* C# source the same
    ///     way the original was — the rewriter's auto-rewrite
    ///     happens at compile time inside the wrapper, never
    ///     against spec.PatchBody. So spec.PatchBody is always
    ///     clean source (`hp -= 10`, not `__set("hp",
    ///     __get&lt;int&gt;("hp") - 10)`) and pasting it between
    ///     the original braces produces a compilable file.
    ///
    /// Conflict detection: callers can pass an `expectedSnapshot`
    /// (typically the body Pull Original last captured). The writer
    /// then re-extracts the current body from disk and aborts if
    /// it differs from the snapshot — that's the case where the
    /// source file changed after Pull and the user's diff is no
    /// longer a clean overlay.
    ///
    /// Safety: the writer creates a `.bak` sibling of the target
    /// file before writing. Failures during write leave the
    /// original in place via the backup; callers can re-run after
    /// fixing whatever blocked the write.
    /// </summary>
    public static class PatchSourceWriter
    {
        public class WriteResult
        {
            public bool Success;
            public string SourcePath;
            public string Error;
            public string BackupPath;

            /// <summary>True when the failure was specifically a
            /// snapshot/file conflict — the UI surfaces this with a
            /// "Pull Original again to refresh" hint rather than a
            /// generic write-error.</summary>
            public bool ConflictDetected;
        }

        /// <summary>
        /// Locate the declaring type's source file + the method's
        /// body extents, replace the body text with
        /// <paramref name="newBody"/>, and save. When
        /// <paramref name="expectedSnapshot"/> is non-null, compare
        /// the file's current method body against the snapshot
        /// first and abort with a clear conflict message on
        /// mismatch — the snapshot is taken at Pull time, so a
        /// mismatch means the file changed underneath us.
        /// </summary>
        public static WriteResult ApplyToFile(MethodInfo target, string newBody, string expectedSnapshot = null)
        {
            if (target == null) return Fail("Target method is null.");
            if (newBody == null) newBody = string.Empty;

            // Share the puller's semantic finder. Same disambiguation
            // depth (per-file SemanticModel + parameter symbol
            // resolution) so partial-class + per-file-alias
            // scenarios route to the exact same overload Pull would
            // have read from.
            var found = PatchSourcePuller.FindMethodForTarget(target);
            if (found == null)
            {
                return Fail($"Could not find a method matching {target.DeclaringType?.Name ?? "<unknown>"}.{target.Name} in any candidate source file. Source must live in Assets/ or Packages/, and the method must be a block-bodied (`{{ … }}`) declaration.");
            }

            if (found.Method.Body == null)
            {
                return Fail($"{target.DeclaringType?.Name}.{target.Name} is expression-bodied; the source writer only handles block-bodied methods. Convert the source to a `{{ … }}` body first.");
            }

            // Conflict guard: if the caller has a snapshot of what
            // the body looked like at pull time, verify the file on
            // disk still matches before splicing. A mismatch means
            // someone (the user, an IDE, source control) edited the
            // file after Pull, and writing now would silently
            // overwrite that newer content with the user's
            // potentially-stale edits.
            if (expectedSnapshot != null)
            {
                var currentBody = PatchSourcePuller.ExtractMethodBody(found.Source, found.Method);
                if (!BodiesMatch(currentBody, expectedSnapshot))
                {
                    return new WriteResult
                    {
                        Success = false,
                        ConflictDetected = true,
                        SourcePath = found.SourcePath,
                        Error = $"Source file changed after the last Pull Original ({found.SourcePath}). Pull Original again to refresh, then re-Apply.",
                    };
                }
            }

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
            var source = found.Source;
            var method = found.Method;
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
                backupPath = found.SourcePath + ".bak";
                File.Copy(found.SourcePath, backupPath, overwrite: true);
            }
            catch (Exception ex) { return Fail($"Could not write backup: {ex.Message}"); }

            try
            {
                File.WriteAllText(found.SourcePath, newSource);
            }
            catch (Exception ex)
            {
                return Fail($"Could not write '{found.SourcePath}': {ex.Message}");
            }

            // Tell Unity to re-import the asset so the editor picks
            // up the change without a manual focus + alt-tab. Best-
            // effort — failures are non-fatal.
            try { AssetDatabase.ImportAsset(found.SourcePath, ImportAssetOptions.ForceUpdate); }
            catch { /* best-effort */ }

            return new WriteResult
            {
                Success = true,
                SourcePath = found.SourcePath,
                BackupPath = backupPath,
            };
        }

        // ─── internals ─────────────────────────────────────────────

        private static WriteResult Fail(string msg) => new WriteResult { Success = false, Error = msg };

        // Compare two body strings while normalizing line endings so
        // a snapshot taken on `\n`-only Unix output and a file read
        // back on `\r\n`-using Windows storage compare equal. Any
        // other whitespace is left strict — if the user
        // reformatted the body, that *is* a real conflict.
        private static bool BodiesMatch(string a, string b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return a.Replace("\r\n", "\n") == b.Replace("\r\n", "\n");
        }

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
    }
}
