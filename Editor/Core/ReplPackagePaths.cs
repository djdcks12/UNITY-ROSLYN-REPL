using System;
using System.IO;
using System.Reflection;
using UnityEditor.PackageManager;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Path resolver for the package's own files (bundled DLLs, UXML,
    /// USS, default snippet JSON). The OpenUPM package id is the
    /// single source of truth — the historical <c>com.roslyn-repl</c>
    /// fallback was removed in 0.7.2 (issue #45) along with the
    /// EditorPrefs migration, on the same "no consumer install yet"
    /// reasoning. A project that still keeps the package folder at
    /// <c>Packages/com.roslyn-repl</c> should rename it to
    /// <c>Packages/com.youngchan.roslyn-repl</c> — the resolver no
    /// longer probes the old name.
    /// </summary>
    internal static class ReplPackagePaths
    {
        public const string PackageName = "com.youngchan.roslyn-repl";

        public static string PackageRoot => ResolvePackageRoot();

        public static string AssetPath(string relativePath)
            => NormalizePath($"{PackageRoot}/{TrimLeadingSlashes(relativePath)}");

        public static string AbsolutePath(string relativePath)
            => Path.GetFullPath(AssetPath(relativePath));

        public static bool IsBundledRoslynAssemblyPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("<", StringComparison.Ordinal))
                return false;

            var normalized = NormalizePath(path);
            return ContainsOrdinalIgnoreCase(normalized, "/Editor/Plugins/Roslyn/")
                   && ContainsOrdinalIgnoreCase(normalized, $"/{PackageName}");
        }

        private static string ResolvePackageRoot()
        {
            try
            {
                var info = PackageInfo.FindForAssembly(Assembly.GetExecutingAssembly());
                if (!string.IsNullOrEmpty(info?.assetPath))
                    return NormalizePath(info.assetPath);
            }
            catch
            {
                // Fall through to filesystem probe. PackageInfo can be
                // unavailable while Unity is recovering from a domain
                // reload — the fallback below just looks for the
                // package.json directly.
            }

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

        private static string TrimLeadingSlashes(string path)
            => (path ?? string.Empty).TrimStart('/', '\\');

        private static string NormalizePath(string path)
            => (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');

        private static bool ContainsOrdinalIgnoreCase(string source, string value)
            => source?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
