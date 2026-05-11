using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Project-local JSON file storage for the larger user-authored
    /// payloads the package keeps between sessions — patch bodies,
    /// snippet library, run history, watch expressions.
    ///
    /// Issue #27 (v0.7.2): those payloads used to live in
    /// <see cref="UnityEditor.EditorPrefs"/>, which on Windows is
    /// backed by the registry and remains after the project folder
    /// is deleted unless the user explicitly hits Clear. Large
    /// patches / snippets also bloat the EditorPrefs blob, and the
    /// cleanup never tied naturally to the project lifecycle. The
    /// canonical Unity location for per-user, per-project data is
    /// the project's <c>UserSettings/</c> folder (Unity's own layout
    /// files / EditorBuildSettings overrides live there). Deleting
    /// the project folder reclaims everything; gitignoring
    /// <c>UserSettings/</c> keeps the data out of source control.
    ///
    /// The atomic-write pipeline mirrors <c>PatchSourceWriter</c> —
    /// write the new content to a dot-prefixed temp file in the same
    /// directory, then <see cref="File.Replace"/> onto the real file
    /// so a process crash mid-write never leaves a half-written
    /// blob the next session reads back as garbage.
    /// </summary>
    public static class UserSettingsStorage
    {
        private const string SubDir = "UserSettings/RoslynRepl";

        // UTF-8 without BOM — Unity tools that diff / open these
        // files don't expect a leading 0xEF 0xBB 0xBF and the BOM
        // would otherwise show up as garbage at the start of every
        // round-trip read.
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>Absolute path to <c>&lt;project&gt;/UserSettings/RoslynRepl/&lt;fileName&gt;</c>.</summary>
        public static string ResolvePath(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) throw new ArgumentException("fileName is required", nameof(fileName));
            // Application.dataPath ends in '/Assets'. The parent directory
            // is the project root, which is where UserSettings/ lives.
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var dir = Path.Combine(projectRoot ?? string.Empty, SubDir);
            return Path.Combine(dir, fileName);
        }

        /// <summary>Try to read the full content of <paramref name="fileName"/>.
        /// Returns <c>false</c> when the file does not exist or cannot be
        /// read — both are non-fatal: callers fall back to legacy
        /// EditorPrefs (migration path) or treat it as "empty store".</summary>
        public static bool TryReadAllText(string fileName, out string content)
        {
            content = null;
            try
            {
                var path = ResolvePath(fileName);
                if (!File.Exists(path)) return false;
                content = File.ReadAllText(path, Utf8NoBom);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Atomically replace the file content with
        /// <paramref name="content"/>. Creates the directory if it doesn't
        /// exist. Uses <see cref="File.Replace"/> on top of a same-directory
        /// dot-prefixed temp file so a crash mid-write can't leave the
        /// destination half-written.</summary>
        public static void WriteAllText(string fileName, string content)
        {
            var path = ResolvePath(fileName);
            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                EnsureGitIgnore(dir);
            }
            else
            {
                EnsureGitIgnore(dir);
            }

            // Dot-prefix the temp so any external tooling watching
            // the directory (e.g. a manual hand-edit IDE) treats the
            // in-flight file as hidden / ephemeral.
            var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".tmp");
            File.WriteAllText(tmp, content ?? string.Empty, Utf8NoBom);

            if (File.Exists(path))
            {
                // File.Replace requires the destination to exist. Same-
                // volume guarantee holds because tmp lives in the same
                // directory as path, so the swap is a rename, not a copy.
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        // Defense-in-depth (issue #27 PR review): the canonical Unity
        // template ships a .gitignore that excludes UserSettings/, but
        // not every consumer project carries that template. A patch
        // body or run-history entry can easily contain a server URL,
        // an auth token, or an account string the user pasted in
        // mid-debug; the goal of this folder is "ephemeral per-user
        // scratch", and an accidental commit makes that promise hollow.
        // Drop a `.gitignore` with a single `*` rule the first time we
        // touch the folder so files in here never show up in
        // `git status` regardless of what the surrounding project's
        // ignore setup looks like. The wildcard also ignores the
        // .gitignore itself (so the marker file isn't a tracked-file
        // suggestion either) — git treats explicit `!` re-includes as
        // the only way to keep a file visible after a parent rule
        // ignored it, and we deliberately don't add one.
        private static void EnsureGitIgnore(string dir)
        {
            var giPath = Path.Combine(dir, ".gitignore");
            if (File.Exists(giPath)) return;
            try
            {
                File.WriteAllText(giPath, "# Auto-generated by Roslyn REPL — keeps the per-user scratch out of git.\n*\n", Utf8NoBom);
            }
            catch
            {
                // A read-only filesystem or unusual permission setup
                // shouldn't break the actual store write that's about
                // to happen — the store itself is the user-facing
                // value, the .gitignore is just the safety net.
            }
        }

        /// <summary>Delete the file if it exists. Quiet on missing —
        /// the caller's intent ("after this returns, no such file
        /// exists") is satisfied either way.</summary>
        public static void Delete(string fileName)
        {
            try
            {
                var path = ResolvePath(fileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // The user-facing semantic of Clear is "stop persisting"; a
                // failed file delete (e.g. the file is open in a hex
                // viewer) doesn't change that the in-memory list is empty,
                // and surfacing an exception mid-Clear breaks every UI
                // callsite that assumes Clear can't throw.
            }
        }

        /// <summary>True iff the file currently exists. Used by stores
        /// to decide between "read from file" and "fall back to
        /// legacy EditorPrefs migration".</summary>
        public static bool Exists(string fileName)
        {
            try { return File.Exists(ResolvePath(fileName)); }
            catch { return false; }
        }
    }
}
