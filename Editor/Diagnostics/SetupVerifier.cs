using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.Diagnostics
{
    public static class SetupVerifier
    {
        private static readonly string[] RequiredAssemblies =
        {
            "Microsoft.CodeAnalysis",
            "Microsoft.CodeAnalysis.CSharp",
            "System.Collections.Immutable",
            "System.Reflection.Metadata"
        };

        [MenuItem("Tools/Roslyn REPL/Verify Setup", priority = 100)]
        public static void Verify()
        {
            var report = BuildReport();
            Debug.Log(report.ToConsoleString());

            if (report.HasMissing)
            {
                if (EditorUtility.DisplayDialog(
                    "Roslyn REPL — Missing DLLs",
                    "Required Roslyn assemblies are not loaded.\n\n" +
                    "Install them by running:\n" +
                    "  Tools / Roslyn REPL / Install Roslyn DLLs\n\n" +
                    "or manually run Tools~/install-roslyn.ps1 inside the package.",
                    "Open Install Menu", "Close"))
                {
                    EditorApplication.ExecuteMenuItem("Tools/Roslyn REPL/Install Roslyn DLLs");
                }
            }
            else if (report.HasConflict)
            {
                EditorUtility.DisplayDialog(
                    "Roslyn REPL — Conflict Detected",
                    "Multiple copies of one or more Roslyn assemblies are loaded.\n\n" +
                    "See the Console for the full list.\n" +
                    "Resolve by disabling one Plugin Importer (Inspector → uncheck Editor).",
                    "OK");
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "Roslyn REPL — OK",
                    "All required Roslyn assemblies are loaded and unique.\nSee Console for resolution details.",
                    "OK");
            }
        }

        public static SetupReport BuildReport()
        {
            var report = new SetupReport();
            var loaded = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var name in RequiredAssemblies)
            {
                var matches = loaded
                    .Where(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                    .Select(ToResolved)
                    .ToList();

                report.Entries.Add(new AssemblyEntry { Name = name, Matches = matches });
            }
            return report;
        }

        private static ResolvedAssembly ToResolved(Assembly a)
        {
            var location = SafeLocation(a);
            return new ResolvedAssembly
            {
                FullName = a.FullName,
                Location = location,
                Version = a.GetName().Version?.ToString() ?? "unknown",
                Origin = ClassifyOrigin(location)
            };
        }

        private static string SafeLocation(Assembly a)
        {
            try { return string.IsNullOrEmpty(a.Location) ? "<dynamic>" : a.Location; }
            catch { return "<inaccessible>"; }
        }

        private static AssemblyOrigin ClassifyOrigin(string location)
        {
            if (string.IsNullOrEmpty(location) || location.StartsWith("<")) return AssemblyOrigin.Unknown;
            var s = location.Replace('\\', '/');
            if (ReplPackagePaths.IsBundledRoslynAssemblyPath(s)) return AssemblyOrigin.BundledByUs;
            if (s.Contains("/Unity/Hub/Editor/") || s.Contains("/Editor/Data/Managed/") || s.Contains("/Editor/Data/MonoBleedingEdge/")) return AssemblyOrigin.UnityShipped;
            if (s.Contains("/Assets/Plugins/NuGet/")) return AssemblyOrigin.NuGetForUnity;
            if (s.Contains("/Library/PackageCache/") || s.Contains("/Packages/")) return AssemblyOrigin.OtherPackage;
            return AssemblyOrigin.External;
        }
    }

    public enum AssemblyOrigin
    {
        Unknown,
        BundledByUs,
        UnityShipped,
        NuGetForUnity,
        OtherPackage,
        External
    }

    public class SetupReport
    {
        public List<AssemblyEntry> Entries = new();

        public bool HasMissing  => Entries.Any(e => e.Matches.Count == 0);
        public bool HasConflict => Entries.Any(e => e.Matches.Count > 1);
        public bool HasIssues   => HasMissing || HasConflict;

        public string ToConsoleString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Roslyn REPL] Setup verification");
            foreach (var e in Entries)
            {
                if (e.Matches.Count == 0)
                {
                    sb.AppendLine($"  [missing] {e.Name}");
                }
                else if (e.Matches.Count == 1)
                {
                    var m = e.Matches[0];
                    sb.AppendLine($"  [ok]      {e.Name}  v{m.Version}  ({m.Origin})");
                    sb.AppendLine($"            {m.Location}");
                }
                else
                {
                    sb.AppendLine($"  [conflict] {e.Name}  ({e.Matches.Count} copies)");
                    foreach (var m in e.Matches)
                    {
                        sb.AppendLine($"             v{m.Version}  ({m.Origin})  {m.Location}");
                    }
                    sb.AppendLine($"             → Disable one Plugin Importer (Inspector → uncheck Editor) to resolve.");
                }
            }

            // Phase 11f: runtime diagnostics. Every Run / Watch refresh
            // emits a `ReplDynamic_<guid>` assembly that can't be
            // unloaded until the Editor reloads its AppDomain (Mono
            // doesn't ship CollectibleAssemblyLoadContext). Surfacing
            // the count makes the slow drift visible — handy for users
            // tuning Watch panels with N expressions × M Runs and
            // wondering why memory crept up.
            sb.AppendLine();
            int dynCount = CountDynamicReplAssemblies();
            var severity = ReplDiagnostics.SeverityOf(dynCount);
            string severityTag = severity switch
            {
                AssemblyLoadSeverity.High => "  [warn]    ",
                AssemblyLoadSeverity.Warn => "  [hint]    ",
                _                         => "  [diag]    ",
            };
            sb.AppendLine($"{severityTag}Loaded REPL dynamic assemblies: {dynCount}");
            sb.AppendLine($"             (cleared by domain reload — recompile any script or re-enter Play Mode)");
            if (severity != AssemblyLoadSeverity.Normal)
            {
                int threshold = severity == AssemblyLoadSeverity.High
                    ? ReplDiagnostics.HighThreshold
                    : ReplDiagnostics.WarnThreshold;
                sb.AppendLine($"             at or above {threshold} — consider 'Tools / Roslyn REPL / Force Domain Reload' to free memory.");
            }
            sb.AppendLine($"  [diag]     Cached MetadataReferences: {AssemblyReferenceCache.CountOrZero}");
            // Note: Harmony is optional — only the Runtime Method
            // Patch feature needs it. Don't list it as Required (so
            // users running just the REPL don't see a "missing DLL"
            // warning), but do report its presence so the Patch UI
            // can ask "did you install Harmony yet?" with one glance.
            sb.AppendLine($"  [optional] Harmony (Runtime Method Patch): " +
                          (IsHarmonyLoaded()
                              ? "present"
                              : "absent — Tools / Roslyn REPL / Install Roslyn DLLs installs Harmony alongside Roslyn"));
            return sb.ToString();
        }

        private static bool IsHarmonyLoaded()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = a.GetName().Name; }
                catch { continue; }
                if (string.Equals(name, "0Harmony", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Single source of truth lives in ReplDiagnostics — both the
        // REPL snippet path and the runtime patch path emit
        // assemblies that need to count, and a local copy of the
        // prefix list is exactly what the PR review caught letting
        // ReplPatch_* drift out of sight.
        private static int CountDynamicReplAssemblies()
            => ReplDiagnostics.DynamicAssemblyCount;
    }

    public class AssemblyEntry
    {
        public string Name;
        public List<ResolvedAssembly> Matches = new();
    }

    public class ResolvedAssembly
    {
        public string FullName;
        public string Location;
        public string Version;
        public AssemblyOrigin Origin;
    }
}
