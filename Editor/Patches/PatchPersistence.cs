using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// File-backed persistence for the runtime method patch list.
    /// Payload lives in
    /// <c>&lt;project&gt;/UserSettings/RoslynRepl/patches.json</c> so
    /// it stays tied to the project folder — deleting the project
    /// reclaims every patch body in one go.
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
            if (!UserSettingsStorage.TryReadAllText(FileName, out var json))
                return new List<MethodPatchSpec>();
            return DecodeJson(json);
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

        /// <summary>Delete the on-disk patch file. Returns the success
        /// flag from <see cref="UserSettingsStorage.Delete"/> so the
        /// caller can aggregate "did the file actually go away?" with
        /// the other store deletes — Reset Project Data uses this to
        /// surface partial-failure dialogs instead of unconditional
        /// success. PR-review followup on #27.</summary>
        public static bool Clear()
        {
            bool ok = UserSettingsStorage.Delete(FileName);
            Changed?.Invoke();
            return ok;
        }

        /// <summary>
        /// True iff the project has *any* persisted patch data — used
        /// by <see cref="PatchRegistry.Clear"/> to detect stale-file
        /// cases where the in-memory dictionary is empty but a file
        /// still exists on disk.
        /// </summary>
        public static bool HasAny()
        {
            return UserSettingsStorage.Exists(FileName);
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
    }
}
