using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Project-local history of recently-executed snippets. Issue #27:
    /// storage moved from <see cref="EditorPrefs"/> to
    /// <c>&lt;project&gt;/UserSettings/RoslynRepl/runHistory.json</c>,
    /// for the same lifecycle / size reasons as
    /// <see cref="WatchStore"/>. Entries are stored most-recent-first;
    /// on every <see cref="Push"/> the new entry is inserted at index 0,
    /// identical preceding entries are de-duplicated, and the tail past
    /// <see cref="Capacity"/> is dropped. First load after upgrade
    /// auto-migrates from the historical EditorPrefs key.
    /// </summary>
    public static class RunHistoryStore
    {
        public const int Capacity = 50;

        public static event Action Changed;

        private const string FileName = "runHistory.json";

        private static string LegacyPrefsKey => ProjectScopedPrefs.BuildKey("RoslynRepl.RunHistory");

        [Serializable]
        private sealed class Envelope
        {
            public int version = 1;
            public List<string> items = new List<string>();
        }

        public static List<string> Load()
        {
            if (UserSettingsStorage.TryReadAllText(FileName, out var json))
            {
                return DecodeJson(json);
            }

            // Capture HasKey *before* LoadLegacy so we drop the
            // registry-backed key even when its decoded list is empty
            // (empty blob, all-corrupt entries) — see WatchStore.Load
            // for the same reasoning. PR-review followup on #27.
            bool hadLegacy = EditorPrefs.HasKey(LegacyPrefsKey);
            var legacy = LoadLegacy();
            if (hadLegacy) EditorPrefs.DeleteKey(LegacyPrefsKey);
            if (legacy.Count > 0) PersistInternal(legacy);
            return legacy;
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
            UserSettingsStorage.Delete(FileName);
            EditorPrefs.DeleteKey(LegacyPrefsKey);
            Changed?.Invoke();
        }

        private static void Persist(List<string> list)
        {
            PersistInternal(list);
            Changed?.Invoke();
        }

        private static void PersistInternal(List<string> list)
        {
            var env = new Envelope { items = new List<string>() };
            foreach (var s in list) env.items.Add(s ?? string.Empty);
            UserSettingsStorage.WriteAllText(FileName, JsonUtility.ToJson(env, prettyPrint: true));
        }

        private static List<string> DecodeJson(string json)
        {
            try
            {
                var env = JsonUtility.FromJson<Envelope>(json);
                return env?.items ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static List<string> LoadLegacy()
        {
            var raw = EditorPrefs.GetString(LegacyPrefsKey, string.Empty);
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
                    // Skip a malformed entry rather than nuking the
                    // whole store.
                }
            }
            return list;
        }
    }
}
