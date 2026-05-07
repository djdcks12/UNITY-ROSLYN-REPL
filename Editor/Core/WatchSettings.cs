using System;
using UnityEditor;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Per-project knobs for the Watch panel. Currently a single switch:
    /// whether the global-search fallback is allowed when an expression
    /// fails to compile / run cleanly. Defaults to on for backwards
    /// compatibility with Phase 8's behavior; users with strict side-
    /// effect concerns or large instance pools can turn it off.
    /// </summary>
    public static class WatchSettings
    {
        public static event Action Changed;

        private static string FallbackKey => ProjectScopedPrefs.BuildKey("RoslynRepl.WatchFallbackEnabled");

        public static bool FallbackEnabled
        {
            // EditorPrefs.GetBool returns the default when the key is
            // missing — for new projects that's `true`, matching the
            // shipped Phase 8 / 10 behavior.
            get => EditorPrefs.GetBool(FallbackKey, true);
            set
            {
                if (value == FallbackEnabled) return;
                EditorPrefs.SetBool(FallbackKey, value);
                Changed?.Invoke();
            }
        }
    }
}
