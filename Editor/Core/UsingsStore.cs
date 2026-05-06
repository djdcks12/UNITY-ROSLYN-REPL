using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// EditorPrefs-backed persistence for the user's *additional* using
    /// namespaces — the ones layered on top of <see cref="ReplOptions.DefaultUsings"/>.
    /// Stored as a comma-joined string under a single key so the storage
    /// stays portable across machines and survives package updates without
    /// schema migration.
    /// </summary>
    public static class UsingsStore
    {
        private const string PrefsKey = "RoslynRepl.CustomUsings";

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
    }
}
