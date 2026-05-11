using System;
using System.IO;
using System.Reflection;
using UnityEditor.PackageManager;

namespace RoslynRepl.Editor.Core
{
    internal static class ReplPackagePaths
    {
        public const string PackageName = "com.youngchan.roslyn-repl";

        private const string LegacyPackageName = "com.roslyn-repl";

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
                   && (ContainsOrdinalIgnoreCase(normalized, $"/{PackageName}")
                       || ContainsOrdinalIgnoreCase(normalized, $"/{LegacyPackageName}"));
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
                // Fall through to filesystem probes. PackageInfo can be
                // unavailable while Unity is recovering from a domain reload.
            }

            if (File.Exists($"Packages/{PackageName}/package.json"))
                return $"Packages/{PackageName}";

            if (File.Exists($"Packages/{LegacyPackageName}/package.json"))
                return $"Packages/{LegacyPackageName}";

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
