using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// EditorPrefs-backed history of recently-executed snippets, project-
    /// scoped so each Unity project keeps its own ring. Entries are stored
    /// most-recent-first; on every <see cref="Push"/> the new entry is
    /// inserted at index 0, identical preceding entries are de-duplicated
    /// (so re-running the same snippet doesn't churn the list), and the
    /// tail past <see cref="Capacity"/> is dropped.
    /// </summary>
    public static class RunHistoryStore
    {
        public const int Capacity = 50;

        public static event Action Changed;

        private static string PrefsKey => ProjectScopedPrefs.BuildKey("RoslynRepl.RunHistory");

        public static List<string> Load()
        {
            var raw = EditorPrefs.GetString(PrefsKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return new List<string>();
            var list = new List<string>(Capacity);
            foreach (var token in raw.Split('\n'))
            {
                if (string.IsNullOrEmpty(token)) continue;
                try
                {
                    var bytes = Convert.FromBase64String(token);
                    list.Add(Encoding.UTF8.GetString(bytes));
                }
                catch (FormatException)
                {
                    // Skip a malformed entry rather than nuking the whole
                    // store — the rest of the ring is still usable.
                }
            }
            return list;
        }

        public static void Push(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            var current = Load();
            // De-dupe against the head so re-running the exact same
            // snippet doesn't push it twice in a row. Older identical
            // entries lower in the list are kept — they record a real
            // earlier moment the user ran something different in between.
            if (current.Count > 0 && current[0] == code)
            {
                return;
            }
            current.Insert(0, code);
            if (current.Count > Capacity)
            {
                current.RemoveRange(Capacity, current.Count - Capacity);
            }
            Persist(current);
        }

        public static void Clear()
        {
            EditorPrefs.DeleteKey(PrefsKey);
            Changed?.Invoke();
        }

        private static void Persist(List<string> list)
        {
            var encoded = list.Select(s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? string.Empty)));
            EditorPrefs.SetString(PrefsKey, string.Join("\n", encoded));
            Changed?.Invoke();
        }
    }
}
