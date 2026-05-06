using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// EditorPrefs-backed persistence for the user's *additional* using
    /// namespaces — the ones layered on top of <see cref="ReplOptions.DefaultUsings"/>.
    ///
    /// EditorPrefs is per-user/per-machine but project-agnostic, so the
    /// storage key is namespaced with a stable hash of the current
    /// project's <see cref="Application.dataPath"/>. Without this every
    /// Unity project on the same machine would inherit the same custom
    /// usings, and a project-specific addition like <c>MyGame.Runtime</c>
    /// would fire CS0234/CS0246 the moment the user opened a sibling
    /// project that doesn't ship that assembly.
    /// </summary>
    public static class UsingsStore
    {
        // Pre-Phase-4 builds wrote to this single project-agnostic key. We
        // migrate any leftover value into the per-project bucket on first
        // access and then delete the legacy key so a different project
        // opening the package next doesn't pick it up.
        private const string LegacyKey = "RoslynRepl.CustomUsings";

        /// <summary>Raised whenever <see cref="Save"/> commits a change.</summary>
        public static event Action Changed;

        public static List<string> LoadCustom()
        {
            MigrateLegacyKeyIfNeeded();
            var raw = EditorPrefs.GetString(PrefsKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return new List<string>();
            return raw.Split(',')
                .Select(s => s?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .ToList();
        }

        public static void Save(IEnumerable<string> customUsings)
        {
            MigrateLegacyKeyIfNeeded();
            var sanitized = (customUsings ?? Enumerable.Empty<string>())
                .Select(s => s?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .ToList();
            EditorPrefs.SetString(PrefsKey, string.Join(",", sanitized));
            Changed?.Invoke();
        }

        /// <summary>
        /// Defaults concatenated with the user's additions, de-duplicated and
        /// in stable order (defaults first). Returned as a fresh list so
        /// callers can hand it to <see cref="ReplOptions.Usings"/> safely.
        /// </summary>
        public static List<string> EffectiveUsings()
        {
            var combined = new List<string>(ReplOptions.DefaultUsings);
            foreach (var u in LoadCustom())
                if (!combined.Contains(u))
                    combined.Add(u);
            return combined;
        }

        // EditorPrefs is shared across every Unity project on the machine,
        // so the key has to discriminate by project. Hash dataPath rather
        // than embedding it raw — registry/plist key length limits, plus
        // the path can contain characters some backends don't love.
        private static string PrefsKey =>
            "RoslynRepl.CustomUsings." + ComputeProjectHash().ToString("x8");

        // FNV-1a 32-bit. We deliberately avoid string.GetHashCode here —
        // modern .NET runtimes randomize string hashes per process, so its
        // output is unstable across editor sessions and would shuffle the
        // user's saved usings out from under them on every restart.
        private static int ComputeProjectHash()
        {
            var path = Application.dataPath ?? string.Empty;
            unchecked
            {
                const int prime = 16777619;
                int hash = unchecked((int)2166136261u);
                for (int i = 0; i < path.Length; i++)
                {
                    hash ^= path[i];
                    hash *= prime;
                }
                return hash;
            }
        }

        private static bool _legacyMigrationChecked;
        private static void MigrateLegacyKeyIfNeeded()
        {
            if (_legacyMigrationChecked) return;
            _legacyMigrationChecked = true;
            if (!EditorPrefs.HasKey(LegacyKey)) return;

            // If we already have a per-project value, the legacy key is
            // ambiguous (we can't tell which project wrote it). Discard
            // it. If the per-project bucket is empty, copy the legacy
            // value forward as a best-guess port for the project the
            // user is currently in.
            if (!EditorPrefs.HasKey(PrefsKey))
            {
                var legacy = EditorPrefs.GetString(LegacyKey, string.Empty);
                if (!string.IsNullOrEmpty(legacy))
                    EditorPrefs.SetString(PrefsKey, legacy);
            }
            EditorPrefs.DeleteKey(LegacyKey);
        }
    }
}
