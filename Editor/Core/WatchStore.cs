using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// EditorPrefs-backed list of watch expressions, project-scoped via
    /// <see cref="ProjectScopedPrefs"/>. Storage is base64-per-entry joined
    /// with '\n' (the same format the snippet library uses), so any
    /// character the user can type is safe.
    /// </summary>
    public static class WatchStore
    {
        public static event Action Changed;

        private static string PrefsKey => ProjectScopedPrefs.BuildKey("RoslynRepl.Watches");

        public static List<string> Load()
        {
            var raw = EditorPrefs.GetString(PrefsKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return new List<string>();
            var list = new List<string>();
            foreach (var token in raw.Split('\n'))
            {
                if (string.IsNullOrEmpty(token)) continue;
                try { list.Add(Encoding.UTF8.GetString(Convert.FromBase64String(token))); }
                catch (FormatException) { /* skip */ }
            }
            return list;
        }

        public static void Add(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return;
            expression = expression.Trim();
            var list = Load();
            // Skip exact duplicates so quick double-Enter doesn't add the
            // same row twice. Different whitespace is preserved as
            // intentional (different expression in the user's mind).
            if (list.Contains(expression)) return;
            list.Add(expression);
            Persist(list);
        }

        public static void Remove(string expression)
        {
            var list = Load();
            int removed = list.RemoveAll(s => s == expression);
            if (removed > 0) Persist(list);
        }

        public static void Clear()
        {
            EditorPrefs.DeleteKey(PrefsKey);
            Changed?.Invoke();
        }

        private static void Persist(List<string> list)
        {
            var encoded = list
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)));
            EditorPrefs.SetString(PrefsKey, string.Join("\n", encoded));
            Changed?.Invoke();
        }
    }
}
