using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;

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
    /// EditorPrefs-backed library of named snippets, project-scoped via
    /// <see cref="ProjectScopedPrefs"/>. Storage format mirrors
    /// <see cref="RunHistoryStore"/>: each entry is "name_b64|code_b64",
    /// entries joined with "\n". Base64 chars don't include '|' or '\n',
    /// so the separators stay unambiguous regardless of what the user
    /// typed for the name or the code.
    /// </summary>
    public static class SnippetStore
    {
        public static event Action Changed;

        private static string PrefsKey => ProjectScopedPrefs.BuildKey("RoslynRepl.Snippets");

        public static List<SnippetEntry> Load()
        {
            var raw = EditorPrefs.GetString(PrefsKey, string.Empty);
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
                    // Skip a corrupt entry; the rest of the library is
                    // still usable.
                }
            }
            return list;
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
            EditorPrefs.DeleteKey(PrefsKey);
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
            var encoded = list
                .Where(s => s != null && !string.IsNullOrEmpty(s.Name))
                .Select(s => Encode(s.Name) + "|" + Encode(s.Code ?? string.Empty));
            EditorPrefs.SetString(PrefsKey, string.Join("\n", encoded));
            Changed?.Invoke();
        }

        private static string Encode(string s)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? string.Empty));
        }

        private static string Decode(string b64)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
    }
}
