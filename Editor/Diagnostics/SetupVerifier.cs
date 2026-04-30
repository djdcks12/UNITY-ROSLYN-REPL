using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace RoslynRepl.Editor.Diagnostics
{
    public static class SetupVerifier
    {
        private const string PackageRoot = "Packages/com.roslyn-repl";

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
            if (s.Contains(PackageRoot + "/Editor/Plugins/Roslyn")) return AssemblyOrigin.BundledByUs;
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
            return sb.ToString();
        }
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
