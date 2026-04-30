using System;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace RoslynRepl.Editor.Diagnostics
{
    [InitializeOnLoad]
    internal static class AssemblyResolutionGuard
    {
        private const string SessionKey = "RoslynRepl.GuardLastWarningHash";
        private static readonly string[] WatchedAssemblies =
        {
            "Microsoft.CodeAnalysis",
            "Microsoft.CodeAnalysis.CSharp"
        };

        static AssemblyResolutionGuard()
        {
            EditorApplication.delayCall += Run;
        }

        private static void Run()
        {
            try
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies();
                var sb = new StringBuilder();
                bool hasConflict = false;

                foreach (var name in WatchedAssemblies)
                {
                    var matches = loaded
                        .Where(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    if (matches.Length <= 1) continue;

                    hasConflict = true;
                    sb.AppendLine($"[Roslyn REPL] Multiple copies of '{name}' detected ({matches.Length}):");
                    foreach (var a in matches)
                    {
                        sb.AppendLine($"  - v{a.GetName().Version}  {SafeLocation(a)}");
                    }
                }

                if (!hasConflict) return;

                sb.AppendLine("Run 'Tools / Roslyn REPL / Verify Setup' for details.");
                sb.AppendLine("Resolve by disabling one Plugin Importer (Inspector → uncheck Editor).");

                var msg = sb.ToString();
                var hash = msg.GetHashCode().ToString();
                if (SessionState.GetString(SessionKey, "") == hash) return;
                SessionState.SetString(SessionKey, hash);

                Debug.LogWarning(msg);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Roslyn REPL] AssemblyResolutionGuard failed: {ex.Message}");
            }
        }

        private static string SafeLocation(Assembly a)
        {
            try { return string.IsNullOrEmpty(a.Location) ? "<dynamic>" : a.Location; }
            catch { return "<inaccessible>"; }
        }
    }
}
