using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Project-local file-backed list of watch expressions. Payload
    /// lives in <c>&lt;project&gt;/UserSettings/RoslynRepl/watches.json</c>,
    /// scoped to the project folder so deleting the project reclaims
    /// the data in one go.
    /// </summary>
    public static class WatchStore
    {
        public static event Action Changed;

        private const string FileName = "watches.json";

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

        /// <summary>True iff the on-disk file currently exists. Used
        /// by Reset Project Data to detect "file is there but Load
        /// returned an empty list" — a corrupt JSON, a locked file,
        /// or any other read failure all collapse to an empty list,
        /// and counting only `Load().Count` would skip the wipe in
        /// exactly the cases the user most needs Reset to handle.
        /// PR-review followup on #27.</summary>
        public static bool HasAny() => UserSettingsStorage.Exists(FileName);

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

        /// <summary>Wipe persisted watches. Returns the success flag
        /// from <see cref="UserSettingsStorage.Delete"/> (true =
        /// post-call file does not exist) so callers like Reset
        /// Project Data can aggregate "did everything actually go
        /// away?" — a stuck file would otherwise be invisible behind
        /// the Changed event we always fire. PR-review followup on
        /// #27.</summary>
        public static bool Clear()
        {
            bool ok = UserSettingsStorage.Delete(FileName);
            Changed?.Invoke();
            return ok;
        }

        private static void Persist(List<string> list)
        {
            var env = new Envelope { items = new List<string>() };
            foreach (var s in list)
            {
                if (!string.IsNullOrWhiteSpace(s)) env.items.Add(s);
            }
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
