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
    /// Format: each spec is six base64-encoded fields joined by `|`,
    /// specs joined by `\n`. Order matches the spec's authoring order
    /// (TargetTypeName | MethodName | ParameterTypes | OriginalBody |
    /// PatchBody | Status). Decode failure on a single line is swallowed
    /// — a malformed entry shouldn't take down the whole patch set.
    ///
    /// LastError is intentionally not persisted: it's transient diagnostic
    /// state from the last apply attempt, valid only for the current
    /// session. the bootstrap path re-runs Apply on the next boot for any spec
    /// stored as Active and refreshes LastError from that attempt.
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
                B64(s.Status.ToString()));
        }

        private static MethodPatchSpec TryDecode(string line)
        {
            try
            {
                var parts = line.Split('|');
                if (parts.Length < 6) return null;
                return new MethodPatchSpec
                {
                    TargetTypeName = D64(parts[0]),
                    MethodName     = D64(parts[1]),
                    ParameterTypes = D64(parts[2]),
                    OriginalBody   = D64(parts[3]),
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
