using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// EditorPrefs-backed persistence for the user's *additional* using
    /// namespaces — the ones layered on top of <see cref="ReplOptions.DefaultUsings"/>.
    ///
    /// EditorPrefs is per-user/per-machine but project-agnostic, so the
    /// storage key is namespaced with a stable hash of the current
    /// project's <see cref="UnityEngine.Application.dataPath"/>. Without
    /// this every Unity project on the same machine would inherit the
    /// same custom usings, and a project-specific addition like
    /// <c>MyGame.Runtime</c> would fire CS0234/CS0246 the moment the
    /// user opened a sibling project that doesn't ship that assembly.
    /// </summary>
    public static class UsingsStore
    {
        /// <summary>Raised whenever <see cref="Save"/> commits a change.</summary>
        public static event Action Changed;

        public static List<string> LoadCustom()
        {
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
            var sanitized = (customUsings ?? Enumerable.Empty<string>())
                .Select(s => s?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .ToList();
            EditorPrefs.SetString(PrefsKey, string.Join(",", sanitized));
            Changed?.Invoke();
        }

        /// <summary>Wipe the user's custom usings for the current project.</summary>
        public static void Clear()
        {
            EditorPrefs.DeleteKey(PrefsKey);
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

        // Per-project bucket — see ProjectScopedPrefs for why a hashed
        // path discriminator is required.
        private static string PrefsKey => ProjectScopedPrefs.BuildKey("RoslynRepl.CustomUsings");
    }
}
