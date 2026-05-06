using System.Collections.Generic;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Bundled starter snippets that ship with the package. They cover
    /// the most common Unity inspection scenarios (scene state, time,
    /// memory, selection, type lookup) so a user opening the REPL for
    /// the first time has something to run rather than a blank editor.
    ///
    /// Importing copies these into <see cref="SnippetStore"/>, where they
    /// behave identically to user-saved entries — editable, removable,
    /// renameable. Existing user snippets with the same name are
    /// preserved (the import skips conflicts rather than overwriting),
    /// so re-running Import after the user edited a default snippet
    /// won't blow their changes away.
    /// </summary>
    public static class DefaultSnippets
    {
        public static IReadOnlyList<SnippetEntry> All => _entries;

        private static readonly SnippetEntry[] _entries =
        {
            new("Unity version",
                "return UnityEngine.Application.unityVersion;"),

            new("Editor time",
                "return new {\n" +
                "    time = UnityEngine.Time.time,\n" +
                "    realtime = UnityEngine.Time.realtimeSinceStartup,\n" +
                "    frame = UnityEngine.Time.frameCount,\n" +
                "};"),

            new("Active scene",
                "var s = UnityEngine.SceneManagement.SceneManager.GetActiveScene();\n" +
                "return new { name = s.name, path = s.path, rootCount = s.rootCount };"),

            new("Root GameObjects",
                "var roots = UnityEngine.SceneManagement.SceneManager\n" +
                "    .GetActiveScene().GetRootGameObjects();\n" +
                "return roots.Select(r => r.name).ToArray();"),

            new("Find singleton (MonoBehaviour)",
                "// Replace TYPE with your manager type and run.\n" +
                "// Returns the first live instance found in the scene.\n" +
                "return UnityEngine.Object.FindFirstObjectByType<UnityEngine.Camera>();"),

            new("Memory snapshot",
                "return new {\n" +
                "    managedKB = System.GC.GetTotalMemory(false) / 1024,\n" +
                "    monoUsedKB = (long)UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1024,\n" +
                "    monoHeapKB = (long)UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / 1024,\n" +
                "};"),

            new("Selection",
                "// What's currently selected in the Hierarchy / Project window?\n" +
                "return UnityEditor.Selection.activeObject;"),

            new("Carry-over example",
                "// Run a snippet that returns a value first, then run this one.\n" +
                "// `_` resolves to the previous successful non-null result; the\n" +
                "// wrapper exposes it as `dynamic` so operators bind at runtime.\n" +
                "return _ != null ? _.ToString() : \"(no previous value)\";"),
        };

        /// <summary>
        /// Adds every default snippet to <see cref="SnippetStore"/>, skipping
        /// names that already exist. Returns (added, skipped) for the caller
        /// to surface to the user.
        /// </summary>
        public static (int added, int skipped) ImportAll()
        {
            int added = 0, skipped = 0;
            foreach (var s in _entries)
            {
                if (SnippetStore.Exists(s.Name)) { skipped++; continue; }
                SnippetStore.Save(s.Name, s.Code);
                added++;
            }
            return (added, skipped);
        }
    }
}
