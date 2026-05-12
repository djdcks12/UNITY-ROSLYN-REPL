using System;
using UnityEditor;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Per-project knobs for the Output result tree. Currently a
    /// single switch: whether property getters are invoked while
    /// walking a value's tree.
    ///
    /// Issue #28: user-defined property getters routinely do things
    /// the Output panel shouldn't trigger from a passive
    /// "inspect this value" walk — lazy init, IO, log emission,
    /// counter mutation, side-effecting cache lookups. The Watch
    /// panel already opted out (see <see cref="WatchEvaluator"/>'s
    /// BuildTree call site) because Watch refreshes on every Run;
    /// Output sat at the opposite end of that axis (one-shot
    /// inspection per Run) so the default stayed at "include
    /// properties" historically. The reviewer's point in #28 is
    /// that even a one-shot getter call can permanently change
    /// project state, so the safer default for Output is also
    /// fields-only — users who want property values back can flip
    /// the inline toggle and re-Run.
    ///
    /// Default: <c>false</c>. The toggle lives in the Output panel
    /// header so it's adjustable without a settings dialog.
    /// </summary>
    public static class OutputSettings
    {
        public static event Action Changed;

        private static string IncludePropertiesKey =>
            ProjectScopedPrefs.BuildKey("RoslynRepl.OutputIncludeProperties");

        /// <summary>True iff the Output result tree should invoke
        /// readable property getters while walking. Defaults to
        /// <c>false</c> (fields-only).</summary>
        public static bool IncludeProperties
        {
            get => EditorPrefs.GetBool(IncludePropertiesKey, false);
            set
            {
                if (value == IncludeProperties) return;
                EditorPrefs.SetBool(IncludePropertiesKey, value);
                Changed?.Invoke();
            }
        }
    }
}
