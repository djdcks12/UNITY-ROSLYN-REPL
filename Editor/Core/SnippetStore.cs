using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Project-local library of named snippets. Issue #27: payload lives
    /// in <c>&lt;project&gt;/UserSettings/RoslynRepl/snippets.json</c>.
    /// </summary>
    public static class SnippetStore
    {
        public static event Action Changed;

        private const string FileName = "snippets.json";

        [Serializable]
        private sealed class Envelope
        {
            public int version = 1;
            public List<SnippetEntry> items = new List<SnippetEntry>();
        }

        public static List<SnippetEntry> Load()
        {
            if (!UserSettingsStorage.TryReadAllText(FileName, out var json))
                return new List<SnippetEntry>();
            return DecodeJson(json);
        }

        /// <summary>True iff the on-disk file currently exists. See
        /// <see cref="WatchStore.HasAny"/> for the same reasoning —
        /// Reset Project Data's scope check needs file existence
        /// separately from successful Load so corrupt / unreadable
        /// files still route through Clear. PR-review followup on
        /// #27.</summary>
        public static bool HasAny() => UserSettingsStorage.Exists(FileName);

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

        /// <summary>Wipe every saved snippet for the current project.
        /// Returns the success flag from
        /// <see cref="UserSettingsStorage.Delete"/> so Reset Project
        /// Data can aggregate file-deletion failures instead of
        /// unconditionally claiming the wipe succeeded. PR-review
        /// followup on #27.</summary>
        public static bool Clear()
        {
            bool ok = UserSettingsStorage.Delete(FileName);
            Changed?.Invoke();
            return ok;
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
            var env = new Envelope { items = new List<SnippetEntry>() };
            foreach (var s in list)
            {
                if (s == null || string.IsNullOrEmpty(s.Name)) continue;
                env.items.Add(new SnippetEntry(s.Name, s.Code ?? string.Empty));
            }
            UserSettingsStorage.WriteAllText(FileName, JsonUtility.ToJson(env, prettyPrint: true));
            Changed?.Invoke();
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
    }
}
