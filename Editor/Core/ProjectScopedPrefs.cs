using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Helpers for building <see cref="UnityEditor.EditorPrefs"/> keys that
    /// don't collide between Unity projects on the same user account.
    ///
    /// EditorPrefs is per-user/per-machine but project-agnostic, so any key
    /// we use must carry a project discriminator — otherwise a value saved
    /// in repo A leaks into repo B the next time it opens this package.
    /// </summary>
    public static class ProjectScopedPrefs
    {
        /// <summary>
        /// Build a project-scoped key by appending a stable 8-hex-digit
        /// FNV-1a hash of the current project's <see cref="Application.dataPath"/>.
        /// </summary>
        public static string BuildKey(string baseName)
        {
            return baseName + "." + ComputeProjectHash().ToString("x8");
        }

        // FNV-1a 32-bit. Deliberately not string.GetHashCode — modern .NET
        // randomizes string hashes per process, so the storage location
        // would change every editor restart and silently lose the user's
        // saved values.
        private static int ComputeProjectHash()
        {
            var path = Application.dataPath ?? string.Empty;
            unchecked
            {
                const int prime = 16777619;
                int hash = unchecked((int)2166136261u);
                for (int i = 0; i < path.Length; i++)
                {
                    hash ^= path[i];
                    hash *= prime;
                }
                return hash;
            }
        }
    }
}
