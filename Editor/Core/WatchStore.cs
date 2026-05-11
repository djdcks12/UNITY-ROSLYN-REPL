using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Project-local file-backed list of watch expressions. Issue #27:
    /// the on-disk location moved from <see cref="EditorPrefs"/> to
    /// <c>&lt;project&gt;/UserSettings/RoslynRepl/watches.json</c>, so
    /// the data is tied to the project folder (deletes with it) and
    /// no longer bloats the project-agnostic EditorPrefs blob. The
    /// first <see cref="Load"/> after upgrade detects the historical
    /// EditorPrefs key, copies its contents into the new file, and
    /// drops the legacy key.
    /// </summary>
    public static class WatchStore
    {
        public static event Action Changed;

        private const string FileName = "watches.json";

        // Same key the historical EditorPrefs-backed store used; the
        // migration path reads it once and deletes it.
        private static string LegacyPrefsKey => ProjectScopedPrefs.BuildKey("RoslynRepl.Watches");

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

            // Migration path: file is missing but the historical
            // EditorPrefs key might still hold values for projects
            // that upgraded from 0.7.1. Decode the legacy format,
            // write the new file, drop the old key.
            //
            // Capture HasKey *before* LoadLegacy so we can drop the
            // registry-backed key even when its decoded list is empty
            // (empty blob, all-corrupt entries) — leaving an empty key
            // behind would defeat the EditorPrefs-cleanup goal of
            // issue #27. PR-review followup.
            bool hadLegacy = EditorPrefs.HasKey(LegacyPrefsKey);
            var legacy = LoadLegacy();
            if (hadLegacy) EditorPrefs.DeleteKey(LegacyPrefsKey);
            if (legacy.Count > 0) PersistInternal(legacy);
            return legacy;
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
            UserSettingsStorage.Delete(FileName);
            // Belt-and-braces: an upgraded project might still carry a
            // dangling legacy EditorPrefs key from before the first
            // Load triggered migration. The historical README promised
            // Clear nukes every package-owned persistence slot, so
            // honour that across both backends.
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
            foreach (var s in list)
            {
                if (!string.IsNullOrWhiteSpace(s)) env.items.Add(s);
            }
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

        // Decode the historical EditorPrefs blob format: base64 entries
        // joined by '\n'. Kept private — once migration runs, the
        // legacy key is dropped and this path never runs again for that
        // project.
        private static List<string> LoadLegacy()
        {
            var raw = EditorPrefs.GetString(LegacyPrefsKey, string.Empty);
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
    }
}
