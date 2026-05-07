using System;
using UnityEditor;

namespace RoslynRepl.Editor.Diagnostics
{
    /// <summary>
    /// First-launch nudge for users who just imported the package and
    /// haven't yet downloaded the Roslyn / Harmony DLLs from NuGet.
    /// Without this, the package looks broken on first run — `Tools /
    /// Roslyn REPL / Open` shows a window that can't compile anything.
    ///
    /// Behavior:
    ///  • Fires once per Editor session (SessionState gate). Domain
    ///    reloads inside the same session don't re-prompt.
    ///  • Skipped if the dependencies are already loaded — installing
    ///    once is enough.
    ///  • Skipped if the user picked "Don't show again" (EditorPrefs
    ///    flag, project-scoped through ProjectScopedPrefs so two
    ///    projects on the same machine can decide independently).
    ///  • Three buttons: Install Now, Don't Show Again, Later. "Later"
    ///    leaves both flags untouched, so the prompt comes back the
    ///    next time Editor is launched.
    /// </summary>
    [InitializeOnLoad]
    public static class FirstRunPrompt
    {
        private const string SessionKey       = "RoslynRepl.FirstRunPromptShownThisSession";
        private const string SkipPrefBaseName = "RoslynRepl.SkipFirstRunPrompt";

        static FirstRunPrompt()
        {
            // Defer one Editor frame so the menu / dialog APIs are
            // safely callable. Calling EditorUtility.DisplayDialog
            // from inside an [InitializeOnLoad] static constructor
            // is unsupported on some Unity versions.
            EditorApplication.delayCall += MaybePrompt;
        }

        private static void MaybePrompt()
        {
            // Once per session, regardless of outcome — even Cancel
            // shouldn't re-fire on the next domain reload.
            if (SessionState.GetBool(SessionKey, false)) return;
            SessionState.SetBool(SessionKey, true);

            if (DependenciesInstalled()) return;
            if (UserDismissedPermanently()) return;

            int choice = EditorUtility.DisplayDialogComplex(
                "Roslyn REPL — First-run setup",
                "Roslyn REPL needs two MIT-licensed dependencies to run, downloaded once from NuGet:\n\n" +
                "  • Roslyn 4.8.0 — compiles your snippets (~30MB).\n" +
                "  • Harmony 2.3.3 — powers Runtime Method Patch (~200KB).\n\n" +
                "You can install both now in one click. Requires PowerShell + internet access. " +
                "If you skip, the REPL window won't compile until Tools / Roslyn REPL / Install Roslyn DLLs is run.",
                "Install Now",       // 0
                "Don't Show Again",  // 1
                "Later");            // 2

            switch (choice)
            {
                case 0:
                    InstallRoslynMenu.Install();
                    break;
                case 1:
                    EditorPrefs.SetBool(SkipPrefKey, true);
                    break;
                case 2:
                    // No-op — prompt re-fires next Editor launch.
                    break;
            }
        }

        private static bool DependenciesInstalled()
        {
            // Roslyn is the harder requirement — Harmony is optional
            // (only Runtime Method Patch needs it). If Roslyn loaded,
            // the user has gone through the install flow at least once
            // and any future Harmony top-up can happen through the
            // existing menu without us pestering on every Editor
            // launch.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = asm.GetName().Name; }
                catch { continue; }
                if (string.Equals(name, "Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool UserDismissedPermanently()
        {
            return EditorPrefs.GetBool(SkipPrefKey, false);
        }

        // Project-scoped so a "don't show again" decision in repo A
        // doesn't suppress the prompt in repo B — two REPL users on
        // the same machine might have very different setup states.
        private static string SkipPrefKey => RoslynRepl.Editor.Core.ProjectScopedPrefs.BuildKey(SkipPrefBaseName);
    }
}
