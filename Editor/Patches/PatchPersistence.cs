using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// File-backed persistence for the runtime method patch list. Issue
    /// #27 (v0.7.2): storage moved from a project-scoped
    /// <see cref="EditorPrefs"/> blob to
    /// <c>&lt;project&gt;/UserSettings/RoslynRepl/patches.json</c> so
    /// the data is tied to the project folder (no registry leftovers
    /// after the folder is deleted, no per-user EditorPrefs bloat for
    /// large patch bodies). The first <see cref="Load"/> after upgrade
    /// detects the historical key, decodes the pipe-separated base64
    /// format, writes the JSON file, and drops the old key.
    ///
    /// File format (current schema, <c>version = 1</c>):
    /// <code>
    /// {
    ///   "version": 1,
    ///   "items": [
    ///     {
    ///       "TargetTypeName": "...",
    ///       "MethodName":     "...",
    ///       "ParameterTypes": "...",
    ///       "OriginalBody":   "...",
    ///       "HasOriginalBody": true,
    ///       "PatchBody":      "...",
    ///       "Status":         1
    ///     }
    ///   ]
    /// }
    /// </code>
    ///
    /// <c>HasOriginalBody</c> is the explicit "did Pull Original ever
    /// run for this spec?" signal — a method declared as
    /// <c>void Foo() {}</c> pulls as an empty-string body, which is a
    /// real snapshot, so the flag is needed to keep <c>null</c> (no
    /// snapshot taken) distinct from <c>""</c> (empty-body snapshot).
    ///
    /// Decode failure on a single line is swallowed — a malformed
    /// entry shouldn't take down the whole patch set.
    ///
    /// <c>LastError</c> is intentionally not persisted: it's transient
    /// diagnostic state from the last apply attempt, valid only for
    /// the current session. The bootstrap path re-runs Apply on the
    /// next boot for any spec stored as Active and refreshes LastError
    /// from that attempt.
    /// </summary>
    public static class PatchPersistence
    {
        public static event Action Changed;

        private const string FileName = "patches.json";

        private static string LegacyPrefsKey => ProjectScopedPrefs.BuildKey("RoslynRepl.RuntimePatches");

        [Serializable]
        private sealed class SpecDto
        {
            public string TargetTypeName;
            public string MethodName;
            public string ParameterTypes;
            public string OriginalBody;
            public bool HasOriginalBody;
            public string PatchBody;
            public PatchStatus Status;
        }

        [Serializable]
        private sealed class Envelope
        {
            public int version = 1;
            public List<SpecDto> items = new List<SpecDto>();
        }

        public static List<MethodPatchSpec> Load()
        {
            if (UserSettingsStorage.TryReadAllText(FileName, out var json))
            {
                return DecodeJson(json);
            }

            // Migration: file missing, historical EditorPrefs key
            // might still hold values for projects upgraded from 0.7.1.
            // Decode the legacy pipe-separated blob, write the JSON file,
            // drop the legacy key.
            var legacy = LoadLegacy();
            if (legacy.Count > 0)
            {
                PersistInternal(legacy);
                EditorPrefs.DeleteKey(LegacyPrefsKey);
            }
            return legacy;
        }

        public static void Save(IEnumerable<MethodPatchSpec> specs)
        {
            var list = (specs ?? Enumerable.Empty<MethodPatchSpec>())
                .Where(s => s != null
                         && !string.IsNullOrEmpty(s.TargetTypeName)
                         && !string.IsNullOrEmpty(s.MethodName))
                .ToList();
            PersistInternal(list);
            Changed?.Invoke();
        }

        public static void Clear()
        {
            UserSettingsStorage.Delete(FileName);
            // Belt-and-braces: drop the legacy key too so a project
            // that upgraded mid-Clear doesn't leave EditorPrefs data
            // hanging around. The historical README promised every
            // package-owned persistence slot goes away on Clear.
            EditorPrefs.DeleteKey(LegacyPrefsKey);
            Changed?.Invoke();
        }

        /// <summary>
        /// True iff the project has *any* persisted patch data — used
        /// by <see cref="PatchRegistry.Clear"/> to detect stale-key
        /// cases where the in-memory dictionary is empty but the
        /// persistence layer still has bytes on disk (or in the
        /// legacy EditorPrefs key for not-yet-migrated projects).
        /// </summary>
        public static bool HasAny()
        {
            return UserSettingsStorage.Exists(FileName)
                || EditorPrefs.HasKey(LegacyPrefsKey);
        }

        private static void PersistInternal(IEnumerable<MethodPatchSpec> specs)
        {
            var env = new Envelope { items = new List<SpecDto>() };
            foreach (var s in specs)
            {
                if (s == null) continue;
                if (string.IsNullOrEmpty(s.TargetTypeName) || string.IsNullOrEmpty(s.MethodName)) continue;
                env.items.Add(new SpecDto
                {
                    TargetTypeName  = s.TargetTypeName,
                    MethodName      = s.MethodName,
                    ParameterTypes  = s.ParameterTypes ?? string.Empty,
                    OriginalBody    = s.OriginalBody   ?? string.Empty,
                    HasOriginalBody = s.OriginalBody   != null,
                    PatchBody       = s.PatchBody      ?? string.Empty,
                    Status          = s.Status,
                });
            }
            UserSettingsStorage.WriteAllText(FileName, JsonUtility.ToJson(env, prettyPrint: true));
        }

        private static List<MethodPatchSpec> DecodeJson(string json)
        {
            var list = new List<MethodPatchSpec>();
            try
            {
                var env = JsonUtility.FromJson<Envelope>(json);
                if (env?.items == null) return list;
                foreach (var dto in env.items)
                {
                    if (dto == null) continue;
                    list.Add(new MethodPatchSpec
                    {
                        TargetTypeName = dto.TargetTypeName,
                        MethodName     = dto.MethodName,
                        ParameterTypes = dto.ParameterTypes,
                        OriginalBody   = dto.HasOriginalBody ? (dto.OriginalBody ?? string.Empty) : null,
                        PatchBody      = dto.PatchBody,
                        Status         = dto.Status,
                    });
                }
            }
            catch
            {
                // Malformed file — surface as "no specs" rather than
                // crashing the editor on load. The user can re-Apply
                // via the form; the in-memory registry stays clean.
            }
            return list;
        }

        // Decode the historical pipe-separated base64 format used by
        // 0.7.0 / 0.7.1. Format: each spec is seven base64-encoded
        // fields joined by '|', specs joined by '\n'. Field order:
        //   TargetTypeName | MethodName | ParameterTypes |
        //   OriginalBody   | PatchBody  | Status         | HasOriginalBody
        // Six-field legacy lines (predating HasOriginalBody) collapse
        // an empty OriginalBody back to null because that's the
        // overwhelmingly common pre-source-pull meaning.
        private static List<MethodPatchSpec> LoadLegacy()
        {
            var raw = EditorPrefs.GetString(LegacyPrefsKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return new List<MethodPatchSpec>();
            var list = new List<MethodPatchSpec>();
            foreach (var line in raw.Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;
                var spec = TryDecodeLegacyLine(line);
                if (spec != null) list.Add(spec);
            }
            return list;
        }

        private static MethodPatchSpec TryDecodeLegacyLine(string line)
        {
            try
            {
                var parts = line.Split('|');
                if (parts.Length < 6) return null;
                var origBody = D64(parts[3]);

                bool hasOriginal;
                if (parts.Length >= 7)
                {
                    hasOriginal = D64(parts[6]) == "1";
                }
                else
                {
                    hasOriginal = !string.IsNullOrEmpty(origBody);
                }

                return new MethodPatchSpec
                {
                    TargetTypeName = D64(parts[0]),
                    MethodName     = D64(parts[1]),
                    ParameterTypes = D64(parts[2]),
                    OriginalBody   = hasOriginal ? origBody : null,
                    PatchBody      = D64(parts[4]),
                    Status         = Enum.TryParse<PatchStatus>(D64(parts[5]), out var st)
                                       ? st
                                       : PatchStatus.Inactive,
                };
            }
            catch (FormatException)
            {
                // base64 broke on one field — skip this line so the
                // rest of the patch set still migrates.
                return null;
            }
        }

        private static string D64(string b) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(b));
    }
}
