using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Project-local history of recently-executed snippets. Issue #27:
    /// payload lives in <c>&lt;project&gt;/UserSettings/RoslynRepl/runHistory.json</c>.
    /// Entries are stored most-recent-first; on every <see cref="Push"/>
    /// the new entry is inserted at index 0, identical preceding entries
    /// are de-duplicated, and the tail past <see cref="Capacity"/> is dropped.
    /// </summary>
    public static class RunHistoryStore
    {
        public const int Capacity = 50;

        public static event Action Changed;

        private const string FileName = "runHistory.json";

        [Serializable]
        private sealed class Envelope
        {
            public int version = 1;
            public List<string> items = new List<string>();
        }

        public static List<string> Load()
        {
            if (!UserSettingsStorage.TryReadAllText(FileName, out var json))
                return new List<string>();
            return DecodeJson(json);
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
            Changed?.Invoke();
        }

        private static void Persist(List<string> list)
        {
            var env = new Envelope { items = new List<string>() };
            foreach (var s in list) env.items.Add(s ?? string.Empty);
            UserSettingsStorage.WriteAllText(FileName, JsonUtility.ToJson(env, prettyPrint: true));
            Changed?.Invoke();
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
    }
}
