using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// EditorPrefs-backed persistence for the runtime method patch list.
    /// Project-scoped via <see cref="ProjectScopedPrefs"/>, same shape
    /// the snippet / history / watch stores already use, so cross-project
    /// leakage is impossible by construction.
    ///
    /// Format: each spec is seven base64-encoded fields joined by `|`,
    /// specs joined by `\n`. Order:
    ///   TargetTypeName | MethodName | ParameterTypes |
    ///   OriginalBody | PatchBody | Status | HasOriginalBody
    ///
    /// HasOriginalBody is the explicit "did Pull Original ever run
    /// for this spec?" signal. It's a separate field because
    /// `OriginalBody == ""` is a real valid snapshot (a method
    /// declared as `void Foo() {}` pulls as an empty string body),
    /// and we'd otherwise have no way to tell that apart from
    /// "no snapshot taken — OriginalBody is empty by default".
    ///
    /// Six-field legacy data still loads: when HasOriginalBody is
    /// missing, `IsNullOrEmpty(OriginalBody)` decides. Empty bodies
    /// in legacy data are treated as "no snapshot" because that's
    /// the overwhelmingly common case in pre-source-export drafts.
    /// New writes always emit seven fields.
    ///
    /// Decode failure on a single line is swallowed — a malformed
    /// entry shouldn't take down the whole patch set.
    ///
    /// LastError is intentionally not persisted: it's transient
    /// diagnostic state from the last apply attempt, valid only for
    /// the current session. The bootstrap path re-runs Apply on the
    /// next boot for any spec stored as Active and refreshes
    /// LastError from that attempt.
    /// </summary>
    public static class PatchPersistence
    {
        public static event Action Changed;

        private static string PrefsKey => ProjectScopedPrefs.BuildKey("RoslynRepl.RuntimePatches");

        public static List<MethodPatchSpec> Load()
        {
            var raw = EditorPrefs.GetString(PrefsKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return new List<MethodPatchSpec>();
            var list = new List<MethodPatchSpec>();
            foreach (var line in raw.Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;
                var spec = TryDecode(line);
                if (spec != null) list.Add(spec);
            }
            return list;
        }

        public static void Save(IEnumerable<MethodPatchSpec> specs)
        {
            var encoded = (specs ?? Enumerable.Empty<MethodPatchSpec>())
                .Where(s => s != null
                         && !string.IsNullOrEmpty(s.TargetTypeName)
                         && !string.IsNullOrEmpty(s.MethodName))
                .Select(EncodeOne);
            EditorPrefs.SetString(PrefsKey, string.Join("\n", encoded));
            Changed?.Invoke();
        }

        public static void Clear()
        {
            EditorPrefs.DeleteKey(PrefsKey);
            Changed?.Invoke();
        }

        /// <summary>
        /// True iff the project has *any* persisted patch data — used by
        /// <see cref="PatchRegistry.Clear"/> to detect stale-key cases
        /// where the in-memory dictionary is empty but the EditorPrefs
        /// entry still exists (e.g. an older package version wrote
        /// SetString(key, "") instead of DeleteKey on Reset; the empty
        /// blob deserializes back to zero specs but the key sticks).
        /// </summary>
        public static bool HasAny() => EditorPrefs.HasKey(PrefsKey);

        private static string EncodeOne(MethodPatchSpec s)
        {
            return string.Join("|",
                B64(s.TargetTypeName),
                B64(s.MethodName),
                B64(s.ParameterTypes ?? string.Empty),
                B64(s.OriginalBody   ?? string.Empty),
                B64(s.PatchBody      ?? string.Empty),
                B64(s.Status.ToString()),
                B64(s.OriginalBody != null ? "1" : "0"));
        }

        private static MethodPatchSpec TryDecode(string line)
        {
            try
            {
                var parts = line.Split('|');
                if (parts.Length < 6) return null;
                var origBody = D64(parts[3]);

                // 7-field format carries an explicit HasOriginalBody
                // flag so we can keep `null` (no Pull yet) and `""`
                // (empty-body Pull) distinct across reload. Legacy
                // 6-field reads collapse `""` to `null` because that's
                // the overwhelmingly common pre-Phase-E meaning of
                // "OriginalBody empty by default".
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
                // base64 broke — skip the line so the rest of the
                // patch set still loads.
                return null;
            }
        }

        private static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? string.Empty));

        private static string D64(string b) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(b));
    }
}
