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

        public static string PackageRoot => ResolvePackageRoot();

        public static string AssetPath(string relativePath)
            => NormalizePath($"{PackageRoot}/{TrimLeadingSlashes(relativePath)}");

        public static string AbsolutePath(string relativePath)
            => Path.GetFullPath(AssetPath(relativePath));

        public static bool IsBundledRoslynAssemblyPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("<", StringComparison.Ordinal))
                return false;

            // Match against the *resolved* PackageRoot (which carries
            // Unity's actual folder name) rather than the literal
            // PackageName. Without this, a legacy embedded checkout
            // would resolve assets from the legacy folder via
            // ResolvePackageRoot but fail to classify its bundled
            // Roslyn DLLs as BundledByUs — setup diagnostics would
            // surface them as OtherPackage and the verifier's
            // "duplicate copy?" branch would lie about what's loaded.
            var root = PackageRoot;
            if (string.IsNullOrEmpty(root)) return false;

            var normalized = NormalizePath(path);
            return ContainsOrdinalIgnoreCase(normalized, "/Editor/Plugins/Roslyn/")
                   && ContainsOrdinalIgnoreCase(normalized, "/" + root);
        }

        private static string ResolvePackageRoot()
        {
            try
            {
                var info = PackageInfo.FindForAssembly(Assembly.GetExecutingAssembly());
                if (!string.IsNullOrEmpty(info?.assetPath))
                {
                    var resolved = NormalizePath(info.assetPath);
                    WarnIfFolderMismatch(resolved);
                    return resolved;
                }
            }
            catch
            {
                // Fall through to filesystem probe. PackageInfo can be
                // unavailable while Unity is recovering from a domain
                // reload — the fallback below just looks for the
                // package.json directly.
            }

            // Filesystem probe under the canonical folder name only.
            // Legacy `com.roslyn-repl/` checkouts go through the
            // PackageInfo branch above (Unity resolves them by
            // package.json's name field, not folder name); the
            // probe here is just for the rare case Unity's package
            // manager hasn't initialised yet.
            if (File.Exists($"Packages/{PackageName}/package.json"))
                return $"Packages/{PackageName}";

            // Last-resort default. If the folder really isn't at
            // Packages/com.youngchan.roslyn-repl every AssetPath
            // call further up the stack will fail to find UXML / USS
            // / DLLs and the user will get a clear "asset not found"
            // path in the Verify Setup dialog — better than silently
            // resolving to a stale legacy folder.
            return $"Packages/{PackageName}";
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
