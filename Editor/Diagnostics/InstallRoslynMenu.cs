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
        private const string ScriptRel = "Tools~/install-roslyn.ps1";

        [MenuItem("Tools/Roslyn REPL/Install Roslyn DLLs", priority = 200)]
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

            if (!EditorUtility.DisplayDialog("Roslyn REPL — Install Roslyn DLLs",
                "This will download Roslyn 4.8.0 from NuGet and install the binaries to:\n" +
                $"  {Path.Combine(packageAbs, "Editor/Plugins/Roslyn")}\n\n" +
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

                if (!string.IsNullOrEmpty(stdout)) Debug.Log("[Roslyn REPL install]\n" + stdout);
                if (!string.IsNullOrEmpty(stderr)) Debug.LogWarning("[Roslyn REPL install — stderr]\n" + stderr);

                if (p.ExitCode != 0)
                {
                    EditorUtility.DisplayDialog("Roslyn REPL — Install Failed",
                        $"PowerShell exit code {p.ExitCode}. See Console for details.",
                        "OK");
                    return;
                }

                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Roslyn REPL — Installed",
                    "Roslyn DLLs installed. Run Verify Setup to confirm.",
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
