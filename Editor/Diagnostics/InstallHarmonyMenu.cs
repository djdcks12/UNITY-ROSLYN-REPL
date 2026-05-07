using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace RoslynRepl.Editor.Diagnostics
{
    /// <summary>
    /// Tools / Roslyn REPL / Install Harmony — fetches Lib.Harmony from
    /// NuGet via the bundled Tools~/install-harmony.ps1 script and lays
    /// the DLL down at Editor/Plugins/Harmony/. Mirrors the existing
    /// Install Roslyn DLLs flow so users only need one mental model
    /// for "external runtime dependency the package needs you to
    /// install once".
    /// </summary>
    public static class InstallHarmonyMenu
    {
        private const string PackageRoot = "Packages/com.roslyn-repl";
        private const string ScriptRel = "Tools~/install-harmony.ps1";

        [MenuItem("Tools/Roslyn REPL/Install Harmony", priority = 210)]
        public static void Install()
        {
            var packageAbs = Path.GetFullPath(PackageRoot);
            var scriptPath = Path.Combine(packageAbs, ScriptRel);
            if (!File.Exists(scriptPath))
            {
                EditorUtility.DisplayDialog("Roslyn REPL",
                    $"Install script not found:\n{scriptPath}",
                    "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("Roslyn REPL — Install Harmony",
                "This will download Lib.Harmony 2.3.3 from NuGet and install the binary to:\n" +
                $"  {Path.Combine(packageAbs, "Editor/Plugins/Harmony")}\n\n" +
                "Harmony powers the Runtime Method Patch feature — without this DLL Patch / Revert can't redirect a method's calls.\n\n" +
                "Requires PowerShell + internet access. Proceed?",
                "Install", "Cancel"))
            {
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) throw new Exception("Failed to launch powershell.exe");

                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();

                if (!string.IsNullOrEmpty(stdout)) Debug.Log("[Roslyn REPL Harmony install]\n" + stdout);
                if (!string.IsNullOrEmpty(stderr)) Debug.LogWarning("[Roslyn REPL Harmony install — stderr]\n" + stderr);

                if (p.ExitCode != 0)
                {
                    EditorUtility.DisplayDialog("Roslyn REPL — Install Failed",
                        $"PowerShell exit code {p.ExitCode}. See Console for details.",
                        "OK");
                    return;
                }

                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Roslyn REPL — Installed",
                    "Harmony installed. Run Verify Setup to confirm the DLL is loaded.",
                    "Verify");
                SetupVerifier.Verify();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Roslyn REPL — Install Failed",
                    $"Exception: {ex.Message}\nSee Console for details.",
                    "OK");
            }
        }
    }
}
