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

        /// <summary>Persist the supplied spec list. Returns
        /// <c>true</c> iff the on-disk state now reflects the
        /// caller's intent — file written for the non-empty case,
        /// file absent for the empty case. Throws on a hard write
        /// failure (out-of-disk, permissions on the file write
        /// itself); returns <c>false</c> only for the soft case
        /// where the write succeeded as a no-op but the
        /// follow-up <see cref="UserSettingsStorage.Delete"/>
        /// (empty-list branch) couldn't drop the file — typically
        /// a sharing violation from an external editor holding
        /// the JSON open. Callers that just want fire-and-forget
        /// behaviour can keep ignoring the return; UI surfaces
        /// like the per-row Delete button check it to show
        /// "in-memory gone but file survived — will reappear on
        /// next reload" partial-failure status (PR-review
        /// followup on #52).</summary>
        public static bool Save(IEnumerable<MethodPatchSpec> specs)
        {
            var list = (specs ?? Enumerable.Empty<MethodPatchSpec>())
                .Where(s => s != null
                         && !string.IsNullOrEmpty(s.TargetTypeName)
                         && !string.IsNullOrEmpty(s.MethodName))
                .ToList();
            bool persistedOk;
            if (list.Count == 0)
            {
                // PR-review followup on #52: deleting the last patch
                // row used to write `{"version":1,"items":[]}` to
                // disk — Patches UI looked empty but HasAny()
                // returned true and Reset Project Data fell through
                // to the stale-file cleanup branch.
                //
                // Now we delete the file and propagate the bool
                // up so a stuck file (locked / read-only) surfaces
                // to the caller instead of a silent partial
                // failure. UserSettingsStorage.Delete also routes
                // the underlying exception through Debug.LogWarning
                // with the path, so the Console always carries the
                // diagnostic regardless of how the caller handles
                // the bool.
                persistedOk = UserSettingsStorage.Delete(FileName);
            }
            else
            {
                PersistInternal(list);
                persistedOk = true;
            }
            Changed?.Invoke();
            return persistedOk;
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
