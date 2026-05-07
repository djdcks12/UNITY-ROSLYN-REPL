using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace RoslynRepl.Editor.Diagnostics
{
    public static class InstallRoslynMenu
    {
        private const string PackageRoot = "Packages/com.roslyn-repl";
        private const string RoslynScriptRel = "Tools~/install-roslyn.ps1";
        private const string HarmonyScriptRel = "Tools~/install-harmony.ps1";

        [MenuItem("Tools/Roslyn REPL/Install Roslyn DLLs", priority = 200)]
        public static void Install()
        {
            var packageAbs = Path.GetFullPath(PackageRoot);
            var roslynScript = Path.Combine(packageAbs, RoslynScriptRel);
            var harmonyScript = Path.Combine(packageAbs, HarmonyScriptRel);

            if (!File.Exists(roslynScript))
            {
                EditorUtility.DisplayDialog("Roslyn REPL",
                    $"Install script not found:\n{roslynScript}",
                    "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("Roslyn REPL — Install Required DLLs",
                "This will download both required dependencies from NuGet:\n\n" +
                $"  • Roslyn 4.8.0  →  {Path.Combine(packageAbs, "Editor/Plugins/Roslyn")}\n" +
                $"  • Harmony 2.3.3 →  {Path.Combine(packageAbs, "Editor/Plugins/Harmony")}\n\n" +
                "Roslyn powers snippet execution; Harmony powers the Runtime Method Patch feature. Both are MIT-licensed.\n\n" +
                "Requires PowerShell + internet access. Proceed?",
                "Install", "Cancel"))
            {
                return;
            }

            try
            {
                // Run both scripts back-to-back. If Roslyn fails, abort
                // before Harmony — the package is unusable without Roslyn
                // either way, and a partial install confuses Verify Setup.
                if (!RunScript(roslynScript, "Roslyn DLLs")) return;

                if (File.Exists(harmonyScript))
                {
                    // Harmony failure isn't fatal — the core REPL still
                    // works without it. Surface a warning but don't block
                    // the success dialog.
                    if (!RunScript(harmonyScript, "Harmony"))
                    {
                        EditorUtility.DisplayDialog("Roslyn REPL — Partial Install",
                            "Roslyn installed successfully, but Harmony failed.\n\n" +
                            "The core REPL still works. Runtime Method Patch will be unavailable until Harmony is installed — re-run this menu or Tools~/install-harmony.ps1 manually.",
                            "OK");
                        AssetDatabase.Refresh();
                        SetupVerifier.Verify();
                        return;
                    }
                }
                else
                {
                    Debug.LogWarning($"[Roslyn REPL] Harmony install script not found at {harmonyScript}; skipping. Runtime Method Patch will be unavailable.");
                }

                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Roslyn REPL — Installed",
                    "Roslyn and Harmony DLLs installed. Run Verify Setup to confirm.",
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

        // Returns true on success. False on PowerShell exit code != 0
        // (the caller already showed a generic failure dialog before
        // this returned). Throws on launch failure.
        private static bool RunScript(string scriptPath, string label)
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

            if (!string.IsNullOrEmpty(stdout)) Debug.Log($"[Roslyn REPL — {label} install]\n{stdout}");
            if (!string.IsNullOrEmpty(stderr)) Debug.LogWarning($"[Roslyn REPL — {label} install — stderr]\n{stderr}");

            if (p.ExitCode != 0)
            {
                EditorUtility.DisplayDialog($"Roslyn REPL — {label} Install Failed",
                    $"PowerShell exit code {p.ExitCode}. See Console for details.",
                    "OK");
                return false;
            }
            return true;
        }
    }
}
