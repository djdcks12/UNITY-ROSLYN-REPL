using System;
using System.IO;
using System.Reflection;
using UnityEditor.PackageManager;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Path resolver for the package's own files (bundled DLLs, UXML,
    /// USS, default snippet JSON).
    ///
    /// The package id <c>com.youngchan.roslyn-repl</c> is the single
    /// source of truth in <c>package.json</c>, but the *folder name*
    /// the package lives under is Unity's choice — for an embedded
    /// package Unity reads <c>package.json</c>'s <c>name</c> field
    /// and that becomes the canonical id regardless of the
    /// containing folder. So this resolver does the right thing:
    ///
    /// <list type="bullet">
    /// <item>Path lookups go through
    /// <see cref="PackageInfo.FindForAssembly"/> — whatever folder
    /// Unity resolved the package at (including a legacy
    /// <c>Packages/com.roslyn-repl/</c> checkout left over from
    /// pre-0.7.1 development) is the right place to read UXML / USS /
    /// bundled DLLs from. Forcing the folder name to match the
    /// published id would break the entirely-legitimate embedded-dev
    /// case where multiple variants / forks live under different
    /// folder names while sharing the same id in <c>package.json</c>.</item>
    /// <item>The "is this DLL one of ours?" check
    /// (<see cref="IsBundledRoslynAssemblyPath"/>) derives its
    /// prefix from the resolved <see cref="PackageRoot"/> rather
    /// than the literal <see cref="PackageName"/> — so a Roslyn
    /// DLL inside the same folder Unity resolved the package at
    /// always counts as BundledByUs in setup diagnostics, even when
    /// the folder name doesn't match the published id.</item>
    /// <item>A one-time <see cref="UnityEngine.Debug.LogWarning"/>
    /// fires when the resolved folder name doesn't match
    /// <see cref="PackageName"/>, so a developer running on a
    /// legacy <c>com.roslyn-repl/</c> checkout gets a nudge to
    /// rename before publishing without their environment breaking
    /// in the meantime.</item>
    /// </list>
    /// </summary>
    internal static class ReplPackagePaths
    {
        public const string PackageName = "com.youngchan.roslyn-repl";

        // Suppress repeat warnings — ResolvePackageRoot runs on every
        // AssetPath / AbsolutePath / IsBundledRoslynAssemblyPath call,
        // so an embedded-dev install with a legacy folder name would
        // otherwise spam the console with the same nudge per Run.
        private static bool _warnedAboutFolderMismatch;

        public static string PackageRoot => ResolvePaths().AssetPath;

        public static string AssetPath(string relativePath)
            => NormalizePath($"{PackageRoot}/{TrimLeadingSlashes(relativePath)}");

        public static string AbsolutePath(string relativePath)
            => Path.GetFullPath(AssetPath(relativePath));

        public static bool IsBundledRoslynAssemblyPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("<", StringComparison.Ordinal))
                return false;

            // PR-review followup: the prior shape used independent
            // Contains("/Editor/Plugins/Roslyn/") + Contains("/" + root)
            // which loosened the match in two ways —
            //   (1) the two markers could land in unrelated parts of
            //       the path, so a Roslyn DLL inside a *different*
            //       package whose folder happened to start with a
            //       similar prefix (`com.roslyn-repl-old`) would
            //       still match a root of `com.roslyn-repl`, and
            //   (2) only the AssetDatabase form (`assetPath`) was
            //       considered, so OpenUPM installs whose
            //       Assembly.Location reports
            //       `Library/PackageCache/<id>@<ver>/Editor/...`
            //       (never routed through assetPath) silently fell
            //       out of the BundledByUs classification.
            //
            // The match now runs against a *combined* segment,
            // `<root>/Editor/Plugins/Roslyn/`, anchored either with
            // a leading `/` (for the asset-style root inside an
            // absolute Assembly.Location) or as the start of the
            // path (for the absolute filesystem root that
            // PackageInfo.resolvedPath reports). Both root forms
            // are tried so the embedded-dev case, the
            // Library/PackageCache OpenUPM case, and any folder
            // Unity resolves the package to via package.json all
            // light up consistently.
            var loc = ResolvePaths();
            var normalized = NormalizePath(path);
            const string segment = "/Editor/Plugins/Roslyn/";

            if (!string.IsNullOrEmpty(loc.AssetPath))
            {
                var marker = "/" + loc.AssetPath + segment;
                if (ContainsOrdinalIgnoreCase(normalized, marker))
                    return true;
            }

            if (!string.IsNullOrEmpty(loc.ResolvedPath))
            {
                var marker = loc.ResolvedPath + segment;
                if (normalized.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
                // Defensive: an Assembly.Location string that came
                // through a different OS canonicaliser than
                // PackageInfo.resolvedPath might land at a different
                // drive-letter or symlink-resolved prefix. The
                // anchored "/" + marker form catches the case where
                // the input path embeds the resolved-path tail
                // somewhere past the start.
                var anchored = "/" + marker;
                if (ContainsOrdinalIgnoreCase(normalized, anchored))
                    return true;
            }

            return false;
        }

        private struct PackageLocation
        {
            public string AssetPath;     // Packages/com.youngchan.roslyn-repl (AssetDatabase form)
            public string ResolvedPath;  // /abs/proj/Packages/...   or  /abs/proj/Library/PackageCache/...@ver
        }

        // Single PackageInfo lookup that hands back both the
        // AssetDatabase-form path (used by AssetPath / AssetDatabase
        // APIs) and the absolute filesystem path Unity resolved the
        // package to (used by IsBundledRoslynAssemblyPath to
        // recognise the Library/PackageCache layout that OpenUPM
        // installs land in). Both are needed because
        // Assembly.Location is filesystem-only — it never reports
        // the virtual `Packages/<id>/` mount point — while
        // AssetDatabase.LoadAssetAtPath only accepts the
        // AssetDatabase form.
        private static PackageLocation ResolvePaths()
        {
            try
            {
                var info = PackageInfo.FindForAssembly(Assembly.GetExecutingAssembly());
                if (info != null)
                {
                    string asset = !string.IsNullOrEmpty(info.assetPath)
                        ? NormalizePath(info.assetPath) : null;
                    string resolved = !string.IsNullOrEmpty(info.resolvedPath)
                        ? NormalizePath(info.resolvedPath) : null;
                    if (asset != null) WarnIfFolderMismatch(asset);
                    if (asset != null || resolved != null)
                    {
                        return new PackageLocation
                        {
                            AssetPath    = asset    ?? $"Packages/{PackageName}",
                            ResolvedPath = resolved,
                        };
                    }
                }
            }
            catch
            {
                // Fall through to filesystem probe. PackageInfo can be
                // unavailable while Unity is recovering from a domain
                // reload.
            }

            // Filesystem probe under the canonical folder name only.
            // Legacy `com.roslyn-repl/` checkouts go through the
            // PackageInfo branch above (Unity resolves them by
            // package.json's name field, not folder name).
            string fallback = $"Packages/{PackageName}";
            if (File.Exists($"{fallback}/package.json"))
            {
                string fsAbs;
                try { fsAbs = NormalizePath(Path.GetFullPath(fallback)); }
                catch { fsAbs = null; }
                return new PackageLocation { AssetPath = fallback, ResolvedPath = fsAbs };
            }

            // Last-resort default. If the folder really isn't at
            // Packages/com.youngchan.roslyn-repl every AssetPath
            // call further up the stack will fail to find UXML / USS
            // / DLLs and the user will get a clear "asset not found"
            // path in the Verify Setup dialog — better than silently
            // resolving to a stale legacy folder.
            return new PackageLocation { AssetPath = fallback, ResolvedPath = null };
        }

        // Soft nudge for embedded checkouts whose folder name doesn't
        // match the published id. The package still works fine —
        // Unity resolves it by package.json, not folder — but the
        // mismatch will eventually become a publish-time foot-gun
        // (OpenUPM consumers always get the folder named after the
        // id, so a local checkout under a different name can mask
        // path-related bugs). One warning per editor session.
        private static void WarnIfFolderMismatch(string assetPath)
        {
            if (_warnedAboutFolderMismatch) return;
            int slash = assetPath.LastIndexOf('/');
            if (slash < 0 || slash + 1 >= assetPath.Length) return;
            var folderId = assetPath.Substring(slash + 1);
            if (string.Equals(folderId, PackageName, StringComparison.OrdinalIgnoreCase)) return;

            _warnedAboutFolderMismatch = true;
            UnityEngine.Debug.LogWarning(
                $"[Roslyn REPL] Embedded package folder name '{folderId}' doesn't match the package id '{PackageName}'. " +
                "Local development keeps working through PackageInfo, but rename the folder to " +
                $"'Packages/{PackageName}/' before consuming via OpenUPM or shipping the project.");
        }

        private static string TrimLeadingSlashes(string path)
            => (path ?? string.Empty).TrimStart('/', '\\');

        private static string NormalizePath(string path)
            => (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');

        private static bool ContainsOrdinalIgnoreCase(string source, string value)
            => source?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
