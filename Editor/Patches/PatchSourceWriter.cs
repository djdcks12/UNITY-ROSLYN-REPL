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
    /// Safety: the writer pre-copies the target file to
    /// <c>Library/RoslynRepl/Backups/&lt;timestamp&gt;_&lt;name&gt;.cs.bak</c>
    /// (issue #44 — <c>Library/</c> is the Unity-reserved derived-data
    /// folder that the asset importer skips and the standard Unity
    /// gitignore template excludes; the old shape dropped a sibling
    /// <c>Foo.cs.bak</c> inside <c>Assets/</c> or <c>Packages/</c>,
    /// which Unity then imported and surfaced in the Project window).
    /// The substitution itself runs as an OS-atomic two-step (write
    /// the new content to a dot-prefixed temp in the source's
    /// directory → <see cref="System.IO.File.Replace(string,string,string,bool)"/>
    /// with the <c>Library/</c> path passed as the backup name).
    /// <c>File.Replace</c> requires a non-null backup path, so the
    /// final-position copy lives at the same <c>Library/</c> location
    /// the step-1 <c>Copy</c> already wrote — Replace overwrites the
    /// step-1 backup with the pre-swap source, which is the same
    /// bytes again so the user's recovery copy stays valid the whole
    /// time. A failure during write leaves the original untouched
    /// and the <c>Library/</c> backup intact; callers can re-run
    /// after fixing whatever blocked the write, or restore by hand
    /// from the <c>Library/</c> copy.
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

            // Atomic write (issue #21) + Library-scoped backup
            // (issue #44). The previous shape dropped the .bak
            // sibling next to the source — fine on Replace's atomic
            // guarantees, but Unity imports the .bak as a stray
            // asset (.meta generated, Project window pollution,
            // accidental commit risk inside Assets/ or Packages/).
            // New shape moves the backup under
            // <project>/Library/RoslynRepl/Backups/, which Unity
            // skips entirely and the standard .gitignore template
            // excludes. Steps:
            //
            //   1. File.Copy(source → Library/RoslynRepl/Backups/<ts>_<name>.bak)
            //         pre-create the backup so a Replace failure
            //         that leaves no destination-side backup still
            //         gives the user a known-good copy.
            //   2. File.WriteAllText(source → tempPath)
            //         write the new contents into a dot-prefixed
            //         sibling temp in the source's directory —
            //         same directory means same volume, which is
            //         what makes File.Replace atomic on the OS
            //         layer.
            //   3. File.Replace(temp → source, null)
            //         OS-atomic swap. The original .cs either
            //         fully becomes tempPath's content or stays
            //         exactly as it was. We pass null for
            //         destinationBackupFileName because the
            //         step-1 Library/ copy already serves the
            //         recovery role.
            //
            // Failure handling: if step 2 throws, the original is
            // untouched and we delete the partial temp. If step 3
            // throws, File.Replace's contract preserves the
            // destination, the step-1 Library/ backup is still
            // valid, and we delete the temp.
            //
            // Temp file naming: dot-prefix + random suffix in the
            // source's directory. Dot-prefix tells Unity's asset
            // importer to skip it (same convention as `.DS_Store`,
            // `.git`, etc.) so we don't trigger a stray .meta during
            // the brief window the file exists.
            string backupPath;
            try
            {
                backupPath = AllocateBackupPath(found.SourcePath);
                File.Copy(found.SourcePath, backupPath, overwrite: true);
            }
            catch (Exception ex) { return Fail($"Could not write backup: {ex.Message}"); }

            string tempPath;
            {
                string dir = Path.GetDirectoryName(found.SourcePath) ?? string.Empty;
                string baseName = Path.GetFileName(found.SourcePath);
                tempPath = Path.Combine(dir,
                    "." + baseName + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            }

            try
            {
                File.WriteAllText(tempPath, newSource);
            }
            catch (Exception ex)
            {
                SafeDelete(tempPath);
                return Fail($"Could not write temporary file '{tempPath}': {ex.Message}");
            }

            try
            {
                // PR-review followup on #44: File.Replace rejects a
                // null destinationBackupFileName with
                // ArgumentException ("The path is not of a legal
                // form."), so the earlier shape failed on every
                // Apply-to-file. Pass the Library/ backup path here
                // instead. Replace then *overwrites* that file with
                // the pre-swap source content; the step-1 Copy wrote
                // identical bytes a moment earlier, so the
                // overwrite is content-equivalent and the user's
                // recovery copy stays valid the whole time.
                //
                // Same-volume invariant: Library/ lives under
                // <project>/ alongside Assets/, so the backup path
                // is always on the same volume as the source file,
                // which is the File.Replace atomicity precondition.
                // The step-1 Copy stays — if Replace itself trips
                // on a sharing violation / antivirus hold, the
                // user still has the pre-Replace copy on disk.
                File.Replace(
                    sourceFileName: tempPath,
                    destinationFileName: found.SourcePath,
                    destinationBackupFileName: backupPath,
                    ignoreMetadataErrors: true);
            }
            catch (Exception ex)
            {
                // File.Replace documents that a failure leaves the
                // destination unchanged. The pre-Replace backup we
                // made in step 1 is still on disk, so the user has
                // a valid recovery point even if Replace itself
                // tripped on a sharing violation / cross-volume /
                // antivirus hold.
                SafeDelete(tempPath);
                return Fail($"Could not atomically replace '{found.SourcePath}': {ex.Message}");
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

        // Project-relative root for Apply-to-file backups. Library/
        // is Unity's reserved derived-data folder — the asset importer
        // skips it entirely, the stock .gitignore template excludes it,
        // and on a clean reimport Unity is free to delete arbitrary
        // contents under it (so callers should treat the backup as
        // "good until the next Library wipe"). Same volume as
        // Application.dataPath, so File.Copy from inside Assets/ stays
        // a cheap intra-disk operation.
        private const string BackupSubDir = "Library/RoslynRepl/Backups";

        // Allocate a unique backup destination for the given source.
        // Path shape:
        //   <project>/Library/RoslynRepl/Backups/<yyyyMMdd_HHmmss_fff>_<baseName>.bak
        //
        // The millisecond suffix in the timestamp + the source's own
        // base name keep the name unique even when two Apply-to-file
        // calls land on the same file in quick succession. If the
        // (vanishingly unlikely) collision still happens we fall back
        // to appending a short GUID — File.Copy(overwrite: true)
        // would otherwise quietly clobber a fresh backup with whatever
        // the second Apply was about to write.
        private static string AllocateBackupPath(string sourcePath)
        {
            string projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath) ?? string.Empty;
            string dir = Path.Combine(projectRoot, BackupSubDir);
            Directory.CreateDirectory(dir);

            string baseName = Path.GetFileName(sourcePath);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string candidate = Path.Combine(dir, $"{ts}_{baseName}.bak");
            if (!File.Exists(candidate)) return candidate;

            // Collision in the same millisecond — extend with a guid.
            return Path.Combine(dir,
                $"{ts}_{Guid.NewGuid().ToString("N").Substring(0, 8)}_{baseName}.bak");
        }

        // Best-effort cleanup of orphaned temp files. Leaving a
        // dot-prefixed temp around is cosmetic (Unity ignores it,
        // git ignores hidden files in most defaults) but better to
        // tidy up; failures here are swallowed because we're already
        // returning a Fail result and a delete-failure on top of a
        // write-failure isn't actionable.
        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort */ }
        }

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
