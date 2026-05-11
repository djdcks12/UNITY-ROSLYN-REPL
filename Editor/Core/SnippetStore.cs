using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// One named snippet — user-supplied label + the code it stores.
    /// </summary>
    [Serializable]
    public class SnippetEntry
    {
        public string Name;
        public string Code;

        public SnippetEntry() { }
        public SnippetEntry(string name, string code) { Name = name; Code = code; }
    }

    /// <summary>
    /// Project-local library of named snippets. Issue #27: storage moved
    /// from <see cref="EditorPrefs"/> to
    /// <c>&lt;project&gt;/UserSettings/RoslynRepl/snippets.json</c>;
    /// migration from the historical EditorPrefs blob happens on the
    /// first <see cref="Load"/> after upgrade.
    /// </summary>
    public static class SnippetStore
    {
        public static event Action Changed;

        private const string FileName = "snippets.json";

        private static string LegacyPrefsKey => ProjectScopedPrefs.BuildKey("RoslynRepl.Snippets");

        [Serializable]
        private sealed class Envelope
        {
            public int version = 1;
            public List<SnippetEntry> items = new List<SnippetEntry>();
        }

        public static List<SnippetEntry> Load()
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

        /// <summary>
        /// Save (insert or update). If a snippet with the given name
        /// already exists, its code is overwritten and ordering is
        /// preserved; otherwise the new entry is appended.
        /// </summary>
        public static void Save(string name, string code)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            code ??= string.Empty;

            var list = Load();
            int idx = list.FindIndex(s => s.Name == name);
            if (idx >= 0) list[idx].Code = code;
            else list.Add(new SnippetEntry(name, code));
            Persist(list);
        }

        public static void Delete(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            var list = Load();
            int removed = list.RemoveAll(s => s.Name == name);
            if (removed > 0) Persist(list);
        }

        /// <summary>Wipe every saved snippet for the current project.</summary>
        public static void Clear()
        {
            UserSettingsStorage.Delete(FileName);
            EditorPrefs.DeleteKey(LegacyPrefsKey);
            Changed?.Invoke();
        }

        public static void Rename(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (oldName == newName) return;

            var list = Load();
            int idx = list.FindIndex(s => s.Name == oldName);
            if (idx < 0) return;
            // Refuse the rename if the new name already exists — the
            // alternative (silently merging) would silently destroy one
            // of the two snippets.
            if (list.Any(s => s.Name == newName)) return;
            list[idx].Name = newName;
            Persist(list);
        }

        public static bool Exists(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return Load().Any(s => s.Name == name);
        }

        private static void Persist(List<SnippetEntry> list)
        {
            PersistInternal(list);
            Changed?.Invoke();
        }

        private static void PersistInternal(List<SnippetEntry> list)
        {
            var env = new Envelope { items = new List<SnippetEntry>() };
            foreach (var s in list)
            {
                if (s == null || string.IsNullOrEmpty(s.Name)) continue;
                env.items.Add(new SnippetEntry(s.Name, s.Code ?? string.Empty));
            }
            UserSettingsStorage.WriteAllText(FileName, JsonUtility.ToJson(env, prettyPrint: true));
        }

        private static List<SnippetEntry> DecodeJson(string json)
        {
            try
            {
                var env = JsonUtility.FromJson<Envelope>(json);
                return env?.items ?? new List<SnippetEntry>();
            }
            catch
            {
                return new List<SnippetEntry>();
            }
        }

        // Decode the historical EditorPrefs blob format: each entry is
        // "name_b64|code_b64", entries joined by '\n'.
        private static List<SnippetEntry> LoadLegacy()
        {
            var raw = EditorPrefs.GetString(LegacyPrefsKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return new List<SnippetEntry>();
            var list = new List<SnippetEntry>();
            foreach (var line in raw.Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;
                var pipe = line.IndexOf('|');
                if (pipe <= 0) continue;
                try
                {
                    var name = Decode(line.Substring(0, pipe));
                    var code = Decode(line.Substring(pipe + 1));
                    if (string.IsNullOrEmpty(name)) continue;
                    list.Add(new SnippetEntry(name, code));
                }
                catch (FormatException)
                {
                    // Skip a corrupt entry; the rest of the library
                    // is still usable.
                }
            }
            return list;
        }

        private static string Decode(string b64)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
    }
}
